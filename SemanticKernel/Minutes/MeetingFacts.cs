namespace SemanticKernel.Minutes;

/// <summary>
/// Stage-one result, structured like a minutes-of-meeting document. All narrative text is in Romanian.
/// Serialized with snake_case names (agenda_id, open_issues, ...) - the same names the prompts use.
/// Any text field the transcript does not state explicitly is <see cref="Unspecified"/>.
/// </summary>
public sealed record MeetingFacts
{
    public const string Unspecified = "Nespecificat";

    public MeetingHeader? Meeting { get; init; }
    public MeetingParticipants? Participants { get; init; }

    /// <summary>True when the agenda was announced in the meeting, false when it was inferred from the topics discussed.</summary>
    public bool AgendaExplicit { get; init; }

    public IReadOnlyList<AgendaItem>? Agenda { get; init; }
    public IReadOnlyList<MeetingDecision>? Decisions { get; init; }
    public IReadOnlyList<MeetingAction>? Actions { get; init; }
    public IReadOnlyList<MeetingIssue>? OpenIssues { get; init; }
    public string? NextMeeting { get; init; }
    public required string Summary { get; init; }
}

public sealed record MeetingHeader
{
    public string? Title { get; init; }
    public string? Date { get; init; }
    public string? Time { get; init; }
    public string? Location { get; init; }
}

public sealed record MeetingParticipants
{
    public string? Chair { get; init; }
    public string? Secretary { get; init; }
    public IReadOnlyList<MeetingParticipant>? Present { get; init; }
    public IReadOnlyList<string>? Absent { get; init; }
}

public sealed record MeetingParticipant
{
    public required string Name { get; init; }
    /// <summary>Function or specialty; <see cref="MeetingFacts.Unspecified"/> when not stated.</summary>
    public string? Role { get; init; }
}

public sealed record AgendaItem
{
    /// <summary>Sequential number starting at 1; decisions, actions and issues refer to it through agenda_id.</summary>
    public int Id { get; init; }
    public required string Topic { get; init; }
    public required string Discussion { get; init; }
}

public sealed record MeetingDecision
{
    public required string Description { get; init; }
    public int? AgendaId { get; init; }
}

public sealed record MeetingAction
{
    public const string Unspecified = MeetingFacts.Unspecified;

    public required string Description { get; init; }
    public string? Responsible { get; init; }
    public string? Deadline { get; init; }
    public int? AgendaId { get; init; }
}

public sealed record MeetingIssue
{
    public required string Description { get; init; }
    public int? AgendaId { get; init; }
}

/// <summary>
/// What the application knows about the recording besides the transcript: the record title and
/// the persons bound to its speakers. Passed to extraction and verification as "metadata";
/// the prompts treat it as a source of facts on a par with the transcript.
/// </summary>
public sealed record MeetingMetadata
{
    public string? Title { get; init; }
    public IReadOnlyList<MeetingParticipant> Participants { get; init; } = [];
}

/// <summary>Intermediate facts, final Romanian minutes and their verification against the original transcript.</summary>
public sealed record MeetingMinutesResult(MeetingFacts Facts, string MinutesMarkdown, MinutesVerification Verification);
