namespace SemanticKernel.Minutes;

/// <summary>Limits for transcript-to-minutes generation and verification.</summary>
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

    /// <summary>Additional attempts when a stage returns malformed or empty output.</summary>
    public int MaxRetries { get; set; } = 1;
}
