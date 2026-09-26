using System.Text;

namespace HealthTech.Transcription
{
    public interface IProcessedAudioSpeakerTranscriptService
    {
        /// <summary>
        /// Transcribes and diarizes the first processed audio file, assigns each transcript segment to a speaker
        /// and saves the dialogue as <c>&lt;name&gt;.speakers.json</c>. Alignment is per Whisper segment, not per word.
        /// </summary>
        Task<SpeakerTranscriptResult> TranscribeWithSpeakersAsync(CancellationToken cancellationToken = default);
    }

    public class ProcessedAudioSpeakerTranscriptService : IProcessedAudioSpeakerTranscriptService
    {
        private readonly IProcessedAudioFiles _files;
        private readonly IProcessedAudioTranscriptionService _transcription;
        private readonly IProcessedAudioDiarizationService _diarization;
        private readonly ILogger<ProcessedAudioSpeakerTranscriptService> _logger;

        public ProcessedAudioSpeakerTranscriptService(
            IProcessedAudioFiles files,
            IProcessedAudioTranscriptionService transcription,
            IProcessedAudioDiarizationService diarization,
            ILogger<ProcessedAudioSpeakerTranscriptService> logger)
        {
            _files = files;
            _transcription = transcription;
            _diarization = diarization;
            _logger = logger;
        }

        public async Task<SpeakerTranscriptResult> TranscribeWithSpeakersAsync(CancellationToken cancellationToken = default)
        {
            var path = _files.FindFirst();

            // Both steps also save their own JSON (<name>.json, <name>.diarization.json).
            var transcript = await _transcription.TranscribeFirstProcessedAsync(cancellationToken);
            var diarization = await _diarization.DiarizeFirstProcessedAsync(cancellationToken);

            var turns = Align(transcript.Segments, diarization.Segments);
            var speakers = turns.Select(t => t.Speaker).Distinct().ToList();
            _logger.LogInformation("Aligned {SegmentCount} segment(s) of {FileName} into {TurnCount} turn(s) of {SpeakerCount} speaker(s)",
                transcript.Segments.Count, transcript.FileName, turns.Count, speakers.Count);

            var result = new SpeakerTranscriptResult(
                transcript.FileName, transcript.DurationSeconds, speakers, turns, FormatDialogue(turns),
                transcript.TranscriptionMs, diarization.DiarizationMs);

            await _files.SaveJsonAsync(result, path, ".speakers.json", cancellationToken);
            return result;
        }

        // Each segment goes to the speaker whose turns overlap it the most; consecutive segments
        // of the same speaker are merged into one turn.
        private static List<SpeakerTranscriptTurn> Align(
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
                    turns[^1] = last with
                    {
                        End = segment.End,
                        EndTime = TranscriptTime.Format(segment.End),
                        Text = last.Text + " " + text
                    };
                }
                else
                {
                    turns.Add(new SpeakerTranscriptTurn(segment.Start, segment.End,
                        TranscriptTime.Format(segment.Start), TranscriptTime.Format(segment.End), speaker, text));
                }
            }
            return turns;
        }

        private const string UnknownSpeaker = "Unknown";

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

        private static string FormatDialogue(IReadOnlyList<SpeakerTranscriptTurn> turns)
        {
            var builder = new StringBuilder();
            foreach (var turn in turns)
            {
                if (builder.Length > 0)
                {
                    builder.Append("\n\n");
                }
                builder.Append(turn.Speaker).Append(":\n").Append(turn.Text);
            }
            return builder.ToString();
        }
    }
}
