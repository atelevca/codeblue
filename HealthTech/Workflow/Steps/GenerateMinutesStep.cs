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
            // Третий долгий шаг: 7B на CPU считает протокол минутами. Без докладов о фазах
            // полоса стоит на 92% и выглядит зависшей.
            var phase = new Progress<DocumentPhase>(p => _ = Progress.ReportAsync(JobId, Caption(p), Percent(p)));

            // Протокол - производная от транскрипта, а не сам транскрипт. Если модель не
            // справилась, задание всё равно завершается: результат распознавания готов и
            // доступен, а документ можно собрать позже через POST /document/save/{jobId}.
            try
            {
                var document = await _documents.GenerateAsync(JobId, phase);
                Logger.LogInformation(
                    "Задание {JobId}: протокол сохранён (сверка: {Verification})",
                    JobId,
                    document.Verification.Completed
                        ? $"{document.Verification.Findings.Count} расхождений"
                        : "не выполнена");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.LogWarning(ex, "Задание {JobId}: протокол не собран, задание завершено без него", JobId);
            }
        }

        private static string Caption(DocumentPhase phase) => phase switch
        {
            DocumentPhase.ExtractingFacts => "Генерация протокола: извлечение фактов",
            DocumentPhase.Generating => "Генерация протокола: составление документа",
            _ => "Генерация протокола: сверка с транскриптом"
        };

        private static int Percent(DocumentPhase phase) => phase switch
        {
            DocumentPhase.ExtractingFacts => BandStart,
            DocumentPhase.Generating => 94,
            _ => 98
        };
    }
}
