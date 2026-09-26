namespace SemanticKernel.Minutes;

public interface IMeetingMinutesGenerator
{
    /// <summary>Extracts facts, renders the Romanian minutes from them, then verifies the document against the transcript.</summary>
    Task<MeetingMinutesResult> GenerateAsync(string transcript, MeetingMetadata? metadata = null, CancellationToken ct = default);

    /// <param name="metadata">Record title and bound participants, when known; a source of facts next to the transcript.</param>
    Task<MeetingFacts> ExtractFactsAsync(string transcript, MeetingMetadata? metadata = null, CancellationToken ct = default);

    /// <summary>
    /// The minutes Markdown, rendered entirely in code from the facts: no model call. Every heading of the
    /// template is present in order; the tables carry the facts verbatim.
    /// </summary>
    string RenderMinutes(MeetingFacts facts);

    /// <param name="metadata">The same metadata the facts were extracted with, so a name taken from it is not reported as unsupported.</param>
    Task<MinutesVerification> VerifyMinutesAsync(string transcript, string minutesMarkdown, MeetingMetadata? metadata = null, CancellationToken ct = default);
}
