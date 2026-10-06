using System.Text;

namespace Martlet.Core.Settings;

/// <summary>The control tag a reply ends with to say in a few words what the picture it got shows, such as
/// <c>[seen: a racing game, last lap]</c> (Companion › Prompts › What you saw). It is never shown or spoken
/// (<see cref="VoiceTagKind.Control"/>, an open tag: <see cref="VoiceTags.OpenEnd"/>); Martlet keeps the words in the
/// conversation in place of the picture, which is never kept, so later replies know what was seen.</summary>
public static class SeenTags
{
    /// <summary>What starts the tag.</summary>
    public const string Opener = "[seen:";

    /// <summary>The tag as a request lists it (<see cref="VoiceTags.OpenEnd"/>).</summary>
    public const string Tag = Opener + VoiceTags.OpenEnd;

    /// <summary>The control tags a request with a picture is offered.</summary>
    public static IReadOnlyList<string> All { get; } = [Tag];

    /// <summary>The longest description Martlet keeps; a longer one is cut at a word.</summary>
    public const int MaximumDescription = 120;

    /// <summary>The words of the last seen tag among <paramref name="controls"/> (a turn's control tags, as written), tidied
    /// (<see cref="Clean"/>), or null when there is none or it is empty.</summary>
    public static string? Description(IEnumerable<string> controls)
    {
        ArgumentNullException.ThrowIfNull(controls);
        string? last = null;
        foreach (var tag in controls)
            if (tag.Length > Opener.Length && tag.StartsWith(Opener, StringComparison.OrdinalIgnoreCase) && tag[^1] == ']')
                last = tag[Opener.Length..^1];
        return last is null ? null : Clean(last);
    }

    /// <summary>A description as the conversation keeps it: one line without brackets or control characters, spacing
    /// collapsed, no closing period, at most <see cref="MaximumDescription"/> characters; null when nothing is left.</summary>
    public static string? Clean(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var builder = new StringBuilder(Math.Min(text.Length, MaximumDescription + 1));
        foreach (var c in text)
        {
            var kept = char.IsControl(c) || char.IsWhiteSpace(c) ? ' ' : c is '[' or ']' or '{' or '}' ? '\0' : c;
            if (kept == '\0' || kept == ' ' && (builder.Length == 0 || builder[^1] == ' ')) continue;
            builder.Append(kept);
        }
        var cleaned = builder.ToString().Trim().TrimEnd('.', ' ');
        if (cleaned.Length > MaximumDescription)
        {
            var cut = cleaned.LastIndexOf(' ', MaximumDescription);
            cleaned = cleaned[..(cut > MaximumDescription / 2 ? cut : MaximumDescription)].TrimEnd(',', ';', ' ') + "…";
        }
        return cleaned.Length > 0 ? cleaned : null;
    }

    /// <summary>The reply's text without its seen tags (what the conversation keeps as Martlet's words; the description goes
    /// with the user's line instead). Text without one comes back as it is.</summary>
    public static string Without(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.IndexOf(Opener, StringComparison.OrdinalIgnoreCase) < 0) return text;
        var builder = new StringBuilder(text.Length);
        var at = 0;
        while (text.IndexOf(Opener, at, StringComparison.OrdinalIgnoreCase) is var start and >= 0)
        {
            var end = text.IndexOfAny([']', '[', '\n', '\r'], start + Opener.Length);
            if (end < 0 || text[end] != ']' || end - start + 1 > VoiceTags.MaximumOpenTag)
            {
                builder.Append(text, at, start + Opener.Length - at);
                at = start + Opener.Length;
                continue;
            }
            builder.Append(text.AsSpan(at, start - at).TrimEnd(" \t"));
            at = end + 1;
        }
        builder.Append(text, at, text.Length - at);
        return builder.ToString().Trim();
    }

    /// <summary>What a look or a message with a picture is told (Companion › Prompts › What you saw); <paramref name="silent"/>
    /// is the word for staying quiet. The same on every request, so the instructions stay the same. Null when the owner
    /// emptied it (then no tag is offered).</summary>
    public static string? Instructions(PromptSettings? prompts, string silent) =>
        PromptSettings.Fill(prompts, PromptCatalog.SeenTag, ("silent", silent));
}
