namespace SemanticKernel
{
    /// <summary>
    /// Counts tokens with the loaded model's own tokenizer. Characters are not a proxy: Romanian with
    /// diacritics and Cyrillic tokenize very differently from English.
    /// </summary>
    public interface ITokenCounter
    {
        uint ContextSize { get; }

        Task<int> CountAsync(string text, CancellationToken ct = default);

        /// <summary>Tokens of the full rendered prompt (chat template included) for a system + user exchange.</summary>
        Task<int> CountPromptAsync(string systemPrompt, string userMessage, CancellationToken ct = default);
    }

    public static class TokenBudget
    {
        /// <summary>Smallest transcript window worth sending; below it the context is misconfigured.</summary>
        public const int MinimalWindowTokens = 1000;

        public static int Margin(uint contextSize) => Math.Max(128, (int)(contextSize * 0.05));

        /// <summary>Tokens the rendered prompt may take so that a reply of <paramref name="replyTokens"/> still fits.</summary>
        public static int ForInput(uint contextSize, int replyTokens) =>
            (int)contextSize - replyTokens - Margin(contextSize);
    }

    /// <summary>Llm:ContextSize cannot hold the prompts together with their replies.</summary>
    public sealed class LlmConfigurationException(string message) : InvalidOperationException(message);
}
