using System.Text.Json;
using System.Text.Json.Serialization;

namespace Martlet.Core.Network;

/// <summary>The port Martlet opened on the router for this PC's host service (router-port.json; nothing secret): which host,
/// the router's internet address and port, where it forwards to, the router's control URL, when it was opened and last
/// renewed, and the last problem.</summary>
public sealed record RouterPortState
{
    public required string HostId { get; init; }
    public required string ExternalAddress { get; init; }
    public required int ExternalPort { get; init; }
    public required string InternalClient { get; init; }
    public required int InternalPort { get; init; }
    public required string ControlUrl { get; init; }
    public required string ServiceType { get; init; }
    public string? RouterName { get; init; }
    public DateTimeOffset OpenedAt { get; init; }
    public DateTimeOffset RenewedAt { get; init; }
    public string? Problem { get; init; }
    /// <summary>Whether this PC answered on the router's internet address and port when Martlet last tried (routers that don't
    /// loop back to themselves make this false even when it works from outside).</summary>
    public bool? AnsweredFromInside { get; init; }
    [JsonIgnore] public string Address => $"{ExternalAddress}:{ExternalPort}";
}

/// <summary>router-port.json in Martlet's data folder: the port Martlet opened on the router for this PC's host service, or none.
/// It stays on this computer: it is how this computer is reached (docs/CLUSTER.md, "What stays with each computer").</summary>
public static class RouterPortFile
{
    public const string FileName = "router-port.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static RouterPortState? Load(string dataDirectory) => Load(dataDirectory, out _);

    /// <summary>The saved port, or null without one; <paramref name="unreadable"/> says a file is there that can't be read.</summary>
    public static RouterPortState? Load(string dataDirectory, out bool unreadable)
    {
        unreadable = false;
        try
        {
            var path = Path.Combine(dataDirectory, FileName);
            return File.Exists(path) ? JsonSerializer.Deserialize<RouterPortState>(File.ReadAllBytes(path), Json) : null;
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            unreadable = true;
            return null;
        }
    }

    public static void Save(string dataDirectory, RouterPortState? state)
    {
        var path = Path.Combine(dataDirectory, FileName);
        if (state is null)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }
        Directory.CreateDirectory(dataDirectory);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(state, Json));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
