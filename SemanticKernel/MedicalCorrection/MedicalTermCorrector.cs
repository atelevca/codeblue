using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using LLamaSharp.SemanticKernel;
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
    public class MedicalTermCorrector : IMedicalTermCorrector
    {
        // The echoed reply is estimated, not counted: keep a fifth of MaxTokens for what the estimate
        // misses (pretty-printed JSON, escapes). A truncated reply loses the whole batch's corrections.
        private const double ReplyShare = 0.8;

        // Context pieces only help the model understand the topic; long ones are cut to their end.
        private const int MaxContextPieceLength = 400;

        // Cyrillic as-is instead of \uXXXX escapes: the model has to read and echo the text.
        private static readonly JsonSerializerOptions RequestJsonOptions = new(JsonSerializerDefaults.Web)
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        private readonly IChatCompletionProvider _chatProvider;
        private readonly ITokenCounter _tokens;
        private readonly LlmOptions _options;
        private readonly ILogger<MedicalTermCorrector> _logger;
        private readonly SemaphoreSlim _runLock = new(1, 1);

        public MedicalTermCorrector(IChatCompletionProvider chatProvider, ITokenCounter tokens, IOptions<LlmOptions> options,
            ILogger<MedicalTermCorrector> logger)
        {
            _chatProvider = chatProvider;
            _tokens = tokens;
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
                var sizing = Stopwatch.StartNew();
                var batches = await SplitIntoBatchesAsync(pieces, profile, ct);
                _logger.LogInformation("Medical correction: {PieceCount} piece(s) sized into {BatchCount} batch(es) in {Seconds:F1} s",
                    pieces.Count, batches.Count, sizing.Elapsed.TotalSeconds);
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

        // A batch closes when the full request (system prompt, pieces with their lowConfidence lists, context
        // pieces and the glossary lines selected for them) would not fit next to the reply; when the pieces the
        // model echoes back would not fit the reply; or at BatchSize pieces. Sizing uses the original texts of the
        // context pieces; at run time they carry corrections, which the validator keeps within ±30% of a piece
        // of at most 400 characters, well inside TokenBudget.Margin.
        private async Task<List<List<Piece>>> SplitIntoBatchesAsync(List<Piece> pieces, RecordProfileContent profile, CancellationToken ct)
        {
            var batchSize = Math.Max(1, _options.BatchSize);
            var inputBudget = TokenBudget.ForInput(_tokens.ContextSize, _options.MaxTokens);
            var batches = new List<List<Piece>>();
            var current = new List<Piece>();
            var replyTokens = 0;
            foreach (var piece in pieces)
            {
                var pieceReply = await EstimateReplyTokensAsync(piece, ct);
                if (current.Count > 0)
                {
                    var fits = current.Count < batchSize
                        && replyTokens + pieceReply <= _options.MaxTokens * ReplyShare
                        && await CountRequestAsync(profile, pieces, [.. current, piece], ct) <= inputBudget;
                    if (!fits)
                    {
                        batches.Add(current);
                        current = [];
                        replyTokens = 0;
                    }
                }
                if (current.Count == 0)
                {
                    var alone = await CountRequestAsync(profile, pieces, [piece], ct);
                    if (alone > inputBudget)
                    {
                        _logger.LogWarning("Medical correction piece {Id} alone needs {Tokens} input tokens, budget {Budget}; sent anyway",
                            piece.Id, alone, inputBudget);
                    }
                }
                current.Add(piece);
                replyTokens += pieceReply;
            }
            if (current.Count > 0)
            {
                batches.Add(current);
            }
            return batches;
        }

        // The reply is [{"id","text"}] for every piece: its text plus ~15% for JSON escaping and 12 tokens of framing.
        private async Task<int> EstimateReplyTokensAsync(Piece piece, CancellationToken ct) =>
            (int)Math.Ceiling(await _tokens.CountAsync(piece.Text, ct) * 1.15) + 12;

        private Task<int> CountRequestAsync(RecordProfileContent profile, List<Piece> all, IReadOnlyList<Piece> batch, CancellationToken ct) =>
            _tokens.CountPromptAsync(profile.SystemPrompt, BuildUserMessage(profile, ContextBefore(all, batch[0]), batch), ct);

        // Piece ids are 1..N in order, so the pieces before `first` are the first Id - 1.
        private List<Piece> ContextBefore(List<Piece> all, Piece first) =>
            all.Take(first.Id - 1).TakeLast(_options.ContextSegments).ToList();

        // Returns id -> corrected text, or null when the model never produced a usable answer for the batch.
        private async Task<Dictionary<int, string>?> RequestCorrectionsAsync(
            IChatCompletionService chat, RecordProfileContent profile, IReadOnlyList<Piece> context,
            IReadOnlyList<Piece> batch, int batchNumber, CancellationToken ct)
        {
            var history = new ChatHistory(profile.SystemPrompt);
            history.AddUserMessage(BuildUserMessage(profile, context, batch));
            _logger.LogInformation("Medical correction batch {Batch}: {Tokens}/{Budget} input tokens, {PieceCount} piece(s)",
                batchNumber,
                await _tokens.CountPromptAsync(profile.SystemPrompt, history[^1].Content ?? "", ct),
                TokenBudget.ForInput(_tokens.ContextSize, _options.MaxTokens), batch.Count);
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

                var ids = parsed.Select(s => s.Id).ToList();
                if (ids.Count != expectedIds.Count || !expectedIds.SetEquals(ids))
                {
                    _logger.LogWarning("Medical correction batch {Batch}, attempt {Attempt}/{Attempts}: ids [{Ids}] don't match [{ExpectedIds}]",
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
            var texts = batch.Select(p => p.Text).ToList();
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
