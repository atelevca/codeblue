namespace SemanticKernel.MedicalCorrection
{
    public interface IMedicalTermCorrector
    {
        /// <summary>
        /// Fixes likely ASR errors in medical terms. The result has the same count, order, ids, speakers and
        /// timestamps as <paramref name="segments"/>; only <see cref="Segment.Text"/> may differ. On any doubt
        /// or failure the original text is kept.
        /// </summary>
        /// <param name="profile">System prompt and glossaries of the selected record type.</param>
        /// <param name="log">Receives every accepted or rejected change, e.g. for <see cref="CorrectionLog.ToMarkdown"/>.</param>
        Task<IReadOnlyList<Segment>> CorrectAsync(
            IReadOnlyList<Segment> segments, RecordProfileContent profile, CorrectionLog log,
            CancellationToken ct = default);
    }
}
