using HealthTech.Audio;
using HealthTech.Transcription;
using Microsoft.Extensions.Options;
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

builder.Services.AddControllers();
builder.Services.AddOpenApi();

builder.Services.Configure<AudioOptions>(builder.Configuration.GetSection(AudioOptions.SectionName));
builder.Services.AddSingleton<IAudioProcessor, AudioProcessor>();
builder.Services.AddSingleton<IAudioBatchService, AudioBatchService>();

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
builder.Services.AddSingleton<IProcessedAudioSpeakerTranscriptService, ProcessedAudioSpeakerTranscriptService>();

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<AudioProcessingExceptionHandler>();

var app = builder.Build();

app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "HealthTech v1"));
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();
