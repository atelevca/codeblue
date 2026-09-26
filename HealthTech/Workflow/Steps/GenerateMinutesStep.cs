using HealthTech.Documents;
using HealthTech.Jobs;
using WorkflowCore.Interface;

namespace HealthTech.Workflow.Steps
{
    /// <summary>
    /// Последний шаг: по готовому транскрипту собирает протокол и сохраняет его рядом с ним.
    /// Сверка протокола с транскриптом в шаг не входит: она ставится в очередь и идёт после
    /// того, как задание уже Completed, чтобы пользователь получил документ на минуты раньше.
    /// </summary>
    public class GenerateMinutesStep : JobStep
    {
        private readonly IDocumentService _documents;

        public GenerateMinutesStep(
            IDocumentService documents,
            IJobProgress progress, IJobRepository jobs, ILogger<GenerateMinutesStep> logger)
            : base(progress, jobs, logger)
        {
            _documents = documents;
        }

        private const int BandStart = 92;
        private const int BandEnd = 100;

        protected override bool CompletesJob => true;

        protected override string StepName => "Генерация протокола";
        protected override int PercentAtStart => BandStart;
        protected override int PercentWhenDone => BandEnd;

        protected override async Task ExecuteAsync(IStepExecutionContext context)
        {
            // Долгий шаг: 7B извлекает факты минутами, на длинной записи - по фрагментам.
            // Без докладов полоса стоит на 92% и выглядит зависшей.
            var progress = new Progress<DocumentProgress>(p => _ = Progress.ReportAsync(JobId, Caption(p), Percent(p)));

            // Протокол - производная от транскрипта, а не сам транскрипт. Если модель не
            // справилась, задание всё равно завершается: результат распознавания готов и
            // доступен, а документ можно собрать позже через POST /document/save/{jobId}.
            try
            {
                var document = await _documents.GenerateAsync(JobId, progress);
                Logger.LogInformation("Задание {JobId}: протокол сохранён (сверка: {Verification})",
                    JobId, DocumentService.Describe(document.Verification));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.LogWarning(ex, "Задание {JobId}: протокол не собран, задание завершено без него", JobId);
            }
        }

        private static string Caption(DocumentProgress p)
        {
            var fragment = p.Total > 1 ? $" (фрагмент {p.Current} из {p.Total})" : "";
            return p.Phase switch
            {
                DocumentPhase.ExtractingFacts => "Генерация протокола: извлечение фактов" + fragment,
                DocumentPhase.Consolidating => "Генерация протокола: объединение фрагментов",
                _ => "Генерация протокола: сверка с транскриптом" + fragment
            };
        }

        // 92–95 извлечение по фрагментам, 95 объединение фрагментов; документ рендерится в коде
        // мгновенно и шаг завершается на 100. Сверка (96–99) в конвейере не докладывается: она
        // идёт в фоне после Completed, но фаза остаётся для полноты сопоставления.
        private static int Percent(DocumentProgress p) => p.Phase switch
        {
            DocumentPhase.ExtractingFacts => BandStart + 3 * (p.Current - 1) / p.Total,
            DocumentPhase.Consolidating => 95,
            _ => 96 + 3 * (p.Current - 1) / p.Total
        };
    }
}
