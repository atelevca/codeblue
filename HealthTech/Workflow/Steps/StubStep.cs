using HealthTech.Jobs;
using WorkflowCore.Interface;
using WorkflowCore.Models;

namespace HealthTech.Workflow.Steps
{
    /// <summary>Заглушка для проверки каркаса: только двигает прогресс. Удаляется в задаче 7.</summary>
    public class StubStep : StepBodyAsync
    {
        private readonly IJobProgress _progress;

        public StubStep(IJobProgress progress)
        {
            _progress = progress;
        }

        public Guid JobId { get; set; }
        public string StepName { get; set; } = "";
        public int Percent { get; set; }

        public override async Task<ExecutionResult> RunAsync(IStepExecutionContext context)
        {
            await _progress.ReportAsync(JobId, StepName, Percent);
            return ExecutionResult.Next();
        }
    }
}
