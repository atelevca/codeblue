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
        /// Оформляет запись по уже загруженному файлу и переводит её в Pending.
        /// Возвращает false, если строки в статусе Uploaded не оказалось.
        /// </summary>
        Task<bool> TrySaveRecordAsync(Guid id, string title, int? speakersCount, string profileKey,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Помечает как Failed задания, оставшиеся в Running или Pending после перезапуска
        /// приложения. Возвращает число затронутых строк.
        /// </summary>
        Task<int> FailRunningAsync(string reason, CancellationToken cancellationToken = default);
    }

    public class JobRepository : IJobRepository
    {
        private const string Columns =
            "Id, FileName, ProfileKey, Status, CurrentStep, Percent, WorkflowId, Error, CreatedAt, CompletedAt, " +
            "SizeBytes, Format, DurationSec, Title, SpeakersCount";

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
                "@Percent, @WorkflowId, @Error, @CreatedAt, @CompletedAt, " +
                "@SizeBytes, @Format, @DurationSec, @Title, @SpeakersCount)",
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
                    CompletedAt = job.CompletedAt?.ToString("O"),
                    job.SizeBytes,
                    job.Format,
                    job.DurationSec,
                    job.Title,
                    job.SpeakersCount
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
            // Загруженные, но не оформленные файлы - ещё не записи: пользователь в этот
            // момент заполняет форму, и в списке им делать нечего.
            var rows = await connection.QueryAsync<Row>(
                $"SELECT {Columns} FROM Jobs WHERE Status <> @Uploaded ORDER BY CreatedAt DESC",
                new { Uploaded = nameof(JobStatus.Uploaded) });
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

        public async Task<bool> TrySaveRecordAsync(
            Guid id, string title, int? speakersCount, string profileKey,
            CancellationToken cancellationToken = default)
        {
            using var connection = _connections.Create();
            // Условие Status = Uploaded делает переход атомарным. Проверки в сервисе для этого мало:
            // два одновременных POST /jobs на один fileId прошли бы её оба и запустили два workflow
            // на одном файле. Здесь второй получает ноль строк и превращается в 409.
            var affected = await connection.ExecuteAsync(
                "UPDATE Jobs SET Title = @Title, SpeakersCount = @SpeakersCount, ProfileKey = @ProfileKey, " +
                "Status = @Pending WHERE Id = @Id AND Status = @Uploaded",
                new
                {
                    Id = id.ToString(),
                    Title = title,
                    SpeakersCount = speakersCount,
                    ProfileKey = profileKey,
                    Pending = nameof(JobStatus.Pending),
                    Uploaded = nameof(JobStatus.Uploaded)
                });
            return affected == 1;
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
            public long SizeBytes { get; set; }
            public string? Format { get; set; }
            public double? DurationSec { get; set; }
            public string? Title { get; set; }
            public long? SpeakersCount { get; set; }

            public Job ToJob() => new(
                Guid.Parse(Id), FileName, ProfileKey,
                Enum.Parse<JobStatus>(Status), CurrentStep, (int)Percent, WorkflowId, Error,
                DateTimeOffset.Parse(CreatedAt),
                CompletedAt is null ? null : DateTimeOffset.Parse(CompletedAt),
                SizeBytes, Format, DurationSec, Title, (int?)SpeakersCount);
        }
    }
}
