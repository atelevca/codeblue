namespace SemanticKernel.Minutes;

public interface IMeetingMinutesGenerator
{
    /// <summary>Extracts facts, generates Romanian minutes, then verifies the final document against the transcript.</summary>
    Task<MeetingMinutesResult> GenerateAsync(string transcript, MeetingMetadata? metadata = null, CancellationToken ct = default);

    /// <param name="metadata">Record title and bound participants, when known; a source of facts next to the transcript.</param>
    Task<MeetingFacts> ExtractFactsAsync(string transcript, MeetingMetadata? metadata = null, CancellationToken ct = default);

    Task<string> GenerateMinutesAsync(MeetingFacts facts, CancellationToken ct = default);

    /// <param name="metadata">The same metadata the facts were extracted with, so a name taken from it is not reported as unsupported.</param>
    Task<MinutesVerification> VerifyMinutesAsync(string transcript, string minutesMarkdown, MeetingMetadata? metadata = null, CancellationToken ct = default);
}
