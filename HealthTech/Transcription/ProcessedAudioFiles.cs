using System.Text.Encodings.Web;
using System.Text.Json;
using HealthTech.Audio;
using Microsoft.Extensions.Options;

namespace HealthTech.Transcription
{
    public interface IProcessedAudioFiles
    {
        /// <summary>Returns the first audio file (by name) in the processed directory.</summary>
        string FindFirst();

        /// <summary>Saves <paramref name="result"/> as <c>&lt;transcripts&gt;/&lt;source name&gt;&lt;suffix&gt;</c> and returns the path.</summary>
        Task<string> SaveJsonAsync<T>(T result, string sourcePath, string suffix, CancellationToken cancellationToken = default);
    }

    public class ProcessedAudioFiles : IProcessedAudioFiles
    {
        private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".wav", ".mp3", ".m4a", ".ogg", ".flac"
        };

        // Readable file on disk: indented, and Cyrillic kept as-is instead of \uXXXX escapes.
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        private readonly IAudioProcessor _audioProcessor;
        private readonly TranscriptsOptions _transcriptsOptions;
        private readonly ILogger<ProcessedAudioFiles> _logger;

        public ProcessedAudioFiles(
            IAudioProcessor audioProcessor,
            IOptions<TranscriptsOptions> transcriptsOptions,
            ILogger<ProcessedAudioFiles> logger)
        {
            _audioProcessor = audioProcessor;
            _transcriptsOptions = transcriptsOptions.Value;
            _logger = logger;
        }

        public string FindFirst()
        {
            var directory = _audioProcessor.OutputDirectory;
            if (!Directory.Exists(directory))
            {
                throw new AudioProcessingException(AudioProcessingError.InputNotFound,
                    $"Processed directory '{directory}' does not exist. Run GET /audio/validateAndProcess first.");
            }

            return Directory.EnumerateFiles(directory)
                       .Where(f => AudioExtensions.Contains(Path.GetExtension(f)))
                       .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                       .FirstOrDefault()
                   ?? throw new AudioProcessingException(AudioProcessingError.InputNotFound,
                       $"No audio files ({string.Join(", ", AudioExtensions)}) found in '{directory}'. Run GET /audio/validateAndProcess first.");
        }

        public async Task<string> SaveJsonAsync<T>(T result, string sourcePath, string suffix, CancellationToken cancellationToken = default)
        {
            var outputFolder = _transcriptsOptions.OutputFolder;
            var outputPath = Path.Combine(outputFolder, Path.GetFileNameWithoutExtension(sourcePath) + suffix);

            try
            {
                Directory.CreateDirectory(outputFolder);
                await using var stream = File.Create(outputPath);
                await JsonSerializer.SerializeAsync(stream, result, JsonOptions, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new AudioProcessingException(AudioProcessingError.FileSystemError,
                    $"Failed to write '{outputPath}': {ex.Message}", ex);
            }

            _logger.LogInformation("Saved {OutputPath}", outputPath);
            return outputPath;
        }
    }
}
