using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace HealthTech.Documents;

// Only complete getContents() snapshots are accepted, never incremental retain/delete operations.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record QuillDelta
{
    public required IReadOnlyList<QuillOperation> Ops { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record QuillOperation
{
    public required string Insert { get; init; }
    public Dictionary<string, JsonElement>? Attributes { get; init; }
}

internal sealed record QuillRun(string Text, bool Bold, bool Italic, bool Underline, string? Link);
internal sealed record QuillLine(IReadOnlyList<QuillRun> Runs, int Header, string? List, int Indent, string? Align);

internal static class QuillDocument
{
    internal static IReadOnlyList<QuillLine> Read(QuillDelta delta)
    {
        if (delta.Ops is not { Count: > 0 and <= 4000 })
            throw Invalid("Delta trebuie să conțină între 1 și 4000 de operații insert.");
        var lines = new List<QuillLine>();
        var runs = new List<QuillRun>();
        var length = 0;
        foreach (var operation in delta.Ops)
        {
            if (operation == null || string.IsNullOrEmpty(operation.Insert) || operation.Insert.Contains('\r'))
                throw Invalid("Fiecare operație trebuie să conțină text insert nevid, cu newline LF.");
            length += operation.Insert.Length;
            if (length > 50000)
                throw Invalid("Documentul depășește 50000 de caractere.");
            var attributes = operation.Attributes ?? [];
            foreach (var (name, value) in attributes)
            {
                var valid = name switch
                {
                    "bold" or "italic" or "underline" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                    "header" => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var h) && h is >= 1 and <= 6,
                    "indent" => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var indent) && indent is >= 0 and <= 8,
                    "list" => value.ValueKind == JsonValueKind.String && value.GetString() is "ordered" or "bullet",
                    "align" => value.ValueKind == JsonValueKind.String && value.GetString() is "left" or "center" or "right" or "justify",
                    "link" => value.ValueKind == JsonValueKind.String && Uri.TryCreate(value.GetString(), UriKind.Absolute, out var uri) &&
                        uri.Scheme is "https" or "http" or "mailto",
                    _ => false
                };
                if (!valid)
                    throw Invalid($"Format Quill nesuportat sau invalid: {name}.");
            }
            if (attributes.ContainsKey("header") && attributes.ContainsKey("list"))
                throw Invalid("O linie nu poate fi simultan titlu și listă.");
            var pieces = operation.Insert.Split('\n');
            for (var index = 0; index < pieces.Length; index++)
            {
                if (pieces[index].Length > 0)
                    runs.Add(new QuillRun(pieces[index], Flag(attributes, "bold"), Flag(attributes, "italic"),
                        Flag(attributes, "underline"), Text(attributes, "link")));
                if (index < pieces.Length - 1)
                {
                    lines.Add(new QuillLine(runs.ToArray(), Number(attributes, "header"), Text(attributes, "list"),
                        Number(attributes, "indent"), Text(attributes, "align")));
                    runs.Clear();
                }
            }
        }
        if (!delta.Ops[^1].Insert.EndsWith('\n') || runs.Count > 0)
            throw Invalid("Delta complet trebuie să se termine cu newline, ca rezultatul quill.getContents().");
        if (!lines.Any(line => line.Runs.Any(run => !string.IsNullOrWhiteSpace(run.Text))))
            throw Invalid("Documentul nu poate fi gol.");
        return lines;
    }

    // Canonical textual view used by the verification model. The editable/PDF source remains Delta.
    internal static string ToMarkdown(QuillDelta delta)
    {
        var result = new StringBuilder();
        var numbering = new int[9];
        foreach (var line in Read(delta))
        {
            result.Append(' ', line.Indent * 2);
            if (line.Header > 0) result.Append('#', line.Header).Append(' ');
            result.Append(ListPrefix(line, numbering));
            foreach (var run in line.Runs)
                result.Append(run.Text);
            result.AppendLine();
        }
        return result.ToString().Replace("\r\n", "\n");
    }

    internal static string ListPrefix(QuillLine line, int[] numbering)
    {
        if (line.List == null)
        {
            Array.Clear(numbering);
            return "";
        }
        Array.Clear(numbering, line.Indent + 1, numbering.Length - line.Indent - 1);
        // Quill cycles decimal/lower-alpha/lower-roman by depth. Bullets reset only deeper counters.
        if (line.List != "ordered") return "• ";
        var number = ++numbering[line.Indent];
        var marker = (line.Indent % 3) switch
        {
            1 => Alphabetic(number),
            2 => Roman(number),
            _ => number.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        return marker + ". ";
    }

    private static string Alphabetic(int number)
    {
        var result = "";
        while (number > 0)
        {
            number--;
            result = (char)('a' + number % 26) + result;
            number /= 26;
        }
        return result;
    }

    private static string Roman(int number)
    {
        if (number > 3999) return number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var result = new StringBuilder();
        foreach (var (value, symbol) in new (int, string)[]
        {
            (1000, "m"), (900, "cm"), (500, "d"), (400, "cd"), (100, "c"), (90, "xc"),
            (50, "l"), (40, "xl"), (10, "x"), (9, "ix"), (5, "v"), (4, "iv"), (1, "i")
        })
        {
            while (number >= value)
            {
                result.Append(symbol);
                number -= value;
            }
        }
        return result.ToString();
    }

    // Generated MOM tables become labelled paragraphs: standard Quill needs no custom table plugin.
    internal static QuillDelta FromMarkdown(string markdown)
    {
        var ops = new List<QuillOperation>();
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith('|') && i + 1 < lines.Length &&
                Cells(lines[i + 1]).All(cell => Regex.IsMatch(cell, @"^:?-{3,}:?$")))
            {
                var headings = Cells(line);
                i += 2;
                while (i < lines.Length && lines[i].Trim().StartsWith('|'))
                {
                    var cells = Cells(lines[i++]);
                    for (var column = 0; column < cells.Length; column++)
                    {
                        AddLine(ops, (column < headings.Length ? headings[column] + ": " : "") + cells[column], null);
                    }
                    AddLine(ops, "", null);
                }
                i--;
                continue;
            }
            Dictionary<string, JsonElement>? attributes = null;
            var heading = Regex.Match(line, @"^(#{1,6})\s+(.*)$");
            var list = Regex.Match(line, @"^(?:[-*+] |\d+[.)] )(.*)$");
            if (heading.Success)
            {
                attributes = new() { ["header"] = JsonSerializer.SerializeToElement(heading.Groups[1].Length) };
                line = heading.Groups[2].Value;
            }
            else if (list.Success)
            {
                attributes = new() { ["list"] = JsonSerializer.SerializeToElement(char.IsDigit(line[0]) ? "ordered" : "bullet") };
                line = list.Groups[1].Value;
            }
            AddLine(ops, line, attributes);
        }
        var delta = new QuillDelta { Ops = ops };
        Read(delta);
        return delta;
    }

    private static void AddLine(List<QuillOperation> ops, string text, Dictionary<string, JsonElement>? attributes)
    {
        text = WebUtility.HtmlDecode(Regex.Replace(text, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase));
        foreach (var line in text.Split('\n'))
        {
            foreach (var part in Regex.Split(line, @"(\*\*[^*]+\*\*|\*[^*]+\*)"))
            {
                if (part.Length == 0) continue;
                var bold = part.Length > 4 && part.StartsWith("**") && part.EndsWith("**");
                var italic = !bold && part.Length > 2 && part.StartsWith('*') && part.EndsWith('*');
                ops.Add(new QuillOperation
                {
                    Insert = bold ? part[2..^2] : italic ? part[1..^1] : part,
                    Attributes = bold ? new() { ["bold"] = JsonSerializer.SerializeToElement(true) }
                        : italic ? new() { ["italic"] = JsonSerializer.SerializeToElement(true) } : null
                });
            }
            ops.Add(new QuillOperation { Insert = "\n", Attributes = attributes });
        }
    }

    private static string[] Cells(string text) => Regex.Split(text.Trim().Trim('|'), @"(?<!\\)\|")
        .Select(cell => cell.Trim().Replace(@"\|", "|")).ToArray();
    private static bool Flag(Dictionary<string, JsonElement> attrs, string key) => attrs.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.True;
    private static string? Text(Dictionary<string, JsonElement> attrs, string key) => attrs.TryGetValue(key, out var value) ? value.GetString() : null;
    private static int Number(Dictionary<string, JsonElement> attrs, string key) => attrs.TryGetValue(key, out var value) ? value.GetInt32() : 0;
    private static DocumentException Invalid(string message) => new(400, message);
}
