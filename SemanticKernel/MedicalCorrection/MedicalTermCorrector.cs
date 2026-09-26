using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using LLamaSharp.SemanticKernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel.ChatCompletion;

namespace SemanticKernel.MedicalCorrection
{
    /// <summary>
    /// Splits segment texts into sentence pieces, sends them in batches to the local LLM and keeps only the
    /// corrections that pass <see cref="CorrectionValidator"/>. Only piece ids and texts are sent, never speakers
    /// or timestamps.
    /// </summary>
    public partial class MedicalTermCorrector : IMedicalTermCorrector
    {
        // Context pieces only help the model understand the topic; long ones are cut to their end.
        private const int MaxContextPieceLength = 400;

        // Cyrillic as-is instead of \uXXXX escapes: the model has to read and echo the text.
        private static readonly JsonSerializerOptions RequestJsonOptions = new(JsonSerializerDefaults.Web)
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        private readonly IChatCompletionProvider _chatProvider;
        private readonly LlmOptions _options;
        private readonly ILogger<MedicalTermCorrector> _logger;
        private readonly SemaphoreSlim _runLock = new(1, 1);

        public MedicalTermCorrector(
            [FromKeyedServices(LlmModelRole.Correction)] IChatCompletionProvider chatProvider,
            IOptions<LlmOptions> options, ILogger<MedicalTermCorrector> logger)
        {
            _chatProvider = chatProvider;
            _options = options.Value;
            _logger = logger;
        }

        /// <summary>A sentence piece of a segment's text. <see cref="Text"/> is trimmed; the whitespace is kept aside.</summary>
        private record Piece(int Id, int SegmentIndex, Segment Segment, string Leading, string Text, string Trailing)
        {
            public IReadOnlyList<LowConfidenceWord> LowConfidence { get; init; } = [];
        }

        public async Task<IReadOnlyList<Segment>> CorrectAsync(
            IReadOnlyList<Segment> segments, RecordProfileContent profile, CorrectionLog log,
            IProgress<BatchProgress>? progress = null, CancellationToken ct = default)
        {
            var pieces = SplitIntoPieces(segments);
            if (pieces.Count == 0)
            {
                return segments;
            }

            var chat = await _chatProvider.GetChatCompletionAsync(ct);

            // One run at a time: the model is CPU/GPU-bound and shared by all requests.
            await _runLock.WaitAsync(ct);
            try
            {
                var finalTexts = new Dictionary<int, string>();
                var done = new List<Piece>();
                var batches = SplitIntoBatches(pieces);
                for (var i = 0; i < batches.Count; i++)
                {
                    var batch = batches[i];
                    var context = done.TakeLast(_options.ContextSegments).Select(p => p with { Text = finalTexts[p.Id] }).ToList();

                    var stopwatch = Stopwatch.StartNew();
                    var corrections = await RequestCorrectionsAsync(chat, profile, context, batch, i + 1, ct);
                    foreach (var piece in batch)
                    {
                        finalTexts[piece.Id] = Apply(piece, corrections, log);
                    }
                    done.AddRange(batch);

                    _logger.LogInformation("Medical correction batch {Batch}/{BatchCount} ({PieceCount} piece(s)) took {Seconds:F1} s",
                        i + 1, batches.Count, batch.Count, stopwatch.Elapsed.TotalSeconds);
                    progress?.Report(new BatchProgress(i + 1, batches.Count));
                }

                _logger.LogInformation("Medical correction: {Accepted} change(s) accepted, {Rejected} rejected in {SegmentCount} segment(s) / {PieceCount} piece(s)",
                    log.Entries.Count(e => e.Accepted), log.Entries.Count(e => !e.Accepted), segments.Count, pieces.Count);
                return Reassemble(segments, pieces, finalTexts);
            }
            finally
            {
                _runLock.Release();
            }
        }

        // Pieces get ids 1..N over the whole run; whitespace-only pieces are not sent.
        private List<Piece> SplitIntoPieces(IReadOnlyList<Segment> segments)
        {
            var pieces = new List<Piece>();
            for (var index = 0; index < segments.Count; index++)
            {
                var segment = segments[index];

                // Куски покрывают текст ровно, поэтому абсолютное начало куска - это сумма
                // длин предыдущих. Слово переносится в тот кусок, внутрь которого попало целиком.
                var partStart = 0;
                foreach (var part in TextPieces.Split(segment.Text, Math.Max(1, _options.MaxPieceCharacters)))
                {
                    var text = part.Trim();
                    if (text.Length == 0)
                    {
                        partStart += part.Length;
                        continue;
                    }
                    var leading = part[..part.IndexOf(text, StringComparison.Ordinal)];
                    var textStart = partStart + leading.Length;

                    var words = segment.LowConfidence
                        .Where(w => w.At >= textStart && w.At + w.Word.Length <= textStart + text.Length)
                        .Select(w => w with { At = w.At - textStart })
                        .ToList();

                    pieces.Add(new Piece(pieces.Count + 1, index, segment, leading, text,
                        part[(leading.Length + text.Length)..]) { LowConfidence = words });
                    partStart += part.Length;
                }
            }
            return pieces;
        }

        // A segment whose pieces all stayed the same is returned as is (exact original text, whitespace included).
        private static List<Segment> Reassemble(IReadOnlyList<Segment> segments, List<Piece> pieces, Dictionary<int, string> finalTexts)
        {
            var bySegment = pieces.ToLookup(p => p.SegmentIndex);
            return segments.Select((segment, index) =>
            {
                var own = bySegment[index].ToList();
                if (own.All(p => finalTexts[p.Id] == p.Text))
                {
                    return segment;
                }

                // Неуверенные слова нетронутых кусков переезжают на новое начало куска: одна правка
                // в длинной реплике не должна стирать пометки всей реплики. У изменённого куска
                // смещения указывают в исчезнувший текст - его слова отбрасываются.
                var text = new StringBuilder();
                var words = new List<LowConfidenceWord>();
                foreach (var piece in own)
                {
                    text.Append(piece.Leading);
                    var final = finalTexts[piece.Id];
                    if (final == piece.Text)
                    {
                        var start = text.Length;
                        words.AddRange(piece.LowConfidence.Select(w => w with { At = w.At + start }));
                    }
                    text.Append(final).Append(piece.Trailing);
                }
                return segment with { Text = text.ToString(), LowConfidence = words };
            }).ToList();
        }

        // Up to BatchSize pieces and MaxBatchCharacters of text per batch (a longer single piece goes alone).
        private List<List<Piece>> SplitIntoBatches(List<Piece> pieces)
        {
            var batchSize = Math.Max(1, _options.BatchSize);
            var batches = new List<List<Piece>>();
            var current = new List<Piece>();
            var characters = 0;
            foreach (var piece in pieces)
            {
                if (current.Count > 0 && (current.Count >= batchSize || characters + piece.Text.Length > _options.MaxBatchCharacters))
                {
                    batches.Add(current);
                    current = [];
                    characters = 0;
                }
                current.Add(piece);
                characters += piece.Text.Length;
            }
            batches.Add(current);
            return batches;
        }

        // Returns id -> corrected text for the pieces the model changed (possibly none), or null when the
        // model never produced a usable answer for the batch. The model returns only changed pieces: echoing
        // every piece back costs ~1,000 output tokens per batch on the CPU, most of them for text that did
        // not change.
        private async Task<Dictionary<int, string>?> RequestCorrectionsAsync(
            IChatCompletionService chat, RecordProfileContent profile, IReadOnlyList<Piece> context,
            IReadOnlyList<Piece> batch, int batchNumber, CancellationToken ct)
        {
            var history = new ChatHistory(profile.SystemPrompt);
            history.AddUserMessage(BuildUserMessage(profile, context, batch));
            var settings = new LLamaSharpPromptExecutionSettings
            {
                Temperature = _options.Temperature,
                MaxTokens = _options.MaxTokens
            };
            var expectedIds = batch.Select(p => p.Id).ToHashSet();

            var attempts = 1 + Math.Max(0, _options.MaxRetries);
            for (var attempt = 1; attempt <= attempts; attempt++)
            {
                string? response;
                try
                {
                    var reply = await ChatCompletionRunner.CompleteAsync(chat, history, settings, ct);
                    response = reply.Content;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Medical correction batch {Batch}, attempt {Attempt}/{Attempts}: model call failed",
                        batchNumber, attempt, attempts);
                    continue;
                }

                if (!CorrectionResponseParser.TryParse(response, out var parsed))
                {
                    _logger.LogWarning("Medical correction batch {Batch}, attempt {Attempt}/{Attempts}: unparseable output: {Response}",
                        batchNumber, attempt, attempts, response);
                    continue;
                }

                // A subset of the batch ids, each at most once. An unknown or repeated id means the model
                // lost track of the input, and its texts cannot be trusted to belong where it says.
                var ids = parsed.Select(s => s.Id).ToList();
                if (ids.Count != ids.Distinct().Count() || !ids.All(expectedIds.Contains))
                {
                    _logger.LogWarning("Medical correction batch {Batch}, attempt {Attempt}/{Attempts}: ids [{Ids}] are not a subset of [{ExpectedIds}]",
                        batchNumber, attempt, attempts, string.Join(", ", ids), string.Join(", ", expectedIds));
                    continue;
                }

                return parsed.ToDictionary(s => s.Id, s => s.Text);
            }

            _logger.LogWarning("Medical correction batch {Batch}: no valid output after {Attempts} attempt(s), keeping the original text of {PieceCount} piece(s)",
                batchNumber, attempts, batch.Count);
            return null;
        }

        private string BuildUserMessage(RecordProfileContent profile, IReadOnlyList<Piece> context, IReadOnlyList<Piece> batch)
        {
            var request = new
            {
                context = context.Select(p => new { id = p.Id, text = Tail(p.Text, MaxContextPieceLength) }),
                // lowConfidence опускается у кусков без подозрительных слов: пустой массив в
                // каждом элементе - это лишние токены в каждом запросе и ничего больше.
                segments = batch.Select(p => p.LowConfidence.Count == 0
                    ? (object)new { id = p.Id, text = p.Text }
                    : new { id = p.Id, text = p.Text, lowConfidence = p.LowConfidence })
            };

            // "\n" rather than AppendLine: the prompt shouldn't depend on the OS line ending.
            var builder = new StringBuilder(JsonSerializer.Serialize(request, RequestJsonOptions));
            // Glossary lines are chosen by the low-confidence words only (with their neighbours, for split
            // terms), not by the whole batch text: matching every word pulled in up to 2,000 characters per
            // list - the ICD-10 one above all - for words the recognizer was sure about. A batch without
            // low-confidence words gets no reference lines at all.
            var texts = batch.SelectMany(LowConfidenceWindows).ToList();
            foreach (var glossary in profile.Glossaries)
            {
                var lines = glossary.Select(texts, _options.MaxGlossaryCharacters);
                if (lines.Count > 0)
                {
                    builder.Append("\n\n").Append(glossary.Heading).Append('\n').AppendJoin('\n', lines);
                }
            }
            return builder.ToString();
        }

        private static string Tail(string text, int maxLength) =>
            text.Length <= maxLength ? text : "…" + text[^maxLength..];

        // For each low-confidence word: the words of the piece it overlaps plus one word on each side,
        // so a term the recognizer split in two ("аторва статин") still shares a glued key with its line.
        private static IEnumerable<string> LowConfidenceWindows(Piece piece)
        {
            if (piece.LowConfidence.Count == 0)
            {
                yield break;
            }

            var words = Words().Matches(piece.Text);
            foreach (var word in piece.LowConfidence)
            {
                var start = word.At;
                var end = word.At + word.Word.Length;
                var first = -1;
                var last = -1;
                for (var i = 0; i < words.Count; i++)
                {
                    if (words[i].Index < end && words[i].Index + words[i].Length > start)
                    {
                        if (first < 0)
                        {
                            first = i;
                        }
                        last = i;
                    }
                }

                if (first < 0)
                {
                    // The offset points nowhere in this text (a stale flag): the word itself still counts.
                    yield return word.Word;
                    continue;
                }

                first = Math.Max(0, first - 1);
                last = Math.Min(words.Count - 1, last + 1);
                yield return string.Join(' ', Enumerable.Range(first, last - first + 1).Select(i => words[i].Value));
            }
        }

        [GeneratedRegex(@"\p{L}+")]
        private static partial Regex Words();

        private string Apply(Piece piece, Dictionary<int, string>? corrections, CorrectionLog log)
        {
            if (corrections == null || !corrections.TryGetValue(piece.Id, out var corrected))
            {
                return piece.Text;
            }

            // Whitespace-only differences are not a correction.
            corrected = corrected?.Trim();
            if (corrected == piece.Text)
            {
                return piece.Text;
            }

            var validation = CorrectionValidator.Validate(piece.Text, corrected);
            log.Add(new CorrectionLogEntry(piece.Segment.Id, piece.Segment.Speaker, piece.Text, corrected ?? "", validation.Accepted, validation.Reason));
            if (!validation.Accepted)
            {
                _logger.LogDebug("Rejected correction of segment {Id} ({Reason}): {Original} -> {Corrected}",
                    piece.Segment.Id, validation.Reason, piece.Text, corrected);
                return piece.Text;
            }

            return corrected!;
        }
    }
}
