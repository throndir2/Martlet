namespace Martlet.Avatar.Hosting;

/// <summary>An emote combo: a tag the owner ties to 2 to 6 of one model's emotes, motions and gestures (<see cref="Parts"/>, their
/// <see cref="CharacterAction.Id"/>s). A reply's <c>{tag}</c> sets off every part that is turned on at once (a lingering part stays
/// on, a brief one shows a moment) and <c>{/tag}</c> turns its lingering parts off. <see cref="Use"/> is the hint replies get next
/// to the tag (null: what it combines, such as "a combination of {blush}, {hearts} and {nod}"). Combos are saved with the model's
/// settings. Each model also gets Martlet's own combos once (<see cref="CharacterActions.MartletCombos"/>); then they are the
/// owner's like the rest.</summary>
public sealed record CharacterCombo
{
    public required string Tag { get; init; }
    public required IReadOnlyList<string> Parts { get; init; }
    public string? Use { get; init; }
    public bool Enabled { get; init; } = true;
}

/// <summary>One of Martlet's own combos: its tag, its parts as the tags of Martlet's gestures (<see cref="CharacterGesture.Tag"/>),
/// when to use it and whether it starts turned on.</summary>
public sealed record MartletCombo(string Tag, IReadOnlyList<string> Parts, string Use, bool Enabled = true);

public sealed partial record CharacterActionCatalog
{
    /// <summary>The model's combos in the owner's order (an incomplete one in a damaged file is left out).</summary>
    public IReadOnlyList<CharacterCombo> Combos =>
        Settings.Combos is { } combos ? [.. combos.Where(c => c is { Tag: not null, Parts: not null })] : [];

    /// <summary>The parts of <paramref name="combo"/> that are turned on, with their settings, in the combo's order: parts the model
    /// doesn't have and parts turned off are left out.</summary>
    public IReadOnlyList<(CharacterActionSource Source, CharacterAction Action)> Parts(CharacterCombo combo)
    {
        var entries = Entries.ToArray();
        return [.. combo.Parts.Distinct(StringComparer.Ordinal).Select(id => entries.FirstOrDefault(e => e.Source.Id == id))
            .Where(e => e.Source is not null && e.Action.Enabled)];
    }

    /// <summary>The combos replies may write as tags: those turned on with at least one part turned on.</summary>
    public IReadOnlyList<CharacterCombo> OfferedCombos() => [.. Combos.Where(c => c.Enabled && Parts(c).Count > 0)];

    /// <summary>Whether a part of <paramref name="combo"/> that is turned on lingers, so its off tag (<c>{/flustered}</c>) turns
    /// something off.</summary>
    public bool Lingers(CharacterCombo combo) => Parts(combo).Any(p => CharacterActions.Lingers(p.Source, p.Action));

    /// <summary>The combo turned on that a reply's tag (<c>{flustered}</c>) or off tag (<c>{/flustered}</c>) names, or null.</summary>
    public CharacterCombo? Combo(string tag) =>
        CharacterActions.OffTagName(tag) is { } off ? Named(off)
            : tag.Length > 2 && tag[0] == '{' && tag[^1] == '}' ? Named(tag[1..^1]) : null;

    // The combo turned on with that tag. A name with a slash (an off tag's) never matches: combo tags are a-z, digits, _ and -.
    private CharacterCombo? Named(string name) =>
        Combos.FirstOrDefault(c => c.Enabled && string.Equals(c.Tag, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The hint replies get next to a combo's tag: its When to use text or, while that is empty, what it combines
    /// (<see cref="CharacterActions.DescribeCombo"/>).</summary>
    public string Hint(CharacterCombo combo) =>
        combo.Use ?? CharacterActions.DescribeCombo([.. Parts(combo).Select(p => CharacterActions.PartLabel(p.Source, p.Action))]);

    // The combos' lines for the reply instructions (after the emotes' lines, so those stay the same), as many as fit in room, the
    // tags a request has left: each takes its tag, and a combo with a lingering part also its off tag.
    private IReadOnlyList<(CharacterCombo Combo, bool Lingers, string Line)> PromptCombos(int room)
    {
        var lines = new List<(CharacterCombo, bool, string)>();
        foreach (var combo in OfferedCombos())
        {
            var lingers = Lingers(combo);
            if (room < (lingers ? 2 : 1)) break;
            room -= lingers ? 2 : 1;
            lines.Add((combo, lingers, $"{{{combo.Tag}}} - {Hint(combo)}" + (lingers ? $" (stays on until you write {{/{combo.Tag}}})" : "")));
        }
        return lines;
    }
}

public static partial class CharacterActions
{
    /// <summary>The most combos one model may have.</summary>
    public const int MaximumCombos = 24;
    /// <summary>The most tags <see cref="CharacterActionSettings.GivenCombos"/> keeps.</summary>
    public const int MaximumGivenCombos = 64;
    /// <summary>The fewest parts a combo may have.</summary>
    public const int MinimumComboParts = 2;
    /// <summary>The most parts a combo may have.</summary>
    public const int MaximumComboParts = 6;

    /// <summary>Martlet's own combos of its gestures. Each model gets each one once (<see cref="CharacterActionSettings.GivenCombos"/>),
    /// after the owner's combos, with the parts it can play; then it is the owner's, to change, turn off or remove. New ones go
    /// last, so the reply instructions' earlier lines (and prompt caches) stay the same. ahegao starts turned off.</summary>
    public static readonly IReadOnlyList<MartletCombo> MartletCombos =
    [
        new("lovestruck", ["heart_eyes", "hearts", "blush_deep", "sway"], "heart eyes, hearts and a deep blush, for being smitten or madly in love"),
        new("flustered", ["blush_deep", "sweat", "shy"], "a deep blush, a sweat drop and a shy look, for being flustered by praise or teasing"),
        new("overheated", ["blush_fierce", "steam", "dizzy"], "a fierce flush, steam and swirly eyes, for being too embarrassed to think"),
        new("fuming", ["pout", "anger", "steam"], "a pout, an anger vein and steam, for being playfully furious"),
        new("heartbroken", ["tears", "gloom", "crying"], "tears, gloom and sobs, for being heartbroken or crushed"),
        new("dozing", ["drowsy", "sleepy", "drool"], "heavy eyes, a Zzz and drool, for nodding off"),
        new("starstruck", ["star_eyes", "sparkles", "mouth_open"], "starry eyes, sparkles and an open mouth, for being amazed or thrilled"),
        new("shocked", ["exclaim", "gasp", "surprised"], "an exclamation mark, a gasp and wide eyes, for a big shock"),
        new("ahegao", ["eyes_up", "mouth_open", "tongue_out", "drool", "blush_fierce", "heart_eyes"],
            "eyes rolled up, tongue out and flushed, for being overwhelmed with pleasure", Enabled: false)
    ];

    /// <summary>How a combo's default hint names a part: its tag (<c>{blush}</c>), or its name while it has no tag.</summary>
    public static string PartLabel(CharacterActionSource source, CharacterAction action) =>
        action.Tag is { Length: > 0 } tag ? "{" + tag + "}" : $"\"{source.Name}\"";

    /// <summary>What a combo combines, the hint replies get while its When to use is empty: "a combination of {blush}, {hearts} and
    /// {nod}" (<paramref name="parts"/> are <see cref="PartLabel"/>s), or "the same as {blush}" while only one part is turned on.</summary>
    public static string DescribeCombo(IReadOnlyList<string> parts) => parts.Count switch
    {
        0 => "a combination of emotes",
        1 => "the same as " + parts[0],
        _ => $"a combination of {string.Join(", ", parts.Take(parts.Count - 1))} and {parts[^1]}"
    };

    /// <summary>The parts a combo's Parts box names (Companion › Character › Emotes and motions › Combos): tags such as
    /// "blush hearts nod" (braces allowed; spaces, commas or + between them) or IDs, matched to <paramref name="actions"/>, each
    /// once, in order. Returns their IDs, or null with <paramref name="problem"/> ("no emote has the tag 'x'.") when a word names
    /// none.</summary>
    public static IReadOnlyList<string>? ParseParts(string text, IReadOnlyList<CharacterAction> actions, out string? problem)
    {
        problem = null;
        var ids = new List<string>();
        foreach (var word in text.Split([' ', ',', '+', ';', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            var name = word.Trim().Trim('{', '}').Trim();
            if (name.Length == 0) continue;
            // An emote turned on wins over one turned off that the owner gave the same tag.
            var action = actions.Where(a => string.Equals(a.Tag, name, StringComparison.OrdinalIgnoreCase)).OrderBy(a => a.Enabled ? 0 : 1)
                .FirstOrDefault() ?? actions.FirstOrDefault(a => a.Id == name);
            if (action is null)
            {
                problem = $"no emote has the tag '{name}'.";
                return null;
            }
            if (!ids.Contains(action.Id)) ids.Add(action.Id);
        }
        return ids;
    }

    /// <summary>The Parts box's text for <paramref name="parts"/>: each part's tag (its ID while it has none), with spaces between.</summary>
    public static string PartsText(IReadOnlyList<string> parts, IReadOnlyList<CharacterAction> actions) =>
        string.Join(" ", parts.Select(id => actions.FirstOrDefault(a => a.Id == id)?.Tag is { Length: > 0 } tag ? tag : id));

    // Why the combos of settings can't be saved, or null.
    private static string? ComboProblem(CharacterActionSettings settings)
    {
        if (settings.GivenCombos is { } given && (given.Count > MaximumGivenCombos || !given.All(IsTag)))
            return "The list of Martlet's combos given to this model is damaged.";
        var combos = settings.Combos ?? [];
        if (combos.Count > MaximumCombos) return $"A model can have at most {MaximumCombos} combos.";
        var actionTags = settings.Actions.Select(a => a.Tag).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ids = settings.Actions.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var combo in combos)
        {
            if (combo is not { Tag: { } tag, Parts: { } parts } || parts.Any(string.IsNullOrEmpty)) return "A combo needs a tag and its parts.";
            if (!IsTag(tag))
                return $"\"{tag}\" can't be a combo's tag: use up to {CharacterActionCatalog.MaximumTagLength} English letters (a-z), digits, _ or -, " +
                    "so every Thinking model can write it.";
            if (actionTags.Contains(tag)) return $"The combo {{{tag}}} has the tag of an emote or motion. Give it a tag of its own.";
            if (!tags.Add(tag)) return $"Two combos use the tag {{{tag}}}.";
            if (parts.Count is < MinimumComboParts or > MaximumComboParts)
                return $"The combo {{{tag}}} needs {MinimumComboParts} to {MaximumComboParts} parts.";
            if (parts.Distinct(StringComparer.Ordinal).Count() != parts.Count) return $"The combo {{{tag}}} has a part twice.";
            if (parts.FirstOrDefault(id => !ids.Contains(id)) is { } missing)
                return $"The combo {{{tag}}} has a part this model doesn't have ({missing}).";
            if (combo.Use is { } use && (use.Length > CharacterActionCatalog.MaximumUseLength || use.Any(char.IsControl)))
                return $"\"When to use\" must be one line of at most {CharacterActionCatalog.MaximumUseLength} characters.";
        }
        return null;
    }

    // The saved combos that still fit inventory: a valid tag, each once; the parts the model has, each once (a model on an older
    // Martlet lacks a newer gesture); at least MinimumComboParts of them. Null when none remain.
    private static IReadOnlyList<CharacterCombo>? MergeCombos(CharacterActionInventory inventory, IReadOnlyList<CharacterCombo>? saved)
    {
        var known = inventory.Sources.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<CharacterCombo>();
        foreach (var combo in saved ?? [])
        {
            if (combo is not { Tag: { } tag, Parts: { } parts } || !IsTag(tag) || !tags.Add(tag)) continue;
            var found = parts.Where(id => id is not null && known.Contains(id)).Distinct(StringComparer.Ordinal).Take(MaximumComboParts).ToArray();
            if (found.Length < MinimumComboParts) continue;
            var use = combo.Use is { } text && (text.Length > CharacterActionCatalog.MaximumUseLength || text.Any(char.IsControl)) ? null : combo.Use;
            kept.Add(combo with { Parts = found, Use = use });
            if (kept.Count == MaximumCombos) break;
        }
        return kept.Count == 0 ? null : kept;
    }

    // Gives the model the Martlet combos it wasn't given yet, after its combos: each with the parts the model can play (at least
    // MinimumComboParts), while there is room and no emote or combo has its tag. Each is given once, added or not, so one the
    // owner removed or renamed never comes back. Returns the combos (null for none) and the tags of every Martlet combo given.
    private static (IReadOnlyList<CharacterCombo>? Combos, IReadOnlyList<string> Given) GiveCombos(CharacterActionInventory inventory,
        IReadOnlyList<CharacterAction> actions, IReadOnlyList<CharacterCombo>? combos, IReadOnlyList<string>? given)
    {
        var had = (given ?? []).Where(IsTag).Distinct(StringComparer.Ordinal).Take(MaximumGivenCombos).ToList();
        var all = new List<CharacterCombo>(combos ?? []);
        var tags = actions.Select(a => a.Tag).OfType<string>().Concat(all.Select(c => c.Tag)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var combo in MartletCombos)
        {
            if (had.Contains(combo.Tag)) continue;
            if (had.Count < MaximumGivenCombos) had.Add(combo.Tag);
            if (all.Count >= MaximumCombos || tags.Contains(combo.Tag)) continue;
            var parts = combo.Parts.Select(tag => PartOf(inventory, actions, tag)).OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
            if (parts.Length < MinimumComboParts) continue;
            all.Add(new() { Tag = combo.Tag, Parts = parts, Use = combo.Use, Enabled = combo.Enabled });
            tags.Add(combo.Tag);
        }
        return (all.Count == 0 ? null : all, had);
    }

    // The part a Martlet combo names by a gesture's tag: Martlet's gesture, or the model's own emote that replaces it (the one
    // with that tag); null when the model has neither.
    private static string? PartOf(CharacterActionInventory inventory, IReadOnlyList<CharacterAction> actions, string tag) =>
        CharacterActionInventory.AllGestures.FirstOrDefault(g => g.Tag == tag) is { } gesture && inventory.Find(gesture.Id) is not null
            ? gesture.Id
            : actions.Where(a => string.Equals(a.Tag, tag, StringComparison.OrdinalIgnoreCase)).OrderBy(a => a.Enabled ? 0 : 1).FirstOrDefault()?.Id;
}
