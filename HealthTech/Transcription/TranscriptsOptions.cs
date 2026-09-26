namespace HealthTech.Transcription
{
    public class TranscriptsOptions
    {
        public const string SectionName = "Transcripts";

        // Where transcript JSON files are written. Relative paths are resolved against the content root.
        public string OutputFolder { get; set; } = "transcripts";
    }
}
