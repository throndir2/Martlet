using System.Text.RegularExpressions;

namespace Martlet.Core.Speakers;

/// <summary>What Learning names may change about the voices in an exchange: a name a voice goes by (NAME), the name it asks to
/// be called from now on (CALL), a learned name that isn't its own (NOT), or another voice that is the same person (SAME).</summary>
public enum VoiceUpdateKind { Name, Call, Not, Same }

/// <summary>One change the Thinking model's answer asks for. <see cref="SameAsId"/> is the other voice of a SAME line;
/// <see cref="Line"/> is the answer's line (from 1).</summary>
public sealed record VoiceUpdate(VoiceUpdateKind Kind, string VoiceId, string? Name = null, string? SameAsId = null, int Line = 0);

/// <summary>Why a line of the answer, or a change it asked for, was not used. The reason never repeats the name, so it can go
/// in logs.</summary>
public sealed record VoiceUpdateRefusal(int Line, string Reason);

public sealed record VoiceUpdateAnswer(IReadOnlyList<VoiceUpdate> Updates, IReadOnlyList<VoiceUpdateRefusal> Refused);

/// <summary>A change made to the voice list: the voice as it is now (the kept one after a merge) and what the talk window says.</summary>
public sealed record VoiceUpdateResult(VoiceUpdateKind Kind, KnownVoice Voice, string? Name, string Text);

/// <summary>The names the companion itself goes by: "Martlet", each persona's name and every word of it, names a persona's
/// instructions give it ("You are Jane") and, for one exchange, names its own reply gives it ("I'm Jane"). The people talking
/// to it are not called that, so a voice never learns one of these from conversation; the owner can still type one on People.</summary>
public sealed partial class CompanionNames
{
    public const string Default = "Martlet";
    private readonly HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> words = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> listed = [];

    private CompanionNames() => Add(Default);

    /// <summary>Only "Martlet".</summary>
    public static CompanionNames Martlet { get; } = new();

    /// <summary>The companion's full names, "Martlet" first.</summary>
    public IReadOnlyList<string> Names => listed;

    public static CompanionNames From(IEnumerable<string?>? personaNames, IEnumerable<string?>? personaTexts = null)
    {
        var result = new CompanionNames();
        foreach (var name in personaNames ?? []) result.Add(name);
        foreach (var text in personaTexts ?? []) result.AddCalled(text, SecondPerson());
        return result;
    }

    /// <summary>These names plus the ones Martlet's reply gives itself ("I'm Jane", "my name is Jane", "call me Jane").</summary>
    public CompanionNames WithReply(string? reply)
    {
        var copy = new CompanionNames();
        foreach (var name in listed) copy.Add(name);
        copy.words.UnionWith(words);
        copy.AddCalled(reply, FirstPerson());
        return copy;
    }

    /// <summary>Whether <paramref name="name"/> is one of the companion's names: a full name, or made only of words of them
    /// ("Jane" or "Doe" for "Jane Doe").</summary>
    public bool Matches(string? name)
    {
        var parts = Words(name);
        return parts.Length > 0 && (names.Contains(string.Join(' ', parts)) || parts.All(words.Contains));
    }

    private void Add(string? name)
    {
        var parts = Words(name);
        if (parts.Length == 0) return;
        var full = string.Join(' ', parts);
        if (full.Length <= VoiceRoster.MaximumNameLength && names.Add(full)) listed.Add(full);
        words.UnionWith(parts);
    }

    private void AddCalled(string? text, Regex pattern)
    {
        if (string.IsNullOrEmpty(text)) return;
        foreach (Match match in pattern.Matches(text.Length > 65_536 ? text[..65_536] : text))
            Add(match.Groups["name"].Value);
    }

    private static string[] Words(string? text) => string.IsNullOrWhiteSpace(text) ? []
        : WordPattern().Matches(text).Select(m => m.Value.Trim('\'', '\u2019', '-', '.')).Where(w => w.Length >= 2 && char.IsLetter(w[0])).ToArray();

    [GeneratedRegex(@"[\p{L}\p{M}\p{N}'\u2019\-.]+", RegexOptions.CultureInvariant)]
    private static partial Regex WordPattern();

    // A persona's instructions naming the companion: "You are Jane, ...", "Your name is Jane", "You go by Jane".
    [GeneratedRegex(@"(?i:\b(?:you\s+are\s+called|you\s+are\s+named|you(?:'|\u2019)re\s+called|you(?:'|\u2019)re\s+named|you\s+are|" +
        @"you(?:'|\u2019)re|your\s+name\s+is|your\s+name(?:'|\u2019)s|you\s+go\s+by|call\s+yourself))\s+" +
        @"(?<name>\p{Lu}[\p{L}\p{M}'\u2019\-]*(?:\s+\p{Lu}[\p{L}\p{M}'\u2019\-]*)?)", RegexOptions.CultureInvariant)]
    private static partial Regex SecondPerson();

    // Martlet's reply naming itself: "I'm Jane", "I am Jane", "My name is Jane", "Call me Jane".
    [GeneratedRegex(@"(?i:\b(?:I(?:'|\u2019)m|I\s+am|my\s+name\s+is|my\s+name(?:'|\u2019)s|call\s+me))\s+" +
        @"(?<name>\p{Lu}[\p{L}\p{M}'\u2019\-]*(?:\s+\p{Lu}[\p{L}\p{M}'\u2019\-]*)?)", RegexOptions.CultureInvariant)]
    private static partial Regex FirstPerson();

    public override string ToString() => $"{nameof(CompanionNames)} ({listed.Count})";
}

/// <summary>Learning names from what is said, after a reply: the Thinking model answers in a tiny line format and Martlet
/// validates each line before changing the voice list. Only voices heard in the exchange get names; the companion's own names
/// are never a voice's; a learned name can be dropped but never one the owner typed; at most one merge per exchange, and never
/// of two voices the owner named differently.</summary>
public static partial class VoiceUpdates
{
    public const string Nothing = "NOTHING";
    /// <summary>The most changes one answer may make.</summary>
    public const int MaximumUpdates = 6;
    private const int MaximumLines = 64;

    private static readonly HashSet<string> NotNames = new(StringComparer.OrdinalIgnoreCase)
    {
        CompanionNames.Default, Nothing, "User", "Unknown", "Nobody", "Someone", "Me", "You", "Myself", "Yourself", "Assistant",
        "Companion", "Speaker", "Person", "None", "No name"
    };

    /// <summary>The changes in the model's answer. <paramref name="voices"/> maps the listed tags (V3) to voice IDs;
    /// <paramref name="heard"/> holds the IDs of the voices heard in the exchange, the only ones that may get or lose names.</summary>
    public static VoiceUpdateAnswer Parse(string? answer, IReadOnlyDictionary<string, string> voices, IReadOnlyCollection<string> heard,
        CompanionNames companion)
    {
        ArgumentNullException.ThrowIfNull(voices);
        ArgumentNullException.ThrowIfNull(heard);
        ArgumentNullException.ThrowIfNull(companion);
        var updates = new List<VoiceUpdate>();
        var refused = new List<VoiceUpdateRefusal>();
        if (string.IsNullOrWhiteSpace(answer)) return new(updates, refused);
        var lines = answer.Split('\n');
        for (var index = 0; index < lines.Length && index < MaximumLines; index++)
        {
            var number = index + 1;
            var line = lines[index].Replace("**", "", StringComparison.Ordinal).Replace("`", "", StringComparison.Ordinal)
                .Trim().TrimStart('-', '*', '\u2022', '>', ' ', '\t').Trim();
            var match = UpdateLine().Match(line);
            if (!match.Success) continue;
            if (updates.Count == MaximumUpdates)
            {
                refused.Add(new(number, "too many changes in one answer"));
                continue;
            }
            if (!voices.TryGetValue("V" + match.Groups["n"].Value, out var id))
            {
                refused.Add(new(number, "not a listed voice"));
                continue;
            }
            var kind = match.Groups["kind"].Value.ToUpperInvariant() switch
            {
                "NAME" => VoiceUpdateKind.Name,
                "CALL" => VoiceUpdateKind.Call,
                "NOT" => VoiceUpdateKind.Not,
                _ => VoiceUpdateKind.Same
            };
            var value = match.Groups["value"].Value.Trim();
            if (kind == VoiceUpdateKind.Same)
            {
                var other = SameTarget().Match(value);
                if (!other.Success || !voices.TryGetValue("V" + other.Groups["n"].Value, out var otherId))
                    refused.Add(new(number, "not a listed voice"));
                else if (otherId == id) refused.Add(new(number, "the same voice twice"));
                else if (!heard.Contains(id) && !heard.Contains(otherId)) refused.Add(new(number, "neither voice was heard in this message"));
                else if (updates.Any(u => u.Kind == VoiceUpdateKind.Same)) refused.Add(new(number, "only one merge per exchange"));
                else updates.Add(new(kind, id, SameAsId: otherId, Line: number));
                continue;
            }
            if (!heard.Contains(id))
            {
                refused.Add(new(number, "the voice wasn't heard in this message"));
                continue;
            }
            if (VoiceRoster.CleanName(value) is not { } name || name.Split(' ').Length > 3 || VoiceTag().IsMatch(name) || NotNames.Contains(name))
            {
                refused.Add(new(number, "not a name"));
                continue;
            }
            if (kind != VoiceUpdateKind.Not && companion.Matches(name))
            {
                refused.Add(new(number, "the companion's own name"));
                continue;
            }
            if (!updates.Any(u => u.Kind == kind && u.VoiceId == id && string.Equals(u.Name, name, StringComparison.OrdinalIgnoreCase)))
                updates.Add(new(kind, id, name, Line: number));
        }
        return new(updates, refused);
    }

    /// <summary>Makes the changes, in order. NAME adds a name the voice goes by; CALL also makes it the learned name shown (a
    /// name the owner typed still wins); NOT drops a learned name, never one the owner typed; SAME merges the two voices into
    /// the owner's, else a named one, else the one heard most.</summary>
    public static (VoiceRoster Roster, IReadOnlyList<VoiceUpdateResult> Applied, IReadOnlyList<VoiceUpdateRefusal> Refused) Apply(
        VoiceRoster roster, IEnumerable<VoiceUpdate> updates, string by, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(updates);
        var applied = new List<VoiceUpdateResult>();
        var refused = new List<VoiceUpdateRefusal>();
        foreach (var update in updates)
        {
            if (roster.Resolve(update.VoiceId) is not { } voice)
            {
                refused.Add(new(update.Line, "the voice was forgotten"));
                continue;
            }
            switch (update.Kind)
            {
                case VoiceUpdateKind.Name or VoiceUpdateKind.Call when update.Name is { } name:
                {
                    var next = roster.AddHeardName(voice.Id, name, by, now, prefer: update.Kind == VoiceUpdateKind.Call);
                    if (ReferenceEquals(next, roster)) continue;
                    roster = next;
                    var named = roster.Resolve(voice.Id)!;
                    applied.Add(new(update.Kind, named, name, update.Kind == VoiceUpdateKind.Call
                        ? $"Learned {Voice(voice)} likes to be called {name}."
                        : voice.Named ? $"Learned {Voice(voice)} also goes by {name}." : $"Learned {Voice(voice)} is {name}."));
                    break;
                }
                case VoiceUpdateKind.Not when update.Name is { } name:
                {
                    if (string.Equals(voice.Name, name, StringComparison.OrdinalIgnoreCase) ||
                        voice.Names.Any(n => n.Source == VoiceNameSource.User && string.Equals(n.Text, name, StringComparison.OrdinalIgnoreCase)))
                    {
                        refused.Add(new(update.Line, "a name the owner typed"));
                        continue;
                    }
                    var next = roster.DropHeardNames(voice.Id, text => string.Equals(text, name, StringComparison.OrdinalIgnoreCase), by, now);
                    if (ReferenceEquals(next, roster)) continue;
                    roster = next;
                    var renamed = roster.Resolve(voice.Id)!;
                    applied.Add(new(update.Kind, renamed, name, $"Learned voice {voice.Number} doesn't go by {name}."));
                    break;
                }
                case VoiceUpdateKind.Same when update.SameAsId is { } otherId:
                {
                    if (roster.Resolve(otherId) is not { } other)
                    {
                        refused.Add(new(update.Line, "the voice was forgotten"));
                        continue;
                    }
                    if (other.Id == voice.Id) continue;
                    if (voice.Name is { } typed && other.Name is { } otherTyped && !string.Equals(typed, otherTyped, StringComparison.OrdinalIgnoreCase))
                    {
                        refused.Add(new(update.Line, "the owner named them differently"));
                        continue;
                    }
                    var keep = new[] { voice, other }.OrderByDescending(v => v.Owner).ThenByDescending(v => v.Name is not null)
                        .ThenByDescending(v => v.Named).ThenByDescending(v => v.Heard).ThenBy(v => v.Number).First();
                    var away = keep.Id == voice.Id ? other : voice;
                    roster = roster.Join(away.Id, keep.Id, by, now);
                    var kept = roster.Resolve(keep.Id)!;
                    applied.Add(new(update.Kind, kept, null, $"Learned {Voice(away)} is the same person as {Voice(kept)}, and merged them."));
                    break;
                }
            }
        }
        return (roster, applied, refused);
    }

    /// <summary>"voice 3" for a voice with no name yet, else "Sam (voice 3)".</summary>
    public static string Voice(KnownVoice voice) =>
        voice.Named ? $"{voice.DisplayName} (voice {voice.Number})" : $"voice {voice.Number}";

    [GeneratedRegex(@"^(?<kind>NAME|CALL|NOT|SAME)\s*V(?<n>\d{1,7})\s*[:=\-\u2013]\s*(?<value>.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UpdateLine();

    [GeneratedRegex(@"^(?:voice\s*)?V?(?<n>\d{1,7})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SameTarget();

    [GeneratedRegex(@"^(voice|v)\s*\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VoiceTag();
}
