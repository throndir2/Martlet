using System.Text;

namespace Martlet.Core.Settings;

/// <summary>Finds, keeps and strips voice-engine tags (<see cref="SpeechEngine.Tags"/>) in reply text. Matching ignores case
/// and writes the engine's own spelling. The chat, captions and voices without a tag catalog get every registered engine's
/// tags stripped, so OpenAI, Windows, F5 or XTTS never read "[laugh]" aloud.</summary>
public static class VoiceTags
{
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
    /// tone reaches only the sentence it starts.</summary>
    public const string TonesHeading =
        "Tones of voice (write one at the very start of a sentence; that whole sentence, and only that one, is said in that tone):";

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
/// written in the list given.</summary>
public sealed class VoiceTagStripper(IEnumerable<string>? characterTags = null, Action<string>? droppedTag = null,
    IEnumerable<string>? controlTags = null, Action<string>? droppedControl = null)
{
    private readonly VoiceTag[] known = [.. VoiceTags.Known.Concat(VoiceTags.CharacterTags(characterTags))
        .Concat(VoiceTags.ControlTags(controlTags)).DistinctBy(tag => tag.Text, StringComparer.OrdinalIgnoreCase)];
    private readonly StringBuilder held = new();
    private readonly StringBuilder spaces = new();
    private bool dropped, emitted;

    public string Push(string delta)
    {
        ArgumentNullException.ThrowIfNull(delta);
        var output = new StringBuilder(delta.Length);
        foreach (var c in delta)
        {
            if (held.Length > 0 || known.Any(tag => char.ToLowerInvariant(tag.Text[0]) == char.ToLowerInvariant(c)))
            {
                held.Append(c);
                var text = held.ToString();
                if (known.FirstOrDefault(tag => string.Equals(tag.Text, text, StringComparison.OrdinalIgnoreCase)) is { } whole)
                {
                    held.Clear();
                    dropped = true;
                    if (whole.Kind == VoiceTagKind.Character) droppedTag?.Invoke(whole.Text);
                    else if (whole.Kind == VoiceTagKind.Control) droppedControl?.Invoke(whole.Text);
                    continue;
                }
                if (known.Any(tag => tag.Text.StartsWith(text, StringComparison.OrdinalIgnoreCase))) continue;
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
