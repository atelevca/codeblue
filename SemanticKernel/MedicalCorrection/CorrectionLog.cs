using System.Text;

namespace SemanticKernel.MedicalCorrection
{
    public record CorrectionLogEntry(int Id, string Speaker, string Original, string Corrected, bool Accepted, string Reason);

    /// <summary>Every change the model proposed, accepted or rejected, for doctors to review.</summary>
    public class CorrectionLog
    {
        private readonly List<CorrectionLogEntry> _entries = [];

        public IReadOnlyList<CorrectionLogEntry> Entries => _entries;

        public void Add(CorrectionLogEntry entry) => _entries.Add(entry);

        /// <summary>A Markdown diff table: Id | Speaker | Original | Corrected | Status | Reason.</summary>
        public string ToMarkdown()
        {
            var accepted = _entries.Count(e => e.Accepted);
            var builder = new StringBuilder()
                .AppendLine("# Medical term corrections")
                .AppendLine()
                .AppendLine($"Proposed changes: {_entries.Count}, accepted: {accepted}, rejected: {_entries.Count - accepted}.")
                .AppendLine();

            if (_entries.Count == 0)
            {
                return builder.AppendLine("No changes were proposed.").ToString();
            }

            builder
                .AppendLine("| Id | Speaker | Original | Corrected | Status | Reason |")
                .AppendLine("|---:|---|---|---|---|---|");
            foreach (var entry in _entries)
            {
                builder.AppendLine(
                    $"| {entry.Id} | {Cell(entry.Speaker)} | {Cell(entry.Original)} | {Cell(entry.Corrected)} | " +
                    $"{(entry.Accepted ? "✅ accepted" : "❌ rejected")} | {Cell(entry.Reason)} |");
            }
            return builder.ToString();
        }

        private static string Cell(string text) =>
            text.Replace("\\", "\\\\").Replace("|", "\\|").ReplaceLineEndings(" ");
    }
}
