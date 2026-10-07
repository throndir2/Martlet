using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Martlet.Avatars;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;

namespace Martlet.Avatar.Hosting;

/// <summary>How Martlet uses one emote or motion: the tag replies write for it (<c>blush</c> is written <c>{blush}</c>), when
/// to use it (for the Thinking model), the voice cue that also sets it off (<see cref="VoiceTag.Cue"/>, such as
/// <c>laugh</c>), whether it is used at all and its <see cref="Mode"/>: <see cref="CharacterActions.Brief"/> (shows a moment)
/// or <see cref="CharacterActions.Lingering"/> (stays on until a reply writes <c>{/blush}</c>); null for the default
/// (<see cref="CharacterActions.DefaultMode"/>), as in files saved before modes existed.</summary>
public sealed record CharacterAction
{
    public required string Id { get; init; }
    public string? Tag { get; init; }
    public string? Use { get; init; }
    public string? Cue { get; init; }
    public bool Enabled { get; init; } = true;
    public string? Mode { get; init; }
}

/// <summary>One model's emote and motion settings (Companion › Character › Emotes and motions). <see cref="DetectedBy"/> is
/// <c>thinking</c> once the Thinking model named them, otherwise <c>names</c> (made from the model's own names).</summary>
public sealed record CharacterActionSettings
{
    public const string ByNames = "names", ByThinking = "thinking";
    public required string ModelId { get; init; }
    public string DetectedBy { get; init; } = ByNames;
    public DateTimeOffset? DetectedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public required IReadOnlyList<CharacterAction> Actions { get; init; }

    public CharacterAction? Find(string id) => Actions.FirstOrDefault(a => a.Id == id);
}

/// <summary>A model's emotes and motions with how Martlet uses them: what the reply model is offered, what a reply's tags and
/// the voice's cues set off, and what the owner sees.</summary>
public sealed record CharacterActionCatalog(CharacterActionInventory Inventory, CharacterActionSettings Settings)
{
    public const int MaximumTagLength = 24;
    public const int MaximumUseLength = 120;

    public IEnumerable<(CharacterActionSource Source, CharacterAction Action)> Entries =>
        Inventory.Sources.Select(source => (source, Settings.Find(source.Id) ??
            CharacterActions.Default(source, CharacterActions.Number(Inventory, source))));

    /// <summary>The emotes and motions a reply may write as tags: those turned on that the speaking voice doesn't already set
    /// off with its own tag for their cue (null <paramref name="engine"/>: a reply that isn't spoken, so all of them), except
    /// Martlet's voice emotes while they keep a cue (they follow the voice only).</summary>
    public IReadOnlyList<(CharacterActionSource Source, CharacterAction Action)> Offered(SpeechEngine? engine) =>
        Entries.Where(e => e.Action is { Enabled: true, Tag: { Length: > 0 } } &&
            !(e.Action.Cue is { } cue && (engine?.Tags.Any(t => t.Cue == cue) == true ||
                CharacterActionInventory.Gesture(e.Source.Id) is { VoiceOnly: true }))).ToArray();

    /// <summary>What a reply's tag (<c>{blush}</c>, or a voice tag such as <c>[laugh]</c> through its cue) sets off. A cue
    /// several emotes follow plays one of its expressions and one of its motions, picked at random, and its gestures.</summary>
    public IReadOnlyList<CharacterActionSource> For(string tag)
    {
        if (tag.Length > 2 && tag[0] == '{' && tag[^1] == '}')
        {
            var name = tag[1..^1];
            return Entries.Where(e => e.Action is { Enabled: true } && string.Equals(e.Action.Tag, name, StringComparison.OrdinalIgnoreCase))
                .Select(e => e.Source).Take(1).ToArray();
        }
        var cue = VoiceTags.CueOf(tag);
        if (cue is null) return [];
        return Entries.Where(e => e.Action is { Enabled: true } && e.Action.Cue == cue).Select(e => e.Source)
            .GroupBy(s => s.Kind).SelectMany(kind => kind.Key == CharacterActionKind.Gesture ? kind.Take(1)
                : kind.Skip(Random.Shared.Next(kind.Count())).Take(1)).ToArray();
    }

    /// <summary>Whether the emote or motion <paramref name="source"/> of this model is turned on and lingers (stays on until
    /// turned off).</summary>
    public bool Lingers(CharacterActionSource source) =>
        Entries.FirstOrDefault(e => e.Source.Id == source.Id) is { Action: { Enabled: true } } entry && CharacterActions.Lingers(entry.Source, entry.Action);

    /// <summary>The lingering emote a reply's off tag (<c>{/blush}</c>) turns off, or null.</summary>
    public CharacterActionSource? Off(string tag) =>
        CharacterActions.OffTagName(tag) is { } name
            ? Entries.Where(e => e.Action is { Enabled: true } && string.Equals(e.Action.Tag, name, StringComparison.OrdinalIgnoreCase) &&
                CharacterActions.Lingers(e.Source, e.Action)).Select(e => e.Source).FirstOrDefault()
            : null;

    /// <summary>The most character tags one reply may be given (the conversation's limit).</summary>
    public const int MaximumTags = 128;

    /// <summary>The reply instructions (Companion › Prompts › Character emotes and motions) and the tags they offer, or null
    /// when none are offered or the owner emptied the prompt. A lingering emote's line says it stays on until its off tag
    /// (<c>{/blush}</c>), which is offered too. <paramref name="showing"/> are the lingering emotes the character shows now: they
    /// become <see cref="CharacterActionPrompt.Showing"/>, a short note for the newest message (never the instructions, so the
    /// request's start stays the same and prompt caches keep working).</summary>
    public CharacterActionPrompt? Prompt(SpeechEngine? engine, PromptSettings? prompts, IReadOnlyList<HeldEmote>? showing = null,
        DateTimeOffset now = default)
    {
        var offered = Offered(engine);
        if (offered.Count == 0) return null;
        var tags = offered.Select(e => "{" + e.Action.Tag + "}").ToList();
        var example = tags[0];
        var lines = offered.Select(e => $"{{{e.Action.Tag}}} - {CharacterActions.Hint(e.Source, e.Action)}" +
            (CharacterActions.Lingers(e.Source, e.Action) ? $" (stays on until you write {{/{e.Action.Tag}}})" : ""));
        var text = PromptSettings.Fill(prompts, PromptCatalog.CharacterActions, ("tags", string.Join("\n", lines)), ("example", example));
        if (text is null) return null;
        var lingering = offered.Where(e => CharacterActions.Lingers(e.Source, e.Action)).ToArray();
        var held = (showing ?? []).Select(h => (Held: h, Tag: lingering.FirstOrDefault(e => e.Source.Id == h.Source.Id).Action?.Tag))
            .Where(h => h.Tag is not null).ToArray();
        // The off tags of what shows now come first, so they always fit.
        foreach (var tag in held.Select(h => h.Tag!).Concat(lingering.Select(e => e.Action.Tag!)).Distinct(StringComparer.OrdinalIgnoreCase))
            if (tags.Count < MaximumTags) tags.Add("{/" + tag + "}");
        var note = held.Length == 0 ? null : PromptSettings.Fill(prompts, PromptCatalog.CharacterShowing,
            ("showing", string.Join(", ", held.Select(h => $"{{{h.Tag}}} ({CharacterActions.Age(now - h.Held.Since)})"))),
            ("example", "{/" + held[0].Tag + "}"));
        return new(text, tags, note);
    }
}

/// <summary>The reply instructions for the character's emotes and motions, the tags a reply may write (on and off tags), and
/// <see cref="Showing"/>: what lingering emotes show now, for the newest message's notes (null when none show). With the
/// character's gaze joined in, <see cref="Looking"/> is where its eyes are while a reply's choice holds them (null when they do
/// their usual), a note of its own.</summary>
public sealed record CharacterActionPrompt(string Instructions, IReadOnlyList<string> Tags, string? Showing = null, string? Looking = null);

/// <summary>Default settings, the owner's edits and the Thinking model's naming of a model's emotes and motions, kept per
/// model in character-actions.json on this PC.</summary>
public static partial class CharacterActions
{
    public const string FileName = "character-actions.json";
    public const int MaximumModels = 32;
    public const int MaximumBytes = 2 * 1024 * 1024;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        MaxDepth = 8
    };

    private sealed record Document(int Version, IReadOnlyList<CharacterActionSettings> Models);

    private static readonly Dictionary<string, string> CueWords = new(StringComparer.Ordinal)
    {
        ["happy"] = "happy", ["joy"] = "happy", ["fun"] = "happy", ["smile"] = "happy", ["glad"] = "happy",
        ["laugh"] = "laugh", ["laughing"] = "laugh", ["chuckle"] = "chuckle", ["giggle"] = "chuckle",
        ["sad"] = "crying", ["sorrow"] = "crying", ["cry"] = "crying", ["crying"] = "crying", ["tear"] = "crying", ["tears"] = "crying",
        ["angry"] = "angry", ["anger"] = "angry", ["mad"] = "angry", ["rage"] = "angry",
        ["surprised"] = "surprised", ["surprise"] = "surprised", ["shock"] = "gasp", ["shocked"] = "gasp", ["gasp"] = "gasp",
        ["fear"] = "fear", ["scared"] = "fear", ["afraid"] = "fear", ["sigh"] = "sigh", ["tired"] = "sigh",
        ["whisper"] = "whispering", ["sarcastic"] = "sarcastic", ["smug"] = "sarcastic"
    };

    /// <summary>An English tag made from a name: lower case ASCII letters and digits joined by underscores, at most
    /// <see cref="CharacterActionCatalog.MaximumTagLength"/> characters (<c>starEyes</c> becomes <c>star_eyes</c>). Any other
    /// script is left out, so a name like <c>脸红</c> gives an empty tag; <see cref="EnglishTag"/> translates common ones.</summary>
    public static string Slug(string name)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < name.Length && builder.Length < CharacterActionCatalog.MaximumTagLength; i++)
        {
            var c = name[i];
            if (char.IsAsciiLetterOrDigit(c))
            {
                if (char.IsAsciiLetterUpper(c) && i > 0 && char.IsAsciiLetterLower(name[i - 1]) && builder.Length > 0 && builder[^1] != '_')
                    builder.Append('_');
                builder.Append(char.ToLowerInvariant(c));
            }
            else if (builder.Length > 0 && builder[^1] != '_') builder.Append('_');
        }
        return builder.ToString().Trim('_');
    }

    // Common emote words in Chinese, Japanese and Korean model names, so a model named in another language still gets English
    // tags before the Thinking model names it. Longer words are tried first.
    private static readonly (string Word, string Tag)[] Translations = new (string, string)[]
    {
        ("星星眼", "star_eyes"), ("爱心眼", "heart_eyes"), ("愛心眼", "heart_eyes"), ("白眼", "eye_roll"), ("脸红", "blush"),
        ("臉紅", "blush"), ("脸黑", "gloomy"), ("黑脸", "gloomy"), ("生气", "angry"), ("生氣", "angry"), ("害羞", "shy"),
        ("开心", "happy"), ("開心", "happy"), ("高兴", "happy"), ("微笑", "smile"), ("惊讶", "surprised"), ("驚訝", "surprised"),
        ("难过", "sad"), ("伤心", "sad"), ("哭", "crying"), ("泪", "tears"), ("淚", "tears"), ("眼泪", "tears"), ("流汗", "sweat"),
        ("汗", "sweat"), ("晕", "dizzy"), ("疑问", "confused"), ("问号", "confused"), ("吐舌", "tongue_out"), ("嘟嘴", "pout"),
        ("鼓脸", "puffed_cheeks"), ("闭眼", "eyes_closed"), ("眯眼", "squint"), ("右手", "right_hand"), ("左手", "left_hand"),
        ("血", "blood"), ("怒", "angry"), ("笑", "smile"), ("照れ", "blush"), ("赤面", "blush"), ("頬染め", "blush"),
        ("笑顔", "smile"), ("怒り", "angry"), ("涙", "tears"), ("泣き", "crying"), ("驚き", "surprised"), ("悲しみ", "sad"),
        ("ジト目", "squint"), ("ハート", "heart_eyes"), ("キラキラ", "sparkle"), ("ドヤ", "smug"), ("困り", "troubled"),
        ("眠い", "sleepy"), ("웃음", "smile"), ("미소", "smile"), ("화남", "angry"), ("분노", "angry"),
        ("눈물", "tears"), ("울음", "crying"), ("부끄", "blush"), ("홍조", "blush"), ("놀람", "surprised"), ("슬픔", "sad")
    }.OrderByDescending(t => t.Item1.Length).ToArray();

    /// <summary>An English tag for <paramref name="source"/>: its name as English letters, a translation of a common emote
    /// word in it, or <c>emote_3</c>/<c>motion_2</c> (its <paramref name="number"/> among its kind).</summary>
    public static string EnglishTag(CharacterActionSource source, int number)
    {
        if (source.Kind == CharacterActionKind.Gesture && CharacterActionInventory.Gesture(source.Id) is { } gesture) return gesture.Tag;
        var slug = Slug(source.Name);
        if (slug.Length > 0 && slug.Any(char.IsAsciiLetter)) return slug;
        foreach (var (word, tag) in Translations)
            if (source.Name.Contains(word, StringComparison.Ordinal)) return tag;
        return $"{(source.Kind == CharacterActionKind.Motion ? "motion" : "emote")}_{number}" + (slug.Length > 0 ? "_" + slug : "");
    }

    /// <summary>Whether <paramref name="tag"/> can be a tag: 1 to 24 English letters (a-z), digits, underscores or hyphens,
    /// so every Thinking model can write it.</summary>
    public static bool IsTag(string? tag) => tag is { Length: > 0 and <= CharacterActionCatalog.MaximumTagLength } &&
        tag.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-') && tag.Any(char.IsAsciiLetter);

    /// <summary>A <see cref="CharacterAction.Mode"/>: shows a moment (an expression a few seconds, a motion or gesture once).</summary>
    public const string Brief = "brief";
    /// <summary>A <see cref="CharacterAction.Mode"/>: stays on after <c>{tag}</c> until a reply writes <c>{/tag}</c> (or the owner
    /// clears it), like a VTuber's toggle hotkey.</summary>
    public const string Lingering = "lingering";

    // Words in an expression's name or tag that mean a look that stays (a prop, an outfit, a state of the face).
    private static readonly HashSet<string> StateWords = new(StringComparer.Ordinal)
    {
        "glasses", "sunglasses", "goggles", "hat", "cap", "helmet", "crown", "hood", "hoodie", "mask", "eyepatch", "blush", "blushing",
        "angry", "anger", "mad", "sad", "gloomy", "tears", "tear", "crying", "dark", "shadow", "shade", "outfit", "clothes", "costume",
        "uniform", "jacket", "coat", "scarf", "apron", "ribbon", "accessory", "headphones", "headset", "earrings", "necklace", "ears",
        "tail", "wings", "horns", "halo", "hair", "ponytail", "twintails", "hairpin", "microphone", "mic", "item", "prop", "toggle",
        "bandage", "pale"
    };

    /// <summary>The mode an emote or motion gets until someone chooses: an expression a VTube Studio toggle hotkey turns on and
    /// off, or whose name or tag names a look that stays (glasses, a hat, a blush, an angry or sad face, tears, a dark face, an
    /// outfit or accessory), lingers, and so does a Martlet gesture the renderer can hold (<see cref="CharacterGesture.Holdable"/>:
    /// a pout, shyness, looking away, drowsiness); everything else (motions, other gestures) is brief.</summary>
    public static string DefaultMode(CharacterActionSource source, string? tag)
    {
        if (source.Kind == CharacterActionKind.Gesture)
            return CharacterActionInventory.Gesture(source.Id) is { Holdable: true } ? Lingering : Brief;
        if (source.Kind != CharacterActionKind.Expression) return Brief;
        if (source.Toggle) return Lingering;
        var words = Slug(source.Name).Split('_').Concat((tag ?? "").Split('_', '-'));
        return words.Any(StateWords.Contains) ? Lingering : Brief;
    }

    /// <summary>Whether <paramref name="action"/> lingers: its mode, or the default for <paramref name="source"/>.</summary>
    public static bool Lingers(CharacterActionSource source, CharacterAction action) =>
        (action.Mode ?? DefaultMode(source, action.Tag)) == Lingering;

    /// <summary>The tag an off tag (<c>{/blush}</c>) names (<c>blush</c>), or null for any other text.</summary>
    public static string? OffTagName(string text) =>
        text.Length > 3 && text[0] == '{' && text[1] == '/' && text[^1] == '}' && text[2..^1].Trim() is var name && IsTag(name.ToLowerInvariant())
            ? name : null;

    /// <summary>How long something has shown, in a few words for a prompt: "just now", "12 min", "2 h 5 min".</summary>
    public static string Age(TimeSpan age) => age.TotalMinutes < 1 ? "just now"
        : age.TotalHours < 1 ? $"{(int)age.TotalMinutes} min"
        : $"{(int)age.TotalHours} h" + (age.Minutes > 0 ? $" {age.Minutes} min" : "");

    /// <summary>A plain description of an emote or motion for a prompt when nobody said when to use it.</summary>
    public static string Describe(CharacterActionSource source) => source.Kind switch
    {
        CharacterActionKind.Expression => $"the character's emote named \"{source.Name}\"",
        CharacterActionKind.Motion => $"the character's motion named \"{source.Name}\"",
        _ => CharacterActionInventory.Gesture(source.Id)?.Use ?? $"the gesture named \"{source.Name}\""
    };

    /// <summary>The hint the reply prompt gives next to a tag: its When to use text, or <see cref="Describe"/> while that is
    /// empty (the grey text in an empty When to use box).</summary>
    public static string Hint(CharacterActionSource source, CharacterAction action) => action.Use ?? Describe(source);

    /// <summary>Martlet's guess from the model's own name: an English tag and a cue when the name says a feeling.
    /// <paramref name="number"/> is its position among its kind (1 for the first emote), for a name with no English in it.</summary>
    public static CharacterAction Default(CharacterActionSource source, int number = 1)
    {
        var tag = EnglishTag(source, number);
        if (source.Kind == CharacterActionKind.Gesture) return new() { Id = source.Id, Tag = tag, Cue = CharacterActionInventory.Gesture(source.Id)?.Cue };
        var cue = tag.Split('_').Select(word => CueWords.GetValueOrDefault(word)).FirstOrDefault(found => found is not null);
        return new() { Id = source.Id, Tag = tag, Cue = cue is not null && VoiceTags.Cues.Contains(cue) ? cue : null };
    }

    /// <summary>The 1-based position of <paramref name="source"/> among the emotes (or motions) of <paramref name="inventory"/>.</summary>
    public static int Number(CharacterActionInventory inventory, CharacterActionSource source) =>
        inventory.Sources.Where(s => s.Kind == source.Kind).TakeWhile(s => s.Id != source.Id).Count() + 1;

    /// <summary>Settings for every emote and motion in <paramref name="inventory"/>: the saved ones as they were, defaults
    /// for the rest, tags made unique.</summary>
    public static CharacterActionSettings Merge(CharacterActionInventory inventory, CharacterActionSettings? saved)
    {
        var actions = new List<CharacterAction>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in inventory.Sources)
        {
            var action = saved?.Find(source.Id) ?? Default(source, Number(inventory, source));
            if (action.Tag is { } invalid && !IsTag(invalid)) action = action with { Tag = EnglishTag(source, Number(inventory, source)) };
            if (action.Tag is { } tag)
            {
                var unique = tag;
                for (var n = 2; !taken.Add(unique); n++) unique = $"{tag[..Math.Min(tag.Length, CharacterActionCatalog.MaximumTagLength - 3)]}_{n}";
                action = action with { Tag = unique };
            }
            actions.Add(action);
        }
        return new()
        {
            ModelId = inventory.ModelId, DetectedBy = saved?.DetectedBy ?? CharacterActionSettings.ByNames, DetectedAt = saved?.DetectedAt,
            UpdatedAt = saved?.UpdatedAt ?? default, Actions = actions
        };
    }

    /// <summary>Why <paramref name="settings"/> can't be saved, or null.</summary>
    public static string? Problem(CharacterActionSettings settings)
    {
        if (!CharacterModelId(settings.ModelId)) return "The model's ID is invalid.";
        if (settings.Actions.Count > CharacterActionInventory.MaximumSources) return "The model has too many emotes and motions.";
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var action in settings.Actions)
        {
            if (action.Tag is { } tag && !IsTag(tag))
                return $"\"{tag}\" can't be a tag: use up to {CharacterActionCatalog.MaximumTagLength} English letters (a-z), digits, _ or -, " +
                    "so every Thinking model can write it.";
            if (action.Tag is { } used && action.Enabled && !tags.Add(used)) return $"Two emotes or motions use the tag {{{used}}}.";
            if (action.Use is { } use && (use.Length > CharacterActionCatalog.MaximumUseLength || use.Any(char.IsControl)))
                return $"\"When to use\" must be one line of at most {CharacterActionCatalog.MaximumUseLength} characters.";
            if (action.Cue is { } cue && !VoiceTags.Cues.Contains(cue)) return $"\"{cue}\" isn't a voice sound or tone.";
            if (action.Mode is not (null or Brief or Lingering)) return $"\"{action.Mode}\" isn't a mode: use {Brief} or {Lingering}.";
        }
        return null;
    }

    private static bool CharacterModelId(string id) => Martlet.Core.Characters.CharacterModelLibrary.IsSha256(id);

    public static string Path(string dataDirectory) => System.IO.Path.Combine(dataDirectory, FileName);

    /// <summary>The saved settings of the model <paramref name="modelId"/>, or null. A damaged file reads as none.</summary>
    public static CharacterActionSettings? Load(string dataDirectory, string modelId) =>
        LoadAll(dataDirectory).FirstOrDefault(m => m.ModelId == modelId);

    public static IReadOnlyList<CharacterActionSettings> LoadAll(string dataDirectory)
    {
        try
        {
            var path = Path(dataDirectory);
            if (!File.Exists(path) || new FileInfo(path).Length > MaximumBytes) return [];
            var document = JsonSerializer.Deserialize<Document>(File.ReadAllBytes(path), Json);
            return document is { Version: 1, Models: { } models } ? models.Where(m => m is { ModelId: not null, Actions: not null }).ToArray() : [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return []; }
    }

    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static readonly JsonSerializerOptions ShareJson = new(Json) { WriteIndented = false };

    /// <summary>Every model's settings as the owner's computers share them (compact, sorted by model ID), so the emotes and
    /// motions named or edited on one computer are the same on all of them.</summary>
    public static string Share(string dataDirectory) =>
        JsonSerializer.Serialize(new Document(1, [.. LoadAll(dataDirectory).OrderBy(m => m.ModelId, StringComparer.Ordinal)]), ShareJson);

    /// <summary>Whether this computer has any emote and motion settings of its own yet.</summary>
    public static bool HasAny(string dataDirectory) => LoadAll(dataDirectory).Count > 0;

    /// <summary>Replaces this computer's settings with <paramref name="shared"/> (another computer's <see cref="Share"/>).
    /// Throws <see cref="ContractException"/> when they are unreadable here (written by a newer Martlet) or invalid.</summary>
    public static async Task ReplaceAllAsync(string dataDirectory, string shared, CancellationToken token = default)
    {
        Document? document;
        try { document = JsonSerializer.Deserialize<Document>(shared, Json); }
        catch (Exception error) when (error is JsonException or NotSupportedException)
        {
            throw new ContractException(ErrorCode.UnsupportedVersion, "They were saved by a newer Martlet. Update this PC to use them.");
        }
        ContractRules.Require(document is { Version: 1, Models: not null }, "They were saved by a newer Martlet. Update this PC to use them.",
            ErrorCode.UnsupportedVersion);
        var models = document!.Models!.Where(m => m is { ModelId: not null, Actions: not null }).ToArray();
        ContractRules.Require(models.Length <= MaximumModels && models.Select(m => m.ModelId).Distinct(StringComparer.Ordinal).Count() == models.Length,
            "The shared emote and motion settings list too many models.");
        foreach (var model in models)
            if (Problem(model) is { } problem) throw new ContractException(ErrorCode.InvalidContract, problem);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Document(1, [.. models.OrderBy(m => m.ModelId, StringComparer.Ordinal)]), Json);
        ContractRules.Require(bytes.Length <= MaximumBytes, "The emote and motion settings are too large.", ErrorCode.PayloadTooLarge);
        await Gate.WaitAsync(token);
        try
        {
            Directory.CreateDirectory(dataDirectory);
            var temporary = System.IO.Path.Combine(dataDirectory, $"character-actions.{Guid.NewGuid():N}.tmp");
            try
            {
                await File.WriteAllBytesAsync(temporary, bytes, token);
                File.Move(temporary, Path(dataDirectory), overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { Gate.Release(); }
    }

    /// <summary>Saves one model's settings (the newest <see cref="MaximumModels"/> models are kept). Throws
    /// <see cref="ContractException"/> when they can't be saved.</summary>
    public static async Task<CharacterActionSettings> SaveAsync(string dataDirectory, CharacterActionSettings settings, DateTimeOffset now,
        CancellationToken token = default)
    {
        if (Problem(settings) is { } problem) throw new ContractException(ErrorCode.InvalidContract, problem);
        settings = settings with { UpdatedAt = now.ToUniversalTime() };
        await Gate.WaitAsync(token);
        try
        {
            var models = LoadAll(dataDirectory).Where(m => m.ModelId != settings.ModelId).Append(settings)
                .OrderByDescending(m => m.UpdatedAt).Take(MaximumModels).OrderBy(m => m.ModelId, StringComparer.Ordinal).ToArray();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new Document(1, models), Json);
            ContractRules.Require(bytes.Length <= MaximumBytes, "The emote and motion settings are too large.", ErrorCode.PayloadTooLarge);
            Directory.CreateDirectory(dataDirectory);
            var temporary = System.IO.Path.Combine(dataDirectory, $"character-actions.{Guid.NewGuid():N}.tmp");
            try
            {
                await File.WriteAllBytesAsync(temporary, bytes, token);
                File.Move(temporary, Path(dataDirectory), overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return settings;
        }
        finally { Gate.Release(); }
    }

    /// <summary>The emotes and motions the Thinking model is asked to name: the model's own, not Martlet's gestures.</summary>
    public static IReadOnlyList<CharacterActionSource> Nameable(CharacterActionInventory inventory) =>
        inventory.Sources.Where(s => s.Kind != CharacterActionKind.Gesture).ToArray();

    /// <summary>The naming request: instructions (Companion › Prompts › Naming character emotes) and the numbered list.</summary>
    public static (string Instructions, string List)? NamingPrompt(CharacterActionInventory inventory, PromptSettings? prompts)
    {
        var sources = Nameable(inventory);
        if (sources.Count == 0) return null;
        var instructions = PromptSettings.Fill(prompts, PromptCatalog.CharacterActionNaming, ("cues", string.Join(", ", VoiceTags.Cues)));
        if (instructions is null) return null;
        var list = new StringBuilder(inventory.Renderer == AvatarRenderer.Vrm ? "A VRM 3D model.\n" : "A Live2D model.\n");
        for (var i = 0; i < sources.Count; i++)
            list.Append(CultureInfo.InvariantCulture, $"{i + 1}. {(sources[i].Kind == CharacterActionKind.Motion ? "motion" : "expression")} " +
                $"\"{sources[i].Name}\": {sources[i].Detail}\n");
        return (instructions, list.ToString());
    }

    [GeneratedRegex(@"^\s*(?:item\s*)?(?<n>\d{1,3})\s*[:.)\-]\s*(?<rest>.+?)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Line();

    // The mode a naming answer gives ("stays", "brief"...), or null when the part is something else (when to use it).
    private static string? ModeWord(string part) => part.Trim().ToLowerInvariant() switch
    {
        "stays" or "stay" or "stays on" or "lingering" or "linger" or "lingers" or "toggle" or "held" or "hold" => Lingering,
        "brief" or "once" or "moment" or "momentary" => Brief,
        _ => null
    };

    /// <summary>Settings from the Thinking model's answer: each "number: tag | cue | mode | when" line names that item (the mode,
    /// <c>stays</c> or <c>brief</c>, may be left out), SKIP turns it off; items it didn't answer keep <paramref name="current"/>.
    /// Tags are made valid and unique; unknown cues are dropped. Returns null when no line could be read.</summary>
    public static CharacterActionSettings? Parse(string? answer, CharacterActionInventory inventory, CharacterActionSettings current,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(answer)) return null;
        var sources = Nameable(inventory);
        var named = new Dictionary<string, CharacterAction>(StringComparer.Ordinal);
        foreach (var raw in answer.Split('\n').Take(400))
        {
            var match = Line().Match(raw.Replace("**", "", StringComparison.Ordinal).Replace("`", "", StringComparison.Ordinal));
            if (!match.Success || !int.TryParse(match.Groups["n"].Value, CultureInfo.InvariantCulture, out var number) ||
                number < 1 || number > sources.Count) continue;
            var source = sources[number - 1];
            var existing = current.Find(source.Id) ?? Default(source, Number(inventory, source));
            var rest = match.Groups["rest"].Value.Trim();
            if (rest.StartsWith("SKIP", StringComparison.OrdinalIgnoreCase))
            {
                named[source.Id] = existing with { Enabled = false };
                continue;
            }
            var parts = rest.Split('|').Select(p => p.Trim().Trim('"', '{', '}', '[', ']', '(', ')').Trim()).ToArray();
            var tag = Slug(parts[0]);
            // A tag not in English letters keeps the English one it had; its cue and when to use it still count.
            if (!IsTag(tag)) tag = existing.Tag ?? EnglishTag(source, Number(inventory, source));
            var cueText = parts.Length > 1 ? parts[1].ToLowerInvariant() : "-";
            var cue = VoiceTags.Cues.FirstOrDefault(c => c == cueText) ??
                VoiceTags.Known.FirstOrDefault(t => string.Equals(t.Text.Trim('[', ']', '(', ')'), cueText, StringComparison.OrdinalIgnoreCase))?.Cue;
            var use = parts.Length > 2 ? string.Join(" ", parts[2..]).Trim().TrimEnd('.') : null;
            var mode = existing.Mode;
            if (parts.Length > 2 && ModeWord(parts[2]) is { } said)
            {
                mode = said;
                use = parts.Length > 3 ? string.Join(" ", parts[3..]).Trim().TrimEnd('.') : null;
            }
            if (use is not null)
            {
                use = new string(use.Where(c => !char.IsControl(c)).ToArray());
                if (use.Length > CharacterActionCatalog.MaximumUseLength) use = use[..CharacterActionCatalog.MaximumUseLength].TrimEnd();
                if (use.Length == 0) use = null;
            }
            named[source.Id] = existing with { Tag = tag, Cue = cue, Use = use, Enabled = true, Mode = mode };
        }
        if (named.Count == 0) return null;
        var merged = current with
        {
            DetectedBy = CharacterActionSettings.ByThinking, DetectedAt = now.ToUniversalTime(),
            Actions = current.Actions.Select(a => named.GetValueOrDefault(a.Id) ?? a).ToArray()
        };
        // Tags the Thinking model gave twice get a number, in list order.
        return Merge(inventory, merged);
    }
}
