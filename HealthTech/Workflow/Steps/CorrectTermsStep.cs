using HealthTech.Jobs;
using HealthTech.Profiles;
using HealthTech.Transcription;
using SemanticKernel.MedicalCorrection;
using WorkflowCore.Interface;

namespace HealthTech.Workflow.Steps
{
    public class CorrectTermsStep : JobStep
    {
        private readonly IMedicalTermCorrector _corrector;
        private readonly IProfileCatalog _profiles;
        private readonly IProcessedAudioFiles _files;
        private readonly IJobPaths _paths;

        public CorrectTermsStep(
            IMedicalTermCorrector corrector, IProfileCatalog profiles,
            IProcessedAudioFiles files, IJobPaths paths,
            IJobProgress progress, IJobRepository jobs, ILogger<CorrectTermsStep> logger)
            : base(progress, jobs, logger)
        {
            _corrector = corrector;
            _profiles = profiles;
            _files = files;
            _paths = paths;
        }

        private const int BandStart = 78;
        private const int BandEnd = 98;

        public string ProfileKey { get; set; } = "";
        public string TranscriptPath { get; set; } = "";

        protected override string StepName => "Коррекция терминов";
        protected override int PercentAtStart => BandStart;
        protected override int PercentWhenDone => BandEnd;

        protected override async Task ExecuteAsync(IStepExecutionContext context)
        {
            var turnsPath = AlignSpeakersStep.TurnsPath(_paths, JobId);
            var turns = await ReadJsonAsync<List<SpeakerTranscriptTurn>>(turnsPath);

            // Индекс реплики + 1 = идентификатор сегмента: по нему правка находит себя обратно.
            var segments = turns
                .Select((t, i) => new Segment(i + 1, t.Speaker,
                    TimeSpan.FromSeconds(t.Start), TimeSpan.FromSeconds(t.End), t.Text))
                .ToList();

            var profile = _profiles.Get(ProfileKey);
            var log = new CorrectionLog();

            // Второй долгий шаг: 7B на CPU держит полосу неподвижной минутами, если не докладывать.
            var progress = new Progress<BatchProgress>(p => _ = Progress.ReportAsync(
                JobId,
                $"Коррекция терминов: батч {p.Done} из {p.Total}",
                BandStart + (BandEnd - BandStart) * p.Done / Math.Max(p.Total, 1)));

            var corrected = await _corrector.CorrectAsync(segments, profile.Content, log, progress);

            // Отчёт называется по записи, а не по служебному turns.json.
            var directory = _paths.TranscriptsDirectory(JobId);
            await _files.SaveTextAsync(log.ToMarkdown(), directory, TranscriptPath, ".medical_corrections.md");

            var updated = turns.Select((t, i) => t with { Text = corrected[i].Text }).ToList();
            await WriteJsonAsync(turnsPath, updated);
        }
    }
}
