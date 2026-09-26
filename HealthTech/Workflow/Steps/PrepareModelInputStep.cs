using HealthTech.Audio;
using HealthTech.Jobs;
using WorkflowCore.Interface;

namespace HealthTech.Workflow.Steps
{
    /// <summary>
    /// Готовит производный 16 кГц моно WAV. Дальше VAD, Whisper и диаризация читают именно его
    /// и не конвертируют ничего, а полноценный WAV рядом остаётся верным исходнику.
    /// </summary>
    public class PrepareModelInputStep : JobStep
    {
        private readonly IAudioProcessor _audio;

        public PrepareModelInputStep(
            IAudioProcessor audio,
            IJobProgress progress, IJobRepository jobs, ILogger<PrepareModelInputStep> logger)
            : base(progress, jobs, logger)
        {
            _audio = audio;
        }

        public string NormalizedPath { get; set; } = "";
        public string Wav16kPath { get; set; } = "";

        protected override string StepName => "Подготовка входа для моделей";
        protected override int PercentAtStart => 5;
        protected override int PercentWhenDone => 10;

        protected override async Task ExecuteAsync(IStepExecutionContext context)
        {
            Wav16kPath = await _audio.PrepareModelInputAsync(NormalizedPath);
        }
    }
}
