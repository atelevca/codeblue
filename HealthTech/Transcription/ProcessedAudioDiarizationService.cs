using System.Diagnostics;

namespace HealthTech.Transcription
{
    public interface IProcessedAudioDiarizationService
    {
        /// <summary>
        /// Takes the first audio file (by name) from the processed directory, runs speaker diarization only
        /// (no speech recognition) and saves the speaker turns as <c>&lt;name&gt;.diarization.json</c>.
        /// </summary>
        Task<DiarizationResult> DiarizeFirstProcessedAsync(CancellationToken cancellationToken = default);
    }

    public class ProcessedAudioDiarizationService : IProcessedAudioDiarizationService
    {
        private readonly IProcessedAudioFiles _files;
        private readonly IAudioSampleReader _sampleReader;
        private readonly ISpeakerDiarizationService _diarization;
        private readonly ILogger<ProcessedAudioDiarizationService> _logger;

        public ProcessedAudioDiarizationService(
            IProcessedAudioFiles files,
            IAudioSampleReader sampleReader,
            ISpeakerDiarizationService diarization,
            ILogger<ProcessedAudioDiarizationService> logger)
        {
            _files = files;
            _sampleReader = sampleReader;
            _diarization = diarization;
            _logger = logger;
        }

        public async Task<DiarizationResult> DiarizeFirstProcessedAsync(CancellationToken cancellationToken = default)
        {
            var path = _files.FindFirst();
            var fileName = Path.GetFileName(path);

            var samples = await _sampleReader.ReadMono16kAsync(path, cancellationToken);
            var durationSeconds = Math.Round((double)samples.Length / AudioSampleReader.TargetSampleRate, 2);
            _logger.LogInformation("Diarizing {FileName}, duration {DurationSeconds:F1} s", fileName, durationSeconds);

            var stopwatch = Stopwatch.StartNew();
            var speakerTurns = await _diarization.DiarizeAsync(samples, cancellationToken);
            var diarizationMs = stopwatch.ElapsedMilliseconds;

            // Labels are assigned in order of first appearance, so this is Speaker 1, Speaker 2, ...
            var speakers = speakerTurns.Select(s => s.Speaker).Distinct().ToList();
            _logger.LogInformation("Diarization of {FileName} took {ElapsedMs} ms: {SpeakerCount} speaker(s), {TurnCount} turn(s)",
                fileName, diarizationMs, speakers.Count, speakerTurns.Count);

            var segments = speakerTurns
                .Select(s => new DiarizationSegment(
                    Math.Round(s.Start, 2), Math.Round(s.End, 2), TranscriptTime.Format(s.Start), TranscriptTime.Format(s.End), s.Speaker))
                .ToList();
            var result = new DiarizationResult(fileName, durationSeconds, speakers, segments, diarizationMs);

            await _files.SaveJsonAsync(result, path, ".diarization.json", cancellationToken);
            return result;
        }
    }
}
