namespace SemanticKernel.MedicalCorrection
{
    /// <summary>
    /// Всё, что коррекции нужно от профиля записи: системный промпт и разобранные глоссарии
    /// с их заголовками. Загружается один раз при старте.
    /// </summary>
    public record RecordProfileContent(string SystemPrompt, IReadOnlyList<Glossary> Glossaries);
}
