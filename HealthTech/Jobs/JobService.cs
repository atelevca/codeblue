using HealthTech.Audio;
using HealthTech.Profiles;
using HealthTech.Workflow;
using WorkflowCore.Interface;

namespace HealthTech.Jobs
{
    public interface IJobService
    {
        /// <summary>
        /// Сохраняет загруженный файл в каталог нового задания, создаёт запись и запускает
        /// обработку. Возвращает идентификатор задания сразу, не дожидаясь конца обработки.
        /// </summary>
        Task<Guid> CreateAsync(Stream content, string fileName, string profileKey, CancellationToken cancellationToken = default);

        Task<Job?> GetAsync(Guid id, CancellationToken cancellationToken = default);
        Task<IReadOnlyList<Job>> ListAsync(CancellationToken cancellationToken = default);

        /// <summary>Содержимое итогового <c>.speakers.json</c> или null, если задание ещё не дошло до конца.</summary>
        Task<string?> GetResultAsync(Guid id, CancellationToken cancellationToken = default);
    }

    public class JobService : IJobService
    {
        private readonly IJobRepository _jobs;
        private readonly IJobPaths _paths;
        private readonly IProfileCatalog _profiles;
        private readonly IWorkflowHost _workflow;
        private readonly ILogger<JobService> _logger;

        public JobService(
            IJobRepository jobs, IJobPaths paths, IProfileCatalog profiles,
            IWorkflowHost workflow, ILogger<JobService> logger)
        {
            _jobs = jobs;
            _paths = paths;
            _profiles = profiles;
            _workflow = workflow;
            _logger = logger;
        }

        public async Task<Guid> CreateAsync(
            Stream content, string fileName, string profileKey, CancellationToken cancellationToken = default)
        {
            // Бросает UnknownProfile -> 400 до того, как что-либо будет записано на диск.
            var profile = _profiles.Get(profileKey);

            var jobId = Guid.NewGuid();
            var safeName = SafeFileName.Sanitize(fileName);
            var inputDirectory = _paths.InputDirectory(jobId);
            var sourcePath = Path.Combine(inputDirectory, safeName);

            try
            {
                Directory.CreateDirectory(inputDirectory);
                await using (var file = File.Create(sourcePath))
                {
                    await content.CopyToAsync(file, cancellationToken);
                }

                // Пустой файл отбивается сразу, до создания задания: клиенту нечего опрашивать,
                // а 422 честнее, чем задание, которое гарантированно упадёт на первом же шаге.
                if (new FileInfo(sourcePath).Length == 0)
                {
                    throw new AudioProcessingException(AudioProcessingError.NotAudio, "Загруженный файл пуст.");
                }
            }
            catch (Exception ex)
            {
                // Каталог задания уже создан, а задания не будет - убираем за собой,
                // иначе каждая отбитая загрузка оставляет пустую папку.
                TryDeleteDirectory(inputDirectory);
                throw ex is AudioProcessingException
                    ? ex
                    : new AudioProcessingException(AudioProcessingError.FileSystemError,
                        $"Не удалось сохранить загруженный файл в '{sourcePath}': {ex.Message}", ex);
            }

            await _jobs.InsertAsync(new Job(
                jobId, safeName, profile.Key, JobStatus.Pending, null, 0, null, null,
                DateTimeOffset.UtcNow, null), cancellationToken);

            string workflowId;
            try
            {
                workflowId = await _workflow.StartWorkflow(TranscriptionWorkflow.WorkflowId, new TranscriptionJobData
                {
                    JobId = jobId,
                    ProfileKey = profile.Key,
                    SourcePath = sourcePath
                });
            }
            catch (Exception ex)
            {
                // Строка уже создана. Без этого задание навсегда осталось бы в Pending с нулевым
                // прогрессом и пустой ошибкой, и клиент опрашивал бы его до следующего перезапуска.
                _logger.LogError(ex, "Задание {JobId}: не удалось запустить workflow", jobId);
                await _jobs.UpdateStatusAsync(jobId, JobStatus.Failed,
                    $"Не удалось запустить обработку: {ex.Message}", cancellationToken);
                throw new AudioProcessingException(AudioProcessingError.FileSystemError,
                    $"Не удалось запустить обработку задания {jobId}: {ex.Message}", ex);
            }

            await _jobs.SetWorkflowIdAsync(jobId, workflowId, cancellationToken);

            _logger.LogInformation("Создано задание {JobId} ({FileName}, профиль {Profile}), workflow {WorkflowId}",
                jobId, safeName, profile.Key, workflowId);
            return jobId;
        }

        private void TryDeleteDirectory(string directory)
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Не удалось убрать каталог отменённой загрузки {Directory}", directory);
            }
        }

        public Task<Job?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
            _jobs.GetAsync(id, cancellationToken);

        public Task<IReadOnlyList<Job>> ListAsync(CancellationToken cancellationToken = default) =>
            _jobs.ListAsync(cancellationToken);

        public async Task<string?> GetResultAsync(Guid id, CancellationToken cancellationToken = default)
        {
            var directory = _paths.TranscriptsDirectory(id);
            if (!Directory.Exists(directory))
            {
                return null;
            }

            var resultPath = Directory.EnumerateFiles(directory, "*.speakers.json").FirstOrDefault();
            return resultPath is null ? null : await File.ReadAllTextAsync(resultPath, cancellationToken);
        }
    }
}
