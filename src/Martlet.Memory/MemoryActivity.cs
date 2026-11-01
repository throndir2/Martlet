using Martlet.Core.Activities;

namespace Martlet.Memory;

/// <summary>Where a fact matters (<see cref="MemoryFact.Activity"/>): everywhere (null), during one kind of activity ("gaming",
/// any game) or during one activity ("gaming/Palworld", that game only). An activity is its kind (lowercase ASCII letters), then
/// optionally '/' and its name (the game's name as Martlet saw it; it may hold a '/' of its own). A fact made while the user
/// played Palworld is recalled while they play Palworld, not in other games; a fact about the user's life is everywhere. Names
/// compare without regard to case or spacing. The rules are <see cref="ActivityScope"/>'s, shared with the user's preferences;
/// this class adds memory's own checks.</summary>
public static class MemoryActivity
{
    /// <summary>The kind for games: "gaming" alone is any game, "gaming/&lt;name&gt;" one game.</summary>
    public const string Gaming = ActivityScope.Gaming;
    public const int MaximumCharacters = ActivityScope.MaximumCharacters;
    public const int MaximumKindCharacters = ActivityScope.MaximumKindCharacters;

    /// <summary>"gaming/&lt;name&gt;" for the game <paramref name="name"/> (spaces folded, control characters left out, at most
    /// <see cref="MaximumCharacters"/> characters in all), or null when the name has no letter or digit.</summary>
    public static string? Game(string? name) => Of(Gaming, name);

    /// <summary>"&lt;kind&gt;/&lt;name&gt;", or null when the name has no letter or digit.</summary>
    public static string? Of(string kind, string? name)
    {
        ValidateKind(kind);
        return ActivityScope.Of(kind, name);
    }

    /// <summary>The kind of <paramref name="activity"/> ("gaming"), or null for everywhere.</summary>
    public static string? Kind(string? activity) => ActivityScope.Kind(activity);

    /// <summary>The name in <paramref name="activity"/> ("Palworld"), or null for everywhere or a kind alone.</summary>
    public static string? Name(string? activity) => ActivityScope.Name(activity);

    /// <summary>Whether a fact kept for <paramref name="fact"/> applies while the user does <paramref name="now"/>: a fact for
    /// everywhere always does; one for a kind ("gaming") during any activity of that kind; one for an activity only during it.
    /// Nothing but everywhere applies while no activity is known.</summary>
    public static bool Applies(string? fact, string? now) => ActivityScope.Applies(fact, now);

    /// <summary>Whether two activities are the same (case and spacing aside).</summary>
    public static bool Same(string? left, string? right) => ActivityScope.Same(left, right);

    /// <summary>Whether <paramref name="said"/> names the activity of a fact (its name's words in order, such as "palworld" in
    /// "what's my favorite Palworld pal?"), so asking about a game recalls its facts while the user does something else.</summary>
    public static bool Named(string? fact, string? said) => ActivityScope.Named(fact, said);

    /// <summary>"everywhere", "while gaming" or "while playing Palworld": how the Memory window and the reply's tools say it.</summary>
    public static string Describe(string? activity) => ActivityScope.Describe(activity);

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
