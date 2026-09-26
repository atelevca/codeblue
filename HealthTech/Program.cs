using HealthTech.Audio;
using HealthTech.Data;
using HealthTech.Documents;
using HealthTech.Jobs;
using HealthTech.Profiles;
using HealthTech.Speakers;
using HealthTech.Transcription;
using HealthTech.Workflow;
using HealthTech.Workflow.Steps;
using WorkflowCore.Interface;
using WorkflowCore.Models;
using Microsoft.Extensions.Options;
using SemanticKernel;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Console + daily rolling file (<LogFiles:Path> with the date inserted before the extension, e.g. healthtech-20260925.log).
// Levels come from the "Serilog" section; the file path is relative to the content root, like the other paths.
var logFilePath = Path.GetFullPath(builder.Configuration["LogFiles:Path"] ?? "../logs/healthtech-.log",
    builder.Environment.ContentRootPath);
builder.Services.AddSerilog(logger => logger
    .ReadFrom.Configuration(builder.Configuration)
    .WriteTo.Console()
    .WriteTo.File(logFilePath,
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: builder.Configuration.GetValue("LogFiles:RetainedFileCountLimit", 14),
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}"));

// Записи консилиумов бывают на час и больше: Medpark_audio.m4a весит 59 МБ,
// а умолчание Kestrel - 30 МБ. Без этого POST /jobs отвечает 413.
var maxUploadBytes = builder.Configuration.GetValue("Uploads:MaxBytes", 1_073_741_824L);
builder.Services.Configure<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>(
    options => options.Limits.MaxRequestBodySize = maxUploadBytes);
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(
    options => options.MultipartBodyLengthLimit = maxUploadBytes);

// Статус задания уходит в UI строкой ("Running"), а не числом: число нечитаемо
// и ломается при любой вставке в середину перечисления.
builder.Services.AddControllers().AddJsonOptions(options =>
    options.JsonSerializerOptions.Converters.Add(
        new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddOpenApi();

// UI (Angular dev server на :4200) живёт на другом origin. Список origin-ов - из конфигурации (Cors:AllowedOrigins).
const string UiCorsPolicy = "Ui";
var corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options => options.AddPolicy(UiCorsPolicy, policy => policy
    .WithOrigins(corsOrigins)
    .AllowAnyHeader()
    .AllowAnyMethod()
    .WithExposedHeaders("Content-Disposition")));

builder.Services.Configure<AudioOptions>(builder.Configuration.GetSection(AudioOptions.SectionName));
builder.Services.AddSingleton<IAudioProcessor, AudioProcessor>();
builder.Services.AddSingleton<IJobPaths, JobPaths>();

// Model/output paths are made absolute against the content root; ValidateOnStart fails startup if a model file is missing.
builder.Services.AddOptions<WhisperOptions>()
    .Bind(builder.Configuration.GetSection(WhisperOptions.SectionName))
    .PostConfigure<IHostEnvironment>((o, env) =>
    {
        o.ModelPath = Path.GetFullPath(o.ModelPath, env.ContentRootPath);
        o.ResolveGpuDevice();
    })
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<WhisperOptions>, WhisperOptionsValidator>();

builder.Services.AddOptions<DiarizationOptions>()
    .Bind(builder.Configuration.GetSection(DiarizationOptions.SectionName))
    .PostConfigure<IHostEnvironment>((o, env) =>
    {
        o.SegmentationModelPath = Path.GetFullPath(o.SegmentationModelPath, env.ContentRootPath);
        o.EmbeddingModelPath = Path.GetFullPath(o.EmbeddingModelPath, env.ContentRootPath);
    })
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<DiarizationOptions>, DiarizationOptionsValidator>();

builder.Services.AddOptions<VadOptions>()
    .Bind(builder.Configuration.GetSection(VadOptions.SectionName))
    .PostConfigure<IHostEnvironment>((o, env) => o.ModelPath = Path.GetFullPath(o.ModelPath, env.ContentRootPath))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<VadOptions>, VadOptionsValidator>();

builder.Services.AddOptions<TranscriptsOptions>()
    .Bind(builder.Configuration.GetSection(TranscriptsOptions.SectionName))
    .PostConfigure<IHostEnvironment>((o, env) => o.OutputFolder = Path.GetFullPath(o.OutputFolder, env.ContentRootPath));

builder.Services.AddSingleton<IProcessedAudioFiles, ProcessedAudioFiles>();
builder.Services.AddSingleton<IAudioSampleReader, AudioSampleReader>();
builder.Services.AddSingleton<ISpeechRecognitionService, WhisperSpeechRecognitionService>();
builder.Services.AddSingleton<ISpeakerDiarizationService, SherpaSpeakerDiarizationService>();
builder.Services.AddSingleton<IVoiceActivityService, SileroVoiceActivityService>();
builder.Services.AddSingleton<IProcessedAudioTranscriptionService, ProcessedAudioTranscriptionService>();
builder.Services.AddSingleton<IProcessedAudioDiarizationService, ProcessedAudioDiarizationService>();
builder.Services.AddSingleton<ISpeakerAlignmentService, SpeakerAlignmentService>();
builder.Services.AddSingleton<ITranscriptCorrectionService, TranscriptCorrectionService>();

// Local LLM (LLamaSharp + Semantic Kernel) for medical term correction; defaults in appsettings.llm.json, "Llm" section overrides.
// The GGUF model is checked when the speaker transcript service is first used, not at startup.
builder.Services.AddMedicalTermCorrection(builder.Configuration);
// Reading/downloading an existing document must not load or require the LLM.
builder.Services.AddSingleton(sp => new Lazy<SemanticKernel.Minutes.IMeetingMinutesGenerator>(
    () => sp.GetRequiredService<SemanticKernel.Minutes.IMeetingMinutesGenerator>()));
builder.Services.AddSingleton<IDocumentPdfRenderer, DocumentPdfRenderer>();
builder.Services.AddSingleton<IDocumentService, DocumentService>();
// Сверка протокола идёт в фоне после завершения задания, а не внутри последнего шага.
builder.Services.AddSingleton<MinutesVerificationQueue>();
builder.Services.AddSingleton<IMinutesVerificationQueue>(sp => sp.GetRequiredService<MinutesVerificationQueue>());
builder.Services.AddHostedService<MinutesVerificationWorker>();

// Прикладная база (Dapper). Путь относительный — резолвится от content root, как остальные.
var appDatabasePath = Path.GetFullPath(
    builder.Configuration["Database:AppDatabasePath"] ?? "../data/healthtech.db",
    builder.Environment.ContentRootPath);
builder.Services.AddSingleton<IDbConnectionFactory>(new SqliteConnectionFactory(appDatabasePath));
builder.Services.AddSingleton<DatabaseInitializer>();
builder.Services.AddSingleton<IJobRepository, JobRepository>();

// Оркестрация. База движка отдельная от прикладной: его фоновый опрос и запросы
// статуса из UI не должны встречаться на блокировке одного файла SQLite.
var workflowDatabasePath = Path.GetFullPath(
    builder.Configuration["Database:WorkflowDatabasePath"] ?? "../data/workflow.db",
    builder.Environment.ContentRootPath);
Directory.CreateDirectory(Path.GetDirectoryName(workflowDatabasePath)!);
builder.Services.AddWorkflow(options => options.UseSqlite(
    $"Data Source={workflowDatabasePath};", canCreateDB: true));

builder.Services.Configure<ProfileOptions>(builder.Configuration.GetSection(ProfileOptions.SectionName));
builder.Services.AddSingleton<IProfileCatalog, ProfileCatalog>();

builder.Services.AddSingleton<IJobService, JobService>();
builder.Services.AddSingleton<IPersonRepository, PersonRepository>();
builder.Services.AddSingleton<ISpeakerBindingRepository, SpeakerBindingRepository>();
builder.Services.AddSingleton<ISpeakerBindingService, SpeakerBindingService>();
builder.Services.AddSingleton<IJobProgress, JobProgress>();
builder.Services.AddTransient<NormalizeAudioStep>();
builder.Services.AddTransient<PrepareModelInputStep>();
builder.Services.AddTransient<DetectSpeechChunksStep>();
builder.Services.AddTransient<RecognizeSpeechStep>();
builder.Services.AddTransient<AlignSpeakersStep>();
builder.Services.AddTransient<CorrectTermsStep>();
builder.Services.AddTransient<SaveResultStep>();
builder.Services.AddTransient<GenerateMinutesStep>();

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<AudioProcessingExceptionHandler>();
builder.Services.AddExceptionHandler<DocumentExceptionHandler>();
builder.Services.AddExceptionHandler<LlmModelNotFoundExceptionHandler>();

var app = builder.Build();

app.Services.GetRequiredService<DatabaseInitializer>().Initialize();

// Строго до workflowHost.Start(): после перезапуска движок возобновит свои инстансы,
// и если пометить задания позже, возобновлённый шаг вернёт строку в Running и она
// застрянет там навсегда. Помечаем упавшими заранее - шаги увидят Failed и не тронут задание.
var orphaned = await app.Services.GetRequiredService<IJobRepository>()
    .FailRunningAsync("Приложение было перезапущено во время обработки.");
if (orphaned > 0)
{
    app.Logger.LogWarning("Помечено как Failed после перезапуска: {Count} задани(й)", orphaned);
}

// Каталог профилей строится сейчас, а не при первом запросе: его конструктор падает на
// отсутствующем промпте или глоссарии, и такой сбой должен ронять старт, как валидаторы моделей,
// а не превращаться в 500 на первой загрузке.
app.Services.GetRequiredService<IProfileCatalog>();

var workflowHost = app.Services.GetRequiredService<IWorkflowHost>();
workflowHost.RegisterWorkflow<TranscriptionWorkflow, TranscriptionJobData>();

// Последняя линия обороны. JobStep ловит всё внутри себя, но сам шаг движок собирает через DI
// ДО вызова RunAsync, поэтому падение конструктора (например, отсутствующий GGUF в KernelFactory)
// туда не попадает. Без этого обработчика задание осталось бы в Running навсегда.
var jobsForErrors = app.Services.GetRequiredService<IJobRepository>();
workflowHost.OnStepError += (workflow, step, exception) =>
{
    if (workflow.Data is not TranscriptionJobData data)
    {
        return;
    }

    app.Logger.LogError(exception, "Задание {JobId}: шаг {Step} упал мимо собственного обработчика",
        data.JobId, step.Name);
    _ = jobsForErrors.UpdateStatusAsync(data.JobId, JobStatus.Failed,
        $"{step.Name}: {exception.Message}");
};

workflowHost.Start();
app.Lifetime.ApplicationStopping.Register(() => workflowHost.Stop());

app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "HealthTech v1"));
}

// До UseHttpsRedirection: preflight OPTIONS должен получить ответ здесь, а не редирект.
app.UseCors(UiCorsPolicy);

// В Development редиректа нет. UI ходит через прокси дев-сервера на http://localhost:5089, а профиль
// https из Visual Studio слушает тот же порт и отвечал бы 307 на https://localhost:7059. Для GET это
// лишний круг и CORS на чужом origin; для multipart POST /files хуже: Kestrel отвечает 307, не дочитав
// тело, и рвёт соединение, пока прокси ещё передаёт файл, и браузер видит ERR_CONNECTION_RESET.
if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.UseAuthorization();

app.MapControllers();

app.Run();
