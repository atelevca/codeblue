using HealthTech.Jobs;
using HealthTech.Transcription;
using WorkflowCore.Interface;

namespace HealthTech.Workflow.Steps
{
    public class SaveResultStep : JobStep
    {
        private readonly IProcessedAudioFiles _files;
        private readonly IJobPaths _paths;

        public SaveResultStep(
            IProcessedAudioFiles files, IJobPaths paths,
            IJobProgress progress, IJobRepository jobs, ILogger<SaveResultStep> logger)
            : base(progress, jobs, logger)
        {
            _files = files;
            _paths = paths;
        }

        public string TranscriptPath { get; set; } = "";
        public string DiarizationPath { get; set; } = "";
        public string SpeakersPath { get; set; } = "";

        protected override string StepName => "Сохранение результата";
        protected override int PercentAtStart => 90;
        protected override int PercentWhenDone => 92;

        protected override async Task ExecuteAsync(IStepExecutionContext context)
        {
            var turnsPath = AlignSpeakersStep.TurnsPath(_paths, JobId);
            var turns = await ReadJsonAsync<List<SpeakerTranscriptTurn>>(turnsPath);
            var transcript = await ReadJsonAsync<TranscriptionResult>(TranscriptPath);
            var diarization = await ReadJsonAsync<DiarizationResult>(DiarizationPath);

            var result = new SpeakerTranscriptResult(
                transcript.FileName,
                transcript.DurationSeconds,
                turns.Select(t => t.Speaker).Distinct().ToList(),
                turns,
                TranscriptDialogue.Format(turns.Select(t => (t.Speaker, t.Text))),
                transcript.TranscriptionMs,
                diarization.DiarizationMs);

            // Имя берётся от TranscriptPath, а не от служебного turns.json.
            var directory = _paths.TranscriptsDirectory(JobId);
            SpeakersPath = await _files.SaveJsonAsync(result, directory, TranscriptPath, ".speakers.json");

            File.Delete(turnsPath);
        }
    }
}
