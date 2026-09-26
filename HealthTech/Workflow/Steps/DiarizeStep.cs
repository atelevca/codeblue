using HealthTech.Jobs;
using HealthTech.Transcription;
using WorkflowCore.Interface;

namespace HealthTech.Workflow.Steps
{
    public class DiarizeStep : JobStep
    {
        private readonly IProcessedAudioDiarizationService _diarization;
        private readonly IJobPaths _paths;

        public DiarizeStep(
            IProcessedAudioDiarizationService diarization, IJobPaths paths,
            IJobProgress progress, IJobRepository jobs, ILogger<DiarizeStep> logger)
            : base(progress, jobs, logger)
        {
            _diarization = diarization;
            _paths = paths;
        }

        public string Wav16kPath { get; set; } = "";
        public string DiarizationPath { get; set; } = "";

        protected override string StepName => "Разделение по говорящим";
        protected override int PercentAtStart => 55;
        protected override int PercentWhenDone => 75;

        protected override async Task ExecuteAsync(IStepExecutionContext context)
        {
            var outputDirectory = _paths.TranscriptsDirectory(JobId);
            await _diarization.DiarizeAsync(Wav16kPath, outputDirectory);
            DiarizationPath = Path.Combine(outputDirectory,
                ProcessedAudioFiles.BaseName(Wav16kPath) + ".diarization.json");
        }
    }
}
