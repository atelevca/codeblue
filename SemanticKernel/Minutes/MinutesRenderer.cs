using System.Text;

namespace SemanticKernel.Minutes;

/// <summary>
/// Renders the minutes from normalized facts, following the stage 2 template of commit 3172c27
/// (headings, order and fixed texts). Every value is copied, so there is nothing for a model to
/// write: rendering in code keeps the document faithful and has no size limit.
/// </summary>
internal static class MinutesRenderer
{
    public static string Render(MeetingFacts facts)
    {
        var meeting = facts.Meeting!;
        var people = facts.Participants!;
        var present = people.Present!.Count == 0
            ? MeetingFacts.Unspecified
            : string.Join(", ", people.Present.Select(p => $"{Inline(p.Name)} – {Inline(p.Role)}"));
        var absent = people.Absent!.Count == 0 ? "Nu au fost consemnați." : string.Join(", ", people.Absent.Select(Inline));

        var document = new StringBuilder();
        document.Append("# Proces-verbal al ședinței\n");
        document.Append($"**Tema:** {Inline(meeting.Title)}\n");
        document.Append($"**Data:** {Inline(meeting.Date)} | **Ora:** {Inline(meeting.Time)} | **Locul:** {Inline(meeting.Location)}\n\n");
        document.Append("## Participanți\n");
        document.Append($"**Președinte:** {Inline(people.Chair)}\n");
        document.Append($"**Secretar:** {Inline(people.Secretary)}\n");
        document.Append($"**Prezenți:** {present}\n");
        document.Append($"**Absenți:** {absent}\n\n");
        document.Append(RenderAgenda(facts)).Append("\n\n");
        document.Append("## Desfășurarea ședinței\n");
        if (facts.Agenda!.Count == 0)
        {
            document.Append(Block(facts.Summary)).Append("\n\n");
        }
        foreach (var item in facts.Agenda)
        {
            document.Append($"### {item.Id}. {Inline(item.Topic)}\n").Append(Block(item.Discussion)).Append("\n\n");
        }
        document.Append(RenderTables(facts)).Append("\n\n");
        document.Append("## Următoarea ședință\n").Append(Block(facts.NextMeeting!)).Append("\n\n");
        document.Append("## Rezumat\n").Append(Block(facts.Summary)).Append("\n\n");
        document.Append("## Semnături\n");
        document.Append($"Președinte: {Inline(people.Chair)} ____________________\n");
        document.Append($"Secretar: {Inline(people.Secretary)} ____________________");
        return document.ToString();
    }

    // Plain text, as the model's output was: one line for header values and names...
    private static string Inline(string? value) =>
        string.IsNullOrWhiteSpace(value) ? MeetingFacts.Unspecified : value.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Trim();

    // ...and normalized line endings for paragraphs.
    private static string Block(string value) => value.Replace("\r\n", "\n").Replace('\r', '\n').Trim();

    private static string RenderAgenda(MeetingFacts facts)
    {
        var agenda = facts.Agenda!;
        if (agenda.Count == 0)
        {
            return "## Ordinea de zi\n\nNu a fost consemnată ordinea de zi.";
        }
        var items = string.Join('\n', agenda.Select(item => $"{item.Id}. {Inline(item.Topic)}"));
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
}
