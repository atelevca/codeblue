# План реализации: задания, оркестрация и профили записей

> **Для агентов-исполнителей:** ОБЯЗАТЕЛЬНЫЙ САБ-СКИЛЛ — используйте
> superpowers:subagent-driven-development (рекомендуется) либо
> superpowers:executing-plans, чтобы выполнять план задача за задачей.
> Шаги размечены чекбоксами (`- [ ]`).

**Цель:** загруженный через API файл с выбранным типом записи проходит весь конвейер
как задание с собственным каталогом артефактов, а его прогресс виден в процентах.

**Архитектура:** обработка выносится из HTTP-запроса в workflow на WorkflowCore с
персистентностью в SQLite. Каждое задание получает свой каталог, свою строку в таблице
`Jobs` и свой профиль записи, определяющий initial_prompt для Whisper и набор глоссариев
для LLM-коррекции. Существующие сервисы распознавания, диаризации и коррекции сохраняются
целиком — меняется только то, откуда они узнают путь к файлу.

**Стек:** .NET 10, ASP.NET Core (контроллеры), WorkflowCore 3.21.0 +
WorkflowCore.Persistence.Sqlite 3.21.0, Dapper + Microsoft.Data.Sqlite, Serilog,
Whisper.net 1.9.1, sherpa-onnx 1.13.8, LLamaSharp 0.27.0 + Semantic Kernel.

**Спека:** `docs/superpowers/specs/2026-09-26-transcription-workflow-design.md`

**Охват плана:** этапы 1–3 раздела 12 спеки. Этапы 4–7 (`lowConfidence`, фонетический
список, привязка спикеров, профили `onco` и `icu`) выносятся в отдельный план — этот
должен завершиться работающим срезом.

## Глобальные ограничения

Копируются из спеки и `CLAUDE.md`, действуют на все задачи ниже.

- **Тесты не пишутся.** Ни тестовых проектов, ни тестовых файлов, ни тестового кода.
  Правило проекта из `CLAUDE.md`. Проверка — сборкой и вызовом эндпоинтов.
- **В контроллерах нет логики.** Контроллер принимает запрос, вызывает сервис, возвращает
  результат. Проверки, ветвления, циклы, сбор ошибок и try/catch — в сервисах. Отображение
  доменных исключений в HTTP — в `IExceptionHandler`, не в контроллере.
- **Модели не скачиваются.** Ни Whisper, ни sherpa, ни GGUF. Отсутствие файла — ошибка
  запуска или 503, но не загрузка.
- Версии пакетов WorkflowCore — ровно `3.21.0` у ядра и у провайдера, они обязаны совпадать.
- Все пакеты LLamaSharp обязаны иметь одну версию `0.27.0`.
- Vulkan-специфику (`GGML_VK_VISIBLE_DEVICES`, `WhisperOptions.ResolveGpuDevice`, `run.bat`)
  не углублять: переезд на macOS/Metal должен остаться заменой пакета и одной ветки конфига.
- Относительные пути в конфиге резолвятся от content root (`HealthTech/`), как сейчас.

## На что обратить внимание при проверке

Пять вещей, которые спека подразумевает, но явно не описывает, — именно они ломаются
первыми у живого пользователя. Проверка каждой встроена в задачу, которая владеет кодом.

1. **Файл больше 30 МБ не загрузится.** У Kestrel `MaxRequestBodySize` по умолчанию 30 МБ,
   а рабочая запись `Medpark_audio.m4a` весит 59 МБ. Без поднятия лимита `POST /jobs`
   вернёт 413 на главном сценарии демо. Задача 6.
2. **Имя файла из внешнего мира попадает в путь.** Кириллица, пробелы, `..\`, двоеточие —
   всё это прилетит в `Path.Combine`. Нужен разбор до безопасного имени. Задача 6.
3. **Не-аудио и пустой файл.** Задание обязано стать `Failed` с внятным текстом, а не
   уронить хост workflow и не зависнуть в `Running` навсегда. Задача 7.
4. **Перезапуск приложения во время обработки.** WorkflowCore поднимет инстанс из своей
   базы, но строка в `Jobs` останется в `Running` с прежним процентом. Нужно решение:
   при старте пометить осиротевшие задания. Задача 2.
5. **Неизвестный `profileKey`.** Должен быть 400 с перечнем доступных ключей, а не
   `NullReferenceException` внутри workflow через пять минут после загрузки. Задача 5.

---

## Структура файлов

**Создаются:**

| Файл | Ответственность |
|---|---|
| `HealthTech/Data/SqliteConnectionFactory.cs` | выдача `IDbConnection` с WAL и `busy_timeout` |
| `HealthTech/Data/DatabaseInitializer.cs` | прогон `schema.sql` при старте |
| `HealthTech/Data/schema.sql` | схема трёх таблиц + сид врачей |
| `HealthTech/Jobs/Job.cs` | модель задания и `JobStatus` |
| `HealthTech/Jobs/JobRepository.cs` | доступ к `Jobs` через Dapper |
| `HealthTech/Jobs/JobPaths.cs` | расчёт путей артефактов задания |
| `HealthTech/Jobs/JobService.cs` | создание задания, запуск workflow, чтение статуса |
| `HealthTech/Jobs/JobProgress.cs` | обновление `CurrentStep` и `Percent` из шагов |
| `HealthTech/Workflow/TranscriptionJobData.cs` | данные, которые движок сериализует между шагами |
| `HealthTech/Workflow/TranscriptionWorkflow.cs` | определение workflow |
| `HealthTech/Workflow/Steps/*.cs` | восемь шагов, по файлу на шаг |
| `HealthTech/Profiles/RecordProfile.cs` | профиль записи |
| `HealthTech/Profiles/ProfileOptions.cs` | секция конфига `Profiles` |
| `HealthTech/Profiles/ProfileCatalog.cs` | загрузка и выдача профилей |
| `HealthTech/Controllers/JobsController.cs` | job-API |
| `HealthTech/Controllers/ProfilesController.cs` | список профилей |

**Изменяются:** `Program.cs`, `Audio/AudioProcessor.cs`, `Audio/AudioOptions.cs`,
`Transcription/ProcessedAudioFiles.cs`, три сервиса `ProcessedAudio*Service.cs`,
`WhisperSpeechRecognitionService.cs`, `Controllers/AudioController.cs`,
`SemanticKernel/MedicalCorrection/MedicalTermCorrector.cs`, `appsettings.json`,
`HealthTech.http`, `CLAUDE.md`, `.gitignore`.

**Удаляются:** `Audio/AudioBatchService.cs`.

---

## Задача 1: Слой базы данных

**Файлы:**
- Создать: `HealthTech/Data/SqliteConnectionFactory.cs`, `HealthTech/Data/DatabaseInitializer.cs`,
  `HealthTech/Data/schema.sql`, `HealthTech/Jobs/Job.cs`, `HealthTech/Jobs/JobRepository.cs`
- Изменить: `HealthTech/HealthTech.csproj`, `HealthTech/Program.cs`,
  `HealthTech/appsettings.json`, `.gitignore`

**Интерфейсы:**
- Отдаёт наружу: `IDbConnectionFactory.Create() : IDbConnection`;
  `IJobRepository` с методами `InsertAsync(Job)`, `GetAsync(Guid) : Job?`,
  `ListAsync() : IReadOnlyList<Job>`, `UpdateProgressAsync(Guid, string, int)`,
  `UpdateStatusAsync(Guid, JobStatus, string?)`, `SetWorkflowIdAsync(Guid, string)`,
  `FailRunningAsync(string reason) : int`; запись `Job`; перечисление `JobStatus`.

- [ ] **Шаг 1: Завести ветку**

Мы на `master`, работа большая — отводим ветку.

```bash
git checkout -b feature/transcription-jobs
```

- [ ] **Шаг 2: Добавить пакеты**

```bash
cd HealthTech
dotnet add package Dapper
dotnet add package Microsoft.Data.Sqlite
```

- [ ] **Шаг 3: Создать `HealthTech/Data/schema.sql`**

```sql
-- Схема прикладных таблиц. Идемпотентна: прогоняется при каждом старте.
CREATE TABLE IF NOT EXISTS Jobs (
    Id           TEXT    PRIMARY KEY,
    FileName     TEXT    NOT NULL,
    ProfileKey   TEXT    NOT NULL,
    Status       TEXT    NOT NULL,
    CurrentStep  TEXT    NULL,
    Percent      INTEGER NOT NULL DEFAULT 0,
    WorkflowId   TEXT    NULL,
    Error        TEXT    NULL,
    CreatedAt    TEXT    NOT NULL,
    CompletedAt  TEXT    NULL
);

CREATE TABLE IF NOT EXISTS Persons (
    Id        TEXT PRIMARY KEY,
    FullName  TEXT NOT NULL,
    Specialty TEXT NULL
);

CREATE TABLE IF NOT EXISTS SpeakerBindings (
    JobId        TEXT NOT NULL,
    SpeakerLabel TEXT NOT NULL,
    PersonId     TEXT NOT NULL,
    PRIMARY KEY (JobId, SpeakerLabel)
);

-- Справочник врачей заводится руками; эндпоинта на добавление нет сознательно.
INSERT OR IGNORE INTO Persons (Id, FullName, Specialty) VALUES
    ('11111111-1111-1111-1111-111111111111', 'Ion Popescu',    'Cardiologie'),
    ('22222222-2222-2222-2222-222222222222', 'Maria Ciobanu',  'Oncologie'),
    ('33333333-3333-3333-3333-333333333333', 'Andrei Rusu',    'Terapie intensivă');
```

- [ ] **Шаг 4: Включить копирование `schema.sql` в вывод**

В `HealthTech/HealthTech.csproj` добавить группу:

```xml
<ItemGroup>
  <None Update="Data\schema.sql" CopyToOutputDirectory="PreserveNewest" />
</ItemGroup>
```

- [ ] **Шаг 5: Создать `HealthTech/Data/SqliteConnectionFactory.cs`**

```csharp
using System.Data;
using Microsoft.Data.Sqlite;

namespace HealthTech.Data
{
    public interface IDbConnectionFactory
    {
        /// <summary>Открытое соединение с прикладной базой. Вызывающий обязан его освободить.</summary>
        IDbConnection Create();
    }

    public class SqliteConnectionFactory : IDbConnectionFactory
    {
        private readonly string _connectionString;

        public SqliteConnectionFactory(string databasePath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared
            }.ToString();
        }

        public IDbConnection Create()
        {
            var connection = new SqliteConnection(_connectionString);
            connection.Open();
            // WAL: читающие не блокируются пишущим. busy_timeout: вместо мгновенной
            // "database is locked" соединение ждёт освобождения файла.
            using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
            pragma.ExecuteNonQuery();
            return connection;
        }
    }
}
```

- [ ] **Шаг 6: Создать `HealthTech/Jobs/Job.cs`**

```csharp
namespace HealthTech.Jobs
{
    public enum JobStatus
    {
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
        DateTimeOffset? CompletedAt);
}
```

- [ ] **Шаг 7: Создать `HealthTech/Jobs/JobRepository.cs`**

Dapper не умеет `Guid`/`DateTimeOffset`/`enum` в SQLite «из коробки» так, как нам надо,
поэтому маппинг явный — читаем строки и переводим сами.

```csharp
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
        /// Помечает как Failed задания, оставшиеся в Running после перезапуска приложения.
        /// Возвращает число затронутых строк.
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
            await connection.ExecuteAsync(
                "UPDATE Jobs SET CurrentStep = @CurrentStep, Percent = @Percent, Status = @Status WHERE Id = @Id",
                new { Id = id.ToString(), CurrentStep = currentStep, Percent = percent, Status = nameof(JobStatus.Running) });
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
```

- [ ] **Шаг 8: Создать `HealthTech/Data/DatabaseInitializer.cs`**

```csharp
using Dapper;

namespace HealthTech.Data
{
    /// <summary>Прогоняет schema.sql при старте. Скрипт идемпотентен.</summary>
    public class DatabaseInitializer
    {
        private readonly IDbConnectionFactory _connections;
        private readonly ILogger<DatabaseInitializer> _logger;

        public DatabaseInitializer(IDbConnectionFactory connections, ILogger<DatabaseInitializer> logger)
        {
            _connections = connections;
            _logger = logger;
        }

        public void Initialize()
        {
            var scriptPath = Path.Combine(AppContext.BaseDirectory, "Data", "schema.sql");
            if (!File.Exists(scriptPath))
            {
                throw new InvalidOperationException($"Не найден скрипт схемы '{scriptPath}'.");
            }

            using var connection = _connections.Create();
            connection.Execute(File.ReadAllText(scriptPath));
            _logger.LogInformation("Схема прикладной базы применена из {ScriptPath}", scriptPath);
        }
    }
}
```

- [ ] **Шаг 9: Добавить секцию `Database` в `HealthTech/appsettings.json`**

Рядом с секцией `Transcripts`:

```json
  "Database": {
    "AppDatabasePath": "../data/healthtech.db",
    "WorkflowDatabasePath": "../data/workflow.db"
  }
```

- [ ] **Шаг 10: Зарегистрировать в `HealthTech/Program.cs`**

После регистрации `TranscriptsOptions` и до `builder.Services.AddProblemDetails()`:

```csharp
// Прикладная база (Dapper). Путь относительный — резолвится от content root, как остальные.
var appDatabasePath = Path.GetFullPath(
    builder.Configuration["Database:AppDatabasePath"] ?? "../data/healthtech.db",
    builder.Environment.ContentRootPath);
builder.Services.AddSingleton<IDbConnectionFactory>(new SqliteConnectionFactory(appDatabasePath));
builder.Services.AddSingleton<DatabaseInitializer>();
builder.Services.AddSingleton<IJobRepository, JobRepository>();
```

После `var app = builder.Build();` и до `app.UseExceptionHandler();`:

```csharp
app.Services.GetRequiredService<DatabaseInitializer>().Initialize();

// Пункт 4 списка проверок: после перезапуска инстансы WorkflowCore поднимутся сами,
// но строки Jobs остались бы в Running навсегда. Честнее пометить их упавшими.
var orphaned = await app.Services.GetRequiredService<IJobRepository>()
    .FailRunningAsync("Приложение было перезапущено во время обработки.");
if (orphaned > 0)
{
    app.Logger.LogWarning("Помечено как Failed после перезапуска: {Count} задани(й)", orphaned);
}
```

Добавить `using HealthTech.Data;` и `using HealthTech.Jobs;` в начало файла.

- [ ] **Шаг 11: Исключить базы из репозитория**

В `.gitignore` добавить:

```
# Локальные базы (WorkflowCore и прикладная)
data/
```

- [ ] **Шаг 12: Собрать**

```bash
dotnet build
```
Ожидается: `Build succeeded`, 0 ошибок.

- [ ] **Шаг 13: Запустить и убедиться, что база создалась**

```bash
dotnet run --project HealthTech --launch-profile http
```

В логе должно быть `Схема прикладной базы применена из ...`. Остановить приложение и
проверить файл:

```bash
ls -la data/
```
Ожидается: `healthtech.db` существует. Если приложение не стартовало из-за отсутствующих
моделей Whisper/sherpa — это ожидаемо на пустом `models/`; временно закомментировать
`.ValidateOnStart()` у трёх валидаторов, проверить базу и вернуть обратно.

- [ ] **Шаг 14: Коммит**

```bash
git add HealthTech/Data HealthTech/Jobs HealthTech/HealthTech.csproj \
        HealthTech/Program.cs HealthTech/appsettings.json .gitignore
git commit -m "feat: прикладная база SQLite на Dapper, таблица заданий"
```

---

## Задача 2: Каркас WorkflowCore на двух шагах-заглушках

Цель задачи — зафиксировать реальный API WorkflowCore на минимальном примере, прежде чем
писать восемь настоящих шагов. Заглушки заменяются в задаче 7.

**Файлы:**
- Создать: `HealthTech/Workflow/TranscriptionJobData.cs`,
  `HealthTech/Workflow/TranscriptionWorkflow.cs`,
  `HealthTech/Workflow/Steps/StubStep.cs`, `HealthTech/Jobs/JobProgress.cs`
- Изменить: `HealthTech/HealthTech.csproj`, `HealthTech/Program.cs`

**Интерфейсы:**
- Потребляет: `IJobRepository` из задачи 1.
- Отдаёт наружу: `TranscriptionJobData` со всеми полями из спеки;
  `IJobProgress.ReportAsync(Guid jobId, string step, int percent)`;
  зарегистрированный и запущенный `IWorkflowHost`.

- [ ] **Шаг 1: Добавить пакеты**

```bash
cd HealthTech
dotnet add package WorkflowCore --version 3.21.0
dotnet add package WorkflowCore.Persistence.Sqlite --version 3.21.0
```

- [ ] **Шаг 2: Создать `HealthTech/Workflow/TranscriptionJobData.cs`**

Только строки, `Guid` и мелкий список: движок сериализует это в базу на каждом переходе,
массивы сэмплов здесь недопустимы.

```csharp
namespace HealthTech.Workflow
{
    /// <summary>Отрезок речи в секундах от начала записи. Сериализуется вместе с данными workflow.</summary>
    public class SpeechChunkDto
    {
        public double Start { get; set; }
        public double End { get; set; }
    }

    public class TranscriptionJobData
    {
        public Guid JobId { get; set; }
        public string ProfileKey { get; set; } = "";
        public string SourcePath { get; set; } = "";
        public string NormalizedPath { get; set; } = "";
        public string Wav16kPath { get; set; } = "";
        public List<SpeechChunkDto> Chunks { get; set; } = [];
        public string TranscriptPath { get; set; } = "";
        public string DiarizationPath { get; set; } = "";
        public string SpeakersPath { get; set; } = "";
    }
}
```

- [ ] **Шаг 3: Создать `HealthTech/Jobs/JobProgress.cs`**

```csharp
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
```

- [ ] **Шаг 4: Создать `HealthTech/Workflow/Steps/StubStep.cs`**

Временный шаг. Удаляется в задаче 7.

```csharp
using HealthTech.Jobs;
using WorkflowCore.Interface;
using WorkflowCore.Models;

namespace HealthTech.Workflow.Steps
{
    /// <summary>Заглушка для проверки каркаса: только двигает прогресс. Удаляется в задаче 7.</summary>
    public class StubStep : StepBodyAsync
    {
        private readonly IJobProgress _progress;

        public StubStep(IJobProgress progress)
        {
            _progress = progress;
        }

        public Guid JobId { get; set; }
        public string StepName { get; set; } = "";
        public int Percent { get; set; }

        public override async Task<ExecutionResult> RunAsync(IStepExecutionContext context)
        {
            await _progress.ReportAsync(JobId, StepName, Percent);
            return ExecutionResult.Next();
        }
    }
}
```

- [ ] **Шаг 5: Создать `HealthTech/Workflow/TranscriptionWorkflow.cs`**

```csharp
using HealthTech.Jobs;
using HealthTech.Workflow.Steps;
using WorkflowCore.Interface;

namespace HealthTech.Workflow
{
    public class TranscriptionWorkflow : IWorkflow<TranscriptionJobData>
    {
        public const string WorkflowId = "transcription";

        public string Id => WorkflowId;
        public int Version => 1;

        public void Build(IWorkflowBuilder<TranscriptionJobData> builder)
        {
            builder
                .StartWith<StubStep>()
                    .Input(step => step.JobId, data => data.JobId)
                    .Input(step => step.StepName, data => "Заглушка 1")
                    .Input(step => step.Percent, data => 50)
                .Then<StubStep>()
                    .Input(step => step.JobId, data => data.JobId)
                    .Input(step => step.StepName, data => "Заглушка 2")
                    .Input(step => step.Percent, data => 100);
        }
    }
}
```

- [ ] **Шаг 6: Зарегистрировать WorkflowCore в `HealthTech/Program.cs`**

Перед `builder.Services.AddProblemDetails();`:

```csharp
// Оркестрация. База движка отдельная от прикладной: его фоновый опрос и запросы
// статуса из UI не должны встречаться на блокировке одного файла SQLite.
var workflowDatabasePath = Path.GetFullPath(
    builder.Configuration["Database:WorkflowDatabasePath"] ?? "../data/workflow.db",
    builder.Environment.ContentRootPath);
Directory.CreateDirectory(Path.GetDirectoryName(workflowDatabasePath)!);
builder.Services.AddWorkflow(options => options.UseSqlite(
    $"Data Source={workflowDatabasePath};", canCreateDB: true));

builder.Services.AddSingleton<IJobProgress, JobProgress>();
builder.Services.AddTransient<StubStep>();
```

После `app.Services.GetRequiredService<DatabaseInitializer>().Initialize();`:

```csharp
var workflowHost = app.Services.GetRequiredService<IWorkflowHost>();
workflowHost.RegisterWorkflow<TranscriptionWorkflow, TranscriptionJobData>();
workflowHost.Start();
app.Lifetime.ApplicationStopping.Register(() => workflowHost.Stop());
```

Добавить `using HealthTech.Workflow;`, `using HealthTech.Workflow.Steps;`,
`using WorkflowCore.Interface;`.

- [ ] **Шаг 7: Собрать и выверить API движка**

```bash
dotnet build
```

Если сигнатуры `UseSqlite`, `StepBodyAsync`, `ExecutionResult.Next()` или
`.Input(...)` не совпали с пакетом 3.21.0 — поправить по фактическому API и
**записать рабочий вариант в комментарий** над `Build(...)`: задачи 7 и 8
опираются на этот образец.

Ожидается: `Build succeeded`.

- [ ] **Шаг 8: Проверить запуск движка**

```bash
dotnet run --project HealthTech --launch-profile http
```
Ожидается: приложение стартует, в логе нет исключений WorkflowCore, а после остановки
в `data/` появился `workflow.db` со своими таблицами.

- [ ] **Шаг 9: Коммит**

```bash
git add HealthTech/Workflow HealthTech/Jobs/JobProgress.cs \
        HealthTech/HealthTech.csproj HealthTech/Program.cs
git commit -m "feat: каркас WorkflowCore с персистентностью в отдельной базе SQLite"
```

---

## Задача 3: Пути по заданию и подготовка входа для моделей

**Файлы:**
- Создать: `HealthTech/Jobs/JobPaths.cs`
- Изменить: `HealthTech/Audio/AudioProcessor.cs`, `HealthTech/Audio/AudioOptions.cs`,
  `HealthTech/Controllers/AudioController.cs`
- Удалить: `HealthTech/Audio/AudioBatchService.cs`

**Интерфейсы:**
- Отдаёт наружу: `JobPaths` со свойствами `InputDirectory`, `ProcessedDirectory`,
  `TranscriptsDirectory` для конкретного `jobId`;
  `IAudioProcessor.ProcessAudioAsync(string inputPath, string outputDirectory, CancellationToken)`;
  `IAudioProcessor.PrepareModelInputAsync(string wavPath, CancellationToken) : Task<string>`.

- [ ] **Шаг 1: Создать `HealthTech/Jobs/JobPaths.cs`**

```csharp
using HealthTech.Audio;
using HealthTech.Transcription;
using Microsoft.Extensions.Options;

namespace HealthTech.Jobs
{
    /// <summary>
    /// Каталоги артефактов одного задания. Каждое задание изолировано: две загрузки
    /// подряд не видят файлов друг друга.
    /// </summary>
    public interface IJobPaths
    {
        string InputDirectory(Guid jobId);
        string ProcessedDirectory(Guid jobId);
        string TranscriptsDirectory(Guid jobId);
    }

    public class JobPaths : IJobPaths
    {
        private readonly string _inputRoot;
        private readonly string _processedRoot;
        private readonly string _transcriptsRoot;

        public JobPaths(IOptions<AudioOptions> audio, IOptions<TranscriptsOptions> transcripts, IHostEnvironment environment)
        {
            _inputRoot = Path.GetFullPath(audio.Value.InputDirectory, environment.ContentRootPath);
            _processedRoot = Path.GetFullPath(audio.Value.OutputDirectory, environment.ContentRootPath);
            _transcriptsRoot = transcripts.Value.OutputFolder;
        }

        public string InputDirectory(Guid jobId) => Path.Combine(_inputRoot, jobId.ToString());
        public string ProcessedDirectory(Guid jobId) => Path.Combine(_processedRoot, jobId.ToString());
        public string TranscriptsDirectory(Guid jobId) => Path.Combine(_transcriptsRoot, jobId.ToString());
    }
}
```

- [ ] **Шаг 2: Переписать сигнатуру `IAudioProcessor`**

В `HealthTech/Audio/AudioProcessor.cs` заменить интерфейс:

```csharp
    public interface IAudioProcessor
    {
        /// <summary>
        /// Проверяет, что <paramref name="inputPath"/> содержит аудиопоток, и пишет его WAV-копию
        /// в <paramref name="outputDirectory"/>. Частота и каналы сохраняются как в оригинале.
        /// </summary>
        Task<ProcessedAudio> ProcessAudioAsync(string inputPath, string outputDirectory, CancellationToken cancellationToken = default);

        /// <summary>
        /// Делает рядом с <paramref name="wavPath"/> файл <c>&lt;имя&gt;.16k.wav</c> — 16 кГц моно,
        /// вход для Whisper, sherpa и VAD. Возвращает путь. Исходный WAV не меняется.
        /// </summary>
        Task<string> PrepareModelInputAsync(string wavPath, CancellationToken cancellationToken = default);
    }
```

- [ ] **Шаг 3: Переписать тело `AudioProcessor`**

Убрать поля `_inputDirectory`/`_outputDirectory` и свойства `InputDirectory`/`OutputDirectory`.
Конструктор оставить только с `IOptions<AudioOptions>` и логгером. В `ProcessAudioAsync`
заменить первые строки:

```csharp
        public async Task<ProcessedAudio> ProcessAudioAsync(
            string inputPath, string outputDirectory, CancellationToken cancellationToken = default)
        {
            var fullInputPath = Path.GetFullPath(inputPath);
            _logger.LogInformation("Processing audio file {InputPath}", fullInputPath);

            if (!File.Exists(fullInputPath))
            {
                throw new AudioProcessingException(AudioProcessingError.InputNotFound,
                    $"Input file '{fullInputPath}' does not exist.");
            }

            var probe = await ProbeAsync(fullInputPath, cancellationToken);
            _logger.LogInformation(
                "Detected format {Format}, codec {Codec}, sample rate {SampleRate} Hz, {Channels} channel(s), sample format {SampleFormat}",
                probe.Format, probe.Codec, probe.SampleRate, probe.Channels, probe.SampleFormat);

            var wavPath = Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(fullInputPath) + ".wav");
```

Остальное тело метода не трогать: `_outputDirectory` в нём заменить на `outputDirectory`.

- [ ] **Шаг 4: Добавить `PrepareModelInputAsync` в `AudioProcessor`**

```csharp
        public async Task<string> PrepareModelInputAsync(string wavPath, CancellationToken cancellationToken = default)
        {
            var targetPath = Path.Combine(
                Path.GetDirectoryName(wavPath)!,
                Path.GetFileNameWithoutExtension(wavPath) + ".16k.wav");
            var tempPath = targetPath + ".partial";

            _logger.LogInformation("Preparing model input {TargetPath} (16 kHz mono)", targetPath);
            try
            {
                // Здесь -ar/-ac задаются намеренно: это производный файл для моделей,
                // полноценный WAV рядом остаётся с исходной частотой и каналами.
                var result = await RunAsync(_options.FfmpegPath,
                    ["-nostdin", "-hide_banner", "-loglevel", "error", "-y",
                     "-i", wavPath,
                     "-map", "0:a:0", "-vn", "-sn", "-dn",
                     "-ar", "16000", "-ac", "1", "-c:a", "pcm_s16le",
                     "-f", "wav", tempPath],
                    cancellationToken);

                if (result.ExitCode != 0)
                {
                    throw new AudioProcessingException(AudioProcessingError.ConversionFailed,
                        $"FFmpeg failed to downsample '{wavPath}' (exit code {result.ExitCode}): {result.StdErr.Trim()}");
                }

                File.Move(tempPath, targetPath, overwrite: true);
            }
            finally
            {
                TryDelete(tempPath);
            }

            return targetPath;
        }
```

- [ ] **Шаг 5: Удалить пакетный сервис и его эндпоинт**

```bash
git rm HealthTech/Audio/AudioBatchService.cs
```

В `HealthTech/Program.cs` убрать строку регистрации `IAudioBatchService`.
В `HealthTech/Controllers/AudioController.cs` удалить действие `Run` (`validateAndProcess`)
и `using HealthTech.Audio;`, если он больше не нужен.

- [ ] **Шаг 6: Зарегистрировать `IJobPaths` в `Program.cs`**

Рядом с регистрацией `IJobRepository`:

```csharp
builder.Services.AddSingleton<IJobPaths, JobPaths>();
```

- [ ] **Шаг 7: Собрать**

```bash
dotnet build
```
Ожидается: ошибки компиляции в `ProcessedAudioFiles` и трёх сервисах транскрипции —
они обращаются к удалённым `IAudioProcessor.OutputDirectory`. Это нормально, чинится
в задаче 4. Если других ошибок нет, переходить дальше **не собрав** — задачи 3 и 4
коммитятся вместе.

- [ ] **Шаг 8: Отложить коммит**

Коммит делается в конце задачи 4, когда сборка снова зелёная.

---

## Задача 4: Сервисы транскрипции на явных путях

**Файлы:**
- Изменить: `HealthTech/Transcription/ProcessedAudioFiles.cs`,
  `HealthTech/Transcription/ProcessedAudioTranscriptionService.cs`,
  `HealthTech/Transcription/ProcessedAudioDiarizationService.cs`,
  `HealthTech/Transcription/ProcessedAudioSpeakerTranscriptService.cs`,
  `HealthTech/Transcription/TranscriptCorrectionService.cs`

**Интерфейсы:**
- Потребляет: `IJobPaths`, `IAudioProcessor` из задачи 3.
- Отдаёт наружу:
  `IProcessedAudioFiles.SaveJsonAsync<T>(T result, string outputDirectory, string sourcePath, string suffix, CancellationToken)`,
  `SaveTextAsync(string text, string outputDirectory, string sourcePath, string suffix, CancellationToken)`;
  `IProcessedAudioTranscriptionService.TranscribeAsync(string wavPath, string outputDirectory, IReadOnlyList<SpeechChunk> chunks, CancellationToken)`;
  `IProcessedAudioDiarizationService.DiarizeAsync(string wavPath, string outputDirectory, CancellationToken)`.

**Сигнатура `TranscribeAsync` дорастает в следующих задачах.** Задача 7 добавляет
`string profileKey` пятым параметром, задача 8 — `IProgress<ChunkProgress>? progress`
шестым. Итоговый вид:
`TranscribeAsync(string wavPath, string outputDirectory, IReadOnlyList<SpeechChunk> chunks, string profileKey, IProgress<ChunkProgress>? progress = null, CancellationToken cancellationToken = default)`.

- [ ] **Шаг 1: Убрать поиск файла из `ProcessedAudioFiles`**

Удалить `FindFirst()`, поле `_audioProcessor`, набор `AudioExtensions` и зависимость от
`IAudioProcessor`. Методы сохранения принимают каталог явно:

```csharp
    public interface IProcessedAudioFiles
    {
        /// <summary>Сохраняет <paramref name="result"/> как <c>&lt;каталог&gt;/&lt;имя источника&gt;&lt;суффикс&gt;</c>.</summary>
        Task<string> SaveJsonAsync<T>(T result, string outputDirectory, string sourcePath, string suffix,
            CancellationToken cancellationToken = default);

        /// <summary>Сохраняет <paramref name="text"/> в UTF-8 по тому же правилу имени.</summary>
        Task<string> SaveTextAsync(string text, string outputDirectory, string sourcePath, string suffix,
            CancellationToken cancellationToken = default);
    }
```

В приватном `SaveAsync` заменить `_transcriptsOptions.OutputFolder` на параметр
`outputDirectory`; зависимость от `IOptions<TranscriptsOptions>` из класса убрать.

- [ ] **Шаг 2: Разделить чтение и распознавание в `ProcessedAudioTranscriptionService`**

Метод `TranscribeFirstProcessedAsync` заменить на:

```csharp
        public async Task<TranscriptionResult> TranscribeAsync(
            string wavPath, string outputDirectory, IReadOnlyList<SpeechChunk> chunks,
            CancellationToken cancellationToken = default)
        {
            const int sampleRate = AudioSampleReader.TargetSampleRate;

            var fileName = Path.GetFileName(wavPath);
            var samples = await _sampleReader.ReadMono16kAsync(wavPath, cancellationToken);
            var durationSeconds = Math.Round((double)samples.Length / sampleRate, 2);
            _logger.LogInformation("Transcribing {FileName}, duration {DurationSeconds:F1} s", fileName, durationSeconds);

            var stopwatch = Stopwatch.StartNew();
            var segments = new List<TranscriptSegment>();
            foreach (var chunk in chunks)
            {
                var from = (int)(chunk.Start * sampleRate);
                var to = Math.Min(samples.Length, (int)Math.Ceiling(chunk.End * sampleRate));
                var chunkSamples = samples[from..to];
                if (chunkSamples.Length < MinChunkSamples)
                {
                    Array.Resize(ref chunkSamples, MinChunkSamples);
                }
                var chunkSegments = await _speechRecognition.TranscribeAsync(chunkSamples, cancellationToken);

                segments.AddRange(chunkSegments.Select(s => new TranscriptSegment(
                    Math.Round(chunk.Start + s.Start, 2),
                    Math.Round(Math.Min(chunk.Start + s.End, chunk.End), 2),
                    s.Text)));
            }

            var transcriptionMs = stopwatch.ElapsedMilliseconds;
            _logger.LogInformation("Transcription of {FileName} took {ElapsedMs} ms: {ChunkCount} chunk(s), {SegmentCount} segment(s)",
                fileName, transcriptionMs, chunks.Count, segments.Count);

            var result = new TranscriptionResult(fileName, durationSeconds, segments, transcriptionMs);
            await _files.SaveJsonAsync(result, outputDirectory, wavPath, ".json", cancellationToken);
            return result;
        }
```

Зависимость от `IVoiceActivityService` из этого сервиса убрать — нарезка стала отдельным
шагом workflow. Зависимость от `IProcessedAudioFiles` оставить.

- [ ] **Шаг 3: Перевести `ProcessedAudioDiarizationService` на явный путь**

По той же схеме: метод становится
`DiarizeAsync(string wavPath, string outputDirectory, CancellationToken)`, внутри
`_files.FindFirst()` заменяется на параметр `wavPath`, сохранение — на
`SaveJsonAsync(result, outputDirectory, wavPath, ".diarization.json", ...)`.

- [ ] **Шаг 4: Разобрать `ProcessedAudioSpeakerTranscriptService`**

Сервис перестаёт оркестрировать — это теперь работа workflow. Оставить в нём только чистую
логику выравнивания, превратив её в публичный метод:

```csharp
    public interface ISpeakerAlignmentService
    {
        /// <summary>
        /// Раздаёт сегменты транскрипта говорящим по максимальному перекрытию и склеивает
        /// подряд идущие сегменты одного говорящего в реплики.
        /// </summary>
        List<SpeakerTranscriptTurn> Align(
            IReadOnlyList<TranscriptSegment> segments, IReadOnlyList<DiarizationSegment> speakerTurns);
    }
```

Тело `Align` и `FindSpeaker` перенести без изменений. Методы
`TranscribeWithSpeakersAsync` и `CorrectMedicalTermsAsync` удалить: первый заменяется
цепочкой шагов, второй переезжает в `CorrectTermsStep` в задаче 7.
Файл переименовать в `SpeakerAlignmentService.cs`.

- [ ] **Шаг 5: Поправить `TranscriptCorrectionService`**

Он работает по имени файла в общей папке транскриптов и остаётся как есть по смыслу,
но вызовы сохранения теперь передают каталог:

```csharp
            var correctedPath = await _files.SaveJsonAsync(
                root, Path.GetDirectoryName(path)!, path, ".corrected.json", cancellationToken);
            var reportPath = await _files.SaveTextAsync(
                log.ToMarkdown(), Path.GetDirectoryName(path)!, path, ".medical_corrections.md", cancellationToken);
```

- [ ] **Шаг 6: Обновить регистрации в `Program.cs`**

Заменить `IProcessedAudioSpeakerTranscriptService` на `ISpeakerAlignmentService`.
Удалить из `AudioController` действия `TranscribeProcessed`, `DiarizeProcessed`,
`TranscribeWithSpeakers` — остаётся только `CorrectTranscript`.

- [ ] **Шаг 7: Собрать**

```bash
dotnet build
```
Ожидается: `Build succeeded`, 0 ошибок.

- [ ] **Шаг 8: Коммит задач 3 и 4**

```bash
git add -A
git commit -m "refactor: изоляция артефактов по заданиям, сервисы принимают явные пути"
```

---

## Задача 5: Профили записей

**Файлы:**
- Создать: `HealthTech/Profiles/RecordProfile.cs`, `HealthTech/Profiles/ProfileOptions.cs`,
  `HealthTech/Profiles/ProfileCatalog.cs`, `HealthTech/Controllers/ProfilesController.cs`
- Изменить: `HealthTech/Transcription/WhisperSpeechRecognitionService.cs`,
  `SemanticKernel/MedicalCorrection/MedicalTermCorrector.cs`,
  `SemanticKernel/MedicalCorrection/IMedicalTermCorrector.cs`,
  `HealthTech/appsettings.json`, `HealthTech/Program.cs`

**Интерфейсы:**
- Отдаёт наружу: `RecordProfile`; `IProfileCatalog.Get(string key) : RecordProfile`,
  `IProfileCatalog.All : IReadOnlyList<RecordProfile>`;
  `ISpeechRecognitionService.TranscribeAsync(float[] samples, string prompt, CancellationToken)`;
  `IMedicalTermCorrector.CorrectAsync(IReadOnlyList<Segment>, RecordProfileContent, CorrectionLog, CancellationToken)`.

- [ ] **Шаг 1: Создать тип содержимого профиля в проекте SemanticKernel**

Корректор не должен знать про ASP.NET-конфиг, поэтому получает только то, что использует.
Создать `SemanticKernel/MedicalCorrection/RecordProfileContent.cs`:

```csharp
namespace SemanticKernel.MedicalCorrection
{
    /// <summary>
    /// Всё, что коррекции нужно от профиля записи: системный промпт и разобранные глоссарии
    /// с их заголовками. Загружается один раз при старте.
    /// </summary>
    public record RecordProfileContent(string SystemPrompt, IReadOnlyList<Glossary> Glossaries);
}
```

- [ ] **Шаг 2: Создать `HealthTech/Profiles/RecordProfile.cs`**

```csharp
using SemanticKernel.MedicalCorrection;

namespace HealthTech.Profiles
{
    /// <summary>
    /// Тип записи. Определяет направляющую фразу для Whisper и содержимое LLM-коррекции.
    /// Выбирается пользователем при загрузке файла.
    /// </summary>
    public record RecordProfile(
        string Key,
        string DisplayName,
        string WhisperPrompt,
        RecordProfileContent Content);
}
```

- [ ] **Шаг 3: Создать `HealthTech/Profiles/ProfileOptions.cs`**

```csharp
namespace HealthTech.Profiles
{
    public class ProfileOptions
    {
        public const string SectionName = "Profiles";

        public Dictionary<string, ProfileDefinition> Items { get; set; } = [];
    }

    public class ProfileDefinition
    {
        public string DisplayName { get; set; } = "";

        /// <summary>
        /// initial_prompt для Whisper. whisper.cpp обрезает его примерно на 224 токенах,
        /// поэтому это направляющая фраза, а не словарь.
        /// </summary>
        public string WhisperPrompt { get; set; } = "";

        /// <summary>Путь относительно каталога вывода, например "Prompts/medical_correction.system.txt".</summary>
        public string SystemPromptFile { get; set; } = "";

        /// <summary>Имена файлов в каталоге Glossary/.</summary>
        public List<string> Glossaries { get; set; } = [];
    }
}
```

- [ ] **Шаг 4: Создать `HealthTech/Profiles/ProfileCatalog.cs`**

```csharp
using HealthTech.Audio;
using Microsoft.Extensions.Options;
using SemanticKernel.MedicalCorrection;

namespace HealthTech.Profiles
{
    public interface IProfileCatalog
    {
        IReadOnlyList<RecordProfile> All { get; }

        /// <summary>Профиль по ключу. Неизвестный ключ — <see cref="AudioProcessingException"/> с перечнем доступных.</summary>
        RecordProfile Get(string key);
    }

    /// <summary>
    /// Синглтон: разбирает промпты и глоссарии один раз при старте. medical_glossary.txt —
    /// 850 строк, разбирать его на каждый запрос незачем.
    /// </summary>
    public class ProfileCatalog : IProfileCatalog
    {
        private const string MedicalGlossaryHeading =
            "Reference medical terms (use only to recognize misheard words):";
        private const string SpeechGlossaryHeading =
            "Normal Moldovan mixed speech (NOT errors, keep these words exactly as written; use only to understand the sentence):";

        private readonly Dictionary<string, RecordProfile> _profiles;

        public ProfileCatalog(IOptions<ProfileOptions> options, ILogger<ProfileCatalog> logger)
        {
            _profiles = new Dictionary<string, RecordProfile>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, definition) in options.Value.Items)
            {
                var systemPromptPath = Path.Combine(AppContext.BaseDirectory, definition.SystemPromptFile);
                if (!File.Exists(systemPromptPath))
                {
                    throw new InvalidOperationException(
                        $"Профиль '{key}': не найден системный промпт '{systemPromptPath}'.");
                }

                var glossaries = new List<Glossary>();
                foreach (var file in definition.Glossaries)
                {
                    var path = Path.Combine(AppContext.BaseDirectory, "Glossary", file);
                    if (!File.Exists(path))
                    {
                        throw new InvalidOperationException($"Профиль '{key}': не найден глоссарий '{path}'.");
                    }
                    glossaries.Add(Glossary.Load(path, HeadingFor(file)));
                }

                _profiles[key] = new RecordProfile(key, definition.DisplayName, definition.WhisperPrompt,
                    new RecordProfileContent(File.ReadAllText(systemPromptPath), glossaries));
            }

            if (_profiles.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Секция '{ProfileOptions.SectionName}' пуста: нужен хотя бы один профиль записи.");
            }

            logger.LogInformation("Загружено профилей записей: {Count} ({Keys})",
                _profiles.Count, string.Join(", ", _profiles.Keys));
        }

        public IReadOnlyList<RecordProfile> All => _profiles.Values.ToList();

        public RecordProfile Get(string key) =>
            _profiles.TryGetValue(key ?? "", out var profile)
                ? profile
                : throw new AudioProcessingException(AudioProcessingError.UnknownProfile,
                    $"Неизвестный тип записи '{key}'. Доступные: {string.Join(", ", _profiles.Keys)}.");

        // Список "нормальной речи" помечается иначе: это не термины, а слова, которые нельзя трогать.
        private static string HeadingFor(string fileName) =>
            fileName.Contains("moldova_speech", StringComparison.OrdinalIgnoreCase)
                ? SpeechGlossaryHeading
                : MedicalGlossaryHeading;
    }
}
```

- [ ] **Шаг 5: Добавить код ошибки и отображение в 400**

В `HealthTech/Audio/AudioProcessingException.cs` в перечисление добавить `UnknownProfile`.
В `HealthTech/Audio/AudioProcessingExceptionHandler.cs` в `switch` добавить ветку:

```csharp
                AudioProcessingError.UnknownProfile => StatusCodes.Status400BadRequest,
```

Это пункт 5 списка проверок: неизвестный ключ отвечает 400 с перечнем доступных,
а не падает внутри workflow.

- [ ] **Шаг 6: Передавать промпт в Whisper параметром**

В `HealthTech/Transcription/WhisperSpeechRecognitionService.cs` изменить интерфейс:

```csharp
    public interface ISpeechRecognitionService
    {
        /// <summary>
        /// Распознаёт 16 кГц моно в [-1, 1]. <paramref name="prompt"/> — направляющая фраза
        /// профиля; пустая строка означает «без промпта».
        /// </summary>
        Task<List<TranscriptSegment>> TranscribeAsync(float[] samples, string prompt, CancellationToken cancellationToken = default);
    }
```

В теле заменить `_options.Prompt` на параметр:

```csharp
                if (!string.IsNullOrWhiteSpace(prompt))
                {
                    builder.WithPrompt(prompt);
                }
```

`WhisperOptions.Prompt` оставить в классе — он становится запасным значением для профилей,
у которых `WhisperPrompt` пуст.

- [ ] **Шаг 7: Передавать профиль в корректор**

В `SemanticKernel/MedicalCorrection/IMedicalTermCorrector.cs` заменить обе перегрузки одной:

```csharp
        /// <summary>
        /// Исправляет вероятные ошибки распознавания в терминах. Количество, порядок, идентификаторы,
        /// говорящие и метки времени сохраняются; меняться может только <see cref="Segment.Text"/>.
        /// При любом сомнении или сбое остаётся исходный текст.
        /// </summary>
        /// <param name="profile">Системный промпт и глоссарии выбранного типа записи.</param>
        /// <param name="log">Принимает каждую правку, принятую и отклонённую.</param>
        Task<IReadOnlyList<Segment>> CorrectAsync(
            IReadOnlyList<Segment> segments, RecordProfileContent profile, CorrectionLog log,
            CancellationToken ct = default);
```

В `MedicalTermCorrector`: удалить константы `SystemPromptFile`, `MedicalGlossaryFile`,
`SpeechGlossaryFile`, `MedicalGlossaryHeading`, `SpeechGlossaryHeading`, поля `_systemPrompt`
и `_glossaries` и всю загрузку файлов из конструктора. Конструктор оставляет
`IChatCompletionProvider`, `IOptions<LlmOptions>`, логгер.

В `CorrectAsync` использовать `profile.SystemPrompt` вместо `_systemPrompt`.
`BuildUserMessage` получает профиль параметром и перебирает `profile.Glossaries`
вместо `_glossaries`; `RequestCorrectionsAsync` тоже принимает профиль и строит
`new ChatHistory(profile.SystemPrompt)`.

- [ ] **Шаг 8: Поправить вызов в `TranscriptCorrectionService`**

Он получает `IProfileCatalog` и параметр профиля:

```csharp
        public async Task<TranscriptCorrectionResult> CorrectTranscriptAsync(
            string fileName, string profileKey, CancellationToken cancellationToken = default)
        {
            // Бросает UnknownProfile → 400 до чтения файла.
            var profile = _profiles.Get(profileKey);

            // ... существующее тело без изменений: ResolvePath, ReadAsync, FindItems,
            // сборка segments, создание CorrectionLog ...

            var corrected = await _corrector.CorrectAsync(segments, profile.Content, log, cancellationToken);
```

В `AudioController` действие получает второй параметр запроса:

```csharp
        [HttpPost("correctTranscript")]
        public Task<TranscriptCorrectionResult> CorrectTranscript(
            [FromQuery] string fileName,
            [FromQuery] string profile,
            [FromServices] ITranscriptCorrectionService transcriptCorrectionService,
            CancellationToken cancellationToken) =>
            transcriptCorrectionService.CorrectTranscriptAsync(fileName, profile, cancellationToken);
```

- [ ] **Шаг 9: Создать `HealthTech/Controllers/ProfilesController.cs`**

```csharp
using HealthTech.Profiles;
using Microsoft.AspNetCore.Mvc;

namespace HealthTech.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class ProfilesController : ControllerBase
    {
        [HttpGet]
        public IReadOnlyList<ProfileView> List([FromServices] IProfileCatalog catalog) =>
            catalog.All.Select(p => new ProfileView(p.Key, p.DisplayName)).ToList();
    }

    public record ProfileView(string Key, string DisplayName);
}
```

Логики в контроллере нет: проекция в две строки — часть контракта API.
Если она разрастётся, переехать в сервис.

- [ ] **Шаг 10: Описать профиль в `HealthTech/appsettings.json`**

Секцию `Whisper:Prompt` оставить как есть (запасное значение). Добавить:

```json
  "Profiles": {
    "Items": {
      "consilium": {
        "DisplayName": "Консилиум (общий)",
        "WhisperPrompt": "Discuție între doi colegi din spital, în română, cu cuvinte rusești printre: ну, короче, давай, ладно. Da, deci ne uităm la analize și vedem ce facem mai departe.",
        "SystemPromptFile": "Prompts/medical_correction.system.txt",
        "Glossaries": [ "medical_glossary.txt", "moldova_speech_glossary.txt" ]
      }
    }
  }
```

- [ ] **Шаг 11: Зарегистрировать в `Program.cs`**

```csharp
builder.Services.Configure<ProfileOptions>(builder.Configuration.GetSection(ProfileOptions.SectionName));
builder.Services.AddSingleton<IProfileCatalog, ProfileCatalog>();
```

- [ ] **Шаг 12: Собрать**

```bash
dotnet build
```
Ожидается: `Build succeeded`.

- [ ] **Шаг 13: Проверить список профилей**

Запустить приложение и выполнить:

```bash
curl.exe -s http://localhost:5089/profiles
```
Ожидается: `[{"key":"consilium","displayName":"Консилиум (общий)"}]`.
В логе старта: `Загружено профилей записей: 1 (consilium)`.

- [ ] **Шаг 14: Проверить неизвестный профиль**

```bash
curl.exe -s -i "http://localhost:5089/audio/correctTranscript?fileName=x.json&profile=nope"
```
Ожидается: HTTP 400 и ProblemDetails с текстом «Неизвестный тип записи 'nope'. Доступные: consilium.»
(пункт 5 списка проверок).

- [ ] **Шаг 15: Коммит**

```bash
git add -A
git commit -m "feat: профили записей с собственным промптом и набором глоссариев"
```

---

## Задача 6: Job API и загрузка файла

**Файлы:**
- Создать: `HealthTech/Jobs/JobService.cs`, `HealthTech/Jobs/SafeFileName.cs`,
  `HealthTech/Controllers/JobsController.cs`
- Изменить: `HealthTech/Program.cs`, `HealthTech/appsettings.json`

**Интерфейсы:**
- Потребляет: `IJobRepository`, `IJobPaths`, `IProfileCatalog`, `IWorkflowHost`.
- Отдаёт наружу: `IJobService.CreateAsync(Stream content, string fileName, string profileKey, CancellationToken) : Task<Guid>`,
  `GetAsync(Guid) : Task<Job?>`, `ListAsync() : Task<IReadOnlyList<Job>>`,
  `GetResultAsync(Guid) : Task<string?>`.

- [ ] **Шаг 1: Создать `HealthTech/Jobs/SafeFileName.cs`**

Пункт 2 списка проверок: имя приходит из внешнего мира и попадает в путь.

```csharp
using System.Text.RegularExpressions;

namespace HealthTech.Jobs
{
    /// <summary>
    /// Приводит присланное клиентом имя файла к безопасному: никаких каталогов, никаких
    /// символов, недопустимых в путях. Кириллица и пробелы сохраняются — они законны.
    /// </summary>
    public static partial class SafeFileName
    {
        private const int MaxLength = 100;

        public static string Sanitize(string? fileName)
        {
            // GetFileName отсекает "../" и "C:\": на выходе остаётся только последний сегмент.
            var name = Path.GetFileName(fileName ?? "").Trim();
            name = Unsafe().Replace(name, "_");

            if (name.Length > MaxLength)
            {
                var extension = Path.GetExtension(name);
                name = string.Concat(name.AsSpan(0, MaxLength - extension.Length), extension);
            }

            return name.Length == 0 || name.All(c => c == '.' || c == '_') ? "upload" : name;
        }

        [GeneratedRegex(@"[\x00-\x1f<>:""/\\|?*]")]
        private static partial Regex Unsafe();
    }
}
```

- [ ] **Шаг 2: Создать `HealthTech/Jobs/JobService.cs`**

```csharp
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
            // Бросает UnknownProfile → 400 до того, как что-либо будет записано на диск.
            var profile = _profiles.Get(profileKey);

            var jobId = Guid.NewGuid();
            var safeName = SafeFileName.Sanitize(fileName);
            var inputDirectory = _paths.InputDirectory(jobId);
            var sourcePath = Path.Combine(inputDirectory, safeName);

            try
            {
                Directory.CreateDirectory(inputDirectory);
                await using var file = File.Create(sourcePath);
                await content.CopyToAsync(file, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new AudioProcessingException(AudioProcessingError.FileSystemError,
                    $"Не удалось сохранить загруженный файл в '{sourcePath}': {ex.Message}", ex);
            }

            if (new FileInfo(sourcePath).Length == 0)
            {
                throw new AudioProcessingException(AudioProcessingError.NotAudio,
                    "Загруженный файл пуст.");
            }

            await _jobs.InsertAsync(new Job(
                jobId, safeName, profile.Key, JobStatus.Pending, null, 0, null, null,
                DateTimeOffset.UtcNow, null), cancellationToken);

            var workflowId = await _workflow.StartWorkflow(TranscriptionWorkflow.WorkflowId, new TranscriptionJobData
            {
                JobId = jobId,
                ProfileKey = profile.Key,
                SourcePath = sourcePath
            });
            await _jobs.SetWorkflowIdAsync(jobId, workflowId, cancellationToken);

            _logger.LogInformation("Создано задание {JobId} ({FileName}, профиль {Profile}), workflow {WorkflowId}",
                jobId, safeName, profile.Key, workflowId);
            return jobId;
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
```

- [ ] **Шаг 3: Создать `HealthTech/Controllers/JobsController.cs`**

```csharp
using HealthTech.Jobs;
using Microsoft.AspNetCore.Mvc;

namespace HealthTech.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class JobsController : ControllerBase
    {
        [HttpPost]
        public async Task<JobCreated> Create(
            IFormFile file,
            [FromForm] string profile,
            [FromServices] IJobService jobService,
            CancellationToken cancellationToken)
        {
            await using var stream = file.OpenReadStream();
            var jobId = await jobService.CreateAsync(stream, file.FileName, profile, cancellationToken);
            return new JobCreated(jobId);
        }

        [HttpGet]
        public Task<IReadOnlyList<Job>> List(
            [FromServices] IJobService jobService, CancellationToken cancellationToken) =>
            jobService.ListAsync(cancellationToken);

        [HttpGet("{id:guid}")]
        public async Task<ActionResult<Job>> Get(
            Guid id, [FromServices] IJobService jobService, CancellationToken cancellationToken)
        {
            var job = await jobService.GetAsync(id, cancellationToken);
            return job is null ? NotFound() : job;
        }

        [HttpGet("{id:guid}/result")]
        public async Task<ActionResult> GetResult(
            Guid id, [FromServices] IJobService jobService, CancellationToken cancellationToken)
        {
            var result = await jobService.GetResultAsync(id, cancellationToken);
            return result is null ? NotFound() : Content(result, "application/json");
        }
    }

    public record JobCreated(Guid JobId);
}
```

- [ ] **Шаг 4: Поднять лимит размера загрузки**

Пункт 1 списка проверок: у Kestrel потолок 30 МБ, рабочая запись — 59 МБ.
В `Program.cs` перед `builder.Services.AddControllers();`:

```csharp
// Записи консилиумов бывают на час и больше: Medpark_audio.m4a весит 59 МБ,
// а умолчание Kestrel — 30 МБ. Без этого POST /jobs отвечает 413.
var maxUploadBytes = builder.Configuration.GetValue("Uploads:MaxBytes", 1_073_741_824L);
builder.Services.Configure<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>(
    options => options.Limits.MaxRequestBodySize = maxUploadBytes);
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(
    options => options.MultipartBodyLengthLimit = maxUploadBytes);
```

В `appsettings.json` добавить:

```json
  "Uploads": {
    "MaxBytes": 1073741824
  }
```

- [ ] **Шаг 5: Зарегистрировать сервис**

```csharp
builder.Services.AddSingleton<IJobService, JobService>();
```

- [ ] **Шаг 6: Собрать**

```bash
dotnet build
```
Ожидается: `Build succeeded`.

- [ ] **Шаг 7: Загрузить маленький файл и увидеть заглушечный конвейер**

Запустить приложение, затем:

```bash
curl.exe -s -X POST http://localhost:5089/jobs \
  -F "file=@assets/input/Medpark_audio_2min.m4a" \
  -F "profile=consilium"
```
Ожидается: `{"jobId":"<guid>"}`. Затем:

```bash
curl.exe -s http://localhost:5089/jobs/<guid>
```
Ожидается: `status` дошёл до `Running`, `percent` равен 100, `currentStep` — «Заглушка 2»
(настоящие шаги появятся в задаче 7). В `assets/input/<guid>/` лежит загруженный файл.

- [ ] **Шаг 8: Загрузить большой файл**

```bash
curl.exe -s -X POST http://localhost:5089/jobs \
  -F "file=@assets/input/Medpark_audio.m4a" \
  -F "profile=consilium"
```
Ожидается: `{"jobId":"..."}`, а не 413. Это проверка пункта 1.

- [ ] **Шаг 9: Проверить обработку имени файла**

```bash
curl.exe -s -X POST http://localhost:5089/jobs \
  -F "file=@assets/input/Medpark_audio_2min.m4a;filename=../../evil:name.m4a" \
  -F "profile=consilium"
```
Ожидается: задание создано, а файл лежит внутри `assets/input/<guid>/` под именем
`evil_name.m4a` — за пределы каталога задания ничего не вышло. Это проверка пункта 2.

- [ ] **Шаг 10: Коммит**

```bash
git add -A
git commit -m "feat: загрузка файла, создание задания и запуск обработки"
```

---

## Задача 7: Восемь настоящих шагов workflow

**Файлы:**
- Создать: `HealthTech/Workflow/Steps/NormalizeAudioStep.cs`,
  `PrepareModelInputStep.cs`, `DetectSpeechChunksStep.cs`, `TranscribeStep.cs`,
  `DiarizeStep.cs`, `AlignSpeakersStep.cs`, `CorrectTermsStep.cs`, `SaveResultStep.cs`
- Изменить: `HealthTech/Workflow/TranscriptionWorkflow.cs`, `HealthTech/Program.cs`
- Удалить: `HealthTech/Workflow/Steps/StubStep.cs`

**Интерфейсы:**
- Потребляет: `IAudioProcessor`, `IAudioSampleReader`, `IVoiceActivityService`,
  `IProcessedAudioTranscriptionService`, `IProcessedAudioDiarizationService`,
  `ISpeakerAlignmentService`, `IMedicalTermCorrector`, `IProfileCatalog`,
  `IJobProgress`, `IJobRepository`, `IJobPaths`, `IProcessedAudioFiles`.
- Отдаёт наружу: готовый `<имя>.speakers.json` в каталоге транскриптов задания.

- [ ] **Шаг 1: Завести общий базовый класс шага**

Создать `HealthTech/Workflow/Steps/JobStep.cs`. Он снимает дублирование: у каждого шага
одинаковые «доложить прогресс» и «перевести задание в Failed при исключении».

```csharp
using HealthTech.Audio;
using HealthTech.Jobs;
using SemanticKernel;
using WorkflowCore.Interface;
using WorkflowCore.Models;

namespace HealthTech.Workflow.Steps
{
    /// <summary>
    /// Общее поведение шага задания: отметить прогресс, выполнить работу, а при падении
    /// перевести задание в Failed с внятным текстом вместо зависания в Running.
    /// </summary>
    public abstract class JobStep : StepBodyAsync
    {
        protected JobStep(IJobProgress progress, IJobRepository jobs, ILogger logger)
        {
            Progress = progress;
            Jobs = jobs;
            Logger = logger;
        }

        protected IJobProgress Progress { get; }
        protected IJobRepository Jobs { get; }
        protected ILogger Logger { get; }

        public Guid JobId { get; set; }

        /// <summary>Подпись шага для UI.</summary>
        protected abstract string StepName { get; }

        /// <summary>Границы полосы шага: процент на входе и процент по завершении.</summary>
        protected abstract int PercentAtStart { get; }
        protected abstract int PercentWhenDone { get; }

        protected abstract Task ExecuteAsync(IStepExecutionContext context);

        public override async Task<ExecutionResult> RunAsync(IStepExecutionContext context)
        {
            await Progress.ReportAsync(JobId, StepName, PercentAtStart);
            try
            {
                await ExecuteAsync(context);
            }
            catch (Exception ex) when (ex is AudioProcessingException or LlmModelNotFoundException)
            {
                Logger.LogWarning(ex, "Задание {JobId} упало на шаге {Step}", JobId, StepName);
                await Jobs.UpdateStatusAsync(JobId, JobStatus.Failed, ex.Message);
                return ExecutionResult.Next();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Задание {JobId}: непредвиденная ошибка на шаге {Step}", JobId, StepName);
                await Jobs.UpdateStatusAsync(JobId, JobStatus.Failed, $"{StepName}: {ex.Message}");
                return ExecutionResult.Next();
            }

            await Progress.ReportAsync(JobId, StepName, PercentWhenDone);
            return ExecutionResult.Next();
        }
    }
}
```

**Важно про обработку ошибок.** Шаг возвращает `Next()` и после падения, чтобы дать
цепочке дойти до конца без ретраев движка. Последующие шаги обязаны проверять, что
задание ещё не `Failed`, и в этом случае ничего не делать. Добавить в `JobStep`:

```csharp
        protected async Task<bool> IsFailedAsync() =>
            (await Jobs.GetAsync(JobId))?.Status == JobStatus.Failed;
```

и первой строкой каждого `ExecuteAsync` — `if (await IsFailedAsync()) return;`.

Туда же — общие помощники чтения и записи JSON, они нужны трём последним шагам:

```csharp
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        protected static async Task<T> ReadJsonAsync<T>(string path)
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<T>(stream, Json)
                   ?? throw new InvalidOperationException($"Пустой или нечитаемый файл '{path}'.");
        }

        protected static async Task WriteJsonAsync<T>(string path, T value)
        {
            await using var stream = File.Create(path);
            await JsonSerializer.SerializeAsync(stream, value, Json);
        }
```

С `using System.Text.Json;` в начале файла.

- [ ] **Шаг 2: `NormalizeAudioStep` (полоса 0→5)**

```csharp
using HealthTech.Audio;
using HealthTech.Jobs;
using WorkflowCore.Interface;

namespace HealthTech.Workflow.Steps
{
    public class NormalizeAudioStep : JobStep
    {
        private readonly IAudioProcessor _audio;
        private readonly IJobPaths _paths;

        public NormalizeAudioStep(
            IAudioProcessor audio, IJobPaths paths,
            IJobProgress progress, IJobRepository jobs, ILogger<NormalizeAudioStep> logger)
            : base(progress, jobs, logger)
        {
            _audio = audio;
            _paths = paths;
        }

        public string SourcePath { get; set; } = "";
        public string NormalizedPath { get; set; } = "";

        protected override string StepName => "Нормализация аудио";
        protected override int PercentAtStart => 0;
        protected override int PercentWhenDone => 5;

        protected override async Task ExecuteAsync(IStepExecutionContext context)
        {
            if (await IsFailedAsync()) return;

            var outputDirectory = _paths.ProcessedDirectory(JobId);
            Directory.CreateDirectory(outputDirectory);
            var processed = await _audio.ProcessAudioAsync(SourcePath, outputDirectory);
            NormalizedPath = processed.WavPath;
        }
    }
}
```

- [ ] **Шаг 3: `PrepareModelInputStep` (полоса 5→10)**

Тот же каркас. Поля `NormalizedPath` (вход) и `Wav16kPath` (выход), тело:

```csharp
            if (await IsFailedAsync()) return;
            Wav16kPath = await _audio.PrepareModelInputAsync(NormalizedPath);
```

`StepName` — `"Подготовка входа для моделей"`, `PercentAtStart` — 5, `PercentWhenDone` — 10.

- [ ] **Шаг 4: `DetectSpeechChunksStep` (полоса 10→15)**

Поля `Wav16kPath` (вход) и `Chunks` (выход, `List<SpeechChunkDto>`), тело:

```csharp
            if (await IsFailedAsync()) return;

            var samples = await _sampleReader.ReadMono16kAsync(Wav16kPath);
            Chunks = _voiceActivity.DetectChunks(samples)
                .Select(c => new SpeechChunkDto { Start = c.Start, End = c.End })
                .ToList();
```

`StepName` — `"Поиск речи"`, `PercentAtStart` — 10, `PercentWhenDone` — 15.

- [ ] **Шаг 5: `TranscribeStep` (полоса 15→55)**

Поля `Wav16kPath`, `ProfileKey`, `Chunks` (вход), `TranscriptPath` (выход):

```csharp
            if (await IsFailedAsync()) return;

            var outputDirectory = _paths.TranscriptsDirectory(JobId);
            Directory.CreateDirectory(outputDirectory);
            var chunks = Chunks.Select(c => new SpeechChunk(c.Start, c.End)).ToList();

            await _transcription.TranscribeAsync(Wav16kPath, outputDirectory, chunks, ProfileKey);
            TranscriptPath = Path.Combine(outputDirectory,
                Path.GetFileNameWithoutExtension(Wav16kPath) + ".json");
```

`StepName` — `"Распознавание речи"`, `PercentAtStart` — 15, `PercentWhenDone` — 55.

Сигнатуру `TranscribeAsync` из задачи 4 расширить четвёртым параметром `profileKey`,
внутри — получить промпт через `IProfileCatalog` и передать в
`_speechRecognition.TranscribeAsync(chunkSamples, prompt, cancellationToken)`.

- [ ] **Шаг 6: `DiarizeStep` (полоса 55→75)**

Поля `Wav16kPath` (вход), `DiarizationPath` (выход):

```csharp
            if (await IsFailedAsync()) return;

            var outputDirectory = _paths.TranscriptsDirectory(JobId);
            await _diarization.DiarizeAsync(Wav16kPath, outputDirectory);
            DiarizationPath = Path.Combine(outputDirectory,
                Path.GetFileNameWithoutExtension(Wav16kPath) + ".diarization.json");
```

`StepName` — `"Разделение по говорящим"`, `PercentAtStart` — 55, `PercentWhenDone` — 75.

- [ ] **Шаг 7: `AlignSpeakersStep` (полоса 75→78)**

Три последних шага обмениваются репликами через файл `turns.json` в каталоге задания,
а не через данные workflow: движок сериализует их в базу на каждом переходе, а реплики
длинной записи там неуместны.

```csharp
using HealthTech.Jobs;
using HealthTech.Transcription;
using WorkflowCore.Interface;

namespace HealthTech.Workflow.Steps
{
    public class AlignSpeakersStep : JobStep
    {
        private readonly ISpeakerAlignmentService _alignment;
        private readonly IJobPaths _paths;

        public AlignSpeakersStep(
            ISpeakerAlignmentService alignment, IJobPaths paths,
            IJobProgress progress, IJobRepository jobs, ILogger<AlignSpeakersStep> logger)
            : base(progress, jobs, logger)
        {
            _alignment = alignment;
            _paths = paths;
        }

        public string TranscriptPath { get; set; } = "";
        public string DiarizationPath { get; set; } = "";

        protected override string StepName => "Выравнивание по говорящим";
        protected override int PercentAtStart => 75;
        protected override int PercentWhenDone => 78;

        protected override async Task ExecuteAsync(IStepExecutionContext context)
        {
            if (await IsFailedAsync()) return;

            var transcript = await ReadJsonAsync<TranscriptionResult>(TranscriptPath);
            var diarization = await ReadJsonAsync<DiarizationResult>(DiarizationPath);

            var turns = _alignment.Align(transcript.Segments, diarization.Segments);
            await WriteJsonAsync(TurnsPath(_paths, JobId), turns);

            Logger.LogInformation("Задание {JobId}: {SegmentCount} сегмент(ов) сведены в {TurnCount} реплик(у)",
                JobId, transcript.Segments.Count, turns.Count);
        }

        /// <summary>Промежуточный файл реплик. Удаляется последним шагом.</summary>
        public static string TurnsPath(IJobPaths paths, Guid jobId) =>
            Path.Combine(paths.TranscriptsDirectory(jobId), "turns.json");
    }
}
```

- [ ] **Шаг 8: `CorrectTermsStep` (полоса 78→98)**

```csharp
using HealthTech.Jobs;
using HealthTech.Profiles;
using HealthTech.Transcription;
using SemanticKernel.MedicalCorrection;
using WorkflowCore.Interface;

namespace HealthTech.Workflow.Steps
{
    public class CorrectTermsStep : JobStep
    {
        private readonly IMedicalTermCorrector _corrector;
        private readonly IProfileCatalog _profiles;
        private readonly IProcessedAudioFiles _files;
        private readonly IJobPaths _paths;

        public CorrectTermsStep(
            IMedicalTermCorrector corrector, IProfileCatalog profiles,
            IProcessedAudioFiles files, IJobPaths paths,
            IJobProgress progress, IJobRepository jobs, ILogger<CorrectTermsStep> logger)
            : base(progress, jobs, logger)
        {
            _corrector = corrector;
            _profiles = profiles;
            _files = files;
            _paths = paths;
        }

        public string ProfileKey { get; set; } = "";

        protected override string StepName => "Коррекция терминов";
        protected override int PercentAtStart => 78;
        protected override int PercentWhenDone => 98;

        protected override async Task ExecuteAsync(IStepExecutionContext context)
        {
            if (await IsFailedAsync()) return;

            var turnsPath = AlignSpeakersStep.TurnsPath(_paths, JobId);
            var turns = await ReadJsonAsync<List<SpeakerTranscriptTurn>>(turnsPath);

            // Индекс реплики + 1 = идентификатор сегмента: по нему правка находит себя обратно.
            var segments = turns
                .Select((t, i) => new Segment(i + 1, t.Speaker,
                    TimeSpan.FromSeconds(t.Start), TimeSpan.FromSeconds(t.End), t.Text))
                .ToList();

            var profile = _profiles.Get(ProfileKey);
            var log = new CorrectionLog();
            var corrected = await _corrector.CorrectAsync(segments, profile.Content, log);

            var directory = _paths.TranscriptsDirectory(JobId);
            await _files.SaveTextAsync(log.ToMarkdown(), directory, turnsPath, ".medical_corrections.md");

            var updated = turns.Select((t, i) => t with { Text = corrected[i].Text }).ToList();
            await WriteJsonAsync(turnsPath, updated);
        }
    }
}
```

Отчёт получит имя `turns.medical_corrections.md`. Этого достаточно: задание изолировано
в своём каталоге, путать не с чем.

- [ ] **Шаг 9: `SaveResultStep` (полоса 98→100)**

```csharp
using HealthTech.Jobs;
using HealthTech.Transcription;
using WorkflowCore.Interface;

namespace HealthTech.Workflow.Steps
{
    public class SaveResultStep : JobStep
    {
        private readonly IProcessedAudioFiles _files;
        private readonly IJobPaths _paths;

        public SaveResultStep(
            IProcessedAudioFiles files, IJobPaths paths,
            IJobProgress progress, IJobRepository jobs, ILogger<SaveResultStep> logger)
            : base(progress, jobs, logger)
        {
            _files = files;
            _paths = paths;
        }

        public string TranscriptPath { get; set; } = "";
        public string DiarizationPath { get; set; } = "";
        public string SpeakersPath { get; set; } = "";

        protected override string StepName => "Сохранение результата";
        protected override int PercentAtStart => 98;
        protected override int PercentWhenDone => 100;

        protected override async Task ExecuteAsync(IStepExecutionContext context)
        {
            if (await IsFailedAsync()) return;

            var turnsPath = AlignSpeakersStep.TurnsPath(_paths, JobId);
            var turns = await ReadJsonAsync<List<SpeakerTranscriptTurn>>(turnsPath);
            var transcript = await ReadJsonAsync<TranscriptionResult>(TranscriptPath);
            var diarization = await ReadJsonAsync<DiarizationResult>(DiarizationPath);

            var result = new SpeakerTranscriptResult(
                transcript.FileName,
                transcript.DurationSeconds,
                turns.Select(t => t.Speaker).Distinct().ToList(),
                turns,
                TranscriptDialogue.Format(turns.Select(t => (t.Speaker, t.Text))),
                transcript.TranscriptionMs,
                diarization.DiarizationMs);

            // Имя берётся от TranscriptPath, а не от turns.json: получится <имя записи>.speakers.json.
            var directory = _paths.TranscriptsDirectory(JobId);
            SpeakersPath = await _files.SaveJsonAsync(result, directory, TranscriptPath, ".speakers.json");

            File.Delete(turnsPath);
            await Jobs.UpdateStatusAsync(JobId, JobStatus.Completed, null);
        }
    }
}
```

Чтобы `TranscriptDialogue.Format` был виден из шага, в
`HealthTech/Transcription/TranscriptModels.cs` поменять
`internal static class TranscriptDialogue` на `public static class TranscriptDialogue`.

`SaveResultStep` нужен `DiarizationPath`, поэтому в `TranscriptionWorkflow` он получает
его входом наравне с `TranscriptPath` — см. следующий шаг.

- [ ] **Шаг 10: Переписать `TranscriptionWorkflow`**

```csharp
        public void Build(IWorkflowBuilder<TranscriptionJobData> builder)
        {
            builder
                .StartWith<NormalizeAudioStep>()
                    .Input(s => s.JobId, d => d.JobId)
                    .Input(s => s.SourcePath, d => d.SourcePath)
                    .Output(d => d.NormalizedPath, s => s.NormalizedPath)
                .Then<PrepareModelInputStep>()
                    .Input(s => s.JobId, d => d.JobId)
                    .Input(s => s.NormalizedPath, d => d.NormalizedPath)
                    .Output(d => d.Wav16kPath, s => s.Wav16kPath)
                .Then<DetectSpeechChunksStep>()
                    .Input(s => s.JobId, d => d.JobId)
                    .Input(s => s.Wav16kPath, d => d.Wav16kPath)
                    .Output(d => d.Chunks, s => s.Chunks)
                .Then<TranscribeStep>()
                    .Input(s => s.JobId, d => d.JobId)
                    .Input(s => s.Wav16kPath, d => d.Wav16kPath)
                    .Input(s => s.ProfileKey, d => d.ProfileKey)
                    .Input(s => s.Chunks, d => d.Chunks)
                    .Output(d => d.TranscriptPath, s => s.TranscriptPath)
                .Then<DiarizeStep>()
                    .Input(s => s.JobId, d => d.JobId)
                    .Input(s => s.Wav16kPath, d => d.Wav16kPath)
                    .Output(d => d.DiarizationPath, s => s.DiarizationPath)
                .Then<AlignSpeakersStep>()
                    .Input(s => s.JobId, d => d.JobId)
                    .Input(s => s.TranscriptPath, d => d.TranscriptPath)
                    .Input(s => s.DiarizationPath, d => d.DiarizationPath)
                .Then<CorrectTermsStep>()
                    .Input(s => s.JobId, d => d.JobId)
                    .Input(s => s.ProfileKey, d => d.ProfileKey)
                .Then<SaveResultStep>()
                    .Input(s => s.JobId, d => d.JobId)
                    .Input(s => s.TranscriptPath, d => d.TranscriptPath)
                    .Input(s => s.DiarizationPath, d => d.DiarizationPath)
                    .Output(d => d.SpeakersPath, s => s.SpeakersPath);
        }
```

- [ ] **Шаг 11: Убрать заглушку и зарегистрировать шаги**

```bash
git rm HealthTech/Workflow/Steps/StubStep.cs
```

В `Program.cs` заменить `AddTransient<StubStep>()` на восемь регистраций:

```csharp
builder.Services.AddTransient<NormalizeAudioStep>();
builder.Services.AddTransient<PrepareModelInputStep>();
builder.Services.AddTransient<DetectSpeechChunksStep>();
builder.Services.AddTransient<TranscribeStep>();
builder.Services.AddTransient<DiarizeStep>();
builder.Services.AddTransient<AlignSpeakersStep>();
builder.Services.AddTransient<CorrectTermsStep>();
builder.Services.AddTransient<SaveResultStep>();
```

Поднять `Version` у `TranscriptionWorkflow` до 2: определение изменилось, а старые
инстансы лежат в `workflow.db`.

- [ ] **Шаг 12: Собрать**

```bash
dotnet build
```
Ожидается: `Build succeeded`.

- [ ] **Шаг 13: Прогнать двухминутную запись целиком**

Положить модели в `models/`, запустить приложение и загрузить
`Medpark_audio_2min.m4a` с профилем `consilium`. Опрашивать статус:

```bash
curl.exe -s http://localhost:5089/jobs/<guid>
```
Ожидается: `currentStep` последовательно проходит «Нормализация аудио» → «Подготовка
входа для моделей» → «Поиск речи» → «Распознавание речи» → «Разделение по говорящим» →
«Выравнивание по говорящим» → «Коррекция терминов» → «Сохранение результата»,
`percent` растёт 5 → 10 → 15 → 55 → 75 → 78 → 98 → 100, статус завершается `Completed`.

Затем:

```bash
curl.exe -s http://localhost:5089/jobs/<guid>/result
```
Ожидается: JSON с репликами и полем `text` в виде диалога.

Проверить на диске: в `assets/processed/<guid>/` лежат `.wav` и `.16k.wav`,
в `transcripts/<guid>/` — `.json`, `.diarization.json`, `.speakers.json`,
`.medical_corrections.md`, а временного `.turns.json` не осталось.

- [ ] **Шаг 14: Проверить не-аудио**

Пункт 3 списка проверок.

```bash
curl.exe -s -X POST http://localhost:5089/jobs \
  -F "file=@CLAUDE.md;filename=notaudio.m4a" -F "profile=consilium"
curl.exe -s http://localhost:5089/jobs/<guid>
```
Ожидается: `status` равен `Failed`, в `error` — текст про отсутствие аудиопотока,
приложение продолжает работать, следующая загрузка обрабатывается нормально.

- [ ] **Шаг 15: Проверить поведение после перезапуска**

Пункт 4 списка проверок. Загрузить длинный файл, дождаться `Running`, остановить
приложение по Ctrl+C и запустить снова. В логе должно быть
`Помечено как Failed после перезапуска: 1 задани(й)`, а `GET /jobs/<guid>`
должен показывать `Failed`, а не вечный `Running`.

- [ ] **Шаг 16: Коммит**

```bash
git add -A
git commit -m "feat: восемь шагов конвейера обработки с прогрессом по заданию"
```

---

## Задача 8: Прогресс внутри распознавания

**Файлы:**
- Изменить: `HealthTech/Transcription/WhisperSpeechRecognitionService.cs`,
  `HealthTech/Transcription/ProcessedAudioTranscriptionService.cs`,
  `HealthTech/Workflow/Steps/TranscribeStep.cs`, `HealthTech/Jobs/JobProgress.cs`

**Интерфейсы:**
- Отдаёт наружу:
  `IProcessedAudioTranscriptionService.TranscribeAsync(..., IProgress<ChunkProgress>? progress, ...)`;
  запись `ChunkProgress(int Done, int Total)`.

- [ ] **Шаг 1: Завести тип отчёта о прогрессе**

В `HealthTech/Transcription/TranscriptModels.cs`:

```csharp
    /// <summary>Сколько чанков распознано из скольких. Для полосы прогресса внутри шага.</summary>
    public record ChunkProgress(int Done, int Total);
```

- [ ] **Шаг 2: Докладывать из цикла по чанкам**

В `ProcessedAudioTranscriptionService.TranscribeAsync` добавить параметр
`IProgress<ChunkProgress>? progress`, счётчик перед циклом и отчёт в конце каждой итерации:

```csharp
            var done = 0;
            foreach (var chunk in chunks)
            {
                // ... существующее тело: вырезать сэмплы чанка, распознать, сдвинуть метки ...

                done++;
                progress?.Report(new ChunkProgress(done, chunks.Count));
            }
```

- [ ] **Шаг 3: Пересчитать долю в полосу шага**

В `TranscribeStep.ExecuteAsync`:

```csharp
            const int bandStart = 15;
            const int bandEnd = 55;

            var progress = new Progress<ChunkProgress>(p =>
            {
                var percent = bandStart + (int)((bandEnd - bandStart) * (double)p.Done / Math.Max(p.Total, 1));
                // Progress<T> выполняет обработчик на пуле потоков: ждать здесь нечего и некому.
                _ = Progress.ReportAsync(JobId, $"Распознавание: чанк {p.Done} из {p.Total}", percent);
            });

            await _transcription.TranscribeAsync(Wav16kPath, outputDirectory, chunks, ProfileKey, progress);
```

- [ ] **Шаг 4: Собрать**

```bash
dotnet build
```
Ожидается: `Build succeeded`.

- [ ] **Шаг 5: Проверить движение полосы**

Загрузить двухминутную запись и опрашивать статус раз в несколько секунд во время
распознавания:

```bash
curl.exe -s http://localhost:5089/jobs/<guid>
```
Ожидается: `currentStep` вида `"Распознавание: чанк 3 из 8"`, `percent` растёт между
15 и 55, а не стоит на 15 до конца шага.

- [ ] **Шаг 6: Коммит**

```bash
git add -A
git commit -m "feat: прогресс по чанкам внутри шага распознавания"
```

---

## Задача 9: Документация и примеры запросов

**Файлы:**
- Изменить: `HealthTech/HealthTech.http`, `CLAUDE.md`

- [ ] **Шаг 1: Переписать `HealthTech/HealthTech.http`**

```
@HealthTech_HostAddress = http://localhost:5089

### Типы записей для выпадашки при загрузке
GET {{HealthTech_HostAddress}}/profiles
Accept: application/json

### Загрузка файла и запуск обработки (multipart)
# Возвращает { "jobId": "..." } сразу, не дожидаясь конца обработки.

### Список заданий
GET {{HealthTech_HostAddress}}/jobs
Accept: application/json

### Статус задания: status, currentStep, percent, error
GET {{HealthTech_HostAddress}}/jobs/00000000-0000-0000-0000-000000000000
Accept: application/json

### Результат: транскрипт со спикерами
GET {{HealthTech_HostAddress}}/jobs/00000000-0000-0000-0000-000000000000/result
Accept: application/json

### Коррекция терминов в уже готовом файле, без повторного распознавания
POST {{HealthTech_HostAddress}}/audio/correctTranscript?fileName=Medpark_audio_2min.speakers.json&profile=consilium
Accept: application/json
```

Загрузку файла через `.http` показать нельзя коротко — рядом дать команду curl
в комментарии.

- [ ] **Шаг 2: Обновить `CLAUDE.md`**

Что поправить:
- «Not a git repository» — неверно, репозиторий есть.
- Раздел про эндпоинты: четырёх старых больше нет, появились `/jobs` и `/profiles`.
- Добавить раздел про задания, базы SQLite и workflow.
- Убрать из описания `IAudioBatchService` и `IProcessedAudioFiles.FindFirst`.
- Описать каталоги артефактов по `<jobId>` и производный `.16k.wav`.
- Отметить, что тип записи выбирается при загрузке и задаёт промпт Whisper и глоссарии.

- [ ] **Шаг 3: Коммит**

```bash
git add -A
git commit -m "docs: обновить CLAUDE.md и примеры запросов под job-API"
```

---

## Что остаётся следующему плану

Этапы 4–7 раздела 12 спеки: `lowConfidence` с пересчётом смещений через три склейки,
файл фонетических ошибок, справочник врачей и ручная привязка спикеров, профили
`onco` и `icu` с замером реальной длины запроса в токенах.
