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

    /// <summary>
    /// Что известно о файле сразу после загрузки: контейнер и длительность.
    /// Длительность может отсутствовать - не каждый контейнер её пишет.
    /// </summary>
    public record AudioFileInfo(string Format, double? DurationSeconds);
}
