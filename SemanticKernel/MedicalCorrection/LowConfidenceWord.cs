namespace SemanticKernel.MedicalCorrection
{
    /// <summary>
    /// Слово, распознанное неуверенно. <see cref="At"/> - смещение в символах от начала текста,
    /// которому слово принадлежит; оно пересчитывается на каждой склейке текстов.
    /// Тип лежит рядом с <see cref="Segment"/>, чтобы оба проекта видели его без преобразований.
    /// </summary>
    public record LowConfidenceWord(int At, string Word, double P);
}
