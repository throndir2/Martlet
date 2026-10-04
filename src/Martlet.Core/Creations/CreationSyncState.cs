using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Core.Contracts;

namespace Martlet.Core.Creations;

/// <summary>What the last creation sync found on one paired host: whether it shares creations (<c>shared</c>), couldn't be
/// reached (<c>unreachable</c>) or runs a Martlet older than creations (<c>old</c>), and the live creations whose every
/// piece it holds (by ID).</summary>
public sealed record CreationHostState
{
    public required string HostId { get; init; }
    public required string State { get; init; }
    public required DateTimeOffset At { get; init; }
    public IReadOnlyList<string> Complete { get; init; } = [];
}

/// <summary>This desktop's record of its last creation sync (creations-sync.json): when it ran, what it said and each paired
/// host's state, for the Creations page and MCP's <c>creations_status</c>. IDs only, never titles or text.</summary>
public sealed record CreationSyncState
{
    public const string FileName = "creations-sync.json";
    public const string Shared = "shared", Unreachable = "unreachable", Old = "old";
    private const int MaximumBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    public required DateTimeOffset CheckedAt { get; init; }
    public required string Summary { get; init; }
    public required IReadOnlyList<CreationHostState> Hosts { get; init; }

    /// <summary>The live creations whose every piece is in <paramref name="present"/>.</summary>
    public static IReadOnlyList<string> CompleteOn(CreationLibrary library, IReadOnlySet<string> present) =>
        library.Live.Where(c => c.Assets!.All(a => a.Chunks.All(present.Contains))).Select(c => c.Id).Order(StringComparer.Ordinal).ToArray();

    public static CreationSyncState? Load(string dataDirectory)
    {
        try
        {
            var path = Path.Combine(dataDirectory, FileName);
            if (!File.Exists(path) || new FileInfo(path).Length > MaximumBytes) return null;
            return JsonSerializer.Deserialize<CreationSyncState>(File.ReadAllBytes(path), Json);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return null; }
    }

    public void Save(string dataDirectory)
    {
        ContractRules.Require(Hosts.Count <= 64, "Too many hosts.");
        Directory.CreateDirectory(dataDirectory);
        var path = Path.Combine(dataDirectory, FileName);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(this, Json));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
