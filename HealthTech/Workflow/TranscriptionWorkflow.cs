using HealthTech.Workflow.Steps;
using WorkflowCore.Interface;
using WorkflowCore.Models;

namespace HealthTech.Workflow
{
    public class TranscriptionWorkflow : IWorkflow<TranscriptionJobData>
    {
        public const string WorkflowId = "transcription";

        public string Id => WorkflowId;

        // Версия 1 — каркас на заглушках; определение изменилось, а старые инстансы
        // остались в workflow.db, поэтому номер поднят.
        public int Version => 2;

        public void Build(IWorkflowBuilder<TranscriptionJobData> builder)
        {
            // Умолчание движка - Retry раз в 60 с без конца. Для нашего конвейера это худший
            // исход: шаги дорогие, а сбой обычно означает отсутствующую модель или битый файл,
            // что повтором не лечится. Terminate останавливает инстанс, а статус задания
            // проставляет обработчик OnStepError в Program.cs.
            builder.UseDefaultErrorBehavior(WorkflowErrorHandling.Terminate);

            builder
                .StartWith<NormalizeAudioStep>()
                    .Input(s => s.JobId, d => d.JobId)
                    .Input(s => s.SourcePath, d => d.SourcePath)
                    .Output(d => d.NormalizedPath, s => s.NormalizedPath)
                .Then<PrepareModelInputStep>()
                    .Input(s => s.JobId, d => d.JobId)
                    .Input(s => s.NormalizedPath, d => d.NormalizedPath)
                    .Output(d => d.Wav16kPath, s => s.Wav16kPath)
                .Then<DetectSpeechChunksStep>()
                    .Input(s => s.JobId, d => d.JobId)
                    .Input(s => s.Wav16kPath, d => d.Wav16kPath)
                    .Output(d => d.Chunks, s => s.Chunks)
                .Then<TranscribeStep>()
                    .Input(s => s.JobId, d => d.JobId)
                    .Input(s => s.Wav16kPath, d => d.Wav16kPath)
                    .Input(s => s.ProfileKey, d => d.ProfileKey)
                    .Input(s => s.Chunks, d => d.Chunks)
                    .Output(d => d.TranscriptPath, s => s.TranscriptPath)
                .Then<DiarizeStep>()
                    .Input(s => s.JobId, d => d.JobId)
                    .Input(s => s.Wav16kPath, d => d.Wav16kPath)
                    .Output(d => d.DiarizationPath, s => s.DiarizationPath)
                .Then<AlignSpeakersStep>()
                    .Input(s => s.JobId, d => d.JobId)
                    .Input(s => s.TranscriptPath, d => d.TranscriptPath)
                    .Input(s => s.DiarizationPath, d => d.DiarizationPath)
                .Then<CorrectTermsStep>()
                    .Input(s => s.JobId, d => d.JobId)
                    .Input(s => s.ProfileKey, d => d.ProfileKey)
                    .Input(s => s.TranscriptPath, d => d.TranscriptPath)
                .Then<SaveResultStep>()
                    .Input(s => s.JobId, d => d.JobId)
                    .Input(s => s.TranscriptPath, d => d.TranscriptPath)
                    .Input(s => s.DiarizationPath, d => d.DiarizationPath)
                    .Output(d => d.SpeakersPath, s => s.SpeakersPath);
        }
    }
}
