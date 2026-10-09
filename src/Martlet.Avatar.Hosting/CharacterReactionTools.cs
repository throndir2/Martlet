using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Martlet.Avatar.Hosting;

/// <summary>What the Touch reactions tools work with for one call: the persona whose reactions change (<see cref="Who"/> is its
/// name), the owner's temperament it uses (without the character's changes), the shown model's zones and emotes, the check-in
/// that calls (and when its run started, so its changes can be counted) and the time.</summary>
public sealed record ReactionToolContext
{
    public required Guid PersonaId { get; init; }
    public string Who { get; init; } = "Martlet";
    public CharacterTouchTemperament? Temperament { get; init; }
    public CharacterTouchZoneSettings? Zones { get; init; }
    public CharacterActionCatalog? Catalog { get; init; }
    public string? CheckInId { get; init; }
    public required DateTimeOffset Run { get; init; }
    public required DateTimeOffset Now { get; init; }
}

/// <summary>What a Touch reactions tool call did: its short result for the model (the first line also shows on the check-in's
/// card, so it never holds the character's reason), whether it failed, and the changes to save (null: nothing changed).</summary>
public sealed record ReactionToolAnswer(string Result, bool Failed, IReadOnlyList<CharacterReactionChange>? Changes = null)
{
    public override string ToString() => $"{nameof(ReactionToolAnswer)} (failed: {Failed}, saves: {Changes is not null})";
}

/// <summary>
/// The Touch reactions tools a check-in calls (Martlet.Conversation's TouchReactions set defines them for the model): read how
/// the character reacts to touches now, change how it feels about a category or a zone, choose what a zone plays, set a mood
/// for every touch, and undo its own changes. Every change is bounded (<see cref="CharacterReactionChanges"/>) and lasts a few
/// hours at most. No provider types here, so the desktop and Martlet's MCP run the same calls.
/// </summary>
public static class CharacterReactionTools
{
    public const string Read = "read_touch_reactions", Feel = "change_touch_feeling", React = "change_zone_reactions",
        Mood = "set_touch_mood", Undo = "undo_touch_change";
    public static IReadOnlyList<string> Names { get; } = [Read, Feel, React, Mood, Undo];
    private const int MaximumListed = 60;

    /// <summary>Runs <paramref name="tool"/> with the model's <paramref name="argumentsJson"/> on the changes saved now.</summary>
    public static ReactionToolAnswer Call(string tool, string? argumentsJson, ReactionToolContext context, IReadOnlyList<CharacterReactionChange> saved)
    {
        ArgumentNullException.ThrowIfNull(context);
        JsonElement arguments;
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson,
                new JsonDocumentOptions { AllowTrailingCommas = true, MaxDepth = 8 });
            arguments = document.RootElement.Clone();
        }
        catch (JsonException) { return Fail("The arguments aren't valid JSON. Send an object such as {\"why\": \"...\"}."); }
        if (arguments.ValueKind != JsonValueKind.Object) return Fail("The arguments must be a JSON object.");
        return tool switch
        {
            Read => new(Describe(context, saved), false),
            Feel => ChangeFeeling(arguments, context, saved),
            React => ChangeZone(arguments, context, saved),
            Mood => SetMood(arguments, context, saved),
            Undo => UndoChange(arguments, context, saved),
            _ => Fail($"There is no tool called {tool} in Touch reactions.")
        };
    }

    private static ReactionToolAnswer Fail(string why) => new(why, true);

    // ---------- reading ----------

    /// <summary>How the character reacts to touches now, with its own changes in effect, for the model: the categories and the
    /// shown model's zones with how it feels and what each plays, what a zone can play, the reaction words, its changes in effect
    /// and the limits left.</summary>
    public static string Describe(ReactionToolContext context, IReadOnlyList<CharacterReactionChange> saved)
    {
        var active = CharacterReactionChanges.Active(saved, context.PersonaId, context.Now);
        var felt = CharacterReactionChanges.Temperament(context.Temperament, active, context.PersonaId);
        var text = new StringBuilder();
        text.Append("How ").Append(context.Who).Append(" reacts to touches now, with its own changes in effect.\n");
        text.Append("Feelings, from least to most liked: ").Append(string.Join(", ", CharacterTouchTemperaments.AttitudeWords)).Append(".\n\n");
        text.Append("Categories (id: what - feeling now; the owner's when it differs):\n");
        foreach (var (_, id, label) in CharacterTouchTemperaments.GroupIds)
        {
            text.Append("- ").Append(id).Append(": ").Append(label).Append(" - ");
            if (id == CharacterTouchTemperaments.IntimateId && felt?.Groups.ContainsKey(id) != true)
            {
                text.Append("as each part's own category\n");
                continue;
            }
            var now = CharacterReactionChanges.BaseAttitude(felt, id);
            var owner = CharacterReactionChanges.BaseAttitude(context.Temperament, id);
            text.Append(CharacterTouchTemperaments.AttitudeWord(now));
            if (now != owner) text.Append(" (the owner's: ").Append(CharacterTouchTemperaments.AttitudeWord(owner)).Append(')');
            text.Append('\n');
        }
        var zones = context.Zones is { } shown ? shown.Zones.Where(shown.Active).ToArray() : [];
        if (zones.Length == 0) text.Append("\nThe shown character has no touch zones in use, so only the categories can change.\n");
        else
        {
            text.Append("\nZones on the shown character (id: name, category - feeling; plays):\n");
            foreach (var zone in zones)
            {
                var changed = CharacterReactionChanges.Zone(zone, context.Temperament, active, context.Catalog);
                var plan = CharacterTouchZones.React(changed, context.Catalog, felt, 1);
                var plays = plan.Actions.Select(s => s.Name).Concat((plan.Sounds ?? []).Select(cue => CharacterReactionChanges.SoundPrefix + cue)).ToArray();
                text.Append("- ").Append(zone.Id).Append(": ").Append(zone.Name).Append(", ").Append(CharacterReactionChanges.CategoryOf(zone.Id))
                    .Append(" - ").Append(plan.Attitude ?? CharacterTouchTemperaments.AttitudeWord(0)).Append("; plays ")
                    .Append(plays.Length == 0 ? "nothing" : string.Join(", ", plays)).Append('\n');
            }
        }
        var playable = Playable(context.Catalog);
        if (playable.Count > 0)
        {
            text.Append("\nWhat a zone can play (id - name, kind), for change_zone_reactions:\n");
            foreach (var source in playable.Take(MaximumListed))
                text.Append("- ").Append(source.Id).Append(" - ").Append(source.Name).Append(", ").Append(source.Kind.ToString().ToLowerInvariant()).Append('\n');
            if (playable.Count > MaximumListed) text.Append("(and ").Append(playable.Count - MaximumListed).Append(" more)\n");
        }
        text.Append("A zone can also play a voice sound: ").Append(CharacterReactionChanges.SoundPrefix).Append("<sound>, one of: ")
            .Append(string.Join(", ", SoundCues)).Append(".\n");
        text.Append("\nReaction words, for change_touch_feeling: ").Append(string.Join(", ", CharacterTouchTemperaments.Vocabulary))
            .Append(", or none for no reaction.\n");
        text.Append("\nYour changes in effect (id: what, until when; why):\n");
        if (active.Count == 0) text.Append("(none)\n");
        foreach (var change in active)
            text.Append("- ").Append(change.Id).Append(": ").Append(CharacterReactionChanges.Describe(change, context.Zones, context.Catalog))
                .Append(", ").Append(CharacterReactionChanges.Until(change, context.Now)).Append("; ").Append(change.Why).Append('\n');
        var (run, day, slots) = Left(context, saved);
        text.Append("\nLimits: ").Append(run).Append(" more changes this check, ").Append(day).Append(" more today and ").Append(slots)
            .Append(" more in effect at once. Each lasts ").Append(Hours(CharacterReactionChanges.MinimumHours)).Append(" to ")
            .Append(Hours(CharacterReactionChanges.MaximumHours)).Append(" (").Append(Hours(CharacterReactionChanges.DefaultHours))
            .Append(" when you don't say). A feeling moves at most ").Append(CharacterReactionChanges.MaximumShift)
            .Append(" steps from the owner's. undo_touch_change ends your own changes early.");
        return text.ToString();
    }

    /// <summary>The emotes, motions and gestures turned on that a zone can play.</summary>
    public static IReadOnlyList<CharacterActionSource> Playable(CharacterActionCatalog? catalog) =>
        catalog is null ? [] : [.. catalog.Entries.Where(e => e.Action.Enabled).Select(e => e.Source)];

    // How many changes are left this run, today and at once.
    private static (int Run, int Day, int Slots) Left(ReactionToolContext context, IReadOnlyList<CharacterReactionChange> saved)
    {
        var own = saved.Where(c => c.PersonaId == context.PersonaId).ToArray();
        var run = own.Count(c => c.Run == context.Run && c.By == context.CheckInId);
        var day = own.Count(c => c.At > context.Now - TimeSpan.FromDays(1));
        var active = own.Count(c => c.ActiveAt(context.Now));
        return (Math.Max(0, CharacterReactionChanges.MaximumPerRun - run), Math.Max(0, CharacterReactionChanges.MaximumPerDay - day),
            Math.Max(0, CharacterReactionChanges.MaximumActive - active));
    }

    private static string Hours(double hours) => hours < 1
        ? $"{Math.Round(hours * 60).ToString(CultureInfo.InvariantCulture)} minutes"
        : hours == 1 ? "1 hour" : $"{hours.ToString("0.##", CultureInfo.InvariantCulture)} hours";

    // ---------- changing ----------

    // The checks every new change passes: a reason, the limits, and how long it lasts. Null and the reason when it may be made.
    private static (string? Problem, string Why, double Hours, string? Note) Common(JsonElement arguments, ReactionToolContext context,
        IReadOnlyList<CharacterReactionChange> saved, string kind, string target)
    {
        if (CharacterReactionChanges.Reason(Text(arguments, "why")) is not { } why)
            return ("Say why in why: one short line in your own words.", "", 0, null);
        var (run, day, slots) = Left(context, saved);
        if (run == 0) return ($"You already made {CharacterReactionChanges.MaximumPerRun} changes in this check.", why, 0, null);
        if (day == 0) return ($"You already made {CharacterReactionChanges.MaximumPerDay} changes today. Try again later.", why, 0, null);
        var replaces = saved.Any(c => c.PersonaId == context.PersonaId && c.ActiveAt(context.Now) && c.Kind == kind && c.Target == target);
        if (slots == 0 && !replaces)
            return ($"{CharacterReactionChanges.MaximumActive} of your changes are in effect already. Undo one first.", why, 0, null);
        var asked = Number(arguments, "hours");
        if (asked is { } given && !double.IsFinite(given)) asked = null;
        var hours = Math.Clamp(asked ?? CharacterReactionChanges.DefaultHours, CharacterReactionChanges.MinimumHours, CharacterReactionChanges.MaximumHours);
        var note = asked is { } wanted && Math.Abs(wanted - hours) > 0.001 ? $" (a change lasts {Hours(CharacterReactionChanges.MinimumHours)} to {Hours(CharacterReactionChanges.MaximumHours)})" : null;
        return (null, why, hours, note);
    }

    private static CharacterReactionChange New(ReactionToolContext context, string kind, string target, string why, double hours) => new()
    {
        Id = CharacterReactionChanges.NewId(), PersonaId = context.PersonaId, Kind = kind, Target = target, Why = why,
        At = context.Now.ToUniversalTime(), Until = context.Now.ToUniversalTime().AddHours(hours), By = context.CheckInId,
        Run = context.Run.ToUniversalTime()
    };

    private static ReactionToolAnswer Saved(ReactionToolContext context, IReadOnlyList<CharacterReactionChange> saved, CharacterReactionChange change, string? note) =>
        new($"{CharacterReactionChanges.Describe(change, context.Zones, context.Catalog)}, for {Hours((change.Until - change.At).TotalHours)}{note} " +
            $"(change {change.Id}).", false, CharacterReactionChanges.With(saved, change, context.Now));

    // A category ID, or a zone of the shown model; null with why when it is neither.
    private static (string? Target, string? Problem) Target(string? text, ReactionToolContext context, bool zonesOnly)
    {
        if (string.IsNullOrWhiteSpace(text)) return (null, zonesOnly ? "Name the zone in zone." : "Name a category or a zone in target.");
        var slug = string.Join("_", text.Trim().ToLowerInvariant().Split([' ', '-', '/'], StringSplitOptions.RemoveEmptyEntries));
        if (!zonesOnly && CharacterReactionChanges.IsCategory(slug)) return (slug, null);
        var zones = context.Zones is { } shown ? shown.Zones.Where(shown.Active).ToArray() : [];
        var zone = zones.FirstOrDefault(z => z.Id == slug) ?? zones.FirstOrDefault(z => z.Id == CharacterTouchZones.Normalize(text)) ??
            zones.FirstOrDefault(z => string.Equals(z.Name, text.Trim(), StringComparison.OrdinalIgnoreCase));
        if (zone is not null) return (zone.Id, null);
        return (null, $"\"{text.Trim()}\" is not " + (zonesOnly ? "" : "a category or ") + "a zone in use on the shown character. " +
            "read_touch_reactions lists them.");
    }

    private static ReactionToolAnswer ChangeFeeling(JsonElement arguments, ReactionToolContext context, IReadOnlyList<CharacterReactionChange> saved)
    {
        var (target, problem) = Target(Text(arguments, "target") ?? Text(arguments, "zone") ?? Text(arguments, "category"), context, zonesOnly: false);
        if (target is null) return Fail(problem!);
        var feeling = Property(arguments, "feeling") ?? Property(arguments, "attitude");
        var asked = feeling switch
        {
            { ValueKind: JsonValueKind.Number } n when n.TryGetInt32(out var value) => CharacterTouchTemperaments.AttitudeOf(value.ToString(CultureInfo.InvariantCulture)),
            { ValueKind: JsonValueKind.String } s => CharacterTouchTemperaments.AttitudeOf(s.GetString()),
            _ => null
        };
        if (asked is not { } attitude) return Fail("Give feeling: one of " + string.Join(", ", CharacterTouchTemperaments.AttitudeWords) + ".");
        IReadOnlyList<string>? words = null;
        if (Property(arguments, "reactions") is { ValueKind: JsonValueKind.Array or JsonValueKind.String } given)
        {
            var texts = given.ValueKind == JsonValueKind.String ? [given.GetString() ?? ""]
                : given.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString() ?? "").ToArray();
            if (texts.Length == 0 || texts.All(t => t.Trim().ToLowerInvariant() is CharacterTouchTemperaments.NoReaction or "nothing")) words = [];
            else
            {
                var unknown = texts.Where(t => CharacterTouchTemperaments.Word(t) is null && t.Trim().ToLowerInvariant() != CharacterTouchTemperaments.NoReaction).ToArray();
                if (unknown.Length > 0)
                    return Fail($"Not reaction words: {string.Join(", ", unknown)}. Use words from read_touch_reactions, or none.");
                words = [.. texts.Select(CharacterTouchTemperaments.Word).OfType<string>().Distinct(StringComparer.Ordinal).Take(CharacterTouchTemperaments.MaximumReactions)];
            }
        }
        var owner = CharacterReactionChanges.BaseAttitude(context.Temperament, target);
        var bounded = Math.Clamp(attitude, owner - CharacterReactionChanges.MaximumShift, owner + CharacterReactionChanges.MaximumShift);
        var (common, why, hours, note) = Common(arguments, context, saved, CharacterReactionChange.KindFeeling, target);
        if (common is not null) return Fail(common);
        if (bounded != attitude)
            note += $" ({CharacterTouchTemperaments.AttitudeWord(bounded)} is as far from the owner's {CharacterTouchTemperaments.AttitudeWord(owner)} as a change goes)";
        var change = New(context, CharacterReactionChange.KindFeeling, target, why, hours) with { Attitude = bounded, Words = words };
        return Saved(context, saved, change, note);
    }

    private static ReactionToolAnswer ChangeZone(JsonElement arguments, ReactionToolContext context, IReadOnlyList<CharacterReactionChange> saved)
    {
        var (target, problem) = Target(Text(arguments, "zone") ?? Text(arguments, "target"), context, zonesOnly: true);
        if (target is null) return Fail(problem!);
        if (Property(arguments, "plays") is not { ValueKind: JsonValueKind.Array } plays)
            return Fail("Give plays: a list of what the zone plays, by the ids read_touch_reactions lists (an empty list plays nothing).");
        var entries = new List<string>();
        var unknown = new List<string>();
        foreach (var item in plays.EnumerateArray())
        {
            var text = item.ValueKind == JsonValueKind.String ? item.GetString()?.Trim() ?? "" : item.ToString();
            if (Entry(text, context.Catalog) is { } entry) { if (!entries.Contains(entry)) entries.Add(entry); }
            else unknown.Add(text);
        }
        if (unknown.Count > 0) return Fail($"The shown character can't play: {string.Join(", ", unknown)}. Use ids from read_touch_reactions.");
        string? note = null;
        if (entries.Count > CharacterTouchZones.MaximumActions)
        {
            entries = entries.Take(CharacterTouchZones.MaximumActions).ToList();
            note = $" (a zone plays at most {CharacterTouchZones.MaximumActions} things)";
        }
        var (common, why, hours, longer) = Common(arguments, context, saved, CharacterReactionChange.KindReactions, target);
        if (common is not null) return Fail(common);
        var change = New(context, CharacterReactionChange.KindReactions, target, why, hours) with { Reactions = entries };
        return Saved(context, saved, change, longer + note);
    }

    /// <summary>The voice sounds a zone can play (the cues of the sound tags Martlet's voices make: laugh, sigh...).</summary>
    public static IReadOnlyList<string> SoundCues { get; } =
        [.. Martlet.Core.Settings.VoiceTags.Known.Where(t => t.Kind == Martlet.Core.Settings.VoiceTagKind.Sound).Select(t => t.Cue).Distinct(StringComparer.Ordinal)];

    /// <summary>A zone's reaction list entry for what the model wrote: a voice sound ("sound:laugh", one of
    /// <see cref="SoundCues"/>), or an emote, motion or gesture of the model that is turned on, by its ID, its tag or its name;
    /// null when there is nothing of that name.</summary>
    public static string? Entry(string? text, CharacterActionCatalog? catalog)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        if (text.StartsWith(CharacterReactionChanges.SoundPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var cue = text[CharacterReactionChanges.SoundPrefix.Length..].Trim().ToLowerInvariant().Replace(' ', '_');
            return SoundCues.Contains(cue) ? CharacterReactionChanges.SoundPrefix + cue : null;
        }
        var playable = catalog?.Entries.Where(e => e.Action.Enabled).ToArray() ?? [];
        var bare = text.Trim('{', '}');
        return playable.FirstOrDefault(e => e.Source.Id == text).Source?.Id ??
            playable.FirstOrDefault(e => string.Equals(e.Action.Tag, bare, StringComparison.OrdinalIgnoreCase)).Source?.Id ??
            playable.FirstOrDefault(e => string.Equals(e.Source.Name, bare, StringComparison.OrdinalIgnoreCase)).Source?.Id ??
            playable.FirstOrDefault(e => CharacterActions.Slug(e.Source.Name) == CharacterActions.Slug(bare)).Source?.Id;
    }

    private static ReactionToolAnswer SetMood(JsonElement arguments, ReactionToolContext context, IReadOnlyList<CharacterReactionChange> saved)
    {
        var shift = Number(arguments, "shift");
        if (shift is not { } value || !double.IsFinite(value) || Math.Round(value) == 0)
            return Fail($"Give shift: -{CharacterReactionChanges.MaximumShift} to {CharacterReactionChanges.MaximumShift}, not 0 (below 0: every touch less liked).");
        var steps = (int)Math.Clamp(Math.Round(value), -CharacterReactionChanges.MaximumShift, CharacterReactionChanges.MaximumShift);
        var (common, why, hours, note) = Common(arguments, context, saved, CharacterReactionChange.KindMood, CharacterReactionChanges.All);
        if (common is not null) return Fail(common);
        var change = New(context, CharacterReactionChange.KindMood, CharacterReactionChanges.All, why, hours) with { Shift = steps };
        return Saved(context, saved, change, note);
    }

    private static ReactionToolAnswer UndoChange(JsonElement arguments, ReactionToolContext context, IReadOnlyList<CharacterReactionChange> saved)
    {
        var id = (Text(arguments, "change") ?? Text(arguments, "id"))?.Trim();
        if (string.IsNullOrEmpty(id)) return Fail("Give change: the id of one of your changes in effect, or all.");
        var active = CharacterReactionChanges.Active(saved, context.PersonaId, context.Now);
        if (id.Equals(All, StringComparison.OrdinalIgnoreCase))
            return active.Count == 0 ? new("None of your changes is in effect.", false)
                : new($"Undid your {active.Count} changes: touches play what the owner chose again.", false,
                    CharacterReactionChanges.End(saved, context.PersonaId, CharacterReactionChange.ByCharacter, context.Now));
        if (active.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase)) is not { } change)
            return Fail($"No change {id} of yours is in effect. read_touch_reactions lists them.");
        return new($"Undid {CharacterReactionChanges.Describe(change, context.Zones, context.Catalog)} (change {change.Id}).", false,
            CharacterReactionChanges.End(saved, context.PersonaId, CharacterReactionChange.ByCharacter, context.Now, change.Id));
    }

    private const string All = CharacterReactionChanges.All;

    // ---------- arguments ----------

    private static JsonElement? Property(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        return null;
    }

    private static string? Text(JsonElement element, string name) => Property(element, name) switch
    {
        { ValueKind: JsonValueKind.String } s => s.GetString(),
        { ValueKind: JsonValueKind.Number } n => n.GetRawText(),
        _ => null
    };

    private static double? Number(JsonElement element, string name) => Property(element, name) switch
    {
        { ValueKind: JsonValueKind.Number } n => n.GetDouble(),
        { ValueKind: JsonValueKind.String } s when double.TryParse(s.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) => v,
        _ => null
    };
}
