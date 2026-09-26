using System.Diagnostics;

namespace HealthTech.Transcription
{
    public interface IProcessedAudioTranscriptionService
    {
        /// <summary>
        /// Runs speech recognition over the given speech chunks of <paramref name="wavPath"/> (16 kHz mono)
        /// and saves the transcript as <c>&lt;name&gt;.json</c> in <paramref name="outputDirectory"/>.
        /// No speaker diarization. The source file is left untouched.
        /// </summary>
        /// <param name="whisperPrompt">The record profile's steering phrase; empty falls back to config.</param>
        /// <param name="progress">Reports finished chunks so the caller can move a progress bar inside the step.</param>
        Task<TranscriptionResult> TranscribeAsync(
            string wavPath, string outputDirectory, IReadOnlyList<SpeechChunk> chunks, string whisperPrompt,
            IProgress<UnitProgress>? progress = null, CancellationToken cancellationToken = default);
    }

    public class ProcessedAudioTranscriptionService : IProcessedAudioTranscriptionService
    {
        private const int MinChunkSamples = AudioSampleReader.TargetSampleRate * 3 / 2;

        private readonly IProcessedAudioFiles _files;
        private readonly IAudioSampleReader _sampleReader;
        private readonly ISpeechRecognitionService _speechRecognition;
        private readonly ILogger<ProcessedAudioTranscriptionService> _logger;

        public ProcessedAudioTranscriptionService(
            IProcessedAudioFiles files,
            IAudioSampleReader sampleReader,
            ISpeechRecognitionService speechRecognition,
            ILogger<ProcessedAudioTranscriptionService> logger)
        {
            _files = files;
            _sampleReader = sampleReader;
            _speechRecognition = speechRecognition;
            _logger = logger;
        }

        public async Task<TranscriptionResult> TranscribeAsync(
            string wavPath, string outputDirectory, IReadOnlyList<SpeechChunk> chunks, string whisperPrompt,
            IProgress<UnitProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            const int sampleRate = AudioSampleReader.TargetSampleRate;

            // Имя записи, а не производного <имя>.16k.wav, который читают модели.
            var fileName = ProcessedAudioFiles.BaseName(wavPath) + Path.GetExtension(wavPath);
            var samples = await _sampleReader.ReadMono16kAsync(wavPath, cancellationToken);
            var durationSeconds = Math.Round((double)samples.Length / sampleRate, 2);
            _logger.LogInformation("Transcribing {FileName}, duration {DurationSeconds:F1} s", fileName, durationSeconds);

            var stopwatch = Stopwatch.StartNew();

            // Each chunk is transcribed on its own; Whisper's timestamps are shifted back to file time.
            var segments = new List<TranscriptSegment>();
            var done = 0;
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
                var chunkSegments = await _speechRecognition.TranscribeAsync(chunkSamples, whisperPrompt, cancellationToken);

                // "with", а не новый TranscriptSegment: пересборка потеряла бы список неуверенно
                // распознанных слов. Смещения в нём остаются верными - текст сегмента не меняется,
                // сдвигаются только временные метки.
                segments.AddRange(chunkSegments.Select(s => s with
                {
                    Start = Math.Round(chunk.Start + s.Start, 2),
                    End = Math.Round(Math.Min(chunk.Start + s.End, chunk.End), 2)
                }));

                done++;
                progress?.Report(new UnitProgress(done, chunks.Count));
            }

            var transcriptionMs = stopwatch.ElapsedMilliseconds;
            _logger.LogInformation("Transcription of {FileName} took {ElapsedMs} ms: {ChunkCount} chunk(s), {SegmentCount} segment(s)",
                fileName, transcriptionMs, chunks.Count, segments.Count);

            var result = new TranscriptionResult(fileName, durationSeconds, segments, transcriptionMs);
            await _files.SaveJsonAsync(result, outputDirectory, wavPath, ".json", cancellationToken);
            return result;
        }
    }
}
