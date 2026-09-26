namespace SemanticKernel.MedicalCorrection
{
    /// <summary>
    /// The ICD-10 export (<c>diagnostics.csv</c> + <c>subclasses.csv</c> + <c>classes.csv</c>, <c>;</c>-separated with a
    /// header row) turned into an ordinary <see cref="Glossary"/>: one line per diagnosis,
    /// <c>code | name | subclass</c>. Only the code and the name are searched; the subclass is context for the model,
    /// exactly like the English column of the hand-written glossaries. The class name is not appended: it would make
    /// lines two to three times longer and halve the number of diagnoses that fit the per-request budget, while a
    /// subclass ("Boli infectioase intestinale") already says what the class does. <c>classes.csv</c> is read only to
    /// verify that every subclass points to a known class.
    /// </summary>
    public static class Icd10Glossary
    {
        private const char Separator = ';';

        /// <param name="diagnosticsPath">Path of <c>diagnostics.csv</c>; the two other files are read from the same folder.</param>
        public static Glossary Load(string diagnosticsPath, string heading)
        {
            var directory = Path.GetDirectoryName(diagnosticsPath) ?? "";
            var classes = ReadRows(Path.Combine(directory, "classes.csv"), 2)
                .Select(r => r[0])
                .ToHashSet();
            var subclasses = ReadRows(Path.Combine(directory, "subclasses.csv"), 3)
                .Where(r => classes.Contains(r[1]))
                .ToDictionary(r => r[0], r => r[2]);

            var lines = ReadRows(diagnosticsPath, 3)
                .Select(r => subclasses.TryGetValue(r[1], out var subclass)
                    ? $"{r[0]} | {r[2]} | {subclass}"
                    : $"{r[0]} | {r[2]}")
                .ToList();
            return Glossary.FromLines(lines, heading);
        }

        // Header row skipped, short/empty rows ignored; File.ReadLines strips the UTF-8 BOM itself.
        private static IEnumerable<string[]> ReadRows(string path, int columns)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"ICD-10 glossary file not found: '{path}'.", path);
            }
            return File.ReadLines(path)
                .Skip(1)
                .Select(l => l.Split(Separator, columns).Select(c => c.Trim()).ToArray())
                .Where(r => r.Length == columns && r.All(c => c.Length > 0));
        }
    }
}
