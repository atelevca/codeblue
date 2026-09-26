using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using LLamaSharp.SemanticKernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel.ChatCompletion;

namespace SemanticKernel.Minutes;

public sealed class MeetingMinutesGenerator : IMeetingMinutesGenerator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    // The extraction and generation prompts name the fields in snake_case (agenda_id, open_issues,
    // next_meeting); the verification prompt keeps camelCase (transcriptQuote).
    private static readonly JsonSerializerOptions FactsJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private const int FragmentNoteReserve = 300;
    private const int ConsolidationMaxTokens = 1024;

    private readonly IChatCompletionProvider _chatProvider;
    private readonly ITokenCounter _tokens;
    private readonly MinutesOptions _options;
    private readonly ILogger<MeetingMinutesGenerator> _logger;

    public MeetingMinutesGenerator(
        [FromKeyedServices(LlmModelRole.Minutes)] IChatCompletionProvider chatProvider,
        [FromKeyedServices(LlmModelRole.Minutes)] ITokenCounter tokens,
        IOptions<MinutesOptions> options, ILogger<MeetingMinutesGenerator> logger)
    {
        _chatProvider = chatProvider;
        _tokens = tokens;
        _options = options.Value;
        _logger = logger;
        if (_options.MaxWindowTokens < TokenBudget.MinimalWindowTokens || _options.ExtractionMaxTokens <= 0 ||
            _options.VerificationMaxTokens <= 0 || _options.MaxRetries is < 0 or > 10 ||
            _options.VerificationMaxRetries is < 0 or > 10)
        {
            throw new LlmConfigurationException($"Minutes:MaxWindowTokens must be at least {TokenBudget.MinimalWindowTokens}, " +
                "Minutes:ExtractionMaxTokens and Minutes:VerificationMaxTokens positive and Minutes:MaxRetries / " +
                "Minutes:VerificationMaxRetries between 0 and 10.");
        }
    }

    public async Task<MeetingMinutesResult> GenerateAsync(string transcript, MeetingMetadata? metadata = null,
        CancellationToken ct = default)
    {
        var facts = await ExtractFactsAsync(transcript, metadata, ct: ct);
        var minutes = RenderMinutes(facts);
        var verification = await VerifyMinutesAsync(transcript, minutes, metadata, ct: ct);
        return new MeetingMinutesResult(facts, minutes, verification);
    }

    private const int VerificationNoteReserve = 100;

    public async Task<MinutesVerification> VerifyMinutesAsync(string transcript, string minutesMarkdown,
        MeetingMetadata? metadata = null, IProgress<MinutesProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transcript);
        ArgumentException.ThrowIfNullOrWhiteSpace(minutesMarkdown);
        var prompt = await ReadPromptAsync("minutes_verification.system.txt", ct);
        // A source quotation may come from the transcript or from a metadata value (a bound
        // participant's name, the record title): both are sources of truth for the model.
        var sources = Sources(transcript, metadata);
        var budget = TokenBudget.ForInput(_tokens.ContextSize, _options.VerificationMaxTokens);

        // Verify the final rendered document against the source, not against the extracted facts.
        var whole = VerificationInput(transcript, metadata, minutesMarkdown);
        if (await _tokens.CountPromptAsync(prompt, whole, ct) <= budget)
        {
            progress?.Report(new MinutesProgress(MinutesStage.Verifying, 1, 1));
            return await RunStageAsync("Minutes Verification", prompt, whole, _options.VerificationMaxTokens,
                _options.VerificationMaxRetries, response => ParseVerification(response, sources, minutesMarkdown), ct);
        }

        var windowBudget = budget - await _tokens.CountPromptAsync(prompt, VerificationInput("", metadata, minutesMarkdown), ct)
                           - VerificationNoteReserve;
        if (windowBudget < TokenBudget.MinimalWindowTokens)
        {
            _logger.LogWarning("MOM verification skipped: the document leaves {Window} tokens for the transcript (context {Context})",
                windowBudget, _tokens.ContextSize);
            return new MinutesVerification
            {
                Completed = false,
                Summary = "Verificarea automată nu a fost efectuată: documentul este prea mare pentru contextul modelului " +
                          $"(Llm:ContextSize = {_tokens.ContextSize}). Documentul necesită revizuire manuală.",
                Findings = []
            };
        }

        // The whole document against one fragment at a time. "Unsupported" (nowhere in the transcript)
        // cannot be judged from a fragment, so it is dropped; the other three kinds are local.
        var windows = await TranscriptWindows.SplitAsync(transcript, windowBudget, _tokens, ct);
        if (windows.Count == 1)
        {
            // Measured together with the document it did not fit, measured alone it does: the difference
            // is inside the margin. A one-fragment check would only lose the Unsupported findings.
            progress?.Report(new MinutesProgress(MinutesStage.Verifying, 1, 1));
            return await RunStageAsync("Minutes Verification", prompt, whole, _options.VerificationMaxTokens,
                _options.VerificationMaxRetries, response => ParseVerification(response, sources, minutesMarkdown), ct);
        }
        _logger.LogInformation("MOM verification: transcript split into {Count} fragments of at most {Budget} tokens", windows.Count, windowBudget);
        var findings = new List<MinutesDiscrepancy>();
        var discarded = 0;
        var failed = new List<int>();
        for (var k = 0; k < windows.Count; k++)
        {
            progress?.Report(new MinutesProgress(MinutesStage.Verifying, k + 1, windows.Count));
            var input = VerificationInput(windows[k], metadata, minutesMarkdown) +
                        $"\n\nAcesta este fragmentul {k + 1} din {windows.Count} al transcriptului. " +
                        "Raportează doar discrepanțele pe care le arată acest fragment.";
            try
            {
                var result = await RunStageAsync($"Minutes Verification {k + 1}/{windows.Count}", prompt, input,
                    _options.VerificationMaxTokens, _options.VerificationMaxRetries,
                    response => ParseVerification(response, sources, minutesMarkdown), ct);
                findings.AddRange(result.Findings.Where(f => f.Kind != "Unsupported"));
                discarded += result.DiscardedFindings;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "MOM verification fragment {Fragment} failed", k + 1);
                failed.Add(k + 1);
            }
        }

        var unique = findings.DistinctBy(f => (f.Kind, f.TranscriptQuote, f.DocumentQuote)).ToList();
        var summary = $"Verificat pe {windows.Count} fragmente: {unique.Count} discrepanțe. " +
                      "Afirmațiile fără suport în transcript nu pot fi verificate pe fragmente.";
        if (discarded > 0)
        {
            summary += $" {discarded} constatări au fost eliminate: citatele nu au putut fi regăsite în text.";
        }
        if (failed.Count > 0)
        {
            summary += $" Fragmentele {string.Join(", ", failed)} nu au putut fi verificate.";
        }
        return new MinutesVerification
        {
            Summary = summary,
            Findings = unique,
            DiscardedFindings = discarded,
            Completed = failed.Count == 0,
            Partial = true
        };
    }

    private static string VerificationInput(string transcript, MeetingMetadata? metadata, string document) =>
        JsonSerializer.Serialize(new { transcript, metadata, document }, FactsJsonOptions);

    private static List<string> Sources(string transcript, MeetingMetadata? metadata)
    {
        var sources = new List<string> { transcript };
        if (metadata != null)
        {
            sources.AddRange(MetadataValues(metadata));
        }
        return sources;
    }

    private static IEnumerable<string> MetadataValues(MeetingMetadata metadata)
    {
        if (!string.IsNullOrWhiteSpace(metadata.Title))
        {
            yield return metadata.Title;
        }
        foreach (var participant in metadata.Participants)
        {
            yield return participant.Name;
            if (!string.IsNullOrWhiteSpace(participant.Role))
            {
                yield return participant.Role;
            }
        }
    }

    // Ответ в целом (summary + массив findings) обязан быть корректным, иначе повтор. Отдельная
    // находка с неточной цитатой или без обязательного поля отбрасывается сама: раньше одна такая
    // находка роняла всю сверку, и на длинных записях она не проходила ни разу.
    private MinutesVerification ParseVerification(string response, IReadOnlyList<string> sources, string minutesMarkdown)
    {
        var root = JsonNode.Parse(UnwrapFence(response)) as JsonObject
            ?? throw new JsonException("Expected a verification object.");
        var summary = ReadString(root, "summary");
        if (string.IsNullOrWhiteSpace(summary) || root["findings"] is not JsonArray items)
        {
            throw new FormatException("Verification must contain summary and findings.");
        }

        var findings = new List<MinutesDiscrepancy>();
        var discarded = 0;
        foreach (var item in items)
        {
            var finding = item is JsonObject obj ? ReadFinding(obj, sources, minutesMarkdown) : null;
            if (finding == null)
            {
                discarded++;
                continue;
            }
            findings.Add(finding);
        }

        if (discarded > 0)
        {
            // Число, а не содержимое: в находках цитаты из транскрипта.
            _logger.LogWarning("MOM stage Minutes Verification: {Discarded} of {Total} finding(s) discarded, evidence not found in the source",
                discarded, items.Count);
            summary += $" {discarded} constatări au fost eliminate: citatele nu au putut fi regăsite în text.";
        }
        return new MinutesVerification { Summary = summary.Trim(), Findings = findings, DiscardedFindings = discarded };
    }

    private static MinutesDiscrepancy? ReadFinding(JsonObject item, IReadOnlyList<string> sources, string minutesMarkdown)
    {
        var kind = ReadString(item, "kind");
        var description = ReadString(item, "description");
        var suggestedCorrection = ReadString(item, "suggestedCorrection");
        if (kind == null || !MinutesDiscrepancy.Kinds.Contains(kind) ||
            string.IsNullOrWhiteSpace(description) || string.IsNullOrWhiteSpace(suggestedCorrection))
        {
            return null;
        }

        // Unsupported needs no source quote, Omission no document quote; Contradiction and
        // Misattribution need both. The source quote may sit in any of the sources.
        if (!TryResolveQuote(ReadString(item, "transcriptQuote"), sources, kind != "Unsupported", out var transcriptQuote) ||
            !TryResolveQuote(ReadString(item, "documentQuote"), [minutesMarkdown], kind != "Omission", out var documentQuote))
        {
            return null;
        }

        var section = ReadString(item, "section");
        return new MinutesDiscrepancy
        {
            Section = string.IsNullOrWhiteSpace(section) ? null : section.Trim(),
            Kind = kind,
            Description = description.Trim(),
            TranscriptQuote = transcriptQuote,
            DocumentQuote = documentQuote,
            SuggestedCorrection = suggestedCorrection.Trim()
        };
    }

    private static int ReadInt(JsonObject item, string name) =>
        item[name] is JsonValue value && value.TryGetValue<int>(out var number) ? number : 0;

    private static string? ReadString(JsonObject item, string name) =>
        item[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary>
    /// Находит цитату в одном из источников и возвращает точный фрагмент источника. Сравнение
    /// прощает то, что модель меняет при переписывании: пробелы, регистр, ş/ș и ţ/ț, типографские
    /// кавычки, тире и многоточия по краям. Возвращается фрагмент источника, а не текст модели,
    /// так что цитата в ответе остаётся точной и её можно подсветить.
    /// </summary>
    private static bool TryResolveQuote(string? quote, IReadOnlyList<string> sources, bool required, out string? resolved)
    {
        resolved = null;
        if (string.IsNullOrWhiteSpace(quote))
        {
            return !required;
        }

        var trimmed = quote.Trim().Trim('.', '…', '"', '\'', '„', '“', '”', '«', '»', ' ');
        if (trimmed.Length == 0)
        {
            return !required;
        }

        var (normalizedQuote, _) = NormalizeForMatch(trimmed);
        if (normalizedQuote.Length == 0)
        {
            return false;
        }

        foreach (var source in sources)
        {
            var (normalizedSource, map) = NormalizeForMatch(source);
            var at = normalizedSource.IndexOf(normalizedQuote, StringComparison.Ordinal);
            if (at < 0)
            {
                continue;
            }

            var start = map[at];
            var end = map[at + normalizedQuote.Length - 1] + 1;
            resolved = source[start..end];
            return true;
        }
        return false;
    }

    // Нормализованный текст и для каждого его символа - индекс в исходной строке.
    private static (string Text, List<int> Map) NormalizeForMatch(string text)
    {
        var builder = new StringBuilder(text.Length);
        var map = new List<int>(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                if (builder.Length > 0 && builder[^1] != ' ')
                {
                    builder.Append(' ');
                    map.Add(i);
                }
                continue;
            }

            builder.Append(char.ToLowerInvariant(c) switch
            {
                'ş' => 'ș',
                'ţ' => 'ț',
                '„' or '“' or '”' or '«' or '»' => '"',
                '‘' or '’' => '\'',
                '–' or '—' => '-',
                var other => other
            });
            map.Add(i);
        }

        if (builder.Length > 0 && builder[^1] == ' ')
        {
            builder.Length--;
            map.RemoveAt(map.Count - 1);
        }
        return (builder.ToString(), map);
    }

    public async Task<MeetingFacts> ExtractFactsAsync(string transcript, MeetingMetadata? metadata = null,
        IProgress<MinutesProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transcript);
        var prompt = await ReadPromptAsync("minutes_extraction.system.txt", ct);
        var inputBudget = TokenBudget.ForInput(_tokens.ContextSize, _options.ExtractionMaxTokens);
        var frame = await _tokens.CountPromptAsync(prompt, ExtractionInput("", metadata), ct);
        var windowBudget = Math.Min(_options.MaxWindowTokens, inputBudget - frame - FragmentNoteReserve);
        if (windowBudget < TokenBudget.MinimalWindowTokens)
        {
            throw new LlmConfigurationException($"Llm:ContextSize = {_tokens.ContextSize} leaves {windowBudget} tokens for a transcript " +
                $"window next to the extraction prompt; at least {TokenBudget.MinimalWindowTokens} are needed.");
        }

        var windows = await TranscriptWindows.SplitAsync(transcript, windowBudget, _tokens, ct);
        if (windows.Count == 1)
        {
            // Short transcript: exactly the request it has always been.
            progress?.Report(new MinutesProgress(MinutesStage.Extracting, 1, 1));
            return await ExtractAsync("Fact Extraction and Summary", prompt, ExtractionInput(transcript, metadata), ct);
        }

        _logger.LogInformation("MOM extraction: transcript split into {Count} fragments of at most {Budget} tokens", windows.Count, windowBudget);
        var parts = new List<MeetingFacts>();
        for (var k = 0; k < windows.Count; k++)
        {
            progress?.Report(new MinutesProgress(MinutesStage.Extracting, k + 1, windows.Count));
            // Earlier topics, oldest dropped first while the hint does not fit: a discussion that
            // crosses into this fragment most likely continues one of the latest topics.
            var topics = parts.SelectMany(p => p.Agenda!).Select(a => a.Topic).Distinct().ToList();
            var all = topics.Count;
            var input = ExtractionInput(windows[k], metadata) + FragmentNote(k + 1, windows.Count, topics);
            while (topics.Count > 0 && await _tokens.CountPromptAsync(prompt, input, ct) > inputBudget)
            {
                topics.RemoveAt(0);
                input = ExtractionInput(windows[k], metadata) + FragmentNote(k + 1, windows.Count, topics);
            }
            if (topics.Count < all)
            {
                _logger.LogWarning("MOM extraction fragment {Fragment}: topics hint kept {Kept} of {All} topics to fit the context",
                    k + 1, topics.Count, all);
            }
            parts.Add(await ExtractAsync($"Fact Extraction {k + 1}/{windows.Count}", prompt, input, ct));
        }

        progress?.Report(new MinutesProgress(MinutesStage.Consolidating, 1, 1));
        var merged = MeetingFactsMerger.Merge(parts);
        return NormalizeFacts(await ConsolidateAsync(merged, parts, ct));
    }

    private Task<MeetingFacts> ExtractAsync(string stage, string prompt, string input, CancellationToken ct) =>
        RunStageAsync(stage, prompt, input, _options.ExtractionMaxTokens, _options.MaxRetries,
            response => NormalizeFacts(JsonSerializer.Deserialize<MeetingFacts>(UnwrapFence(response), FactsJsonOptions)
                ?? throw new JsonException("Expected a facts object.")), ct);

    private static string ExtractionInput(string transcript, MeetingMetadata? metadata) =>
        JsonSerializer.Serialize(new { transcript, metadata }, FactsJsonOptions);

    private static string FragmentNote(int fragment, int total, IReadOnlyList<string> topics)
    {
        var note = $"\n\nAcesta este fragmentul {fragment} din {total} al transcriptului. " +
                   "Extrage doar faptele din acest fragment; nu presupune că ședința începe sau se termină aici.";
        if (topics.Count > 0)
        {
            note += "\nTeme identificate deja în fragmentele anterioare:\n" +
                    string.Join('\n', topics.Select((topic, i) => $"{i + 1}. {topic}")) +
                    "\nDacă fragmentul continuă una dintre aceste teme, folosește exact același titlu (topic).";
        }
        return note;
    }

    // Groups topics that are the same under different titles and writes one summary. The model only sees
    // titles and fragment summaries; the facts stay in code. On failure the merge stands as it is.
    private async Task<MeetingFacts> ConsolidateAsync(MeetingFacts merged, IReadOnlyList<MeetingFacts> parts, CancellationToken ct)
    {
        var input = JsonSerializer.Serialize(new
        {
            topics = merged.Agenda!.Select(a => new { id = a.Id, topic = a.Topic }),
            summaries = parts.Select(p => p.Summary)
        }, FactsJsonOptions);
        try
        {
            var prompt = await ReadPromptAsync("minutes_consolidation.system.txt", ct);
            var result = await RunStageAsync("Consolidation", prompt, input, ConsolidationMaxTokens, _options.MaxRetries, ParseConsolidation, ct);
            return MeetingFactsMerger.ApplyTopicMerges(merged, result.Merges) with { Summary = result.Summary };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "MOM consolidation failed; topics are not merged and the fragment summaries are joined");
            return merged;
        }
    }

    private sealed record Consolidation(IReadOnlyList<(int Id, int Into)> Merges, string Summary);

    private static Consolidation ParseConsolidation(string response)
    {
        var root = JsonNode.Parse(UnwrapFence(response)) as JsonObject
            ?? throw new JsonException("Expected a consolidation object.");
        var summary = ReadString(root, "summary");
        if (string.IsNullOrWhiteSpace(summary) || root["merge"] is not JsonArray merges)
        {
            throw new FormatException("Consolidation must contain merge and summary.");
        }
        // Flat {"id","into"} objects: with nested id arrays ([[1,2,5]]) the 7B model repeats the group
        // outside the array and the JSON breaks on every attempt.
        var parsed = merges.OfType<JsonObject>()
            .Select(item => (Id: ReadInt(item, "id"), Into: ReadInt(item, "into")))
            .Where(merge => merge.Id > 0 && merge.Into > 0)
            .ToList();
        return new Consolidation(parsed, summary.Trim());
    }

    /// <summary>
    /// The whole document comes from the facts, in code. The former second model call ("write the minutes
    /// from these facts") added nothing the facts did not already hold - the agenda list and the tables were
    /// rendered here anyway, and the header, participants, course, next meeting and summary are copied
    /// fields - while costing about a minute and a half of 7B on the CPU per record.
    /// </summary>
    public string RenderMinutes(MeetingFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        return MinutesRenderer.Render(NormalizeFacts(facts));
    }

    private static async Task<string> ReadPromptAsync(string file, CancellationToken ct) =>
        await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Prompts", file), ct);

    private async Task<T> RunStageAsync<T>(string stage, string prompt, string input, int maxTokens,
        int maxRetries, Func<string, T> parse, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var budget = TokenBudget.ForInput(_tokens.ContextSize, maxTokens);
        var inputTokens = await _tokens.CountPromptAsync(prompt, input, ct);
        _logger.Log(inputTokens > budget ? LogLevel.Warning : LogLevel.Information,
            "MOM stage {Stage}: {Tokens}/{Budget} input tokens", stage, inputTokens, budget);
        var chat = await _chatProvider.GetChatCompletionAsync(ct);
        var settings = new LLamaSharpPromptExecutionSettings { Temperature = 0, MaxTokens = maxTokens };
        Exception? lastError = null;
        for (var attempt = 0; attempt <= maxRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var history = new ChatHistory(prompt);
            history.AddUserMessage(input);
            if (attempt > 0)
            {
                history.AddUserMessage("Răspunsul anterior nu a respectat formatul. Respectă exact schema și toate secțiunile cerute; returnează un răspuns complet.");
            }
            _logger.LogInformation("MOM stage {Stage}, attempt {Attempt}", stage, attempt + 1);
            var reply = await ChatCompletionRunner.CompleteAsync(chat, history, settings, ct);
            ct.ThrowIfCancellationRequested();
            try
            {
                if (string.IsNullOrWhiteSpace(reply.Content))
                {
                    throw new FormatException("The model returned an empty response.");
                }
                return parse(reply.Content);
            }
            catch (Exception ex) when (ex is JsonException or FormatException)
            {
                lastError = ex;
                // Do not log transcript, facts or model output: these can contain patient data.
                _logger.LogWarning("MOM stage {Stage} returned invalid output on attempt {Attempt}: {Reason}", stage, attempt + 1, ex.Message);
            }
        }
        throw new InvalidOperationException($"MOM stage '{stage}' failed after {maxRetries + 1} attempt(s).", lastError);
    }

    /// <summary>
    /// Rejects a reply that lacks the lists or has empty entries, fills every missing text
    /// field with "Nespecificat", renumbers the agenda 1..n and drops agenda references that
    /// point nowhere. <see cref="RenderMinutes"/> reads only a normalized object, so it never
    /// has to deal with a null field.
    /// </summary>
    private static MeetingFacts NormalizeFacts(MeetingFacts facts)
    {
        if (string.IsNullOrWhiteSpace(facts.Summary) || facts.Agenda == null || facts.Decisions == null ||
            facts.Actions == null || facts.OpenIssues == null ||
            facts.Agenda.Any(item => item == null || string.IsNullOrWhiteSpace(item.Topic) || string.IsNullOrWhiteSpace(item.Discussion)) ||
            facts.Decisions.Any(decision => decision == null || string.IsNullOrWhiteSpace(decision.Description)) ||
            facts.Actions.Any(action => action == null || string.IsNullOrWhiteSpace(action.Description)) ||
            facts.OpenIssues.Any(issue => issue == null || string.IsNullOrWhiteSpace(issue.Description)) ||
            (facts.Participants?.Present ?? []).Any(person => person == null || string.IsNullOrWhiteSpace(person.Name)) ||
            (facts.Participants?.Absent ?? []).Any(string.IsNullOrWhiteSpace))
        {
            throw new FormatException("Facts must include summary, agenda, decisions, actions and open_issues; entries cannot be empty.");
        }

        // The prompt asks for ids starting at 1, but the model is not trusted with numbering:
        // references are remapped through the ids it actually used, then the agenda is renumbered.
        var idMap = new Dictionary<int, int>();
        var agenda = facts.Agenda.Select((item, index) =>
        {
            idMap.TryAdd(item.Id, index + 1);
            return item with { Id = index + 1, Topic = item.Topic.Trim(), Discussion = item.Discussion.Trim() };
        }).ToArray();
        int? Reference(int? agendaId) => agendaId.HasValue && idMap.TryGetValue(agendaId.Value, out var id) ? id : null;

        return facts with
        {
            Summary = facts.Summary.Trim(),
            Meeting = new MeetingHeader
            {
                Title = Text(facts.Meeting?.Title),
                Date = Text(facts.Meeting?.Date),
                Time = Text(facts.Meeting?.Time),
                Location = Text(facts.Meeting?.Location)
            },
            Participants = new MeetingParticipants
            {
                Chair = Text(facts.Participants?.Chair),
                Secretary = Text(facts.Participants?.Secretary),
                Present = (facts.Participants?.Present ?? [])
                    .Select(person => person with { Name = person.Name.Trim(), Role = Text(person.Role) }).ToArray(),
                Absent = (facts.Participants?.Absent ?? []).Select(name => name.Trim()).ToArray()
            },
            Agenda = agenda,
            Decisions = facts.Decisions.Select(decision => decision with
            {
                Description = decision.Description.Trim(),
                AgendaId = Reference(decision.AgendaId)
            }).ToArray(),
            Actions = facts.Actions.Select(action => action with
            {
                Description = action.Description.Trim(),
                Responsible = Text(action.Responsible),
                Deadline = Text(action.Deadline),
                AgendaId = Reference(action.AgendaId)
            }).ToArray(),
            OpenIssues = facts.OpenIssues.Select(issue => issue with
            {
                Description = issue.Description.Trim(),
                AgendaId = Reference(issue.AgendaId)
            }).ToArray(),
            NextMeeting = Text(facts.NextMeeting)
        };
    }

    private static string Text(string? value) =>
        string.IsNullOrWhiteSpace(value) ? MeetingFacts.Unspecified : value.Trim();

    private static string UnwrapFence(string response)
    {
        var text = response.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal) && text.EndsWith("```", StringComparison.Ordinal))
        {
            var newline = text.IndexOf('\n');
            if (newline >= 0 && newline < text.Length - 3)
            {
                return text[(newline + 1)..^3].Trim();
            }
        }
        return text;
    }
}
