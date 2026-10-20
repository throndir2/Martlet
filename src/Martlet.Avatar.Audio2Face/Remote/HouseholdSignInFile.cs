using System.Text.Json;

namespace Martlet.Avatar.Audio2Face.Remote;

/// <summary>A household sign-in provider this PC set up (its configuration, never the secret) and whether this PC keeps its
/// client secret in Windows Credential Manager, so it can add the provider to a host of the network that misses it.</summary>
public sealed record KeptHouseholdProvider
{
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public required string Name { get; init; }
    public string? Issuer { get; init; }
    public string? ClientId { get; init; }
    public string? Scopes { get; init; }
    public int? RedirectPort { get; init; }
    public bool Secret { get; init; }
    public DateTimeOffset SavedAt { get; init; }

    public HouseholdProvider Provider => new(Id, Kind, Name, Issuer, ClientId, Scopes, RedirectPort);

    public static KeptHouseholdProvider From(HouseholdProvider provider, bool secret, DateTimeOffset at) => new()
    {
        Id = provider.Id, Kind = provider.Kind, Name = provider.Name, Issuer = provider.Issuer, ClientId = provider.ClientId,
        Scopes = provider.Scopes, RedirectPort = provider.RedirectPort, Secret = secret, SavedAt = at
    };
}

/// <summary>household-signin.json in Martlet's data folder (docs/NETWORK.md, household sign-in providers): the providers this PC
/// set up for the household (<see cref="KeptHouseholdProvider"/>, no secret) and what it last read of every host's providers (which
/// hosts have each one; for MCP network_status). Nothing secret is written here: a kept client secret is in this PC's Windows
/// Credential Manager, so the file stays on this PC (docs/CLUSTER.md, "What stays with each computer"); the providers themselves
/// are on the hosts.</summary>
public static class HouseholdSignInFile
{
    public const string FileName = "household-signin.json";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public sealed record Document
    {
        public int Version { get; init; } = 1;
        public List<KeptHouseholdProvider> Kept { get; init; } = [];
        public DateTimeOffset? CheckedAt { get; init; }
        public List<Summary> Providers { get; init; } = [];
        public Dictionary<string, string> Problems { get; init; } = new(StringComparer.Ordinal);
    }

    public sealed record Summary(string Id, string Kind, string Name, string[] On, string[] Missing, string[] Different, string[] WithoutSecret,
        bool KeptHere);

    /// <summary>The saved document, an empty one without a file; throws <see cref="IOException"/>,
    /// <see cref="UnauthorizedAccessException"/> or <see cref="JsonException"/> when the file can't be read.</summary>
    public static Document Read(string directory)
    {
        var path = Path.Combine(directory, FileName);
        if (!File.Exists(path)) return new();
        var document = JsonSerializer.Deserialize<Document>(File.ReadAllBytes(path), Json) ?? new();
        return document with { Kept = document.Kept ?? [], Providers = document.Providers ?? [], Problems = document.Problems ?? new(StringComparer.Ordinal) };
    }

    public static void Save(string directory, Document document)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, FileName);
        var temporary = Path.Combine(directory, $"household-signin.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(document, Json));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>The document as MCP shows it (the file's own JSON form).</summary>
    public static JsonElement Describe(Document document) => JsonSerializer.SerializeToElement(document, Json);
}
