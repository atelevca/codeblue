using HealthTech.Workflow.Steps;
using WorkflowCore.Interface;

namespace HealthTech.Workflow
{
    public class TranscriptionWorkflow : IWorkflow<TranscriptionJobData>
    {
        public const string WorkflowId = "transcription";

        public string Id => WorkflowId;
        public int Version => 1;

        public void Build(IWorkflowBuilder<TranscriptionJobData> builder)
        {
            builder
                .StartWith<StubStep>()
                    .Input(step => step.JobId, data => data.JobId)
                    .Input(step => step.StepName, data => "Заглушка 1")
                    .Input(step => step.Percent, data => 50)
                .Then<StubStep>()
                    .Input(step => step.JobId, data => data.JobId)
                    .Input(step => step.StepName, data => "Заглушка 2")
                    .Input(step => step.Percent, data => 100);
        }
    }
}
