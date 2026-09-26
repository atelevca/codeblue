namespace HealthTech.Transcription
{
    /// <summary>A piece of recognized text. Times are in seconds from the start of the audio.</summary>
    public record TranscriptSegment(double Start, double End, string Text);

    /// <summary>A time range attributed to one speaker. Times are in seconds from the start of the audio.</summary>
    public record SpeakerSegment(double Start, double End, string Speaker);

    public record TranscriptionResult(
        string FileName,
        double DurationSeconds,
        IReadOnlyList<TranscriptSegment> Segments,
        long TranscriptionMs);

    /// <summary>A speaker turn. Start/End are seconds; StartTime/EndTime are the same values as "mm:ss".</summary>
    public record DiarizationSegment(double Start, double End, string StartTime, string EndTime, string Speaker);

    public record DiarizationResult(
        string FileName,
        double DurationSeconds,
        IReadOnlyList<string> Speakers,
        IReadOnlyList<DiarizationSegment> Segments,
        long DiarizationMs);

    /// <summary>A speaker turn with its text: consecutive transcript segments of one speaker merged together.</summary>
    public record SpeakerTranscriptTurn(double Start, double End, string StartTime, string EndTime, string Speaker, string Text);

    /// <summary>
    /// Transcript aligned with diarization. <see cref="Text"/> is the whole dialogue as
    /// "Speaker 1:" / text / blank line / "Speaker 2:" / text ..., for reading.
    /// </summary>
    public record SpeakerTranscriptResult(
        string FileName,
        double DurationSeconds,
        IReadOnlyList<string> Speakers,
        IReadOnlyList<SpeakerTranscriptTurn> Turns,
        string Text,
        long TranscriptionMs,
        long DiarizationMs);

    internal static class TranscriptTime
    {
        // mm:ss; minutes keep counting past 59 instead of wrapping into hours.
        public static string Format(double seconds)
        {
            var time = TimeSpan.FromSeconds(seconds);
            return $"{(int)time.TotalMinutes:00}:{time.Seconds:00}";
        }
    }
}
