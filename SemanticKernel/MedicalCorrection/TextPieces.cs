using System.Text.RegularExpressions;

namespace SemanticKernel.MedicalCorrection
{
    /// <summary>
    /// Cuts a long text into pieces of whole sentences so each piece is corrected and validated on its own:
    /// a rejected change then costs one sentence, not a whole speaker turn. The pieces cover the text exactly,
    /// so gluing them back gives the original when nothing changed.
    /// </summary>
    public static partial class TextPieces
    {
        public static IReadOnlyList<string> Split(string text, int maxLength)
        {
            if (text.Length <= maxLength)
            {
                return [text];
            }

            var pieces = new List<string>();
            var current = "";
            foreach (var unit in SentenceEnds().Split(text).SelectMany(s => SplitLongSentence(s, maxLength)))
            {
                if (current.Length > 0 && current.Length + unit.Length > maxLength)
                {
                    pieces.Add(current);
                    current = "";
                }
                current += unit;
            }
            pieces.Add(current);
            return pieces;
        }

        // A sentence longer than the limit is cut after the last whitespace that fits (a single huge word stays whole).
        private static IEnumerable<string> SplitLongSentence(string sentence, int maxLength)
        {
            while (sentence.Length > maxLength)
            {
                var cut = sentence.LastIndexOf(' ', maxLength - 1);
                if (cut <= 0)
                {
                    break;
                }
                yield return sentence[..(cut + 1)];
                sentence = sentence[(cut + 1)..];
            }
            yield return sentence;
        }

        // Splits after sentence punctuation + its whitespace; the whitespace stays with the sentence before it.
        [GeneratedRegex(@"(?<=[.!?…]\s+)(?=\S)")]
        private static partial Regex SentenceEnds();
    }
}
