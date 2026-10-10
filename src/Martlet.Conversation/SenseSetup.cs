using Martlet.Core.Cluster;
using Martlet.Core.Settings;

namespace Martlet.Conversation;

/// <summary>This PC's image and audio models as the lists on Companion › Vision and Companion › Hearing say
/// (<see cref="SenseLists"/>): each kind's places in order (<see cref="Image"/>, <see cref="Audio"/>), its list
/// (<see cref="ImageList"/>, <see cref="AudioList"/>; null before it has one) and the choices <see cref="SenseRouting"/> reads
/// (<see cref="Senses"/>). <see cref="State"/>: <c>lists</c> (from pools-local.json), <c>sense-models</c> (no list yet, so
/// sense-models.json), <c>none</c> (neither) or <c>unreadable</c> (pools-local.json is a newer Martlet's or damaged; the text model
/// takes both).</summary>
public sealed record SenseSetup(SenseModels Senses, IReadOnlyList<DeepThinkingSettings> Image, IReadOnlyList<DeepThinkingSettings> Audio,
    PoolList? ImageList, PoolList? AudioList, string State)
{
    public IReadOnlyList<DeepThinkingSettings> For(SenseKind kind) => kind == SenseKind.Image ? Image : Audio;

    public PoolList? List(SenseKind kind) => kind == SenseKind.Image ? ImageList : AudioList;

    /// <summary>The choices as a reader without the desktop sees them (MCP): the lists when there are any, else sense-models.json;
    /// nothing is written. The state is <c>lists</c>, <c>loaded</c> (sense-models.json), <c>none</c> or <c>unreadable</c>.</summary>
    public static (SenseModels Senses, string State) Read(string? directory)
    {
        var setup = Load(directory, "", directory is null ? null : ModelAbilities.Load(directory), migrate: false);
        return (setup.Senses, setup.State == "sense-models" ? "loaded" : setup.State);
    }

    /// <summary>Reads the lists in <paramref name="directory"/>. With <paramref name="migrate"/> (the desktop), a kind without a
    /// list gets one made from sense-models.json and saved once (its key kept in pool-keys.json); without it (MCP), sense-models.json
    /// is read as it is. <paramref name="device"/> is this PC's device ID (a member kept for other companion PCs is left out);
    /// <paramref name="hosts"/> finds a paired computer (null: hosts.json).</summary>
    public static SenseSetup Load(string? directory, string device, ModelAbilities? abilities, bool migrate, Func<string, SenseHost?>? hosts = null)
    {
        if (directory is null) return new(new(), [], [], null, null, "none");
        var path = Path.Combine(directory, PoolSettings.LocalFileName);
        try
        {
            if (File.Exists(path) && PoolSettings.Parse(File.ReadAllText(path)) is null) return new(new(), [], [], null, null, "unreadable");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return new(new(), [], [], null, null, "unreadable"); }
        var image = PoolSettings.LoadFor(directory, PoolAreas.Vision);
        var audio = PoolSettings.LoadFor(directory, PoolAreas.Hearing);
        var keys = PoolKeys.Load(directory);
        if (image is null || audio is null)
        {
            var (legacy, state) = SenseModels.Read(directory);
            if (!migrate || state == "unreadable")
            {
                var senses = state == "unreadable" ? new SenseModels() : legacy;
                IReadOnlyList<DeepThinkingSettings> Places(SenseKind kind, PoolList? list) => list is not null
                    ? SenseLists.Models(SenseLists.Area(kind), list, device, Resolver(directory, hosts), keys)
                    : senses.Place(kind) is { } own ? [own] : [];
                var (i, a) = (Places(SenseKind.Image, image), Places(SenseKind.Audio, audio));
                return new(image is null && audio is null ? senses : SensePool.Senses(i, a, abilities), i, a, image, audio,
                    image is null && audio is null ? state == "none" ? "none" : "sense-models" : "lists");
            }
            var before = keys;
            var at = legacy.Image.ChosenAt ?? legacy.Audio.ChosenAt ?? DateTimeOffset.Now;
            image ??= Save(directory, PoolAreas.Vision, SenseLists.FromSenseModels(SenseKind.Image, legacy, ref keys, at));
            audio ??= Save(directory, PoolAreas.Hearing, SenseLists.FromSenseModels(SenseKind.Audio, legacy, ref keys, at));
            if (!ReferenceEquals(keys, before)) keys.Save(directory);
        }
        var find = Resolver(directory, hosts);
        var imageModels = SenseLists.Models(PoolAreas.Vision, image, device, find, keys);
        var audioModels = SenseLists.Models(PoolAreas.Hearing, audio, device, find, keys);
        return new(SensePool.Senses(imageModels, audioModels, abilities), imageModels, audioModels, image, audio, "lists");
    }

    private static PoolList Save(string directory, PoolArea area, PoolList list)
    {
        PoolSettings.SaveFor(directory, area, list);
        return list;
    }

    private static Func<string, SenseHost?> Resolver(string directory, Func<string, SenseHost?>? hosts)
    {
        if (hosts is not null) return hosts;
        var read = SenseLists.ReadHosts(directory);
        return id => read.GetValueOrDefault(id);
    }

    public override string ToString() => $"{nameof(SenseSetup)} {State}: {Image.Count} image, {Audio.Count} audio";
}
