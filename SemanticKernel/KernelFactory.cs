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
    /// Owns the local GGUF model (LLamaSharp, in-process, offline) and a Semantic Kernel <see cref="Kernel"/>
    /// with <see cref="LLamaSharpChatCompletion"/> registered as <see cref="IChatCompletionService"/>.
    /// The model path is checked in the constructor (fails fast with the expected path); the weights are loaded
    /// once, on first use, so endpoints that never correct text don't pay for the multi-GB load.
    /// </summary>
    public sealed class KernelFactory : IChatCompletionProvider, IDisposable
    {
        private readonly LlmOptions _options;
        private readonly ILogger<KernelFactory> _logger;
        private readonly SemaphoreSlim _loadLock = new(1, 1);
        private LLamaWeights? _weights;
        private Kernel? _kernel;
        private bool _disposed;

        public KernelFactory(IOptions<LlmOptions> options, ILogger<KernelFactory> logger)
        {
            _options = options.Value;
            _logger = logger;
            ModelPath = _options.ResolveModelPath(AppContext.BaseDirectory);
        }

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

            var parameters = new ModelParams(ModelPath)
            {
                ContextSize = _options.ContextSize,
                GpuLayerCount = _options.GpuLayerCount
            };

            _logger.LogInformation("Loading LLM {ModelPath} (context {ContextSize}, GPU layers {GpuLayerCount})",
                ModelPath, _options.ContextSize, _options.GpuLayerCount);
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
                _logger.LogInformation("LLM loaded in {Seconds:F1} s ({Parameters:N0} parameters)",
                    stopwatch.Elapsed.TotalSeconds, weights.ParameterCount);
                return kernel;
            }
            catch
            {
                weights.Dispose();
                throw;
            }
        }

        // llama.cpp prints the whole model metadata at info level; keep it at Debug, warnings/errors as is.
        // The callback can only be set before the native library is loaded, hence the catch.
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
                // Native library already loaded; its logs keep going to stderr.
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
