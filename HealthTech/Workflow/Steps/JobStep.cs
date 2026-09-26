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

        /// <summary>
        /// Последний шаг конвейера переводит задание в Completed. Делает это базовый класс,
        /// а не сам шаг: UpdateProgressAsync намеренно не трогает терминальные задания, поэтому
        /// отметить 100% нужно строго до смены статуса, иначе полоса замрёт на 98%.
        /// </summary>
        protected virtual bool CompletesJob => false;

        protected abstract Task ExecuteAsync(IStepExecutionContext context);

        public override async Task<ExecutionResult> RunAsync(IStepExecutionContext context)
        {
            // Try охватывает весь метод, а не только ExecuteAsync: обращения к базе в
            // IsFailedAsync и ReportAsync тоже могут бросить, а всё, что вылетело отсюда,
            // движок обрабатывает сам и никто уже не запишет Failed.
            try
            {
                // Проверка строго раньше любого отчёта о прогрессе. Отчёт переводит задание в
                // Running, поэтому шаг, возобновлённый после перезапуска, иначе вернул бы уже
                // помеченное Failed задание обратно в Running и оно зависло бы там навсегда.
                if (await IsFailedAsync())
                {
                    Logger.LogInformation("Задание {JobId} помечено Failed, шаг {Step} пропущен", JobId, StepName);
                    return ExecutionResult.Next();
                }

                await Progress.ReportAsync(JobId, StepName, PercentAtStart);
                await ExecuteAsync(context);
                await Progress.ReportAsync(JobId, StepName, PercentWhenDone);
                if (CompletesJob)
                {
                    await Jobs.UpdateStatusAsync(JobId, JobStatus.Completed, null);
                }
                return ExecutionResult.Next();
            }
            catch (Exception ex)
            {
                // Доменные ошибки ожидаемы и пишутся как есть; остальное - как есть плюс имя шага.
                var expected = ex is AudioProcessingException or LlmModelNotFoundException;
                if (expected)
                {
                    Logger.LogWarning(ex, "Задание {JobId} упало на шаге {Step}", JobId, StepName);
                }
                else
                {
                    Logger.LogError(ex, "Задание {JobId}: непредвиденная ошибка на шаге {Step}", JobId, StepName);
                }

                await TryMarkFailedAsync(expected ? ex.Message : $"{StepName}: {ex.Message}");
                return ExecutionResult.Next();
            }
        }

        private async Task<bool> IsFailedAsync() =>
            (await Jobs.GetAsync(JobId))?.Status == JobStatus.Failed;

        // Пометка Failed - последнее, что мы можем сделать. Если и она не удалась, падать
        // дальше некуда: исключение отсюда снова ушло бы движку и задание зависло бы в Running.
        private async Task TryMarkFailedAsync(string error)
        {
            try
            {
                await Jobs.UpdateStatusAsync(JobId, JobStatus.Failed, error);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Задание {JobId}: не удалось записать статус Failed", JobId);
            }
        }

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
