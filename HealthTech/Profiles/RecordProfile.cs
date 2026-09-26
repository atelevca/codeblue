using SemanticKernel.MedicalCorrection;

namespace HealthTech.Profiles
{
    /// <summary>
    /// Тип записи. Определяет направляющую фразу для Whisper и содержимое LLM-коррекции.
    /// Выбирается пользователем при загрузке файла.
    /// </summary>
    public record RecordProfile(
        string Key,
        string DisplayName,
        string WhisperPrompt,
        RecordProfileContent Content);
}
