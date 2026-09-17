using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Martlet.Updates;

internal sealed record CandidateEnvelope
{
    public required byte[] Manifest { get; init; }
    public required byte[] Signature { get; init; }
}

internal sealed record CandidateManifest
{
    public required int FormatVersion { get; init; }
    public required int MinimumReaderFormat { get; init; }
    public required string Application { get; init; }
    public required string Algorithm { get; init; }
    public required string SignerId { get; init; }
    public required string ApplicationVersion { get; init; }
    public required string Rid { get; init; }
    public required int SettingsMinimumReader { get; init; }
    public required int SettingsMaximumReader { get; init; }
    public required long ArchiveBytes { get; init; }
    public required string ArchiveSha256 { get; init; }
    public required PayloadFile[] Files { get; init; }
}

internal sealed record PayloadFile
{
    public required string Path { get; init; }
    public required long Bytes { get; init; }
    public required string Sha256 { get; init; }
}

internal sealed record InternalPayloadManifest
{
    public required int SchemaVersion { get; init; }
    public required string Channel { get; init; }
    public required string ApplicationVersion { get; init; }
    public required string Rid { get; init; }
    public required string SdkVersion { get; init; }
    public required string RuntimeVersion { get; init; }
    public required string SourceCommit { get; init; }
    public required bool SourceDirty { get; init; }
    public required PayloadFile[] Files { get; init; }
}

internal sealed record ReceiptDocument
{
    public required int FormatVersion { get; init; }
    public required string Destination { get; init; }
    public required InstalledVersionFacts Installed { get; init; }
    public required string EnvelopeSha256 { get; init; }
    public required string ManifestSha256 { get; init; }
    public required CandidateManifest Candidate { get; init; }
    public required string NextSteps { get; init; }
}

internal sealed record VerifiedCandidate(CandidateManifest Manifest, byte[] ManifestBytes, byte[] EnvelopeBytes,
    long ExpandedBytes, int DirectoryCount);

internal static class Wire
{
    internal const int MaximumManifestBytes = 2 * 1024 * 1024;
    internal const int MaximumEnvelopeBytes = 3 * 1024 * 1024;
    internal const int MaximumReceiptBytes = 3 * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        MaxDepth = 16
    };

    internal static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    internal static bool IsHash(string? text) => IsHex(text, 64);
    internal static bool IsHex(string? text, int length) =>
        text is not null && text.Length == length && text.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    internal static Version Version(string? text)
    {
        if (text is not { Length: > 0 and <= 48 } || !System.Version.TryParse(text, out var version) ||
            version.Revision < 0 || text != version.ToString(4))
            throw new StagingException(StagingFailure.InvalidVersion);
        return version;
    }

    internal static byte[] Write<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);
    internal static T Read<T>(byte[] bytes, int maximum, bool canonical = false)
    {
        if (bytes.Length == 0 || bytes.Length > maximum) throw new StagingException(StagingFailure.CapacityExceeded);
        try
        {
            using var doc = ReadDocument(bytes, maximum, CancellationToken.None);
            var value = JsonSerializer.Deserialize<T>(bytes, Options);
            if (value is null || canonical && !Write(value).AsSpan().SequenceEqual(bytes))
                throw new StagingException(StagingFailure.InvalidManifest);
            return value;
        }
        catch (JsonException) { throw new StagingException(StagingFailure.InvalidManifest); }
    }

    internal static JsonDocument ReadDocument(byte[] bytes, int maximum, CancellationToken token)
    {
        if (bytes.Length == 0 || bytes.Length > maximum) throw new StagingException(StagingFailure.CapacityExceeded);
        token.ThrowIfCancellationRequested();
        JsonDocument document;
        try { document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 }); }
        catch (JsonException) { throw new StagingException(StagingFailure.InvalidManifest); }
        try
        {
            CheckDuplicates(document.RootElement, token);
            return document;
        }
        catch (InvalidOperationException)
        {
            document.Dispose();
            throw new StagingException(StagingFailure.InvalidManifest);
        }
        catch { document.Dispose(); throw; }
    }

    private static void CheckDuplicates(JsonElement value, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new StagingException(StagingFailure.InvalidManifest);
                CheckDuplicates(property.Value, token);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) CheckDuplicates(item, token);
    }
}
