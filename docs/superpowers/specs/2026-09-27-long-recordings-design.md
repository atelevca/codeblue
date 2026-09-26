# Long recordings on normal machines: token budgets and windowed minutes

Date: 2026-09-27
Status: implemented (see docs/superpowers/plans/2026-09-27-long-recordings.md)

## 1. Why

A 10-minute recording processed with the `http` launch profile (8192-token context, CPU) exposed
three limits. A 30–40 minute recording hits all of them:

1. **Term correction overflows the context** (`NoKvSlot`, `ContextOverflowException`). A batch is
   capped by `Llm:MaxBatchCharacters`, which counts only the segment text. The `lowConfidence`
   lists, context pieces and glossary lines are not counted. On the failing job the
   `lowConfidence` JSON was 21,269 characters against 8,164 characters of text. The corrector
   catches the error and keeps the original text, so correction is silently skipped.
2. **The minutes refuse long transcripts.** Extraction and verification send the whole transcript
   in one request, guarded by `Minutes:MaxTranscriptCharacters` (6000). The `mac` profile raises
   it to 55000 with a 32k context, but only that profile does, and only a 32k context makes it
   fit. A 40-minute transcript is ~34k characters (~12k tokens).
3. **Stage 2 (generation) grows with the meeting.** Its input is all extracted facts and its reply
   is the whole document, capped at `Minutes:GenerationMaxTokens`.

Goal: any length of recording produces minutes on a normal machine (8k context, CPU) and on the
demo Mac (M3 Max, 32k context, Metal). Results are the same on both; only the speed differs.

Non-goals: speeding up Whisper or diarization (a colleague is working on that); capping
`lowConfidence` per piece; a larger default context.

## 2. Token budgets (foundation)

- **`ITokenCounter`** (`SemanticKernel/`): `Task<int> CountAsync(string text, CancellationToken)`,
  backed by the tokenizer of the weights `KernelFactory` already loads (loading them on first use
  if needed). All budgets below are in real model tokens; characters are never used as a proxy.
- **Request budget**: `input tokens ≤ Llm:ContextSize − reply MaxTokens − template overhead − margin`.
  Input = system prompt + user message. The template overhead is measured once by counting a
  rendered empty exchange. The margin is an internal constant: 5% of the context, at least 128 tokens.
- **Nothing is truncated.** Input that does not fit is split (sections 3, 5, 6); if it cannot be
  split further, the stage reports that explicitly.
- **Context check on first load**: the GGUF is loaded lazily by design (a missing model is a 503
  on first use, the app still starts), and the check needs the tokenizer. So when `KernelFactory`
  first loads the model, it validates that `ContextSize` fits the largest system prompt + its reply
  limit + a minimal window of 1000 tokens. Otherwise it throws `LlmConfigurationException`, whose
  message names the setting to raise. The first LLM step then fails with that message in
  `Jobs.Error` before doing any work. (This replaces "refuse to start" from the design discussion.)
- **Logging**: every LLM request logs its input token count and budget (numbers only, never content).

## 3. Transcript windows

`TranscriptWindows.Split(string transcript, int maxTokens, ITokenCounter)` → ordered list of windows.

- The transcript is the dialogue text of `.speakers.json`: `Speaker N:\n<text>` blocks separated by
  a blank line. Whole blocks are packed greedily until the next block would exceed `maxTokens`.
- A block larger than `maxTokens` on its own is cut at sentence boundaries (`TextPieces`); every
  part keeps the `Speaker N:` label (or the bound name) so attribution survives.
- No overlap between windows.
- If everything fits in one window, callers take the existing single-request path unchanged.

Window size for extraction: `min(Minutes:MaxWindowTokens, extraction request budget − prompt − metadata)`.
`Minutes:MaxWindowTokens` defaults to **4000** (~12–15 minutes of speech). The cap applies on
every machine because the extraction reply (`ExtractionMaxTokens`) must hold every fact of its
window, and a 7B model reads a 4k-token window more carefully than a 12k one. A 40-minute
recording is therefore 3–4 windows on both an 8k laptop and the 32k Mac.

## 4. Term correction batching

`MedicalTermCorrector.SplitIntoBatches` closes a batch when either:

- the full user message (`BuildUserMessage`: segments with `lowConfidence`, context pieces,
  selected glossary lines) plus the system prompt would exceed the request budget
  (`Llm:MaxTokens` as the reply), counted on the message actually built after glossary selection; or
- the batch text tokens plus the reply's JSON overhead (estimated as 15% of the text tokens plus
  12 tokens per piece) would exceed `Llm:MaxTokens`, because the model returns every piece; or
- the batch reaches `Llm:BatchSize` pieces.

A single piece that exceeds the budget on its own is sent alone (pieces are ≤ `MaxPieceCharacters`,
so this only happens with extreme glossary or `lowConfidence` volume) and logged. `Llm:MaxBatchCharacters`
is removed.

## 5. Fact extraction

`ExtractFactsAsync(transcript, metadata)` keeps its signature.

**One window**: unchanged (one call, `NormalizeFacts`).

**Several windows**:

1. **Per window, in order**: the existing extraction system prompt; the user message is the
   existing JSON (`transcript` = the window, `metadata` = the full metadata) followed by a note:
   - "This is fragment k of n of the transcript. Extract only facts from this fragment; do not
     assume the meeting starts or ends here."
   - "Topics already identified in earlier fragments: 1. …, 2. … If this fragment continues one of
     them, use exactly the same title." (Omitted for k = 1.)
   Each result goes through `NormalizeFacts` (local agenda ids 1..m).
2. **Merge in code** (`MeetingFactsMerger`):
   - `meeting.*`, `chair`, `secretary`: first value that is not `Nespecificat`, in window order.
   - `present`: union by name, compared case- and diacritics-insensitively; role = first stated.
   - `absent`: union, minus names that appear in `present`.
   - `agenda_explicit`: true if any window says true.
   - `agenda`: concatenated in order; an item whose normalized title equals an earlier one is
     merged into it (discussions joined with a space). Window ids are remapped to global ids.
   - `decisions`, `actions`, `open_issues`: appended in order with remapped `agenda_id`; exact
     duplicates (normalized description + agenda id) are dropped.
   - `next_meeting`: last value that is not `Nespecificat`.
   - `summary`: from step 3.
3. **Consolidation** (one LLM call, new prompt `minutes_consolidation.system.txt`):
   - Input: numbered agenda titles and each window's `summary`. No discussions, no transcript.
   - Output: `{"merge": [{"id": 5, "into": 2}, …], "summary": "…"}`, one flat object per topic that
     continues an earlier one. (A nested `{"same_topic": [[2,5]]}` form was tried first: the 7B model
     repeated the group outside the array and broke the JSON on every attempt.) The summary has 3–6
     sentences, in Romanian.
   - Code applies the merges (`MeetingFactsMerger.ApplyTopicMerges`): `into` must be an earlier known
     id, an id named twice is ignored, chains resolve to the first topic; discussions are joined in
     order, references remapped, and rows that now duplicate each other are removed.
   - On failure after `Minutes:MaxRetries`: no topic merging; the summary is the window summaries
     joined together; a warning is logged. The document is still produced.
   - The result goes through `NormalizeFacts` once more.
4. If any window's extraction fails after retries, `ExtractFactsAsync` throws (same behavior as
   today): a document silently missing part of the meeting is worse than none.

## 6. Document rendering (replaces the stage 2 LLM call)

`GenerateMinutesAsync(facts)` keeps its signature and returns the same Markdown, now rendered
entirely in code from the normalized facts, following the template of `minutes_generation.system.txt`
(commit `3172c27`) exactly:

- `# Proces-verbal al ședinței`, `**Tema:**`, `**Data:** … | **Ora:** … | **Locul:** …`
- `## Participanți` with `**Președinte:**`, `**Secretar:**`, `**Prezenți:**` ("Nume – funcție"
  list or `Nespecificat`), `**Absenți:**` (list or "Nu au fost consemnați.")
- `## Ordinea de zi`: existing `RenderAgenda`
- `## Desfășurarea ședinței`: `### id. topic` + discussion for each item; the summary if the agenda is empty
- `## Decizii`, `## Acțiuni`, `## Probleme deschise`: existing `RenderTables`
- `## Următoarea ședință`, `## Rezumat`, `## Semnături` (chair/secretary lines)

Table cells go through the existing `Cell` escaping. Other free text (header values, names,
discussions, summary) is inserted as plain text with line endings normalized. That is what the
model's output contained until now, so `QuillDocument` and the PDF see the same kind of input. `minutes_generation.system.txt`, `RequiredHeadings` validation for
model output, and `Minutes:GenerationMaxTokens` are removed. `DocumentPhase.Generating` stays
(now instant).

## 7. Verification

`VerifyMinutesAsync(transcript, markdown, metadata)` keeps its signature. It is used both after
generation and for user edits (`POST /document/save` with a delta).

**If transcript + document fit one request**: unchanged, all four kinds.

**Otherwise**: the whole document is checked against the transcript window by window, and the result
carries `Partial = true`, so `IsConsistent` is never true for it (Unsupported was not checked).

- Window size: `verification budget − prompt − tokens(metadata + document JSON)`. If that is
  below 1000 tokens, verification is skipped: `Completed = false` and the summary gives the reason
  (document too large for `Llm:ContextSize`). No exception.
- Each call: the existing prompt; the user message is the existing JSON with the window as
  `transcript`, plus a note: "This is fragment k of n of the transcript. Report only discrepancies
  this fragment shows."
- **Kinds**: `Contradiction`, `Misattribution` and `Omission` are valid per window. `Unsupported`
  cannot be judged from one window and is **dropped in code**. The summary states it: "Verificat pe
  N fragmente; afirmațiile fără suport în transcript nu pot fi verificate pe fragmente."
- **Combining**: findings concatenated; identical findings (kind + resolved quotes) deduplicated;
  quotes resolved against the full transcript/metadata and full document with the existing
  `TryResolveQuote`; `DiscardedFindings` summed; summary composed in code (fragments, number of
  discrepancies, the Unsupported note).
- A window that fails after retries: the others still count, `Completed = false`, and the summary
  names the unchecked fragment(s).

## 8. Progress

`IProgress<DocumentPhase>` becomes `IProgress<DocumentProgress>`, where `DocumentProgress` is
`(DocumentPhase Phase, int Current, int Total)` with a new `Consolidating` phase.
`GenerateMinutesStep` reports, inside its existing 92–100% band:

- "Generare protocol: extragere fragment k din n"
- "consolidare"
- "verificare fragment k din n"

With one window it reports what it does today.

## 9. Configuration

| Setting | Change |
|---|---|
| `Minutes:MaxTranscriptCharacters`, `MaxFactsCharacters`, `MaxVerificationCharacters` | removed (token budgets) |
| `Minutes:GenerationMaxTokens` | removed (no generation LLM) |
| `Llm:MaxBatchCharacters` | removed (token-based batches) |
| `Minutes:MaxWindowTokens` | new, default 4000 |
| `mac` launch profile | only the dead `Minutes__*` keys removed; 32k context and GPU stay |
| `http` / `https` profiles | untouched |

Removed keys still present in an environment are ignored.

## 10. Files

- `SemanticKernel/`:
  - new: `ITokenCounter`, `TranscriptWindows`, `Minutes/MeetingFactsMerger.cs`,
    `Prompts/minutes_consolidation.system.txt`
  - changed: `KernelFactory.cs`, `LlmOptions.cs`, `MedicalCorrection/MedicalTermCorrector.cs`,
    `Minutes/MeetingMinutesGenerator.cs`, `Minutes/MinutesOptions.cs`,
    `Minutes/IMeetingMinutesGenerator.cs` (progress), `appsettings.llm.json`, `Minutes/README.md`
  - unchanged: `Prompts/minutes_extraction.system.txt`, `minutes_verification.system.txt` (the
    fragment notes go in the user message, so the single-window path sends exactly what it sends today)
  - deleted: `Prompts/minutes_generation.system.txt`
- `HealthTech/`: `Documents/DocumentModels.cs`, `Documents/DocumentService.cs` (progress type),
  `Workflow/Steps/GenerateMinutesStep.cs` (progress text), `Properties/launchSettings.json` (mac only),
  `Documents/README.md`, `CLAUDE.md`

The workflow step chain is unchanged: no `TranscriptionWorkflow` version bump. The likely overlap
with the colleague's pipeline work is `GenerateMinutesStep.cs`.

## 11. Verification plan (no test code, per project rules)

1. `dotnet build`; minutes run with 8192 and with 32768 context; with a context too small for the
   prompts, the first LLM step fails with the `LlmConfigurationException` message.
2. **Forced windowing** on the 10-minute job with `Minutes__MaxWindowTokens=1000` (3–4 windows):
   extraction, merge, consolidation and windowed verification succeed; the document and PDF render.
3. **Laptop budget on the Mac**: `Llm__ContextSize=8192`; logged token counts stay within their
   budgets for every request.
4. **The `NoKvSlot` job** (`039e2d29…`): correction via `POST /audio/correctTranscript` at 8192
   context; no batch fails.
5. **Code rendering vs. the old LLM output** on a short job: same headings and order; PDF and email work.
6. **Long transcript**: build a synthetic job by joining several jobs' `.speakers.json` turns
   (≥ 35k characters, ~40 minutes). Run `POST /document/save/{jobId}` at 8192 and at 32768 context and
   record time, windows, findings and whether any request exceeded its budget. This does not cover
   Whisper or diarization on long audio.
