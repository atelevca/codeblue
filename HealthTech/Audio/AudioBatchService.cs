namespace HealthTech.Audio
{
    public interface IAudioBatchService
    {
        /// <summary>
        /// Normalizes every file in the configured input directory to WAV.
        /// A file that fails does not stop the rest; its error is reported alongside the successful results.
        /// Throws <see cref="AudioProcessingException"/> when the input directory is missing or FFmpeg is unavailable.
        /// </summary>
        Task<AudioRunResult> ProcessInputDirectoryAsync(CancellationToken cancellationToken = default);
    }

    public class AudioBatchService : IAudioBatchService
    {
        private readonly IAudioProcessor _audioProcessor;

        public AudioBatchService(IAudioProcessor audioProcessor)
        {
            _audioProcessor = audioProcessor;
        }

        public async Task<AudioRunResult> ProcessInputDirectoryAsync(CancellationToken cancellationToken = default)
        {
            if (!Directory.Exists(_audioProcessor.InputDirectory))
            {
                throw new AudioProcessingException(AudioProcessingError.InputNotFound,
                    $"Input directory '{_audioProcessor.InputDirectory}' does not exist.");
            }

            var processed = new List<ProcessedAudio>();
            var failed = new List<AudioRunFailure>();

            foreach (var file in Directory.EnumerateFiles(_audioProcessor.InputDirectory))
            {
                try
                {
                    processed.Add(await _audioProcessor.ProcessAudioAsync(file, cancellationToken));
                }
                catch (AudioProcessingException ex) when (ex.Error != AudioProcessingError.FfmpegUnavailable)
                {
                    // FfmpegUnavailable propagates: every remaining file would fail the same way.
                    failed.Add(new AudioRunFailure(file, ex.Error.ToString(), ex.Message));
                }
            }

            return new AudioRunResult(processed, failed);
        }
    }

    public record AudioRunFailure(string Path, string Error, string Message);

    public record AudioRunResult(IReadOnlyList<ProcessedAudio> Processed, IReadOnlyList<AudioRunFailure> Failed);
}
