namespace SemanticKernel.Minutes;

/// <summary>Limits for transcript-to-minutes extraction and verification.</summary>
public sealed class MinutesOptions
{
    public const string SectionName = "Minutes";

    /// <summary>
    /// Maximum transcript length for a single extraction request. Oversized input is rejected, never truncated.
    /// Tune together with Llm:ContextSize and the model's tokenizer.
    /// </summary>
    public int MaxTranscriptCharacters { get; set; } = 6000;

    public int ExtractionMaxTokens { get; set; } = 3072;

    /// <summary>Maximum serialized combined transcript and final document length. Nothing is truncated.</summary>
    public int MaxVerificationCharacters { get; set; } = 14000;

    public int VerificationMaxTokens { get; set; } = 2048;

    /// <summary>Additional attempts when extraction returns malformed or empty output.</summary>
    public int MaxRetries { get; set; } = 1;

    /// <summary>
    /// Additional attempts for verification. 0 by default: a verification that failed once tends to fail the
    /// same way again (the quotes are not exact), and each attempt costs minutes on the CPU while the
    /// document itself is already done. The verification result is quality control, not the deliverable.
    /// </summary>
    public int VerificationMaxRetries { get; set; }
}
