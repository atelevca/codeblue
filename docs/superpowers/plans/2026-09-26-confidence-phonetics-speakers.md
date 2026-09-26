# Уверенность ASR, фонетический список и привязка спикеров: план реализации

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Довести до конца этапы 5–7 спеки: передать в LLM сведения о неуверенно распознанных словах, подключить список известных фонетических ошибок и дать возможность вручную сопоставить «Speaker N» с врачом из справочника.

**Architecture:** Whisper начинает отдавать вероятности токенов; слова собираются из токенов, слово с вероятностью ниже порога попадает в `lowConfidence` сегмента со смещением в тексте. Смещение переживает две склейки — сегменты в реплику и реплика в куски — и доезжает до запроса к модели. Фонетический список подключается как ещё один глоссарий: формат тот же, механизм отбора строк тот же. Привязка спикеров живёт только в базе (`Persons`, `SpeakerBindings`), файлы транскриптов не переписываются.

**Tech Stack:** ASP.NET Core .NET 10, Whisper.net 1.9.1, Dapper + Microsoft.Data.Sqlite, LLamaSharp + Semantic Kernel.

**Spec:** `docs/superpowers/specs/2026-09-26-transcription-workflow-design.md` (этапы 5–7 раздела 12; предметные разделы 7, 8, 9)

## Global Constraints

- **Тесты не пишутся.** Правило проекта из `CLAUDE.md`. Каждая задача проверяется запуском приложения и вызовом эндпоинтов; шаги проверки обязательны, их вывод и есть доказательство.
- **Логики в контроллерах нет.** Контроллер принимает запрос, вызывает сервис, возвращает результат. Доменные исключения превращаются в HTTP в `IExceptionHandler`.
- Доступ к `healthtech.db` — Dapper. EF Core нашим кодом не используется.
- Комментарии в изменяемых файлах — на русском, как в окружающем коде; поясняют «почему».
- Полный прогон одной записи занимает около десяти минут (Whisper ~5 мин + LLM-коррекция на CPU ~5 мин). **Запускать несколько записей параллельно нельзя**: на прошлом этапе два одновременных контекста LLM подняли процесс до 10.9 ГБ и система начала гасить процессы. Проверять по одной.
- `Whisper:Language` остаётся `ro`.

## Review Focus

Пять мест, которые обычная проверка «прогнал запись, посмотрел результат» не задевает:

1. **Сегмент, в котором собранное из токенов слово не находится в тексте сегмента** (Whisper тримит и нормализует текст). Ожидание: слово молча пропускается, остальные смещения не съезжают, сегмент не теряется. Закреплено в задаче 1, шаг 6.
2. **Реплика, склеенная из трёх и более сегментов.** Ожидание: смещения второго и третьего сегмента сдвинуты на суммарную длину предыдущих плюс разделители, и слово по смещению действительно совпадает с записанным. Закреплено в задаче 2, шаг 5.
3. **Реплика длиннее `MaxPieceCharacters`, разрезанная на куски.** Ожидание: слово попадает ровно в один кусок, смещение внутри куска верное, слова на границе не дублируются и не теряются. Закреплено в задаче 3, шаг 5.
4. **Повторный `POST /audio/correctTranscript` по готовому файлу.** Ожидание: `lowConfidence` читается из файла и уходит в модель так же, как при полном прогоне; файл без этого поля (созданный до изменения) обрабатывается без ошибки. Закреплено в задаче 4, шаг 4.
5. **`PUT /jobs/{id}/speakers` с меткой, которой нет в записи, и с несуществующим `personId`.** Ожидание: 400 в обоих случаях, ни одна привязка не сохранена (всё или ничего). Закреплено в задаче 6, шаг 5.

---

### Task 1: Вероятности токенов и сборка слов в Whisper

**Files:**
- Modify: `HealthTech/Transcription/WhisperOptions.cs`
- Create: `SemanticKernel/MedicalCorrection/LowConfidenceWord.cs`
- Modify: `HealthTech/Transcription/TranscriptModels.cs`
- Modify: `HealthTech/Transcription/WhisperSpeechRecognitionService.cs`
- Modify: `HealthTech/appsettings.json`

**Interfaces:**
- Consumes: `WhisperProcessorBuilder.WithProbabilities()`; `SegmentData.Tokens` (`Whisper.net.WhisperToken[]`). У `WhisperToken` `Text` и `Probability` — **публичные поля**, а не свойства (проверено рефлексией по 1.9.1).
- Produces: `record LowConfidenceWord(int At, string Word, double P)` в `SemanticKernel.MedicalCorrection`; `TranscriptSegment` получает шестое свойство `IReadOnlyList<LowConfidenceWord> LowConfidence`.

- [ ] **Step 1: Порог в настройках**

В `HealthTech/Transcription/WhisperOptions.cs` добавить свойство:

```csharp
        /// <summary>
        /// Слово с вероятностью ниже этого значения помечается как неуверенно распознанное.
        /// 0.5 отобрано на глаз: ниже - список раздувается обычными словами, выше - пропускает термины.
        /// </summary>
        public double LowConfidenceThreshold { get; set; } = 0.5;
```

В `HealthTech/appsettings.json`, в секцию `Whisper`, после `"NoContext": true`:

```json
    "LowConfidenceThreshold": 0.5,
```

- [ ] **Step 2: Тип слова**

Создать `SemanticKernel/MedicalCorrection/LowConfidenceWord.cs`:

```csharp
namespace SemanticKernel.MedicalCorrection
{
    /// <summary>
    /// Слово, распознанное неуверенно. <see cref="At"/> - смещение в символах от начала текста,
    /// которому слово принадлежит; оно пересчитывается на каждой склейке текстов.
    /// Тип лежит рядом с <see cref="Segment"/>, чтобы оба проекта видели его без преобразований.
    /// </summary>
    public record LowConfidenceWord(int At, string Word, double P);
}
```

- [ ] **Step 3: Поле в сегменте транскрипта**

В `HealthTech/Transcription/TranscriptModels.cs` добавить `using SemanticKernel.MedicalCorrection;` наверх и заменить `TranscriptSegment`:

```csharp
    /// <summary>A piece of recognized text. Times are in seconds from the start of the audio.</summary>
    public record TranscriptSegment(double Start, double End, string Text)
    {
        /// <summary>
        /// Неуверенно распознанные слова со смещениями внутри <see cref="Text"/>.
        /// Пустой список у сегментов, прочитанных из файлов, записанных до появления поля.
        /// </summary>
        public IReadOnlyList<LowConfidenceWord> LowConfidence { get; init; } = [];
    }
```

Свойство с инициализатором, а не позиционный параметр: `TranscriptSegment` создаётся в трёх местах, и позиционный параметр заставил бы править их все, а `with { }` в `ProcessedAudioTranscriptionService` остаётся работать без изменений.

- [ ] **Step 4: Включить вероятности и собрать слова**

В `WhisperSpeechRecognitionService.TranscribeAsync`, к цепочке билдера:

```csharp
                var builder = _factory.CreateBuilder()
                    .WithLanguage(_options.Language)
                    .WithProbabilities()
                    .WithThreads(Environment.ProcessorCount);
```

И в цикле по сегментам:

```csharp
                    var text = segment.Text.Trim();
                    if (text.Length > 0)
                    {
                        segments.Add(new TranscriptSegment(segment.Start.TotalSeconds, segment.End.TotalSeconds, text)
                        {
                            LowConfidence = LowConfidenceWords(segment, text, _options.LowConfidenceThreshold)
                        });
                    }
```

Новый приватный метод в том же классе:

```csharp
        /// <summary>
        /// Собирает слова из подсловных токенов и возвращает те, чья вероятность ниже порога.
        /// Вероятность слова - минимум по его токенам: для пометки подозрительных мест нужен
        /// консервативный агрегат, среднее прячет один плохой кусок внутри длинного слова.
        /// </summary>
        private static List<LowConfidenceWord> LowConfidenceWords(SegmentData segment, string text, double threshold)
        {
            if (segment.Tokens is not { Length: > 0 })
            {
                return [];
            }

            var words = new List<(string Word, double P)>();
            var current = "";
            var probability = 1.0;

            foreach (var token in segment.Tokens)
            {
                // Служебные токены whisper.cpp ([_BEG_], [_TT_123] и прочие) в тексте не появляются.
                var piece = token.Text;
                if (string.IsNullOrEmpty(piece) || piece.StartsWith("[_", StringComparison.Ordinal))
                {
                    continue;
                }

                // Токен, начинающийся с пробела, открывает новое слово; остальные приклеиваются.
                if (piece.StartsWith(' ') && current.Length > 0)
                {
                    words.Add((current, probability));
                    current = "";
                    probability = 1.0;
                }

                current += piece.TrimStart();
                probability = Math.Min(probability, token.Probability);
            }
            if (current.Length > 0)
            {
                words.Add((current, probability));
            }

            // Смещения ищутся по тексту от курсора, а не считаются по токенам: segment.Text
            // тримится и может не совпасть со склейкой токенов посимвольно, а поиск от курсора
            // к такому расхождению устойчив. Не найденное слово просто пропускается.
            var result = new List<LowConfidenceWord>();
            var cursor = 0;
            foreach (var (word, p) in words)
            {
                var trimmed = word.Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                var at = text.IndexOf(trimmed, cursor, StringComparison.Ordinal);
                if (at < 0)
                {
                    continue;
                }
                cursor = at + trimmed.Length;

                if (p < threshold)
                {
                    result.Add(new LowConfidenceWord(at, trimmed, Math.Round(p, 3)));
                }
            }
            return result;
        }
```

Добавить `using SemanticKernel.MedicalCorrection;` в начало файла.

- [ ] **Step 5: Сборка**

```sh
taskkill //F //IM HealthTech.exe; dotnet build
```
Expected: `Build succeeded`.

- [ ] **Step 6: Проверка на реальной записи**

Запустить приложение, загрузить и оформить **одну** запись типа `medical`, дождаться `Completed` (около десяти минут), затем посмотреть распознанный файл:

```sh
python -c "
import io,json,glob
p=glob.glob('transcripts/<jobId>/*.json')
f=[x for x in p if 'diariz' not in x and 'speakers' not in x][0]
d=json.load(io.open(f,encoding='utf-8'))
n=0
for s in d['segments']:
    for w in s.get('lowConfidence',[]):
        n+=1
        assert s['text'][w['at']:w['at']+len(w['word'])]==w['word'], (s['text'], w)
        if n<=10: print(round(w['p'],2), repr(w['word']))
print('всего слов:', n, 'в сегментах:', len(d['segments']))
"
```
Expected: assert не срабатывает ни разу — это и есть проверка Review Focus 1: каждое смещение указывает ровно на записанное слово, а слова, которые не нашлись, отброшены. Список непустой и на глаз состоит из сомнительных мест, а не из служебных слов. Если список пуст на всей записи — порог или сборка токенов не работают, разбираться по `systematic-debugging`, а не поднимать порог наугад.

- [ ] **Step 7: Commit**

```bash
git add HealthTech/Transcription/WhisperOptions.cs SemanticKernel/MedicalCorrection/LowConfidenceWord.cs HealthTech/Transcription/TranscriptModels.cs HealthTech/Transcription/WhisperSpeechRecognitionService.cs HealthTech/appsettings.json
git commit -m "feat: вероятности токенов и список неуверенно распознанных слов"
```

---

### Task 2: Смещения переживают склейку сегментов в реплику

**Files:**
- Modify: `HealthTech/Transcription/TranscriptModels.cs`
- Modify: `HealthTech/Transcription/SpeakerAlignmentService.cs`

**Interfaces:**
- Consumes: `TranscriptSegment.LowConfidence` (задача 1).
- Produces: `SpeakerTranscriptTurn` получает свойство `IReadOnlyList<LowConfidenceWord> LowConfidence` со смещениями относительно `Text` реплики.

Первая склейка (чанк → файл) смещений не трогает: текст сегмента там не меняется, сдвигаются только временные метки. Правка нужна только здесь.

- [ ] **Step 1: Поле в реплике**

В `TranscriptModels.cs` заменить `SpeakerTranscriptTurn`:

```csharp
    /// <summary>A speaker turn with its text: consecutive transcript segments of one speaker merged together.</summary>
    public record SpeakerTranscriptTurn(double Start, double End, string StartTime, string EndTime, string Speaker, string Text)
    {
        /// <summary>Неуверенно распознанные слова со смещениями внутри <see cref="Text"/> реплики.</summary>
        public IReadOnlyList<LowConfidenceWord> LowConfidence { get; init; } = [];
    }
```

- [ ] **Step 2: Сдвиг при слиянии**

В `SpeakerAlignmentService.Align` заменить тело цикла:

```csharp
                var speaker = FindSpeaker(segment, speakerTurns);
                if (turns.Count > 0 && turns[^1].Speaker == speaker)
                {
                    var last = turns[^1];

                    // Тексты склеиваются через один пробел, поэтому слова присоединяемого
                    // сегмента съезжают ровно на длину накопленного текста плюс этот пробел.
                    var shift = last.Text.Length + 1;
                    turns[^1] = last with
                    {
                        End = segment.End,
                        EndTime = TranscriptTime.Format(segment.End),
                        Text = last.Text + " " + text,
                        LowConfidence = [.. last.LowConfidence,
                            .. Shift(segment.LowConfidence, shift, text, segment.Text)]
                    };
                }
                else
                {
                    turns.Add(new SpeakerTranscriptTurn(segment.Start, segment.End,
                        TranscriptTime.Format(segment.Start), TranscriptTime.Format(segment.End), speaker, text)
                    {
                        LowConfidence = Shift(segment.LowConfidence, 0, text, segment.Text)
                    });
                }
```

И новый статический метод:

```csharp
        /// <summary>
        /// Переносит слова сегмента в систему координат реплики. Смещения считались от
        /// нетримленного <paramref name="originalText"/>, а в реплику попадает тримленный
        /// <paramref name="trimmedText"/>, поэтому вычитается длина срезанного слева.
        /// Слово, съехавшее за границы, отбрасывается: лучше потерять пометку, чем указать не туда.
        /// </summary>
        private static List<LowConfidenceWord> Shift(
            IReadOnlyList<LowConfidenceWord> words, int shift, string trimmedText, string originalText)
        {
            if (words.Count == 0)
            {
                return [];
            }

            var trimmedLeft = originalText.Length - originalText.TrimStart().Length;
            var result = new List<LowConfidenceWord>(words.Count);
            foreach (var word in words)
            {
                var at = word.At - trimmedLeft + shift;
                if (at < shift || at + word.Word.Length > shift + trimmedText.Length)
                {
                    continue;
                }
                result.Add(word with { At = at });
            }
            return result;
        }
```

Добавить `using SemanticKernel.MedicalCorrection;` в начало файла.

- [ ] **Step 3: Перенос поля в результат**

В `HealthTech/Workflow/Steps/SaveResultStep.cs` ничего менять не нужно: `SpeakerTranscriptResult` хранит сами `SpeakerTranscriptTurn`, и поле поедет в `.speakers.json` автоматически. Убедиться в этом глазами при проверке.

- [ ] **Step 4: Сборка**

```sh
taskkill //F //IM HealthTech.exe; dotnet build
```
Expected: `Build succeeded`.

- [ ] **Step 5: Проверка смещений после склейки**

Прогнать **одну** запись целиком до `Completed` и проверить реплики:

```sh
python -c "
import io,json,glob
f=glob.glob('transcripts/<jobId>/*.speakers.json')[0]
d=json.load(io.open(f,encoding='utf-8'))
bad=0; total=0
for t in d['turns']:
    for w in t.get('lowConfidence',[]):
        total+=1
        if t['text'][w['at']:w['at']+len(w['word'])]!=w['word']:
            bad+=1
            print('MISMATCH', repr(w['word']), 'got', repr(t['text'][w['at']:w['at']+len(w['word'])]))
print('слов:', total, 'ошибочных смещений:', bad)
print('реплик:', len(d['turns']), 'длины:', [len(t['text']) for t in d['turns']])
"
```
Expected: `ошибочных смещений: 0`. Это Review Focus 2: длины реплик показывают, что склейка из нескольких сегментов действительно была (реплика заметно длиннее одного сегмента), и ни одно смещение не съехало.

- [ ] **Step 6: Commit**

```bash
git add HealthTech/Transcription/TranscriptModels.cs HealthTech/Transcription/SpeakerAlignmentService.cs
git commit -m "feat: смещения неуверенных слов переживают склейку сегментов в реплику"
```

---

### Task 3: Неуверенные слова доезжают до запроса к модели

**Files:**
- Modify: `SemanticKernel/MedicalCorrection/Segment.cs`
- Modify: `SemanticKernel/MedicalCorrection/MedicalTermCorrector.cs`
- Modify: `SemanticKernel/Prompts/medical_correction.system.txt`
- Modify: `HealthTech/Workflow/Steps/CorrectTermsStep.cs`

**Interfaces:**
- Consumes: `SpeakerTranscriptTurn.LowConfidence` (задача 2); `LowConfidenceWord` (задача 1).
- Produces: `Segment` получает свойство `IReadOnlyList<LowConfidenceWord> LowConfidence`; запрос к модели получает поле `lowConfidence` у каждого элемента `segments`.

- [ ] **Step 1: Поле в сегменте коррекции**

`SemanticKernel/MedicalCorrection/Segment.cs` целиком:

```csharp
namespace SemanticKernel.MedicalCorrection
{
    /// <summary>A speaker-attributed piece of transcript. Only <see cref="Text"/> may be changed by the correction.</summary>
    public record Segment(int Id, string Speaker, TimeSpan Start, TimeSpan End, string Text)
    {
        /// <summary>Неуверенно распознанные слова со смещениями внутри <see cref="Text"/>.</summary>
        public IReadOnlyList<LowConfidenceWord> LowConfidence { get; init; } = [];
    }
}
```

- [ ] **Step 2: Раскладка слов по кускам**

В `MedicalTermCorrector` расширить запись куска и `SplitIntoPieces`:

```csharp
        private record Piece(int Id, int SegmentIndex, Segment Segment, string Leading, string Text, string Trailing)
        {
            public IReadOnlyList<LowConfidenceWord> LowConfidence { get; init; } = [];
        }
```

```csharp
        // Pieces get ids 1..N over the whole run; whitespace-only pieces are not sent.
        private List<Piece> SplitIntoPieces(IReadOnlyList<Segment> segments)
        {
            var pieces = new List<Piece>();
            for (var index = 0; index < segments.Count; index++)
            {
                var segment = segments[index];

                // Куски покрывают текст ровно, поэтому абсолютное начало куска - это сумма
                // длин предыдущих. Слово переносится в тот кусок, внутрь которого попало целиком.
                var partStart = 0;
                foreach (var part in TextPieces.Split(segment.Text, Math.Max(1, _options.MaxPieceCharacters)))
                {
                    var text = part.Trim();
                    if (text.Length == 0)
                    {
                        partStart += part.Length;
                        continue;
                    }
                    var leading = part[..part.IndexOf(text, StringComparison.Ordinal)];
                    var textStart = partStart + leading.Length;

                    var words = segment.LowConfidence
                        .Where(w => w.At >= textStart && w.At + w.Word.Length <= textStart + text.Length)
                        .Select(w => w with { At = w.At - textStart })
                        .ToList();

                    pieces.Add(new Piece(pieces.Count + 1, index, segment, leading, text,
                        part[(leading.Length + text.Length)..]) { LowConfidence = words });
                    partStart += part.Length;
                }
            }
            return pieces;
        }
```

- [ ] **Step 3: Слова в запросе**

В `BuildUserMessage` заменить построение `request`:

```csharp
            var request = new
            {
                context = context.Select(p => new { id = p.Id, text = Tail(p.Text, MaxContextPieceLength) }),
                // lowConfidence опускается у кусков без подозрительных слов: пустой массив в
                // каждом элементе - это лишние токены в каждом запросе и ничего больше.
                segments = batch.Select(p => p.LowConfidence.Count == 0
                    ? (object)new { id = p.Id, text = p.Text }
                    : new { id = p.Id, text = p.Text, lowConfidence = p.LowConfidence })
            };
```

`LowConfidenceWord` сериализуется как `{ "at": .., "word": "..", "p": .. }` — `RequestJsonOptions` построен на `JsonSerializerDefaults.Web`, то есть camelCase.

- [ ] **Step 4: Абзац в системном промпте**

В `SemanticKernel/Prompts/medical_correction.system.txt` после строки `Items in "context" are for understanding only. Do NOT return them.` вставить:

```text
A segment may carry a "lowConfidence" list: words the recognizer itself was unsure about, with their
offset in the text and the probability. Start with those — they are the most likely errors. This is a
hint, not a restriction: a clear mishearing outside the list may be fixed too, and a word in the list
that is obviously correct must be left alone.
```

Общий промпт `general_correction.system.txt` получает тот же абзац дословно.

- [ ] **Step 5: Передать слова из реплик в коррекцию**

В `HealthTech/Workflow/Steps/CorrectTermsStep.cs` — при построении сегментов:

```csharp
            // Индекс реплики + 1 = идентификатор сегмента: по нему правка находит себя обратно.
            var segments = turns
                .Select((t, i) => new Segment(i + 1, t.Speaker,
                    TimeSpan.FromSeconds(t.Start), TimeSpan.FromSeconds(t.End), t.Text)
                {
                    LowConfidence = t.LowConfidence
                })
                .ToList();
```

И при записи исправленных реплик обратно:

```csharp
            // У реплики, текст которой изменился, смещения указывают в старый текст и починить
            // их нечем: модель переписала кусок целиком. Список сбрасывается - пустой честнее
            // указывающего не туда. У нетронутых реплик он остаётся.
            var updated = turns
                .Select((t, i) => t.Text == corrected[i].Text
                    ? t
                    : t with { Text = corrected[i].Text, LowConfidence = [] })
                .ToList();
```

- [ ] **Step 6: Сборка и проверка раскладки по кускам**

```sh
taskkill //F //IM HealthTech.exe; dotnet build
```
Expected: `Build succeeded`.

Прогнать **одну** запись целиком. В логе коррекции ничего нового не появится, поэтому проверяется результат и отчёт:

```sh
python -c "
import io,json,glob
f=glob.glob('transcripts/<jobId>/*.speakers.json')[0]
d=json.load(io.open(f,encoding='utf-8'))
for t in d['turns']:
    print(len(t['text']), 'симв.,', len(t.get('lowConfidence',[])), 'неуверенных слов')
"
```
Expected: у реплик, которые коррекция не тронула, список сохранился и смещения по-прежнему совпадают (проверка та же, что в задаче 2); у изменённых он пуст. Review Focus 3: если хотя бы одна реплика длиннее `MaxPieceCharacters` (значение в `SemanticKernel/appsettings.llm.json`), значит разрезание на куски было задействовано; убедиться, что слова из неё не потерялись целиком и не задвоились.

- [ ] **Step 7: Commit**

```bash
git add SemanticKernel/MedicalCorrection/Segment.cs SemanticKernel/MedicalCorrection/MedicalTermCorrector.cs SemanticKernel/Prompts/medical_correction.system.txt SemanticKernel/Prompts/general_correction.system.txt HealthTech/Workflow/Steps/CorrectTermsStep.cs
git commit -m "feat: неуверенно распознанные слова уходят в запрос к модели"
```

---

### Task 4: Повторная коррекция читает `lowConfidence` из файла

**Files:**
- Modify: `HealthTech/Transcription/TranscriptCorrectionService.cs`

**Interfaces:**
- Consumes: `Segment.LowConfidence` (задача 3); поле `lowConfidence` в `.speakers.json` и `.json`.
- Produces: ничего нового.

`POST /audio/correctTranscript` должен давать тот же результат, что полный прогон, иначе подбирать глоссарии по готовому файлу бессмысленно: модель будет видеть меньше, чем видела в конвейере.

- [ ] **Step 1: Прочитать поле из файла**

В `TranscriptCorrectionService.CorrectTranscriptAsync` — при построении сегментов (`FindItems` возвращает `List<JsonObject>`):

```csharp
            var segments = items.Select((item, i) => new Segment(
                    i + 1,
                    ReadString(item, "speaker") ?? "",
                    TimeSpan.FromSeconds(ReadSeconds(item, "start")),
                    TimeSpan.FromSeconds(ReadSeconds(item, "end")),
                    ReadString(item, "text")!)
                {
                    LowConfidence = ReadLowConfidence(item)
                })
                .ToList();
```

Рядом с `ReadString` добавить:

```csharp
        // Поле необязательное: файлы, записанные до его появления, читаются как раньше -
        // с пустым списком, а не с ошибкой. Битый элемент пропускается, весь файл из-за
        // одного кривого слова терять незачем.
        private static IReadOnlyList<LowConfidenceWord> ReadLowConfidence(JsonObject item)
        {
            if (item["lowConfidence"] is not JsonArray array)
            {
                return [];
            }

            var words = new List<LowConfidenceWord>(array.Count);
            foreach (var node in array)
            {
                if (node is not JsonObject word
                    || word["at"] is not JsonValue atValue || !atValue.TryGetValue<int>(out var at)
                    || word["word"] is not JsonValue wordValue || !wordValue.TryGetValue<string>(out var text)
                    || string.IsNullOrEmpty(text))
                {
                    continue;
                }

                var p = word["p"] is JsonValue pValue && pValue.TryGetValue<double>(out var value) ? value : 0;
                words.Add(new LowConfidenceWord(at, text, p));
            }
            return words;
        }
```

Добавить `using SemanticKernel.MedicalCorrection;`, если его ещё нет в файле.

- [ ] **Step 2: Сбросить поле там, где текст изменился**

Заменить цикл записи исправленных текстов:

```csharp
            for (var i = 0; i < items.Count; i++)
            {
                if (ReadString(items[i], "text") == corrected[i].Text)
                {
                    continue;
                }

                items[i]["text"] = corrected[i].Text;
                // Текст переписан - смещения указывают в старую строку, и починить их нечем.
                // Поле убирается целиком: отсутствующее честнее указывающего не туда.
                // То же правило работает в конвейере (CorrectTermsStep).
                items[i].Remove("lowConfidence");
            }
```

- [ ] **Step 3: Сборка**

```sh
taskkill //F //IM HealthTech.exe; dotnet build
```
Expected: `Build succeeded`.

- [ ] **Step 4: Проверка на новом и на старом файле**

Запустить приложение. Взять запись, прогнанную в задаче 3, и повторить коррекцию:

```sh
curl.exe -s -X POST "http://localhost:5089/audio/correctTranscript?jobId=<jobId>&fileName=<имя>.speakers.json&profile=medical"
```
Expected: HTTP 200, в ответе `segmentCount` и список изменений; в `<имя>.speakers.corrected.json` поле `lowConfidence` присутствует у неизменённых реплик.

Теперь старый файл — из задания, обработанного до этой работы (в `transcripts/` такие есть):

```sh
curl.exe -s -o /dev/null -w "%{http_code}\n" -X POST "http://localhost:5089/audio/correctTranscript?jobId=<старыйJobId>&fileName=<имя>.speakers.json&profile=medical"
```
Expected: `200`, не 500. Это Review Focus 4.

- [ ] **Step 5: Commit**

```bash
git add HealthTech/Transcription/TranscriptCorrectionService.cs
git commit -m "feat: повторная коррекция видит неуверенные слова из файла"
```

---

### Task 5: Файл известных фонетических ошибок

**Files:**
- Create: `SemanticKernel/Glossary/phonetic_confusions.txt`
- Modify: `HealthTech/Profiles/ProfileCatalog.cs`
- Modify: `HealthTech/appsettings.json`

**Interfaces:**
- Consumes: `Glossary.Load(path, heading)` и `Glossary.Select` — оба переиспользуются без единой правки: формат файла совпадает с глоссарием, а `Select` ищет по всем колонкам кроме последней, то есть и по услышанному варианту, и по правильному.
- Produces: файл подключается через существующий массив `Glossaries` в конфиге профиля.

Отдельный ключ `PhoneticFile` в `ProfileDefinition` спека предполагала, но он не нужен: список подключается как ещё один глоссарий, а отличается только заголовком. Лишнее поле в конфиге, лишнее поле в `RecordProfileContent` и лишняя ветка в `BuildUserMessage` не окупаются.

- [ ] **Step 1: Файл**

Создать `SemanticKernel/Glossary/phonetic_confusions.txt`:

```text
# Известные ошибки распознавания.
# услышано | правильно | комментарий
# Пополняется вручную по отчётам <имя>.medical_corrections.md.
аторва статин | аторвастатин | разрыв слова
mio cardic | miocardic | разрыв слова
эка ге | ЭКГ | аббревиатура по буквам
эхо кардио | эхокардиография | разрыв слова
тромб аспирация | тромбаспирация | разрыв слова
ин фаркт | инфaрct | разрыв слова
```

Последняя строка намеренно показывает, что правая колонка пишется в правильном виде; при пополнении файла руками так и делать.

- [ ] **Step 2: Заголовок для файла**

В `HealthTech/Profiles/ProfileCatalog.cs` добавить константу рядом с двумя существующими:

```csharp
        private const string PhoneticHeading =
            "Known ASR mishearings (left = what the recognizer produces, right = the correct form):";
```

И расширить `HeadingFor`:

```csharp
        // Списки подключаются одинаково, но означают разное, поэтому и заголовки разные:
        // термины - что искать, нормальная речь - что не трогать, фонетика - что чем заменять.
        private static string HeadingFor(string fileName) =>
            fileName.Contains("phonetic", StringComparison.OrdinalIgnoreCase) ? PhoneticHeading
            : fileName.Contains("moldova_speech", StringComparison.OrdinalIgnoreCase) ? SpeechGlossaryHeading
            : MedicalGlossaryHeading;
```

- [ ] **Step 3: Подключить к медицинскому профилю**

В `HealthTech/appsettings.json`, профиль `medical`:

```json
        "Glossaries": [ "medical_glossary.txt", "moldova_speech_glossary.txt", "phonetic_confusions.txt" ]
```

Административный и финансовый профили список не получают: он медицинский по содержанию.

Это третий глоссарий у `medical`, а `MaxGlossaryCharacters` применяется к каждому файлу отдельно — запрос вырастет ещё максимум на 2000 символов. Замерить фактическую длину при проверке ниже.

- [ ] **Step 4: Сборка и проверка длины запроса**

```sh
taskkill //F //IM HealthTech.exe; dotnet build
```
Expected: `Build succeeded`.

Запустить приложение.
Expected: в логе `Загружено профилей записей: 3`; старт не падает (падение означает, что файл не скопировался в вывод — `SemanticKernel.csproj` копирует `Glossary\**\*`).

Прогнать **одну** запись типа `medical` целиком.
Expected: запись доходит до `Completed`. В логе коррекции нет предупреждений `no valid output after ... attempt(s)` — они означали бы, что запрос перестал помещаться в контекст. Если такие строки появились, опустить `MaxGlossaryCharacters` в `SemanticKernel/appsettings.llm.json` до 1200 и прогнать ещё раз.

В отчёте `<имя>.medical_corrections.md` посмотреть, попали ли строки фонетического списка в подсказку и исправился ли хотя бы один разрыв слова.

- [ ] **Step 5: Commit**

```bash
git add SemanticKernel/Glossary/phonetic_confusions.txt HealthTech/Profiles/ProfileCatalog.cs HealthTech/appsettings.json
git commit -m "feat: список известных фонетических ошибок в подсказке модели"
```

---

### Task 6: Справочник врачей и привязка спикеров

**Files:**
- Create: `HealthTech/Speakers/Person.cs`
- Create: `HealthTech/Speakers/PersonRepository.cs`
- Create: `HealthTech/Speakers/SpeakerBindingRepository.cs`
- Create: `HealthTech/Speakers/SpeakerBindingService.cs`
- Create: `HealthTech/Controllers/PersonsController.cs`
- Modify: `HealthTech/Controllers/JobsController.cs`
- Modify: `HealthTech/Program.cs`

**Interfaces:**
- Consumes: `IDbConnectionFactory`; `IJobService.GetResultAsync(Guid)`; таблицы `Persons` и `SpeakerBindings` (уже в `schema.sql`); `AudioProcessingError.InvalidRequest` (400) и `InputNotFound` (404).
- Produces: `record Person(Guid Id, string FullName, string? Specialty)`; `record SpeakerBinding(string Label, Guid PersonId)`; `ISpeakerBindingService.ListPersonsAsync()`, `.BindAsync(Guid jobId, IReadOnlyList<SpeakerBinding>)`, `.GetTranscriptAsync(Guid jobId)`.

- [ ] **Step 1: Модели**

`HealthTech/Speakers/Person.cs`:

```csharp
namespace HealthTech.Speakers
{
    /// <summary>Врач из справочника. Заводится руками правкой schema.sql или самой базы.</summary>
    public record Person(Guid Id, string FullName, string? Specialty);

    /// <summary>Сопоставление метки диаризации ("Speaker 1") с врачом.</summary>
    public record SpeakerBinding(string Label, Guid PersonId);

    /// <summary>Реплика с подставленным именем. Speaker - исходная метка, DisplayName - что показать.</summary>
    public record NamedTurn(string Speaker, string DisplayName, string StartTime, string EndTime, string Text);

    /// <summary>Диалог задания с подставленными именами.</summary>
    public record NamedTranscript(IReadOnlyList<NamedTurn> Turns, string Text);
}
```

- [ ] **Step 2: Репозитории**

`HealthTech/Speakers/PersonRepository.cs`:

```csharp
using Dapper;
using HealthTech.Data;

namespace HealthTech.Speakers
{
    public interface IPersonRepository
    {
        Task<IReadOnlyList<Person>> ListAsync(CancellationToken cancellationToken = default);
        Task<IReadOnlyList<Guid>> ExistingIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default);
    }

    public class PersonRepository : IPersonRepository
    {
        private readonly IDbConnectionFactory _connections;

        public PersonRepository(IDbConnectionFactory connections)
        {
            _connections = connections;
        }

        public async Task<IReadOnlyList<Person>> ListAsync(CancellationToken cancellationToken = default)
        {
            using var connection = _connections.Create();
            var rows = await connection.QueryAsync<(string Id, string FullName, string? Specialty)>(
                "SELECT Id, FullName, Specialty FROM Persons ORDER BY FullName");
            return rows.Select(r => new Person(Guid.Parse(r.Id), r.FullName, r.Specialty)).ToList();
        }

        public async Task<IReadOnlyList<Guid>> ExistingIdsAsync(
            IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default)
        {
            if (ids.Count == 0)
            {
                return [];
            }

            using var connection = _connections.Create();
            var found = await connection.QueryAsync<string>(
                "SELECT Id FROM Persons WHERE Id IN @Ids",
                new { Ids = ids.Select(i => i.ToString()).ToArray() });
            return found.Select(Guid.Parse).ToList();
        }
    }
}
```

`HealthTech/Speakers/SpeakerBindingRepository.cs`:

```csharp
using Dapper;
using HealthTech.Data;

namespace HealthTech.Speakers
{
    public interface ISpeakerBindingRepository
    {
        Task<IReadOnlyList<SpeakerBinding>> GetAsync(Guid jobId, CancellationToken cancellationToken = default);

        /// <summary>Заменяет весь набор привязок задания: PUT - это целиком новый список, а не добавление.</summary>
        Task ReplaceAsync(Guid jobId, IReadOnlyList<SpeakerBinding> bindings, CancellationToken cancellationToken = default);
    }

    public class SpeakerBindingRepository : ISpeakerBindingRepository
    {
        private readonly IDbConnectionFactory _connections;

        public SpeakerBindingRepository(IDbConnectionFactory connections)
        {
            _connections = connections;
        }

        public async Task<IReadOnlyList<SpeakerBinding>> GetAsync(
            Guid jobId, CancellationToken cancellationToken = default)
        {
            using var connection = _connections.Create();
            var rows = await connection.QueryAsync<(string SpeakerLabel, string PersonId)>(
                "SELECT SpeakerLabel, PersonId FROM SpeakerBindings WHERE JobId = @JobId",
                new { JobId = jobId.ToString() });
            return rows.Select(r => new SpeakerBinding(r.SpeakerLabel, Guid.Parse(r.PersonId))).ToList();
        }

        public async Task ReplaceAsync(
            Guid jobId, IReadOnlyList<SpeakerBinding> bindings, CancellationToken cancellationToken = default)
        {
            using var connection = _connections.Create();
            connection.Open();
            // Всё или ничего: половина сохранённых привязок хуже, чем ни одной.
            using var transaction = connection.BeginTransaction();
            await connection.ExecuteAsync(
                "DELETE FROM SpeakerBindings WHERE JobId = @JobId",
                new { JobId = jobId.ToString() }, transaction);
            if (bindings.Count > 0)
            {
                await connection.ExecuteAsync(
                    "INSERT INTO SpeakerBindings (JobId, SpeakerLabel, PersonId) VALUES (@JobId, @SpeakerLabel, @PersonId)",
                    bindings.Select(b => new
                    {
                        JobId = jobId.ToString(),
                        SpeakerLabel = b.Label,
                        PersonId = b.PersonId.ToString()
                    }), transaction);
            }
            transaction.Commit();
        }
    }
}
```

- [ ] **Step 3: Сервис**

`HealthTech/Speakers/SpeakerBindingService.cs` — вся проверка живёт здесь, контроллеры остаются тонкими:

```csharp
using System.Text.Json;
using HealthTech.Audio;
using HealthTech.Jobs;
using HealthTech.Transcription;

namespace HealthTech.Speakers
{
    public interface ISpeakerBindingService
    {
        Task<IReadOnlyList<Person>> ListPersonsAsync(CancellationToken cancellationToken = default);
        Task<NamedTranscript> BindAsync(Guid jobId, IReadOnlyList<SpeakerBinding> bindings, CancellationToken cancellationToken = default);
        Task<NamedTranscript> GetTranscriptAsync(Guid jobId, CancellationToken cancellationToken = default);
    }

    public class SpeakerBindingService : ISpeakerBindingService
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        private readonly IPersonRepository _persons;
        private readonly ISpeakerBindingRepository _bindings;
        private readonly IJobService _jobs;

        public SpeakerBindingService(
            IPersonRepository persons, ISpeakerBindingRepository bindings, IJobService jobs)
        {
            _persons = persons;
            _bindings = bindings;
            _jobs = jobs;
        }

        public Task<IReadOnlyList<Person>> ListPersonsAsync(CancellationToken cancellationToken = default) =>
            _persons.ListAsync(cancellationToken);

        public async Task<NamedTranscript> BindAsync(
            Guid jobId, IReadOnlyList<SpeakerBinding> bindings, CancellationToken cancellationToken = default)
        {
            var result = await ReadResultAsync(jobId, cancellationToken);

            // Метка не из этой записи - почти всегда опечатка в UI, и молча принять её значит
            // показать человеку, что привязка сохранена, а в диалоге ничего не изменится.
            var known = result.Speakers.ToHashSet(StringComparer.Ordinal);
            var unknownLabels = bindings.Select(b => b.Label).Where(l => !known.Contains(l)).Distinct().ToList();
            if (unknownLabels.Count > 0)
            {
                throw new AudioProcessingException(AudioProcessingError.InvalidRequest,
                    $"В записи нет говорящих: {string.Join(", ", unknownLabels)}. Доступные: {string.Join(", ", result.Speakers)}.");
            }

            var duplicates = bindings.GroupBy(b => b.Label, StringComparer.Ordinal)
                .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            if (duplicates.Count > 0)
            {
                throw new AudioProcessingException(AudioProcessingError.InvalidRequest,
                    $"Метка указана дважды: {string.Join(", ", duplicates)}.");
            }

            var requested = bindings.Select(b => b.PersonId).Distinct().ToList();
            var existing = (await _persons.ExistingIdsAsync(requested, cancellationToken)).ToHashSet();
            var missing = requested.Where(id => !existing.Contains(id)).ToList();
            if (missing.Count > 0)
            {
                throw new AudioProcessingException(AudioProcessingError.InvalidRequest,
                    $"В справочнике нет врачей: {string.Join(", ", missing)}.");
            }

            await _bindings.ReplaceAsync(jobId, bindings, cancellationToken);
            return await BuildAsync(jobId, result, cancellationToken);
        }

        public async Task<NamedTranscript> GetTranscriptAsync(
            Guid jobId, CancellationToken cancellationToken = default)
        {
            var result = await ReadResultAsync(jobId, cancellationToken);
            return await BuildAsync(jobId, result, cancellationToken);
        }

        private async Task<SpeakerTranscriptResult> ReadResultAsync(Guid jobId, CancellationToken cancellationToken)
        {
            var json = await _jobs.GetResultAsync(jobId, cancellationToken)
                ?? throw new AudioProcessingException(AudioProcessingError.InputNotFound,
                    $"У записи {jobId} нет готового результата: она ещё обрабатывается, упала или не существует.");
            return JsonSerializer.Deserialize<SpeakerTranscriptResult>(json, Json)
                ?? throw new AudioProcessingException(AudioProcessingError.InvalidTranscript,
                    $"Результат записи {jobId} не читается.");
        }

        // Имена подставляются на лету: файлы транскриптов не переписываются, поэтому привязку
        // можно менять сколько угодно раз, не перезапуская обработку.
        private async Task<NamedTranscript> BuildAsync(
            Guid jobId, SpeakerTranscriptResult result, CancellationToken cancellationToken)
        {
            var saved = await _bindings.GetAsync(jobId, cancellationToken);
            var people = (await _persons.ListAsync(cancellationToken)).ToDictionary(p => p.Id);
            var names = saved
                .Where(b => people.ContainsKey(b.PersonId))
                .ToDictionary(b => b.Label, b => people[b.PersonId].FullName, StringComparer.Ordinal);

            var turns = result.Turns
                .Select(t => new NamedTurn(t.Speaker,
                    names.TryGetValue(t.Speaker, out var name) ? name : t.Speaker,
                    t.StartTime, t.EndTime, t.Text))
                .ToList();

            return new NamedTranscript(turns, TranscriptDialogue.Format(turns.Select(t => (t.DisplayName, t.Text))));
        }
    }
}
```

- [ ] **Step 4: Контроллеры и регистрация**

`HealthTech/Controllers/PersonsController.cs`:

```csharp
using HealthTech.Speakers;
using Microsoft.AspNetCore.Mvc;

namespace HealthTech.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class PersonsController : ControllerBase
    {
        [HttpGet]
        public Task<IReadOnlyList<Person>> List(
            [FromServices] ISpeakerBindingService speakers, CancellationToken cancellationToken) =>
            speakers.ListPersonsAsync(cancellationToken);
    }
}
```

В `JobsController` добавить два метода и `using HealthTech.Speakers;`:

```csharp
        [HttpPut("{id:guid}/speakers")]
        public Task<NamedTranscript> Bind(
            Guid id,
            [FromBody] IReadOnlyList<SpeakerBinding> bindings,
            [FromServices] ISpeakerBindingService speakers,
            CancellationToken cancellationToken) =>
            speakers.BindAsync(id, bindings, cancellationToken);

        [HttpGet("{id:guid}/transcript")]
        public Task<NamedTranscript> Transcript(
            Guid id,
            [FromServices] ISpeakerBindingService speakers,
            CancellationToken cancellationToken) =>
            speakers.GetTranscriptAsync(id, cancellationToken);
```

В `Program.cs`, рядом с регистрацией `IJobService`:

```csharp
builder.Services.AddSingleton<IPersonRepository, PersonRepository>();
builder.Services.AddSingleton<ISpeakerBindingRepository, SpeakerBindingRepository>();
builder.Services.AddSingleton<ISpeakerBindingService, SpeakerBindingService>();
```

И `using HealthTech.Speakers;` наверх.

- [ ] **Step 5: Сборка и проверка**

```sh
taskkill //F //IM HealthTech.exe; dotnet build
```
Expected: `Build succeeded`.

Запустить приложение. Взять запись, доведённую до `Completed` в задаче 5.

```sh
curl.exe -s http://localhost:5089/persons
```
Expected: три врача из `schema.sql` с именами и специальностями.

Диалог без привязок:

```sh
curl.exe -s http://localhost:5089/jobs/<jobId>/transcript
```
Expected: `displayName` равен `speaker` у всех реплик, `text` — обычный диалог со `Speaker 1:` / `Speaker 2:`.

Привязка:

```sh
curl.exe -s -X PUT http://localhost:5089/jobs/<jobId>/speakers -H "Content-Type: application/json" -d "[{\"label\":\"Speaker 1\",\"personId\":\"11111111-1111-1111-1111-111111111111\"}]"
```
Expected: 200, в ответе у реплик `Speaker 1` появилось `"displayName":"Ion Popescu"`, в `text` заголовок стал `Ion Popescu:`, у `Speaker 2` ничего не изменилось.

`GET /jobs/<jobId>/transcript` ещё раз.
Expected: имена сохранились — привязка лежит в базе, а не в ответе.

Review Focus 5 — обе ошибки:

```sh
curl.exe -s -o /dev/null -w "%{http_code}\n" -X PUT http://localhost:5089/jobs/<jobId>/speakers -H "Content-Type: application/json" -d "[{\"label\":\"Speaker 9\",\"personId\":\"11111111-1111-1111-1111-111111111111\"}]"
curl.exe -s -o /dev/null -w "%{http_code}\n" -X PUT http://localhost:5089/jobs/<jobId>/speakers -H "Content-Type: application/json" -d "[{\"label\":\"Speaker 1\",\"personId\":\"99999999-9999-9999-9999-999999999999\"}]"
```
Expected: оба `400`. После них `GET /jobs/<jobId>/transcript` показывает **прежнюю** привязку `Ion Popescu` — отказ не стёр сохранённое.

Запись без результата:

```sh
curl.exe -s -o /dev/null -w "%{http_code}\n" http://localhost:5089/jobs/00000000-0000-0000-0000-000000000000/transcript
```
Expected: `404`.

- [ ] **Step 6: Commit**

```bash
git add HealthTech/Speakers HealthTech/Controllers/PersonsController.cs HealthTech/Controllers/JobsController.cs HealthTech/Program.cs
git commit -m "feat: справочник врачей и ручная привязка спикеров"
```

---

### Task 7: Документация и сквозная проверка

**Files:**
- Modify: `HealthTech/HealthTech.http`
- Modify: `CLAUDE.md`
- Modify: `docs/superpowers/specs/2026-09-26-transcription-workflow-design.md`

**Interfaces:**
- Consumes: всё из задач 1–6.
- Produces: ничего для кода.

- [ ] **Step 1: Примеры запросов**

В `HealthTech/HealthTech.http` дописать в конец:

```http
### Справочник врачей для выпадашки привязки
GET {{HealthTech_HostAddress}}/persons
Accept: application/json

### Привязать говорящих к врачам. Список заменяет прежний целиком.
PUT {{HealthTech_HostAddress}}/jobs/{{FileId}}/speakers
Content-Type: application/json

[
  { "label": "Speaker 1", "personId": "11111111-1111-1111-1111-111111111111" },
  { "label": "Speaker 2", "personId": "22222222-2222-2222-2222-222222222222" }
]

### Диалог с подставленными именами. Файлы транскриптов при этом не меняются.
GET {{HealthTech_HostAddress}}/jobs/{{FileId}}/transcript
Accept: application/json
```

- [ ] **Step 2: `CLAUDE.md`**

Четыре правки:

1. В «Transcription pipeline» — про `WithProbabilities()`, сборку слов из токенов (вероятность слова = минимум по токенам), `Whisper:LowConfidenceThreshold` и про то, что смещение пересчитывается на двух склейках, а у изменённой коррекцией реплики список сбрасывается.
2. В «Medical term correction» — про поле `lowConfidence` в запросе и про фонетический список как третий глоссарий `medical` с собственным заголовком.
3. Новый абзац «Speakers and persons»: `Persons` заполняется руками, `PUT /jobs/{id}/speakers` заменяет набор целиком и проверяет метки по результату записи, имена подставляются на лету и файлы не переписываются.
4. В «Controllers» — `PersonsController` и два новых метода `JobsController`; из «Not built yet» убрать всё, что теперь сделано, оставив только действительно отложенное.

- [ ] **Step 3: Спека**

В разделе 12 спеки отметить этапы 5–7 сделанными, как отмечены 1–4. В разделе 15 снять пункты, которые перестали быть отложенными, и дописать новые, если они появились по ходу работы.

- [ ] **Step 4: Сквозная проверка**

```sh
taskkill //F //IM HealthTech.exe; dotnet build
```
Expected: `Build succeeded`.

Полный цикл на одной записи `medical`: `POST /files` → `POST /jobs` → опрос до `Completed` → `GET /jobs/{id}/result` → `PUT /jobs/{id}/speakers` → `GET /jobs/{id}/transcript`.

Expected: запись доходит до 100%; в `.speakers.json` есть `lowConfidence` с верными смещениями; отчёт коррекции показывает, что подсказки применялись; диалог отдаётся с именами врачей.

- [ ] **Step 5: Commit**

```bash
git add HealthTech/HealthTech.http CLAUDE.md docs/superpowers/specs/2026-09-26-transcription-workflow-design.md
git commit -m "docs: уверенность ASR, фонетический список и привязка спикеров"
```
