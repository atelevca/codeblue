namespace SemanticKernel.Minutes;

/// <summary>Model assessment of the final document against the original transcript.</summary>
public sealed record MinutesVerification
{
    public required string Summary { get; init; }
    public required IReadOnlyList<MinutesDiscrepancy> Findings { get; init; }

    /// <summary>No discrepancies were reported by the model; not a guarantee of factual correctness.</summary>
    public bool IsConsistent => Findings.Count == 0;
}

public sealed record MinutesDiscrepancy
{
    /// <summary>Unsupported, Omission or Contradiction.</summary>
    public required string Kind { get; init; }
    public required string Description { get; init; }
    /// <summary>Exact source quotation; null for an unsupported claim.</summary>
    public required string? TranscriptQuote { get; init; }
    /// <summary>Exact document quotation; null for an omission.</summary>
    public required string? DocumentQuote { get; init; }
    public required string SuggestedCorrection { get; init; }
}
