using System.Diagnostics;

namespace HealthTech.Transcription
{
    public interface IProcessedAudioTranscriptionService
    {
        /// <summary>
        /// Takes the first audio file (by name) from the processed directory, splits it into speech chunks (VAD),
        /// runs speech recognition on each chunk and saves the transcript as JSON. No speaker diarization.
        /// The source file is left untouched.
        /// </summary>
        Task<TranscriptionResult> TranscribeFirstProcessedAsync(CancellationToken cancellationToken = default);
    }

    public class ProcessedAudioTranscriptionService : IProcessedAudioTranscriptionService
    {
        private const int MinChunkSamples = AudioSampleReader.TargetSampleRate * 3 / 2;

        private readonly IProcessedAudioFiles _files;
        private readonly IAudioSampleReader _sampleReader;
        private readonly IVoiceActivityService _voiceActivity;
        private readonly ISpeechRecognitionService _speechRecognition;
        private readonly ILogger<ProcessedAudioTranscriptionService> _logger;

        public ProcessedAudioTranscriptionService(
            IProcessedAudioFiles files,
            IAudioSampleReader sampleReader,
            IVoiceActivityService voiceActivity,
            ISpeechRecognitionService speechRecognition,
            ILogger<ProcessedAudioTranscriptionService> logger)
        {
            _files = files;
            _sampleReader = sampleReader;
            _voiceActivity = voiceActivity;
            _speechRecognition = speechRecognition;
            _logger = logger;
        }

        public async Task<TranscriptionResult> TranscribeFirstProcessedAsync(CancellationToken cancellationToken = default)
        {
            const int sampleRate = AudioSampleReader.TargetSampleRate;

            var path = _files.FindFirst();
            var fileName = Path.GetFileName(path);

            var samples = await _sampleReader.ReadMono16kAsync(path, cancellationToken);
            var durationSeconds = Math.Round((double)samples.Length / sampleRate, 2);
            _logger.LogInformation("Transcribing {FileName}, duration {DurationSeconds:F1} s", fileName, durationSeconds);

            var stopwatch = Stopwatch.StartNew();
            var chunks = _voiceActivity.DetectChunks(samples);

            // Each chunk is transcribed on its own; Whisper's timestamps are shifted back to file time.
            var segments = new List<TranscriptSegment>();
            foreach (var chunk in chunks)
            {
                var from = (int)(chunk.Start * sampleRate);
                var to = Math.Min(samples.Length, (int)Math.Ceiling(chunk.End * sampleRate));
                var chunkSamples = samples[from..to];
                if (chunkSamples.Length < MinChunkSamples)
                {
                    // whisper.cpp returns nothing for input under 1 s; pad short utterances with silence.
                    Array.Resize(ref chunkSamples, MinChunkSamples);
                }
                var chunkSegments = await _speechRecognition.TranscribeAsync(chunkSamples, cancellationToken);

                segments.AddRange(chunkSegments.Select(s => new TranscriptSegment(
                    Math.Round(chunk.Start + s.Start, 2),
                    Math.Round(Math.Min(chunk.Start + s.End, chunk.End), 2),
                    s.Text)));
            }

            var transcriptionMs = stopwatch.ElapsedMilliseconds;
            _logger.LogInformation("Transcription of {FileName} took {ElapsedMs} ms: {ChunkCount} chunk(s), {SegmentCount} segment(s)",
                fileName, transcriptionMs, chunks.Count, segments.Count);

            var result = new TranscriptionResult(fileName, durationSeconds, segments, transcriptionMs);
            await _files.SaveJsonAsync(result, path, ".json", cancellationToken);
            return result;
        }
    }
}
