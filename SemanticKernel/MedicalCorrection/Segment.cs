namespace SemanticKernel.MedicalCorrection
{
    /// <summary>A speaker-attributed piece of transcript. Only <see cref="Text"/> may be changed by the correction.</summary>
    public record Segment(int Id, string Speaker, TimeSpan Start, TimeSpan End, string Text)
    {
        /// <summary>Неуверенно распознанные слова со смещениями внутри <see cref="Text"/>.</summary>
        public IReadOnlyList<LowConfidenceWord> LowConfidence { get; init; } = [];
    }
}
