using HealthTech.Jobs;
using HealthTech.Transcription;
using WorkflowCore.Interface;

namespace HealthTech.Workflow.Steps
{
    public class AlignSpeakersStep : JobStep
    {
        private readonly ISpeakerAlignmentService _alignment;
        private readonly IJobPaths _paths;

        public AlignSpeakersStep(
            ISpeakerAlignmentService alignment, IJobPaths paths,
            IJobProgress progress, IJobRepository jobs, ILogger<AlignSpeakersStep> logger)
            : base(progress, jobs, logger)
        {
            _alignment = alignment;
            _paths = paths;
        }

        public string TranscriptPath { get; set; } = "";
        public string DiarizationPath { get; set; } = "";

        protected override string StepName => "Выравнивание по говорящим";
        protected override int PercentAtStart => 75;
        protected override int PercentWhenDone => 78;

        protected override async Task ExecuteAsync(IStepExecutionContext context)
        {
            var transcript = await ReadJsonAsync<TranscriptionResult>(TranscriptPath);
            var diarization = await ReadJsonAsync<DiarizationResult>(DiarizationPath);

            var turns = _alignment.Align(transcript.Segments, diarization.Segments);

            // Реплики передаются следующим шагам через файл, а не через данные workflow:
            // движок сериализует их в базу на каждом переходе.
            await WriteJsonAsync(TurnsPath(_paths, JobId), turns);

            Logger.LogInformation("Задание {JobId}: {SegmentCount} сегмент(ов) сведены в {TurnCount} реплик(у)",
                JobId, transcript.Segments.Count, turns.Count);
        }

        /// <summary>Промежуточный файл реплик. Удаляется последним шагом.</summary>
        public static string TurnsPath(IJobPaths paths, Guid jobId) =>
            Path.Combine(paths.TranscriptsDirectory(jobId), "turns.json");
    }
}
