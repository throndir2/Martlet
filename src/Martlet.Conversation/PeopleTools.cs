using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.Core.Speakers;
using Martlet.Providers;

namespace Martlet.Conversation;

/// <summary>The People check-in tool set (<c>people</c>): reads the voice list (Companion › People). For each voice it gives its
/// tag (V3), the name Martlet uses first, every other name and whether the owner typed it or Martlet learned it in conversation,
/// whether it is the signed-in user or linked to someone's Martlet account, and how often and when it was last heard. It never
/// gives voiceprints, clips or audio, and it changes nothing. Learning names uses the same tools (docs/VOICES.md).</summary>
public static class PeopleTools
{
    public const string SetId = "people";
    public const string List = "people_list", Lately = "people_lately";
    /// <summary>The most voices one answer lists.</summary>
    public const int MaximumListed = 20;
    public const int DefaultLatelyMinutes = 60;
    public const int MaximumLatelyMinutes = 7 * 24 * 60;

    public static CheckInToolSet Set { get; } = new(SetId, "People",
        "Looks up the people in Martlet's voice list (Companion › People): the names each voice goes by, whether it is you or " +
        "linked to someone's account, how often and when it was last heard, and who spoke lately. Never voiceprints or audio.",
    [
        new(List, "Lists the people Martlet knows by voice, most recently heard first: each voice's tag (like V3), the name " +
            "Martlet uses first, its other names (typed by the owner or learned in conversation), whether it is the signed-in user " +
            "or linked to an account, and how often and when it was last heard. Give a name or a tag to find one person.",
            $$$"""{"type":"object","properties":{"query":{"type":"string","description":"A name, part of one, or a voice tag like V3. Empty lists everyone, most recently heard first (at most {{{MaximumListed}}})."}},"additionalProperties":false}"""),
        new(Lately, "Lists who spoke lately: the voices heard in the last minutes, most recent first, with their names.",
            $$$"""{"type":"object","properties":{"minutes":{"type":"integer","minimum":1,"maximum":{{{MaximumLatelyMinutes}}},"description":"How far back to look, in minutes (default {{{DefaultLatelyMinutes}}})."}},"additionalProperties":false}""")
    ]);

    /// <summary>One answer of the set's tools: <see cref="Result"/> for the model (its first line holds only counts, so it can
    /// show on the card and in check-ins-status.json) and the voices it listed.</summary>
    public sealed record Answer(ConversationToolResult Result, IReadOnlyList<KnownVoice> Shown);

    /// <summary>Runs one call of the set on <paramref name="roster"/>. <paramref name="yours"/> says whether a voice is the
    /// signed-in user's.</summary>
    public static Answer Run(TextToolCall call, VoiceRoster roster, Func<KnownVoice, bool> yours, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(yours);
        var live = roster.Live;
        switch (call.Name)
        {
            case List:
            {
                var query = CheckInToolSets.Argument(call, "query");
                var found = query is null ? live : live.Where(v => Matches(v, query)).ToArray();
                var shown = found.Take(MaximumListed).ToArray();
                var text = new StringBuilder($"People: {live.Count} voice(s) saved, {found.Count} " +
                    (query is null ? "listed" : "match") + (found.Count > shown.Length ? $", the {shown.Length} heard most recently shown" : "") + ".\n");
                if (live.Count == 0) text.Append("Martlet doesn't know anyone by voice yet.\n");
                else if (shown.Length == 0) text.Append("Nobody saved goes by that name or tag.\n");
                foreach (var voice in shown) text.Append(Describe(voice, yours(voice), now)).Append('\n');
                return new(new(text.ToString().TrimEnd()), shown);
            }
            case Lately:
            {
                var minutes = Minutes(call);
                var since = now - TimeSpan.FromMinutes(minutes);
                var heard = live.Where(v => v.LastHeardAt >= since).ToArray();
                var shown = heard.Take(MaximumListed).ToArray();
                var text = new StringBuilder($"People: {heard.Length} voice(s) heard in the last {Span(TimeSpan.FromMinutes(minutes))}" +
                    (heard.Length > shown.Length ? $", the {shown.Length} most recent shown" : "") + ".\n");
                if (shown.Length == 0) text.Append("Nobody Martlet knows by voice spoke in that time.\n");
                foreach (var voice in shown) text.Append(Describe(voice, yours(voice), now)).Append('\n');
                return new(new(text.ToString().TrimEnd()), shown);
            }
            default:
                return new(new($"People: no tool called {(call.Name.Length > 64 ? call.Name[..64] : call.Name)}.", true), []);
        }
    }

    /// <summary>"V3: goes by Sam first; also Sammy (typed by the owner), Samuel (learned). The signed-in user. Heard 52 times,
    /// last 5 minutes ago." Names are cleaned of brackets so they can't pass for Martlet's own labels.</summary>
    public static string Describe(KnownVoice voice, bool yours, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(voice);
        var text = new StringBuilder(voice.Tag).Append(": ");
        if (!voice.Named) text.Append("no name yet");
        else
        {
            text.Append("goes by ").Append(Clean(voice.DisplayName)).Append(" first (").Append(Source(voice, voice.DisplayName)).Append(')');
            var others = voice.OtherNames;
            if (others.Count > 0)
                text.Append("; also ").Append(string.Join(", ", others.Select(n => $"{Clean(n)} ({Source(voice, n)})")));
        }
        text.Append('.');
        if (yours) text.Append(" The signed-in user.");
        else if (voice.Account is not null) text.Append(" Linked to someone's Martlet account.");
        else if (voice.Owner) text.Append(" The household owner.");
        text.Append(" Heard ").Append(voice.Heard.ToString(CultureInfo.InvariantCulture)).Append(voice.Heard == 1 ? " time" : " times")
            .Append(", last ").Append(Ago(now - voice.LastHeardAt)).Append('.');
        return text.ToString();
    }

    private static string Source(KnownVoice voice, string name) =>
        string.Equals(voice.Name, name, StringComparison.OrdinalIgnoreCase) ||
        voice.Names.Any(n => n.Source == VoiceNameSource.User && string.Equals(n.Text, name, StringComparison.OrdinalIgnoreCase))
            ? "typed by the owner"
            : voice.Names.FirstOrDefault(n => string.Equals(n.Text, name, StringComparison.OrdinalIgnoreCase)) is { } learned
                ? learned.Uses == 1 ? "learned" : $"learned, used {learned.Uses.ToString(CultureInfo.InvariantCulture)} times"
                : "learned";

    private static bool Matches(KnownVoice voice, string query)
    {
        var wanted = query.Trim();
        if (string.Equals(wanted, voice.Tag, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(wanted, "voice " + voice.Number.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase))
            return true;
        return new[] { voice.Name }.Concat(voice.Names.Select(n => n.Text)).OfType<string>()
            .Any(name => name.Contains(wanted, StringComparison.OrdinalIgnoreCase));
    }

    private static int Minutes(TextToolCall call)
    {
        try
        {
            if (JsonNode.Parse(call.ArgumentsJson) is JsonObject arguments && arguments["minutes"] is JsonValue value)
            {
                if (value.TryGetValue<int>(out var whole)) return Math.Clamp(whole, 1, MaximumLatelyMinutes);
                if (value.TryGetValue<double>(out var number) && double.IsFinite(number)) return (int)Math.Clamp(Math.Round(number), 1, MaximumLatelyMinutes);
            }
        }
        catch (JsonException) { }
        return DefaultLatelyMinutes;
    }

    private static string Clean(string name) => name.Replace("[", "(", StringComparison.Ordinal).Replace("]", ")", StringComparison.Ordinal);

    private static string Ago(TimeSpan span) => span < TimeSpan.FromMinutes(1) ? "just now" : Span(span) + " ago";

    private static string Span(TimeSpan span) => span.TotalMinutes < 120 ? Count((int)Math.Max(1, span.TotalMinutes), "minute")
        : span.TotalHours < 48 ? Count((int)span.TotalHours, "hour") : Count((int)span.TotalDays, "day");

    private static string Count(int value, string unit) => value.ToString(CultureInfo.InvariantCulture) + " " + unit + (value == 1 ? "" : "s");
}
