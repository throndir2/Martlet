using System.Text;

namespace Martlet.Memory;

/// <summary>Where a fact matters (<see cref="MemoryFact.Activity"/>): everywhere (null), during one kind of activity ("gaming",
/// any game) or during one activity ("gaming/Palworld", that game only). An activity is its kind (lowercase ASCII letters), then
/// optionally '/' and its name (the game's name as Martlet saw it; it may hold a '/' of its own). A fact made while the user
/// played Palworld is recalled while they play Palworld, not in other games; a fact about the user's life is everywhere. Names
/// compare without regard to case or spacing.</summary>
public static class MemoryActivity
{
    /// <summary>The kind for games: "gaming" alone is any game, "gaming/&lt;name&gt;" one game.</summary>
    public const string Gaming = "gaming";
    public const int MaximumCharacters = 128;
    public const int MaximumKindCharacters = 24;

    /// <summary>"gaming/&lt;name&gt;" for the game <paramref name="name"/> (spaces folded, control characters left out, at most
    /// <see cref="MaximumCharacters"/> characters in all), or null when the name has no letter or digit.</summary>
    public static string? Game(string? name) => Of(Gaming, name);

    /// <summary>"&lt;kind&gt;/&lt;name&gt;", or null when the name has no letter or digit.</summary>
    public static string? Of(string kind, string? name)
    {
        ValidateKind(kind);
        if (name is null) return null;
        var text = new StringBuilder();
        foreach (var character in name)
        {
            if (char.IsControl(character) || char.IsWhiteSpace(character))
            {
                if (text.Length > 0 && text[^1] != ' ') text.Append(' ');
                continue;
            }
            text.Append(character);
        }
        var clean = text.ToString().Trim();
        var room = MaximumCharacters - kind.Length - 1;
        if (clean.Length > room)
        {
            clean = clean[..room];
            if (char.IsHighSurrogate(clean[^1])) clean = clean[..^1];
            clean = clean.TrimEnd();
        }
        return clean.Any(char.IsLetterOrDigit) ? kind + "/" + clean : null;
    }

    /// <summary>The kind of <paramref name="activity"/> ("gaming"), or null for everywhere.</summary>
    public static string? Kind(string? activity) =>
        activity is null ? null : activity.IndexOf('/') is var slash and >= 0 ? activity[..slash] : activity;

    /// <summary>The name in <paramref name="activity"/> ("Palworld"), or null for everywhere or a kind alone.</summary>
    public static string? Name(string? activity) =>
        activity is not null && activity.IndexOf('/') is var slash and >= 0 ? activity[(slash + 1)..] : null;

    /// <summary>Whether a fact kept for <paramref name="fact"/> applies while the user does <paramref name="now"/>: a fact for
    /// everywhere always does; one for a kind ("gaming") during any activity of that kind; one for an activity only during it.
    /// Nothing but everywhere applies while no activity is known.</summary>
    public static bool Applies(string? fact, string? now)
    {
        if (fact is null) return true;
        if (now is null) return false;
        if (Same(fact, now)) return true;
        return Name(fact) is null && string.Equals(Kind(fact), Kind(now), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether two activities are the same (case and spacing aside).</summary>
    public static bool Same(string? left, string? right) =>
        left is null || right is null ? left == right
            : string.Equals(Kind(left), Kind(right), StringComparison.OrdinalIgnoreCase) &&
              string.Equals(Key(Name(left)), Key(Name(right)), StringComparison.Ordinal);

    /// <summary>Whether <paramref name="said"/> names the activity of a fact (its name's words in order, such as "palworld" in
    /// "what's my favorite Palworld pal?"), so asking about a game recalls its facts while the user does something else.</summary>
    public static bool Named(string? fact, string? said)
    {
        if (Name(fact) is not { } name || string.IsNullOrWhiteSpace(said)) return false;
        var wanted = Words(name);
        if (wanted.Count == 0) return false;
        var words = Words(said);
        for (var start = 0; start + wanted.Count <= words.Count; start++)
        {
            var match = true;
            for (var i = 0; i < wanted.Count && match; i++)
                match = words[start + i] == wanted[i];
            if (match) return true;
        }
        // "eldenring" or "Elden-Ring" for "ELDEN RING".
        var joined = string.Concat(wanted);
        return joined.Length >= 4 && words.Contains(joined, StringComparer.Ordinal);
    }

    /// <summary>"everywhere", "while gaming" or "while playing Palworld": how the Memory window and the reply's tools say it.</summary>
    public static string Describe(string? activity) => activity is null ? "everywhere"
        : (Kind(activity), Name(activity)) switch
        {
            (Gaming, null) => "while gaming",
            (Gaming, { } game) => "while playing " + game,
            ({ } kind, null) => "while " + kind,
            (var kind, var name) => $"while {kind}: {name}"
        };

    /// <summary>Null, or a kind (1-24 lowercase ASCII letters), optionally followed by '/' and a name: one line with a letter or
    /// digit, no leading or trailing space, at most <see cref="MaximumCharacters"/> characters in all.</summary>
    internal static void Validate(string? activity)
    {
        if (activity is null) return;
        MemoryGuard.Require(activity.Length is > 0 and <= MaximumCharacters && !activity.Any(char.IsControl));
        ValidateKind(Kind(activity)!);
        if (Name(activity) is { } name)
            MemoryGuard.Require(name.Length > 0 && name.Any(char.IsLetterOrDigit) && !char.IsWhiteSpace(name[0]) &&
                !char.IsWhiteSpace(name[^1]) && !char.IsSurrogate(name[^1]));
    }

    private static void ValidateKind(string kind) =>
        MemoryGuard.Require(kind is { Length: > 0 and <= MaximumKindCharacters } && kind.All(character => character is >= 'a' and <= 'z'));

    private static string? Key(string? name) => name is null ? null : string.Join(' ', Words(name));

    private static List<string> Words(string text)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        foreach (var character in text + " ")
        {
            if (char.IsLetterOrDigit(character)) word.Append(char.ToLowerInvariant(character));
            else if (word.Length > 0)
            {
                words.Add(word.ToString());
                word.Clear();
            }
        }
        return words;
    }
}

/// <summary>Which facts a search may find, by where they matter (<see cref="MemoryQuery.Activity"/>): those for everywhere, those
/// that apply while the user does <paramref name="Now"/> (null: no activity known) and those whose activity the words name.</summary>
public sealed record MemoryActivityFilter(string? Now)
{
    public bool Allows(MemoryFact fact, string? said = null)
    {
        ArgumentNullException.ThrowIfNull(fact);
        return MemoryActivity.Applies(fact.Activity, Now) || MemoryActivity.Named(fact.Activity, said);
    }
}
