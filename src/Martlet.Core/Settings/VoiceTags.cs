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
    /// tones): exactly that engine's tags in its own syntax, one per line with when to use it. Null when the engine has no
    /// tags or the owner emptied the prompt.</summary>
    public static string? Instructions(SpeechEngine? engine, PromptSettings? prompts) =>
        engine is { SupportsTags: true }
            ? PromptSettings.Fill(prompts, PromptCatalog.VoiceTags, ("engine", engine.Name),
                ("tags", string.Join("\n", engine.Tags.Select(tag => $"{tag.Text} - {tag.Usage}"))), ("example", engine.Tags[0].Text))
            : null;
    /// <summary>Every registered engine's tags (what chat and captions never show).</summary>
    public static IReadOnlyList<VoiceTag> Known => SpeechEngines.All.SelectMany(engine => engine.Tags)
        .DistinctBy(tag => tag.Text, StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>Text for display or a voice without tags: every registered tag removed and spacing kept natural.</summary>
    public static string Strip(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var stripper = new VoiceTagStripper();
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

/// <summary>Strips every registered voice tag from streamed reply text for the chat, keeping spacing natural. Tags may be
/// split across deltas, so a possible tag's start is held until it completes or stops matching, and spaces wait for the next
/// word (a space before a removed tag's punctuation is dropped).</summary>
public sealed class VoiceTagStripper
{
    private readonly VoiceTag[] known = [.. VoiceTags.Known];
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
                if (known.Any(tag => string.Equals(tag.Text, text, StringComparison.OrdinalIgnoreCase)))
                {
                    held.Clear();
                    dropped = true;
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
