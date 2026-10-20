using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Core.Contracts;

namespace Martlet.Avatar.Hosting;

/// <summary>Reaction planning: what a touched zone plays (its own list, the temperament's defaults or the built-in plan) and what Martlet notices.</summary>
public static partial class CharacterTouchZones
{
    // ---------- reactions ----------

    /// <summary>What touching <paramref name="zone"/> plays on the model: its reaction list, or for a zone not filled yet its
    /// <see cref="DefaultReactions"/> with no temperament. Only emotes and motions in use.</summary>
    public static IReadOnlyList<CharacterActionSource> Plan(CharacterTouchZone zone, CharacterActionCatalog? catalog) => React(zone, catalog, null, 0).Actions;

    /// <summary>What a touch on <paramref name="zone"/> plays: its reaction list (<see cref="CharacterTouchReaction.Actions"/>, as
    /// the model has it; a zone not filled yet plays its <see cref="DefaultReactions"/>) and its voice sounds. The persona's
    /// <paramref name="temperament"/> for the zone kind or its group adds how it feels about the touch, how long the first
    /// reaction lingers, how long the eyes then turn to the mouse pointer, and after <paramref name="repeats"/> touches in a
    /// row of a disliked or loved zone, its escalation first.</summary>
    public static TouchReactionPlan React(CharacterTouchZone zone, CharacterActionCatalog? catalog, CharacterTouchTemperament? temperament, int repeats)
    {
        var attitude = CharacterTouchTemperaments.Attitude(temperament, zone.Id);
        var entry = CharacterTouchTemperaments.Entry(temperament, zone.Id);
        var list = List(zone, catalog, temperament);
        var from = zone.Reaction.Actions is not null ? TouchReactionPlan.FromOwner
            : entry is not null ? TouchReactionPlan.FromTemperament : TouchReactionPlan.FromDefault;
        var actions = Sources(list, catalog);
        var escalated = false;
        if (temperament is not null && entry is not null && CharacterTouchTemperaments.Escalation(temperament, entry, repeats) is { Count: > 0 } first)
        {
            actions = [.. CharacterTouchTemperaments.Resolve(first, catalog).Concat(actions).Distinct().Take(MaximumActions)];
            escalated = true;
        }
        string[] sounds = [.. list.Where(CharacterTouchReaction.IsSound).Select(s => s[CharacterTouchReaction.SoundPrefix.Length..]).Where(c => c.Length > 0)];
        return new(actions, entry?.LingerSeconds ?? 0, attitude, from, escalated, entry?.LookSeconds ?? 0, sounds);
    }

    /// <summary>The reaction list a new zone gets: with a <paramref name="temperament"/> that covers the zone (its kind, else its
    /// category), its reactions (or its feeling's usual ones) as the model has them; else the model's own tap motion for that part
    /// (a group named like TapHead or TapBody) when it has one, then the zone's built-in emotes and gestures (one from each of its
    /// slots, the first of their names the model has; a zone special to the character reacts as the extras do). Entries are
    /// <see cref="CharacterActionSource.Id"/>s of the model's emotes and motions in use; empty without the model's emotes
    /// (<paramref name="catalog"/> null).</summary>
    public static IReadOnlyList<string> DefaultReactions(CharacterTouchZone zone, CharacterActionCatalog? catalog, CharacterTouchTemperament? temperament)
    {
        if (catalog is null) return [];
        if (temperament is not null && CharacterTouchTemperaments.Entry(temperament, zone.Id) is { } entry)
            return [.. CharacterTouchTemperaments.Resolve(CharacterTouchTemperaments.Words(temperament, entry, 1).Words, catalog).Select(s => s.Id)];
        var plan = DefaultPlan(zone, catalog);
        return TapMotion(zone, catalog) is { } motion ? [motion.Id, .. plan.Where(s => s != motion).Select(s => s.Id)] : [.. plan.Select(s => s.Id)];
    }

    /// <summary>The reaction a new zone gets: its <see cref="DefaultReactions"/>, noticed by Martlet, with no words of the owner's
    /// and the usual rest.</summary>
    public static CharacterTouchReaction FreshReaction(CharacterTouchZone zone, CharacterActionCatalog? catalog, CharacterTouchTemperament? temperament) =>
        new() { Actions = DefaultReactions(zone, catalog, temperament) };

    /// <summary><paramref name="settings"/> with each zone whose reaction list isn't filled yet (null: saved before reaction lists,
    /// or just found) given its <see cref="DefaultReactions"/>, and entries of the old second list joined to its end
    /// (<see cref="List"/>), so the list shows exactly what plays. The same settings when there is nothing to fill or
    /// <paramref name="catalog"/> is another model's.</summary>
    public static CharacterTouchZoneSettings Filled(CharacterTouchZoneSettings settings, CharacterActionCatalog catalog, CharacterTouchTemperament? temperament) =>
        catalog.Inventory.ModelId != settings.ModelId || settings.Zones.All(z => z.Reaction is { Actions: not null, Later: null }) ? settings : settings with
        {
            Zones = [.. settings.Zones.Select(z => z.Reaction is { Actions: not null, Later: null } ? z
                : z with { Reaction = z.Reaction with { Actions = List(z, catalog, temperament), Later = null } })]
        };

    /// <summary>The zone's reaction list as it plays: <see cref="CharacterTouchReaction.Actions"/> (a zone not filled yet: its
    /// <see cref="DefaultReactions"/>), with entries saved in the old second list (autoplay) joined to its end, at most
    /// <see cref="MaximumActions"/>.</summary>
    public static IReadOnlyList<string> List(CharacterTouchZone zone, CharacterActionCatalog? catalog, CharacterTouchTemperament? temperament)
    {
        var list = zone.Reaction.Actions ?? DefaultReactions(zone, catalog, temperament);
        return zone.Reaction.Later is { Count: > 0 } later ? [.. list.Concat(later).Distinct(StringComparer.Ordinal).Take(MaximumActions)] : list;
    }

    /// <summary>Whether a reaction list entry plays on the model: an emote, gesture or motion it has in use, or a voice sound
    /// (<see cref="CharacterTouchReaction.SoundPrefix"/> and a cue).</summary>
    public static bool Resolves(string entry, CharacterActionCatalog? catalog) =>
        CharacterTouchReaction.IsSound(entry) ? entry.Length > CharacterTouchReaction.SoundPrefix.Length
            : catalog?.Entries.Any(e => e.Action.Enabled && e.Source.Id == entry) == true;

    // The emotes, gestures and motions of a reaction list the model has in use, in order (voice sounds and the rest left out).
    private static IReadOnlyList<CharacterActionSource> Sources(IReadOnlyList<string> list, CharacterActionCatalog? catalog)
    {
        var entries = catalog?.Entries.Where(e => e.Action.Enabled).ToArray() ?? [];
        return [.. list.Select(id => entries.FirstOrDefault(e => e.Source.Id == id).Source).OfType<CharacterActionSource>().Distinct().Take(MaximumActions)];
    }

    // The model's own tap motion for the zone's part, when it has one in use: the head's for the head's zones (the hair's for hair).
    private static CharacterActionSource? TapMotion(CharacterTouchZone zone, CharacterActionCatalog catalog)
    {
        var part = Kind(zone.Id)?.Group == TouchZoneGroup.Head ? zone.Id.StartsWith("hair", StringComparison.Ordinal) ? "hair" : "head" : "body";
        var motions = catalog.Entries.Where(e => e.Action.Enabled && e.Source.Kind == CharacterActionKind.Motion).Select(e => e.Source).ToArray();
        return TouchMotion([.. motions.Select(m => m.Name)], part) is { } group ? motions.FirstOrDefault(m => m.Name == group) : null;
    }

    /// <summary>The model's motion group for a tap on <paramref name="zone"/> (a rough zone: head, hair, face, body...), matched
    /// ignoring case and punctuation: Tap or Touch followed by the zone, then by Head (for the head, hair and face) or Body
    /// (elsewhere), then plain Tap or Touch.</summary>
    public static string? TouchMotion(IReadOnlyList<string> groups, string zone)
    {
        static string Plain(string name) => new(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        var part = zone is "head" or "hair" or "face" ? "head" : "body";
        foreach (var wanted in new[] { "tap" + zone, "touch" + zone, "tap" + part, "touch" + part, "tap", "touch" })
            if (groups.FirstOrDefault(group => Plain(group) == wanted) is { } found) return found;
        return null;
    }

    private static IReadOnlyList<CharacterActionSource> DefaultPlan(CharacterTouchZone zone, CharacterActionCatalog? catalog)
    {
        if (catalog is null) return [];
        var entries = catalog.Entries.Where(e => e.Action.Enabled).ToArray();
        var plan = new List<CharacterActionSource>();
        // A zone of its own, special to the character (a hair ribbon, a halo), reacts as the extras do.
        foreach (var slot in Kind(zone.Id)?.Defaults ?? Extra)
            foreach (var word in slot)
            {
                var hit = entries.Where(e => Named(e, word)).OrderBy(e => e.Source.Kind == CharacterActionKind.Gesture ? 1 : 0)
                    .Select(e => e.Source).FirstOrDefault(s => !plan.Contains(s));
                if (hit is null) continue;
                plan.Add(hit);
                break;
            }
        return plan;
    }

    private static bool Named((CharacterActionSource Source, CharacterAction Action) entry, string word) =>
        string.Equals(entry.Action.Tag, word, StringComparison.OrdinalIgnoreCase) ||
        (entry.Source.Kind == CharacterActionKind.Gesture && string.Equals(entry.Source.Name, word, StringComparison.OrdinalIgnoreCase)) ||
        CharacterActions.Slug(entry.Source.Name) == word;

    /// <summary>The owner's own words for a touch on a zone Martlet notices (a hint that goes with it), or null: none, the zone's
    /// built-in line, or Martlet doesn't notice the zone.</summary>
    public static string? Narration(CharacterTouchZone zone) => zone.Reaction.Notices && zone.Reaction.Narration is { Length: > 0 } own &&
        own != Kind(zone.Id)?.Narration ? own : null;

    /// <summary>Whether a quick tap on <paramref name="zone"/> is a pat (the top of the head, the hair, animal ears) rather than a poke.</summary>
    public static bool Pats(CharacterTouchZone zone) => zone.Id is "top_of_head" or "hair" or "animal_ears";

    /// <summary>Where <paramref name="zone"/> is, as the character hears it ("the top of your head", "your left cheek").</summary>
    public static string Part(CharacterTouchZone zone) => zone.Label is null ? zone.Id switch
    {
        "top_of_head" => "the top of your head",
        "face" => "your face",
        "waist" => "your sides",
        "held_item" => "what you're holding",
        "skirt_hem" => "the hem of your skirt",
        _ => "your " + zone.Name.ToLowerInvariant()
    } : "your " + zone.Name.ToLowerInvariant();
}
