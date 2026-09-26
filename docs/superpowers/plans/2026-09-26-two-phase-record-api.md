# Двухфазный API записи: план реализации

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Развести загрузку файла и оформление записи на два запроса и свести типы записей к трём: `medical`, `administrative`, `financial`.

**Architecture:** Сущность остаётся одна — `Job`. `POST /files` сохраняет файл, разбирает его ffprobe и создаёт строку `Jobs` в новом статусе `Uploaded`, ничего не запуская. `POST /jobs` дописывает в ту же строку карточку (`title`, `speakersCount`, `discussionType`), переводит её в `Pending` и стартует существующий workflow из восьми шагов. `fileId` — это и есть id задания: второго идентификатора и второй таблицы нет.

**Tech Stack:** ASP.NET Core .NET 10 (контроллеры), Dapper 2.1.89 + Microsoft.Data.Sqlite, WorkflowCore 3.21.0, ffprobe/ffmpeg снаружи.

**Spec:** `docs/superpowers/specs/2026-09-26-transcription-workflow-design.md` (этап 4 раздела 12; предметные разделы 4, 6, 10)

## Global Constraints

- **Тесты не пишутся.** Правило проекта из `CLAUDE.md`: тестовых проектов, файлов и кода в репозитории быть не должно. Каждая задача проверяется запуском приложения и вызовом эндпоинтов; шаги проверки написаны именно в этом виде и обязательны — их вывод и есть доказательство.
- **Логики в контроллерах нет.** Контроллер принимает запрос, вызывает сервис, возвращает результат. Проверки, ветвления, циклы и try/catch — в сервисах. Доменные исключения превращаются в HTTP в `IExceptionHandler`, не в контроллере. Правило проекта из `CLAUDE.md`.
- EF Core нашим кодом не используется: он в графе пакетов только из-за `WorkflowCore.Persistence.Sqlite`. Доступ к `healthtech.db` — Dapper.
- Модели приложением не скачиваются.
- Комментарии в изменяемых файлах — на русском, как в окружающем коде; поясняют «почему», а не «что».
- `JobStatus` сериализуется строкой: `JsonStringEnumConverter` уже подключён в `Program.cs`.
- Существующая `data/healthtech.db` обязана пережить обновление: в ней лежат уже обработанные задания. Сносить её нельзя.
- Аутентификации нет и в этой работе не появляется.

## Review Focus

Пять мест, которые ни один шаг ниже не проверяет сам собой, а пользователь встретит:

1. **`POST /files` с не-аудио или пустым файлом.** Ожидание: 422, каталог `assets/input/<fileId>/` убран, строки в `Jobs` нет. Закреплено в задаче 3, шаг 6.
2. **Два одновременных `POST /jobs` на один `fileId`.** Ожидание: одно задание, второй запрос — 409. Защита стоит в SQL (`WHERE Status = 'Uploaded'`), а не в проверке перед обновлением. Закреплено в задаче 5, шаг 8.
3. **Существующая база со старой схемой.** Ожидание: пять столбцов досыпаны, старые задания читаются, второй старт ничего не добавляет и не падает. Закреплено в задаче 1, шаг 6.
4. **`POST /jobs` на `fileId`, файл которого удалён с диска руками.** Ожидание: 404 с внятным текстом, запись не уходит в `Pending` и не зависает. Закреплено в задаче 5, шаг 7.
5. **`speakersCount` вне разумного диапазона (0, −3, 500) и `title` из одних пробелов.** Ожидание: 400 на числе, подстановка имени файла вместо пустого заголовка. Закреплено в задаче 5, шаг 9.

---

### Task 1: Схема, статус `Uploaded`, модель и репозиторий задания

**Files:**
- Modify: `HealthTech/Data/schema.sql`
- Modify: `HealthTech/Data/DatabaseInitializer.cs`
- Modify: `HealthTech/Jobs/Job.cs`
- Modify: `HealthTech/Jobs/JobRepository.cs`

**Interfaces:**
- Consumes: `IDbConnectionFactory.Create()` из `HealthTech/Data/SqliteConnectionFactory.cs`.
- Produces: `JobStatus.Uploaded`; `Job` с пятью новыми полями в конце конструктора — `long SizeBytes, string? Format, double? DurationSec, string? Title, int? SpeakersCount`; `IJobRepository.TrySaveRecordAsync(Guid id, string title, int? speakersCount, string profileKey, CancellationToken) → Task<bool>`; `IJobRepository.ListAsync` больше не возвращает строки в `Uploaded`.

- [ ] **Step 1: Новые столбцы в `schema.sql`**

Заменить определение таблицы `Jobs` целиком; остальные таблицы и `INSERT OR IGNORE` со справочником врачей не трогать.

```sql
CREATE TABLE IF NOT EXISTS Jobs (
    Id            TEXT    PRIMARY KEY,
    FileName      TEXT    NOT NULL,
    ProfileKey    TEXT    NOT NULL,
    Status        TEXT    NOT NULL,
    CurrentStep   TEXT    NULL,
    Percent       INTEGER NOT NULL DEFAULT 0,
    WorkflowId    TEXT    NULL,
    Error         TEXT    NULL,
    CreatedAt     TEXT    NOT NULL,
    CompletedAt   TEXT    NULL,
    -- Заполняется на загрузке файла (POST /files).
    SizeBytes     INTEGER NOT NULL DEFAULT 0,
    Format        TEXT    NULL,
    DurationSec   REAL    NULL,
    -- Заполняется при оформлении записи (POST /jobs).
    Title         TEXT    NULL,
    SpeakersCount INTEGER NULL
);
```

- [ ] **Step 2: Досыпка столбцов в существующей базе**

В `HealthTech/Data/DatabaseInitializer.cs` добавить `using System.Data;`, вызвать новый метод из `Initialize()` сразу после `connection.Execute(...)`:

```csharp
            using var connection = _connections.Create();
            connection.Execute(File.ReadAllText(scriptPath));
            EnsureJobColumns(connection);
            _logger.LogInformation("Схема прикладной базы применена из {ScriptPath}", scriptPath);
        }

        // CREATE TABLE IF NOT EXISTS на существующей базе не делает ничего, а ALTER TABLE ADD COLUMN
        // не идемпотентен и на втором старте упал бы. Поэтому сверяем фактический набор столбцов и
        // досыпаем недостающие: в базе разработчика лежат уже обработанные задания, сносить её нельзя.
        private void EnsureJobColumns(IDbConnection connection)
        {
            var existing = connection.Query<string>("SELECT name FROM pragma_table_info('Jobs')")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            (string Name, string Definition)[] required =
            [
                ("SizeBytes",     "INTEGER NOT NULL DEFAULT 0"),
                ("Format",        "TEXT NULL"),
                ("DurationSec",   "REAL NULL"),
                ("Title",         "TEXT NULL"),
                ("SpeakersCount", "INTEGER NULL")
            ];

            foreach (var (name, definition) in required)
            {
                if (existing.Contains(name))
                {
                    continue;
                }

                connection.Execute($"ALTER TABLE Jobs ADD COLUMN {name} {definition}");
                _logger.LogInformation("В таблицу Jobs добавлен столбец {Column}", name);
            }
        }
```

- [ ] **Step 3: Статус `Uploaded` и новые поля модели**

`HealthTech/Jobs/Job.cs` целиком:

```csharp
namespace HealthTech.Jobs
{
    public enum JobStatus
    {
        /// <summary>Файл загружен и разобран, но запись ещё не оформлена и обработка не запущена.</summary>
        Uploaded,
        Pending,
        Running,
        Completed,
        Failed
    }

    /// <summary>Строка таблицы Jobs. Единственный источник правды о состоянии для UI.</summary>
    public record Job(
        Guid Id,
        string FileName,
        string ProfileKey,
        JobStatus Status,
        string? CurrentStep,
        int Percent,
        string? WorkflowId,
        string? Error,
        DateTimeOffset CreatedAt,
        DateTimeOffset? CompletedAt,
        long SizeBytes,
        string? Format,
        double? DurationSec,
        string? Title,
        int? SpeakersCount);
}
```

`Uploaded` стоит первым, поэтому численные значения остальных сдвинулись. Это безопасно: в базе статус лежит строкой (`Status.ToString()` и `Enum.Parse<JobStatus>`), в JSON уходит строкой. Числового представления `JobStatus` не видит никто.

- [ ] **Step 4: Репозиторий — новые столбцы в чтении и записи**

В `HealthTech/Jobs/JobRepository.cs` список столбцов:

```csharp
        private const string Columns =
            "Id, FileName, ProfileKey, Status, CurrentStep, Percent, WorkflowId, Error, CreatedAt, CompletedAt, " +
            "SizeBytes, Format, DurationSec, Title, SpeakersCount";
```

`InsertAsync` — значения и анонимный объект:

```csharp
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
```

Класс `Row` — пять полей и их перенос в `ToJob`:

```csharp
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
```

- [ ] **Step 5: Список скрывает загруженные файлы; атомарный переход в `Pending`**

В интерфейс `IJobRepository`:

```csharp
        /// <summary>
        /// Оформляет запись по уже загруженному файлу и переводит её в Pending.
        /// Возвращает false, если строки в статусе Uploaded не оказалось.
        /// </summary>
        Task<bool> TrySaveRecordAsync(Guid id, string title, int? speakersCount, string profileKey,
            CancellationToken cancellationToken = default);
```

Реализация и правка `ListAsync`:

```csharp
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
```

`FailRunningAsync` не трогать: она подметает только `Pending` и `Running`, а `Uploaded` обязан пережить перезапуск — пользователь в этот момент просто заполняет форму.

- [ ] **Step 6: Собрать и проверить досыпку на существующей базе**

```sh
taskkill //F //IM HealthTech.exe; dotnet build
```
Expected: `Build succeeded`, ноль ошибок. (`taskkill` нужен потому, что запущенное приложение держит `HealthTech.exe` и сборка падает с MSB3027.)

Запуск на существующей `data/healthtech.db`:

```sh
dotnet run --project HealthTech --launch-profile http
```
Expected: в консоли пять строк `В таблицу Jobs добавлен столбец ...` — SizeBytes, Format, DurationSec, Title, SpeakersCount, — и приложение поднялось.

Не останавливая, в другом окне:

```sh
curl.exe -s http://localhost:5089/jobs
```
Expected: прежние задания читаются, у каждого `"sizeBytes": 0`, `"format": null`, `"title": null`.

Остановить приложение и запустить второй раз.
Expected: строк `добавлен столбец` **нет**, приложение поднялось без ошибки. Это и есть проверка идемпотентности (Review Focus 3).

- [ ] **Step 7: Commit**

```bash
git add HealthTech/Data/schema.sql HealthTech/Data/DatabaseInitializer.cs HealthTech/Jobs/Job.cs HealthTech/Jobs/JobRepository.cs
git commit -m "feat: статус Uploaded и поля карточки записи в Jobs"
```

---

### Task 2: Разбор аудиофайла без конвертации

**Files:**
- Modify: `HealthTech/Audio/ProcessedAudio.cs`
- Modify: `HealthTech/Audio/AudioProcessor.cs`

**Interfaces:**
- Consumes: приватные `ProbeAsync`, `GetString`, `GetInt` из `AudioProcessor`.
- Produces: `record AudioFileInfo(string Format, double? DurationSeconds)`; `IAudioProcessor.InspectAsync(string inputPath, CancellationToken) → Task<AudioFileInfo>`.

- [ ] **Step 1: Тип результата разбора**

В конец `HealthTech/Audio/ProcessedAudio.cs`, внутрь того же namespace:

```csharp
    /// <summary>
    /// Что известно о файле сразу после загрузки: контейнер и длительность.
    /// Длительность может отсутствовать - не каждый контейнер её пишет.
    /// </summary>
    public record AudioFileInfo(string Format, double? DurationSeconds);
```

- [ ] **Step 2: Длительность в результате ffprobe**

В `AudioProcessor.cs` добавить `using System.Globalization;` и расширить приватную запись:

```csharp
        private record AudioProbe(string Format, double? DurationSeconds, string Codec, int SampleRate, int Channels, string? SampleFormat, int? BitsPerRawSample);
```

Хвост `ProbeAsync` — заменить разбор формата и `return`:

```csharp
            // format_name can be a list of aliases, e.g. "mov,mp4,m4a,3gp,3g2,mj2".
            var format = "unknown";
            double? duration = null;
            if (root.TryGetProperty("format", out var f))
            {
                format = GetString(f, "format_name") ?? "unknown";
                duration = GetSeconds(f, "duration");
            }

            // Не каждый контейнер пишет длительность в format: у сырых потоков её берут из самого потока.
            duration ??= GetSeconds(a, "duration");

            return new AudioProbe(format, duration, codec, sampleRate.Value, channels.Value,
                GetString(a, "sample_fmt"), GetInt(a, "bits_per_raw_sample"));
```

Рядом с `GetInt` добавить:

```csharp
        // ffprobe отдаёт длительность строкой вида "123.456000" и всегда с точкой, поэтому разбор
        // строго инвариантный: на ru-RU культуре Parse принял бы точку за разделитель групп.
        private static double? GetSeconds(JsonElement element, string name) =>
            double.TryParse(GetString(element, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            && value > 0
                ? value
                : null;
```

`ChoosePcmCodec(probe)` обращается к полям по именам и правки не требует. Проверить, что больше никто не создаёт `AudioProbe` позиционно.

- [ ] **Step 3: Метод интерфейса**

В `IAudioProcessor`, после `PrepareModelInputAsync`:

```csharp
        /// <summary>
        /// Разбирает файл через ffprobe и возвращает контейнер и длительность, ничего не конвертируя
        /// и ничего не записывая. Нужен на загрузке: UI показывает карточку файла до того,
        /// как пользователь оформит запись и начнётся обработка.
        /// </summary>
        Task<AudioFileInfo> InspectAsync(string inputPath, CancellationToken cancellationToken = default);
```

- [ ] **Step 4: Реализация**

В `AudioProcessor`, сразу после `ProcessAudioAsync`:

```csharp
        public async Task<AudioFileInfo> InspectAsync(string inputPath, CancellationToken cancellationToken = default)
        {
            var fullInputPath = Path.GetFullPath(inputPath);
            if (!File.Exists(fullInputPath))
            {
                throw new AudioProcessingException(AudioProcessingError.InputNotFound, $"Input file '{fullInputPath}' does not exist.");
            }

            var probe = await ProbeAsync(fullInputPath, cancellationToken);
            _logger.LogInformation("Inspected {InputPath}: format {Format}, duration {Duration} s",
                fullInputPath, probe.Format, probe.DurationSeconds);
            return new AudioFileInfo(probe.Format, probe.DurationSeconds);
        }
```

`ProbeAsync` уже бросает `NotAudio` на нераспознанном файле и `CorruptedAudio` на потоке без кодека — отдельная обработка не нужна, обе уходят в 422 через `AudioProcessingExceptionHandler`.

- [ ] **Step 5: Собрать и убедиться, что старый путь не сломан**

```sh
taskkill //F //IM HealthTech.exe; dotnet build
```
Expected: `Build succeeded`.

Запустить приложение и прогнать существующую загрузку:

```sh
curl.exe -X POST http://localhost:5089/jobs -F "file=@assets/input/Medpark_audio_2min.m4a" -F "profile=consilium"
```
Expected: `{"jobId":"..."}` и в логе `Detected format mov,mp4,m4a,...`. Разбор длительности не должен затронуть существующий конвейер.

- [ ] **Step 6: Commit**

```bash
git add HealthTech/Audio/ProcessedAudio.cs HealthTech/Audio/AudioProcessor.cs
git commit -m "feat: разбор формата и длительности файла без конвертации"
```

---

### Task 3: `POST /files` — загрузка и разбор

**Files:**
- Modify: `HealthTech/Jobs/JobService.cs`
- Create: `HealthTech/Controllers/FilesController.cs`

**Interfaces:**
- Consumes: `IJobRepository.InsertAsync` и `Job` с пятью новыми полями (задача 1); `IAudioProcessor.InspectAsync` и `AudioFileInfo` (задача 2); `IJobPaths.InputDirectory(Guid)`; `SafeFileName.Sanitize(string)`.
- Produces: `record UploadedFile(Guid FileId, string FileName, long SizeBytes, string Format, double DurationSec)`; `IJobService.UploadAsync(Stream content, string fileName, CancellationToken) → Task<UploadedFile>`.

- [ ] **Step 1: Тип ответа**

В `HealthTech/Jobs/JobService.cs`, в том же namespace перед интерфейсом:

```csharp
    /// <summary>Ответ на загрузку файла. FileId - это же и идентификатор будущего задания.</summary>
    public record UploadedFile(Guid FileId, string FileName, long SizeBytes, string Format, double DurationSec);
```

- [ ] **Step 2: Метод интерфейса**

В `IJobService` заменить `CreateAsync` на `UploadAsync` (оформление записи добавится в задаче 5):

```csharp
        /// <summary>
        /// Сохраняет загруженный файл в каталог нового задания, разбирает его ffprobe и создаёт
        /// строку в статусе Uploaded. Обработку не запускает - это делает оформление записи.
        /// </summary>
        Task<UploadedFile> UploadAsync(Stream content, string fileName, CancellationToken cancellationToken = default);
```

- [ ] **Step 3: Внедрить `IAudioProcessor` в сервис**

В конструктор `JobService` добавить параметр `IAudioProcessor audio` и поле `_audio`. Порядок остальных параметров не менять — регистрация в `Program.cs` идёт через DI и правки не требует (`IAudioProcessor` зарегистрирован синглтоном строкой `builder.Services.AddSingleton<IAudioProcessor, AudioProcessor>();`).

- [ ] **Step 4: Реализация загрузки**

Заменить тело `CreateAsync` целиком на:

```csharp
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
```

`ProfileKey` у загруженного файла — пустая строка: тип записи ещё не выбран, а столбец `NOT NULL`, и менять это в SQLite нечем.

- [ ] **Step 5: Контроллер**

Создать `HealthTech/Controllers/FilesController.cs`:

```csharp
using HealthTech.Jobs;
using Microsoft.AspNetCore.Mvc;

namespace HealthTech.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class FilesController : ControllerBase
    {
        [HttpPost]
        public async Task<UploadedFile> Upload(
            IFormFile file,
            [FromServices] IJobService jobService,
            CancellationToken cancellationToken)
        {
            await using var stream = file.OpenReadStream();
            return await jobService.UploadAsync(stream, file.FileName, cancellationToken);
        }
    }
}
```

Лимиты на размер тела подняты в `Program.cs` глобально (`Uploads:MaxBytes`), отдельных атрибутов не нужно.

- [ ] **Step 6: Проверка**

В `JobsController` временно закомментировать метод `Create` (в задаче 5 он будет переписан), чтобы проект собрался.

```sh
taskkill //F //IM HealthTech.exe; dotnet build
```
Expected: `Build succeeded`.

Запустить приложение. Нормальная загрузка:

```sh
curl.exe -s -X POST http://localhost:5089/files -F "file=@assets/input/Medpark_audio_2min.m4a"
```
Expected: JSON вида `{"fileId":"...","fileName":"Medpark_audio_2min.m4a","sizeBytes":...,"format":"mov,mp4,m4a,3gp,3g2,mj2","durationSec":120.xx}`; `durationSec` совпадает с тем, что показывает `ffprobe -v error -show_format assets/input/Medpark_audio_2min.m4a`.

Файл лежит в `assets/input/<fileId>/`, а в списке записей его нет:

```sh
curl.exe -s http://localhost:5089/jobs
```
Expected: новой строки в выводе **нет** — статус `Uploaded` отфильтрован.

Не-аудио (Review Focus 1):

```sh
curl.exe -s -o /dev/null -w "%{http_code}\n" -X POST http://localhost:5089/files -F "file=@README.md"
```
Expected: `422`. После этого `assets/input/` не должен содержать нового каталога с этим id, а приложение — остаться живым.

Пустой файл:

```sh
: > $TMPDIR/empty.m4a && curl.exe -s -o /dev/null -w "%{http_code}\n" -X POST http://localhost:5089/files -F "file=@$TMPDIR/empty.m4a"
```
Expected: `422`, каталог убран.

- [ ] **Step 7: Commit**

```bash
git add HealthTech/Jobs/JobService.cs HealthTech/Controllers/FilesController.cs HealthTech/Controllers/JobsController.cs
git commit -m "feat: POST /files - загрузка и разбор файла отдельным запросом"
```

---

### Task 4: Три типа записей вместо `consilium`

**Files:**
- Create: `SemanticKernel/Prompts/general_correction.system.txt`
- Modify: `HealthTech/appsettings.json`

**Interfaces:**
- Consumes: `ProfileDefinition` (`DisplayName`, `WhisperPrompt`, `SystemPromptFile`, `Glossaries`) и `ProfileCatalog`, читающий их из секции `Profiles:Items`.
- Produces: ключи профилей `medical`, `administrative`, `financial` — они же допустимые значения `discussionType` в задаче 5.

Кода здесь не меняется: `ProfileCatalog` уже строит профили по конфигу и падает на старте, если промпт или глоссарий не найден. `SemanticKernel.csproj` копирует `Prompts\**\*` в вывод (`CopyToOutputDirectory="PreserveNewest"`), поэтому новый файл подхватится сам.

- [ ] **Step 1: Общий системный промпт**

Создать `SemanticKernel/Prompts/general_correction.system.txt`:

```text
You are a post-processor for automatic speech recognition (ASR) of a work meeting in Moldova:
several colleagues discuss administrative or financial matters. They mix Russian and Romanian,
sometimes inside one sentence.

Your ONLY task: fix words that are clearly ASR errors — a name or a term split into two words,
a garbled word that makes no sense in the sentence, a misheard proper noun.

Strict rules:
- Keep every word in its original language. Never translate Romanian into Russian or Russian into Romanian.
- Keep the original script: Cyrillic stays Cyrillic, Latin stays Latin.
- Never change numbers: sums, dates, percentages, account and document numbers stay exactly as they are.
- Do not expand or replace abbreviations. Only fix an abbreviation if it is clearly misheard.
- Do not change grammar, word order, fillers, repetitions or style.
- Do not add, remove, merge or split segments.
- Do not add conclusions, recommendations, facts, explanations or summaries.
- Do not "correct" what a speaker said, even if it seems wrong. Fix only recognition errors.
- If you are not sure, leave the text unchanged.

Input: a JSON object {"context": [...], "segments": [{"id": int, "text": string}]}.
Items in "context" are for understanding only. Do NOT return them.
After the JSON there may be a reference list. "Normal Moldovan mixed speech" lists words that are NOT
errors (for example Russian words written in Latin letters inside Romanian speech, or Romanian words
inside Russian speech): keep them exactly as written. Never copy words from that list into the text
unless they fix a misheard word.
Output: ONLY a JSON array with exactly the same ids as "segments":
[{"id": int, "text": string}]
No markdown, no comments, no other text.
```

- [ ] **Step 2: Три профиля в конфиге**

В `HealthTech/appsettings.json` заменить секцию `Profiles` целиком:

```json
  "Profiles": {
    "Items": {
      "medical": {
        "DisplayName": "Медицинская запись",
        "WhisperPrompt": "Discuție între doi colegi din spital, în română, cu cuvinte rusești printre: ну, короче, давай, ладно. Da, deci ne uităm la analize și vedem ce facem mai departe.",
        "SystemPromptFile": "Prompts/medical_correction.system.txt",
        "Glossaries": [ "medical_glossary.txt", "moldova_speech_glossary.txt" ]
      },
      "administrative": {
        "DisplayName": "Административная запись",
        "WhisperPrompt": "Ședință de lucru în română, cu cuvinte rusești printre: ну, короче, давай, ладно. Deci, discutăm ordinea de zi, termenele și cine răspunde de fiecare punct.",
        "SystemPromptFile": "Prompts/general_correction.system.txt",
        "Glossaries": [ "moldova_speech_glossary.txt" ]
      },
      "financial": {
        "DisplayName": "Финансовая запись",
        "WhisperPrompt": "Discuție financiară în română, cu cuvinte rusești printre: ну, короче, давай, ладно. Deci, ne uităm la buget, facturi, plăți, contract și sume în lei și euro.",
        "SystemPromptFile": "Prompts/general_correction.system.txt",
        "Glossaries": [ "moldova_speech_glossary.txt" ]
      }
    }
  }
```

Список нормальной молдавской речи подключён и к немедицинским типам не случайно: без него корректор «исправляет» русские вкрапления в румынский, а это самая частая порча текста на здешних записях.

- [ ] **Step 3: Проверка**

```sh
taskkill //F //IM HealthTech.exe; dotnet build
```
Expected: `Build succeeded`.

Запустить приложение.
Expected: в логе `Загружено профилей записей: 3 (medical, administrative, financial)`. Если `general_correction.system.txt` не скопировался в вывод, старт упадёт с `Профиль 'administrative': не найден системный промпт ...` — это правильное поведение, но тогда надо проверить сборку `SemanticKernel`.

```sh
curl.exe -s http://localhost:5089/profiles
```
Expected: три элемента с ключами `medical`, `administrative`, `financial` и русскими подписями.

- [ ] **Step 4: Commit**

```bash
git add SemanticKernel/Prompts/general_correction.system.txt HealthTech/appsettings.json
git commit -m "feat: три типа записей - medical, administrative, financial"
```

---

### Task 5: `POST /jobs` — оформление записи и запуск обработки

**Files:**
- Modify: `HealthTech/Audio/AudioProcessingException.cs`
- Modify: `HealthTech/Audio/AudioProcessingExceptionHandler.cs`
- Modify: `HealthTech/Jobs/JobService.cs`
- Modify: `HealthTech/Controllers/JobsController.cs`

**Interfaces:**
- Consumes: `IJobRepository.TrySaveRecordAsync` и `GetAsync` (задача 1); `IProfileCatalog.Get(string)`; ключи профилей из задачи 4; `IWorkflowHost.StartWorkflow`; `TranscriptionJobData` с полями `JobId`, `ProfileKey`, `SourcePath`.
- Produces: `record SaveRecordRequest(Guid FileId, string? Title, int? SpeakersCount, string DiscussionType)`; `IJobService.SaveRecordAsync(SaveRecordRequest, CancellationToken) → Task<Job>`; значения `AudioProcessingError.RecordAlreadyCreated` (409) и `AudioProcessingError.InvalidRequest` (400).

- [ ] **Step 1: Два новых кода ошибки**

В `HealthTech/Audio/AudioProcessingException.cs` в конец перечисления:

```csharp
        UnknownProfile,
        /// <summary>По этому файлу запись уже оформлена.</summary>
        RecordAlreadyCreated,
        /// <summary>Поле запроса не проходит проверку.</summary>
        InvalidRequest
```

- [ ] **Step 2: Отображение в HTTP**

В `AudioProcessingExceptionHandler.TryHandleAsync`, в `switch`:

```csharp
                AudioProcessingError.InputNotFound => StatusCodes.Status404NotFound,
                AudioProcessingError.UnknownProfile or AudioProcessingError.InvalidRequest => StatusCodes.Status400BadRequest,
                AudioProcessingError.RecordAlreadyCreated => StatusCodes.Status409Conflict,
                AudioProcessingError.NotAudio or AudioProcessingError.CorruptedAudio or AudioProcessingError.InvalidTranscript => StatusCodes.Status422UnprocessableEntity,
```

- [ ] **Step 3: Тип запроса**

В `HealthTech/Jobs/JobService.cs`, рядом с `UploadedFile`:

```csharp
    /// <summary>
    /// Оформление записи по загруженному файлу. DiscussionType - ключ профиля
    /// (medical | administrative | financial).
    /// </summary>
    public record SaveRecordRequest(Guid FileId, string? Title, int? SpeakersCount, string DiscussionType);
```

- [ ] **Step 4: Метод интерфейса**

В `IJobService`, после `UploadAsync`:

```csharp
        /// <summary>
        /// Дописывает карточку к загруженному файлу и запускает обработку.
        /// Возвращает карточку сразу, не дожидаясь конца обработки.
        /// </summary>
        Task<Job> SaveRecordAsync(SaveRecordRequest request, CancellationToken cancellationToken = default);
```

- [ ] **Step 5: Реализация**

В `JobService`, после `UploadAsync`:

```csharp
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
```

- [ ] **Step 6: Контроллер**

В `HealthTech/Controllers/JobsController.cs` заменить закомментированный `Create` на:

```csharp
        [HttpPost]
        public Task<Job> Save(
            [FromBody] SaveRecordRequest request,
            [FromServices] IJobService jobService,
            CancellationToken cancellationToken) =>
            jobService.SaveRecordAsync(request, cancellationToken);
```

Запись `JobCreated` в конце файла удалить — она больше не используется.

- [ ] **Step 7: Проверка основного пути и пропавшего файла**

```sh
taskkill //F //IM HealthTech.exe; dotnet build
```
Expected: `Build succeeded`.

Запустить приложение. Загрузить файл и оформить запись:

```sh
curl.exe -s -X POST http://localhost:5089/files -F "file=@assets/input/Medpark_audio_2min.m4a"
curl.exe -s -X POST http://localhost:5089/jobs -H "Content-Type: application/json" -d "{\"fileId\":\"<FILE_ID>\",\"title\":\"Vizita ATI - 26 sept\",\"speakersCount\":4,\"discussionType\":\"medical\"}"
```
Expected: карточка со `"status":"Pending"` или `"Running"`, `"title":"Vizita ATI - 26 sept"`, `"speakersCount":4`, `"profileKey":"medical"`, заполненными `sizeBytes`/`format`/`durationSec`.

Неизвестный тип:

```sh
curl.exe -s -X POST http://localhost:5089/jobs -H "Content-Type: application/json" -d "{\"fileId\":\"<FILE_ID>\",\"discussionType\":\"med_icu\"}"
```
Expected: `400`, в `detail` перечислены `medical, administrative, financial`.

Несуществующий `fileId`:

```sh
curl.exe -s -X POST http://localhost:5089/jobs -H "Content-Type: application/json" -d "{\"fileId\":\"00000000-0000-0000-0000-000000000000\",\"discussionType\":\"medical\"}"
```
Expected: `404`.

Пропавший файл (Review Focus 4): загрузить новый файл, удалить каталог `assets/input/<fileId>/` руками, затем оформить запись.
Expected: `404` с текстом про отсутствующий файл; `GET /jobs` не показывает новую строку, статус в базе остался `Uploaded`, а не `Pending`.

- [ ] **Step 8: Проверка повторного и одновременного оформления**

Повторное оформление уже запущенной записи:

```sh
curl.exe -s -o /dev/null -w "%{http_code}\n" -X POST http://localhost:5089/jobs -H "Content-Type: application/json" -d "{\"fileId\":\"<FILE_ID>\",\"discussionType\":\"medical\"}"
```
Expected: `409`.

Гонка (Review Focus 2): загрузить новый файл и отправить два оформления одновременно:

```sh
curl.exe -s -o /dev/null -w "%{http_code}\n" -X POST http://localhost:5089/jobs -H "Content-Type: application/json" -d "{\"fileId\":\"<NEW_ID>\",\"discussionType\":\"medical\"}" &
curl.exe -s -o /dev/null -w "%{http_code}\n" -X POST http://localhost:5089/jobs -H "Content-Type: application/json" -d "{\"fileId\":\"<NEW_ID>\",\"discussionType\":\"medical\"}" &
wait
```
Expected: ровно один `200` и один `409`. В логе — одна строка `Оформлена запись`, не две.

- [ ] **Step 9: Проверка граничных значений (Review Focus 5)**

```sh
curl.exe -s -o /dev/null -w "%{http_code}\n" -X POST http://localhost:5089/jobs -H "Content-Type: application/json" -d "{\"fileId\":\"<ID>\",\"speakersCount\":0,\"discussionType\":\"medical\"}"
curl.exe -s -o /dev/null -w "%{http_code}\n" -X POST http://localhost:5089/jobs -H "Content-Type: application/json" -d "{\"fileId\":\"<ID>\",\"speakersCount\":500,\"discussionType\":\"medical\"}"
```
Expected: оба `400`, запись остаётся в `Uploaded` и её можно оформить корректным запросом следом.

Пустой заголовок:

```sh
curl.exe -s -X POST http://localhost:5089/jobs -H "Content-Type: application/json" -d "{\"fileId\":\"<ID>\",\"title\":\"   \",\"discussionType\":\"medical\"}"
```
Expected: `200`, в `title` подставлено имя файла.

Отсутствующий `speakersCount` (поле необязательное):
Expected: `200`, `"speakersCount":null`, обработка идёт как обычно.

- [ ] **Step 10: Commit**

```bash
git add HealthTech/Audio/AudioProcessingException.cs HealthTech/Audio/AudioProcessingExceptionHandler.cs HealthTech/Jobs/JobService.cs HealthTech/Controllers/JobsController.cs
git commit -m "feat: POST /jobs оформляет запись по загруженному файлу"
```

---

### Task 6: Документация, примеры запросов и сквозная проверка

**Files:**
- Modify: `HealthTech/HealthTech.http`
- Modify: `CLAUDE.md`

**Interfaces:**
- Consumes: всё, что сделано в задачах 1–5.
- Produces: ничего для кода.

- [ ] **Step 1: Примеры запросов**

`HealthTech/HealthTech.http` целиком:

```http
@HealthTech_HostAddress = http://localhost:5089
@FileId = 00000000-0000-0000-0000-000000000000

### Типы записей для выпадашки: medical | administrative | financial
GET {{HealthTech_HostAddress}}/profiles
Accept: application/json

###
# Фаза 1 - загрузка файла. Multipart из .http-файла неудобен, проще curl:
#
#   curl.exe -X POST http://localhost:5089/files \
#     -F "file=@assets/input/Medpark_audio_2min.m4a"
#
# Ответ: { fileId, fileName, sizeBytes, format, durationSec }.
# Обработка при этом НЕ запускается.

### Фаза 2 - оформление записи. Именно этот запрос запускает конвейер.
POST {{HealthTech_HostAddress}}/jobs
Content-Type: application/json

{
  "fileId": "{{FileId}}",
  "title": "Vizită ATI — 26 sept",
  "speakersCount": 4,
  "discussionType": "medical"
}

### Список записей, новые сверху. Загруженные, но не оформленные файлы сюда не попадают.
GET {{HealthTech_HostAddress}}/jobs
Accept: application/json

### Карточка и состояние: status, currentStep, percent, error
# currentStep внутри долгих шагов уточняется: "Распознавание: чанк 3 из 7".
GET {{HealthTech_HostAddress}}/jobs/{{FileId}}
Accept: application/json

### Результат: транскрипт со спикерами и диалогом
GET {{HealthTech_HostAddress}}/jobs/{{FileId}}/result
Accept: application/json

### Повторная коррекция терминов в уже готовом артефакте записи
# Позволяет крутить глоссарии и промпты, не переплачивая за распознавание.
# Файл ищется в transcripts/<jobId>/; имя — без каталогов, ".json" можно опустить.
POST {{HealthTech_HostAddress}}/audio/correctTranscript?jobId={{FileId}}&fileName=Medpark_audio_2min.speakers.json&profile=medical
Accept: application/json
```

- [ ] **Step 2: `CLAUDE.md`**

Внести четыре правки, не переписывая файл целиком:

1. В «Overview» заменить «A recording is uploaded through the job API, gets its own folder and a record-type profile» на описание двух фаз: файл загружается через `POST /files` и разбирается ffprobe, затем `POST /jobs` оформляет карточку и запускает конвейер.
2. В «Commands» заменить пример `curl` на загрузку в `POST /files` без поля `profile`.
3. В «Storage» дописать к перечню столбцов `Jobs` пять новых и одно предложение про досыпку через `PRAGMA table_info`; в «Jobs and orchestration» — про статус `Uploaded`, про то, что он не подметается стартовой зачисткой, и что `GET /jobs` его скрывает.
4. В «Record profiles» заменить упоминание выбора типа «at upload» на выбор при оформлении записи и перечислить три ключа; в «Controllers» переписать список эндпоинтов под `FilesController` и новый `POST /jobs`.

В разделе «Not built yet» убрать `onco` и `icu` из перечня — этих профилей больше не будет.

- [ ] **Step 3: Сквозная проверка**

```sh
taskkill //F //IM HealthTech.exe; dotnet build
```
Expected: `Build succeeded`.

Запустить приложение и прогнать полный цикл на `Medpark_audio_2min.m4a`: `POST /files` → `POST /jobs` с `discussionType: "medical"` → опрос `GET /jobs/{id}`.

Expected: процент растёт и меняется шаг, включая уточнения внутри распознавания и коррекции; запись доходит до `100% Completed`; в `transcripts/<fileId>/` четыре артефакта (`.json`, `.diarization.json`, `.speakers.json`, `.medical_corrections.md`), а `turns.json` удалён; `GET /jobs/{id}/result` отдаёт транскрипт со спикерами.

Повторить с `discussionType: "administrative"` на том же файле (загрузив его заново).
Expected: запись доходит до `Completed`; в `.medical_corrections.md` видно, что применялся общий промпт — медицинских терминов в правках нет.

- [ ] **Step 4: Commit**

```bash
git add HealthTech/HealthTech.http CLAUDE.md
git commit -m "docs: двухфазный API и три типа записей в описании проекта"
```
