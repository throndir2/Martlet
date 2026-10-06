using Martlet.Core.Settings;

namespace Martlet.Conversation;

/// <summary>A tag a reply wrote that made something happen: one of the desktop character's tags (<c>{nod}</c>) or a sound or
/// tone the speaking voice performed (<c>[laugh]</c>, <c>[happy]</c>). <see cref="Tag"/> is how the character's list or the
/// engine spells it, <see cref="Name"/> what it is in a word or two (nod, shake head, laugh, happy) and <see cref="Written"/>
/// the other spelling the reply used instead (<c>[nod]</c>, <c>*nods*</c>; <see cref="VoiceTags.Spellings"/>), or null when it
/// wrote the tag itself.</summary>
public sealed record ReplyTag(string Tag, VoiceTagKind Kind, string Name, string? Written = null)
{
    /// <summary>What <paramref name="matched"/> (a tag found in a reply) did with a voice whose tags are
    /// <paramref name="voiceTags"/> (none for a reply that isn't spoken or a voice that reads words only): a character tag, or a
    /// sound or tone that voice performs. Null for another engine's tag, which nothing performs, and for control tags.</summary>
    public static ReplyTag? Of(VoiceTag matched, IReadOnlyList<VoiceTag> voiceTags)
    {
        ArgumentNullException.ThrowIfNull(matched);
        ArgumentNullException.ThrowIfNull(voiceTags);
        var written = matched.AliasOf is null ? null : matched.Text;
        if (matched.Kind == VoiceTagKind.Character)
            return new(matched.Canonical, VoiceTagKind.Character, matched.Canonical.Trim('{', '}').Replace('_', ' ').Trim(), written);
        if (matched.Kind is not (VoiceTagKind.Sound or VoiceTagKind.Emotion)) return null;
        return voiceTags.FirstOrDefault(tag => string.Equals(tag.Text, matched.Canonical, StringComparison.OrdinalIgnoreCase)) is { } performed
            ? new(performed.Text, performed.Kind, performed.Cue, written) : null;
    }

    /// <summary>The note under a reply in the talk window saying how it was acted out: <c>Tone: happy. Sound: laugh. Emotes: nod,
    /// blush.</c>, each part only when the reply wrote one; null when it wrote none.</summary>
    public static string? Note(IEnumerable<ReplyTag> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        var all = tags.ToArray();
        string? Part(VoiceTagKind kind, string one, string several) =>
            all.Where(tag => tag.Kind == kind).Select(tag => tag.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() is { Length: > 0 } names
                ? $"{(names.Length == 1 ? one : several)}: {string.Join(", ", names)}." : null;
        var note = string.Join(" ", new[]
        {
            Part(VoiceTagKind.Emotion, "Tone", "Tones"), Part(VoiceTagKind.Sound, "Sound", "Sounds"), Part(VoiceTagKind.Character, "Emote", "Emotes")
        }.Where(part => part is not null));
        return note.Length > 0 ? note : null;
    }

    /// <summary>The tags for the desktop log, in order, each with the other spelling the reply used: <c>{nod} (written [nod]),
    /// [laugh]</c>.</summary>
    public static string Describe(IEnumerable<ReplyTag> tags) =>
        string.Join(", ", tags.Select(tag => tag.Written is null ? tag.Tag : $"{tag.Tag} (written {tag.Written})"));
}
