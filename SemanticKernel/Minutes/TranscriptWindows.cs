using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using SemanticKernel.MedicalCorrection;

namespace SemanticKernel.Minutes;

/// <summary>
/// Splits the dialogue text ("Speaker N:\n&lt;text&gt;" blocks separated by a blank line) into windows of
/// whole turns, each at most <c>maxTokens</c>. Windows do not overlap. A turn longer than a window is cut
/// at sentence boundaries and every part keeps its speaker line, so attribution survives.
/// Sizes are measured on the JSON-escaped text, because that is how a window travels in a request.
/// </summary>
public static partial class TranscriptWindows
{
    private const string Separator = "\n\n";
    // "\n\n" becomes "\\n\\n" inside a JSON string.
    private const int SeparatorTokens = 3;
    private const int SentencePieceCharacters = 300;

    private static readonly JsonSerializerOptions Escaping = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static async Task<IReadOnlyList<string>> SplitAsync(string transcript, int maxTokens, ITokenCounter tokens, CancellationToken ct)
    {
        var windows = new List<string>();
        var current = new List<string>();
        var used = 0;
        foreach (var block in Blocks(transcript))
        {
            foreach (var part in await FitBlockAsync(block, maxTokens, tokens, ct))
            {
                var size = await CountAsync(part, tokens, ct) + SeparatorTokens;
                if (current.Count > 0 && used + size > maxTokens)
                {
                    windows.Add(string.Join(Separator, current));
                    current.Clear();
                    used = 0;
                }
                current.Add(part);
                used += size;
            }
        }
        if (current.Count > 0)
        {
            windows.Add(string.Join(Separator, current));
        }
        return windows;
    }

    // Turns are separated by blank lines (TranscriptDialogue.Format). A text without any blank line (an
    // edited or foreign file) is split at its speaker lines instead, so one speaker's label is not
    // stamped on every part of the whole transcript.
    private static IEnumerable<string> Blocks(string transcript)
    {
        var text = transcript.Replace("\r\n", "\n").Replace('\r', '\n');
        if (text.Contains(Separator, StringComparison.Ordinal))
        {
            return text.Split(Separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        var blocks = new List<string>();
        var current = new StringBuilder();
        foreach (var line in text.Split('\n'))
        {
            if (IsLabel(line) && current.Length > 0)
            {
                blocks.Add(current.ToString().Trim());
                current.Clear();
            }
            current.Append(line).Append('\n');
        }
        if (current.ToString().Trim().Length > 0)
        {
            blocks.Add(current.ToString().Trim());
        }
        return blocks;
    }

    private static async Task<IReadOnlyList<string>> FitBlockAsync(string block, int maxTokens, ITokenCounter tokens, CancellationToken ct)
    {
        if (await CountAsync(block, tokens, ct) + SeparatorTokens <= maxTokens)
        {
            return [block];
        }

        // Only a real speaker line is repeated on every part; any other first line is content.
        var newline = block.IndexOf('\n');
        var label = newline > 0 && IsLabel(block[..newline]) ? block[..newline] : "";
        var body = label.Length > 0 ? block[(newline + 1)..] : block;
        var labelTokens = label.Length == 0 ? 0 : await CountAsync(label + "\n", tokens, ct);
        var parts = new List<string>();
        var text = new StringBuilder();
        var used = labelTokens + SeparatorTokens;
        foreach (var sentence in TextPieces.Split(body, SentencePieceCharacters))
        {
            var size = await CountAsync(sentence, tokens, ct);
            if (text.Length > 0 && used + size > maxTokens)
            {
                parts.Add(WithLabel(label, text.ToString()));
                text.Clear();
                used = labelTokens + SeparatorTokens;
            }
            text.Append(sentence);
            used += size;
        }
        if (text.Length > 0)
        {
            parts.Add(WithLabel(label, text.ToString()));
        }
        return parts;
    }

    // The string as it appears inside the request JSON, without the surrounding quotes.
    private static Task<int> CountAsync(string text, ITokenCounter tokens, CancellationToken ct) =>
        tokens.CountAsync(JsonSerializer.Serialize(text, Escaping)[1..^1], ct);

    private static bool IsLabel(string line) => Label().IsMatch(line.Trim());

    private static string WithLabel(string label, string text) =>
        label.Length == 0 ? text.Trim() : label + "\n" + text.Trim();

    // "Speaker 2:" or a bound name ("Dr. Ana Popa:"): a short line ending in a colon, at most five words.
    [GeneratedRegex(@"^(?:\S+\s){0,4}\S+:$")]
    private static partial Regex Label();
}
