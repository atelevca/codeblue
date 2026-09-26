using HealthTech.Audio;
using HealthTech.Profiles;
using HealthTech.Workflow;
using WorkflowCore.Interface;

namespace HealthTech.Jobs
{
    /// <summary>Ответ на загрузку файла. FileId - это же и идентификатор будущего задания.</summary>
    public record UploadedFile(Guid FileId, string FileName, long SizeBytes, string Format, double DurationSec);

    /// <summary>
    /// Оформление записи по загруженному файлу. DiscussionType - ключ профиля
    /// (medical | administrative | financial).
    /// </summary>
    public record SaveRecordRequest(Guid FileId, string? Title, int? SpeakersCount, string DiscussionType);

    public interface IJobService
    {
        /// <summary>
        /// Сохраняет загруженный файл в каталог нового задания, разбирает его ffprobe и создаёт
        /// строку в статусе Uploaded. Обработку не запускает - это делает оформление записи.
        /// </summary>
        Task<UploadedFile> UploadAsync(Stream content, string fileName, CancellationToken cancellationToken = default);

        /// <summary>
        /// Дописывает карточку к загруженному файлу и запускает обработку.
        /// Возвращает карточку сразу, не дожидаясь конца обработки.
        /// </summary>
        Task<Job> SaveRecordAsync(SaveRecordRequest request, CancellationToken cancellationToken = default);

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
        private readonly IAudioProcessor _audio;
        private readonly IWorkflowHost _workflow;
        private readonly ILogger<JobService> _logger;

        public JobService(
            IJobRepository jobs, IJobPaths paths, IProfileCatalog profiles, IAudioProcessor audio,
            IWorkflowHost workflow, ILogger<JobService> logger)
        {
            _jobs = jobs;
            _paths = paths;
            _profiles = profiles;
            _audio = audio;
            _workflow = workflow;
            _logger = logger;
        }

        public async Task<UploadedFile> UploadAsync(
            Stream content, string fileName, CancellationToken cancellationToken = default)
        {
            var fileId = Guid.NewGuid();
            var safeName = SafeFileName.Sanitize(fileName);
            var inputDirectory = _paths.InputDirectory(fileId);
            var sourcePath = Path.Combine(inputDirectory, safeName);

            long sizeBytes;
            AudioFileInfo info;
            try
            {
                Directory.CreateDirectory(inputDirectory);
                await using (var file = File.Create(sourcePath))
                {
                    await content.CopyToAsync(file, cancellationToken);
                }

                sizeBytes = new FileInfo(sourcePath).Length;
                if (sizeBytes == 0)
                {
                    throw new AudioProcessingException(AudioProcessingError.NotAudio, "Загруженный файл пуст.");
                }

                // ffprobe здесь, а не на первом шаге конвейера: битый файл отбивается 422-м
                // прямо на загрузке, а не падением задания через минуту обработки.
                info = await _audio.InspectAsync(sourcePath, cancellationToken);
            }
            catch (Exception ex)
            {
                // Каталог уже создан, а строки не будет - убираем за собой, иначе каждая
                // отбитая загрузка оставляет мусорную папку.
                TryDeleteDirectory(inputDirectory);
                throw ex is AudioProcessingException
                    ? ex
                    : new AudioProcessingException(AudioProcessingError.FileSystemError,
                        $"Не удалось сохранить загруженный файл в '{sourcePath}': {ex.Message}", ex);
            }

            // ProfileKey у загруженного файла пустой: тип записи ещё не выбран, а столбец
            // NOT NULL, и поменять это в SQLite нечем.
            try
            {
                await _jobs.InsertAsync(new Job(
                    fileId, safeName, "", JobStatus.Uploaded, null, 0, null, null,
                    DateTimeOffset.UtcNow, null,
                    sizeBytes, info.Format, info.DurationSeconds, null, null), cancellationToken);
            }
            catch (Exception ex)
            {
                // Файл на диске есть, строки нет - и такого состояния не видно ни в GET /jobs,
                // ни в будущей уборке по таблице. Это хуже брошенной строки в Uploaded: там
                // хотя бы есть за что зацепиться. Убираем каталог, раз записи не будет.
                TryDeleteDirectory(inputDirectory);
                throw new AudioProcessingException(AudioProcessingError.FileSystemError,
                    $"Не удалось создать запись о файле {fileId}: {ex.Message}", ex);
            }

            _logger.LogInformation("Загружен файл {FileId} ({FileName}, {Format}, {Bytes} Б)",
                fileId, safeName, info.Format, sizeBytes);

            // Длительность в ответе не nullable: UI показывает её в карточке, и ноль там
            // честнее, чем пустое место. Контейнеры без длительности встречаются редко.
            return new UploadedFile(fileId, safeName, sizeBytes, info.Format, info.DurationSeconds ?? 0);
        }

        // Верхняя граница взята с запасом: на записи консилиума бывает до шести говорящих,
        // всё что выше - опечатка в форме, и показывать её в карточке незачем.
        private const int MaxSpeakersCount = 20;

        public async Task<Job> SaveRecordAsync(
            SaveRecordRequest request, CancellationToken cancellationToken = default)
        {
            // Бросает UnknownProfile -> 400 с перечнем допустимых, до любых изменений.
            var profile = _profiles.Get(request.DiscussionType);

            if (request.SpeakersCount is { } speakers && (speakers < 1 || speakers > MaxSpeakersCount))
            {
                throw new AudioProcessingException(AudioProcessingError.InvalidRequest,
                    $"speakersCount должен быть от 1 до {MaxSpeakersCount}, получено {speakers}.");
            }

            var job = await _jobs.GetAsync(request.FileId, cancellationToken)
                ?? throw new AudioProcessingException(AudioProcessingError.InputNotFound,
                    $"Файл {request.FileId} не найден. Сначала загрузите его через POST /files.");

            if (job.Status != JobStatus.Uploaded)
            {
                throw new AudioProcessingException(AudioProcessingError.RecordAlreadyCreated,
                    $"По файлу {request.FileId} запись уже оформлена (статус {job.Status}).");
            }

            // Файл могли убрать с диска руками между загрузкой и оформлением. Проверяем до
            // смены статуса: иначе запись ушла бы в Pending и упала бы на первом шаге.
            var sourcePath = Path.Combine(_paths.InputDirectory(request.FileId), job.FileName);
            if (!File.Exists(sourcePath))
            {
                throw new AudioProcessingException(AudioProcessingError.InputNotFound,
                    $"Файл записи '{sourcePath}' отсутствует на диске. Загрузите его заново.");
            }

            // Пустой заголовок - не ошибка: в списке лучше показать имя файла, чем пустую строку.
            var title = string.IsNullOrWhiteSpace(request.Title) ? job.FileName : request.Title.Trim();

            if (!await _jobs.TrySaveRecordAsync(
                    request.FileId, title, request.SpeakersCount, profile.Key, cancellationToken))
            {
                // Ноль изменённых строк значит, что между проверкой выше и этим обновлением
                // прошёл второй такой же запрос и забрал файл себе.
                throw new AudioProcessingException(AudioProcessingError.RecordAlreadyCreated,
                    $"По файлу {request.FileId} запись уже оформлена.");
            }

            string workflowId;
            try
            {
                workflowId = await _workflow.StartWorkflow(TranscriptionWorkflow.WorkflowId, new TranscriptionJobData
                {
                    JobId = request.FileId,
                    ProfileKey = profile.Key,
                    SourcePath = sourcePath
                });
            }
            catch (Exception ex)
            {
                // Строка уже в Pending. Без этого запись навсегда осталась бы с нулевым
                // прогрессом и пустой ошибкой, и клиент опрашивал бы её до перезапуска.
                _logger.LogError(ex, "Запись {JobId}: не удалось запустить workflow", request.FileId);
                await _jobs.UpdateStatusAsync(request.FileId, JobStatus.Failed,
                    $"Не удалось запустить обработку: {ex.Message}", cancellationToken);
                throw new AudioProcessingException(AudioProcessingError.FileSystemError,
                    $"Не удалось запустить обработку записи {request.FileId}: {ex.Message}", ex);
            }

            await _jobs.SetWorkflowIdAsync(request.FileId, workflowId, cancellationToken);

            _logger.LogInformation("Оформлена запись {JobId} «{Title}» ({Profile}), workflow {WorkflowId}",
                request.FileId, title, profile.Key, workflowId);

            return (await _jobs.GetAsync(request.FileId, cancellationToken))!;
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
