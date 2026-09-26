namespace HealthTech.Audio
{
    public record ProcessedAudio(
        string OriginalPath,
        string WavPath,
        string OriginalFormat,
        string? Codec,
        int? SampleRate,
        int? Channels,
        bool Converted);
}
