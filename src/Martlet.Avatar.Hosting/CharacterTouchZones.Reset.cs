using System.IO;
using System.Text.Json;

namespace Martlet.Avatar.Hosting;

/// <summary>Touch zones' Reset (Companion › Touch): one model's zones back to what a fresh zone gets, or the model forgotten so
/// its zones are found again as for a new character.</summary>
public static partial class CharacterTouchZones
{
    /// <summary>Whether <paramref name="zone"/> already does what a fresh zone does (<paramref name="fresh"/>): the same reaction
    /// list (a zone not filled yet, with no list, gets the fresh one), Martlet notices, own words and rest.</summary>
    public static bool IsFresh(CharacterTouchZone zone, CharacterTouchReaction fresh) =>
        FreshActions(zone, fresh) && zone.Reaction.Notices == fresh.Notices && !OwnWords(zone) &&
        zone.Reaction.CooldownSeconds.Equals(fresh.CooldownSeconds);

    /// <summary>Whether the zone's reaction list is the fresh one, or not filled yet (it then gets the fresh one). Entries of the
    /// old second list that wait to join the list make it not fresh.</summary>
    public static bool FreshActions(CharacterTouchZone zone, CharacterTouchReaction fresh) => zone.Reaction.Later is null &&
        (zone.Reaction.Actions is not { } own || fresh.Actions is { } seed && own.SequenceEqual(seed, StringComparer.Ordinal));

    /// <summary>Whether the owner wrote their own words for the zone (not empty and not the zone's built-in line).</summary>
    public static bool OwnWords(CharacterTouchZone zone) => zone.Reaction.Narration is { Length: > 0 } words && words != Kind(zone.Id)?.Narration;

    /// <summary><paramref name="settings"/> with every zone's reaction what a fresh zone gets (<paramref name="fresh"/>); the
    /// zones, their boxes, names and on/off choices stay.</summary>
    public static CharacterTouchZoneSettings WithFreshReactions(CharacterTouchZoneSettings settings, Func<CharacterTouchZone, CharacterTouchReaction> fresh) =>
        settings with { Zones = [.. settings.Zones.Select(z => z with { Reaction = fresh(z) })] };

    /// <summary>Forgets the model with <paramref name="modelId"/>: its zones (found and added), the snapshot they were found in
    /// with its probe, and what its last detection sent. Other models stay. The Touch zones page then places a first guess again,
    /// as for a model it never saw.</summary>
    public static async Task RemoveAsync(string dataDirectory, string modelId, CancellationToken token = default)
    {
        await Gate.WaitAsync(token);
        try
        {
            var all = LoadAll(dataDirectory);
            if (all.Any(m => m.ModelId == modelId))
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(new Document(DocumentVersion, [.. all.Where(m => m.ModelId != modelId)]), Json);
                var temporary = System.IO.Path.Combine(dataDirectory, $"character-touch-zones.{Guid.NewGuid():N}.tmp");
                try
                {
                    await File.WriteAllBytesAsync(temporary, bytes, token);
                    File.Move(temporary, Path(dataDirectory), overwrite: true);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
        }
        finally { Gate.Release(); }
        foreach (var file in new[] { SnapshotPath(dataDirectory, modelId), SnapshotProbePath(dataDirectory, modelId) })
            if (File.Exists(file)) File.Delete(file);
        if (Directory.Exists(SentFolder(dataDirectory, modelId))) Directory.Delete(SentFolder(dataDirectory, modelId), recursive: true);
    }
}
