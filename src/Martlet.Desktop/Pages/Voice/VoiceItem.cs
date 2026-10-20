using Martlet.Core.Voices;
using Martlet.F5;

namespace Martlet.Desktop;

/// <summary>One voice in Companion › Voice › Voices: its ID (reference revision), name, where it comes from
/// (<paramref name="Note"/>), this PC's recording of it (null while it is still being copied here, or for a starter voice not
/// used yet) and its automation key: a starter recording's key, else the first 16 hex digits of its ID. <paramref name="Clips"/>
/// are the lengths of the recordings it was made from (null for one recording).</summary>
internal sealed record VoiceItem(string Id, string Name, string? Note, string? Transcript, string AudioSha256, int DurationMilliseconds,
    DateTimeOffset AddedAt, F5ReferenceSnapshot? Local, string Key, bool Starter, IReadOnlyList<int>? Clips = null)
{
    /// <summary>Its recording is here, or Martlet carries it.</summary>
    internal bool Available => Local is not null || Starter;

    /// <summary>The voices to list: every live voice of the shared list in the order it joined, then recordings only this PC
    /// has (added before voices were shared; they join the list on the next change).</summary>
    internal static List<VoiceItem> List(SpeakingVoiceLibrary library, IReadOnlyDictionary<string, F5ReferenceSnapshot> local)
    {
        static (string Key, bool Starter) KeyOf(string id, string sha256) =>
            F5BundledVoices.ForAudio(sha256) is { } starter ? (starter.Key, true) : (id[..16], false);
        var items = library.Live.Select(v =>
        {
            var (key, starter) = KeyOf(v.Id, v.AudioSha256!);
            return new VoiceItem(v.Id, v.Name!, v.Note, v.Transcript, v.AudioSha256!, v.DurationMilliseconds, v.AddedAt,
                local.GetValueOrDefault(v.Id), key, starter, v.ClipMilliseconds);
        }).ToList();
        foreach (var (id, snapshot) in local.Where(pair => library.Find(pair.Key) is null).OrderBy(pair => pair.Value.CreatedAtUtc))
        {
            var (key, starter) = KeyOf(id, snapshot.AudioSha256);
            items.Add(new(id, snapshot.PresetName, null, null, snapshot.AudioSha256, snapshot.AudioFormat.DurationMilliseconds,
                snapshot.CreatedAtUtc, snapshot, key, starter));
        }
        return items;
    }
}
