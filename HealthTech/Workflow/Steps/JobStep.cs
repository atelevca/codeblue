using System.Text.Encodings.Web;
using System.Text.Json;
using HealthTech.Audio;
using HealthTech.Jobs;
using SemanticKernel;
using WorkflowCore.Interface;
using WorkflowCore.Models;

namespace HealthTech.Workflow.Steps
{
    /// <summary>
    /// Общее поведение шага задания: отметить прогресс, выполнить работу, а при падении
    /// перевести задание в Failed с внятным текстом вместо зависания в Running.
    /// </summary>
    public abstract class JobStep : StepBodyAsync
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        protected JobStep(IJobProgress progress, IJobRepository jobs, ILogger logger)
        {
            Progress = progress;
            Jobs = jobs;
            Logger = logger;
        }

        protected IJobProgress Progress { get; }
        protected IJobRepository Jobs { get; }
        protected ILogger Logger { get; }

        public Guid JobId { get; set; }

        /// <summary>Подпись шага для UI.</summary>
        protected abstract string StepName { get; }

        /// <summary>Границы полосы шага: процент на входе и процент по завершении.</summary>
        protected abstract int PercentAtStart { get; }
        protected abstract int PercentWhenDone { get; }

        protected abstract Task ExecuteAsync(IStepExecutionContext context);

        public override async Task<ExecutionResult> RunAsync(IStepExecutionContext context)
        {
            // Проверка строго раньше любого отчёта о прогрессе. Отчёт переводит задание в Running,
            // поэтому шаг, возобновлённый движком после перезапуска приложения, иначе вернул бы
            // уже помеченное Failed задание обратно в Running и оно зависло бы там навсегда.
            if (await IsFailedAsync())
            {
                Logger.LogInformation("Задание {JobId} помечено Failed, шаг {Step} пропущен", JobId, StepName);
                return ExecutionResult.Next();
            }

            await Progress.ReportAsync(JobId, StepName, PercentAtStart);
            try
            {
                await ExecuteAsync(context);
            }
            catch (Exception ex) when (ex is AudioProcessingException or LlmModelNotFoundException)
            {
                Logger.LogWarning(ex, "Задание {JobId} упало на шаге {Step}", JobId, StepName);
                await Jobs.UpdateStatusAsync(JobId, JobStatus.Failed, ex.Message);
                return ExecutionResult.Next();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Задание {JobId}: непредвиденная ошибка на шаге {Step}", JobId, StepName);
                await Jobs.UpdateStatusAsync(JobId, JobStatus.Failed, $"{StepName}: {ex.Message}");
                return ExecutionResult.Next();
            }

            await Progress.ReportAsync(JobId, StepName, PercentWhenDone);
            return ExecutionResult.Next();
        }

        private async Task<bool> IsFailedAsync() =>
            (await Jobs.GetAsync(JobId))?.Status == JobStatus.Failed;

        protected static async Task<T> ReadJsonAsync<T>(string path)
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<T>(stream, Json)
                   ?? throw new InvalidOperationException($"Пустой или нечитаемый файл '{path}'.");
        }

        protected static async Task WriteJsonAsync<T>(string path, T value)
        {
            await using var stream = File.Create(path);
            await JsonSerializer.SerializeAsync(stream, value, Json);
        }
    }
}
