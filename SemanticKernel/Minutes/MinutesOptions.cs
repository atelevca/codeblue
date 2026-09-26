namespace SemanticKernel.Minutes;

/// <summary>Limits for transcript-to-minutes extraction and verification.</summary>
public sealed class MinutesOptions
{
    public const string SectionName = "Minutes";

    /// <summary>
    /// Largest transcript window per extraction request, in tokens. Caps the window on every machine: the
    /// extraction reply must hold every fact of its window, and a 7B model reads a short window more carefully.
    /// </summary>
    public int MaxWindowTokens { get; set; } = 4000;

    public int ExtractionMaxTokens { get; set; } = 3072;

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
