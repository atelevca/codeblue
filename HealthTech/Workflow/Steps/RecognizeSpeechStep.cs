using HealthTech.Jobs;
using HealthTech.Profiles;
using HealthTech.Transcription;
using WorkflowCore.Interface;

namespace HealthTech.Workflow.Steps
{
    /// <summary>
    /// Распознавание речи (Whisper) и разделение по говорящим (sherpa-onnx) одним шагом, параллельно:
    /// они не зависят друг от друга, оба читают один и тот же 16 kHz WAV, а нагрузка у них разная -
    /// Whisper занимает GPU, диаризация CPU. Раньше шли двумя последовательными шагами, и время
    /// шага складывалось; теперь это максимум из двух.
    /// </summary>
    public class RecognizeSpeechStep : JobStep
    {
        private const int BandStart = 15;
        private const int BandEnd = 75;

        // Прогресс внутри полосы ведёт Whisper (он дольше); остаток полосы отдаётся ожиданию диаризации.
        private const int TranscriptionBandEnd = 70;

        private readonly IProcessedAudioTranscriptionService _transcription;
        private readonly IProcessedAudioDiarizationService _diarization;
        private readonly IProfileCatalog _profiles;
        private readonly IJobPaths _paths;

        public RecognizeSpeechStep(
            IProcessedAudioTranscriptionService transcription, IProcessedAudioDiarizationService diarization,
            IProfileCatalog profiles, IJobPaths paths,
            IJobProgress progress, IJobRepository jobs, ILogger<RecognizeSpeechStep> logger)
            : base(progress, jobs, logger)
        {
            _transcription = transcription;
            _diarization = diarization;
            _profiles = profiles;
            _paths = paths;
        }

        public string Wav16kPath { get; set; } = "";
        public string ProfileKey { get; set; } = "";
        public List<SpeechChunkDto> Chunks { get; set; } = [];
        public string TranscriptPath { get; set; } = "";
        public string DiarizationPath { get; set; } = "";

        protected override string StepName => "Распознавание речи и говорящих";
        protected override int PercentAtStart => BandStart;
        protected override int PercentWhenDone => BandEnd;

        protected override async Task ExecuteAsync(IStepExecutionContext context)
        {
            var outputDirectory = _paths.TranscriptsDirectory(JobId);
            Directory.CreateDirectory(outputDirectory);

            var chunks = Chunks.Select(c => new SpeechChunk(c.Start, c.End)).ToList();
            var prompt = _profiles.Get(ProfileKey).WhisperPrompt;

            // Без этого полоса стояла бы на 15% всё распознавание — самый долгий шаг конвейера.
            // Progress<T> выполняет обработчик на пуле потоков, ждать запись в базу здесь некому.
            var progress = new Progress<UnitProgress>(p => _ = Progress.ReportAsync(
                JobId,
                $"Распознавание: чанк {p.Done} из {p.Total}",
                BandStart + (TranscriptionBandEnd - BandStart) * p.Done / Math.Max(p.Total, 1)));

            var transcribing = _transcription.TranscribeAsync(Wav16kPath, outputDirectory, chunks, prompt, progress);
            var diarizing = _diarization.DiarizeAsync(Wav16kPath, outputDirectory);

            // WhenAll, а не два await подряд: если упадёт первый, второй всё равно дорабатывает до
            // конца, и нативные модели не остаются с незавершённым вызовом на фоне следующего задания.
            await Task.WhenAll(transcribing, diarizing);

            var baseName = ProcessedAudioFiles.BaseName(Wav16kPath);
            TranscriptPath = Path.Combine(outputDirectory, baseName + ".json");
            DiarizationPath = Path.Combine(outputDirectory, baseName + ".diarization.json");
        }
    }
}
