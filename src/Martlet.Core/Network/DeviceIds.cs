using System.Security.Cryptography;
using System.Text.Json;

namespace Martlet.Core.Network;

/// <summary>How a data folder came by its device ID.</summary>
public enum DeviceIdSource
{
    /// <summary>Read from device.json.</summary>
    Saved,
    /// <summary>The ID this folder's pairings already use (the most common, if they differ).</summary>
    Pairings,
    /// <summary>The desktop-&lt;pc name&gt; form every Martlet before device.json used, kept because this folder was paired or
    /// in a network.</summary>
    Legacy,
    /// <summary>A new desktop-&lt;pc name&gt;-&lt;6 of [a-z0-9]&gt; ID.</summary>
    New
}

public sealed record DeviceIdChoice(string Id, DeviceIdSource Source);

/// <summary>The ID one Windows user's Martlet uses with its hosts, in the network roster and the shared plan, and as the source of
/// its log lines (docs/ACCOUNTS.md, Device ID). It is kept in device.json in the data folder, so each Windows user (each data
/// folder) has its own and it never changes. A data folder from before device.json keeps the ID its pairings use, or
/// desktop-&lt;pc name&gt; when it was paired or in a network; a new one is desktop-&lt;pc name&gt;-&lt;6 of [a-z0-9]&gt;, so two
/// Windows users on one PC never share an ID.</summary>
public static class DeviceIds
{
    public const string FileName = "device.json";
    public const int MaximumLength = 64;
    private const string Prefix = "desktop-";
    private const int SuffixLength = 6;
    private const int MaximumFileBytes = 4096;
    private const string SuffixCharacters = "abcdefghijklmnopqrstuvwxyz0123456789";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly object Gate = new();

    private sealed record Document
    {
        public int Version { get; init; }
        public string? DeviceId { get; init; }
    }

    /// <summary>The ID every Martlet before device.json used on every Windows user of a PC: "desktop-" and the computer name.</summary>
    public static string Legacy(string computerName)
    {
        var id = Prefix + Name(computerName);
        return id.Length > MaximumLength ? id[..MaximumLength] : id;
    }

    /// <summary>A new ID: "desktop-", the computer name (shortened to fit), "-" and six random lowercase letters or digits.</summary>
    public static string New(string computerName)
    {
        var name = Name(computerName);
        var room = MaximumLength - Prefix.Length - 1 - SuffixLength;
        if (name.Length > room) name = name[..room];
        return Prefix + name + "-" + RandomNumberGenerator.GetString(SuffixCharacters, SuffixLength);
    }

    /// <summary>Whether <paramref name="id"/> is a device ID a pairing accepts: 1 to 64 ASCII letters, digits, '.', '_' or '-',
    /// starting with a letter or digit.</summary>
    public static bool IsValid(string? id) => id is { Length: > 0 and <= MaximumLength } && char.IsAsciiLetterOrDigit(id[0]) &&
        id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    /// <summary>The ID saved in <paramref name="dataDirectory"/>'s device.json, or null when there is none or it can't be read.</summary>
    public static string? Saved(string dataDirectory)
    {
        try
        {
            var path = Path.Combine(dataDirectory, FileName);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaximumFileBytes) return null;
            var document = JsonSerializer.Deserialize<Document>(File.ReadAllBytes(path), Json);
            return document is { Version: 1 } && IsValid(document.DeviceId) ? document.DeviceId : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>The ID this data folder uses, without writing anything: the saved one, else the one it would keep
    /// (<see cref="Existing"/>), else null (Martlet picks a new one on its next start).</summary>
    public static DeviceIdChoice? Peek(string dataDirectory, string? computerName = null) =>
        Saved(dataDirectory) is { } saved ? new(saved, DeviceIdSource.Saved) : Existing(dataDirectory, computerName);

    /// <summary>The ID a data folder without device.json already uses with hosts: the one its pairings use (hosts.json, and the
    /// older single lip-sync pairing in avatar.json), else the <see cref="Legacy"/> form when it has paired hosts or been in a
    /// network (hosts.json, network.json or a network key), else null.</summary>
    public static DeviceIdChoice? Existing(string dataDirectory, string? computerName = null)
    {
        var paired = PairingIds(dataDirectory).GroupBy(id => id, StringComparer.Ordinal).OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal).Select(g => g.Key).FirstOrDefault();
        if (paired is not null) return new(paired, DeviceIdSource.Pairings);
        var earlier = File.Exists(Path.Combine(dataDirectory, "hosts.json")) || File.Exists(Path.Combine(dataDirectory, "network.json")) ||
            File.Exists(Path.Combine(dataDirectory, "network", "device_ecdsa"));
        return earlier ? new(Legacy(computerName ?? Environment.MachineName), DeviceIdSource.Legacy) : null;
    }

    /// <summary>This data folder's device ID, chosen and saved in device.json the first time (<see cref="Existing"/>, else a
    /// <see cref="New"/> one). Once saved it never changes. Throws when the folder can't be written.</summary>
    public static DeviceIdChoice Ensure(string dataDirectory, string? computerName = null)
    {
        lock (Gate)
        {
            if (Saved(dataDirectory) is { } saved) return new(saved, DeviceIdSource.Saved);
            var choice = Existing(dataDirectory, computerName) ?? new(New(computerName ?? Environment.MachineName), DeviceIdSource.New);
            Directory.CreateDirectory(dataDirectory);
            var path = Path.Combine(dataDirectory, FileName);
            // A device.json that exists here can't be read (Saved returned null), so it is replaced.
            var replace = File.Exists(path);
            var temporary = Path.Combine(dataDirectory, $"device.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(new Document { Version = 1, DeviceId = choice.Id }, Json));
                try { File.Move(temporary, path, overwrite: replace); }
                catch (IOException) when (!replace && Saved(dataDirectory) is { } other)
                {
                    // Another Martlet on this data folder saved one a moment earlier; that one stays.
                    return new(other, DeviceIdSource.Saved);
                }
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            return choice;
        }
    }

    private static string Name(string computerName)
    {
        var name = new string(computerName.ToLowerInvariant().Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.').ToArray());
        return name.Length == 0 ? "pc" : name;
    }

    private static IEnumerable<string> PairingIds(string dataDirectory)
    {
        var ids = new List<string>();
        var hosts = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(dataDirectory, "hosts.json")));
            if (document.RootElement.TryGetProperty("hosts", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var host in list.EnumerateArray())
                    if (host.ValueKind == JsonValueKind.Object && host.TryGetProperty("pairing", out var pairing) &&
                        pairing.ValueKind == JsonValueKind.Object && Text(pairing, "deviceId") is { } id && IsValid(id))
                    {
                        ids.Add(id);
                        if (Text(pairing, "hostId") is { } hostId) hosts.Add(hostId);
                    }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(dataDirectory, "avatar.json")));
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("remote_host", out var remote) &&
                remote.ValueKind == JsonValueKind.Object && Text(remote, "device_id") is { } id && IsValid(id) &&
                !(Text(remote, "host_id") is { } hostId && hosts.Contains(hostId)))
                ids.Add(id);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        return ids;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
