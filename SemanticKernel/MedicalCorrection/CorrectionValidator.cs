using System.Text.RegularExpressions;

namespace SemanticKernel.MedicalCorrection
{
    public record ValidationResult(bool Accepted, string Reason);

    /// <summary>
    /// Guards against the model doing more than fixing misheard terms. A rejected correction means the original
    /// text is kept.
    /// </summary>
    public static partial class CorrectionValidator
    {
        /// <summary>Max change of the Cyrillic share of letters (0..1) before it counts as translation.</summary>
        public const double MaxCyrillicRatioChange = 0.15;

        /// <summary>
        /// Letters moved between scripts that count as a translated word (Cyrillic down and Latin up by at least
        /// this much, or the reverse). One-letter tweaks in both scripts still pass.
        /// </summary>
        public const int MinScriptShiftLetters = 2;

        /// <summary>Max Levenshtein distance as a share of the original length.</summary>
        public const double MaxEditDistanceRatio = 0.25;

        /// <summary>Allowed corrected/original length range.</summary>
        public const double MinLengthRatio = 0.70;
        public const double MaxLengthRatio = 1.30;

        public static ValidationResult Validate(string original, string? corrected)
        {
            if (string.IsNullOrWhiteSpace(corrected))
            {
                return new ValidationResult(false, "empty text");
            }

            if (corrected == original)
            {
                return new ValidationResult(true, "unchanged");
            }

            if (!Numbers(original).SequenceEqual(Numbers(corrected)))
            {
                return new ValidationResult(false, "numbers changed");
            }

            // The ratio alone misses a single translated word in a long segment: a translation moves letters from one
            // script to the other, while a real fix ("аторва статин" -> "аторвастатин") stays within one script.
            // The order of Latin/Cyrillic word runs must stay the same too.
            if (Math.Abs(CyrillicRatio(original) - CyrillicRatio(corrected)) > MaxCyrillicRatioChange
                || ShiftsScript(original, corrected)
                || !ScriptRuns(original).SequenceEqual(ScriptRuns(corrected)))
            {
                return new ValidationResult(false, "possible translation");
            }

            // Checked before the edit distance: a >30% length change always exceeds it and deserves its own reason.
            var lengthRatio = (double)corrected.Length / Math.Max(original.Length, 1);
            if (lengthRatio is < MinLengthRatio or > MaxLengthRatio)
            {
                return new ValidationResult(false, "added/removed content");
            }

            if (Levenshtein(original, corrected) > MaxEditDistanceRatio * original.Length)
            {
                return new ValidationResult(false, "rewritten");
            }

            return new ValidationResult(true, "medical term corrected");
        }

        private static IEnumerable<string> Numbers(string text) => DigitGroups().Matches(text).Select(m => m.Value);

        private static double CyrillicRatio(string text)
        {
            int letters = 0, cyrillic = 0;
            foreach (var c in text)
            {
                if (!char.IsLetter(c))
                {
                    continue;
                }
                letters++;
                if (IsCyrillic(c))
                {
                    cyrillic++;
                }
            }
            return letters == 0 ? 0 : (double)cyrillic / letters;
        }

        // Cyrillic letters went down and Latin up (or the reverse), both by at least MinScriptShiftLetters.
        private static bool ShiftsScript(string original, string corrected)
        {
            var (cyrillicBefore, latinBefore) = CountScripts(original);
            var (cyrillicAfter, latinAfter) = CountScripts(corrected);
            var cyrillicChange = cyrillicAfter - cyrillicBefore;
            var latinChange = latinAfter - latinBefore;
            return Math.Abs(cyrillicChange) >= MinScriptShiftLetters && Math.Abs(latinChange) >= MinScriptShiftLetters
                   && Math.Sign(cyrillicChange) != Math.Sign(latinChange);
        }

        private static (int Cyrillic, int Latin) CountScripts(string text)
        {
            int cyrillic = 0, latin = 0;
            foreach (var c in text.Where(char.IsLetter))
            {
                if (IsCyrillic(c))
                {
                    cyrillic++;
                }
                else
                {
                    latin++;
                }
            }
            return (cyrillic, latin);
        }

        // Script of each word (true = Cyrillic), consecutive words of the same script collapsed into one run.
        private static List<bool> ScriptRuns(string text)
        {
            var runs = new List<bool>();
            foreach (Match word in Words().Matches(text))
            {
                var cyrillic = word.Value.Any(IsCyrillic);
                if (runs.Count == 0 || runs[^1] != cyrillic)
                {
                    runs.Add(cyrillic);
                }
            }
            return runs;
        }

        private static bool IsCyrillic(char c) => c is >= 'Ѐ' and <= 'ӿ';

        private static int Levenshtein(string a, string b)
        {
            var previous = new int[b.Length + 1];
            var current = new int[b.Length + 1];
            for (var j = 0; j <= b.Length; j++)
            {
                previous[j] = j;
            }

            for (var i = 1; i <= a.Length; i++)
            {
                current[0] = i;
                for (var j = 1; j <= b.Length; j++)
                {
                    var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
                }
                (previous, current) = (current, previous);
            }
            return previous[b.Length];
        }

        [GeneratedRegex(@"\d+")]
        private static partial Regex DigitGroups();

        [GeneratedRegex(@"\p{L}+")]
        private static partial Regex Words();
    }
}
