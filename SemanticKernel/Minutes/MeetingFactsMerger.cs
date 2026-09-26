using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SemanticKernel.Minutes;

/// <summary>
/// Merges the facts of consecutive transcript fragments (each already normalized, agenda ids 1..m per
/// fragment). Deterministic: no model sees or rewrites a decision, action or issue here.
/// </summary>
internal static partial class MeetingFactsMerger
{
    public static MeetingFacts Merge(IReadOnlyList<MeetingFacts> parts)
    {
        var agenda = new List<AgendaItem>();
        var decisions = new List<MeetingDecision>();
        var actions = new List<MeetingAction>();
        var issues = new List<MeetingIssue>();
        foreach (var part in parts)
        {
            // A topic continued from an earlier fragment (same title, thanks to the topics hint) joins it.
            var map = new Dictionary<int, int>();
            foreach (var item in part.Agenda!)
            {
                var existing = agenda.FindIndex(a => Key(a.Topic) == Key(item.Topic));
                if (existing >= 0)
                {
                    agenda[existing] = agenda[existing] with { Discussion = agenda[existing].Discussion + " " + item.Discussion };
                    map[item.Id] = agenda[existing].Id;
                }
                else
                {
                    agenda.Add(item with { Id = agenda.Count + 1 });
                    map[item.Id] = agenda.Count;
                }
            }
            int? Reference(int? id) => id.HasValue && map.TryGetValue(id.Value, out var global) ? global : null;
            AddDistinct(decisions, part.Decisions!.Select(d => d with { AgendaId = Reference(d.AgendaId) }), d => (Key(d.Description), d.AgendaId));
            AddDistinct(actions, part.Actions!.Select(a => a with { AgendaId = Reference(a.AgendaId) }), a => (Key(a.Description), a.AgendaId));
            AddDistinct(issues, part.OpenIssues!.Select(i => i with { AgendaId = Reference(i.AgendaId) }), i => (Key(i.Description), i.AgendaId));
        }

        var present = new List<MeetingParticipant>();
        foreach (var person in parts.SelectMany(p => p.Participants!.Present!))
        {
            var at = present.FindIndex(p => Key(p.Name) == Key(person.Name));
            if (at < 0)
            {
                present.Add(person);
            }
            else if (!Specified(present[at].Role) && Specified(person.Role))
            {
                present[at] = present[at] with { Role = person.Role };
            }
        }
        var presentNames = present.Select(p => Key(p.Name)).ToHashSet();

        return new MeetingFacts
        {
            Meeting = new MeetingHeader
            {
                Title = First(parts, f => f.Meeting!.Title),
                Date = First(parts, f => f.Meeting!.Date),
                Time = First(parts, f => f.Meeting!.Time),
                Location = First(parts, f => f.Meeting!.Location)
            },
            Participants = new MeetingParticipants
            {
                Chair = First(parts, f => f.Participants!.Chair),
                Secretary = First(parts, f => f.Participants!.Secretary),
                Present = present,
                Absent = parts.SelectMany(p => p.Participants!.Absent!)
                    .Where(name => !presentNames.Contains(Key(name))).DistinctBy(Key).ToArray()
            },
            AgendaExplicit = parts.Any(p => p.AgendaExplicit),
            Agenda = agenda,
            Decisions = decisions,
            Actions = actions,
            OpenIssues = issues,
            // The next meeting is usually agreed at the end.
            NextMeeting = parts.Select(p => p.NextMeeting).LastOrDefault(Specified) ?? MeetingFacts.Unspecified,
            // Replaced by the consolidated summary; this is the fallback when consolidation fails.
            Summary = string.Join(" ", parts.Select(p => p.Summary))
        };
    }

    /// <summary>
    /// Joins each agenda item the consolidation marked as the same topic as an earlier one into that earlier
    /// item; chains (6 into 5, 5 into 2) end at the first. Unknown ids, merges into a later item and ids
    /// named more than once are ignored. Ids are left as they are; NormalizeFacts renumbers them afterwards.
    /// </summary>
    public static MeetingFacts ApplyTopicMerges(MeetingFacts facts, IReadOnlyList<(int Id, int Into)> merges)
    {
        var agenda = facts.Agenda!;
        var known = agenda.Select(a => a.Id).ToHashSet();
        var repeated = merges.GroupBy(m => m.Id).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        var into = merges
            .Where(m => known.Contains(m.Id) && known.Contains(m.Into) && m.Into < m.Id && !repeated.Contains(m.Id))
            .ToDictionary(m => m.Id, m => m.Into);
        // Into < Id, so every chain ends.
        int Root(int id)
        {
            while (into.TryGetValue(id, out var next))
            {
                id = next;
            }
            return id;
        }
        var target = into.Keys.ToDictionary(id => id, Root);
        if (target.Count == 0)
        {
            return facts;
        }

        var merged = agenda.Where(a => !target.ContainsKey(a.Id)).Select(a => a with
        {
            Discussion = string.Join(" ", agenda
                .Where(o => o.Id == a.Id || (target.TryGetValue(o.Id, out var into) && into == a.Id))
                .Select(o => o.Discussion))
        }).ToArray();
        int? Reference(int? id) => id.HasValue && target.TryGetValue(id.Value, out var into) ? into : id;
        return facts with
        {
            Agenda = merged,
            // A row stated under both merged topics now points at one topic: keep it once.
            Decisions = Distinct(facts.Decisions!.Select(d => d with { AgendaId = Reference(d.AgendaId) }), d => (Key(d.Description), d.AgendaId)),
            Actions = Distinct(facts.Actions!.Select(a => a with { AgendaId = Reference(a.AgendaId) }), a => (Key(a.Description), a.AgendaId)),
            OpenIssues = Distinct(facts.OpenIssues!.Select(i => i with { AgendaId = Reference(i.AgendaId) }), i => (Key(i.Description), i.AgendaId))
        };
    }

    private static T[] Distinct<T, TKey>(IEnumerable<T> items, Func<T, TKey> key)
    {
        var list = new List<T>();
        AddDistinct(list, items, key);
        return list.ToArray();
    }

    private static void AddDistinct<T, TKey>(List<T> target, IEnumerable<T> items, Func<T, TKey> key)
    {
        var seen = target.Select(key).ToHashSet();
        foreach (var item in items)
        {
            if (seen.Add(key(item)))
            {
                target.Add(item);
            }
        }
    }

    private static bool Specified(string? value) => !string.IsNullOrWhiteSpace(value) && value != MeetingFacts.Unspecified;

    private static string First(IEnumerable<MeetingFacts> parts, Func<MeetingFacts, string?> field) =>
        parts.Select(field).FirstOrDefault(Specified) ?? MeetingFacts.Unspecified;

    // Case, diacritics (ș/ş, ț/ţ, ă, â, î) and spacing do not make two names or titles different.
    private static string Key(string text)
    {
        var builder = new StringBuilder();
        foreach (var c in text.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(char.IsWhiteSpace(c) ? ' ' : c);
            }
        }
        return Spaces().Replace(builder.ToString(), " ").Trim(' ', '.', ',', ';', ':');
    }

    [GeneratedRegex(" +")]
    private static partial Regex Spaces();
}
