namespace SemanticKernel.Minutes;

public interface IMeetingMinutesGenerator
{
    /// <summary>Extracts facts, generates Romanian minutes, then verifies the final document against the transcript.</summary>
    Task<MeetingMinutesResult> GenerateAsync(string transcript, CancellationToken ct = default);

    Task<MeetingFacts> ExtractFactsAsync(string transcript, CancellationToken ct = default);

    Task<string> GenerateMinutesAsync(MeetingFacts facts, CancellationToken ct = default);

    Task<MinutesVerification> VerifyMinutesAsync(string transcript, string minutesMarkdown, CancellationToken ct = default);
}
