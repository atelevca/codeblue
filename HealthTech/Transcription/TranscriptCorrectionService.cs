using System.Text.Json;
using System.Text.Json.Nodes;
using HealthTech.Audio;
using Microsoft.Extensions.Options;
using SemanticKernel.MedicalCorrection;

namespace HealthTech.Transcription
{
    public record TranscriptCorrectionResult(
        string SourceFile,
        string CorrectedFile,
        string ReportFile,
        int SegmentCount,
        int Accepted,
        int Rejected,
        IReadOnlyList<CorrectionLogEntry> Changes);

    public interface ITranscriptCorrectionService
    {
        /// <summary>
        /// Corrects medical terms in an existing transcript <c>&lt;transcripts&gt;/&lt;fileName&gt;</c> with the local LLM
        /// (texts are cut into sentence pieces and sent in batches). Saves <c>&lt;name&gt;.corrected.json</c> (same JSON,
        /// only texts changed) and <c>&lt;name&gt;.medical_corrections.md</c>; the source file is not modified.
        /// </summary>
        Task<TranscriptCorrectionResult> CorrectTranscriptAsync(string fileName, CancellationToken cancellationToken = default);
    }

    public class TranscriptCorrectionService : ITranscriptCorrectionService
    {
        // Speaker transcripts keep their items in "turns", plain transcripts in "segments".
        private static readonly string[] ItemArrays = ["turns", "segments"];

        private readonly IProcessedAudioFiles _files;
        private readonly IMedicalTermCorrector _corrector;
        private readonly TranscriptsOptions _transcriptsOptions;
        private readonly ILogger<TranscriptCorrectionService> _logger;

        public TranscriptCorrectionService(
            IProcessedAudioFiles files,
            IMedicalTermCorrector corrector,
            IOptions<TranscriptsOptions> transcriptsOptions,
            ILogger<TranscriptCorrectionService> logger)
        {
            _files = files;
            _corrector = corrector;
            _transcriptsOptions = transcriptsOptions.Value;
            _logger = logger;
        }

        public async Task<TranscriptCorrectionResult> CorrectTranscriptAsync(string fileName, CancellationToken cancellationToken = default)
        {
            var path = ResolvePath(fileName);
            var root = await ReadAsync(path, cancellationToken);
            var items = FindItems(root, path);

            var segments = items.Select((item, i) => new Segment(
                    i + 1,
                    ReadString(item, "speaker") ?? "",
                    TimeSpan.FromSeconds(ReadSeconds(item, "start")),
                    TimeSpan.FromSeconds(ReadSeconds(item, "end")),
                    ReadString(item, "text")!))
                .ToList();
            _logger.LogInformation("Correcting medical terms in {Path}: {SegmentCount} segment(s)", path, segments.Count);

            var log = new CorrectionLog();
            var corrected = await _corrector.CorrectAsync(segments, log, cancellationToken);

            for (var i = 0; i < items.Count; i++)
            {
                items[i]["text"] = corrected[i].Text;
            }
            // A speaker transcript also has the whole dialogue as "text": rebuild it from the corrected turns.
            if (root["text"] is JsonValue && segments.All(s => s.Speaker.Length > 0))
            {
                root["text"] = TranscriptDialogue.Format(corrected.Select(s => (s.Speaker, s.Text)));
            }

            // Результат кладётся рядом с исходником: он может лежать в каталоге задания.
            var directory = Path.GetDirectoryName(path)!;
            var correctedPath = await _files.SaveJsonAsync(root, directory, path, ".corrected.json", cancellationToken);
            var reportPath = await _files.SaveTextAsync(log.ToMarkdown(), directory, path, ".medical_corrections.md", cancellationToken);

            return new TranscriptCorrectionResult(
                Path.GetFileName(path), Path.GetFileName(correctedPath), Path.GetFileName(reportPath), segments.Count,
                log.Entries.Count(e => e.Accepted), log.Entries.Count(e => !e.Accepted), log.Entries);
        }

        // Only a plain file name inside the transcripts folder; ".json" may be omitted.
        private string ResolvePath(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName) || Path.GetFileName(fileName) != fileName)
            {
                throw new AudioProcessingException(AudioProcessingError.InvalidTranscript,
                    $"'{fileName}' is not a file name. Pass just the name of a file in the transcripts folder, e.g. 'Medpark_audio.speakers.json'.");
            }

            var folder = _transcriptsOptions.OutputFolder;
            var path = Path.Combine(folder, fileName);
            if (!File.Exists(path) && !fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                                   && File.Exists(path + ".json"))
            {
                path += ".json";
            }

            return File.Exists(path)
                ? path
                : throw new AudioProcessingException(AudioProcessingError.InputNotFound, $"Transcript '{fileName}' not found in '{folder}'.");
        }

        private static async Task<JsonObject> ReadAsync(string path, CancellationToken cancellationToken)
        {
            try
            {
                await using var stream = File.OpenRead(path);
                return await JsonNode.ParseAsync(stream, new JsonNodeOptions { PropertyNameCaseInsensitive = true },
                           cancellationToken: cancellationToken) as JsonObject
                       ?? throw new AudioProcessingException(AudioProcessingError.InvalidTranscript,
                           $"'{Path.GetFileName(path)}' is not a transcript: the JSON root is not an object.");
            }
            catch (JsonException ex)
            {
                throw new AudioProcessingException(AudioProcessingError.InvalidTranscript,
                    $"'{Path.GetFileName(path)}' is not valid JSON: {ex.Message}", ex);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new AudioProcessingException(AudioProcessingError.FileSystemError,
                    $"Failed to read '{path}': {ex.Message}", ex);
            }
        }

        // The first "turns"/"segments" array whose items all have a text (a diarization file has none).
        private static List<JsonObject> FindItems(JsonObject root, string path)
        {
            foreach (var name in ItemArrays)
            {
                if (root[name] is JsonArray array && array.Count > 0
                    && array.All(item => item is JsonObject o && ReadString(o, "text") != null))
                {
                    return array.Cast<JsonObject>().ToList();
                }
            }

            throw new AudioProcessingException(AudioProcessingError.InvalidTranscript,
                $"'{Path.GetFileName(path)}' has no \"turns\" or \"segments\" with text (a diarization file can't be corrected).");
        }

        private static string? ReadString(JsonObject item, string name) =>
            item[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        private static double ReadSeconds(JsonObject item, string name) =>
            item[name] is JsonValue value && value.TryGetValue<double>(out var seconds) ? seconds : 0;
    }
}
