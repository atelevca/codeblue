using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SemanticKernel.MedicalCorrection
{
    /// <summary>
    /// A reference list (<c>term | term | meaning</c> per line, <c>#</c> = comment/heading). The whole file is far too
    /// big for the model context, so each request gets only the lines that look related to its text.
    /// </summary>
    public partial class Glossary
    {
        // Words are compared by their first letters, without case and diacritics, so a garbled ending
        // ("miocardica", "аторва") still finds the term.
        private const int KeyLength = 5;
        private const int MinWordLength = 4;

        private readonly List<(string Line, HashSet<string> Keys)> _entries;
        private readonly Dictionary<string, int> _linesPerKey;

        private Glossary(string heading, List<(string Line, HashSet<string> Keys)> entries)
        {
            Heading = heading;
            _entries = entries;
            _linesPerKey = entries
                .SelectMany(e => e.Keys)
                .GroupBy(k => k)
                .ToDictionary(g => g.Key, g => g.Count());
        }

        public string Heading { get; }

        public static Glossary Load(string path, string heading)
        {
            var entries = File.ReadLines(path)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith('#') && l.Contains('|'))
                .Select(l => (l, Keys(SearchableColumns(l), withPairs: false)))
                .ToList();
            return new Glossary(heading, entries);
        }

        /// <summary>
        /// Lines sharing words with <paramref name="texts"/>, best first by matched words (a word found in many lines,
        /// like "acut", counts less), up to <paramref name="maxCharacters"/>; returned in file order.
        /// </summary>
        public IReadOnlyList<string> Select(IEnumerable<string> texts, int maxCharacters)
        {
            var textKeys = new HashSet<string>();
            foreach (var text in texts)
            {
                textKeys.UnionWith(Keys(text, withPairs: true));
            }

            var ranked = _entries
                .Select((e, index) => (e.Line, Index: index,
                    Score: e.Keys.Where(textKeys.Contains).Sum(k => 1.0 / _linesPerKey[k])))
                .Where(e => e.Score > 0)
                .OrderByDescending(e => e.Score);

            var selected = new List<(string Line, int Index)>();
            var characters = 0;
            foreach (var entry in ranked)
            {
                if (characters + entry.Line.Length + 1 > maxCharacters)
                {
                    continue;
                }
                selected.Add((entry.Line, entry.Index));
                characters += entry.Line.Length + 1;
            }
            return selected.OrderBy(e => e.Index).Select(e => e.Line).ToList();
        }

        // The last column is English / an explanation ("... | lung cancer"): matching it would pull in lines
        // for Romanian words like "lung", so only the spoken-form columns are searched.
        private static string SearchableColumns(string line)
        {
            var columns = line.Split('|');
            return columns.Length >= 3 ? string.Join(' ', columns[..^1]) : line;
        }

        // Word prefixes; for transcript text also of adjacent words glued together, because ASR splits
        // terms ("mio cardic", "аторва статин").
        private static HashSet<string> Keys(string text, bool withPairs)
        {
            var words = Words().Matches(Normalize(text)).Select(m => m.Value).ToList();
            var keys = new HashSet<string>();
            for (var i = 0; i < words.Count; i++)
            {
                AddKey(keys, words[i]);
                if (withPairs && i + 1 < words.Count)
                {
                    AddKey(keys, words[i] + words[i + 1]);
                }
            }
            return keys;
        }

        private static void AddKey(HashSet<string> keys, string word)
        {
            if (word.Length >= MinWordLength)
            {
                keys.Add(word[..Math.Min(KeyLength, word.Length)]);
            }
        }

        // Lowercase without diacritics: ă/â/î/ș/ț -> a/a/i/s/t, ё -> е, й -> и (both sides alike).
        private static string Normalize(string text)
        {
            var builder = new StringBuilder(text.Length);
            foreach (var c in text.ToLowerInvariant().Normalize(NormalizationForm.FormD))
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                {
                    builder.Append(c);
                }
            }
            return builder.ToString();
        }

        [GeneratedRegex(@"\p{L}+")]
        private static partial Regex Words();
    }
}
