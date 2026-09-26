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

        public MedicalTermCorrector(IChatCompletionProvider chatProvider, IOptions<LlmOptions> options, ILogger<MedicalTermCorrector> logger)
        {
            _chatProvider = chatProvider;
            _options = options.Value;
            _logger = logger;
        }

        /// <summary>A sentence piece of a segment's text. <see cref="Text"/> is trimmed; the whitespace is kept aside.</summary>
        private record Piece(int Id, int SegmentIndex, Segment Segment, string Leading, string Text, string Trailing);

        public async Task<IReadOnlyList<Segment>> CorrectAsync(
            IReadOnlyList<Segment> segments, RecordProfileContent profile, CorrectionLog log, CancellationToken ct = default)
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
                foreach (var part in TextPieces.Split(segment.Text, Math.Max(1, _options.MaxPieceCharacters)))
                {
                    var text = part.Trim();
                    if (text.Length == 0)
                    {
                        continue;
                    }
                    var leading = part[..part.IndexOf(text, StringComparison.Ordinal)];
                    pieces.Add(new Piece(pieces.Count + 1, index, segment, leading, text, part[(leading.Length + text.Length)..]));
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
                var text = string.Concat(own.Select(p => p.Leading + finalTexts[p.Id] + p.Trailing));
                return segment with { Text = text };
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

        // Returns id -> corrected text, or null when the model never produced a usable answer for the batch.
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
                    var reply = await chat.GetChatMessageContentAsync(history, settings, cancellationToken: ct);
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
                segments = batch.Select(p => new { id = p.Id, text = p.Text })
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
