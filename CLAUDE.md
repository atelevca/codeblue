# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview

ASP.NET Core Web API (.NET 10, controllers, nullable + implicit usings enabled) for a HealthTech hackathon. The web project lives in `HealthTech/` under the `HealthTech.slnx` solution; the class library `SemanticKernel/` (medical term correction with a local LLM) is referenced by it. A recording is uploaded in two phases: `POST /files` stores it in its own folder and probes it with
ffprobe, then `POST /jobs` fills in the record card (title, speaker count, record type) and starts the
processing. The pipeline itself is orchestrated (WorkflowCore): normalization with FFmpeg, chunking with Silero VAD,
speech recognition (Whisper.net), speaker diarization (sherpa-onnx), speaker alignment and medical term
correction with a local LLM. Progress is visible in percent while it runs.

## Commands

Run from the repo root (or `HealthTech/`):

```sh
dotnet build                                   # build
dotnet run --project HealthTech --launch-profile http   # Windows: serves http://localhost:5089
HealthTech/run.sh                                       # macOS: same, profile "mac" (Metal)
```

- There is no test project and no linter configured. Verify changes by running the app and calling endpoints; sample requests are in `HealthTech/HealthTech.http`.
- Uploading a file is multipart, which `.http` files handle poorly; use
  `curl.exe -X POST http://localhost:5089/files -F "file=@assets/input/<name>.m4a"`, then post the
  returned `fileId` to `/jobs` with the record card.
- **Do NOT create any tests** — no test projects, test files, or test code. This is a project rule.
- **No logic in controllers.** Controllers only take the request, call a service, and return its result. Validation, loops, branching, error collection, and try/catch belong in services (e.g. `HealthTech/Audio/`). Map domain exceptions to HTTP responses in an `IExceptionHandler`, not in the controller. This is a project rule.
- CORS: policy `Ui` allows the origins in `Cors:AllowedOrigins` (default `http://localhost:4200` — the Angular dev server); `UseCors` sits before `UseHttpsRedirection` so preflights aren't redirected.
- OpenAPI document is mapped only in Development (`/openapi/v1.json`); Swagger UI is at `/swagger`.
- Git repository, default branch `master`.

## External dependency: FFmpeg

The audio pipeline shells out to `ffprobe` and `ffmpeg`. They are **not bundled**; on the Windows dev machine ffmpeg 9.0.2 is installed via winget (`Gyan.FFmpeg`), on macOS via `brew install ffmpeg`. macOS setup is in `docs/running-on-macos.md`. Paths come from config (`Audio:FfmpegPath`, `Audio:FfprobePath`, default: looked up on PATH). If they can't be started, the service throws `AudioProcessingError.FfmpegUnavailable` (HTTP 503 via the exception handler).

## External dependency: models

Transcription models are **not downloaded by the app** (`download-models.sh` at the repo root fetches all but the GGUF for a new machine) — they must already exist in `models/` at the repo root (gitignored): `ggml-large-v3.bin` (Whisper large-v3; turbo loops more on Romanian and is not used), `pyannote-segmentation-3.0.onnx` (pyannote segmentation 3.0), `wespeaker_en_voxceleb_resnet34_LM.onnx` (speaker embeddings), `silero_vad.onnx` (VAD). Paths come from `Whisper:ModelPath`, `Diarization:*ModelPath` and `Vad:ModelPath`; `ValidateOnStart` validators make startup fail with the missing path. The medical-correction GGUF `qwen2.5-7b-instruct-q4_k_m.gguf` lives there too (see `Llm:ModelFile`).

## Storage

Two separate SQLite files under `data/` (gitignored), created on startup:

- `data/workflow.db` — WorkflowCore's own state, its own EF-based provider, its own schema.
- `data/healthtech.db` — `Jobs`, `Persons`, `SpeakerBindings`, accessed with **Dapper** over
  `Microsoft.Data.Sqlite`. Schema and the seeded doctor list come from `HealthTech/Data/schema.sql`,
  which is idempotent and re-run at every start by `DatabaseInitializer`.

They are deliberately separate: WorkflowCore polls its store continuously, the UI polls job status
often, and in one file those two would eventually collide on a SQLite lock. Both connections enable
`journal_mode=WAL` and `busy_timeout=5000` (`SqliteConnectionFactory`).

EF Core stays in the package graph because `WorkflowCore.Persistence.Sqlite` is built on it; our own
code never uses it. There are no migrations — `schema.sql` is the whole schema. `Persons` has no
create endpoint on purpose: doctors are added by editing the seed or the `.db` file directly.

`Jobs` is the single source of truth for the UI; workflow steps write to it and the UI never touches
WorkflowCore's internals. A row carries both halves of the upload: `SizeBytes`, `Format` and
`DurationSec` come from the ffprobe at `POST /files`, `Title`, `SpeakersCount` and `ProfileKey` from
the record card at `POST /jobs`. `schema.sql` cannot add those columns to a database that already
exists (`CREATE TABLE IF NOT EXISTS` is a no-op and `ALTER TABLE ADD COLUMN` is not idempotent), so
`DatabaseInitializer` compares `PRAGMA table_info(Jobs)` against the expected set and adds what is
missing.

## Architecture

**Configuration** — options pattern. `Audio` section in `appsettings.json` binds to `AudioOptions` (registered in `Program.cs`). Override with env vars, e.g. `Audio__InputDirectory`, `Audio__OutputDirectory`. All directories are relative and resolve against the **content root** (`HealthTech/`): `InputDirectory` = `../assets/input`, `OutputDirectory` = `../assets/processed`, `Transcripts:OutputFolder` = `../transcripts`, `Database:*Path` = `../data/*.db`. The `assets/`, `transcripts/` and `data/` folders sit next to the solution file.

**Logging** — Serilog (`AddSerilog` in `Program.cs`): console + daily rolling file `logs/healthtech-YYYYMMDD.log` at the repo root (gitignored). Levels come from the `Serilog:MinimumLevel` section (the default `Logging` section is not used); file path/retention from `LogFiles:Path` (relative to the content root) and `LogFiles:RetainedFileCountLimit`.

**Jobs and orchestration** (`HealthTech/Jobs/`, `HealthTech/Workflow/`):
- Every upload becomes a job with its own folders: `assets/input/<jobId>/`, `assets/processed/<jobId>/`,
  `transcripts/<jobId>/` (`IJobPaths`). Nothing looks for "the first file in a shared folder" any more.
- `IJobService.UploadAsync` saves the upload under a sanitized name (`SafeFileName` strips directories
  and illegal characters, keeping Cyrillic and spaces), probes it with `IAudioProcessor.InspectAsync`
  and inserts a `Jobs` row in status `Uploaded`. No workflow starts. A file that is not audio is
  rejected here with 422 instead of failing a step a minute into processing, and the job folder is
  removed on every failure path.
- `IJobService.SaveRecordAsync` is the second phase: it validates the record type, checks the file is
  still on disk (before touching the status, or the record would go to `Pending` and die on step one),
  moves the row from `Uploaded` to `Pending` and starts the workflow. `fileId` is the job id — there is
  no separate file entity. The move is a single `UPDATE ... WHERE Status = 'Uploaded'`, so two
  simultaneous saves of one file yield one job and one 409, not two workflows on the same audio.
- `Uploaded` rows are hidden from `GET /jobs` and are **not** swept by the startup pass that fails
  orphaned jobs: a file whose form is still being filled in must survive a restart.
- `speakersCount` is stored and returned but does not influence processing — diarization still decides
  the speaker count itself. Values outside 1..20 are a 400.
- Upload limits are raised to `Uploads:MaxBytes` (1 GB) on both Kestrel and the multipart form; the
  Kestrel default of 30 MB is smaller than a real recording.
- `TranscriptionWorkflow` (version 3) runs nine steps, each a `JobStep`: normalize (5%) → prepare 16 kHz
  mono (10%) → VAD (15%) → transcribe (55%) → diarize (75%) → align (78%) → correct terms (90%) → save
  (92%) → generate minutes (100%). The number in brackets is the accumulated percent written to
  `Jobs.Percent`. The version is bumped whenever the step chain changes, because old instances stay in
  `workflow.db`.
- Data crossing a step boundary is serialized into `workflow.db`, so `TranscriptionJobData` holds only
  paths, ids and the chunk list. The turns produced by alignment travel through `turns.json` in the job's
  transcripts folder instead, and the save step deletes it.
- The three slow steps report progress inside their own band, so the bar keeps moving:
  `"Распознавание: чанк 3 из 7"` (15–55%), `"Коррекция терминов: батч 2 из 5"` (78–90%) and
  `"Генерация протокола: ..."` by phase — extracting facts (92%), writing the document (94%), checking it
  against the transcript (98%).
- `GenerateMinutesStep` is the step that sets `Completed` (`CompletesJob`). It calls
  `IDocumentService.GenerateAsync`, which skips the `Completed` check that `POST /document/save` makes.
  A minutes failure is logged as a warning and the job still completes: the transcript is the result,
  the minutes are derived from it and can be regenerated with `POST /document/save/{jobId}`.
- A failing step puts the job in `Failed` with the message and lets the chain run out; later steps see
  `Failed` and do nothing. There is no automatic retry — the steps are far too expensive for one.
- `JobStep` checks `Failed` **before** its first progress report, and `UpdateProgressAsync` only raises
  the status out of `Pending`/`Running`. Without both, a step resumed after a restart would revive a
  `Failed` job, and the final report would overwrite `Completed`.
- On startup, jobs left in `Running`/`Pending` are marked `Failed` — before `IWorkflowHost.Start()`, or a
  resumed instance would put them back into `Running` forever.

**Record profiles** (`HealthTech/Profiles/`):
- The record type is chosen when the record is saved (`discussionType` in `POST /jobs`, one of
  `medical`, `administrative`, `financial`) and stored in `Jobs.ProfileKey`; the key and the
  `discussionType` value are the same string. Only `medical` has real content — the other two run on
  the generic `Prompts/general_correction.system.txt` with the Moldovan-speech list alone, which is
  what keeps the corrector from translating Russian insertions into Romanian. A profile carries the Whisper
  initial prompt, the correction system prompt and the set of glossaries (`Profiles:Items` in
  `appsettings.json`). `GET /profiles` feeds the UI dropdown; an unknown key is a 400 listing the valid ones.
- `IProfileCatalog` is a singleton that parses prompts and glossaries once at startup — `medical_glossary.txt`
  is 850 lines and has no business being re-parsed per request. `Program.cs` resolves it eagerly so a missing
  prompt or glossary aborts startup instead of surfacing as a 500 on the first upload.
- A step failing outside its own try/catch — most plausibly its constructor, e.g. `KernelFactory` with no GGUF —
  is caught by `IWorkflowHost.OnStepError`, which writes `Failed`. The workflow's default error behavior is
  `Terminate`, not the engine's default endless 60 s retry, which would wedge the job in `Running`.
- whisper.cpp truncates the initial prompt at roughly 224 tokens, so `WhisperPrompt` is a steering phrase
  (language, register, a handful of domain terms), never a glossary. Terminology belongs to the LLM step.

**Audio pipeline** (`HealthTech/Audio/`):
- `IAudioProcessor.ProcessAudioAsync(inputPath, outputDirectory)` is the reusable entry point (singleton).
- `PrepareModelInputAsync` writes a derived `<name>.16k.wav` (16 kHz mono) next to it. VAD, Whisper and
  diarization all read that one, so nothing resamples repeatedly; the full-quality WAV stays faithful to
  the original. Artifact names drop the `.16k` suffix (`ProcessedAudioFiles.BaseName`).
- Flow: ffprobe (JSON) → pick the first audio stream → decide copy vs convert → write to `<output>/<name>.wav`.
- Validation is content-based (ffprobe), never by file extension. No audio stream / unparseable → `NotAudio`; audio stream missing codec/rate/channels → `CorruptedAudio`.
- Already PCM-in-WAV is copied byte-for-byte (not re-encoded). Anything else (including WAV containing MP3/ADPCM) is converted.
- Conversion never passes `-ar`/`-ac` so sample rate and channels are preserved; `ChoosePcmCodec` keeps bit depth (s16→16-bit, 24-bit FLAC/ALAC→`pcm_s24le`, lossy float decoders→`pcm_s16le`).
- ffmpeg writes to `<name>.wav.partial` then renames, so failures leave no output. The original input is never modified; the service refuses if output path == input path.
- All failures surface as `AudioProcessingException` carrying an `AudioProcessingError` enum. `AudioProcessingExceptionHandler` (global `IExceptionHandler`) maps the enum to a ProblemDetails response: `InputNotFound` 404, `UnknownProfile` 400, `NotAudio`/`CorruptedAudio`/`InvalidTranscript` 422, `FfmpegUnavailable` 503, others 500.

**Transcription pipeline** (`HealthTech/Transcription/`):
- Options `Whisper`, `Diarization`, `Vad`, `Transcripts` are bound in `Program.cs`; relative paths are made absolute against the content root in `PostConfigure` (models/transcripts sit at the repo root, hence `../`).
- `IProcessedAudioFiles` writes result JSON and Markdown into a directory given by the caller. It no longer
  searches for a file — `FindFirst()` is gone along with the whole "first file wins" model.
- `IProcessedAudioTranscriptionService` takes an explicit WAV path, reads it via `IAudioSampleReader` (NAudio: WAV only, downmix + resample to 16 kHz mono float in memory when needed), splits it into speech chunks with Silero VAD (`IVoiceActivityService`), runs Whisper on each chunk separately (timestamps shifted back to file time), saves `<transcripts>/<name>.json`. **No diarization** in this flow. Chunking is what stops Whisper repetition loops ("Субтитры делал…", one phrase repeated for a minute) from spreading; Chunking (`Vad:*`): speech regions are grouped at pauses into ~15–25 s chunks (`TargetChunkMin`/`TargetChunkMax`; a chunk under 15 s may grow to `MaxChunkDuration` = 30 s), each padded by `ChunkOverlap`/2 = 0.25 s per side. sherpa's `MaxSpeechDuration` is not a hard cap, so `SileroVoiceActivityService` cuts pause-less speech itself at the quietest 100 ms frame 15–25 s into the piece. Audio is Romanian with Russian words mixed in: `Whisper:Language` = `ro` + full `ggml-large-v3` gave by far the best result; `auto` misdetects per chunk (and then *translates* Romanian into Russian) and turbo loops more.
- Whisper is built with `WithProbabilities()`. Words are assembled from tokens (a token starting with a
  space opens a word; whisper.cpp special tokens `[_...]` are skipped), a word's probability is the minimum
  over its tokens, and a word below `Whisper:LowConfidenceThreshold` (0.5) goes into the segment's
  `lowConfidence` list as `{ at, word, p }`, `at` being the character offset in the segment text. A word
  not found in the (trimmed, normalized) segment text is silently skipped. The offset is recomputed on
  both merges — segments into a turn (`SpeakerAlignmentService`) and a turn into correction pieces
  (`MedicalTermCorrector`). When the correction changes a turn, `Reassemble` moves the words of unchanged
  pieces to the pieces' new positions and drops only the words of rewritten pieces, so one fix in a long
  turn does not wipe its flags. The `.speakers.json` and `.corrected.json` carry that recomputed list.
- `IProcessedAudioDiarizationService` runs diarization only (no Whisper) on the given file and saves `<transcripts>/<name>.diarization.json` (segments with seconds + `mm:ss`).
- `ISpeakerAlignmentService.Align` assigns each Whisper segment to the speaker whose turns overlap it most
  (nearest turn if none overlap) and merges consecutive same-speaker segments into turns. Alignment is per
  segment, not per word, so a long Whisper segment spanning a speaker change goes entirely to the dominant
  speaker. Orchestration lives in the workflow steps, not in this service.
- Speakers are labelled `Speaker 1`, `Speaker 2`, ... in order of first appearance (sherpa cluster indices are remapped in `SherpaSpeakerDiarizationService`). No role detection (doctor/patient).
- Whisper runs on the GPU via `Whisper.net.Runtime.Vulkan`; `Whisper.net.Runtime` (CPU) is also referenced as the fallback when no Vulkan driver is present (Whisper.net tries Vulkan before CPU). `Whisper:UseGpu` / `Whisper:GpuDevice` (default 1 = Intel Arc A370M; env `WHISPER_GPU_DEVICE` overrides) control it, and the loaded runtime is logged at model load (`Whisper runtime: Vulkan`). If `GGML_VK_VISIBLE_DEVICES` is set (launchSettings and `HealthTech/run.bat` set it to `1`), ggml renumbers the visible devices from 0, so `WhisperOptions.ResolveGpuDevice` forces `GpuDevice` = 0. `Whisper:NativeLogging` (on in Development) forwards whisper.cpp/ggml logs, incl. the `ggml_vulkan:` device list, to Serilog.
- `WhisperSpeechRecognitionService` and `SherpaSpeakerDiarizationService` are singletons holding the loaded models; each serializes runs with a `SemaphoreSlim`. Model failures → `AudioProcessingError.ModelFailed` (500).
- `Diarization:ExclusiveSegments` makes speaker turns non-overlapping (shorter turn wins); without it, bridged long turns swallow the other speaker.

**Medical term correction** (`SemanticKernel/` project, registered with `AddMedicalTermCorrection(configuration)` in `Program.cs`):
- Fully offline: Semantic Kernel + LLamaSharp 0.27.0 (`LLamaSharp.Backend.Cpu`; all LLamaSharp packages must share one version) running a GGUF model in-process. The model `models/qwen2.5-7b-instruct-q4_k_m.gguf` must be placed manually and is never downloaded; `LlmOptions.ResolveModelPath` walks up from the output dir until `Llm:ModelsDirectory` (`../models`) exists.
- Settings: `SemanticKernel/appsettings.llm.json` (copied to output with `Prompts/` and `Glossary/`) gives defaults for the `Llm` section; the app's own config/env vars (`Llm__*`) override.
- `KernelFactory` (singleton) checks the model path in its constructor (missing → `LlmModelNotFoundException` → 503 via `LlmModelNotFoundExceptionHandler`, before any transcription work) but loads the weights only on first use. The chat prompt uses the model's own chat template (`PromptTemplateTransformer`).
- `CorrectTermsStep` corrects the aligned turns (turn index + 1 = segment id) and saves
  `<transcripts>/<jobId>/<name>.medical_corrections.md`. The system prompt and glossaries come from the
  job's profile (`RecordProfileContent`), not from hard-coded file names.
- `ITranscriptCorrectionService` (`POST /audio/correctTranscript?jobId=...&fileName=...&profile=...`), resolving the file inside that job's `transcripts/<jobId>/` runs the same correction on an existing file in `transcripts/` (`turns` or `segments` with `text`; `.json` may be omitted; only a bare file name is accepted, bad/diarization-only JSON → `InvalidTranscript` 422). Writes `<name>.corrected.json` (source JSON with only texts changed; the dialogue `text` of a speakers file is rebuilt) and `<name>.medical_corrections.md`.
- `MedicalTermCorrector` cuts each segment into sentence pieces (`TextPieces`, ≤ `MaxPieceCharacters`; pieces cover the text exactly, so an unchanged segment comes back byte-identical), batches pieces (`BatchSize`, `MaxBatchCharacters`), sends only id+text plus previous pieces as context, retries unparseable/mismatched output (`MaxRetries`) and otherwise keeps the originals. Validation and the log are per piece.
- Glossaries (`Glossary/medical_glossary.txt`, `Glossary/moldova_speech_glossary.txt`; `term | term | English` lines, `#` = comment) are far bigger than the context, so `Glossary.Select` adds per request only lines whose words share 5-letter, diacritics-free prefixes with the batch text (adjacent words glued too, for split terms; the last English column is not searched), rarer matches first, up to `MaxGlossaryCharacters` per file. The Moldova list is labelled as "NOT errors, keep as written".
- Each piece in the request carries `lowConfidence` (omitted when empty, to save tokens); the system prompt
  tells the model to start with those words but treat them as a hint, not a restriction. `POST /audio/correctTranscript`
  reads the same field from the file, so a re-run sees what the pipeline saw; files without it still work.
- `Glossary/phonetic_confusions.txt` (`heard | correct | comment`) is the third glossary of `medical` only.
  It is loaded and selected exactly like the others; `ProfileCatalog.HeadingFor` gives it its own heading
  ("Known ASR mishearings ..."). It is filled by hand from `.medical_corrections.md` reports.
- `CorrectionValidator` rejects changed numbers, translation (Cyrillic ratio change > 0.15, letters moving between Cyrillic and Latin ≥ 2 each way, or a changed Latin/Cyrillic word-run order), >30% length change and >25% edit distance.

**Speakers and persons** (`HealthTech/Speakers/`):
- `Persons` is filled by hand (seed in `schema.sql` or the `.db` file); `GET /persons` lists it for the UI.
- `PUT /jobs/{id}/speakers` takes `[{ label, personId }]` and **replaces** the job's whole binding set in one
  transaction. Every label must be a speaker of that job's `.speakers.json`, no label twice, every
  `personId` must exist — otherwise 400 and nothing is saved. `[]` clears the bindings. No result yet → 404.
- `GET /jobs/{id}/transcript` returns the turns with `displayName` (the doctor's name, or the label if
  unbound) and the dialogue text rebuilt with those names. Names are substituted on the fly; transcript
  files are never rewritten, so bindings can change any number of times without reprocessing.
- All checks live in `SpeakerBindingService`; the repositories are Dapper over `IDbConnectionFactory`.

**Minutes of meeting** (`HealthTech/Documents/`, `SemanticKernel/Minutes/`): the local LLM extracts facts
from the speakers transcript, writes the minutes, and a verification pass checks them against the
transcript. The document is stored as a Quill Delta in `transcripts/<jobId>/minutes.document.json`; the
PDF is rendered from that Delta with PDFsharp/MigraDoc, using Arial on Windows and macOS and DejaVu Sans on
Linux (`Documents:FontDirectory` overrides). Details and the editor contract are in `HealthTech/Documents/README.md`.

**Controllers** (`HealthTech/Controllers/`, attribute-routed `[Route("[controller]")]`) — thin, delegate to services:
- `FilesController` — `POST /files` (multipart: `file`) → `{ fileId, fileName, sizeBytes, format, durationSec }`.
- `JobsController` — `POST /jobs` (JSON: `fileId`, `title`, `speakersCount`, `discussionType`) → the record
  card; `GET /jobs` (hides `Uploaded`); `GET /jobs/{id}` (card + status, currentStep, percent, error);
  `GET /jobs/{id}/result` (the `.speakers.json` content); `PUT /jobs/{id}/speakers` and
  `GET /jobs/{id}/transcript` → `ISpeakerBindingService`.
- `PersonsController` — `GET /persons`, the doctor directory for the binding dropdown.
- `ProfilesController` — `GET /profiles` for the upload dropdown.
- `AudioController` — `POST /audio/correctTranscript?jobId=&fileName=&profile=` → `ITranscriptCorrectionService`.
  The four old endpoints (`validateAndProcess`, `transcribeProcessed`, `diarizeProcessed`,
  `transcribeWithSpeakers`) are gone: with per-job folders "the first file in a shared directory" is meaningless.
- `DocumentController` — `GET /document/get/{jobId}`, `POST /document/save/{jobId}` (no body: regenerate;
  `{ delta }`: verify and save an edit), `GET /document/downloadpdf/{jobId}`.
- `MainController` (`GET /main/run`) is a template placeholder.

`JobStatus` is serialized as a string (`"Running"`), not as an enum number.

## Not built yet

Deferred items are listed in section 15 of `docs/superpowers/specs/2026-09-26-transcription-workflow-design.md`.
Glossary content for the administrative and financial types is deliberately empty — the mechanism is
there, the words are not. The phonetic-confusions list holds only a handful of seed lines. There is no
automatic speaker identification by voice and no create endpoint for `Persons`.

No authentication: `UseAuthorization()` is called without any scheme, every endpoint is open.
