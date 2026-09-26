using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using LLamaSharp.SemanticKernel;
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

    // Headings of the generation template, in order. The agenda list and everything between
    // "## Decizii" and "## Următoarea ședință" are rendered from the facts, not taken from the model.
    private static readonly string[] RequiredHeadings =
    [
        "# Proces-verbal al ședinței", "## Participanți", "## Ordinea de zi", "## Desfășurarea ședinței",
        "## Decizii", "## Acțiuni", "## Probleme deschise", "## Următoarea ședință", "## Rezumat"
    ];

    private readonly IChatCompletionProvider _chatProvider;
    private readonly MinutesOptions _options;
    private readonly ILogger<MeetingMinutesGenerator> _logger;

    public MeetingMinutesGenerator(IChatCompletionProvider chatProvider, IOptions<MinutesOptions> options,
        ILogger<MeetingMinutesGenerator> logger)
    {
        _chatProvider = chatProvider;
        _options = options.Value;
        _logger = logger;
        if (_options.MaxTranscriptCharacters <= 0 || _options.MaxFactsCharacters <= 0 ||
            _options.ExtractionMaxTokens <= 0 || _options.GenerationMaxTokens <= 0 ||
            _options.MaxVerificationCharacters <= 0 || _options.VerificationMaxTokens <= 0 ||
            _options.MaxRetries is < 0 or > 10)
        {
            throw new ArgumentException("Minutes limits must be positive and MaxRetries must be between 0 and 10.", nameof(options));
        }
    }

    public async Task<MeetingMinutesResult> GenerateAsync(string transcript, MeetingMetadata? metadata = null,
        CancellationToken ct = default)
    {
        var facts = await ExtractFactsAsync(transcript, metadata, ct);
        var minutes = await GenerateMinutesAsync(facts, ct);
        var verification = await VerifyMinutesAsync(transcript, minutes, metadata, ct);
        return new MeetingMinutesResult(facts, minutes, verification);
    }

    public Task<MinutesVerification> VerifyMinutesAsync(string transcript, string minutesMarkdown,
        MeetingMetadata? metadata = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transcript);
        ArgumentException.ThrowIfNullOrWhiteSpace(minutesMarkdown);
        EnsureLength(transcript, _options.MaxTranscriptCharacters, "transcript");
        var input = JsonSerializer.Serialize(new { transcript, metadata, document = minutesMarkdown }, FactsJsonOptions);
        EnsureLength(input, _options.MaxVerificationCharacters, "verification input");
        // A source quotation may come from the transcript or from a metadata value (a bound
        // participant's name, the record title): both are sources of truth for the model.
        var sources = new List<string> { transcript };
        if (metadata != null)
        {
            sources.AddRange(MetadataValues(metadata));
        }
        // Verify the final rendered document against the source, not against potentially incomplete extracted facts.
        return RunStageAsync("Minutes Verification", "minutes_verification.system.txt", input,
            _options.VerificationMaxTokens, response => ParseVerification(response, sources, minutesMarkdown), ct);
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
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transcript);
        EnsureLength(transcript, _options.MaxTranscriptCharacters, "transcript");
        return await RunStageAsync("Fact Extraction and Summary", "minutes_extraction.system.txt",
            JsonSerializer.Serialize(new { transcript, metadata }, FactsJsonOptions), _options.ExtractionMaxTokens,
            response => NormalizeFacts(JsonSerializer.Deserialize<MeetingFacts>(UnwrapFence(response), FactsJsonOptions)
                ?? throw new JsonException("Expected a facts object.")), ct);
    }

    public Task<string> GenerateMinutesAsync(MeetingFacts facts, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var normalizedFacts = NormalizeFacts(facts);
        var input = JsonSerializer.Serialize(normalizedFacts, FactsJsonOptions);
        EnsureLength(input, _options.MaxFactsCharacters, "extracted facts");
        // A fresh history contains only validated stage-one facts, never the transcript.
        return RunStageAsync("Minutes Generation", "minutes_generation.system.txt", input,
            _options.GenerationMaxTokens, response =>
            {
                var markdown = UnwrapFence(response);
                var lines = markdown.Split('\n');
                var previousIndex = -1;
                foreach (var heading in RequiredHeadings)
                {
                    var indices = Enumerable.Range(0, lines.Length).Where(i => lines[i].Trim() == heading).ToArray();
                    if (indices.Length != 1 || indices[0] <= previousIndex)
                    {
                        throw new FormatException($"Missing, duplicate or out-of-order minutes heading: {heading}");
                    }
                    previousIndex = indices[0];
                }
                // The agenda list, decisions, actions and open issues are rendered from the facts,
                // with their agenda references. The second model may format prose, but cannot drop
                // an item, move it to another agenda point or replace an unspecified owner/deadline
                // with a guess. (On a short recording the model tends to keep the agenda note and
                // skip the list itself.)
                var agendaIndex = Array.FindIndex(lines, line => line.Trim() == "## Ordinea de zi");
                var courseIndex = Array.FindIndex(lines, line => line.Trim() == "## Desfășurarea ședinței");
                var decisionsIndex = Array.FindIndex(lines, line => line.Trim() == "## Decizii");
                var nextMeetingIndex = Array.FindIndex(lines, line => line.Trim() == "## Următoarea ședință");
                return string.Join('\n', lines.Take(agendaIndex)) + "\n\n" +
                    RenderAgenda(normalizedFacts) + "\n\n" +
                    string.Join('\n', lines.Skip(courseIndex).Take(decisionsIndex - courseIndex)) + "\n\n" +
                    RenderTables(normalizedFacts) + "\n\n" + string.Join('\n', lines.Skip(nextMeetingIndex));
            }, ct);
    }

    private static string RenderAgenda(MeetingFacts facts)
    {
        var agenda = facts.Agenda!;
        if (agenda.Count == 0)
        {
            return "## Ordinea de zi\n\nNu a fost consemnată ordinea de zi.";
        }
        var items = string.Join('\n', agenda.Select(item => $"{item.Id}. {item.Topic}"));
        var note = facts.AgendaExplicit ? "" : "\n\n*Ordinea de zi a fost stabilită pe baza temelor discutate.*";
        return "## Ordinea de zi\n\n" + items + note;
    }

    private static string RenderTables(MeetingFacts facts) =>
        "## Decizii\n\n" + RenderDecisions(facts.Decisions!) + "\n\n" +
        "## Acțiuni\n\n" + RenderActions(facts.Actions!) + "\n\n" +
        "## Probleme deschise\n\n" + RenderIssues(facts.OpenIssues!);

    private static string RenderDecisions(IReadOnlyList<MeetingDecision> decisions)
    {
        if (decisions.Count == 0)
        {
            return "Nu au fost consemnate decizii.";
        }
        var rows = decisions.Select((decision, index) =>
            $"| {index + 1} | {Cell(decision.Description)} | {AgendaCell(decision.AgendaId)} |");
        return "| Nr. | Decizie | Punct |\n| --- | --- | --- |\n" + string.Join('\n', rows);
    }

    private static string RenderActions(IReadOnlyList<MeetingAction> actions)
    {
        if (actions.Count == 0)
        {
            return "Nu au fost consemnate acțiuni.";
        }
        var rows = actions.Select((action, index) =>
            $"| {index + 1} | {Cell(action.Description)} | {Cell(action.Responsible!)} | {Cell(action.Deadline!)} | {AgendaCell(action.AgendaId)} |");
        return "| Nr. | Acțiune | Responsabil | Termen | Punct |\n| --- | --- | --- | --- | --- |\n" + string.Join('\n', rows);
    }

    private static string RenderIssues(IReadOnlyList<MeetingIssue> issues)
    {
        if (issues.Count == 0)
        {
            return "Nu au fost consemnate probleme deschise.";
        }
        var rows = issues.Select((issue, index) =>
            $"| {index + 1} | {Cell(issue.Description)} | {AgendaCell(issue.AgendaId)} |");
        return "| Nr. | Problemă | Punct |\n| --- | --- | --- |\n" + string.Join('\n', rows);
    }

    private static string AgendaCell(int? agendaId) =>
        agendaId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "–";

    private static string Cell(string value) => System.Net.WebUtility.HtmlEncode(value)
        .Replace("\\", "&#92;").Replace("|", "&#124;").Replace("`", "&#96;")
        .Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "<br>");

    private async Task<T> RunStageAsync<T>(string stage, string promptFile, string input, int maxTokens,
        Func<string, T> parse, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var prompt = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Prompts", promptFile), ct);
        var chat = await _chatProvider.GetChatCompletionAsync(ct);
        var settings = new LLamaSharpPromptExecutionSettings { Temperature = 0, MaxTokens = maxTokens };
        Exception? lastError = null;
        for (var attempt = 0; attempt <= _options.MaxRetries; attempt++)
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
        throw new InvalidOperationException($"MOM stage '{stage}' failed after {_options.MaxRetries + 1} attempts.", lastError);
    }

    /// <summary>
    /// Rejects a reply that lacks the lists or has empty entries, fills every missing text
    /// field with "Nespecificat", renumbers the agenda 1..n and drops agenda references that
    /// point nowhere. A normalized object is what the generation prompt describes.
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

    private static void EnsureLength(string text, int maximum, string name)
    {
        if (text.Length > maximum)
        {
            throw new ArgumentException($"The {name} exceeds the configured limit of {maximum} characters. " +
                "Increase the corresponding Minutes limit together with Llm:ContextSize; input is never truncated.", name);
        }
    }
}
