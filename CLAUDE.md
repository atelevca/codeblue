# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview

ASP.NET Core Web API (.NET 10, controllers, nullable + implicit usings enabled) for a HealthTech hackathon. The web project lives in `HealthTech/` under the `HealthTech.slnx` solution; the class library `SemanticKernel/` (medical term correction with a local LLM) is referenced by it. Features: audio normalization (files from an input folder are validated and converted to WAV with FFmpeg) and offline transcription of the normalized audio with speaker diarization (sherpa-onnx) + speech recognition (Whisper.net).

## Commands

Run from the repo root (or `HealthTech/`):

```sh
dotnet build                                   # build
dotnet run --project HealthTech --launch-profile http   # serves http://localhost:5089
```

- There is no test project and no linter configured. Verify changes by running the app and calling endpoints; sample requests are in `HealthTech/HealthTech.http`.
- **Do NOT create any tests** — no test projects, test files, or test code. This is a project rule.
- **No logic in controllers.** Controllers only take the request, call a service, and return its result. Validation, loops, branching, error collection, and try/catch belong in services (e.g. `HealthTech/Audio/`). Map domain exceptions to HTTP responses in an `IExceptionHandler`, not in the controller. This is a project rule.
- OpenAPI document is mapped only in Development (`/openapi/v1.json`); Swagger UI is at `/swagger`.
- Not a git repository.

## External dependency: FFmpeg

The audio pipeline shells out to `ffprobe` and `ffmpeg`. They are **not bundled** and were not installed on the dev machine at time of writing. Paths come from config (`Audio:FfmpegPath`, `Audio:FfprobePath`, default: looked up on PATH). If they can't be started, the service throws `AudioProcessingError.FfmpegUnavailable` (HTTP 503 via the exception handler).

## External dependency: models

Transcription models are **not downloaded by the app** — they must already exist in `models/` at the repo root (gitignored): `ggml-large-v3.bin` (Whisper; `ggml-large-v3-turbo.bin` is also there but is worse on Romanian), `pyannote-segmentation-3.0.onnx` (pyannote segmentation 3.0), `wespeaker_en_voxceleb_resnet34_LM.onnx` (speaker embeddings), `silero_vad.onnx` (VAD). Paths come from `Whisper:ModelPath`, `Diarization:*ModelPath` and `Vad:ModelPath`; `ValidateOnStart` validators make startup fail with the missing path.

## Architecture

**Configuration** — options pattern. `Audio` section in `appsettings.json` binds to `AudioOptions` (registered in `Program.cs`). Override with env vars, e.g. `Audio__InputDirectory`, `Audio__OutputDirectory`. `InputDirectory` is an absolute path set in both `appsettings.json` and `appsettings.Development.json`. Relative directories (e.g. `OutputDirectory` = `../assets/processed`) resolve against the **content root** (`HealthTech/`); the `assets/` folder sits next to the solution file.

**Logging** — Serilog (`AddSerilog` in `Program.cs`): console + daily rolling file `logs/healthtech-YYYYMMDD.log` at the repo root (gitignored). Levels come from the `Serilog:MinimumLevel` section (the default `Logging` section is not used); file path/retention from `LogFiles:Path` (relative to the content root) and `LogFiles:RetainedFileCountLimit`.

**Audio pipeline** (`HealthTech/Audio/`):
- `IAudioProcessor.ProcessAudioAsync(inputPath)` is the reusable entry point (registered as singleton). Relative `inputPath` resolves against the input directory.
- Flow: ffprobe (JSON) → pick the first audio stream → decide copy vs convert → write to `<output>/<name>.wav`.
- Validation is content-based (ffprobe), never by file extension. No audio stream / unparseable → `NotAudio`; audio stream missing codec/rate/channels → `CorruptedAudio`.
- Already PCM-in-WAV is copied byte-for-byte (not re-encoded). Anything else (including WAV containing MP3/ADPCM) is converted.
- Conversion never passes `-ar`/`-ac` so sample rate and channels are preserved; `ChoosePcmCodec` keeps bit depth (s16→16-bit, 24-bit FLAC/ALAC→`pcm_s24le`, lossy float decoders→`pcm_s16le`).
- ffmpeg writes to `<name>.wav.partial` then renames, so failures leave no output. The original input is never modified; the service refuses if output path == input path.
- `IAudioBatchService.ProcessInputDirectoryAsync()` runs the processor over every file in the input directory; per-file failures are collected in the result, except `FfmpegUnavailable` (and a missing input directory → `InputNotFound`), which is thrown.
- All failures surface as `AudioProcessingException` carrying an `AudioProcessingError` enum. `AudioProcessingExceptionHandler` (global `IExceptionHandler`) maps the enum to a ProblemDetails response: `InputNotFound` 404, `NotAudio`/`CorruptedAudio`/`InvalidTranscript` 422, `FfmpegUnavailable` 503, others 500.

**Transcription pipeline** (`HealthTech/Transcription/`):
- Options `Whisper`, `Diarization`, `Vad`, `Transcripts` are bound in `Program.cs`; relative paths are made absolute against the content root in `PostConfigure` (models/transcripts sit at the repo root, hence `../`).
- `IProcessedAudioFiles` finds the first audio file by name in the processed dir (`IAudioProcessor.OutputDirectory`) and writes result JSON to the transcripts folder; shared by both services below.
- `IProcessedAudioTranscriptionService` takes the first processed file, reads it once via `IAudioSampleReader` (NAudio: WAV only, downmix + resample to 16 kHz mono float in memory when needed), splits it into speech chunks with Silero VAD (`IVoiceActivityService`), runs Whisper on each chunk separately (timestamps shifted back to file time), saves `<transcripts>/<name>.json`. **No diarization** in this flow. Chunking is what stops Whisper repetition loops ("Субтитры делал…", one phrase repeated for a minute) from spreading; Chunking (`Vad:*`): speech regions are grouped at pauses into ~15–25 s chunks (`TargetChunkMin`/`TargetChunkMax`; a chunk under 15 s may grow to `MaxChunkDuration` = 30 s), each padded by `ChunkOverlap`/2 = 0.25 s per side. sherpa's `MaxSpeechDuration` is not a hard cap, so `SileroVoiceActivityService` cuts pause-less speech itself at the quietest 100 ms frame 15–25 s into the piece. Audio is Romanian with Russian words mixed in: `Whisper:Language` = `ro` + full `ggml-large-v3` gave by far the best result; `auto` misdetects per chunk (and then *translates* Romanian into Russian) and turbo loops more.
- `IProcessedAudioDiarizationService` runs diarization only (no Whisper) on the first processed file and saves `<transcripts>/<name>.diarization.json` (segments with seconds + `mm:ss`).
- `IProcessedAudioSpeakerTranscriptService` runs both services above, then assigns each Whisper segment to the speaker whose turns overlap it most (nearest turn if none overlap), merges consecutive same-speaker segments into turns and saves `<transcripts>/<name>.speakers.json` (turns + a `Speaker N:` dialogue `text`). Alignment is per segment, not per word, so a long Whisper segment spanning a speaker change goes entirely to the dominant speaker.
- Speakers are labelled `Speaker 1`, `Speaker 2`, ... in order of first appearance (sherpa cluster indices are remapped in `SherpaSpeakerDiarizationService`). No role detection (doctor/patient).
- Whisper runs on the GPU via `Whisper.net.Runtime.Vulkan`; `Whisper.net.Runtime` (CPU) is also referenced as the fallback when no Vulkan driver is present (Whisper.net tries Vulkan before CPU). `Whisper:UseGpu` / `Whisper:GpuDevice` (default 1 = Intel Arc A370M; env `WHISPER_GPU_DEVICE` overrides) control it, and the loaded runtime is logged at model load (`Whisper runtime: Vulkan`). If `GGML_VK_VISIBLE_DEVICES` is set (launchSettings and `HealthTech/run.bat` set it to `1`), ggml renumbers the visible devices from 0, so `WhisperOptions.ResolveGpuDevice` forces `GpuDevice` = 0. `Whisper:NativeLogging` (on in Development) forwards whisper.cpp/ggml logs, incl. the `ggml_vulkan:` device list, to Serilog.
- `WhisperSpeechRecognitionService` and `SherpaSpeakerDiarizationService` are singletons holding the loaded models; each serializes runs with a `SemaphoreSlim`. Model failures → `AudioProcessingError.ModelFailed` (500).
- `Diarization:ExclusiveSegments` makes speaker turns non-overlapping (shorter turn wins); without it, bridged long turns swallow the other speaker.

**Medical term correction** (`SemanticKernel/` project, registered with `AddMedicalTermCorrection(configuration)` in `Program.cs`):
- Fully offline: Semantic Kernel + LLamaSharp 0.27.0 (`LLamaSharp.Backend.Cpu`; all LLamaSharp packages must share one version) running a GGUF model in-process. The model `models/qwen2.5-7b-instruct-q4_k_m.gguf` must be placed manually and is never downloaded; `LlmOptions.ResolveModelPath` walks up from the output dir until `Llm:ModelsDirectory` (`../models`) exists.
- Settings: `SemanticKernel/appsettings.llm.json` (copied to output with `Prompts/` and `Glossary/`) gives defaults for the `Llm` section; the app's own config/env vars (`Llm__*`) override.
- `KernelFactory` (singleton) checks the model path in its constructor (missing → `LlmModelNotFoundException` → 503 via `LlmModelNotFoundExceptionHandler`, before any transcription work) but loads the weights only on first use. The chat prompt uses the model's own chat template (`PromptTemplateTransformer`).
- `ProcessedAudioSpeakerTranscriptService` corrects the aligned turns right after `Align` (turn index + 1 = segment id) and saves `<transcripts>/<name>.medical_corrections.md`.
- `ITranscriptCorrectionService` (`POST /audio/correctTranscript?fileName=...`) runs the same correction on an existing file in `transcripts/` (`turns` or `segments` with `text`; `.json` may be omitted; only a bare file name is accepted, bad/diarization-only JSON → `InvalidTranscript` 422). Writes `<name>.corrected.json` (source JSON with only texts changed; the dialogue `text` of a speakers file is rebuilt) and `<name>.medical_corrections.md`.
- `MedicalTermCorrector` cuts each segment into sentence pieces (`TextPieces`, ≤ `MaxPieceCharacters`; pieces cover the text exactly, so an unchanged segment comes back byte-identical), batches pieces (`BatchSize`, `MaxBatchCharacters`), sends only id+text plus previous pieces as context, retries unparseable/mismatched output (`MaxRetries`) and otherwise keeps the originals. Validation and the log are per piece.
- Glossaries (`Glossary/medical_glossary.txt`, `Glossary/moldova_speech_glossary.txt`; `term | term | English` lines, `#` = comment) are far bigger than the context, so `Glossary.Select` adds per request only lines whose words share 5-letter, diacritics-free prefixes with the batch text (adjacent words glued too, for split terms; the last English column is not searched), rarer matches first, up to `MaxGlossaryCharacters` per file. The Moldova list is labelled as "NOT errors, keep as written".
- `CorrectionValidator` rejects changed numbers, translation (Cyrillic ratio change > 0.15, letters moving between Cyrillic and Latin ≥ 2 each way, or a changed Latin/Cyrillic word-run order), >30% length change and >25% edit distance.

**Controllers** (`HealthTech/Controllers/`, attribute-routed `[Route("[controller]")]`) — thin, delegate to services:
- `AudioController` — `GET /audio/validateAndProcess` → `IAudioBatchService`; `POST /audio/transcribeProcessed` → `IProcessedAudioTranscriptionService`; `POST /audio/diarizeProcessed` → `IProcessedAudioDiarizationService`; `POST /audio/transcribeWithSpeakers` → `IProcessedAudioSpeakerTranscriptService`; `POST /audio/correctTranscript?fileName=` → `ITranscriptCorrectionService`. Services are injected per action via `[FromServices]` so an endpoint only loads the models it needs.
- `MainController` (`GET /main/run`) is a template placeholder.
