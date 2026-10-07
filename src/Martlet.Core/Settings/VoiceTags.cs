using System.Runtime.CompilerServices;
using System.Text;

namespace Martlet.Core.Settings;

/// <summary>Finds, keeps and strips voice-engine tags (<see cref="SpeechEngine.Tags"/>) in reply text. Matching ignores case
/// and writes the engine's own spelling. The chat, captions and voices without a tag catalog get every registered engine's
/// tags stripped, so OpenAI, Windows, F5 or XTTS never read "[laugh]" aloud. Models often write a tag they were given in
/// other brackets or as a stage direction ([nod] or *nods* for {nod}, (sighs) for [sigh]); those spellings
/// (<see cref="Spellings"/>) count as the tag itself.</summary>
public static class VoiceTags
{
    private static readonly (char Open, char Close)[] Brackets = [('[', ']'), ('(', ')'), ('{', '}'), ('<', '>')];
    private static readonly (char Open, char Close)[] ActionBrackets = [.. Brackets, ('*', '*')];

    /// <summary>Other ways models write <paramref name="tag"/> that count as it, so a reply that mixes up the brackets still
    /// acts and never shows or speaks the tag: its words in any of [ ], ( ), { } and &lt; &gt; ([nod], (nod) or &lt;nod&gt; for
    /// {nod}; {laugh} or (laugh) for [laugh]); a sound's or tone's engine-independent cue the same way ([laugh] for Dia's
    /// (laughs)); words joined by spaces, underscores or hyphens alike ([shake head] for {shake_head}); and, for sounds and the
    /// character's tags, the action a stage direction would write ([nods], *nods*, (sighs), *clears throat*). A sound's or
    /// tone's other words for its cue (<see cref="Synonyms"/>: [whisper], (whispers), *whispers*, {hushed} for [whispering])
    /// count the same way, in any bracket, and as *...* when they read as a stage direction (-s or -ing). Tones of voice
    /// otherwise get only the other brackets (and *...* for an -ing tone such as *whispering*); control tags get none. The
    /// tag's own spelling isn't listed.</summary>
    public static IReadOnlyList<VoiceTag> Spellings(VoiceTag tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        var text = tag.Text;
        if (tag.Kind == VoiceTagKind.Control || text.Length < 3 || !Brackets.Contains((text[0], text[^1]))) return [];
        var words = new List<string> { text[1..^1].Trim() };
        if (tag.Kind != VoiceTagKind.Character && tag.Cue.All(c => char.IsAsciiLetter(c) || c is ' ' or '_' or '-')) words.Add(tag.Cue);
        var acts = tag.Kind != VoiceTagKind.Emotion;
        var spellings = new List<VoiceTag>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { text };
        foreach (var word in words.Where(word => word.Length > 0))
            foreach (var joined in Joinings(word))
            {
                Add(joined, acts ? FirstWord(joined).EndsWith("s", StringComparison.OrdinalIgnoreCase)
                    : FirstWord(joined).EndsWith("ing", StringComparison.OrdinalIgnoreCase));
                if (acts && Action(joined) is { } action) Add(action, true);
            }
        if (tag.Kind is VoiceTagKind.Sound or VoiceTagKind.Emotion && Synonyms.TryGetValue(tag.Cue, out var others))
            foreach (var joined in others.SelectMany(Joinings))
                Add(joined, StageVerb(FirstWord(joined)));
        return spellings;

        void Add(string form, bool action)
        {
            foreach (var (open, close) in action ? ActionBrackets : Brackets)
                if (seen.Add($"{open}{form}{close}"))
                    spellings.Add(tag with { Text = $"{open}{form}{close}", CueName = tag.Cue, AliasOf = tag.Canonical });
        }
    }

    // A tag's words joined by spaces, underscores and hyphens ("shake_head": "shake head", "shake_head", "shake-head").
    private static IEnumerable<string> Joinings(string words)
    {
        var parts = words.Split([' ', '_', '-'], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length < 2 ? [words] : new[] { " ", "_", "-" }.Select(separator => string.Join(separator, parts));
    }

    private static string FirstWord(string words) => words.IndexOfAny([' ', '_', '-']) is var cut and >= 0 ? words[..cut] : words;

    // Whether a word reads as a stage direction's verb ("whispers", "sobbing"), so *word* is the action, not emphasis.
    private static bool StageVerb(string word) =>
        word.EndsWith("ing", StringComparison.OrdinalIgnoreCase) ||
        word.EndsWith('s') && !word.EndsWith("ss", StringComparison.OrdinalIgnoreCase) && !word.EndsWith("ous", StringComparison.OrdinalIgnoreCase);

    /// <summary>Other words models write for a sound's or tone's cue (<see cref="VoiceTag.Cue"/>) instead of the tag they were
    /// given: other forms of the word ([whisper], [whispered], (sighing)) and close synonyms ({hushed}, [sobbing]). Each counts
    /// as the engine's own tag for that cue (<see cref="Spellings"/>), so a reply that writes [whisper] is spoken as Chatterbox's
    /// [whispering] rather than silencing its line. No word belongs to two cues.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Synonyms = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["laugh"] = ["laughing", "laughter", "laughs out loud"],
        ["chuckle"] = ["chuckling", "chuckles", "giggle", "giggles", "giggling"],
        ["sigh"] = ["sighing", "sighs heavily"],
        ["gasp"] = ["gasping"],
        ["cough"] = ["coughing", "coughs"],
        ["clear throat"] = ["clearing throat", "clears her throat", "clears his throat", "ahem"],
        ["groan"] = ["groaning"],
        ["sniff"] = ["sniffing", "sniffle", "sniffles", "sniffling"],
        ["shush"] = ["shushing", "shh", "shhh"],
        ["happy"] = ["happily", "cheerful", "cheerfully"],
        ["sarcastic"] = ["sarcastically", "sarcasm"],
        ["surprised"] = ["surprise", "surprisedly"],
        ["angry"] = ["angrily", "anger"],
        ["fear"] = ["fearful", "fearfully", "scared", "afraid", "frightened"],
        ["crying"] = ["cry", "cries", "sob", "sobs", "sobbing", "tearful", "tearfully", "teary"],
        ["whispering"] = ["whisper", "whispers", "whispered", "whispery", "whisper voice", "whispers softly", "in a whisper", "hushed",
            "hushed voice", "quietly", "softly", "lowers voice", "lowering voice"],
        ["dramatic"] = ["dramatically", "theatrical", "theatrically"]
    };

    // The words as a stage direction writes the action: the first word in the third person ("nod": "nods", "blush": "blushes",
    // "clear throat": "clears throat"), or null when it already ends in s, -ing or -ed, or isn't an English word.
    private static string? Action(string words)
    {
        var first = FirstWord(words);
        var lower = first.ToLowerInvariant();
        if (first.Length < 2 || !first.All(char.IsAsciiLetter) || lower.EndsWith('s') || lower.EndsWith("ing", StringComparison.Ordinal) ||
            lower.EndsWith("ed", StringComparison.Ordinal))
            return null;
        var verb = lower.EndsWith("sh", StringComparison.Ordinal) || lower.EndsWith("ch", StringComparison.Ordinal) || lower[^1] is 'x' or 'z' or 'o'
            ? first + "es"
            : lower[^1] == 'y' && lower[^2] is not ('a' or 'e' or 'i' or 'o' or 'u') ? first[..^1] + "ies"
            : first + "s";
        return verb + words[first.Length..];
    }

    private static readonly ConditionalWeakTable<object, VoiceTagSet.Group> Exact = new(), Spelled = new();

    /// <summary>The tags a reply may write, for finding them as it streams: <paramref name="voiceTags"/> (the speaking engine's,
    /// which it keeps), <paramref name="characterTags"/> ({blush}) and <paramref name="controlTags"/>
    /// ([chattiness:quiet]), then every other spelling of the engine's and the character's tags (<see cref="Spellings"/>), then
    /// every registered engine's tags and their other spellings. When two share a spelling the first wins: the speaking voice's
    /// sounds before the character's emotes, and either before another engine's tag.</summary>
    public static VoiceTagSet Recognized(IReadOnlyList<VoiceTag>? voiceTags = null, IEnumerable<string>? characterTags = null,
        IEnumerable<string>? controlTags = null)
    {
        var kept = voiceTags ?? [];
        var character = CharacterTags(characterTags);
        var engines = SpeechEngines.All;
        var controls = ControlTags(controlTags);
        return new([
            Exact.GetValue(kept, _ => new(kept)), new(character), new(controls.Where(tag => !IsOpen(tag.Text))),
            Spelled.GetValue(kept, _ => new(kept.SelectMany(Spellings))), new(character.SelectMany(Spellings)),
            Exact.GetValue(engines, _ => new(Known)), Spelled.GetValue(engines, _ => new(Known.SelectMany(Spellings)))
        ], [.. controls.Where(tag => IsOpen(tag.Text))]);
    }

    /// <summary>Ends an open control tag: <c>[seen:…]</c> stands for <c>[seen:</c>, any words on one line (no brackets) and
    /// <c>]</c>, such as <c>[seen: a racing game, last lap]</c>. The tag found is what the reply wrote.</summary>
    public const string OpenEnd = "…]";

    /// <summary>The longest an open control tag may be, brackets included; a longer one is ordinary text.</summary>
    public const int MaximumOpenTag = 200;

    /// <summary>Whether <paramref name="tag"/> is an open control tag (<see cref="OpenEnd"/>).</summary>
    public static bool IsOpen(string tag) => tag.Length > OpenEnd.Length + 1 && tag.EndsWith(OpenEnd, StringComparison.Ordinal);

    /// <summary>The tag of <paramref name="tags"/> starting at <paramref name="index"/> in <paramref name="text"/>, or null.</summary>
    public static VoiceTag? At(string text, int index, IReadOnlyList<VoiceTag> tags)
    {
        VoiceTag? found = null;
        foreach (var tag in tags)
            if (index + tag.Text.Length <= text.Length && (found is null || tag.Text.Length > found.Text.Length) &&
                string.Compare(text, index, tag.Text, 0, tag.Text.Length, StringComparison.OrdinalIgnoreCase) == 0)
                found = tag;
        return found;
    }

    /// <summary>What the Thinking model is told about the speaking engine's tags (Companion › Prompts › Voice sounds and
    /// tones): exactly that engine's tags in its own syntax (<see cref="Catalog"/>). Null when the engine has no tags or the
    /// owner emptied the prompt.</summary>
    public static string? Instructions(SpeechEngine? engine, PromptSettings? prompts) =>
        engine is { SupportsTags: true }
            ? PromptSettings.Fill(prompts, PromptCatalog.VoiceTags, ("engine", engine.Name), ("tags", Catalog(engine)),
                ("example", engine.Tags[0].Text))
            : null;

    /// <summary>Heads the engine's non-word sounds in <see cref="Catalog"/>.</summary>
    public const string SoundsHeading = "Non-word sounds (write one inline, exactly where the sound happens):";

    /// <summary>Heads the engine's tones of voice in <see cref="Catalog"/>. Each spoken piece is synthesized on its own, so a
    /// tone reaches only the sentence it starts; a reply that should stay in a tone (asked to whisper) starts each sentence
    /// with it.</summary>
    public const string TonesHeading =
        "Tones of voice (write one at the very start of a sentence; that whole sentence, and only that one, is said in that " +
        "tone, so to keep a tone start every sentence with it):";

    /// <summary>The engine's tags as the Thinking prompt lists them (its {tags}): the non-word sounds, then the tones of voice,
    /// each group under a heading saying where its tags go and each tag on its own line with when to use it. A group the engine
    /// has none of is left out.</summary>
    public static string Catalog(SpeechEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        string? Group(VoiceTagKind kind, string heading) =>
            engine.Tags.Where(tag => tag.Kind == kind).Select(tag => $"{tag.Text} - {tag.Usage}").ToArray() is { Length: > 0 } lines
                ? heading + "\n" + string.Join("\n", lines) : null;
        return string.Join("\n", new[] { Group(VoiceTagKind.Sound, SoundsHeading), Group(VoiceTagKind.Emotion, TonesHeading) }
            .Where(group => group is not null));
    }
    /// <summary>Every registered engine's tags (what chat and captions never show).</summary>
    public static IReadOnlyList<VoiceTag> Known => SpeechEngines.All.SelectMany(engine => engine.Tags)
        .DistinctBy(tag => tag.Text, StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>Every engine-independent cue (<see cref="VoiceTag.Cue"/>) a registered engine speaks, sounds first, each
    /// once: what a character's emote or motion can follow.</summary>
    public static IReadOnlyList<string> Cues => SpeechEngines.All.SelectMany(engine => engine.Tags)
        .OrderBy(tag => tag.Kind).Select(tag => tag.Cue).Distinct(StringComparer.Ordinal).ToArray();

    /// <summary>The cue of the tag of <paramref name="engine"/> written as <paramref name="text"/>, or null.</summary>
    public static string? CueOf(string text, SpeechEngine? engine = null) =>
        (engine?.Tags ?? Known).FirstOrDefault(tag => string.Equals(tag.Text, text, StringComparison.OrdinalIgnoreCase))?.Cue;

    /// <summary>Character tags (<c>{blush}</c>) as tags the segmenter and stripper recognize; never sent to an engine.</summary>
    public static IReadOnlyList<VoiceTag> CharacterTags(IEnumerable<string>? tags) =>
        tags?.Where(tag => !string.IsNullOrWhiteSpace(tag) && tag == tag.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).Select(tag => new VoiceTag(tag, VoiceTagKind.Character, "")).ToArray() ?? [];

    /// <summary>Control tags (<c>[chattiness:quiet]</c>) as tags the segmenter and stripper recognize: dropped from what is
    /// shown and spoken; never sent to an engine.</summary>
    public static IReadOnlyList<VoiceTag> ControlTags(IEnumerable<string>? tags) =>
        tags?.Where(tag => !string.IsNullOrWhiteSpace(tag) && tag == tag.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).Select(tag => new VoiceTag(tag, VoiceTagKind.Control, "")).ToArray() ?? [];

    /// <summary>Text for display or a voice without tags: every registered tag removed and spacing kept natural.</summary>
    public static string Strip(string text, IEnumerable<string>? characterTags = null, IEnumerable<string>? controlTags = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        var stripper = new VoiceTagStripper(characterTags, controlTags: controlTags);
        return stripper.Push(text) + stripper.Finish();
    }
    /// <summary>Whether <paramref name="text"/> is only tags and spacing (nothing left to show once they are stripped).</summary>
    public static bool OnlyTags(string text) => !string.IsNullOrWhiteSpace(text) && string.IsNullOrWhiteSpace(Strip(text));

    /// <summary>Spacing left after removing a tag from a finished sentence: "word  word" and "word ." are tidied.</summary>
    public static string Tidy(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is ' ' or '\t' && builder.Length > 0 && builder[^1] is ' ' or '\t') continue;
            if (c is '.' or ',' or '!' or '?' or ';' or ':' && builder.Length > 0 && builder[^1] == ' ') builder.Length--;
            builder.Append(c);
        }
        return builder.ToString().Trim();
    }
}

/// <summary>Strips every registered voice tag (and the character and control tags it is given) from streamed reply text for
/// the chat, keeping spacing natural. Tags may be split across deltas, so a possible tag's start is held until it completes or
/// stops matching, and spaces wait for the next word (a space before a removed tag's punctuation is dropped). Each character
/// tag removed is passed to <paramref name="droppedTag"/> and each control tag to <paramref name="droppedControl"/>, as
/// written in the list given, whichever spelling the reply used (<see cref="VoiceTags.Spellings"/>). <paramref name="voiceTags"/>
/// are the speaking engine's tags, so a spelling two tags share means the same as it does to the speech segmenter; every tag
/// removed is passed to <paramref name="removed"/> as matched.</summary>
public sealed class VoiceTagStripper(IEnumerable<string>? characterTags = null, Action<string>? droppedTag = null,
    IEnumerable<string>? controlTags = null, Action<string>? droppedControl = null, IReadOnlyList<VoiceTag>? voiceTags = null,
    Action<VoiceTag>? removed = null)
{
    private readonly VoiceTagSet known = VoiceTags.Recognized(voiceTags, characterTags, controlTags);
    private readonly StringBuilder held = new();
    private readonly StringBuilder spaces = new();
    private bool dropped, emitted;

    public string Push(string delta)
    {
        ArgumentNullException.ThrowIfNull(delta);
        var output = new StringBuilder(delta.Length);
        foreach (var c in delta)
        {
            if (held.Length > 0 || known.CanStart(c))
            {
                held.Append(c);
                var text = held.ToString();
                if (known.Find(text) is { } whole)
                {
                    held.Clear();
                    dropped = true;
                    if (whole.Kind == VoiceTagKind.Character) droppedTag?.Invoke(whole.Canonical);
                    else if (whole.Kind == VoiceTagKind.Control) droppedControl?.Invoke(whole.Text);
                    removed?.Invoke(whole);
                    continue;
                }
                if (known.StartsAny(text)) continue;
                held.Clear();
                foreach (var replayed in text) Emit(replayed, output);
                continue;
            }
            Emit(c, output);
        }
        return output.ToString();
    }

    /// <summary>What is still held at the end of the reply (an unfinished tag start is ordinary text).</summary>
    public string Finish()
    {
        var output = new StringBuilder();
        var text = held.ToString();
        held.Clear();
        foreach (var c in text) Emit(c, output);
        if (!dropped) output.Append(spaces);
        spaces.Clear();
        return output.ToString();
    }

    private void Emit(char c, StringBuilder output)
    {
        if (c is ' ' or '\t')
        {
            spaces.Append(c);
            return;
        }
        if (spaces.Length > 0 || dropped)
        {
            if (!dropped) output.Append(spaces);
            else if (spaces.Length > 0 && emitted && c is not ('.' or ',' or '!' or '?' or ';' or ':' or '\n')) output.Append(' ');
        }
        spaces.Clear();
        dropped = false;
        emitted = c != '\n';
        output.Append(c);
    }
}

/// <summary>The tags a reply may write (<see cref="VoiceTags.Recognized"/>), for finding them as it streams: groups in the
/// order they win when two share a spelling, each sorted so a spelling, or the start of one, is found without going through
/// every tag. Matching ignores case.</summary>
public sealed class VoiceTagSet
{
    private readonly Group[] groups;
    // Open control tags (VoiceTags.OpenEnd): what starts each ("[seen:") and the tag as the request lists it.
    private readonly (string Opener, VoiceTag Tag)[] open;

    internal VoiceTagSet(Group[] groups, IReadOnlyList<VoiceTag>? open = null)
    {
        this.groups = groups;
        this.open = open?.Select(tag => (tag.Text[..^VoiceTags.OpenEnd.Length], tag)).ToArray() ?? [];
    }

    /// <summary>Whether a tag can start with <paramref name="c"/>.</summary>
    public bool CanStart(char c)
    {
        var lower = char.ToLowerInvariant(c);
        foreach (var group in groups)
            if (group.Starts.Contains(lower)) return true;
        foreach (var (opener, _) in open)
            if (char.ToLowerInvariant(opener[0]) == lower) return true;
        return false;
    }

    /// <summary>The tag spelled <paramref name="text"/>, from the first group that has it, or null. An open control tag is
    /// found as the reply wrote it.</summary>
    public VoiceTag? Find(string text)
    {
        foreach (var group in groups)
            if (group.Find(text) is { } tag) return tag;
        foreach (var (opener, tag) in open)
            if (Open(text, opener) == OpenMatch.Whole) return tag with { Text = text };
        return null;
    }

    /// <summary>Every tag whose spelling starts with <paramref name="prefix"/> (a spelling two groups share, once from each).</summary>
    public IEnumerable<VoiceTag> StartingWith(string prefix) => groups.SelectMany(group => group.StartingWith(prefix))
        .Concat(open.Where(o => Open(prefix, o.Opener) == OpenMatch.Partial).Select(o => o.Tag));

    /// <summary>Whether any tag's spelling starts with <paramref name="prefix"/>.</summary>
    public bool StartsAny(string prefix)
    {
        foreach (var group in groups)
            if (group.StartingWith(prefix).Any()) return true;
        foreach (var (opener, _) in open)
            if (Open(prefix, opener) == OpenMatch.Partial) return true;
        return false;
    }

    private enum OpenMatch { None, Partial, Whole }

    // How text matches an open tag that starts with opener: the start of one, a whole one (closed by ']'), or neither (another
    // bracket, a new line or too long).
    private static OpenMatch Open(string text, string opener)
    {
        if (text.Length <= opener.Length)
            return opener.StartsWith(text, StringComparison.OrdinalIgnoreCase) ? OpenMatch.Partial : OpenMatch.None;
        if (text.Length > VoiceTags.MaximumOpenTag || !text.StartsWith(opener, StringComparison.OrdinalIgnoreCase)) return OpenMatch.None;
        var body = text.AsSpan(opener.Length);
        var words = body[^1] == ']' ? body[..^1] : body;
        if (words.IndexOfAny("[]\r\n") >= 0) return OpenMatch.None;
        return body[^1] == ']' ? OpenMatch.Whole : OpenMatch.Partial;
    }

    internal sealed class Group
    {
        private static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;
        private readonly VoiceTag[] sorted;

        internal Group(IEnumerable<VoiceTag> tags)
        {
            sorted = [.. tags.DistinctBy(tag => tag.Text, Comparer)];
            Array.Sort(sorted, (a, b) => Comparer.Compare(a.Text, b.Text));
            Starts = [.. sorted.Select(tag => char.ToLowerInvariant(tag.Text[0]))];
        }

        internal HashSet<char> Starts { get; }

        internal VoiceTag? Find(string text)
        {
            var at = Lower(text);
            return at < sorted.Length && Comparer.Equals(sorted[at].Text, text) ? sorted[at] : null;
        }

        internal IEnumerable<VoiceTag> StartingWith(string prefix)
        {
            for (var at = Lower(prefix); at < sorted.Length && sorted[at].Text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase); at++)
                yield return sorted[at];
        }

        // The first tag that doesn't sort before key: where key, and every spelling starting with it, would be.
        private int Lower(string key)
        {
            int low = 0, high = sorted.Length;
            while (low < high)
            {
                var middle = (low + high) >>> 1;
                if (Comparer.Compare(sorted[middle].Text, key) < 0) low = middle + 1;
                else high = middle;
            }
            return low;
        }
    }
}
