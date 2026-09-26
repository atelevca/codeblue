using System.Diagnostics;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Transformers;
using LLamaSharp.SemanticKernel;
using LLamaSharp.SemanticKernel.ChatCompletion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace SemanticKernel
{
    /// <summary>Source of the chat model; lets the corrector run against any <see cref="IChatCompletionService"/>.</summary>
    public interface IChatCompletionProvider
    {
        Task<IChatCompletionService> GetChatCompletionAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Owns one local GGUF model (LLamaSharp, in-process, offline) and a Semantic Kernel <see cref="Kernel"/>
    /// with <see cref="LLamaSharpChatCompletion"/> registered as <see cref="IChatCompletionService"/>.
    /// One instance per <see cref="LlmModelRole"/>: the correction model and the minutes model may differ
    /// (a 3B for the many short correction requests, the 7B for the two long minutes requests); when they are
    /// the same file, DI hands both roles the same instance.
    /// The model path is checked in the constructor (fails fast with the expected path); the weights are loaded
    /// once, on first use, so endpoints that never touch the LLM don't pay for the multi-GB load.
    /// </summary>
    public sealed class KernelFactory : IChatCompletionProvider, IDisposable
    {
        private readonly LlmOptions _options;
        private readonly LlmModelSettings _model;
        private readonly ILogger<KernelFactory> _logger;
        private readonly SemaphoreSlim _loadLock = new(1, 1);
        private LLamaWeights? _weights;
        private Kernel? _kernel;
        private bool _disposed;

        public KernelFactory(LlmModelRole role, IOptions<LlmOptions> options, ILogger<KernelFactory> logger)
        {
            Role = role;
            _options = options.Value;
            _model = _options.ModelFor(role);
            _logger = logger;
            ModelPath = _options.ResolveModelPath(AppContext.BaseDirectory, _model.ModelFile);
        }

        public LlmModelRole Role { get; }

        public string ModelPath { get; }

        public async Task<Kernel> GetKernelAsync(CancellationToken cancellationToken = default)
        {
            if (_kernel != null)
            {
                return _kernel;
            }

            await _loadLock.WaitAsync(cancellationToken);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _kernel ??= await LoadAsync(cancellationToken);
            }
            finally
            {
                _loadLock.Release();
            }
        }

        public async Task<IChatCompletionService> GetChatCompletionAsync(CancellationToken cancellationToken = default) =>
            (await GetKernelAsync(cancellationToken)).GetRequiredService<IChatCompletionService>();

        private async Task<Kernel> LoadAsync(CancellationToken cancellationToken)
        {
            if (!File.Exists(ModelPath))
            {
                throw new LlmModelNotFoundException(ModelPath);
            }

            ForwardNativeLogs();

            // With the Vulkan backend installed LLamaSharp picks it before the CPU one and falls back on its
            // own when no Vulkan device is usable; GPU layers = 0 then simply keeps every layer on the CPU.
            // The device is the one ggml sees first (GGML_VK_VISIBLE_DEVICES, shared with Whisper).
            var parameters = new ModelParams(ModelPath)
            {
                ContextSize = _model.ContextSize,
                GpuLayerCount = _model.GpuLayerCount
            };

            _logger.LogInformation("Loading {Role} LLM {ModelPath} (context {ContextSize}, GPU layers {GpuLayerCount})",
                Role, ModelPath, _model.ContextSize, _model.GpuLayerCount);
            var stopwatch = Stopwatch.StartNew();
            var weights = await LLamaWeights.LoadFromFileAsync(parameters, cancellationToken, null);
            try
            {
                // StatelessExecutor creates a fresh context per request: no chat state leaks between batches.
                // ApplyTemplate stays off because PromptTemplateTransformer below already renders the prompt
                // with the model's own chat template (ChatML for Qwen); the connector's default transform
                // would produce a plain "User:/Assistant:" prompt instead.
                var executor = new StatelessExecutor(weights, parameters, _logger) { ApplyTemplate = false };
                var chatCompletion = new LLamaSharpChatCompletion(
                    executor,
                    new LLamaSharpPromptExecutionSettings
                    {
                        Temperature = _options.Temperature,
                        MaxTokens = _options.MaxTokens
                    },
                    new PromptTemplateTransformer(weights, withAssistant: true),
                    // Special tokens aren't decoded into the text; this only guards against literal ChatML markers
                    // (and replaces the connector's default "User:/Assistant:" keyword filter).
                    new LLamaTransforms.KeywordTextOutputStreamTransform(["<|im_end|>", "<|im_start|>"], 5, false));

                var builder = Kernel.CreateBuilder();
                builder.Services.AddSingleton<IChatCompletionService>(chatCompletion);
                var kernel = builder.Build();

                _weights = weights;
                _logger.LogInformation("{Role} LLM loaded in {Seconds:F1} s ({Parameters:N0} parameters)",
                    Role, stopwatch.Elapsed.TotalSeconds, weights.ParameterCount);
                return kernel;
            }
            catch
            {
                weights.Dispose();
                throw;
            }
        }

        // llama.cpp prints the whole model metadata at info level; keep it at Debug, warnings/errors as is.
        // The callback can only be set before the native library is loaded, hence the catch (the second
        // factory always lands here: the first one has loaded the library already).
        private void ForwardNativeLogs()
        {
            try
            {
                NativeLibraryConfig.All.WithLogCallback((level, message) =>
                {
                    var logLevel = level switch
                    {
                        LLamaLogLevel.Error => LogLevel.Error,
                        LLamaLogLevel.Warning => LogLevel.Warning,
                        _ => LogLevel.Debug
                    };
                    _logger.Log(logLevel, "llama.cpp: {Message}", message.TrimEnd());
                });
            }
            catch (InvalidOperationException)
            {
                // Native library already loaded; its logs keep going where the first factory sent them.
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _weights?.Dispose();
            _loadLock.Dispose();
        }
    }
}
