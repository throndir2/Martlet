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
/// MARTLET_TOUCH_TEMPERAMENT_FIXTURE file stood in for it) or edited by the owner (<see cref="ByOwner"/>). Zone kinds
/// (<see cref="Zones"/>) win over their group (<see cref="Groups"/>); a zone with neither keeps its built-in default reaction.
/// <see cref="PersonalityDigest"/> is the <see cref="CharacterTouchTemperaments.Digest"/> of the personality it was decided from.</summary>
public sealed record CharacterTouchTemperament
{
    public const string ByThinking = "thinking", ByOwner = "owner", ByFixture = "fixture";
    public required Guid PersonaId { get; init; }
    public string Source { get; init; } = ByThinking;
    public string? PersonalityDigest { get; init; }
    public DateTimeOffset? DecidedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public GazeMode? Gaze { get; init; }
    public IReadOnlyDictionary<string, TouchTemperamentEntry> Groups { get; init; } = new Dictionary<string, TouchTemperamentEntry>();
    public IReadOnlyDictionary<string, TouchTemperamentEntry> Zones { get; init; } = new Dictionary<string, TouchTemperamentEntry>();
    public TouchEscalation Escalation { get; init; } = new();
}

/// <summary>What a touch on a zone plays: the model's emotes, motions and gestures, how long the first one lingers, the
/// persona's attitude word for the zone (null without a temperament), where the reaction came from (<c>owner</c>: the zone's
/// own pick, <c>temperament</c>: the persona's temperament, <c>default</c>: the zone's built-in reaction), whether repeated
/// touches escalated it, and how many seconds the character looks at the mouse pointer after it (the temperament's; 0: it
/// doesn't).</summary>
public sealed record TouchReactionPlan(IReadOnlyList<CharacterActionSource> Actions, double LingerSeconds = 0, string? Attitude = null,
    string From = TouchReactionPlan.FromDefault, bool Escalated = false, double LookSeconds = 0)
{
    public const string FromOwner = "owner", FromTemperament = "temperament", FromDefault = "default";
}

/// <summary>Touch temperaments: the vocabulary of abstract reactions, the Thinking model's request and how its answer is read,
/// turning a temperament into what a touch plays on a model, and the per-persona temperaments kept in
/// character-temperaments.json (shared between the owner's computers, like the personas).</summary>
public static class CharacterTouchTemperaments
{
    public const string FileName = "character-temperaments.json";
    public const int MinimumAttitude = -2, MaximumAttitude = 3, MaximumReactions = 3, MaximumPersonas = 32, MaximumBytes = 1024 * 1024;
    public const int MinimumAfter = 2, MaximumAfter = 20;
    public const double MaximumLinger = 15, RepeatWindowSeconds = 30, MaximumLook = CharacterGaze.MaximumAttention;
    /// <summary>The word a temperament writes for a part the character doesn't react to at all.</summary>
    public const string NoReaction = "none";

    /// <summary>The attitude words, from <see cref="MinimumAttitude"/> up.</summary>
    public static readonly IReadOnlyList<string> AttitudeWords = ["hates", "dislikes", "neutral", "likes", "loves", "craves"];

    /// <summary>Zone groups as the temperament names them.</summary>
    public static readonly IReadOnlyList<(TouchZoneGroup Group, string Id, string Label)> GroupIds =
    [
        (TouchZoneGroup.Head, "head", "Head and face"), (TouchZoneGroup.Torso, "torso", "Neck and torso"), (TouchZoneGroup.Arms, "arms", "Arms and hands"),
        (TouchZoneGroup.LowerBody, "lower_body", "Hips and legs"), (TouchZoneGroup.Extras, "extras", "Extras (ears, tail, wings...)")
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

    /// <summary>A vocabulary word as Martlet knows it (lower case, underscores, common names mapped), or null when it isn't one.</summary>
    public static string? Word(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var slug = string.Join("_", text.Trim().Trim('{', '}', '*', '[', ']').ToLowerInvariant()
            .Split([' ', '-', '/', '.', ','], StringSplitOptions.RemoveEmptyEntries));
        if (Aliases.TryGetValue(slug, out var alias)) slug = alias;
        return Vocabulary.Contains(slug) ? slug : null;
    }

    /// <summary>The temperament's entry for a zone kind: the zone's own, else its group's, else null (the built-in default).</summary>
    public static TouchTemperamentEntry? Entry(CharacterTouchTemperament? temperament, string zoneId)
    {
        if (temperament is null) return null;
        if (temperament.Zones.TryGetValue(zoneId, out var own)) return own;
        return CharacterTouchZones.Kind(zoneId) is { } kind && temperament.Groups.TryGetValue(GroupId(kind.Group), out var group) ? group : null;
    }

    /// <summary>The persona's attitude to a zone in one word ("loves", "hates"), or null when the temperament doesn't cover it.
    /// Data for others to use (such as a touch summary); nothing here puts it in a prompt.</summary>
    public static string? Attitude(CharacterTouchTemperament? temperament, string zoneId) =>
        Entry(temperament, zoneId) is { } entry ? AttitudeWord(entry.Attitude) : null;

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
        "Give the gaze and all five groups. Add zones only where they differ from their group. escalation: after that many " +
        "touches in a row, a disliked zone plays \"disliked\" and a loved zone plays \"loved\" first. Answer with JSON only, no " +
        "other text, in this form:\n" +
        "{\"gaze\":\"mouse\",\"groups\":{\"head\":{\"attitude\":1,\"reactions\":[\"smile\",\"blush\"],\"linger\":0,\"look\":0}," +
        "\"torso\":{...},\"arms\":{...},\"lower_body\":{...},\"extras\":{...}},\"zones\":{\"stomach\":{\"attitude\":-1," +
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

    /// <summary>The message that goes with <see cref="DecisionInstructions"/>: the personality and the zones by group.</summary>
    public static string DecisionRequest(string personality) =>
        "Personality:\n" + personality.Trim() + "\n\nZones by group (id - what):\n" + string.Join("\n", GroupIds.Select(g =>
            $"{g.Id}: " + string.Join(", ", CharacterTouchZones.Kinds.Where(k => k.Group == g.Group).Select(k => $"{k.Id} ({k.Label.ToLowerInvariant()})"))));

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
        var slug = string.Join("_", name.Trim().ToLowerInvariant().Split([' ', '-', '/'], StringSplitOptions.RemoveEmptyEntries));
        return slug switch
        {
            "head" or "face" or "head_face" => "head",
            "torso" or "body" or "neck_torso" => "torso",
            "arms" or "arm" or "arms_hands" or "hands" => "arms",
            "lower_body" or "lowerbody" or "legs" or "hips_legs" => "lower_body",
            "extras" or "extra" => "extras",
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

    /// <summary>The temperament per group in words, for status lines ("head likes, torso dislikes, ..., 2 zones of their own"),
    /// then its usual gaze and the parts whose touch turns the eyes to the mouse pointer.</summary>
    public static string Summary(CharacterTouchTemperament? temperament)
    {
        if (temperament is null) return "built-in reactions for every zone";
        string Line(string id, TouchTemperamentEntry e) => $"{id} {AttitudeWord(e.Attitude)}" + (e.Reactions is { Count: 0 } ? " (no reaction)" : "");
        var groups = GroupIds.Select(g => temperament.Groups.TryGetValue(g.Id, out var e) ? Line(g.Id, e) : $"{g.Id} built-in");
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

    private sealed record Document(int Version, IReadOnlyList<CharacterTouchTemperament> Personas);

    public static string Path(string dataDirectory) => System.IO.Path.Combine(dataDirectory, FileName);

    public static CharacterTouchTemperament? Load(string dataDirectory, Guid personaId) => LoadAll(dataDirectory).FirstOrDefault(t => t.PersonaId == personaId);

    public static IReadOnlyList<CharacterTouchTemperament> LoadAll(string dataDirectory)
    {
        try
        {
            var path = Path(dataDirectory);
            if (!File.Exists(path) || new FileInfo(path).Length > MaximumBytes) return [];
            return Valid(JsonSerializer.Deserialize<Document>(File.ReadAllBytes(path), Json)) ?? [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return []; }
    }

    private static IReadOnlyList<CharacterTouchTemperament>? Valid(Document? document) =>
        document is { Version: 1, Personas: { } personas }
            ? personas.Where(t => t is { PersonaId: var id, Groups: not null, Zones: not null, Escalation: not null } && id != Guid.Empty && Problem(t) is null).ToArray()
            : null;

    /// <summary>Why <paramref name="temperament"/> can't be saved, or null.</summary>
    public static string? Problem(CharacterTouchTemperament temperament)
    {
        if (temperament.PersonaId == Guid.Empty) return "The persona is unknown.";
        if (temperament.Source is not (CharacterTouchTemperament.ByThinking or CharacterTouchTemperament.ByOwner or CharacterTouchTemperament.ByFixture))
            return $"\"{temperament.Source}\" isn't who decided a temperament.";
        if (temperament.Groups.Keys.Any(k => GroupIds.All(g => g.Id != k))) return "A temperament names a group Martlet doesn't know.";
        if (temperament.Zones.Keys.Any(k => CharacterTouchZones.Kind(k) is null)) return "A temperament names a zone Martlet doesn't know.";
        if (temperament.Gaze is { } gaze && !Enum.IsDefined(gaze)) return "A temperament names a gaze Martlet doesn't know.";
        foreach (var entry in temperament.Groups.Values.Concat(temperament.Zones.Values))
        {
            if (entry is null) return "A temperament entry is empty.";
            if (entry.Attitude is < MinimumAttitude or > MaximumAttitude) return $"An attitude must be {MinimumAttitude} to {MaximumAttitude}.";
            if (entry.Reactions is { } reactions && (reactions.Count > MaximumReactions || reactions.Any(r => !Vocabulary.Contains(r))))
                return $"A zone reacts with at most {MaximumReactions} of Martlet's touch reactions.";
            if (!double.IsFinite(entry.LingerSeconds) || entry.LingerSeconds is < 0 or > MaximumLinger) return $"Lingering must be 0 to {MaximumLinger:0} seconds.";
            if (!double.IsFinite(entry.LookSeconds) || entry.LookSeconds is < 0 or > MaximumLook) return $"Looking at the mouse must be 0 to {MaximumLook:0} seconds.";
        }
        var e = temperament.Escalation;
        if (e.After is < MinimumAfter or > MaximumAfter) return $"Repeated touches escalate after {MinimumAfter} to {MaximumAfter} touches.";
        if (e.Disliked is null || e.Loved is null || e.Disliked.Concat(e.Loved).Any(r => !Vocabulary.Contains(r)) || e.Disliked.Count > MaximumReactions ||
            e.Loved.Count > MaximumReactions)
            return "Escalation reacts with Martlet's touch reactions only.";
        return null;
    }

    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task<CharacterTouchTemperament> SaveAsync(string dataDirectory, CharacterTouchTemperament temperament, DateTimeOffset now,
        CancellationToken token = default)
    {
        if (Problem(temperament) is { } problem) throw new ContractException(ErrorCode.InvalidContract, problem);
        temperament = temperament with { UpdatedAt = now.ToUniversalTime() };
        await Gate.WaitAsync(token);
        try
        {
            var personas = LoadAll(dataDirectory).Where(t => t.PersonaId != temperament.PersonaId).Append(temperament)
                .OrderByDescending(t => t.UpdatedAt).Take(MaximumPersonas).ToArray();
            await WriteAsync(dataDirectory, personas, token);
            return temperament;
        }
        finally { Gate.Release(); }
    }

    /// <summary>Forgets a persona's temperament, so its zones play their built-in reactions again.</summary>
    public static async Task RemoveAsync(string dataDirectory, Guid personaId, CancellationToken token = default)
    {
        await Gate.WaitAsync(token);
        try { await WriteAsync(dataDirectory, LoadAll(dataDirectory).Where(t => t.PersonaId != personaId).ToArray(), token); }
        finally { Gate.Release(); }
    }

    private static async Task WriteAsync(string dataDirectory, IReadOnlyList<CharacterTouchTemperament> personas, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Document(1, [.. personas.OrderBy(t => t.PersonaId)]), Json);
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

    /// <summary>Every persona's temperament as the owner's computers share them (compact, sorted by persona).</summary>
    public static string Share(string dataDirectory) =>
        JsonSerializer.Serialize(new Document(1, [.. LoadAll(dataDirectory).OrderBy(t => t.PersonaId)]), ShareJson);

    public static bool HasAny(string dataDirectory) => LoadAll(dataDirectory).Count > 0;

    /// <summary>Replaces this computer's temperaments with <paramref name="shared"/> (another computer's <see cref="Share"/>).
    /// Throws <see cref="ContractException"/> when they are unreadable here (written by a newer Martlet).</summary>
    public static async Task ReplaceAllAsync(string dataDirectory, string shared, CancellationToken token = default)
    {
        IReadOnlyList<CharacterTouchTemperament>? personas;
        try { personas = Valid(JsonSerializer.Deserialize<Document>(shared, Json)); }
        catch (Exception error) when (error is JsonException or NotSupportedException)
        {
            throw new ContractException(ErrorCode.UnsupportedVersion, "They were saved by a newer Martlet. Update this PC to use them.");
        }
        ContractRules.Require(personas is not null, "They were saved by a newer Martlet. Update this PC to use them.", ErrorCode.UnsupportedVersion);
        await Gate.WaitAsync(token);
        try { await WriteAsync(dataDirectory, personas!.Take(MaximumPersonas).ToArray(), token); }
        finally { Gate.Release(); }
    }
}
