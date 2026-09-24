using System.Text.Json;
using System.Text.Json.Serialization;

namespace Martlet.Host.Setup;

internal enum DeploymentJournalState { Prepared, Staged, Publishing, Published }

internal sealed record DeploymentRevision
{
    public required string BundleFingerprint { get; init; }
    public required DeploymentJournalState State { get; init; }
    public required string? DirectoryIdentity { get; init; }
    public required Dictionary<string, string> Files { get; init; }
}

internal sealed record DeploymentJournal
{
    public required int FormatVersion { get; init; }
    public required string Purpose { get; init; }
    public required HostDeploymentIdentity Identity { get; init; }
    public required string ParentIdentity { get; init; }
    public required string LeaseIdentity { get; init; }
    public required string LeaseNonce { get; init; }
    public required DeploymentRevision[] Revisions { get; init; }
    public required string IntegritySha256 { get; init; }
}

internal static class DeploymentJournalCodec
{
    internal const int MaximumBytes = 262_144;
    private static readonly JsonSerializerOptions Options = new()
    {
        MaxDepth = 16, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter<DeploymentJournalState>(allowIntegerValues: false) }
    };

    internal static byte[] Write(DeploymentJournal document)
    {
        Validate(document);
        var hash = FingerprintBuilder.Bytes(JsonSerializer.SerializeToUtf8Bytes(document with { IntegritySha256 = "" }, Options));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document with { IntegritySha256 = hash }, Options);
        DeploymentRules.Require(bytes.Length <= MaximumBytes, HostDeploymentFailure.JournalInvalid);
        return bytes;
    }

    internal static DeploymentJournal Read(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            DeploymentRules.Require(bytes.Length <= MaximumBytes, HostDeploymentFailure.JournalInvalid);
            using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            Unique(json.RootElement);
            var document = JsonSerializer.Deserialize<DeploymentJournal>(bytes.Span, Options)
                ?? throw new HostDeploymentException(HostDeploymentFailure.JournalInvalid);
            Validate(document);
            DeploymentRules.Require(Write(document).AsSpan().SequenceEqual(bytes.Span), HostDeploymentFailure.JournalInvalid);
            return document;
        }
        catch (JsonException error) { throw new HostDeploymentException(HostDeploymentFailure.JournalInvalid, error); }
    }

    private static void Validate(DeploymentJournal document)
    {
        DeploymentRules.Require(document.FormatVersion == 2 && document.Purpose == "HostDeploymentConfiguration" &&
            document.Identity is { HostId: var host, DeploymentId: var deployment, OwnerId: var owner } &&
            host != Guid.Empty && deployment != Guid.Empty && owner != Guid.Empty &&
            Text(document.ParentIdentity) && Text(document.LeaseIdentity) && Hash(document.LeaseNonce) &&
            document.Revisions is { Length: > 0 and <= 16 }, HostDeploymentFailure.JournalInvalid);
        var revisions = document.Revisions!;
        DeploymentRules.Require(revisions.All(row => row is not null && Hash(row.BundleFingerprint) &&
            Enum.IsDefined(row.State) && row.Files is { Count: <= 32 } &&
            (row.State == DeploymentJournalState.Prepared ? row.DirectoryIdentity is null && row.Files.Count == 0 :
                Text(row.DirectoryIdentity)) &&
            row.Files.All(file => file.Key.Length is > 0 and <= 96 &&
                file.Key.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-') &&
                !file.Key.Contains("..", StringComparison.Ordinal) && Text(file.Value))) &&
            revisions.Select(row => row.BundleFingerprint).Distinct().Count() == revisions.Length &&
            revisions.Count(row => row.State != DeploymentJournalState.Published) <= 1,
            HostDeploymentFailure.JournalInvalid);
    }

    private static bool Text(string? value) => value is { Length: > 0 and <= 256 } && !value.Any(char.IsControl);
    private static bool Hash(string? value) => value is { Length: 64 } && value.All(c => char.IsAsciiHexDigitLower(c));
    private static void Unique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                DeploymentRules.Require(names.Add(property.Name), HostDeploymentFailure.JournalInvalid);
                Unique(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) Unique(item);
    }
}

internal sealed record DeploymentStorageSnapshot(string ParentIdentity, string? JournalVersion,
    DeploymentJournal? Journal, DeploymentRevision? Document, string? LeaseIdentity, string StagingPath,
    string FinalPath, string Fingerprint);
