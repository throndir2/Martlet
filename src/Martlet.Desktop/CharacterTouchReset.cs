using System.Globalization;
using Martlet.Avatar.Hosting;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>What Touch zones' Reset works on: the shown model's zones (<see cref="Zones"/> follows only that model), the active
/// persona (null when there is none) and its temperaments, what a fresh zone's reaction is (<see cref="Fresh"/>), and
/// <see cref="Redecide"/>, which has the persona's temperament decided again from its personality in the background.</summary>
internal sealed record TouchResetTarget(CharacterTouchZoneService Zones, CharacterTemperamentService Temperaments, PersonaProfile? Persona,
    Func<CharacterTouchZone, CharacterTouchReaction> Fresh, Action<PersonaProfile> Redecide);

/// <summary>One level of Touch zones' Reset: its ID (for the log), its label in the list, what it clears (the note under the
/// list), what the owner loses now (the confirmation's lines; none: nothing to reset), what happens after it (or null), and the
/// reset itself (returns why it failed, or null). <see cref="Covers"/> names the levels whose losses this one already includes,
/// so Everything doesn't run or list them twice.</summary>
internal sealed record TouchResetLevel(string Id, string Label, string Clears,
    Func<TouchResetTarget, IReadOnlyList<string>> Loses,
    Func<TouchResetTarget, string?> After,
    Func<TouchResetTarget, CancellationToken, Task<string?>> Run,
    IReadOnlyList<string>? Covers = null);

/// <summary>Companion › Touch › Touch zones' Reset: the character's touch setup back to how a fresh character starts, at one of
/// <see cref="Levels"/>, for the shown model and the active persona only. Add a level to <see cref="Levels"/> (before
/// Everything) and Everything includes it.</summary>
internal static class CharacterTouchReset
{
    internal const string ReactionsId = "reactions", ZonesId = "zones", TemperamentId = "temperament", EverythingId = "everything";
    private const int MostNames = 6;

    /// <summary>Every level, in the order the list shows them; Everything last.</summary>
    internal static readonly IReadOnlyList<TouchResetLevel> Levels =
    [
        new(ReactionsId, "Zone reactions",
            "What each zone does when you touch it goes back to what a fresh zone gets: its reactions, its rest, Martlet notices and " +
            "your own words. The zones, their boxes and names stay.",
            ReactionLosses, _ => "Each zone reacts as a fresh zone does.",
            (target, token) => target.Zones.ResetReactionsAsync(target.Fresh, token)),
        new(ZonesId, "Zones",
            "Removes this model's zones, found and added, with their boxes, names and reactions, and the pictures they were found " +
            "in. Martlet then places a first guess again, as for a new character.",
            ZoneLosses, _ => "Martlet places a first guess at this model's zones, with no AI and nothing sent. Detect zones has " +
                "your Thinking model find them again.",
            (target, token) => target.Zones.ForgetAsync(token), Covers: [ReactionsId]),
        new(TemperamentId, "Touch temperament",
            "Forgets the active persona's touch temperament (decided from its personality or changed by you) and any temperament it " +
            "uses instead. Martlet then decides it again from the personality.",
            TemperamentLosses, TemperamentAfter, ForgetTemperamentAsync),
        new(EverythingId, "Everything",
            "All of the above: this model's zones and the active persona's touch temperament start again as a fresh character's.",
            target => [.. Parts().SelectMany(level => level.Loses(target))],
            target => Parts().Where(level => level.Loses(target).Count > 0).Select(level => level.After(target)).OfType<string>()
                .Aggregate((string?)null, (all, next) => all is null ? next : all + " " + next),
            RunAllAsync)
    ];

    internal static TouchResetLevel Level(string? id) => Levels.FirstOrDefault(l => l.Id == id) ?? Levels[0];

    /// <summary>The levels Everything runs: every other level that no other one already covers.</summary>
    internal static IReadOnlyList<TouchResetLevel> Parts() =>
        [.. Levels.Where(l => l.Id != EverythingId && !Levels.Any(other => other.Id != EverythingId && other.Covers?.Contains(l.Id) == true))];

    /// <summary>The confirmation's question: what the owner loses at <paramref name="level"/> (<paramref name="loses"/>) and what
    /// happens after.</summary>
    internal static string Question(TouchResetLevel level, TouchResetTarget target, IReadOnlyList<string> loses) =>
        $"Reset {level.Label.ToLowerInvariant()} for this character?\n\nYou lose:\n" +
        string.Join("\n", loses.Select(line => "\u2022 " + char.ToUpperInvariant(line[0]) + line[1..] + ".")) +
        (level.After(target) is { } after ? "\n\nAfterwards: " + after : "") +
        "\n\nOther models and personas keep theirs. You can't undo this.";

    /// <summary>"3 zones: Hair, Left cheek and Tail" (at most six names, then how many more); <paramref name="what"/> goes after "zones".</summary>
    internal static string Zones(IReadOnlyList<CharacterTouchZone> zones, string what = "")
    {
        var names = zones.Take(MostNames).Select(z => z.Name).ToList();
        if (zones.Count > MostNames) names.Add($"{zones.Count - MostNames} more");
        var list = names.Count == 1 ? names[0] : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];
        return $"{zones.Count} zone{(zones.Count == 1 ? "" : "s")}{what}: {list}";
    }

    private static IReadOnlyList<string> ReactionLosses(TouchResetTarget target)
    {
        if (target.Zones.Current is not { Zones.Count: > 0 } settings) return [];
        var lines = new List<string>();
        void Add(Func<CharacterTouchZone, CharacterTouchReaction, bool> lost, string what)
        {
            var zones = settings.Zones.Where(z => lost(z, target.Fresh(z))).ToArray();
            if (zones.Length > 0) lines.Add($"{what} for {Zones(zones)}");
        }
        Add((zone, fresh) => !CharacterTouchZones.FreshActions(zone, fresh), "the reactions you chose");
        Add((zone, fresh) => zone.Reaction.Notices != fresh.Notices && !zone.Reaction.Notices, "Martlet notices turned off");
        Add((zone, fresh) => zone.Reaction.Notices != fresh.Notices && zone.Reaction.Notices, "Martlet notices turned on");
        Add((zone, _) => CharacterTouchZones.OwnWords(zone), "your own words");
        Add((zone, fresh) => !zone.Reaction.CooldownSeconds.Equals(fresh.CooldownSeconds), "the rest you changed");
        return lines;
    }

    private static IReadOnlyList<string> ZoneLosses(TouchResetTarget target)
    {
        var lines = new List<string>();
        if (target.Zones.Current is { Zones.Count: > 0 } settings)
        {
            var who = settings.DetectedBy switch
            {
                CharacterTouchZoneSettings.ByVision => "found by your Thinking model",
                CharacterTouchZoneSettings.ByEstimate => "Martlet's first guess",
                _ => "made by you"
            };
            var added = settings.Zones.Where(z => z.Added).ToArray();
            lines.Add($"this model's {settings.Zones.Count} zone{(settings.Zones.Count == 1 ? "" : "s")} ({who}), with their boxes, names, " +
                "on and off choices and reactions");
            if (added.Length > 0) lines.Add($"the {Zones(added, " you added")} (Detect zones no longer looks for {(added.Length == 1 ? "it" : "them")})");
        }
        if (target.Zones.Current is { IncludeIntimate: false }) lines.Add("your choice to leave out intimate zones");
        if (target.Zones.SnapshotPath is not null) lines.Add("the picture of the character the zones were found in");
        if (target.Zones.SentFolder is not null) lines.Add("the pictures the last Detect zones sent to your Thinking model");
        return lines;
    }

    private static IReadOnlyList<string> TemperamentLosses(TouchResetTarget target)
    {
        if (target.Persona is not { } persona) return [];
        var saved = target.Temperaments.Saved;
        var lines = new List<string>();
        if (saved.Own(persona.Id) is { } own)
            lines.Add($"{persona.Name}'s own touch temperament (" + own.Source switch
            {
                CharacterTouchTemperament.ByOwner => "with your own changes",
                CharacterTouchTemperament.ByFixture => "FIXTURE - NOT AI",
                _ => "decided by your Thinking model" + (own.DecidedAt is { } at ? " on " + at.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) : "")
            } + ")");
        if (saved.UsesBuiltIn(persona.Id)) lines.Add($"{persona.Name}'s choice to use the built-in reactions");
        else if (saved.CustomOf(persona.Id) is { } custom)
            lines.Add($"{persona.Name}'s choice to use your custom temperament \"{custom.Name}\" (the custom temperament itself stays)");
        return lines;
    }

    private static string? TemperamentAfter(TouchResetTarget target) => target.Persona is not { } persona ? null
        : string.IsNullOrWhiteSpace(persona.Text)
            ? $"{persona.Name} has no personality text, so it uses the built-in reactions. Your other Martlet computers get this change too."
            : $"Martlet decides how {persona.Name} reacts to touch again from its personality, in the background when it isn't replying. " +
              "Your other Martlet computers get this change too.";

    private static async Task<string?> ForgetTemperamentAsync(TouchResetTarget target, CancellationToken token)
    {
        if (target.Persona is not { } persona) return "No persona is active.";
        if (target.Temperaments.Busy) return "Martlet is deciding a touch temperament now. Wait until it is done.";
        var why = await target.Temperaments.ForgetAsync(persona.Id, token);
        if (why is null && !string.IsNullOrWhiteSpace(persona.Text)) target.Redecide(persona);
        return why;
    }

    // Everything: each part with something to lose, in order; the reasons any of them failed, or null.
    private static async Task<string?> RunAllAsync(TouchResetTarget target, CancellationToken token)
    {
        var failed = new List<string>();
        foreach (var level in Parts().Where(level => level.Loses(target).Count > 0))
            if (await level.Run(target, token) is { } why) failed.Add($"{level.Label}: {why}");
        return failed.Count == 0 ? null : string.Join(" ", failed);
    }
}
