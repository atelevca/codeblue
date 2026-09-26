using HealthTech.Audio;
using HealthTech.Jobs;
using WorkflowCore.Interface;

namespace HealthTech.Workflow.Steps
{
    public class NormalizeAudioStep : JobStep
    {
        private readonly IAudioProcessor _audio;
        private readonly IJobPaths _paths;

        public NormalizeAudioStep(
            IAudioProcessor audio, IJobPaths paths,
            IJobProgress progress, IJobRepository jobs, ILogger<NormalizeAudioStep> logger)
            : base(progress, jobs, logger)
        {
            _audio = audio;
            _paths = paths;
        }

        public string SourcePath { get; set; } = "";
        public string NormalizedPath { get; set; } = "";

        protected override string StepName => "Нормализация аудио";
        protected override int PercentAtStart => 0;
        protected override int PercentWhenDone => 5;

        protected override async Task ExecuteAsync(IStepExecutionContext context)
        {
            var outputDirectory = _paths.ProcessedDirectory(JobId);
            Directory.CreateDirectory(outputDirectory);
            var processed = await _audio.ProcessAudioAsync(SourcePath, outputDirectory);
            NormalizedPath = processed.WavPath;
        }
    }
}
