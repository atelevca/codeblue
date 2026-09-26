namespace SemanticKernel.Minutes;

/// <summary>Model assessment of the final document against the original transcript.</summary>
public sealed record MinutesVerification
{
    public required string Summary { get; init; }
    public required IReadOnlyList<MinutesDiscrepancy> Findings { get; init; }

    /// <summary>
    /// False when the verification stage did not produce a usable answer. Такой документ
    /// сохраняется как есть, но выдавать его за сверенный нельзя: пустой список находок
    /// у несостоявшейся сверки означает "не проверяли", а не "расхождений нет".
    /// </summary>
    public bool Completed { get; init; } = true;

    /// <summary>
    /// Находки, отброшенные при проверке: цитата не нашлась в источнике даже после
    /// нормализации пробелов, регистра и диакритики, либо не хватало обязательных полей.
    /// Модель что-то нашла, но доказать не смогла, поэтому такой документ не считается сверенным.
    /// </summary>
    public int DiscardedFindings { get; init; }

    /// <summary>No discrepancies were reported by the model; not a guarantee of factual correctness.</summary>
    public bool IsConsistent => Completed && Findings.Count == 0 && DiscardedFindings == 0;
}

public sealed record MinutesDiscrepancy
{
    public static readonly IReadOnlySet<string> Kinds =
        new HashSet<string>(StringComparer.Ordinal) { "Unsupported", "Omission", "Contradiction", "Misattribution" };

    /// <summary>Document section the finding belongs to ("Antet", "Participanți", "Acțiuni", ...); null when the model gave none.</summary>
    public string? Section { get; init; }
    /// <summary>Unsupported, Omission, Contradiction or Misattribution.</summary>
    public required string Kind { get; init; }
    public required string Description { get; init; }
    /// <summary>Exact source quotation (transcript or metadata); null for an unsupported claim.</summary>
    public required string? TranscriptQuote { get; init; }
    /// <summary>Exact document quotation; null for an omission.</summary>
    public required string? DocumentQuote { get; init; }
    public required string SuggestedCorrection { get; init; }
}
