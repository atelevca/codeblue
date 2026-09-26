using HealthTech.Audio;
using Microsoft.Extensions.Options;
using Whisper.net;
using Whisper.net.LibraryLoader;
using Whisper.net.Logger;

namespace HealthTech.Transcription
{
    public interface ISpeechRecognitionService
    {
        /// <summary>
        /// Transcribes 16 kHz mono float samples in [-1, 1]. <paramref name="prompt"/> is the record
        /// profile's steering phrase; an empty string means no initial prompt.
        /// </summary>
        Task<List<TranscriptSegment>> TranscribeAsync(float[] samples, string prompt, CancellationToken cancellationToken = default);
    }

    // Singleton: the ggml large-v3 model is ~3 GB, so the factory (which holds it) is loaded once and reused.
    public sealed class WhisperSpeechRecognitionService : ISpeechRecognitionService, IDisposable
    {
        private readonly WhisperFactory _factory;
        private readonly WhisperOptions _options;
        private readonly ILogger<WhisperSpeechRecognitionService> _logger;
        private readonly IDisposable? _nativeLog;

        // One transcription at a time: a large-v3 run already saturates the GPU/CPU and each run allocates its own state.
        private readonly SemaphoreSlim _gate = new(1, 1);

        public WhisperSpeechRecognitionService(IOptions<WhisperOptions> options, ILogger<WhisperSpeechRecognitionService> logger)
        {
            _options = options.Value;
            _logger = logger;

            // Must be registered before the factory loads the native library, otherwise the Vulkan device
            // enumeration ("ggml_vulkan: Found N Vulkan devices", "ggml_vulkan: 0 = ...") is not captured.
            if (_options.NativeLogging)
            {
                _nativeLog = LogProvider.AddLogger((level, message) =>
                {
                    var text = message?.TrimEnd();
                    if (!string.IsNullOrEmpty(text))
                    {
                        _logger.LogInformation("[whisper.cpp {Level}] {Message}", level, text);
                    }
                });
            }

            _logger.LogInformation("Loading Whisper model {ModelPath} (UseGpu={UseGpu}, GpuDevice={GpuDevice}, {VisibleEnv}={VisibleDevices})",
                _options.ModelPath, _options.UseGpu, _options.GpuDevice,
                WhisperOptions.VulkanVisibleDevicesEnvVar,
                Environment.GetEnvironmentVariable(WhisperOptions.VulkanVisibleDevicesEnvVar) ?? "<not set>");
            try
            {
                _factory = WhisperFactory.FromPath(_options.ModelPath, new WhisperFactoryOptions
                {
                    UseGpu = _options.UseGpu,
                    GpuDevice = _options.GpuDevice,
                });
            }
            catch (Exception ex)
            {
                throw new AudioProcessingException(AudioProcessingError.ModelFailed,
                    $"Failed to load Whisper model '{_options.ModelPath}': {ex.Message}", ex);
            }
            // Vulkan when a GPU driver is available, otherwise the CPU fallback runtime.
            _logger.LogInformation("Whisper runtime: {Runtime} (UseGpu={UseGpu}, GpuDevice={GpuDevice})",
                RuntimeOptions.LoadedLibrary, _options.UseGpu, _options.GpuDevice);
        }

        public async Task<List<TranscriptSegment>> TranscribeAsync(
            float[] samples, string prompt, CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                var builder = _factory.CreateBuilder()
                    .WithLanguage(_options.Language)
                    .WithThreads(Environment.ProcessorCount);

                // Профиль записи задаёт свою фразу; Whisper:Prompt остаётся запасным значением
                // для профилей, у которых WhisperPrompt пуст.
                var initialPrompt = string.IsNullOrWhiteSpace(prompt) ? _options.Prompt : prompt;
                if (!string.IsNullOrWhiteSpace(initialPrompt))
                {
                    builder.WithPrompt(initialPrompt);
                }
                if (_options.BeamSize > 1)
                {
                    builder.WithBeamSearchSamplingStrategy(beam => beam.WithBeamSize(_options.BeamSize));
                }
                if (_options.NoContext)
                {
                    builder.WithNoContext();
                }

                await using var processor = builder.Build();

                var segments = new List<TranscriptSegment>();
                await foreach (var segment in processor.ProcessAsync(samples, cancellationToken))
                {
                    var text = segment.Text.Trim();
                    if (text.Length > 0)
                    {
                        segments.Add(new TranscriptSegment(segment.Start.TotalSeconds, segment.End.TotalSeconds, text));
                    }
                }

                return segments;
            }
            catch (Exception ex) when (ex is not (OperationCanceledException or AudioProcessingException))
            {
                throw new AudioProcessingException(AudioProcessingError.ModelFailed, $"Whisper transcription failed: {ex.Message}", ex);
            }
            finally
            {
                _gate.Release();
            }
        }

        public void Dispose()
        {
            _factory.Dispose();
            _gate.Dispose();
            _nativeLog?.Dispose();
        }
    }
}
