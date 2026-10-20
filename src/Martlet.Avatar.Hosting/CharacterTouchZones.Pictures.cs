using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Core.Contracts;

namespace Martlet.Avatar.Hosting;

/// <summary>The picture and probe files kept beside the store: the snapshot, the pictures the vision model was sent and the renderer's probes.</summary>
public static partial class CharacterTouchZones
{
    /// <summary>Where the pictures the vision model saw in the model's last detection are kept, with <see cref="SentFile"/>.</summary>
    public static string SentFolder(string dataDirectory, string modelId) => System.IO.Path.ChangeExtension(SnapshotPath(dataDirectory, modelId), null) + "-sent";

    public const string SentFile = "sent.json";

    /// <summary>What the model's last detection sent, or null before one ran (or when it can't be read).</summary>
    public static TouchZoneSent? LoadSent(string dataDirectory, string modelId)
    {
        try
        {
            var path = System.IO.Path.Combine(SentFolder(dataDirectory, modelId), SentFile);
            return File.Exists(path) && new FileInfo(path).Length <= MaximumBytes ? JsonSerializer.Deserialize<TouchZoneSent>(File.ReadAllBytes(path), Json) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return null; }
    }

    /// <summary>Clears the model's sent folder before a new detection, so it holds only what that one sends.</summary>
    public static string ClearSent(string dataDirectory, string modelId)
    {
        var folder = SentFolder(dataDirectory, modelId);
        if (Directory.Exists(folder))
            foreach (var file in Directory.EnumerateFiles(folder)) File.Delete(file);
        Directory.CreateDirectory(folder);
        return folder;
    }

    public static async Task SaveSentAsync(string dataDirectory, string modelId, TouchZoneSent sent, CancellationToken token = default)
    {
        var folder = SentFolder(dataDirectory, modelId);
        Directory.CreateDirectory(folder);
        await File.WriteAllBytesAsync(System.IO.Path.Combine(folder, SentFile), JsonSerializer.SerializeToUtf8Bytes(sent, Json), token);
    }

    /// <summary>What the renderer's probe told of the model when its last detection took its picture, kept with the pictures.</summary>
    public const string ProbeFile = "probe.json";

    public static async Task SaveProbeAsync(string dataDirectory, string modelId, TouchZoneProbeFile probe, CancellationToken token = default)
    {
        var folder = SentFolder(dataDirectory, modelId);
        Directory.CreateDirectory(folder);
        await File.WriteAllBytesAsync(System.IO.Path.Combine(folder, ProbeFile), JsonSerializer.SerializeToUtf8Bytes(probe, Json), token);
    }

    /// <summary>The probe the model's last detection took its picture with (<see cref="ProbeFile"/>), or null.</summary>
    public static TouchZoneProbeFile? LoadProbe(string dataDirectory, string modelId) => ReadProbe(System.IO.Path.Combine(SentFolder(dataDirectory, modelId), ProbeFile));

    /// <summary>A <see cref="ProbeFile"/>, or null when it is missing or can't be read.</summary>
    public static TouchZoneProbeFile? ReadProbe(string path)
    {
        try
        {
            return File.Exists(path) && new FileInfo(path).Length <= MaximumBytes ? JsonSerializer.Deserialize<TouchZoneProbeFile>(File.ReadAllBytes(path), Json) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return null; }
    }
    public static async Task SaveSnapshotAsync(string dataDirectory, string modelId, byte[] png, CancellationToken token = default)
    {
        var path = SnapshotPath(dataDirectory, modelId);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, png, token);
    }

    /// <summary>Where the probe of the snapshot the model's zones are on is kept (where the snapshot sat on the page and where the
    /// model's drawables, bones, own parts and swinging parts were in it), so the zones can be bound again when the owner moves,
    /// adds or changes one (<see cref="Rebind"/>).</summary>
    public static string SnapshotProbePath(string dataDirectory, string modelId) =>
        System.IO.Path.ChangeExtension(SnapshotPath(dataDirectory, modelId), ".probe.json");

    /// <summary>The probe of the snapshot the model's zones are on, or null.</summary>
    public static TouchZoneProbeFile? LoadSnapshotProbe(string dataDirectory, string modelId) => ReadProbe(SnapshotProbePath(dataDirectory, modelId));

    /// <summary>Keeps the probe of the snapshot just saved for the model (<see cref="SnapshotProbePath"/>); without one, a probe
    /// kept for an earlier snapshot is deleted, so it is never taken for this one's.</summary>
    public static async Task SaveSnapshotProbeAsync(string dataDirectory, string modelId, TouchZoneProbeFile? probe, CancellationToken token = default)
    {
        var path = SnapshotProbePath(dataDirectory, modelId);
        if (probe is null)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, JsonSerializer.SerializeToUtf8Bytes(probe, Json), token);
    }
}
