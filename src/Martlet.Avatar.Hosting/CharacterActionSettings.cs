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
/// <c>laugh</c>) and whether it is used at all.</summary>
public sealed record CharacterAction
{
    public required string Id { get; init; }
    public string? Tag { get; init; }
    public string? Use { get; init; }
    public string? Cue { get; init; }
    public bool Enabled { get; init; } = true;
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
    /// off with its own tag for their cue (null <paramref name="engine"/>: a reply that isn't spoken, so all of them).</summary>
    public IReadOnlyList<(CharacterActionSource Source, CharacterAction Action)> Offered(SpeechEngine? engine) =>
        Entries.Where(e => e.Action is { Enabled: true, Tag: { Length: > 0 } } &&
            !(e.Action.Cue is { } cue && engine?.Tags.Any(t => t.Cue == cue) == true)).ToArray();

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

    /// <summary>The reply instructions (Companion › Prompts › Character emotes and motions) and the tags they offer, or null
    /// when none are offered or the owner emptied the prompt.</summary>
    public CharacterActionPrompt? Prompt(SpeechEngine? engine, PromptSettings? prompts)
    {
        var offered = Offered(engine);
        if (offered.Count == 0) return null;
        var tags = offered.Select(e => "{" + e.Action.Tag + "}").ToArray();
        var lines = offered.Select(e => $"{{{e.Action.Tag}}} - {e.Action.Use ?? CharacterActions.Describe(e.Source)}");
        var text = PromptSettings.Fill(prompts, PromptCatalog.CharacterActions, ("tags", string.Join("\n", lines)), ("example", tags[0]));
        return text is null ? null : new(text, tags);
    }
}

public sealed record CharacterActionPrompt(string Instructions, IReadOnlyList<string> Tags);

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
        if (source.Kind == CharacterActionKind.Gesture) return source.Id == "gesture:nod" ? "nod" : "shake_head";
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

    /// <summary>A plain description of an emote or motion for a prompt when nobody said when to use it.</summary>
    public static string Describe(CharacterActionSource source) => source.Kind switch
    {
        CharacterActionKind.Expression => $"the character's emote named \"{source.Name}\"",
        CharacterActionKind.Motion => $"the character's motion named \"{source.Name}\"",
        _ => source.Id == "gesture:nod" ? "nod, for yes or agreement" : "shake your head, for no or disbelief"
    };

    /// <summary>Martlet's guess from the model's own name: an English tag and a cue when the name says a feeling.
    /// <paramref name="number"/> is its position among its kind (1 for the first emote), for a name with no English in it.</summary>
    public static CharacterAction Default(CharacterActionSource source, int number = 1)
    {
        var tag = EnglishTag(source, number);
        if (source.Kind == CharacterActionKind.Gesture) return new() { Id = source.Id, Tag = tag };
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

    /// <summary>Settings from the Thinking model's answer: each "number: tag | cue | when" line names that item, SKIP turns it
    /// off; items it didn't answer keep <paramref name="current"/>. Tags are made valid and unique; unknown cues are dropped.
    /// Returns null when no line could be read.</summary>
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
            if (use is not null)
            {
                use = new string(use.Where(c => !char.IsControl(c)).ToArray());
                if (use.Length > CharacterActionCatalog.MaximumUseLength) use = use[..CharacterActionCatalog.MaximumUseLength].TrimEnd();
                if (use.Length == 0) use = null;
            }
            named[source.Id] = existing with { Tag = tag, Cue = cue, Use = use, Enabled = true };
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
