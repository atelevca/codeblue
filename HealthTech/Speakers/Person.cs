namespace HealthTech.Speakers
{
    /// <summary>Врач из справочника. Заводится руками правкой schema.sql или самой базы.</summary>
    public record Person(Guid Id, string FullName, string? Specialty);

    /// <summary>Сопоставление метки диаризации ("Speaker 1") с врачом.</summary>
    public record SpeakerBinding(string Label, Guid PersonId);

    /// <summary>Реплика с подставленным именем. Speaker - исходная метка, DisplayName - что показать.</summary>
    public record NamedTurn(string Speaker, string DisplayName, string StartTime, string EndTime, string Text);

    /// <summary>Диалог задания с подставленными именами.</summary>
    public record NamedTranscript(IReadOnlyList<NamedTurn> Turns, string Text);
}
