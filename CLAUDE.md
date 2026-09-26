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

Transcription models are **not downloaded by the app** (`download-models.sh` at the repo root fetches all but the GGUF for a new machine) — they must already exist in `models/` at the repo root (gitignored): `ggml-large-v3.bin` (Whisper large-v3; turbo loops more on Romanian and is not used), `pyannote-segmentation-3.0.onnx` (pyannote segmentation 3.0), `3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx` (CAM++ speaker embeddings; the heavier `wespeaker_en_voxceleb_resnet34_LM.onnx` gave the same turns and took 5× longer, so it is kept in the download script only as an alternative), `silero_vad.onnx` (VAD). Paths come from `Whisper:ModelPath`, `Diarization:*ModelPath` and `Vad:ModelPath`; `ValidateOnStart` validators make startup fail with the missing path. The two GGUF files live there too: `qwen2.5-3b-instruct-q4_k_m.gguf` for term correction (`Llm:ModelFile`) and `qwen2.5-7b-instruct-q4_k_m.gguf` for the minutes (`Llm:Minutes:ModelFile`); `download-models.sh` fetches both.

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
- `TranscriptionWorkflow` (version 4) runs eight steps, each a `JobStep`: normalize (5%) → prepare 16 kHz
  mono (10%) → VAD (15%) → recognize speech and speakers (75%) → align (78%) → correct terms (90%) → save
  (92%) → generate minutes (100%). The minutes step extracts facts and renders the document; the
  verification against the transcript is **not** part of the step (see "Minutes of meeting"). The number in brackets is the accumulated percent written to
  `Jobs.Percent`. The version is bumped whenever the step chain changes, because old instances stay in
  `workflow.db`.
- `RecognizeSpeechStep` runs Whisper and diarization **in parallel** (`Task.WhenAll`): they are independent,
  both read `Wav16kPath`, and Whisper occupies the GPU while diarization is CPU-bound, so the step costs the
  longer of the two instead of their sum. Both services keep their own `SemaphoreSlim`, so two jobs still
  never share a model. WorkflowCore's own `Parallel()` was not used: it schedules branches but executes the
  pointers of one instance sequentially, so two long-running steps would not overlap.
- Data crossing a step boundary is serialized into `workflow.db`, so `TranscriptionJobData` holds only
  paths, ids and the chunk list. The turns produced by alignment travel through `turns.json` in the job's
  transcripts folder instead, and the save step deletes it.
- The three slow steps report progress inside their own band, so the bar keeps moving:
  `"Распознавание: чанк 3 из 7"` (15–70%, the last 5% of the band is left for a diarization that
  outlives Whisper), `"Коррекция терминов: батч 2 из 5"` (78–90%) and
  `"Генерация протокола: извлечение фактов"` (92%; the document itself is rendered in code, no phase).
- `GenerateMinutesStep` is the step that sets `Completed` (`CompletesJob`). It calls
  `IDocumentService.GenerateAsync`, which skips the `Completed` check that `POST /document/save` makes
  and does not verify: it saves the document with `verification.pending = true` and puts the job id on
  `MinutesVerificationQueue`; `MinutesVerificationWorker` (a `BackgroundService`) verifies it after the
  job is already `Completed` and writes the result into `minutes.document.json`, unless the document was
  re-saved meanwhile (`SavedAt` changed → the stale result is dropped). On startup the worker re-queues
  every saved document still marked pending. A minutes failure is logged as a warning and the job still
  completes: the transcript is the result, the minutes are derived from it and can be regenerated with
  `POST /document/save/{jobId}`.
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
- `Whisper:UseFlashAttention` (on) and `Whisper:BeamSize` = 2 are the speed settings: on the Arc A370M the 2-minute sample went from 93 s (beam 5, no flash attention) to 47 s with the same segments and no repetition loops. Beam 5 did not transcribe better on that sample; if loops come back on other recordings, raise `BeamSize` to 3 before touching anything else. The log line `flash attn = 1` at model load confirms the flag took effect.
- `Diarization:NumThreads` = 8 (the P-cores of the dev laptop; all 20 logical cores oversubscribe ORT and make it slower). With CAM++ embeddings diarization of the 2-minute sample takes ~13 s instead of 68 s.
- `WhisperSpeechRecognitionService` and `SherpaSpeakerDiarizationService` are singletons holding the loaded models; each serializes runs with a `SemaphoreSlim`. Model failures → `AudioProcessingError.ModelFailed` (500).
- `Diarization:ExclusiveSegments` makes speaker turns non-overlapping (shorter turn wins); without it, bridged long turns swallow the other speaker.

**Medical term correction** (`SemanticKernel/` project, registered with `AddMedicalTermCorrection(configuration)` in `Program.cs`):
- Fully offline: Semantic Kernel + LLamaSharp 0.27.0 running GGUF models in-process. Backends:
  `LLamaSharp.Backend.Vulkan` (Intel Arc) plus `LLamaSharp.Backend.Cpu` as the fallback LLamaSharp picks
  itself when no Vulkan device works; all LLamaSharp packages must share one version. The models are never
  downloaded by the app; `LlmOptions.ResolveModelPath` walks up from the output dir until `Llm:ModelsDirectory` (`../models`) exists.
- **Two models, two roles** (`LlmModelRole`): `Llm:ModelFile` = Qwen2.5-**3B** q4_k_m for term correction
  (many short, narrow requests; `CorrectionValidator` catches what a small model breaks; ~2.5× faster than 7B),
  `Llm:Minutes:ModelFile` = Qwen2.5-**7B** q4_k_m for fact extraction and verification (one long request each).
  `Llm:Minutes:*` (`ModelFile`, `ContextSize`, `GpuLayerCount`) fall back field by field to the top-level
  values, so an empty section means one shared model. DI registers `KernelFactory`/`IChatCompletionProvider`
  keyed by role (`[FromKeyedServices(LlmModelRole.Correction)]` in `MedicalTermCorrector`, `Minutes` in
  `MeetingMinutesGenerator`); the same file for both roles yields one factory, so the weights load once.
- **GPU**: `Llm:GpuLayerCount` / `Llm:Minutes:GpuLayerCount` offload that many layers via Vulkan to the device
  `GGML_VK_VISIBLE_DEVICES` exposes, the same one Whisper uses. **Both are 0 on Windows on purpose.** Measured on
  the 2-minute sample (Release build): 12 layers of the 3B took 1.6 GB of the A370M's 4 GB next to Whisper
  large-v3, the process spilled into shared memory and Whisper went from 43 s to 123 s while the correction
  batch only went from 62 s to 41 s — a net loss of a minute. Offload pays off only if Whisper leaves room
  (a quantized `ggml-large-v3-q5_0` or a different GPU); re-measure the whole pipeline, not the LLM step
  alone. The mac profile sets `Llm__GpuLayerCount=999` (Metal, unified memory).
- Settings: `SemanticKernel/appsettings.llm.json` (copied to output with `Prompts/` and `Glossary/`) gives defaults for the `Llm` section; the app's own config/env vars (`Llm__*`, `Llm__Minutes__ModelFile`) override.
- `KernelFactory` (one per role) checks the model path in its constructor (missing → `LlmModelNotFoundException` → 503 via `LlmModelNotFoundExceptionHandler`, before any transcription work) but loads the weights only on first use. The chat prompt uses the model's own chat template (`PromptTemplateTransformer`).
- `CorrectTermsStep` corrects the aligned turns (turn index + 1 = segment id) and saves
  `<transcripts>/<jobId>/<name>.medical_corrections.md`. The system prompt and glossaries come from the
  job's profile (`RecordProfileContent`), not from hard-coded file names.
- `ITranscriptCorrectionService` (`POST /audio/correctTranscript?jobId=...&fileName=...&profile=...`), resolving the file inside that job's `transcripts/<jobId>/` runs the same correction on an existing file in `transcripts/` (`turns` or `segments` with `text`; `.json` may be omitted; only a bare file name is accepted, bad/diarization-only JSON → `InvalidTranscript` 422). Writes `<name>.corrected.json` (source JSON with only texts changed; the dialogue `text` of a speakers file is rebuilt) and `<name>.medical_corrections.md`.
- `MedicalTermCorrector` cuts each segment into sentence pieces (`TextPieces`, ≤ `MaxPieceCharacters`; pieces cover the text exactly, so an unchanged segment comes back byte-identical), batches pieces (`BatchSize`, `MaxBatchCharacters`), sends only id+text plus previous pieces as context, retries unparseable/mismatched output (`MaxRetries`) and otherwise keeps the originals. Validation and the log are per piece.
- **The model returns only the pieces it changed** (`[]` when nothing changed); the prompts say so and
  `RequestCorrectionsAsync` accepts any subset of the batch ids (no unknown id, no duplicate). Echoing the
  whole batch back cost ~1,000 output tokens per batch on the CPU for text that mostly did not change.
- Glossaries (`Glossary/medical_glossary.txt`, `Glossary/moldova_speech_glossary.txt`; `term | term | English` lines, `#` = comment) are far bigger than the context, so `Glossary.Select` adds per request only lines whose words share 5-letter, diacritics-free prefixes with the query text (adjacent words glued too, for split terms; the last English column is not searched), rarer matches first, up to `MaxGlossaryCharacters` (1,000) per file. The query text is **not the whole batch**: it is the low-confidence words of the batch, each with one neighbouring word on either side (`LowConfidenceWindows`), so a batch without low-confidence words gets no reference lines at all. Matching the whole text pulled in 2,000 characters per list, the ICD-10 one above all, for words Whisper was sure about. The Moldova list is labelled as "NOT errors, keep as written".
- Each piece in the request carries `lowConfidence` (omitted when empty, to save tokens); the system prompt
  tells the model to start with those words but treat them as a hint, not a restriction. `POST /audio/correctTranscript`
  reads the same field from the file, so a re-run sees what the pipeline saw; files without it still work.
- `Glossary/phonetic_confusions.txt` (`heard | correct | comment`) is the third glossary of `medical` only.
  It is loaded and selected exactly like the others; `ProfileCatalog.HeadingFor` gives it its own heading
  ("Known ASR mishearings ..."). It is filled by hand from `.medical_corrections.md` reports.
- `Glossary/diagnostics.csv` + `subclasses.csv` + `classes.csv` are an ICD-10 export (Romanian, **no
  diacritics**, `;`-separated, header row; 12,350 diagnoses). Only `diagnostics.csv` is listed in the profile;
  `ProfileCatalog` sends any `.csv` to `Icd10Glossary.Load`, which reads the two other files from the same
  folder and joins them into ordinary glossary lines `code | name | subclass`. From there it is a
  normal `Glossary` (same prefix matching, same `MaxGlossaryCharacters` cap); the subclass column is context
  only, like the English column. The class name is deliberately not appended: it doubles the line length
  and halves how many diagnoses fit in the 2,000-character budget, so `classes.csv` only validates the join. The heading and the system prompt tell the model the names lack diacritics
  and that it must not strip them from the transcript or insert codes. The fourth glossary of `medical` only.
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
from the speakers transcript, the document is rendered from them in code, and a verification pass checks
it against the transcript. The transcript is the named dialogue (bound doctors' names instead of `Speaker N`, same as
`GET /jobs/{id}/transcript`), and the record title plus the bound persons go along as `metadata`. The
facts (`MeetingFacts`, snake_case JSON) have the shape of the document: header, participants, agenda,
decisions/actions/open issues with an `agenda_id`, next meeting, summary. **There is no "write the
minutes" model call**: `RenderMinutes` builds the whole Markdown (header, participants, agenda, course
of the meeting, the three tables, next meeting, summary, signatures) from the normalized facts. That call
used to cost ~96 s of 7B on the CPU and added nothing the facts did not hold. The verification runs
**after** the job is `Completed`, in the background (see the jobs section), with
`Minutes:VerificationMaxRetries` = 0: a failed verification tends to fail the same way again and each
attempt costs minutes. `POST /document/save/{jobId}` (regenerate or save an edit) still verifies
synchronously — that is the "button". `GET /document/get` returns `verification.pending = true` until
the background pass has written its result. Finding kinds are `Unsupported`, `Omission`, `Contradiction`, `Misattribution`,
each with an optional `section`. The document is stored as a Quill Delta in `transcripts/<jobId>/minutes.document.json`; the
PDF is rendered from that Delta with PDFsharp/MigraDoc, using Arial on Windows and macOS and DejaVu Sans on
Linux (`Documents:FontDirectory` overrides). The PDF carries the Medpark letterhead taken from
`Ghid-de-pregatire-pentru-ecografie-final.pdf`: logo and tagline (`HealthTech/Documents/Branding/*.png`,
embedded resources) in the page header, teal `#007C84` rules and headings, grey `#625C5B` footer/status. Details and the editor contract are in `HealthTech/Documents/README.md`.

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
