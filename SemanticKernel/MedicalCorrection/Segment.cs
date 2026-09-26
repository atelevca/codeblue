namespace SemanticKernel.MedicalCorrection
{
    /// <summary>A speaker-attributed piece of transcript. Only <see cref="Text"/> may be changed by the correction.</summary>
    public record Segment(int Id, string Speaker, TimeSpan Start, TimeSpan End, string Text);
}
