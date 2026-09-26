using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace HealthTech.Audio
{
    public interface IAudioProcessor
    {
        /// <summary>
        /// Validates that <paramref name="inputPath"/> contains an audio stream and writes a WAV copy of it
        /// to <paramref name="outputDirectory"/>. Sample rate and channels are preserved as in the original.
        /// </summary>
        Task<ProcessedAudio> ProcessAudioAsync(string inputPath, string outputDirectory, CancellationToken cancellationToken = default);

        /// <summary>
        /// Writes <c>&lt;name&gt;.16k.wav</c> next to <paramref name="wavPath"/> — 16 kHz mono, the input
        /// expected by Whisper, sherpa and the VAD. Returns its path; the source WAV is left untouched.
        /// </summary>
        Task<string> PrepareModelInputAsync(string wavPath, CancellationToken cancellationToken = default);

        /// <summary>
        /// Разбирает файл через ffprobe и возвращает контейнер и длительность, ничего не конвертируя
        /// и ничего не записывая. Нужен на загрузке: UI показывает карточку файла до того,
        /// как пользователь оформит запись и начнётся обработка.
        /// </summary>
        Task<AudioFileInfo> InspectAsync(string inputPath, CancellationToken cancellationToken = default);
    }

    public class AudioProcessor : IAudioProcessor
    {
        /// <summary>
        /// Suffix of the derived 16 kHz mono file, inserted before the extension. Artifact names are
        /// derived from the recording, not from this file, so consumers strip it — see
        /// <c>ProcessedAudioFiles</c>.
        /// </summary>
        public const string ModelInputSuffix = ".16k";

        private readonly AudioOptions _options;
        private readonly ILogger<AudioProcessor> _logger;

        private static readonly StringComparison PathComparison =
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        public AudioProcessor(IOptions<AudioOptions> options, ILogger<AudioProcessor> logger)
        {
            _options = options.Value;
            _logger = logger;
        }

        public async Task<ProcessedAudio> ProcessAudioAsync(
            string inputPath, string outputDirectory, CancellationToken cancellationToken = default)
        {
            var fullInputPath = Path.GetFullPath(inputPath);
            _logger.LogInformation("Processing audio file {InputPath}", fullInputPath);

            if (!File.Exists(fullInputPath))
            {
                throw new AudioProcessingException(AudioProcessingError.InputNotFound, $"Input file '{fullInputPath}' does not exist.");
            }

            var probe = await ProbeAsync(fullInputPath, cancellationToken);
            _logger.LogInformation(
                "Detected format {Format}, codec {Codec}, sample rate {SampleRate} Hz, {Channels} channel(s), sample format {SampleFormat}",
                probe.Format, probe.Codec, probe.SampleRate, probe.Channels, probe.SampleFormat);

            var wavPath = Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(fullInputPath) + ".wav");
            var isPcmWav = probe.Format == "wav" && probe.Codec.StartsWith("pcm_", StringComparison.Ordinal);
            var sameFile = string.Equals(fullInputPath, wavPath, PathComparison);

            if (sameFile && !isPcmWav)
            {
                throw new AudioProcessingException(AudioProcessingError.FileSystemError,
                    $"Output path '{wavPath}' is the input file itself; refusing to overwrite the original.");
            }

            try
            {
                Directory.CreateDirectory(outputDirectory);

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

        public async Task<AudioFileInfo> InspectAsync(string inputPath, CancellationToken cancellationToken = default)
        {
            var fullInputPath = Path.GetFullPath(inputPath);
            if (!File.Exists(fullInputPath))
            {
                throw new AudioProcessingException(AudioProcessingError.InputNotFound, $"Input file '{fullInputPath}' does not exist.");
            }

            var probe = await ProbeAsync(fullInputPath, cancellationToken);
            _logger.LogInformation("Inspected {InputPath}: format {Format}, duration {Duration} s",
                fullInputPath, probe.Format, probe.DurationSeconds);
            return new AudioFileInfo(probe.Format, probe.DurationSeconds);
        }

        public async Task<string> PrepareModelInputAsync(string wavPath, CancellationToken cancellationToken = default)
        {
            var targetPath = Path.Combine(
                Path.GetDirectoryName(wavPath)!,
                Path.GetFileNameWithoutExtension(wavPath) + ModelInputSuffix + ".wav");
            var tempPath = targetPath + ".partial";

            _logger.LogInformation("Preparing model input {TargetPath} (16 kHz mono)", targetPath);
            try
            {
                // -ar/-ac are set on purpose here: this is a derived file for the models, while the
                // full-quality WAV next to it keeps the original sample rate and channel count.
                var result = await RunAsync(_options.FfmpegPath,
                    ["-nostdin", "-hide_banner", "-loglevel", "error", "-y",
                     "-i", wavPath,
                     "-map", "0:a:0", "-vn", "-sn", "-dn",
                     "-ar", "16000", "-ac", "1", "-c:a", "pcm_s16le",
                     "-f", "wav", tempPath],
                    cancellationToken);

                if (result.ExitCode != 0)
                {
                    throw new AudioProcessingException(AudioProcessingError.ConversionFailed,
                        $"FFmpeg failed to downsample '{wavPath}' (exit code {result.ExitCode}): {result.StdErr.Trim()}");
                }

                File.Move(tempPath, targetPath, overwrite: true);
            }
            finally
            {
                TryDelete(tempPath);
            }

            return targetPath;
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
            var format = "unknown";
            double? duration = null;
            if (root.TryGetProperty("format", out var f))
            {
                format = GetString(f, "format_name") ?? "unknown";
                duration = GetSeconds(f, "duration");
            }

            // Не каждый контейнер пишет длительность в format: у сырых потоков её берут из самого потока.
            duration ??= GetSeconds(a, "duration");

            return new AudioProbe(format, duration, codec, sampleRate.Value, channels.Value,
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

        // ffprobe отдаёт длительность строкой вида "123.456000" и всегда с точкой, поэтому разбор
        // строго инвариантный: на ru-RU культуре Parse принял бы точку за разделитель групп.
        private static double? GetSeconds(JsonElement element, string name) =>
            double.TryParse(GetString(element, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            && value > 0
                ? value
                : null;

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

        private record AudioProbe(string Format, double? DurationSeconds, string Codec, int SampleRate, int Channels, string? SampleFormat, int? BitsPerRawSample);

        private record ProcessResult(int ExitCode, string StdOut, string StdErr);
    }
}
