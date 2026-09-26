using System.Text.Encodings.Web;
using System.Text.Json;
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

    public async Task<MeetingMinutesResult> GenerateAsync(string transcript, CancellationToken ct = default)
    {
        var facts = await ExtractFactsAsync(transcript, ct);
        var minutes = await GenerateMinutesAsync(facts, ct);
        var verification = await VerifyMinutesAsync(transcript, minutes, ct);
        return new MeetingMinutesResult(facts, minutes, verification);
    }

    public Task<MinutesVerification> VerifyMinutesAsync(string transcript, string minutesMarkdown, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transcript);
        ArgumentException.ThrowIfNullOrWhiteSpace(minutesMarkdown);
        EnsureLength(transcript, _options.MaxTranscriptCharacters, "transcript");
        var input = JsonSerializer.Serialize(new { transcript, document = minutesMarkdown }, JsonOptions);
        EnsureLength(input, _options.MaxVerificationCharacters, "verification input");
        // Verify the final rendered document against the source, not against potentially incomplete extracted facts.
        return RunStageAsync("Minutes Verification", "minutes_verification.system.txt", input,
            _options.VerificationMaxTokens, response =>
            {
                var verification = JsonSerializer.Deserialize<MinutesVerification>(UnwrapFence(response), JsonOptions)
                    ?? throw new JsonException("Expected a verification object.");
                if (string.IsNullOrWhiteSpace(verification.Summary) || verification.Findings == null)
                {
                    throw new FormatException("Verification must contain summary and findings.");
                }
                foreach (var finding in verification.Findings)
                {
                    if (finding == null || string.IsNullOrWhiteSpace(finding.Description) ||
                        string.IsNullOrWhiteSpace(finding.SuggestedCorrection) ||
                        finding.Kind is not ("Unsupported" or "Omission" or "Contradiction"))
                    {
                        throw new FormatException("Invalid verification finding.");
                    }
                    ValidateQuote(finding.TranscriptQuote, transcript, finding.Kind != "Unsupported");
                    ValidateQuote(finding.DocumentQuote, minutesMarkdown, finding.Kind != "Omission");
                }
                return verification;
            }, ct);
    }

    private static void ValidateQuote(string? quote, string source, bool required)
    {
        if (quote == null && !required)
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(quote) || !source.Contains(quote, StringComparison.Ordinal))
        {
            throw new FormatException("Verification evidence must be an exact quotation from the supplied source.");
        }
    }

    public async Task<MeetingFacts> ExtractFactsAsync(string transcript, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transcript);
        EnsureLength(transcript, _options.MaxTranscriptCharacters, "transcript");
        return await RunStageAsync("Fact Extraction and Summary", "minutes_extraction.system.txt",
            JsonSerializer.Serialize(new { transcript }, JsonOptions), _options.ExtractionMaxTokens,
            response => NormalizeFacts(JsonSerializer.Deserialize<MeetingFacts>(UnwrapFence(response), JsonOptions)
                ?? throw new JsonException("Expected a facts object.")), ct);
    }

    public Task<string> GenerateMinutesAsync(MeetingFacts facts, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var normalizedFacts = NormalizeFacts(facts);
        var input = JsonSerializer.Serialize(normalizedFacts, JsonOptions);
        EnsureLength(input, _options.MaxFactsCharacters, "extracted facts");
        // A fresh history contains only validated stage-one facts, never the transcript.
        return RunStageAsync("Minutes Generation", "minutes_generation.system.txt", input,
            _options.GenerationMaxTokens, response =>
            {
                var markdown = UnwrapFence(response);
                var lines = markdown.Split('\n');
                var previousIndex = -1;
                foreach (var heading in new[] { "# Proces-verbal", "## Rezumat", "## Decizii", "## Acțiuni", "## Probleme" })
                {
                    var indices = Enumerable.Range(0, lines.Length).Where(i => lines[i].Trim() == heading).ToArray();
                    if (indices.Length != 1 || indices[0] <= previousIndex)
                    {
                        throw new FormatException($"Missing, duplicate or out-of-order minutes heading: {heading}");
                    }
                    previousIndex = indices[0];
                }
                // Preserve every action and its attribution exactly as extracted. The second model
                // may format prose, but cannot replace an unspecified owner/deadline with a guess.
                var actionsIndex = Array.FindIndex(lines, line => line.Trim() == "## Acțiuni");
                var issuesIndex = Array.FindIndex(lines, line => line.Trim() == "## Probleme");
                return string.Join('\n', lines.Take(actionsIndex + 1)) + "\n\n" +
                    RenderActions(normalizedFacts.Actions) + "\n\n" + string.Join('\n', lines.Skip(issuesIndex));
            }, ct);
    }

    private static string RenderActions(IReadOnlyList<MeetingAction> actions)
    {
        if (actions.Count == 0)
        {
            return "Nu au fost consemnate acțiuni.";
        }
        var rows = actions.Select((action, index) =>
            $"| {index + 1} | {Cell(action.Description)} | {Cell(action.Responsible!)} | {Cell(action.Deadline!)} |");
        return "| Nr. | Acțiune | Responsabil | Termen |\n| --- | --- | --- | --- |\n" + string.Join('\n', rows);
    }

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
                _logger.LogWarning("MOM stage {Stage} returned invalid output on attempt {Attempt}", stage, attempt + 1);
            }
        }
        throw new InvalidOperationException($"MOM stage '{stage}' failed after {_options.MaxRetries + 1} attempts.", lastError);
    }

    private static MeetingFacts NormalizeFacts(MeetingFacts facts)
    {
        if (string.IsNullOrWhiteSpace(facts.Summary) || facts.Decisions == null || facts.Actions == null ||
            facts.Issues == null || facts.Decisions.Any(string.IsNullOrWhiteSpace) ||
            facts.Issues.Any(string.IsNullOrWhiteSpace) ||
            facts.Actions.Any(action => action == null || string.IsNullOrWhiteSpace(action.Description)))
        {
            throw new FormatException("Facts must include summary, decisions, actions and issues; entries cannot be empty.");
        }
        return facts with
        {
            Actions = facts.Actions.Select(action => action with
            {
                Responsible = string.IsNullOrWhiteSpace(action.Responsible) ? MeetingAction.Unspecified : action.Responsible.Trim(),
                Deadline = string.IsNullOrWhiteSpace(action.Deadline) ? MeetingAction.Unspecified : action.Deadline.Trim()
            }).ToArray()
        };
    }

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
