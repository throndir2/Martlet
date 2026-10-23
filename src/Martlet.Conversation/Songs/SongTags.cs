using System.Text.Json;
using Martlet.Core.Settings;

namespace Martlet.Conversation;

/// <summary>What a song tag asks for: <see cref="SongTools.SingName"/>, <see cref="SongTools.PlayName"/> or
/// <see cref="SongTools.StopName"/>, with the same arguments as the tool's (JSON).</summary>
public sealed record SongTagCall(string Name, string ArgumentsJson);

/// <summary>Singing without tools: when the Thinking route does no function calling (a model on one of the owner's computers,
/// or a model that rejected tools), a reply writes <c>[sing: what it's about]</c>, <c>[play_song: ID from chorus]</c> or
/// <c>[stop_singing]</c> in place of the song tools (Companion › Prompts › Singing without tools). They are control tags
/// (<see cref="VoiceTagKind.Control"/>), never shown or spoken; Martlet does what they ask once the reply is done.</summary>
public static class SongTags
{
    /// <summary>What starts the tag that makes a song, and the tag that plays one.</summary>
    public const string SingOpener = "[sing:", PlayOpener = "[play_song:";

    /// <summary>The tag that stops the song.</summary>
    public const string Stop = "[stop_singing]";

    /// <summary>The control tags a reply is offered while singing goes through tags (open tags: <see cref="VoiceTags.OpenEnd"/>).</summary>
    public static IReadOnlyList<string> All { get; } = [SingOpener + VoiceTags.OpenEnd, PlayOpener + VoiceTags.OpenEnd, Stop];

    /// <summary>What a reply is told while singing goes through tags; <see cref="StayQuiet.Marker"/> is the word for staying
    /// quiet. The same on every reply, so the instructions stay the same. Null when the owner emptied it (then no tag is
    /// offered).</summary>
    public static string? Instructions(PromptSettings? prompts) =>
        SkillSettings.Guidance(prompts, SkillCatalog.SongTags, ("silent", StayQuiet.Marker));

    /// <summary>The song tags among <paramref name="controls"/> (a turn's control tags, as written), in order, each as the tool
    /// call it stands for. Other tags are left out.</summary>
    public static IReadOnlyList<SongTagCall> Calls(IEnumerable<string> controls)
    {
        ArgumentNullException.ThrowIfNull(controls);
        var calls = new List<SongTagCall>();
        foreach (var tag in controls)
        {
            if (Words(tag, SingOpener) is { } about)
                calls.Add(new(SongTools.SingName, JsonSerializer.Serialize(new { about })));
            else if (Words(tag, PlayOpener) is { } play)
            {
                var (id, from) = SongAndFrom(play);
                calls.Add(new(SongTools.PlayName, from is null
                    ? JsonSerializer.Serialize(new { song_id = id })
                    : JsonSerializer.Serialize(new { song_id = id, from })));
            }
            else if (string.Equals(tag, Stop, StringComparison.OrdinalIgnoreCase))
                calls.Add(new(SongTools.StopName, "{}"));
        }
        return calls;
    }

    // The words of an open tag that starts with opener, or null (another tag, or no words).
    private static string? Words(string tag, string opener)
    {
        if (tag.Length <= opener.Length + 1 || !tag.StartsWith(opener, StringComparison.OrdinalIgnoreCase) || tag[^1] != ']') return null;
        var words = SeenTags.Clean(tag[opener.Length..^1]);
        return string.IsNullOrWhiteSpace(words) ? null : words;
    }

    /// <summary>The song ID and where to start from the words of a play tag: <c>3fa2c19b0d71</c>, <c>3fa2c19b0d71 from chorus</c>
    /// or <c>3fa2c19b0d71, 1:05</c>. The ID loses quotes and a leading "song" or "ID"; null from means the start.</summary>
    public static (string Id, string? From) SongAndFrom(string words)
    {
        ArgumentNullException.ThrowIfNull(words);
        var parts = words.Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim('"', '\'', '“', '”', '(', ')')).ToList();
        while (parts.Count > 1 && (parts[0].Equals("song", StringComparison.OrdinalIgnoreCase) ||
            parts[0].TrimEnd(':').Equals("id", StringComparison.OrdinalIgnoreCase) || parts[0].Equals("song_id", StringComparison.OrdinalIgnoreCase)))
            parts.RemoveAt(0);
        var id = parts.Count == 0 ? words.Trim() : parts[0].TrimEnd(':');
        var rest = parts.Skip(1).ToList();
        if (rest.Count > 0 && rest[0].TrimEnd(':').Equals("from", StringComparison.OrdinalIgnoreCase)) rest.RemoveAt(0);
        return (id, rest.Count == 0 ? null : string.Join(' ', rest));
    }

    /// <summary>The song tags a reply wrote, as the conversation keeps them after its words (one line, so later replies see that
    /// the tag was written); null when there are none.</summary>
    public static string? Kept(IEnumerable<string> controls)
    {
        ArgumentNullException.ThrowIfNull(controls);
        var kept = controls.Where(tag => Words(tag, SingOpener) is not null || Words(tag, PlayOpener) is not null ||
            string.Equals(tag, Stop, StringComparison.OrdinalIgnoreCase)).ToArray();
        return kept.Length == 0 ? null : string.Join(' ', kept);
    }
}
