using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using HealthTech.Audio;

namespace HealthTech.Transcription
{
    public interface IProcessedAudioFiles
    {
        /// <summary>Saves <paramref name="result"/> as <c>&lt;outputDirectory&gt;/&lt;source name&gt;&lt;suffix&gt;</c> and returns the path.</summary>
        Task<string> SaveJsonAsync<T>(T result, string outputDirectory, string sourcePath, string suffix,
            CancellationToken cancellationToken = default);

        /// <summary>Saves <paramref name="text"/> as <c>&lt;outputDirectory&gt;/&lt;source name&gt;&lt;suffix&gt;</c> (UTF-8) and returns the path.</summary>
        Task<string> SaveTextAsync(string text, string outputDirectory, string sourcePath, string suffix,
            CancellationToken cancellationToken = default);
    }

    public class ProcessedAudioFiles : IProcessedAudioFiles
    {
        // Readable file on disk: indented, and Cyrillic kept as-is instead of \uXXXX escapes.
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        private readonly ILogger<ProcessedAudioFiles> _logger;

        public ProcessedAudioFiles(ILogger<ProcessedAudioFiles> logger)
        {
            _logger = logger;
        }

        public Task<string> SaveJsonAsync<T>(T result, string outputDirectory, string sourcePath, string suffix,
            CancellationToken cancellationToken = default) =>
            SaveAsync(outputDirectory, sourcePath, suffix,
                async stream => await JsonSerializer.SerializeAsync(stream, result, JsonOptions, cancellationToken));

        public Task<string> SaveTextAsync(string text, string outputDirectory, string sourcePath, string suffix,
            CancellationToken cancellationToken = default) =>
            SaveAsync(outputDirectory, sourcePath, suffix,
                async stream => await stream.WriteAsync(Encoding.UTF8.GetBytes(text), cancellationToken));

        // Artifacts are named after the recording. The models read the derived <name>.16k.wav, so that
        // suffix is dropped here — otherwise every artifact would come out as "<name>.16k.speakers.json".
        private static string BaseName(string sourcePath)
        {
            var name = Path.GetFileNameWithoutExtension(sourcePath);
            return name.EndsWith(AudioProcessor.ModelInputSuffix, StringComparison.OrdinalIgnoreCase)
                ? name[..^AudioProcessor.ModelInputSuffix.Length]
                : name;
        }

        private async Task<string> SaveAsync(string outputDirectory, string sourcePath, string suffix, Func<Stream, Task> write)
        {
            var outputPath = Path.Combine(outputDirectory, BaseName(sourcePath) + suffix);

            try
            {
                Directory.CreateDirectory(outputDirectory);
                await using var stream = File.Create(outputPath);
                await write(stream);
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
