using HealthTech.Jobs;
using HealthTech.Profiles;
using HealthTech.Transcription;
using WorkflowCore.Interface;

namespace HealthTech.Workflow.Steps
{
    /// <summary>Самый длинный шаг конвейера, поэтому внутри него прогресс идёт по чанкам.</summary>
    public class TranscribeStep : JobStep
    {
        private const int BandStart = 15;
        private const int BandEnd = 55;

        private readonly IProcessedAudioTranscriptionService _transcription;
        private readonly IProfileCatalog _profiles;
        private readonly IJobPaths _paths;

        public TranscribeStep(
            IProcessedAudioTranscriptionService transcription, IProfileCatalog profiles, IJobPaths paths,
            IJobProgress progress, IJobRepository jobs, ILogger<TranscribeStep> logger)
            : base(progress, jobs, logger)
        {
            _transcription = transcription;
            _profiles = profiles;
            _paths = paths;
        }

        public string Wav16kPath { get; set; } = "";
        public string ProfileKey { get; set; } = "";
        public List<SpeechChunkDto> Chunks { get; set; } = [];
        public string TranscriptPath { get; set; } = "";

        protected override string StepName => "Распознавание речи";
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
                BandStart + (BandEnd - BandStart) * p.Done / Math.Max(p.Total, 1)));

            await _transcription.TranscribeAsync(Wav16kPath, outputDirectory, chunks, prompt, progress);

            TranscriptPath = Path.Combine(outputDirectory, ProcessedAudioFiles.BaseName(Wav16kPath) + ".json");
        }
    }
}
