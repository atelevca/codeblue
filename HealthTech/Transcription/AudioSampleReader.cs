using HealthTech.Audio;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace HealthTech.Transcription
{
    public interface IAudioSampleReader
    {
        /// <summary>
        /// Reads a WAV file into 16 kHz mono float samples in [-1, 1], the input format expected by Whisper and sherpa-onnx.
        /// Files already in that format are read as-is; others are downmixed and resampled in memory.
        /// </summary>
        Task<float[]> ReadMono16kAsync(string path, CancellationToken cancellationToken = default);
    }

    public class AudioSampleReader : IAudioSampleReader
    {
        public const int TargetSampleRate = 16000;

        private readonly ILogger<AudioSampleReader> _logger;

        public AudioSampleReader(ILogger<AudioSampleReader> logger)
        {
            _logger = logger;
        }

        public Task<float[]> ReadMono16kAsync(string path, CancellationToken cancellationToken = default) =>
            Task.Run(() => Read(path, cancellationToken), cancellationToken);

        private float[] Read(string path, CancellationToken cancellationToken)
        {
            // The normalization pipeline only ever writes WAV, and NAudio.Core has no decoders for other containers.
            if (!string.Equals(Path.GetExtension(path), ".wav", StringComparison.OrdinalIgnoreCase))
            {
                throw new AudioProcessingException(AudioProcessingError.NotAudio,
                    $"'{path}' is not a WAV file. Normalize it first with GET /audio/validateAndProcess.");
            }

            try
            {
                using var reader = new WaveFileReader(path);
                var format = reader.WaveFormat;

                // Throws ArgumentException for encodings other than PCM / IEEE float (e.g. ADPCM inside WAV).
                var provider = reader.ToSampleProvider();
                if (format.Channels > 1)
                {
                    provider = new DownmixToMonoSampleProvider(provider);
                }
                if (format.SampleRate != TargetSampleRate)
                {
                    provider = new WdlResamplingSampleProvider(provider, TargetSampleRate);
                }

                var needsConversion = format.Channels != 1 || format.SampleRate != TargetSampleRate;
                _logger.LogInformation(
                    "Reading {Path}: {Encoding} {SampleRate} Hz, {Channels} channel(s), {Bits}-bit; {Action}",
                    path, format.Encoding, format.SampleRate, format.Channels, format.BitsPerSample,
                    needsConversion ? $"converting to {TargetSampleRate} Hz mono in memory" : "no conversion needed");

                var expectedSamples = (long)(reader.SampleCount * (double)TargetSampleRate / format.SampleRate) + TargetSampleRate;
                var samples = new List<float>((int)Math.Min(expectedSamples, Array.MaxLength));
                var buffer = new float[TargetSampleRate * 10];
                int read;
                while ((read = provider.Read(buffer)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    samples.AddRange(buffer.AsSpan(0, read));
                }

                if (samples.Count == 0)
                {
                    throw new AudioProcessingException(AudioProcessingError.CorruptedAudio, $"'{path}' contains no audio samples.");
                }

                return samples.ToArray();
            }
            catch (Exception ex) when (ex is FormatException or InvalidDataException or ArgumentException or EndOfStreamException)
            {
                throw new AudioProcessingException(AudioProcessingError.CorruptedAudio,
                    $"'{path}' could not be read as PCM WAV: {ex.Message}", ex);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new AudioProcessingException(AudioProcessingError.FileSystemError,
                    $"Failed to read '{path}': {ex.Message}", ex);
            }
        }

        // Averages all channels into one; NAudio's ToMono only handles stereo.
        private sealed class DownmixToMonoSampleProvider : ISampleProvider
        {
            private readonly ISampleProvider _source;
            private readonly int _channels;
            private float[] _sourceBuffer = [];

            public DownmixToMonoSampleProvider(ISampleProvider source)
            {
                _source = source;
                _channels = source.WaveFormat.Channels;
                WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
            }

            public WaveFormat WaveFormat { get; }

            public int Read(Span<float> buffer)
            {
                var needed = buffer.Length * _channels;
                if (_sourceBuffer.Length < needed)
                {
                    _sourceBuffer = new float[needed];
                }

                var frames = _source.Read(_sourceBuffer.AsSpan(0, needed)) / _channels;
                for (var frame = 0; frame < frames; frame++)
                {
                    var sum = 0f;
                    for (var channel = 0; channel < _channels; channel++)
                    {
                        sum += _sourceBuffer[frame * _channels + channel];
                    }
                    buffer[frame] = sum / _channels;
                }

                return frames;
            }
        }
    }
}
