namespace HealthTech.Audio
{
    public class AudioOptions
    {
        public const string SectionName = "Audio";

        // Relative paths are resolved against the application's content root.
        public string InputDirectory { get; set; } = "input";
        public string OutputDirectory { get; set; } = "processed";

        // Executable names (looked up on PATH) or full paths.
        public string FfmpegPath { get; set; } = "ffmpeg";
        public string FfprobePath { get; set; } = "ffprobe";
    }
}
