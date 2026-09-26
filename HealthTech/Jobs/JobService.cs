using HealthTech.Audio;
using HealthTech.Profiles;
using HealthTech.Workflow;
using WorkflowCore.Interface;

namespace HealthTech.Jobs
{
    /// <summary>Ответ на загрузку файла. FileId - это же и идентификатор будущего задания.</summary>
    public record UploadedFile(Guid FileId, string FileName, long SizeBytes, string Format, double DurationSec);

    public interface IJobService
    {
        /// <summary>
        /// Сохраняет загруженный файл в каталог нового задания, разбирает его ffprobe и создаёт
        /// строку в статусе Uploaded. Обработку не запускает - это делает оформление записи.
        /// </summary>
        Task<UploadedFile> UploadAsync(Stream content, string fileName, CancellationToken cancellationToken = default);

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
            await _jobs.InsertAsync(new Job(
                fileId, safeName, "", JobStatus.Uploaded, null, 0, null, null,
                DateTimeOffset.UtcNow, null,
                sizeBytes, info.Format, info.DurationSeconds, null, null), cancellationToken);

            _logger.LogInformation("Загружен файл {FileId} ({FileName}, {Format}, {Bytes} Б)",
                fileId, safeName, info.Format, sizeBytes);

            // Длительность в ответе не nullable: UI показывает её в карточке, и ноль там
            // честнее, чем пустое место. Контейнеры без длительности встречаются редко.
            return new UploadedFile(fileId, safeName, sizeBytes, info.Format, info.DurationSeconds ?? 0);
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
