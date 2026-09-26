namespace SemanticKernel.MedicalCorrection
{
    /// <summary>Сколько батчей отправлено модели из скольких. Для полосы прогресса внутри шага.</summary>
    public record BatchProgress(int Done, int Total);
}
