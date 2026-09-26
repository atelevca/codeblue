using HealthTech.Jobs;
using HealthTech.Transcription;
using WorkflowCore.Interface;

namespace HealthTech.Workflow.Steps
{
    public class DetectSpeechChunksStep : JobStep
    {
        private readonly IAudioSampleReader _sampleReader;
        private readonly IVoiceActivityService _voiceActivity;

        public DetectSpeechChunksStep(
            IAudioSampleReader sampleReader, IVoiceActivityService voiceActivity,
            IJobProgress progress, IJobRepository jobs, ILogger<DetectSpeechChunksStep> logger)
            : base(progress, jobs, logger)
        {
            _sampleReader = sampleReader;
            _voiceActivity = voiceActivity;
        }

        public string Wav16kPath { get; set; } = "";
        public List<SpeechChunkDto> Chunks { get; set; } = [];

        protected override string StepName => "Поиск речи";
        protected override int PercentAtStart => 10;
        protected override int PercentWhenDone => 15;

        protected override async Task ExecuteAsync(IStepExecutionContext context)
        {
            var samples = await _sampleReader.ReadMono16kAsync(Wav16kPath);
            Chunks = _voiceActivity.DetectChunks(samples)
                .Select(c => new SpeechChunkDto { Start = c.Start, End = c.End })
                .ToList();
        }
    }
}
