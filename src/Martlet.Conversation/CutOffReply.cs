using System.Text.RegularExpressions;
using Martlet.Core.Settings;

namespace Martlet.Conversation;

/// <summary>A spoken reply the user stopped while Martlet said it (they talked over it, pressed Stop or Esc, or a touch stopped
/// it): the conversation keeps only what was said aloud (<see cref="Kept"/>, ending with <see cref="Marker"/>), and the rest it
/// hadn't said (<see cref="Unsaid"/>) goes once in the notes of the next request (<see cref="Note"/>, a <c>consume</c> note on the
/// context board), so the next reply can pick it up or drop it. The sentence that was playing counts as said.</summary>
public static partial class CutOffReply
{
    /// <summary>The context board source of the note with the rest of the reply.</summary>
    public const string BoardSource = "cut-off";
    /// <summary>What ends the reply the conversation keeps, to show it was cut off there.</summary>
    public const string Marker = " —";
    /// <summary>At most this many characters of the rest go in the note (its start, cut at a word).</summary>
    public const int MaximumUnsaidCharacters = 400;
    /// <summary>How long the note waits on the board for the next request.</summary>
    public static readonly TimeSpan NoteAge = TimeSpan.FromMinutes(2);

    /// <summary>What the conversation keeps of the reply: <paramref name="saidAloud"/> with <see cref="Marker"/> after it, or null
    /// when nothing was said aloud.</summary>
    public static string? Kept(string? saidAloud) =>
        saidAloud?.Trim() is { Length: > 0 } said ? said + Marker : null;

    /// <summary>The part of <paramref name="reply"/> (the reply's text so far) after what was said aloud
    /// (<paramref name="saidAloud"/>), found by its words in order; null when nothing is left or the words can't be found.</summary>
    public static string? Unsaid(string? reply, string? saidAloud)
    {
        if (string.IsNullOrWhiteSpace(reply)) return null;
        var said = WordPattern().Matches(saidAloud ?? "").Select(m => m.Value.ToLowerInvariant()).ToArray();
        if (said.Length == 0) return Clean(reply);
        var words = WordPattern().Matches(reply).ToArray();
        int next = 0, end = -1, matched = 0;
        foreach (var word in said)
        {
            // A word the reply's text doesn't have nearby (a voice tag's word, a changed spelling) is skipped.
            for (var i = next; i < words.Length && i < next + 8; i++)
            {
                if (!string.Equals(words[i].Value, word, StringComparison.OrdinalIgnoreCase)) continue;
                next = i + 1;
                end = words[i].Index + words[i].Length;
                matched++;
                break;
            }
        }
        // Too few words found: where it stopped is not known, so nothing is guessed.
        if (matched * 2 < said.Length) return null;
        return Clean(reply[end..]);
    }

    /// <summary>The note for the next request (Companion › Prompts › Cut off: what you hadn't said, with
    /// <paramref name="unsaid"/> as {unsaid}, its start at most <see cref="MaximumUnsaidCharacters"/>), or null when nothing is
    /// left or the prompt is empty.</summary>
    public static string? Note(PromptSettings? prompts, string? unsaid) =>
        Start(unsaid) is { } rest ? PromptSettings.Fill(prompts, PromptCatalog.CutOff, ("unsaid", rest)) : null;

    // The start of the rest, at most MaximumUnsaidCharacters, cut at a word.
    private static string? Start(string? text)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        if (trimmed.Length <= MaximumUnsaidCharacters) return trimmed;
        var start = trimmed[..MaximumUnsaidCharacters];
        var space = start.LastIndexOf(' ');
        return (space > MaximumUnsaidCharacters - 40 ? start[..space] : start).TrimEnd() + "…";
    }

    // The rest without the punctuation that ended what was said; null when no word is left.
    private static string? Clean(string rest)
    {
        var trimmed = rest.TrimStart().TrimStart('.', ',', '!', '?', ';', ':', '…', '"', '\'', ')', ']').Trim();
        return WordPattern().IsMatch(trimmed) ? trimmed : null;
    }

    [GeneratedRegex(@"[\p{L}\p{N}]+(?:['’][\p{L}\p{N}]+)*")]
    private static partial Regex WordPattern();
}
