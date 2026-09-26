using SemanticKernel.MedicalCorrection;

namespace HealthTech.Transcription
{
    public interface ISpeakerAlignmentService
    {
        /// <summary>
        /// Assigns each transcript segment to the speaker whose turns overlap it most and merges
        /// consecutive segments of the same speaker into turns. Alignment is per segment, not per word,
        /// so a long segment spanning a speaker change goes entirely to the dominant speaker.
        /// </summary>
        List<SpeakerTranscriptTurn> Align(
            IReadOnlyList<TranscriptSegment> segments, IReadOnlyList<DiarizationSegment> speakerTurns);
    }

    public class SpeakerAlignmentService : ISpeakerAlignmentService
    {
        private const string UnknownSpeaker = "Unknown";

        public List<SpeakerTranscriptTurn> Align(
            IReadOnlyList<TranscriptSegment> segments, IReadOnlyList<DiarizationSegment> speakerTurns)
        {
            var turns = new List<SpeakerTranscriptTurn>();
            foreach (var segment in segments)
            {
                var text = segment.Text.Trim();
                if (text.Length == 0)
                {
                    continue;
                }

                var speaker = FindSpeaker(segment, speakerTurns);
                if (turns.Count > 0 && turns[^1].Speaker == speaker)
                {
                    var last = turns[^1];

                    // Тексты склеиваются через один пробел, поэтому слова присоединяемого
                    // сегмента съезжают ровно на длину накопленного текста плюс этот пробел.
                    var shift = last.Text.Length + 1;
                    turns[^1] = last with
                    {
                        End = segment.End,
                        EndTime = TranscriptTime.Format(segment.End),
                        Text = last.Text + " " + text,
                        LowConfidence = [.. last.LowConfidence, .. Shift(segment, text, shift)]
                    };
                }
                else
                {
                    turns.Add(new SpeakerTranscriptTurn(segment.Start, segment.End,
                        TranscriptTime.Format(segment.Start), TranscriptTime.Format(segment.End), speaker, text)
                    {
                        LowConfidence = Shift(segment, text, 0)
                    });
                }
            }
            return turns;
        }

        /// <summary>
        /// Переносит слова сегмента в систему координат реплики. Смещения считались от
        /// <see cref="TranscriptSegment.Text"/>, а в реплику попадает его тримленный вид,
        /// поэтому вычитается длина срезанного слева. Слово, съехавшее за границы, отбрасывается:
        /// лучше потерять пометку, чем указать не туда.
        /// </summary>
        private static List<LowConfidenceWord> Shift(TranscriptSegment segment, string trimmedText, int shift)
        {
            if (segment.LowConfidence.Count == 0)
            {
                return [];
            }

            var trimmedLeft = segment.Text.Length - segment.Text.TrimStart().Length;
            var result = new List<LowConfidenceWord>(segment.LowConfidence.Count);
            foreach (var word in segment.LowConfidence)
            {
                var at = word.At - trimmedLeft + shift;
                if (at < shift || at + word.Word.Length > shift + trimmedText.Length)
                {
                    continue;
                }
                result.Add(word with { At = at });
            }
            return result;
        }

        private static string FindSpeaker(TranscriptSegment segment, IReadOnlyList<DiarizationSegment> speakerTurns)
        {
            if (speakerTurns.Count == 0)
            {
                return UnknownSpeaker;
            }

            var byOverlap = speakerTurns
                .GroupBy(t => t.Speaker)
                .Select(g => (Speaker: g.Key,
                    Overlap: g.Sum(t => Math.Max(0, Math.Min(segment.End, t.End) - Math.Max(segment.Start, t.Start)))))
                .MaxBy(x => x.Overlap);
            if (byOverlap.Overlap > 0)
            {
                return byOverlap.Speaker;
            }

            // Segment falls into a gap between turns (diarization skipped it): take the closest turn.
            return speakerTurns
                .MinBy(t => Math.Max(t.Start - segment.End, segment.Start - t.End))!
                .Speaker;
        }
    }
}
