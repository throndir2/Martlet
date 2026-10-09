using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Core.Contracts;

namespace Martlet.Avatar.Hosting;

/// <summary>How a persona feels about one zone group or zone kind being touched: its <see cref="Attitude"/>
/// (<see cref="CharacterTouchTemperaments.MinimumAttitude"/> hates .. <see cref="CharacterTouchTemperaments.MaximumAttitude"/>
/// craves), the abstract reactions it plays, in order (words from <see cref="CharacterTouchTemperaments.Vocabulary"/>; null
/// plays the attitude's defaults, an empty list nothing at all), how many seconds the first reaction lingers (0: it plays once)
/// and how many seconds the character then looks at the mouse pointer (<see cref="LookSeconds"/>; 0: it doesn't).</summary>
public sealed record TouchTemperamentEntry
{
    public int Attitude { get; init; }
    public IReadOnlyList<string>? Reactions { get; init; }
    public double LingerSeconds { get; init; }
    public double LookSeconds { get; init; }
}

/// <summary>What repeated touches of one zone do: from the <see cref="After"/>th touch in a row (each within
/// <see cref="CharacterTouchTemperaments.RepeatWindowSeconds"/> of the one before), a disliked or hated zone plays
/// <see cref="Disliked"/> first and a loved or craved zone plays <see cref="Loved"/> first.</summary>
public sealed record TouchEscalation
{
    public int After { get; init; } = 3;
    public IReadOnlyList<string> Disliked { get; init; } = ["anger", "look_away"];
    public IReadOnlyList<string> Loved { get; init; } = ["hearts", "blush"];
}

/// <summary>One persona's touch temperament: how the character ACTS (never what it says) when each part of its body is touched,
/// and where its eyes usually go (<see cref="Gaze"/>; null before it is decided: they follow the mouse).
/// Decided by the Thinking model from the persona's personality (<see cref="ByThinking"/>; <see cref="ByFixture"/> when a
/// MARTLET_TOUCH_TEMPERAMENT_FIXTURE file stood in for it) or edited by the owner (<see cref="ByOwner"/>); <see cref="ByCustom"/>
/// when the persona uses one of the owner's custom temperaments (<see cref="Custom"/>) instead of its own. Zone kinds
/// (<see cref="Zones"/>) win over their category (<see cref="Groups"/>: an intimate kind's is
/// <see cref="CharacterTouchTemperaments.IntimateId"/>, else its body group's); a zone with neither keeps its built-in default
/// reaction. <see cref="PersonalityDigest"/> is the <see cref="CharacterTouchTemperaments.Digest"/> of the personality it was
/// decided from.</summary>
public sealed record CharacterTouchTemperament
{
    public const string ByThinking = "thinking", ByOwner = "owner", ByFixture = "fixture", ByCustom = "custom";
    public required Guid PersonaId { get; init; }
    public string Source { get; init; } = ByThinking;
    public string? PersonalityDigest { get; init; }
    public DateTimeOffset? DecidedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public GazeMode? Gaze { get; init; }
    public IReadOnlyDictionary<string, TouchTemperamentEntry> Groups { get; init; } = new Dictionary<string, TouchTemperamentEntry>();
    public IReadOnlyDictionary<string, TouchTemperamentEntry> Zones { get; init; } = new Dictionary<string, TouchTemperamentEntry>();
    public TouchEscalation Escalation { get; init; } = new();
    /// <summary>For <see cref="ByCustom"/>: the owner's custom temperament the persona uses. Never saved with a persona's own.</summary>
    [JsonIgnore] public CustomTouchTemperament? Custom { get; init; }
}

/// <summary>A touch temperament the owner made and named (Companion › Touch › Touch temperament), which any persona can use
/// instead of its own (<see cref="TouchTemperamentSet.Uses"/>). Editing it changes it for every persona that uses it. It never
/// goes to the Thinking model.</summary>
public sealed record CustomTouchTemperament
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public GazeMode? Gaze { get; init; }
    public IReadOnlyDictionary<string, TouchTemperamentEntry> Groups { get; init; } = new Dictionary<string, TouchTemperamentEntry>();
    public IReadOnlyDictionary<string, TouchTemperamentEntry> Zones { get; init; } = new Dictionary<string, TouchTemperamentEntry>();
    public TouchEscalation Escalation { get; init; } = new();

    /// <summary>This temperament as the persona <paramref name="personaId"/> uses it.</summary>
    public CharacterTouchTemperament For(Guid personaId) => new()
    {
        PersonaId = personaId, Source = CharacterTouchTemperament.ByCustom, UpdatedAt = UpdatedAt, Gaze = Gaze, Groups = Groups, Zones = Zones,
        Escalation = Escalation, Custom = this
    };

    /// <summary>A new custom temperament called <paramref name="name"/>: a copy of <paramref name="temperament"/> (null: the
    /// built-in reactions, with nothing decided).</summary>
    public static CustomTouchTemperament Copy(string name, CharacterTouchTemperament? temperament, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(), Name = name.Trim(), UpdatedAt = now.ToUniversalTime(), Gaze = temperament?.Gaze,
        Groups = temperament?.Groups ?? new Dictionary<string, TouchTemperamentEntry>(),
        Zones = temperament?.Zones ?? new Dictionary<string, TouchTemperamentEntry>(), Escalation = temperament?.Escalation ?? new()
    };
}

/// <summary>What character-temperaments.json keeps: each persona's own temperament (<see cref="Personas"/>: decided from its
/// personality, or edited by the owner), the owner's custom temperaments (<see cref="Custom"/>) and which temperament a persona
/// uses instead of its own (<see cref="Uses"/>: <see cref="CharacterTouchTemperaments.BuiltIn"/> for the built-in reactions, or a
/// custom temperament's ID). A persona that isn't in <see cref="Uses"/> uses its own.</summary>
public sealed record TouchTemperamentSet
{
    public static readonly TouchTemperamentSet Empty = new();
    public IReadOnlyList<CharacterTouchTemperament> Personas { get; init; } = [];
    public IReadOnlyList<CustomTouchTemperament> Custom { get; init; } = [];
    public IReadOnlyDictionary<Guid, string> Uses { get; init; } = new Dictionary<Guid, string>();

    /// <summary>The persona's own temperament, or null.</summary>
    public CharacterTouchTemperament? Own(Guid personaId) => Personas.FirstOrDefault(t => t.PersonaId == personaId);

    /// <summary>Whether the persona uses the built-in reactions, as the owner chose.</summary>
    public bool UsesBuiltIn(Guid personaId) => Uses.TryGetValue(personaId, out var uses) && uses == CharacterTouchTemperaments.BuiltIn;

    /// <summary>The custom temperament the persona uses, or null.</summary>
    public CustomTouchTemperament? CustomOf(Guid personaId) =>
        Uses.TryGetValue(personaId, out var uses) && Guid.TryParse(uses, out var id) ? Custom.FirstOrDefault(c => c.Id == id) : null;

    /// <summary>The temperament the persona uses: the custom one it uses, none for the built-in reactions, else its own.</summary>
    public CharacterTouchTemperament? For(Guid personaId) =>
        UsesBuiltIn(personaId) ? null : CustomOf(personaId) is { } custom ? custom.For(personaId) : Own(personaId);

    /// <summary>The personas that use the custom temperament <paramref name="customId"/>.</summary>
    public IReadOnlyList<Guid> UsedBy(Guid customId) =>
        Uses.Where(u => Guid.TryParse(u.Value, out var id) && id == customId).Select(u => u.Key).Order().ToArray();

    /// <summary>With the persona's own temperament saved (the newest <see cref="CharacterTouchTemperaments.MaximumPersonas"/> kept).</summary>
    public TouchTemperamentSet WithOwn(CharacterTouchTemperament temperament) => this with
    {
        Personas = Personas.Where(t => t.PersonaId != temperament.PersonaId).Append(temperament).OrderByDescending(t => t.UpdatedAt)
            .Take(CharacterTouchTemperaments.MaximumPersonas).ToArray()
    };

    /// <summary>Without the persona's own temperament.</summary>
    public TouchTemperamentSet WithoutOwn(Guid personaId) => this with { Personas = Personas.Where(t => t.PersonaId != personaId).ToArray() };

    /// <summary>With the custom temperament saved: added, or in place of the one with its ID.</summary>
    public TouchTemperamentSet WithCustom(CustomTouchTemperament custom)
    {
        custom = custom with { Name = custom.Name.Trim() };
        return this with { Custom = Custom.Any(c => c.Id == custom.Id) ? Custom.Select(c => c.Id == custom.Id ? custom : c).ToArray() : [.. Custom, custom] };
    }

    /// <summary>Without the custom temperament <paramref name="id"/>: the personas that used it use their own again.</summary>
    public TouchTemperamentSet WithoutCustom(Guid id) => this with
    {
        Custom = Custom.Where(c => c.Id != id).ToArray(),
        Uses = Uses.Where(u => !(Guid.TryParse(u.Value, out var used) && used == id)).ToDictionary()
    };

    /// <summary>With the persona using <paramref name="uses"/>: null for its own temperament, <see cref="CharacterTouchTemperaments.BuiltIn"/>
    /// for the built-in reactions, or a custom temperament's ID.</summary>
    public TouchTemperamentSet Choose(Guid personaId, string? uses)
    {
        var next = Uses.Where(u => u.Key != personaId).ToDictionary();
        if (uses is not null) next[personaId] = uses;
        return this with { Uses = next };
    }
}

/// <summary>What a touch on a zone plays: the model's emotes, motions and gestures, how long the first one lingers, the
/// persona's attitude word for the zone (null without a temperament), where the reaction came from (<c>owner</c>: the zone's
/// own pick, <c>temperament</c>: the persona's temperament, <c>default</c>: the zone's built-in reaction), whether repeated
/// touches escalated it, and how many seconds the character looks at the mouse pointer after it (the temperament's; 0: it
/// doesn't).</summary>
public sealed record TouchReactionPlan(IReadOnlyList<CharacterActionSource> Actions, double LingerSeconds = 0, string? Attitude = null,
    string From = TouchReactionPlan.FromDefault,     bool Escalated = false, double LookSeconds = 0,
        IReadOnlyList<CharacterActionSource>? Autoplay = null, double AutoplaySeconds = 0)
    {
    public const string FromOwner = "owner", FromTemperament = "temperament", FromDefault = "default";
}

/// <summary>Touch temperaments: the vocabulary of abstract reactions, the Thinking model's request and how its answer is read,
/// turning a temperament into what a touch plays on a model, and the per-persona temperaments, the owner's custom temperaments
/// and which one each persona uses, kept in character-temperaments.json (shared between the owner's computers, like the
/// personas).</summary>
public static class CharacterTouchTemperaments
{
    public const string FileName = "character-temperaments.json";
    public const int MinimumAttitude = -2, MaximumAttitude = 3, MaximumReactions = 3, MaximumPersonas = 32, MaximumBytes = 1024 * 1024;
    public const int MinimumAfter = 2, MaximumAfter = 20;
    public const double MaximumLinger = 15, RepeatWindowSeconds = 30, MaximumLook = CharacterGaze.MaximumAttention;
    /// <summary>The word a temperament writes for a part the character doesn't react to at all.</summary>
    public const string NoReaction = "none";
    /// <summary>The category of every intimate zone kind (<see cref="TouchZoneKind.Intimate"/>).</summary>
    public const string IntimateId = "intimate";
    /// <summary>What <see cref="TouchTemperamentSet.Uses"/> holds for a persona that uses the built-in reactions.</summary>
    public const string BuiltIn = "built_in";
    public const int MaximumCustom = 32, MaximumNameLength = 40, MaximumUses = 64;
    /// <summary>The choices besides the custom temperaments, as Companion › Touch › Touch temperament names them. A custom
    /// temperament can't take these names.</summary>
    public const string OwnLabel = "Decided from its personality", BuiltInLabel = "Built-in reactions";

    /// <summary>The attitude words, from <see cref="MinimumAttitude"/> up.</summary>
    public static readonly IReadOnlyList<string> AttitudeWords = ["hates", "dislikes", "neutral", "likes", "loves", "craves"];

    /// <summary>The temperament's categories (its groups), each zone kind in exactly one (<see cref="GroupOf"/>): a body group
    /// holds its kinds that aren't intimate, and <see cref="IntimateId"/> (no body group) holds every intimate kind.</summary>
    public static readonly IReadOnlyList<(TouchZoneGroup? Group, string Id, string Label)> GroupIds =
    [
        (TouchZoneGroup.Head, "head", "Head and face"), (TouchZoneGroup.Torso, "torso", "Shoulders and torso"),
        (TouchZoneGroup.Arms, "arms", "Arms and hands"), (TouchZoneGroup.LowerBody, "lower_body", "Legs and feet"),
        (TouchZoneGroup.Extras, "extras", "Extras (animal ears, tail, wings...)"), (null, IntimateId, "Intimate parts")
    ];

    /// <summary>The abstract reactions a temperament may pick: Martlet's gestures and overlays that suit a touch. Each resolves
    /// per model when it plays (<see cref="Resolve"/>): the model's own emote or motion with that name first, else Martlet's.</summary>
    public static readonly IReadOnlyList<string> Vocabulary =
    [
        "smile", "blush", "shy", "giggle", "lean_in", "hearts", "sparkles", "happy", "laugh", "chuckle", "nod", "tilt", "sway", "bounce", "wink",
        "surprise", "gasp", "flinch", "exclaim", "question", "think", "pout", "sweat", "look_away", "eye_roll", "sigh", "groan", "shake", "shrug",
        "anger", "angry", "gloom", "tears", "crying", "fear", "drowsy", "sleepy", "music", "bow"
    ];

    // The model's own emote or motion names tried first for an abstract reaction (its tag or name, as a slug).
    private static readonly Dictionary<string, string[]> ModelNames = new(StringComparer.Ordinal)
    {
        ["smile"] = ["smile", "happy", "joy"], ["happy"] = ["happy", "smile", "joy"], ["blush"] = ["blush", "embarrassed"],
        ["shy"] = ["shy", "embarrassed", "blush"], ["giggle"] = ["giggle", "laugh"], ["laugh"] = ["laugh", "giggle"], ["chuckle"] = ["chuckle", "laugh"],
        ["hearts"] = ["hearts", "heart", "love", "loving"], ["sparkles"] = ["sparkles", "sparkle", "excited"], ["surprise"] = ["surprised", "surprise", "shock"],
        ["gasp"] = ["gasp", "surprised", "shock"], ["pout"] = ["pout", "sulk"], ["sweat"] = ["sweat", "nervous"], ["anger"] = ["angry", "anger", "mad"],
        ["angry"] = ["angry", "anger", "mad"], ["gloom"] = ["gloom", "depressed", "sad"], ["tears"] = ["tears", "cry", "crying", "sad"],
        ["crying"] = ["crying", "cry", "tears", "sad"], ["fear"] = ["fear", "scared", "afraid"], ["sleepy"] = ["sleepy", "sleep"], ["drowsy"] = ["drowsy", "sleepy"],
        ["wink"] = ["wink"], ["flinch"] = ["flinch", "startled"], ["look_away"] = ["look_away"], ["lean_in"] = ["lean_in"]
    };

    // Other words a model may write for a vocabulary word.
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["heart"] = "hearts", ["love"] = "hearts", ["mad"] = "anger", ["anger_vein"] = "anger", ["cry"] = "crying", ["sad"] = "tears",
        ["surprised"] = "surprise", ["embarrassed"] = "shy", ["lean"] = "lean_in", ["leanin"] = "lean_in", ["lookaway"] = "look_away",
        ["look_aside"] = "look_away", ["sparkle"] = "sparkles", ["scared"] = "fear", ["afraid"] = "fear", ["shake_head"] = "shake",
        ["tilt_head"] = "tilt", ["eyeroll"] = "eye_roll", ["roll_eyes"] = "eye_roll", ["startled"] = "flinch", ["exclamation"] = "exclaim",
        ["sulk"] = "pout", ["nervous"] = "sweat", ["smiling"] = "smile", ["blushing"] = "blush", ["giggling"] = "giggle", ["laughing"] = "laugh",
        ["notes"] = "music", ["zzz"] = "sleepy"
    };

    /// <summary>The reactions an attitude plays when no reactions were picked for it.</summary>
    public static IReadOnlyList<string> DefaultReactions(int attitude) => Math.Clamp(attitude, MinimumAttitude, MaximumAttitude) switch
    {
        -2 => ["anger", "flinch", "look_away"],
        -1 => ["pout", "sweat"],
        0 => ["tilt"],
        1 => ["smile", "blush"],
        2 => ["hearts", "blush", "lean_in"],
        _ => ["hearts", "blush", "shy"]
    };

    public static string AttitudeWord(int attitude) => AttitudeWords[Math.Clamp(attitude, MinimumAttitude, MaximumAttitude) - MinimumAttitude];

    public static string GroupId(TouchZoneGroup group) => GroupIds.First(g => g.Group == group).Id;

    /// <summary>The category a zone kind is in: <see cref="IntimateId"/> for an intimate kind, else its body group's.</summary>
    public static string GroupOf(TouchZoneKind kind) => kind.Intimate ? IntimateId : GroupId(kind.Group);

    /// <summary>The zone kinds in a category, in Martlet's order.</summary>
    public static IReadOnlyList<TouchZoneKind> KindsIn(string groupId) => CharacterTouchZones.Kinds.Where(k => GroupOf(k) == groupId).ToArray();

    /// <summary>A vocabulary word as Martlet knows it (lower case, underscores, common names mapped), or null when it isn't one.</summary>
    public static string? Word(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var slug = string.Join("_", text.Trim().Trim('{', '}', '*', '[', ']').ToLowerInvariant()
            .Split([' ', '-', '/', '.', ','], StringSplitOptions.RemoveEmptyEntries));
        if (Aliases.TryGetValue(slug, out var alias)) slug = alias;
        return Vocabulary.Contains(slug) ? slug : null;
    }

    /// <summary>The temperament's entry for a zone kind: the zone's own, else for an intimate kind the intimate category's, else
    /// its body group's (for a zone of its own, special to the character, the extras'), else null (the built-in default). So a
    /// temperament saved before the intimate category covers its intimate kinds with their body group, as it did then.</summary>
    public static TouchTemperamentEntry? Entry(CharacterTouchTemperament? temperament, string zoneId)
    {
        if (temperament is null) return null;
        if (temperament.Zones.TryGetValue(zoneId, out var own)) return own;
        if (CharacterTouchZones.Kind(zoneId) is not { } kind)
            return temperament.Groups.TryGetValue(GroupId(TouchZoneGroup.Extras), out var extras) ? extras : null;
        if (kind.Intimate && temperament.Groups.TryGetValue(IntimateId, out var intimate)) return intimate;
        return temperament.Groups.TryGetValue(GroupId(kind.Group), out var group) ? group : null;
    }

    /// <summary>The persona's attitude to a zone in one word ("loves", "hates"), or null when the temperament doesn't cover it.
    /// Data for others to use (such as a touch summary); nothing here puts it in a prompt.</summary>
    public static string? Attitude(CharacterTouchTemperament? temperament, string zoneId) =>
        Entry(temperament, zoneId) is { } entry ? AttitudeWord(entry.Attitude) : null;

    /// <summary>How the persona feels about being touched on <paramref name="zones"/>, from its temperament, for the touch line:
    /// "you love being touched there", or across zones it feels differently about, "you love it on your chest and your stomach,
    /// and hate it on your groin". Null when the temperament covers none of them or is neutral about them all.</summary>
    public static string? Feeling(CharacterTouchTemperament? temperament, IReadOnlyList<CharacterTouchZone> zones)
    {
        var felt = zones.Select(z => (Zone: z, Word: Attitude(temperament, z.Id))).Where(f => f.Word is not null and not "neutral").ToArray();
        if (felt.Length == 0) return null;
        // hates, dislikes, likes, loves, craves: the verb after "you".
        static string Verb(string word) => word[..^1];
        var groups = felt.GroupBy(f => f.Word!).ToArray();
        if (groups.Length == 1 && felt.Length == zones.Count) return $"you {Verb(groups[0].Key)} being touched there";
        static string List(IReadOnlyList<string> parts) => parts.Count == 1 ? parts[0] : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1];
        return "you " + string.Join(", and ", groups.Select(g => $"{Verb(g.Key)} it on {List([.. g.Select(f => CharacterTouchZones.Part(f.Zone))])}"));
    }

    /// <summary>The abstract reactions a touch plays: the entry's (or its attitude's defaults; none when it picked
    /// <see cref="NoReaction"/>), with the escalation's first after <see cref="TouchEscalation.After"/> touches in a row of a
    /// disliked or loved zone. At most <see cref="MaximumReactions"/>.</summary>
    public static (IReadOnlyList<string> Words, bool Escalated) Words(CharacterTouchTemperament temperament, TouchTemperamentEntry entry, int repeats)
    {
        var words = entry.Reactions ?? DefaultReactions(entry.Attitude);
        var escalation = repeats >= Math.Max(MinimumAfter, temperament.Escalation.After)
            ? entry.Attitude <= -1 ? temperament.Escalation.Disliked : entry.Attitude >= 2 ? temperament.Escalation.Loved : null
            : null;
        if (escalation is not { Count: > 0 }) return (words.Take(MaximumReactions).ToArray(), false);
        return (escalation.Concat(words).Distinct(StringComparer.Ordinal).Take(MaximumReactions).ToArray(), true);
    }

    /// <summary>The model's emotes and motions for abstract reactions: for each word, the model's own expression (then motion)
    /// whose tag or name matches it first, else Martlet's gesture of that name, if in use. Words the model has neither for are
    /// left out.</summary>
    public static IReadOnlyList<CharacterActionSource> Resolve(IEnumerable<string> words, CharacterActionCatalog? catalog)
    {
        if (catalog is null) return [];
        var entries = catalog.Entries.Where(e => e.Action.Enabled).ToArray();
        var plan = new List<CharacterActionSource>();
        foreach (var word in words)
        {
            var names = ModelNames.TryGetValue(word, out var own) ? own : [word];
            var hit = names.SelectMany(name => entries.Where(e => e.Source.Kind != CharacterActionKind.Gesture && Own(e, name))
                    .OrderBy(e => e.Source.Kind == CharacterActionKind.Expression ? 0 : 1))
                .Select(e => e.Source).FirstOrDefault(s => !plan.Contains(s))
                ?? entries.Where(e => e.Source.Kind == CharacterActionKind.Gesture && string.Equals(e.Source.Name, word, StringComparison.OrdinalIgnoreCase))
                    .Select(e => e.Source).FirstOrDefault(s => !plan.Contains(s));
            if (hit is not null) plan.Add(hit);
            if (plan.Count >= MaximumReactions) break;
        }
        return plan;
    }

    private static bool Own((CharacterActionSource Source, CharacterAction Action) entry, string name) =>
        string.Equals(entry.Action.Tag, name, StringComparison.OrdinalIgnoreCase) || CharacterActions.Slug(entry.Source.Name) == name;

    // ---------- the Thinking model's request and answer ----------

    /// <summary>What the Thinking model is asked: how the character physically reacts to touch and where its eyes usually go,
    /// from its personality, as JSON.</summary>
    public static string DecisionInstructions =>
        "You decide how a character physically reacts when the user touches parts of its body on screen, and where its eyes " +
        "usually go, from the character's personality. Decide ACTIONS ONLY: animations, gestures, face overlays and where it " +
        "looks. Never write words, dialogue or narration. Characters differ: some love being touched, some hate it, some like a " +
        "head pat but not a belly touch, some really crave it; some follow the user's every move and others hardly notice them. " +
        "Follow the personality; when it says nothing about a part, choose what fits the character best.\n" +
        "gaze: where the eyes go when nothing else draws them: " + string.Join(", ", GazeHints.Select(h => $"{h.Word} ({h.Hint})")) + ".\n" +
        "attitude: -2 hates, -1 dislikes, 0 neutral, 1 likes, 2 loves, 3 craves (really wants it).\n" +
        "reactions: up to 3 actions from this list ONLY, most important first: " + string.Join(", ", Vocabulary) + ". Write " +
        "[\"" + NoReaction + "\"] when the character doesn't react to that touch at all.\n" +
        "linger: seconds (0 to 10) the first reaction stays on; 0 plays it once.\n" +
        "look: seconds (0 to 10) the character's eyes turn to the user's mouse pointer after that touch, as if to see who did " +
        "it; 0 when they don't.\n" +
        "Give the gaze and all six groups. intimate is the character's intimate parts (" + CharacterTouchZones.IntimateParts + "); " +
        "each other group is the rest of its part of the body. Add zones only where they differ from their group. escalation: " +
        "after that many touches in a row, a disliked zone plays \"disliked\" and a loved zone plays \"loved\" first. Answer with " +
        "JSON only, no other text, in this form:\n" +
        "{\"gaze\":\"mouse\",\"groups\":{\"head\":{\"attitude\":1,\"reactions\":[\"smile\",\"blush\"],\"linger\":0,\"look\":0}," +
        "\"torso\":{...},\"arms\":{...},\"lower_body\":{...},\"extras\":{...},\"intimate\":{...}},\"zones\":{\"stomach\":{\"attitude\":-1," +
        "\"reactions\":[\"pout\",\"sweat\"],\"look\":2}}," +
        "\"escalation\":{\"after\":3,\"disliked\":[\"anger\",\"look_away\"],\"loved\":[\"hearts\",\"blush\"]}}";

    // What each gaze suits, for the Thinking model.
    private static readonly (string Word, string Hint)[] GazeHints =
    [
        ("mouse", "follows the user's mouse pointer everywhere: attentive, curious, clingy"),
        ("near", "looks at the pointer only while it is near the character: calm, easygoing"),
        ("ahead", "looks straight ahead and ignores the pointer: aloof, passive, shy, stoic"),
        ("window", "watches the window the user works in: helpful, focused, a study buddy")
    ];

    /// <summary>The message that goes with <see cref="DecisionInstructions"/>: the personality and the zones by group (every
    /// intimate kind under <see cref="IntimateId"/>).</summary>
    public static string DecisionRequest(string personality) =>
        "Personality:\n" + personality.Trim() + "\n\nZones by group (id - what):\n" + string.Join("\n", GroupIds.Select(g =>
            $"{g.Id}: " + string.Join(", ", KindsIn(g.Id).Select(k => $"{k.Id} ({k.Label.ToLowerInvariant()})"))));

    /// <summary>The temperament in the Thinking model's answer, or null when nothing could be read. Unknown groups, zones and
    /// actions are left out, attitudes and lingering are clamped, and an entry whose actions were all unknown plays its
    /// attitude's defaults.</summary>
    public static CharacterTouchTemperament? Parse(string? answer, Guid personaId, string? digest, string source, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(answer)) return null;
        var start = answer.IndexOf('{');
        var end = answer.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        JsonDocument document;
        try { document = JsonDocument.Parse(answer[start..(end + 1)], new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 16 }); }
        catch (JsonException) { return null; }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var groups = new Dictionary<string, TouchTemperamentEntry>(StringComparer.Ordinal);
            var zones = new Dictionary<string, TouchTemperamentEntry>(StringComparer.Ordinal);
            if (Property(root, "groups") is { ValueKind: JsonValueKind.Object } readGroups)
                foreach (var property in readGroups.EnumerateObject())
                    if (Group(property.Name) is { } id && ReadEntry(property.Value) is { } entry) groups.TryAdd(id, entry);
            if (Property(root, "zones") is { ValueKind: JsonValueKind.Object } readZones)
                foreach (var property in readZones.EnumerateObject())
                    if (CharacterTouchZones.Normalize(property.Name) is { } id && ReadEntry(property.Value) is { } entry) zones.TryAdd(id, entry);
            if (groups.Count == 0 && zones.Count == 0) return null;
            var gaze = Property(root, "gaze") is { ValueKind: JsonValueKind.String } word ? CharacterGaze.ModeOf(word.GetString()) : null;
            var escalation = new TouchEscalation();
            if (Property(root, "escalation") is { ValueKind: JsonValueKind.Object } readEscalation)
            {
                var after = Number(Property(readEscalation, "after"));
                var disliked = ReadWords(Property(readEscalation, "disliked"));
                var loved = ReadWords(Property(readEscalation, "loved"));
                escalation = new()
                {
                    After = after is { } n ? (int)Math.Clamp(Math.Round(n), MinimumAfter, MaximumAfter) : escalation.After,
                    Disliked = disliked is { Count: > 0 } ? disliked : escalation.Disliked,
                    Loved = loved is { Count: > 0 } ? loved : escalation.Loved
                };
            }
            return new()
            {
                PersonaId = personaId, Source = source, PersonalityDigest = digest, DecidedAt = now.ToUniversalTime(), UpdatedAt = now.ToUniversalTime(),
                Gaze = gaze, Groups = groups, Zones = zones, Escalation = escalation
            };
        }
    }

    private static string? Group(string name)
    {
        var slug = string.Join("_", name.Trim().ToLowerInvariant().Split([' ', '-', '/', '&'], StringSplitOptions.RemoveEmptyEntries)).Replace("_and_", "_");
        return slug switch
        {
            "head" or "face" or "head_face" => "head",
            "torso" or "body" or "neck_torso" or "shoulders_torso" => "torso",
            "arms" or "arm" or "arms_hands" or "hands" => "arms",
            "lower_body" or "lowerbody" or "legs" or "hips_legs" or "legs_feet" => "lower_body",
            "extras" or "extra" => "extras",
            "intimate" or "intimate_parts" or "intimate_zones" or "erogenous" or "erogenous_zones" or "erogenous_parts" or "private" or
                "private_parts" or "sensitive" or "sensitive_parts" or "sensitive_zones" => IntimateId,
            _ => null
        };
    }

    private static TouchTemperamentEntry? ReadEntry(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        int? attitude = null;
        var value = Property(element, "attitude");
        if (Number(value) is { } number) attitude = (int)Math.Clamp(Math.Round(number), MinimumAttitude, MaximumAttitude);
        else if (value is { ValueKind: JsonValueKind.String } text && AttitudeOf(text.GetString()) is { } word) attitude = word;
        if (attitude is null) return null;
        var reactions = ReadReactions(Property(element, "reactions") ?? Property(element, "actions"));
        var linger = Number(Property(element, "linger") ?? Property(element, "linger_seconds")) ?? 0;
        var look = Number(Property(element, "look") ?? Property(element, "look_seconds") ?? Property(element, "looks")) ?? 0;
        return new()
        {
            Attitude = attitude.Value, Reactions = reactions,
            LingerSeconds = double.IsFinite(linger) ? Math.Clamp(linger, 0, MaximumLinger) : 0,
            LookSeconds = double.IsFinite(look) ? Math.Clamp(look, 0, MaximumLook) : 0
        };
    }

    /// <summary>An entry's reactions: the known words (in order, at most <see cref="MaximumReactions"/>), an empty list for no
    /// reaction (<see cref="NoReaction"/>, or an empty list), or null for the attitude's defaults (missing, or only unknown words).</summary>
    private static IReadOnlyList<string>? ReadReactions(JsonElement? element)
    {
        if (element is { ValueKind: JsonValueKind.String } single)
            return IsNone(single.GetString()) ? [] : Word(single.GetString()) is { } one ? [one] : null;
        if (element is not { ValueKind: JsonValueKind.Array } array) return null;
        if (array.GetArrayLength() == 0) return [];
        var words = ReadWords(element);
        if (words is { Count: > 0 }) return words;
        return array.EnumerateArray().Any(e => e.ValueKind == JsonValueKind.String && IsNone(e.GetString())) ? [] : null;
    }

    private static bool IsNone(string? word) =>
        word?.Trim().Trim('{', '}', '*', '[', ']').ToLowerInvariant().Replace(' ', '_').Replace('-', '_') is
            NoReaction or "nothing" or "no_reaction" or "no_reactions" or "ignore" or "ignores" or "ignore_it" or "no";

    /// <summary>An attitude word (or number as text) as its value, or null.</summary>
    public static int? AttitudeOf(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var word = text.Trim().ToLowerInvariant();
        if (int.TryParse(word, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return Math.Clamp(n, MinimumAttitude, MaximumAttitude);
        var index = AttitudeWords.ToList().IndexOf(word);
        if (index >= 0) return index + MinimumAttitude;
        return word switch { "hate" => -2, "dislike" => -1, "like" => 1, "love" => 2, "crave" or "wants" => 3, _ => null };
    }

    private static IReadOnlyList<string>? ReadWords(JsonElement? element)
    {
        if (element is not { ValueKind: JsonValueKind.Array } array) return null;
        return array.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => Word(e.GetString())).OfType<string>()
            .Distinct(StringComparer.Ordinal).Take(MaximumReactions).ToArray();
    }

    private static double? Number(JsonElement? element) => element switch
    {
        { ValueKind: JsonValueKind.Number } n => n.GetDouble(),
        { ValueKind: JsonValueKind.String } s when double.TryParse(s.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) => v,
        _ => null
    };

    private static JsonElement? Property(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in element.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        return null;
    }

    /// <summary>A short fingerprint of a personality that ignores case, spacing and punctuation, so only a meaningful change
    /// decides the temperament again.</summary>
    public static string Digest(string personality)
    {
        var words = new StringBuilder();
        foreach (var c in personality.ToLowerInvariant())
            if (char.IsLetterOrDigit(c)) words.Append(c);
            else if (words.Length > 0 && words[^1] != ' ') words.Append(' ');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(words.ToString().Trim())))[..16].ToLowerInvariant();
    }

    /// <summary>The temperament per category in words, for status lines ("head likes, torso dislikes, ..., 2 zones of their
    /// own"; intimate parts follow their body groups when the temperament doesn't cover them), then its usual gaze and the parts
    /// whose touch turns the eyes to the mouse pointer.</summary>
    public static string Summary(CharacterTouchTemperament? temperament)
    {
        if (temperament is null) return "built-in reactions for every zone";
        string Line(string id, TouchTemperamentEntry e) => $"{id} {AttitudeWord(e.Attitude)}" + (e.Reactions is { Count: 0 } ? " (no reaction)" : "");
        var groups = GroupIds.Select(g => temperament.Groups.TryGetValue(g.Id, out var e) ? Line(g.Id, e)
            : g.Id == IntimateId ? $"{g.Id} as body groups" : $"{g.Id} built-in");
        var zones = temperament.Zones.OrderBy(z => z.Key, StringComparer.Ordinal).Select(z => Line(z.Key, z.Value)).ToArray();
        var looks = temperament.Groups.Concat(temperament.Zones).Where(e => e.Value.LookSeconds > 0).Select(e => e.Key)
            .Order(StringComparer.Ordinal).ToArray();
        return string.Join(", ", groups) + (zones.Length == 0 ? "" : "; zones: " + string.Join(", ", zones.Take(8)) + (zones.Length > 8 ? $" and {zones.Length - 8} more" : "")) +
            "; eyes: " + (temperament.Gaze is { } gaze ? CharacterGaze.Label(gaze).ToLowerInvariant() : "not decided (follow your mouse)") +
            (looks.Length == 0 ? "" : "; looks at your mouse after touches on " + string.Join(", ", looks.Take(6)) + (looks.Length > 6 ? $" and {looks.Length - 6} more" : ""));
    }

    // ---------- storage ----------

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        MaxDepth = 10
    };

    private static readonly JsonSerializerOptions ShareJson = new(Json) { WriteIndented = false };

    /// <summary>The file's version: 1 kept each persona's own temperament; 2 adds the custom temperaments and which one each
    /// persona uses. Martlet still writes version 1, word for word as before, while nothing needs version 2 (<see cref="NeedsVersion2"/>),
    /// so the text the owner's computers share doesn't change on its own when one of them updates. An older Martlet reports a
    /// version 2 file from another computer as saved by a newer Martlet.</summary>
    public const int FileVersion = 2;

    /// <summary>Whether the temperaments need version 2: a custom temperament, a persona that uses one or the built-in reactions,
    /// or an intimate line (an older Martlet would drop a temperament with a category it doesn't know).</summary>
    public static bool NeedsVersion2(TouchTemperamentSet set) =>
        set.Custom.Count > 0 || set.Uses.Count > 0 || set.Personas.Any(t => t.Groups.ContainsKey(IntimateId));

    private sealed record Document(int Version, IReadOnlyList<CharacterTouchTemperament> Personas, IReadOnlyList<CustomTouchTemperament>? Custom = null,
        IReadOnlyDictionary<Guid, string>? Uses = null);

    public static string Path(string dataDirectory) => System.IO.Path.Combine(dataDirectory, FileName);

    /// <summary>The persona's own temperament (decided from its personality, or edited by the owner), or null.</summary>
    public static CharacterTouchTemperament? Load(string dataDirectory, Guid personaId) => LoadSet(dataDirectory).Own(personaId);

    /// <summary>The temperament the persona uses (<see cref="TouchTemperamentSet.For"/>): the custom one it uses, none for the
    /// built-in reactions, else its own.</summary>
    public static CharacterTouchTemperament? Used(string dataDirectory, Guid personaId) => LoadSet(dataDirectory).For(personaId);

    /// <summary>Every persona's own temperament.</summary>
    public static IReadOnlyList<CharacterTouchTemperament> LoadAll(string dataDirectory) => LoadSet(dataDirectory).Personas;

    /// <summary>Everything character-temperaments.json keeps; empty when it is missing, unreadable or saved by a newer Martlet. A
    /// version 1 file has only the personas' own temperaments.</summary>
    public static TouchTemperamentSet LoadSet(string dataDirectory)
    {
        try
        {
            var path = Path(dataDirectory);
            if (!File.Exists(path) || new FileInfo(path).Length > MaximumBytes) return TouchTemperamentSet.Empty;
            return Valid(JsonSerializer.Deserialize<Document>(File.ReadAllBytes(path), Json)) ?? TouchTemperamentSet.Empty;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return TouchTemperamentSet.Empty;
        }
    }

    // What this Martlet can use of a document, or null when a newer Martlet wrote it. Entries it can't use are left out, and a
    // persona that used a custom temperament that is left out uses its own.
    private static TouchTemperamentSet? Valid(Document? document)
    {
        if (document is not { Version: 1 or FileVersion, Personas: { } personas }) return null;
        var own = personas.Where(t => t is { PersonaId: var id, Groups: not null, Zones: not null, Escalation: not null } && id != Guid.Empty && Problem(t) is null)
            .ToArray();
        if (document.Version == 1) return new() { Personas = own };
        var custom = new List<CustomTouchTemperament>();
        foreach (var made in document.Custom ?? [])
            if (made is { Name: not null, Groups: not null, Zones: not null, Escalation: not null } && Problem(made) is null && custom.Count < MaximumCustom &&
                custom.All(c => c.Id != made.Id && !SameName(c.Name, made.Name)))
                custom.Add(made with { Name = made.Name.Trim() });
        var uses = (document.Uses ?? new Dictionary<Guid, string>())
            .Where(u => u.Key != Guid.Empty && (u.Value == BuiltIn || Guid.TryParse(u.Value, out var id) && custom.Any(c => c.Id == id)))
            .Take(MaximumUses).ToDictionary();
        return new() { Personas = own, Custom = custom, Uses = uses };
    }

    /// <summary>Why <paramref name="temperament"/> (a persona's own) can't be saved, or null.</summary>
    public static string? Problem(CharacterTouchTemperament temperament)
    {
        if (temperament.PersonaId == Guid.Empty) return "The persona is unknown.";
        if (temperament.Source is not (CharacterTouchTemperament.ByThinking or CharacterTouchTemperament.ByOwner or CharacterTouchTemperament.ByFixture))
            return $"\"{temperament.Source}\" isn't who decided a temperament.";
        return Problem(temperament.Gaze, temperament.Groups, temperament.Zones, temperament.Escalation);
    }

    /// <summary>Why the custom temperament can't be saved, or null.</summary>
    public static string? Problem(CustomTouchTemperament custom) =>
        custom.Id == Guid.Empty ? "The custom temperament is unknown."
            : NameProblem(custom.Name) ?? Problem(custom.Gaze, custom.Groups, custom.Zones, custom.Escalation);

    /// <summary>Why <paramref name="name"/> can't name a custom temperament, or null: it needs 1 to <see cref="MaximumNameLength"/>
    /// characters on one line, and it can't be one of the other choices (<see cref="OwnLabel"/>, <see cref="BuiltInLabel"/>).</summary>
    public static string? NameProblem(string? name)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length == 0) return "Give the custom temperament a name.";
        if (trimmed.Length > MaximumNameLength) return $"A custom temperament's name has at most {MaximumNameLength} characters.";
        if (trimmed.Any(char.IsControl)) return "A custom temperament's name is one line of text.";
        if (SameName(trimmed, OwnLabel) || SameName(trimmed, BuiltInLabel)) return $"\"{trimmed}\" is already a choice. Give the custom temperament another name.";
        return null;
    }

    /// <summary>Why the temperaments can't be saved together, or null: each one must be valid, the custom temperaments' names
    /// different (ignoring case), and each persona must use a temperament that is there.</summary>
    public static string? Problem(TouchTemperamentSet set)
    {
        foreach (var temperament in set.Personas)
            if (Problem(temperament) is { } problem) return problem;
        if (set.Custom.Count > MaximumCustom) return $"Martlet keeps at most {MaximumCustom} custom temperaments.";
        for (var i = 0; i < set.Custom.Count; i++)
        {
            if (Problem(set.Custom[i]) is { } problem) return problem;
            for (var j = 0; j < i; j++)
            {
                if (set.Custom[j].Id == set.Custom[i].Id) return "Two custom temperaments have the same ID.";
                if (SameName(set.Custom[j].Name, set.Custom[i].Name)) return $"There is already a custom temperament called \"{set.Custom[i].Name.Trim()}\".";
            }
        }
        if (set.Uses.Count > MaximumUses) return $"At most {MaximumUses} personas can use a temperament other than their own.";
        foreach (var (persona, uses) in set.Uses)
            if (persona == Guid.Empty || uses != BuiltIn && !(Guid.TryParse(uses, out var id) && set.Custom.Any(c => c.Id == id)))
                return "A persona uses a touch temperament Martlet doesn't know.";
        return null;
    }

    private static bool SameName(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string? Problem(GazeMode? gaze, IReadOnlyDictionary<string, TouchTemperamentEntry>? groups,
        IReadOnlyDictionary<string, TouchTemperamentEntry>? zones, TouchEscalation? escalation)
    {
        if (groups is null || zones is null || escalation is null) return "A temperament is incomplete.";
        if (groups.Keys.Any(k => GroupIds.All(g => g.Id != k))) return "A temperament names a group Martlet doesn't know.";
        if (zones.Keys.Any(k => CharacterTouchZones.Kind(k) is null)) return "A temperament names a zone Martlet doesn't know.";
        if (gaze is { } known && !Enum.IsDefined(known)) return "A temperament names a gaze Martlet doesn't know.";
        foreach (var entry in groups.Values.Concat(zones.Values))
        {
            if (entry is null) return "A temperament entry is empty.";
            if (entry.Attitude is < MinimumAttitude or > MaximumAttitude) return $"An attitude must be {MinimumAttitude} to {MaximumAttitude}.";
            if (entry.Reactions is { } reactions && (reactions.Count > MaximumReactions || reactions.Any(r => !Vocabulary.Contains(r))))
                return $"A zone reacts with at most {MaximumReactions} of Martlet's touch reactions.";
            if (!double.IsFinite(entry.LingerSeconds) || entry.LingerSeconds is < 0 or > MaximumLinger) return $"Lingering must be 0 to {MaximumLinger:0} seconds.";
            if (!double.IsFinite(entry.LookSeconds) || entry.LookSeconds is < 0 or > MaximumLook) return $"Looking at the mouse must be 0 to {MaximumLook:0} seconds.";
        }
        var e = escalation;
        if (e.After is < MinimumAfter or > MaximumAfter) return $"Repeated touches escalate after {MinimumAfter} to {MaximumAfter} touches.";
        if (e.Disliked is null || e.Loved is null || e.Disliked.Concat(e.Loved).Any(r => !Vocabulary.Contains(r)) || e.Disliked.Count > MaximumReactions ||
            e.Loved.Count > MaximumReactions)
            return "Escalation reacts with Martlet's touch reactions only.";
        return null;
    }

    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Changes the temperaments in one step: <paramref name="change"/> gets what character-temperaments.json holds now, and
    /// what it returns is checked (a problem throws <see cref="ContractException"/> and nothing is written) and saved. Returns what
    /// was saved.</summary>
    public static async Task<TouchTemperamentSet> UpdateAsync(string dataDirectory, Func<TouchTemperamentSet, TouchTemperamentSet> change,
        CancellationToken token = default)
    {
        await Gate.WaitAsync(token);
        try
        {
            var next = change(LoadSet(dataDirectory));
            if (Problem(next) is { } problem) throw new ContractException(ErrorCode.InvalidContract, problem);
            await WriteAsync(dataDirectory, next, token);
            return next;
        }
        finally { Gate.Release(); }
    }

    /// <summary>Saves a persona's own temperament.</summary>
    public static async Task<CharacterTouchTemperament> SaveAsync(string dataDirectory, CharacterTouchTemperament temperament, DateTimeOffset now,
        CancellationToken token = default)
    {
        if (Problem(temperament) is { } problem) throw new ContractException(ErrorCode.InvalidContract, problem);
        temperament = temperament with { UpdatedAt = now.ToUniversalTime() };
        await UpdateAsync(dataDirectory, set => set.WithOwn(temperament), token);
        return temperament;
    }

    /// <summary>Forgets a persona's own temperament, so its zones play their built-in reactions again (unless it uses a custom one).</summary>
    public static Task RemoveAsync(string dataDirectory, Guid personaId, CancellationToken token = default) =>
        UpdateAsync(dataDirectory, set => set.WithoutOwn(personaId), token);

    private static async Task WriteAsync(string dataDirectory, TouchTemperamentSet set, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(ToDocument(set), Json);
        ContractRules.Require(bytes.Length <= MaximumBytes, "The touch temperaments are too large.", ErrorCode.PayloadTooLarge);
        Directory.CreateDirectory(dataDirectory);
        var temporary = System.IO.Path.Combine(dataDirectory, $"character-temperaments.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, token);
            File.Move(temporary, Path(dataDirectory), overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    // Sorted, so the same temperaments always write the same text; version 1 (without custom and uses) while nothing needs 2.
    private static Document ToDocument(TouchTemperamentSet set) => NeedsVersion2(set)
        ? new(FileVersion, [.. set.Personas.OrderBy(t => t.PersonaId)], [.. set.Custom.OrderBy(c => c.Id)], set.Uses.OrderBy(u => u.Key).ToDictionary())
        : new(1, [.. set.Personas.OrderBy(t => t.PersonaId)]);

    /// <summary>Every persona's own temperament, the custom temperaments and which one each persona uses, as the owner's computers
    /// share them (compact, sorted).</summary>
    public static string Share(string dataDirectory) => JsonSerializer.Serialize(ToDocument(LoadSet(dataDirectory)), ShareJson);

    public static bool HasAny(string dataDirectory) =>
        LoadSet(dataDirectory) is var set && (set.Personas.Count > 0 || set.Custom.Count > 0 || set.Uses.Count > 0);

    /// <summary>Replaces this computer's temperaments with <paramref name="shared"/> (another computer's <see cref="Share"/>), all
    /// of them, so this computer then shares the same text: the newest change wins, as for every shared setting. Throws
    /// <see cref="ContractException"/> when they are unreadable here (written by a newer Martlet).</summary>
    public static async Task ReplaceAllAsync(string dataDirectory, string shared, CancellationToken token = default)
    {
        TouchTemperamentSet? set;
        try { set = Valid(JsonSerializer.Deserialize<Document>(shared, Json)); }
        catch (Exception error) when (error is JsonException or NotSupportedException)
        {
            throw new ContractException(ErrorCode.UnsupportedVersion, "They were saved by a newer Martlet. Update this PC to use them.");
        }
        ContractRules.Require(set is not null, "They were saved by a newer Martlet. Update this PC to use them.", ErrorCode.UnsupportedVersion);
        await Gate.WaitAsync(token);
        try { await WriteAsync(dataDirectory, set! with { Personas = set.Personas.Take(MaximumPersonas).ToArray() }, token); }
        finally { Gate.Release(); }
    }
}
