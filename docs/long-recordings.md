# Long recordings: token budgets, windowed minutes, code-rendered document

Branch `feature/long-recordings`, 2026-09-27. Design: `docs/superpowers/specs/2026-09-27-long-recordings-design.md`.
Step-by-step plan: `docs/superpowers/plans/2026-09-27-long-recordings.md`.

This document explains what changed, why, how each part works, which settings control it, what the
API and UI now return, what was measured, and how to diagnose problems from the logs.

---

## 1. Why this was needed

A 10-minute recording processed on 26 September failed in two places:

1. **Term correction ran out of context.** Two of three correction batches failed with
   `LLamaDecodeError: llama_decode failed: 'NoKvSlot'` and `ContextOverflowException`. The corrector
   caught the error and silently kept the uncorrected text.
2. **The minutes were never generated.** `The transcript exceeds the configured limit of 6000
   characters`. The job completed without minutes, so there was nothing to download or email.

Three limits lay behind this. A 30–40 minute recording hits all of them:

| Limit | Where | Why it broke |
|---|---|---|
| Correction batches were capped by `Llm:MaxBatchCharacters` (4000), which counted **only the segment text** | `MedicalTermCorrector` | Each request also carries the `lowConfidence` word lists, the context pieces and the glossary lines. On the failing job the `lowConfidence` JSON alone was 21,269 characters against 8,164 characters of text. |
| Extraction and verification sent the **whole transcript in one request**, guarded by `Minutes:MaxTranscriptCharacters` (6000) | `MeetingMinutesGenerator` | 10 minutes of speech is ~8.4k characters; 40 minutes is ~34k characters (~12k tokens). |
| Stage 2 (generation) sent **all extracted facts** and got back the **whole document** in one reply | `MeetingMinutesGenerator` | Both grow with the length of the meeting. |

The Mac launch profile hid part of this: it raised the context to 32k and the transcript limit to
55,000 characters. Only that profile did, though. On 26 September Rider was running the `http`
profile, so the app ran with an 8k context on the CPU.

**Goal:** a recording of any length produces a corrected transcript and minutes, both on a normal
machine (8k context, CPU) and on the demo Mac (M3 Max, 32k context, Metal). Both produce the same
result; only the speed differs.

---

## 2. What changed at a glance

| Area | Before | After |
|---|---|---|
| Request sizing | Character limits, guessed per stage | **Real tokens** from the GGUF tokenizer, checked against `Llm:ContextSize` minus that stage's reply limit |
| Misconfigured context | `NoKvSlot` mid-job, minutes silently skipped | **503 `LlmConfiguration`** on first model use, with the setting to raise; remembered, so the model is not reloaded |
| Term correction batches | ≤ 4000 characters of text | Close when the **full request** would not fit, when the echoed reply would exceed 80% of `Llm:MaxTokens`, or at `BatchSize` pieces |
| Fact extraction | One request, ≤ 6000 characters | **Windows of whole turns** (≤ 4000 tokens), one request each, merged in code, plus one small consolidation call |
| Document generation (stage 2) | LLM call that copied facts into a template | **Rendered in code** (`MinutesRenderer`), same template, no model call |
| Verification | One request, ≤ 14000 characters | One request if it fits; otherwise the **whole document vs each transcript window**, flagged `partial` |
| Length limit | 422 above 6000 characters | **None**; nothing is ever truncated |
| Progress | 92 / 94 / 98 | Per fragment: 92–95 extraction, 95 merging, 96 document, 96–99 verification |

---

## 3. Token budgets (foundation)

### 3.1 Counting tokens: `ITokenCounter`

`SemanticKernel/TokenBudget.cs` defines:

```csharp
public interface ITokenCounter
{
    uint ContextSize { get; }
    Task<int> CountAsync(string text, CancellationToken ct = default);
    Task<int> CountPromptAsync(string systemPrompt, string userMessage, CancellationToken ct = default);
}
```

`KernelFactory` implements it with the tokenizer of the loaded GGUF (`LLamaWeights.Tokenize`), and
DI registers it as `ITokenCounter`. `CountPromptAsync` renders the system + user exchange through
the same `PromptTemplateTransformer` (the model's own ChatML template) that the executor uses. The
count therefore includes the template tokens, and there is no separate overhead estimate.

Counting needs the weights, so the first count loads the model if it has not been loaded yet.
Tokenizing only reads the vocabulary and the template, which makes it safe to run while another
request is running inference.

Characters are never used as a proxy. Romanian with diacritics and Cyrillic tokenize very
differently from English, and the old character limits were exactly what failed.

### 3.2 The budget formula: `TokenBudget`

```
margin(context)         = max(128, 5% of context)
ForInput(context, reply) = context − reply − margin(context)
```

The rendered prompt of a request must be ≤ `ForInput(ContextSize, that stage's reply limit)`. The
margin absorbs what is not counted exactly. That includes:
- the retry message `RunStageAsync` appends on a second attempt (~50 tokens);
- the corrector's context pieces, which are sized with their original text but sent corrected (±30% of at most 400 characters).

Worked budgets (input tokens available for the prompt):

| Stage (reply setting) | Laptop defaults, 8192 | 8192 with Mac replies (4096) | Mac profile, 32768 |
|---|---|---|---|
| Margin | 409 | 409 | 1638 |
| Correction (`Llm:MaxTokens`) | 8192 − 2048 − 409 = **5735** | **3687** | 32768 − 4096 − 1638 = **27034** |
| Extraction (`Minutes:ExtractionMaxTokens`) | 8192 − 3072 − 409 = **4711** | **3687** | **27034** |
| Consolidation (fixed 1024) | **6759** | **6759** | **30106** |
| Verification (`Minutes:VerificationMaxTokens`) | 8192 − 2048 − 409 = **5735** | **3687** | **27034** |

`TokenBudget.MinimalWindowTokens = 1000` is the smallest transcript window worth sending. Below it,
the context is considered misconfigured.

### 3.3 The context check: `LlmConfigurationException`

The GGUF is loaded lazily on purpose. A missing model is a 503 on first use, and the app still
starts without it. When `KernelFactory` first loads the model, `ValidateContext` checks:

```
largest Prompts/*.system.txt (rendered, tokens)
  + largest reply of Llm:MaxTokens, Minutes:ExtractionMaxTokens, Minutes:VerificationMaxTokens
  + 1000 (minimal window)
  + margin
  ≤ Llm:ContextSize
```

If the check passes, it logs:

```
LLM context check: 32768 tokens, largest prompt minutes_verification.system.txt 1166 tokens, largest reply 4096
```

If it fails, it throws `LlmConfigurationException`, which `LlmModelNotFoundExceptionHandler` maps to **503**:

```json
{"title":"LlmConfiguration","status":503,
 "detail":"Llm:ContextSize = 3000 is too small: prompt minutes_verification.system.txt (1166 tokens) + reply 4096 + window 1000 + margin 150 need 6412. Raise Llm:ContextSize or lower Llm:MaxTokens / Minutes:ExtractionMaxTokens / Minutes:VerificationMaxTokens."}
```

- **The failure is remembered.** The configuration cannot change without a restart, so every later
  call gets the same 503 immediately and the multi-GB weights are not reloaded. Measured: the first
  call failed in 0.43 s, the second in 0.003 s, with one model load.
- **Invalid `Minutes:*` values** (for example `MaxWindowTokens` < 1000, a non-positive reply limit, or
  `MaxRetries` outside 0–10) also throw `LlmConfigurationException` (503). Before, they threw an
  `ArgumentException`, which surfaced as a 422.
- **In a workflow job,** the first LLM step (term correction, at 78%) fails with this message in
  `Jobs.Error`, right away instead of twenty minutes in.

---

## 4. Term correction batching (`MedicalTermCorrector`)

`SplitIntoBatchesAsync` walks the sentence pieces in order and closes the current batch when adding
the next piece would break any of three rules:

1. **Full request fits.** The system prompt plus the user message actually built for the batch must
   be ≤ `ForInput(ContextSize, Llm:MaxTokens)`. The message is the pieces with their `lowConfidence`
   lists, the previous `ContextSegments` pieces as context, and the glossary lines `Glossary.Select`
   picks for exactly these pieces. It is built with the same `BuildUserMessage` that sends it.
2. **The reply fits.** The model echoes every piece back as `[{"id","text"}]`. The estimate per piece
   is `ceil(tokens(text) × 1.15) + 12`, and the batch total must stay ≤ **80%** of `Llm:MaxTokens`
   (`ReplyShare = 0.8`). A reply cut off at `MaxTokens` is unparseable JSON and would lose the whole
   batch's corrections; the 20% covers pretty-printed JSON and escapes.
3. **`Llm:BatchSize` pieces** (default 15), as before.

A single piece that exceeds the budget on its own is sent alone and logged:
`Medical correction piece {Id} alone needs {Tokens} input tokens, budget {Budget}; sent anyway`.
Pieces are at most `MaxPieceCharacters` (300), so this only happens with extreme glossary or
`lowConfidence` volume.

`Llm:MaxBatchCharacters` is removed.

**Effect by machine.** At 8k with 4096-token replies, batches hold 2–4 pieces, because the system
prompt, glossaries and `lowConfidence` lists fill the input. At 32k they grow to the reply limit, so
there are fewer batches: the same 10-minute job took 13 batches at 8k and 3 at 32k.

**Cost of sizing.** Sizing tokenizes the candidate request once per piece. Measured: 49 pieces in
about 1 s, so roughly 3–4 s for 40 minutes. It is logged as
`Medical correction: {PieceCount} piece(s) sized into {BatchCount} batch(es) in {Seconds} s`.

---

## 5. Minutes pipeline

`IMeetingMinutesGenerator` keeps its methods. `ExtractFactsAsync` and `VerifyMinutesAsync` gained an
optional `IProgress<MinutesProgress>` parameter:

```csharp
Task<MeetingFacts> ExtractFactsAsync(string transcript, MeetingMetadata? metadata = null,
    IProgress<MinutesProgress>? progress = null, CancellationToken ct = default);
Task<string> GenerateMinutesAsync(MeetingFacts facts, CancellationToken ct = default);
Task<MinutesVerification> VerifyMinutesAsync(string transcript, string minutesMarkdown, MeetingMetadata? metadata = null,
    IProgress<MinutesProgress>? progress = null, CancellationToken ct = default);
```

Callers that passed a `CancellationToken` positionally must now name it (`ct: ct`).

### 5.1 Transcript windows (`TranscriptWindows.SplitAsync`)

The transcript the minutes see is the named dialogue: `Speaker N:` (or a bound doctor's name) on its
own line, then the text, with turns separated by a blank line (`TranscriptDialogue.Format`).

- **Whole turns** are packed greedily until the next one would exceed the window budget. Windows do
  not overlap: overlap would extract each boundary fact twice.
- **A turn longer than a window** (a monologue) is cut at sentence boundaries (`TextPieces.Split`,
  pieces ≤ 300 characters). Every part repeats the speaker line, so attribution survives.
  Only a real speaker line is repeated, meaning a short line of at most five words ending in a
  colon. Any other first line is treated as content.
- **Sizes are measured on the JSON-escaped text,** because a window travels inside a JSON string. The
  blank line between turns becomes `\n\n` and is counted as 3 tokens.
- **Line endings** `\r\n` and `\r` are normalized.
- **A transcript with no blank lines at all** (an edited or foreign file) is split at its speaker lines
  instead of becoming one block with one speaker's label stamped on everything.

### 5.2 Fact extraction

```
window budget = min(Minutes:MaxWindowTokens, ForInput(context, ExtractionMaxTokens) − prompt − metadata − 300)
```

The 300 tokens are reserved for the fragment note.

**`Minutes:MaxWindowTokens` (4000) applies on every machine**, including the 32k Mac. The extraction
reply must hold every fact of its window within `ExtractionMaxTokens`, and a 7B model reads a
4k-token window more carefully than a 12k one. So a 40-minute recording is split into several
windows on both machines, and both produce the same result.

**If the transcript fits one window,** the request is exactly what it always was: same prompt, same
JSON (`{"transcript", "metadata"}`), same `NormalizeFacts`. Short recordings behave as before.

**If it does not fit,** each window is extracted in order with the same system prompt. The user
message is the usual JSON with the window as `transcript`, followed by a note:

> Acesta este fragmentul k din n al transcriptului. Extrage doar faptele din acest fragment; nu
> presupune că ședința începe sau se termină aici.
>
> Teme identificate deja în fragmentele anterioare:
> 1. …
> Dacă fragmentul continuă una dintre aceste teme, folosește exact același titlu (topic).

In English: "This is fragment k of n of the transcript. Extract only the facts from this fragment; do
not assume the meeting starts or ends here. Topics already identified in earlier fragments: … If this
fragment continues one of these topics, use exactly the same title."

**The topics hint** carries the titles found in earlier fragments, so a discussion cut by a window
boundary keeps its title. If the hint makes the request too big, the **oldest topics are dropped
first** until it fits, because a continued discussion is most likely one of the latest topics. This
is logged as `MOM extraction fragment {k}: topics hint kept {Kept} of {All} topics to fit the context`.

Each fragment's reply goes through the existing `NormalizeFacts`, so each fragment's agenda ids are
local (1..m). **If any fragment fails** after retries, extraction fails as a whole, as it did before:
a document silently missing part of the meeting is worse than none.

### 5.3 Merging fragments in code (`MeetingFactsMerger.Merge`)

No model sees or rewrites a decision, action or issue here.

| Field | Rule |
|---|---|
| `meeting.title/date/time/location`, `chair`, `secretary` | first value that is not `Nespecificat`, in fragment order |
| `present` | union by name; role = first stated |
| `absent` | union, minus anyone who turned out to be present |
| `agenda_explicit` | true if any fragment says so |
| `agenda` | concatenated in order; an item with the same title as an earlier one is **merged** into it (discussions joined with a space); fragment ids remapped to global ids |
| `decisions`, `actions`, `open_issues` | appended in order with remapped `agenda_id`; exact duplicates (same description and agenda point) dropped |
| `next_meeting` | **last** stated value; it is usually agreed at the end |
| `summary` | fragment summaries joined; replaced by the consolidated summary (5.4) |

"Same name" and "same title" ignore case, diacritics (ș/ş, ț/ţ, ă, â, î), spacing and trailing
punctuation.

### 5.4 Consolidation: one small LLM call

Prompt: `SemanticKernel/Prompts/minutes_consolidation.system.txt`. Reply limit: 1024 tokens.

- **Input:** only the numbered agenda titles and each fragment's summary. It never contains
  discussions or the transcript, so it stays small at any length (measured 586–1189 tokens).
- **Output:**

  ```json
  {"merge":[{"id":5,"into":2}],"summary":"…"}
  ```

  Each object says that topic `id` is the same subject as the earlier topic `into`, worded
  differently. The summary is 3–6 sentences in Romanian for the whole meeting.

**Why the format is flat.** The first version asked for groups, `{"same_topic":[[2,5]]}`. On a real
long run the 7B model wrote `{"same_topic":[[1,2,5,6]],[1,2,5,6],…}`, repeating the nested array
outside it. It did so on both attempts, so the JSON broke every time. One flat object per merge
avoids nested integer arrays entirely. The prompt's example summary is `"..."`, because an earlier
example text ("Rezumat în română") was copied by the model as a prefix.

**How merges are applied** (`MeetingFactsMerger.ApplyTopicMerges`):
- A merge is ignored unless `id` and `into` both exist and `into` is earlier (`into < id`).
- An `id` named twice is ignored entirely.
- Chains (6→5, 5→2) resolve to the first topic.
- Discussions are joined in order and references are remapped.
- Rows that now duplicate each other (the same decision stated under both merged topics) are kept once.
- `NormalizeFacts` then renumbers the agenda 1..n.

**If consolidation fails** after retries (invalid JSON, an empty summary, …), topics are not merged
and the summary is the fragment summaries joined. The document is still produced. The log shows
`MOM consolidation failed; topics are not merged and the fragment summaries are joined`.

### 5.5 Document rendering in code (replaces the stage 2 LLM call)

The stage 2 prompt from commit `3172c27`, "Changed MoM type", was a fixed template where every line
copied a value from the facts: `**Tema:** meeting.title`, `### id. topic` followed by the discussion,
`## Rezumat` followed by the summary, and so on. The code already overwrote the agenda and the three
tables with its own rendering. `MinutesRenderer.Render` now produces the whole document from the
normalized facts, with the same headings, order and fixed texts:

```
# Proces-verbal al ședinței
**Tema:** … / **Data:** … | **Ora:** … | **Locul:** …
## Participanți        Președinte, Secretar, Prezenți ("Nume – funcție", …), Absenți ("Nu au fost consemnați." if none)
## Ordinea de zi       numbered topics + "*Ordinea de zi a fost stabilită pe baza temelor discutate.*" if inferred
## Desfășurarea ședinței   "### id. topic" + discussion per item (summary if the agenda is empty)
## Decizii / ## Acțiuni / ## Probleme deschise   tables with the "Punct" column, as before
## Următoarea ședință
## Rezumat
## Semnături           Președinte: … ____ / Secretar: … ____
```

Header values, names and agenda titles are forced onto one line. Paragraphs keep their line breaks
with normalized line endings. Table cells keep the existing `Cell` escaping.

**Gained:**
- no size limit;
- no model time: saving the 2-minute job went from ~30 s to 19 s;
- fidelity by construction: nothing can be paraphrased, dropped or replaced;
- `Absenți` now follows the template ("Nu au fost consemnați."); the model used to write "Nespecificat".

**Lost:** nothing in practice. The template already forbade the model from rewording anything.

`GenerateMinutesAsync(facts)` keeps its signature. The following are deleted:
`minutes_generation.system.txt`, the heading-order validation of model output, and
`Minutes:GenerationMaxTokens`.

### 5.6 Verification

`VerifyMinutesAsync(transcript, markdown, metadata, progress)` serves both the pipeline and user
edits (`POST /document/save/{jobId}` with a `delta`).

1. **Everything fits one request** (prompt + transcript + metadata + document ≤ verification budget):
   one request, all four finding kinds, as before.
2. **Otherwise the check is windowed.** The whole document is checked against one transcript window
   at a time. Window budget = verification budget − (prompt + metadata + document) − 100 tokens for
   the note: "Acesta este fragmentul k din n al transcriptului. Raportează doar discrepanțele pe care
   le arată acest fragment." ("This is fragment k of n of the transcript. Report only the
   discrepancies this fragment shows.")
   - `Contradiction`, `Misattribution` and `Omission` can be judged from one window. **`Unsupported`
     cannot**: "no support anywhere" needs the whole transcript. So it is dropped in code, even if the
     model returns it.
   - Findings from all windows are concatenated. Identical findings (same kind and quotes) are
     removed. Quotes are still resolved against the **full** transcript, metadata and document.
     `DiscardedFindings` is summed.
   - The summary is composed in code: `Verificat pe N fragmente: X discrepanțe. Afirmațiile fără
     suport în transcript nu pot fi verificate pe fragmente.` ("Checked in N fragments: X
     discrepancies. Claims without support in the transcript cannot be checked in fragments.")
   - The result carries **`Partial = true`**. `IsConsistent` is **never** true for a partial check, even
     with no findings, because the check for invented facts did not run.
   - **A window that fails** after retries: the others still count, `Completed = false`, and the
     summary names the fragment ("Fragmentele 2 nu au putut fi verificate.").
   - If windowing yields a single window, the check goes as the single request it is, so it does not
     lose `Unsupported` for nothing.
3. **The document alone leaves less than 1000 tokens** for a window: verification is skipped **without
   an exception**. `Completed = false`, with the summary "Verificarea automată nu a fost efectuată:
   documentul este prea mare pentru contextul modelului (Llm:ContextSize = …). Documentul necesită
   revizuire manuală." ("Automatic verification was not performed: the document is too large for the
   model's context. The document needs manual review.") This happens at 8k when the reply limits are
   raised to 4096 (see §8).

### 5.7 Progress

`MinutesProgress(MinutesStage Stage, int Current, int Total)` with `Extracting`, `Consolidating` and
`Verifying` comes out of the generator. `DocumentService` relays it as
`DocumentProgress(DocumentPhase Phase, int Current = 1, int Total = 1)`, and `DocumentPhase` gains
`Consolidating`. `GenerateMinutesStep` turns it into `Jobs.CurrentStep` and `Jobs.Percent` inside
its 92–100 band:

| Caption (`currentStep`) | Percent |
|---|---|
| `Генерация протокола: извлечение фактов (фрагмент k из n)` | 92 → 95 |
| `Генерация протокола: объединение фрагментов` | 95 |
| `Генерация протокола: составление документа` | 96 |
| `Генерация протокола: сверка с транскриптом (фрагмент k из n)` | 96 → 99 |
| `Генерация протокола` (done) | 100 |

With a single window, the "(фрагмент k из n)" suffix is omitted, as before. The step chain itself
is unchanged, so there is no `TranscriptionWorkflow` version bump.

---

## 6. API and UI contract changes

### `verification.partial` (new)

`MinutesVerification` has a new `Partial` property, serialized as `partial`. Documents saved before
this change don't have it.

| `completed` | `partial` | `isConsistent` | Meaning |
|---|---|---|---|
| true | false | true | full check, no discrepancies |
| true | false | false | full check, discrepancies in `findings` (or unprovable ones in `discardedFindings`) |
| true | **true** | false | **long recording checked per fragment**: contradictions, misattributions and omissions checked; claims without support were not |
| false | any | false | check failed or skipped; document saved unverified |

- **UI** (`mom-view.ts`): a partial check with no findings is shown with the neutral "unknown" tone
  and the label "Verificat pe fragmente: nicio neconcordanță; afirmațiile fără suport nu au fost
  verificate" (checked in fragments, no discrepancies; unsupported claims were not checked). It is
  never shown with the green "Verificat" state.
- **PDF** (`DocumentPdfRenderer`): the status line reads "Verificare automată pe fragmente: fără
  discrepanțe; afirmațiile fără suport nu au fost verificate."
- `health-tech-ui/src/app/api/models.ts` declares `partial?: boolean`.

### Errors

- The `422` for a transcript over 6000 characters is gone. There is no length limit.
- **`503 LlmConfiguration`**: the context cannot hold the prompts, or a `Minutes:*` setting is invalid.
  The `detail` names the setting.
- Nothing else changed: `404` for a missing job or document, `409` when the transcription is not
  finished, `422` for an unreadable transcript file.

### Timing

`POST /document/save/{jobId}` without a body now takes from about a minute to 5 minutes on the Mac
GPU, and several times longer on a CPU. It grows with the recording's length (see §9).

---

## 7. Configuration

Defaults live in `SemanticKernel/appsettings.llm.json`. App config and environment variables
(`Llm__*`, `Minutes__*`) override them.

| Setting | Default | Status | Notes |
|---|---|---|---|
| `Llm:ContextSize` | 8192 | unchanged | every budget derives from it |
| `Llm:MaxTokens` | 2048 | unchanged | correction reply limit; batches use ≤ 80% of it |
| `Llm:BatchSize` | 15 | unchanged | max pieces per correction batch |
| `Llm:MaxBatchCharacters` | — | **removed** | replaced by token sizing |
| `Minutes:MaxWindowTokens` | **4000** | **new** | cap on an extraction window on every machine; minimum 1000 |
| `Minutes:ExtractionMaxTokens` | 3072 | unchanged | extraction reply limit |
| `Minutes:VerificationMaxTokens` | 2048 | unchanged | verification reply limit |
| `Minutes:MaxRetries` | 1 | unchanged | |
| `Minutes:MaxTranscriptCharacters` | — | **removed** | |
| `Minutes:MaxFactsCharacters` | — | **removed** | |
| `Minutes:MaxVerificationCharacters` | — | **removed** | |
| `Minutes:GenerationMaxTokens` | — | **removed** | no generation call |

Removed keys that are still set in an environment are ignored.

**`mac` launch profile** (`HealthTech/Properties/launchSettings.json`): only the four dead `Minutes__*`
keys were removed. It still sets:
- `Llm__ContextSize=32768`
- `Llm__GpuLayerCount=999`
- `Llm__MaxTokens=4096`
- `Minutes__ExtractionMaxTokens=4096`
- `Minutes__VerificationMaxTokens=4096`

The `http`/`https` (Windows) profiles are untouched.

> **Pitfall: env vars and launch profiles.** `dotnet run --launch-profile mac` overwrites any
> environment variable the profile itself defines. `Llm__ContextSize=8192 dotnet run --launch-profile mac`
> therefore runs at **32768**. To override one of those keys, run with `--no-launch-profile` and set
> the profile's variables yourself, plus `ASPNETCORE_URLS=http://localhost:5089`.

> **Pitfall: Rider.** Rider's run configuration picks the profile. With `HealthTech: http` selected on
> the Mac, the LLM runs on the CPU with an 8k context (this is what happened on 26 September).
> Select **`HealthTech: mac`**.

**Choosing reply limits for a small context.** Replies of 4096 tokens at an 8k context leave only
3687 tokens of input. A 40-minute document then no longer leaves room for a verification window,
and verification is skipped (§5.6, case 3). On an 8k machine keep the shipped defaults (2048 / 3072
/ 2048). Raise reply limits only together with the context.

---

## 8. Measurements (M3 Max, Metal)

| Run | Settings | Result |
|---|---|---|
| Correction of the 10-min job that hit `NoKvSlot` | 8192, Mac replies | HTTP 200 in 2:48; 13 batches, largest 3640 of 3687 input tokens; **0** overflows (before: 2 of 3 batches lost, 100–270 s each on CPU) |
| Minutes of the 2-min job | 32768 | 19 s (was ~30 s with the generation call); headings identical to the LLM version |
| Minutes of the 10-min job, forced windowing (`MaxWindowTokens=1000`) | 32768 | 72 s; 4 fragments of 891/955/522/899 tokens, each starting with a speaker line; facts in = facts out (0 decisions, 0 actions, 2 issues) |
| Minutes of the 10-min job | 8192, Mac replies | 102 s; extraction 2 fragments (3089, 2588 of 3687); verification 3 fragments (3595/3300/3160 of 3687); `partial=true` |
| Full workflow upload of the 10-min file | 32768, `MaxWindowTokens=1000` | Completed; captions in order 92→94 (fragments 1–4), 95, 96, 96, 100; correction 3 batches |
| Synthetic ~40-min transcript (35,530 chars, one 14,090-char monologue) | 8192, Mac replies | 211 s; 8 fragments ≤ 2272 tokens, monologue split; verification **skipped** (document left 292 tokens), honestly reported |
| Same transcript | 8192, laptop defaults | 290 s; 6 extraction fragments (max 4463 of 4711); consolidation OK; verification 8 fragments (max 5646 of 5735), completed |
| Same transcript | 32768, `mac` profile | 135 s; 5 fragments (cap 4000); verification single pass (16,629 of 27,034 tokens) |
| Context too small | `ContextSize=3000` | 503 in 0.43 s, then 0.003 s (no reload) |
| Invalid setting | `MaxWindowTokens=500` | 503 naming the setting |

The synthetic transcript repeats the same 10-minute meeting, so one agenda item after merging is the
correct outcome.

---

## 9. Diagnosing from the logs

Logs: `logs/healthtech-YYYYMMDD.log` at the repo root. Every LLM request logs its size as numbers
only, never content.

| Log line | Meaning |
|---|---|
| `LLM context check: C tokens, largest prompt P N tokens, largest reply R` | the model loaded and the context fits |
| `Medical correction: P piece(s) sized into B batch(es) in S s` | correction batches planned |
| `Medical correction batch k: X/Y input tokens, n piece(s)` | one correction request; X must be ≤ Y |
| `Medical correction piece i alone needs X input tokens, budget Y; sent anyway` | one piece exceeds the budget by itself (huge glossary/`lowConfidence`) |
| `MOM extraction: transcript split into N fragments of at most B tokens` | windowed extraction started |
| `MOM stage Fact Extraction k/N: X/Y input tokens` | one extraction request |
| `MOM extraction fragment k: topics hint kept a of b topics to fit the context` | the oldest topics were left out of the hint |
| `MOM stage Consolidation: X/Y input tokens` | the consolidation request |
| `MOM consolidation failed; topics are not merged and the fragment summaries are joined` | fallback used; the document is still saved |
| `MOM verification: transcript split into N fragments of at most B tokens` | windowed verification, so `partial=true` |
| `MOM stage Minutes Verification k/N: X/Y input tokens` | one verification request |
| `MOM verification fragment k failed` | that fragment is unchecked; `completed=false` |
| `MOM verification skipped: the document leaves X tokens for the transcript (context C)` | document too large for the context; raise the context or lower `VerificationMaxTokens` |
| `MOM stage Minutes Verification: a of b finding(s) discarded, evidence not found in the source` | pre-existing: the model's quote was not found (common on garbled audio) |
| `MOM stage S: X/Y input tokens` at **Warning** level | a request exceeded its budget; should not happen, please report |

**Symptoms and what to check:**
- **`NoKvSlot` / `ContextOverflowException` again.** Look for a Warning-level `X/Y` line. Check
  whether a prompt file was made much longer; the context check only looks at `Prompts/*.system.txt`
  in the output folder.
- **Minutes are slow on the Mac.** `Loading LLM … (context 8192, GPU layers 0)` means the wrong launch
  profile (see §7).
- **Many near-identical agenda items.** Consolidation failed (look for its warning) or the topics
  hint was trimmed.

---

## 10. Known limitations

- **Windowed verification can't catch invented facts.** In `partial` mode `Unsupported` is not
  checked, which is why such documents are never shown as fully verified. With extraction windows of
  ≤ 4000 tokens and a document rendered in code, invented facts are less likely, but not impossible.
- **Findings are judged one fragment at a time.** An `Omission` or `Contradiction` found against one
  fragment can occasionally be settled by another fragment.
- **Duplicate detection is exact.** A decision restated in different words in two fragments stays
  twice.
- **One failed extraction fragment fails the whole minutes generation.** The job still completes,
  and `POST /document/save` can retry.
- **Correction retries repeat the same answer.** Retries run at temperature 0 with the same history.
  This predates this work.
- **Stale prompt copies can linger in `bin/Prompts`.** MSBuild does not delete output files whose
  source was removed. A stale `minutes_generation.system.txt` is harmless (it is smaller than the
  largest prompt); a clean build removes it.

---

## 11. File map

**New**
- `SemanticKernel/TokenBudget.cs`: `ITokenCounter`, `TokenBudget`, `LlmConfigurationException`
- `SemanticKernel/Minutes/TranscriptWindows.cs`: splitting the transcript into token windows
- `SemanticKernel/Minutes/MeetingFactsMerger.cs`: merging fragments and applying topic merges
- `SemanticKernel/Minutes/MinutesRenderer.cs`: rendering the document in code
- `SemanticKernel/Minutes/MinutesProgress.cs`: `MinutesStage`, `MinutesProgress`
- `SemanticKernel/Prompts/minutes_consolidation.system.txt`

**Changed**
- `SemanticKernel/KernelFactory.cs`: token counting, context check, cached configuration error
- `SemanticKernel/MedicalCorrection/MedicalTermCorrector.cs`: token-based batches, per-request logging
- `SemanticKernel/Minutes/MeetingMinutesGenerator.cs`: windowed extraction, consolidation, windowed verification, code rendering
- `SemanticKernel/Minutes/MinutesVerification.cs`: `Partial`
- `SemanticKernel/Minutes/IMeetingMinutesGenerator.cs`, `MinutesOptions.cs`, `LlmOptions.cs`, `ServiceCollectionExtensions.cs`, `appsettings.llm.json`, `Minutes/README.md`
- `HealthTech/Documents/DocumentModels.cs` (`DocumentProgress`, `DocumentPhase.Consolidating`), `DocumentService.cs` (progress relay), `DocumentPdfRenderer.cs` (partial status line)
- `HealthTech/Workflow/Steps/GenerateMinutesStep.cs`: fragment captions and percents
- `HealthTech/Transcription/LlmModelNotFoundExceptionHandler.cs`: 503 for `LlmConfigurationException`
- `HealthTech/Properties/launchSettings.json`: `mac` profile only
- `health-tech-ui/src/app/api/models.ts`, `health-tech-ui/src/app/pages/record/mom-view.ts`: the `partial` label
- `CLAUDE.md`, `docs/ui-integration.md`

**Deleted**
- `SemanticKernel/Prompts/minutes_generation.system.txt`

---

## 12. Notes for the team

- **The stage 2 LLM call from "Changed MoM type" (`3172c27`) is replaced by code rendering of the
  same template.** If the minutes should ever contain prose the model actually writes (rather than
  copies), the renderer is the place to add a new, bounded model call for that section only.
- **The Windows `http`/`https` profiles keep the shipped defaults** (8k context, 2048/3072/2048
  replies). That combination now handles long recordings. Raise the context there only if the
  hardware allows it.
- **Recordings on noisy audio produce many `lowConfidence` words.** They are now counted, so they
  only make correction batches smaller; they no longer overflow the context.
