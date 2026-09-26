namespace SemanticKernel.Minutes;

/// <summary>Limits for transcript-to-minutes generation and verification.</summary>
public sealed class MinutesOptions
{
    public const string SectionName = "Minutes";

    /// <summary>
    /// Maximum transcript length for a single extraction request. Oversized input is rejected, never truncated.
    /// Tune together with Llm:ContextSize and the model's tokenizer.
    /// </summary>
    public int MaxTranscriptCharacters { get; set; } = 6000;

    /// <summary>Maximum serialized facts length passed to minutes generation.</summary>
    public int MaxFactsCharacters { get; set; } = 8000;

    public int ExtractionMaxTokens { get; set; } = 3072;

    public int GenerationMaxTokens { get; set; } = 3072;

    /// <summary>Maximum serialized combined transcript and final document length. Nothing is truncated.</summary>
    public int MaxVerificationCharacters { get; set; } = 14000;

    public int VerificationMaxTokens { get; set; } = 2048;

    /// <summary>Additional attempts when a stage returns malformed or empty output.</summary>
    public int MaxRetries { get; set; } = 1;
}
