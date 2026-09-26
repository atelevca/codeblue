using System.Diagnostics;
using System.Text;
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
using SemanticKernel.Minutes;

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
    /// (a 3B for the many short correction requests, the 7B for the long minutes requests); when they are
    /// the same file, DI hands both roles the same instance, which then serves both roles.
    /// The model path is checked in the constructor (fails fast with the expected path); the weights are loaded
    /// once, on first use, so endpoints that never touch the LLM don't pay for the multi-GB load.
    /// It is also the <see cref="ITokenCounter"/> of its model: the tokenizer of the loaded GGUF sizes every
    /// request against this model's context.
    /// </summary>
    public sealed class KernelFactory : IChatCompletionProvider, ITokenCounter, IDisposable
    {
        private readonly LlmOptions _options;
        private readonly MinutesOptions _minutes;
        private readonly LlmModelSettings _model;
        private readonly ILogger<KernelFactory> _logger;
        private readonly SemaphoreSlim _loadLock = new(1, 1);
        private LLamaWeights? _weights;
        private Kernel? _kernel;
        // A context too small for the prompts cannot fix itself without a restart; without this every
        // later call would reload the multi-GB weights only to fail the same way.
        private LlmConfigurationException? _configurationError;
        private bool _disposed;

        /// <param name="roles">The stages this model serves; both when the two roles share one file.</param>
        public KernelFactory(IReadOnlyCollection<LlmModelRole> roles, IOptions<LlmOptions> options,
            IOptions<MinutesOptions> minutes, ILogger<KernelFactory> logger)
        {
            ArgumentOutOfRangeException.ThrowIfZero(roles.Count);
            Roles = roles;
            _options = options.Value;
            _minutes = minutes.Value;
            _model = _options.ModelFor(roles.First());
            _logger = logger;
            ModelPath = _options.ResolveModelPath(AppContext.BaseDirectory, _model.ModelFile);
        }

        public IReadOnlyCollection<LlmModelRole> Roles { get; }

        private string RoleName => string.Join("+", Roles);

        public string ModelPath { get; }

        public uint ContextSize => _model.ContextSize;

        public async Task<int> CountAsync(string text, CancellationToken ct = default) =>
            Count(await GetWeightsAsync(ct), text, addBos: false);

        public async Task<int> CountPromptAsync(string systemPrompt, string userMessage, CancellationToken ct = default) =>
            CountPrompt(await GetWeightsAsync(ct), systemPrompt, userMessage);

        private async Task<LLamaWeights> GetWeightsAsync(CancellationToken ct)
        {
            await GetKernelAsync(ct);
            return _weights!;
        }

        private static int Count(LLamaWeights weights, string text, bool addBos) =>
            weights.Tokenize(text, addBos, true, Encoding.UTF8).Length;

        // Renders exactly what the executor receives: PromptTemplateTransformer with the model's chat template.
        private static int CountPrompt(LLamaWeights weights, string systemPrompt, string userMessage)
        {
            var history = new LLama.Common.ChatHistory();
            history.AddMessage(LLama.Common.AuthorRole.System, systemPrompt);
            history.AddMessage(LLama.Common.AuthorRole.User, userMessage);
            return Count(weights, new PromptTemplateTransformer(weights, withAssistant: true).HistoryToText(history), addBos: true);
        }

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
                if (_configurationError != null)
                {
                    throw new LlmConfigurationException(_configurationError.Message);
                }
                try
                {
                    return _kernel ??= await LoadAsync(cancellationToken);
                }
                catch (LlmConfigurationException ex)
                {
                    _configurationError = ex;
                    throw;
                }
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
                RoleName, ModelPath, _model.ContextSize, _model.GpuLayerCount);
            var stopwatch = Stopwatch.StartNew();
            var weights = await LLamaWeights.LoadFromFileAsync(parameters, cancellationToken, null);
            try
            {
                ValidateContext(weights);

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
                    RoleName, stopwatch.Elapsed.TotalSeconds, weights.ParameterCount);
                return kernel;
            }
            catch
            {
                weights.Dispose();
                throw;
            }
        }

        // The weights load lazily, so this is the earliest point the tokenizer exists. For every role this model
        // serves, pairs the largest of that role's prompts with the largest of its replies: if even a minimal
        // window does not fit, every long job would fail later with NoKvSlot. The correction role reads the
        // profile prompts (*_correction.system.txt), the minutes role its own (minutes_*.system.txt).
        private void ValidateContext(LLamaWeights weights)
        {
            var promptsDirectory = Path.Combine(AppContext.BaseDirectory, "Prompts");
            var margin = TokenBudget.Margin(_model.ContextSize);
            foreach (var role in Roles)
            {
                var (pattern, reply, replySettings) = role == LlmModelRole.Minutes
                    ? ("minutes_*.system.txt", Math.Max(_minutes.ExtractionMaxTokens, _minutes.VerificationMaxTokens),
                        "Minutes:ExtractionMaxTokens / Minutes:VerificationMaxTokens")
                    : ("*_correction.system.txt", _options.MaxTokens, "Llm:MaxTokens");
                var largest = Directory.GetFiles(promptsDirectory, pattern)
                    .Select(path => (Name: Path.GetFileName(path), Tokens: CountPrompt(weights, File.ReadAllText(path), "")))
                    .DefaultIfEmpty((Name: "(none)", Tokens: 0))
                    .MaxBy(prompt => prompt.Tokens);
                var needed = largest.Tokens + reply + TokenBudget.MinimalWindowTokens + margin;
                if (needed > _model.ContextSize)
                {
                    var contextSetting = role == LlmModelRole.Minutes && Roles.Count == 1 ? "Llm:Minutes:ContextSize" : "Llm:ContextSize";
                    throw new LlmConfigurationException(
                        $"{contextSetting} = {_model.ContextSize} is too small for the {role} model: prompt {largest.Name} ({largest.Tokens} tokens) " +
                        $"+ reply {reply} + window {TokenBudget.MinimalWindowTokens} + margin {margin} need {needed}. " +
                        $"Raise {contextSetting} or lower {replySettings}.");
                }
                _logger.LogInformation("{Role} LLM context check: {ContextSize} tokens, largest prompt {Prompt} {PromptTokens} tokens, largest reply {Reply}",
                    role, _model.ContextSize, largest.Name, largest.Tokens, reply);
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
