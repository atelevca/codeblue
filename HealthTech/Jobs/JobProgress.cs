namespace HealthTech.Jobs
{
    public interface IJobProgress
    {
        /// <summary>Записывает текущий шаг и накопленный процент в строку задания.</summary>
        Task ReportAsync(Guid jobId, string step, int percent, CancellationToken cancellationToken = default);
    }

    public class JobProgress : IJobProgress
    {
        private readonly IJobRepository _jobs;
        private readonly ILogger<JobProgress> _logger;

        public JobProgress(IJobRepository jobs, ILogger<JobProgress> logger)
        {
            _jobs = jobs;
            _logger = logger;
        }

        public async Task ReportAsync(Guid jobId, string step, int percent, CancellationToken cancellationToken = default)
        {
            await _jobs.UpdateProgressAsync(jobId, step, Math.Clamp(percent, 0, 100), cancellationToken);
            _logger.LogInformation("Задание {JobId}: {Step} ({Percent}%)", jobId, step, percent);
        }
    }
}
