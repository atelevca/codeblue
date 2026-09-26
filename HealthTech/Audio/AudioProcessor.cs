using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace HealthTech.Audio
{
    public interface IAudioProcessor
    {
        /// <summary>Absolute path of the configured input directory.</summary>
        string InputDirectory { get; }

        /// <summary>Absolute path of the configured output (processed) directory.</summary>
        string OutputDirectory { get; }

        /// <summary>
        /// Validates that <paramref name="inputPath"/> contains an audio stream and writes a WAV copy of it
        /// to the configured output directory. Relative paths are resolved against the configured input directory.
        /// </summary>
        Task<ProcessedAudio> ProcessAudioAsync(string inputPath, CancellationToken cancellationToken = default);
    }

    public class AudioProcessor : IAudioProcessor
    {
        private readonly AudioOptions _options;
        private readonly string _inputDirectory;
        private readonly string _outputDirectory;
        private readonly ILogger<AudioProcessor> _logger;

        private static readonly StringComparison PathComparison =
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        public AudioProcessor(IOptions<AudioOptions> options, IHostEnvironment environment, ILogger<AudioProcessor> logger)
        {
            _options = options.Value;
            _inputDirectory = Path.GetFullPath(_options.InputDirectory, environment.ContentRootPath);
            _outputDirectory = Path.GetFullPath(_options.OutputDirectory, environment.ContentRootPath);
            _logger = logger;
        }

        public string InputDirectory => _inputDirectory;

        public string OutputDirectory => _outputDirectory;

        public async Task<ProcessedAudio> ProcessAudioAsync(string inputPath, CancellationToken cancellationToken = default)
        {
            var fullInputPath = Path.GetFullPath(inputPath, _inputDirectory);
            _logger.LogInformation("Processing audio file {InputPath}", fullInputPath);

            if (!File.Exists(fullInputPath))
            {
                throw new AudioProcessingException(AudioProcessingError.InputNotFound, $"Input file '{fullInputPath}' does not exist.");
            }

            var probe = await ProbeAsync(fullInputPath, cancellationToken);
            _logger.LogInformation(
                "Detected format {Format}, codec {Codec}, sample rate {SampleRate} Hz, {Channels} channel(s), sample format {SampleFormat}",
                probe.Format, probe.Codec, probe.SampleRate, probe.Channels, probe.SampleFormat);

            var wavPath = Path.Combine(_outputDirectory, Path.GetFileNameWithoutExtension(fullInputPath) + ".wav");
            var isPcmWav = probe.Format == "wav" && probe.Codec.StartsWith("pcm_", StringComparison.Ordinal);
            var sameFile = string.Equals(fullInputPath, wavPath, PathComparison);

            if (sameFile && !isPcmWav)
            {
                throw new AudioProcessingException(AudioProcessingError.FileSystemError,
                    $"Output path '{wavPath}' is the input file itself; refusing to overwrite the original.");
            }

            try
            {
                Directory.CreateDirectory(_outputDirectory);

                if (isPcmWav)
                {
                    if (!sameFile)
                    {
                        File.Copy(fullInputPath, wavPath, overwrite: true);
                    }
                    _logger.LogInformation("Input is already PCM WAV; copied without re-encoding to {WavPath}", wavPath);
                }
                else
                {
                    await ConvertAsync(fullInputPath, wavPath, probe, cancellationToken);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new AudioProcessingException(AudioProcessingError.FileSystemError,
                    $"Failed to write '{wavPath}': {ex.Message}", ex);
            }

            return new ProcessedAudio(fullInputPath, wavPath, probe.Format, probe.Codec, probe.SampleRate, probe.Channels,
                Converted: !isPcmWav);
        }

        private async Task<AudioProbe> ProbeAsync(string path, CancellationToken cancellationToken)
        {
            var result = await RunAsync(_options.FfprobePath,
                ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", path],
                cancellationToken);

            if (result.ExitCode != 0)
            {
                throw new AudioProcessingException(AudioProcessingError.NotAudio,
                    $"'{path}' is not a recognized media file or is corrupted: {result.StdErr.Trim()}");
            }

            using var json = JsonDocument.Parse(result.StdOut);
            var root = json.RootElement;

            JsonElement? audio = null;
            if (root.TryGetProperty("streams", out var streams))
            {
                foreach (var stream in streams.EnumerateArray())
                {
                    if (GetString(stream, "codec_type") == "audio")
                    {
                        audio = stream;
                        break;
                    }
                }
            }

            if (audio is not { } a)
            {
                throw new AudioProcessingException(AudioProcessingError.NotAudio, $"'{path}' does not contain an audio stream.");
            }

            var codec = GetString(a, "codec_name");
            var sampleRate = GetInt(a, "sample_rate");
            var channels = GetInt(a, "channels");
            if (string.IsNullOrEmpty(codec) || sampleRate is null or <= 0 || channels is null or <= 0)
            {
                throw new AudioProcessingException(AudioProcessingError.CorruptedAudio,
                    $"'{path}' has an audio stream with missing codec, sample rate or channel information.");
            }

            // format_name can be a list of aliases, e.g. "mov,mp4,m4a,3gp,3g2,mj2".
            var format = root.TryGetProperty("format", out var f) ? GetString(f, "format_name") ?? "unknown" : "unknown";

            return new AudioProbe(format, codec, sampleRate.Value, channels.Value,
                GetString(a, "sample_fmt"), GetInt(a, "bits_per_raw_sample"));
        }

        private async Task ConvertAsync(string inputPath, string wavPath, AudioProbe probe, CancellationToken cancellationToken)
        {
            var pcmCodec = ChoosePcmCodec(probe);
            // Write to a temporary file first so a failed conversion never leaves a partial output behind.
            var tempPath = wavPath + ".partial";

            _logger.LogInformation("Converting {InputPath} to WAV ({PcmCodec}) at {WavPath}", inputPath, pcmCodec, wavPath);

            try
            {
                // Sample rate and channel count are intentionally not set, so FFmpeg keeps the source values.
                var result = await RunAsync(_options.FfmpegPath,
                    ["-nostdin", "-hide_banner", "-loglevel", "error", "-y",
                     "-i", inputPath,
                     "-map", "0:a:0", "-vn", "-sn", "-dn",
                     "-c:a", pcmCodec, "-rf64", "auto",
                     "-f", "wav", tempPath],
                    cancellationToken);

                if (result.ExitCode != 0)
                {
                    var stderr = result.StdErr.Trim();
                    var error = stderr.Contains("Invalid data found", StringComparison.OrdinalIgnoreCase)
                        ? AudioProcessingError.CorruptedAudio
                        : AudioProcessingError.ConversionFailed;
                    throw new AudioProcessingException(error,
                        $"FFmpeg failed to convert '{inputPath}' (exit code {result.ExitCode}): {stderr}");
                }

                File.Move(tempPath, wavPath, overwrite: true);
            }
            finally
            {
                TryDelete(tempPath);
            }

            _logger.LogInformation("Conversion succeeded: {WavPath}", wavPath);
        }

        // Keeps the source bit depth where it is known; lossy decoders (MP3, AAC, Opus, ...) produce float samples,
        // for which 16-bit PCM is the conventional WAV target.
        private static string ChoosePcmCodec(AudioProbe probe)
        {
            return probe.SampleFormat switch
            {
                "u8" or "u8p" => "pcm_u8",
                "s16" or "s16p" => "pcm_s16le",
                "s32" or "s32p" when probe.BitsPerRawSample is > 16 and <= 24 => "pcm_s24le",
                "s32" or "s32p" or "s64" or "s64p" => "pcm_s32le",
                "flt" or "fltp" when probe.Codec.StartsWith("pcm_f", StringComparison.Ordinal) => "pcm_f32le",
                "dbl" or "dblp" when probe.Codec.StartsWith("pcm_f", StringComparison.Ordinal) => "pcm_f64le",
                _ => "pcm_s16le"
            };
        }

        private async Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments, CancellationToken cancellationToken)
        {
            var startInfo = new ProcessStartInfo(fileName, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = startInfo };
            try
            {
                process.Start();
            }
            catch (Win32Exception ex)
            {
                throw new AudioProcessingException(AudioProcessingError.FfmpegUnavailable,
                    $"Could not start '{fileName}'. Make sure FFmpeg is installed and the path is configured in '{AudioOptions.SectionName}'.", ex);
            }

            var stdOut = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stdErr = process.StandardError.ReadToEndAsync(cancellationToken);

            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw;
            }

            return new ProcessResult(process.ExitCode, await stdOut, await stdErr);
        }

        private void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not delete temporary file {Path}", path);
            }
        }

        private static string? GetString(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        // ffprobe reports some numbers as strings (e.g. "sample_rate": "44100").
        private static int? GetInt(JsonElement element, string name)
        {
            if (!element.TryGetProperty(name, out var value))
            {
                return null;
            }
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            {
                return number;
            }
            return value.ValueKind == JsonValueKind.String
                && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
        }

        private record AudioProbe(string Format, string Codec, int SampleRate, int Channels, string? SampleFormat, int? BitsPerRawSample);

        private record ProcessResult(int ExitCode, string StdOut, string StdErr);
    }
}
