namespace SemanticKernel.Minutes;

/// <summary>Stage-one result. All narrative text is in Romanian.</summary>
public sealed record MeetingFacts
{
    public required string Summary { get; init; }
    public required IReadOnlyList<string> Decisions { get; init; }
    public required IReadOnlyList<MeetingAction> Actions { get; init; }
    public required IReadOnlyList<string> Issues { get; init; }
}

public sealed record MeetingAction
{
    public const string Unspecified = "Nespecificat";

    public required string Description { get; init; }
    public required string? Responsible { get; init; }
    public required string? Deadline { get; init; }
}

/// <summary>Intermediate facts, final Romanian minutes and their verification against the original transcript.</summary>
public sealed record MeetingMinutesResult(MeetingFacts Facts, string MinutesMarkdown, MinutesVerification Verification);
