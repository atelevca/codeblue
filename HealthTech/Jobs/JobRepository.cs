using Dapper;
using HealthTech.Data;

namespace HealthTech.Jobs
{
    public interface IJobRepository
    {
        Task InsertAsync(Job job, CancellationToken cancellationToken = default);
        Task<Job?> GetAsync(Guid id, CancellationToken cancellationToken = default);
        Task<IReadOnlyList<Job>> ListAsync(CancellationToken cancellationToken = default);
        Task UpdateProgressAsync(Guid id, string currentStep, int percent, CancellationToken cancellationToken = default);
        Task UpdateStatusAsync(Guid id, JobStatus status, string? error, CancellationToken cancellationToken = default);
        Task SetWorkflowIdAsync(Guid id, string workflowId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Помечает как Failed задания, оставшиеся в Running или Pending после перезапуска
        /// приложения. Возвращает число затронутых строк.
        /// </summary>
        Task<int> FailRunningAsync(string reason, CancellationToken cancellationToken = default);
    }

    public class JobRepository : IJobRepository
    {
        private const string Columns =
            "Id, FileName, ProfileKey, Status, CurrentStep, Percent, WorkflowId, Error, CreatedAt, CompletedAt";

        private readonly IDbConnectionFactory _connections;

        public JobRepository(IDbConnectionFactory connections)
        {
            _connections = connections;
        }

        public async Task InsertAsync(Job job, CancellationToken cancellationToken = default)
        {
            using var connection = _connections.Create();
            await connection.ExecuteAsync(
                $"INSERT INTO Jobs ({Columns}) VALUES (@Id, @FileName, @ProfileKey, @Status, @CurrentStep, " +
                "@Percent, @WorkflowId, @Error, @CreatedAt, @CompletedAt)",
                new
                {
                    Id = job.Id.ToString(),
                    job.FileName,
                    job.ProfileKey,
                    Status = job.Status.ToString(),
                    job.CurrentStep,
                    job.Percent,
                    job.WorkflowId,
                    job.Error,
                    CreatedAt = job.CreatedAt.ToString("O"),
                    CompletedAt = job.CompletedAt?.ToString("O")
                });
        }

        public async Task<Job?> GetAsync(Guid id, CancellationToken cancellationToken = default)
        {
            using var connection = _connections.Create();
            var row = await connection.QuerySingleOrDefaultAsync<Row>(
                $"SELECT {Columns} FROM Jobs WHERE Id = @Id", new { Id = id.ToString() });
            return row?.ToJob();
        }

        public async Task<IReadOnlyList<Job>> ListAsync(CancellationToken cancellationToken = default)
        {
            using var connection = _connections.Create();
            var rows = await connection.QueryAsync<Row>($"SELECT {Columns} FROM Jobs ORDER BY CreatedAt DESC");
            return rows.Select(r => r.ToJob()).ToList();
        }

        public async Task UpdateProgressAsync(Guid id, string currentStep, int percent, CancellationToken cancellationToken = default)
        {
            using var connection = _connections.Create();
            // Терминальные задания не трогаем вовсе: отчёты о прогрессе идут из коллбэков
            // "выстрелил и забыл" на пуле потоков, поэтому запоздавший отчёт мог бы показать
            // Completed с процентом 49 и подписью "чанк 6 из 7". MAX не даёт полосе пятиться
            // при перестановке таких отчётов местами.
            await connection.ExecuteAsync(
                "UPDATE Jobs SET CurrentStep = @CurrentStep, Percent = MAX(Percent, @Percent), " +
                "Status = @Running " +
                "WHERE Id = @Id AND Status IN (@Pending, @Running)",
                new
                {
                    Id = id.ToString(),
                    CurrentStep = currentStep,
                    Percent = percent,
                    Pending = nameof(JobStatus.Pending),
                    Running = nameof(JobStatus.Running)
                });
        }

        public async Task UpdateStatusAsync(Guid id, JobStatus status, string? error, CancellationToken cancellationToken = default)
        {
            var completed = status is JobStatus.Completed or JobStatus.Failed;
            using var connection = _connections.Create();
            await connection.ExecuteAsync(
                "UPDATE Jobs SET Status = @Status, Error = @Error, CompletedAt = @CompletedAt WHERE Id = @Id",
                new
                {
                    Id = id.ToString(),
                    Status = status.ToString(),
                    Error = error,
                    CompletedAt = completed ? DateTimeOffset.UtcNow.ToString("O") : null
                });
        }

        public async Task SetWorkflowIdAsync(Guid id, string workflowId, CancellationToken cancellationToken = default)
        {
            using var connection = _connections.Create();
            await connection.ExecuteAsync(
                "UPDATE Jobs SET WorkflowId = @WorkflowId WHERE Id = @Id",
                new { Id = id.ToString(), WorkflowId = workflowId });
        }

        public async Task<int> FailRunningAsync(string reason, CancellationToken cancellationToken = default)
        {
            using var connection = _connections.Create();
            return await connection.ExecuteAsync(
                "UPDATE Jobs SET Status = @Failed, Error = @Error, CompletedAt = @CompletedAt " +
                "WHERE Status IN (@Running, @Pending)",
                new
                {
                    Failed = nameof(JobStatus.Failed),
                    Running = nameof(JobStatus.Running),
                    Pending = nameof(JobStatus.Pending),
                    Error = reason,
                    CompletedAt = DateTimeOffset.UtcNow.ToString("O")
                });
        }

        // Промежуточная строка: SQLite хранит всё текстом, перевод в типы делаем явно.
        private sealed class Row
        {
            public string Id { get; set; } = "";
            public string FileName { get; set; } = "";
            public string ProfileKey { get; set; } = "";
            public string Status { get; set; } = "";
            public string? CurrentStep { get; set; }
            public long Percent { get; set; }
            public string? WorkflowId { get; set; }
            public string? Error { get; set; }
            public string CreatedAt { get; set; } = "";
            public string? CompletedAt { get; set; }

            public Job ToJob() => new(
                Guid.Parse(Id), FileName, ProfileKey,
                Enum.Parse<JobStatus>(Status), CurrentStep, (int)Percent, WorkflowId, Error,
                DateTimeOffset.Parse(CreatedAt),
                CompletedAt is null ? null : DateTimeOffset.Parse(CompletedAt));
        }
    }
}
