using HealthTech.Documents;
using HealthTech.Jobs;
using WorkflowCore.Interface;

namespace HealthTech.Workflow.Steps
{
    /// <summary>
    /// Последний шаг: по готовому транскрипту собирает протокол и сохраняет его рядом с ним.
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
            // Третий долгий шаг: 7B считает протокол минутами, на длинной записи - по фрагментам.
            // Без докладов полоса стоит на 92% и выглядит зависшей.
            var progress = new Progress<DocumentProgress>(p => _ = Progress.ReportAsync(JobId, Caption(p), Percent(p)));

            // Протокол - производная от транскрипта, а не сам транскрипт. Если модель не
            // справилась, задание всё равно завершается: результат распознавания готов и
            // доступен, а документ можно собрать позже через POST /document/save/{jobId}.
            try
            {
                var document = await _documents.GenerateAsync(JobId, progress);
                Logger.LogInformation(
                    "Задание {JobId}: протокол сохранён (сверка: {Verification})",
                    JobId,
                    document.Verification.Completed
                        ? $"{document.Verification.Findings.Count} расхождений, {document.Verification.DiscardedFindings} отброшено"
                        : "не выполнена");
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
                DocumentPhase.Generating => "Генерация протокола: составление документа",
                _ => "Генерация протокола: сверка с транскриптом" + fragment
            };
        }

        // 92–95 извлечение по фрагментам, 95 объединение, 96 документ, 96–99 сверка по фрагментам.
        private static int Percent(DocumentProgress p) => p.Phase switch
        {
            DocumentPhase.ExtractingFacts => BandStart + 3 * (p.Current - 1) / p.Total,
            DocumentPhase.Consolidating => 95,
            DocumentPhase.Generating => 96,
            _ => 96 + 3 * (p.Current - 1) / p.Total
        };
    }
}
