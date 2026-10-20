using System.Text.Json;

namespace Martlet.Home;

/// <summary>The saved smart home connection (smart-home.json). The access token lives in Windows Credential Manager, never here.
/// What Martlet may do with it (<paramref name="Control"/>, <paramref name="AllowSensitive"/>, <paramref name="ModelTools"/>) is
/// the household setting smart-home; the connection itself travels through the paired hosts (docs/CLUSTER.md, "The shared Home
/// Assistant connection").
/// <paramref name="SharedRevision"/> is the newest connection shared through the paired hosts this PC has seen;
/// <paramref name="FollowShare"/> is on while this PC's connection is the one every computer uses (it shared it, or took it
/// from a host in <paramref name="SharedBy"/>). Home Assistant is one connection for the whole app, so a newer one shared
/// anywhere replaces it and a disconnection anywhere disconnects it.</summary>
public sealed record HomePreferences(string Address = "", Guid CredentialId = default, bool Control = false,
    bool AllowSensitive = false, string LocationName = "", string Version = "", bool ModelTools = false,
    long SharedRevision = 0, bool FollowShare = false, string SharedBy = "")
{
    public const string FileName = "smart-home.json";

    /// <summary>The saved connection, or the defaults when there is none or it can't be read.</summary>
    public static HomePreferences Load(string? directory)
    {
        if (directory is null) return new();
        try { return Read(directory); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    /// <summary>The saved connection, or the defaults when there is none; throws <see cref="IOException"/>,
    /// <see cref="UnauthorizedAccessException"/> or <see cref="JsonException"/> when the file can't be read.</summary>
    public static HomePreferences Read(string directory)
    {
        var path = Path.Combine(directory, FileName);
        if (!File.Exists(path)) return new();
        var loaded = JsonSerializer.Deserialize<HomePreferences>(File.ReadAllText(path)) ?? new();
        return loaded with
        {
            Address = loaded.Address ?? "", LocationName = loaded.LocationName ?? "", Version = loaded.Version ?? "",
            SharedBy = loaded.SharedBy ?? ""
        };
    }

    public bool Save(string? directory)
    {
        if (directory is null) return false;
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, FileName);
            var temporary = Path.Combine(directory, $"smart-home.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(this));
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }
}
