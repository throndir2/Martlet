using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Martlet.LocalStt;

internal sealed record PackageFileDocument
{
    public required string Path { get; init; }
    public required long Bytes { get; init; }
    public required string Sha256 { get; init; }
    public required string Purpose { get; init; }
}

internal sealed record PackageReceiptDocument
{
    public required int FormatVersion { get; init; }
    public required string PackageId { get; init; }
    public required string ManifestSha256 { get; init; }
    public required string ImportEvidenceSha256 { get; init; }
    public required string RuntimeRevision { get; init; }
    public required string ModelRevision { get; init; }
    public required string ArchiveSha256 { get; init; }
    public required string ModelSha256 { get; init; }
    public required string SbomSha256 { get; init; }
    public required bool AutomaticAcquisitionPerformed { get; init; }
    public required bool DeniedEgressEvidenceIncluded { get; init; }
    public required PackageFileDocument[] Files { get; init; }
    public required string IntegritySha256 { get; init; }
}

internal sealed record PackageStageOwnerDocument
{
    public required int FormatVersion { get; init; }
    public required string Kind { get; init; }
    public required string TransactionId { get; init; }
    public required string PackageId { get; init; }
    public required string ManifestSha256 { get; init; }
    public required string ImportEvidenceSha256 { get; init; }
    public required string[] NoticeFiles { get; init; }
}

internal sealed record CycloneDxDocument
{
    [JsonPropertyName("bomFormat")]
    public required string BomFormat { get; init; }
    [JsonPropertyName("specVersion")]
    public required string SpecVersion { get; init; }
    public required int Version { get; init; }
    public required CycloneDxMetadata Metadata { get; init; }
    public required CycloneDxComponent[] Components { get; init; }
    public required CycloneDxDependency[] Dependencies { get; init; }
}

internal sealed record CycloneDxMetadata
{
    public required CycloneDxTool Tools { get; init; }
    public required CycloneDxProperty[] Properties { get; init; }
}

internal sealed record CycloneDxTool
{
    public required CycloneDxComponent[] Components { get; init; }
}

internal sealed record CycloneDxComponent
{
    public required string Type { get; init; }
    [JsonPropertyName("bom-ref")]
    public required string BomRef { get; init; }
    public required string Name { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Version { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CycloneDxHash[]? Hashes { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CycloneDxLicense[]? Licenses { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CycloneDxProperty[]? Properties { get; init; }
}

internal sealed record CycloneDxHash
{
    public required string Alg { get; init; }
    public required string Content { get; init; }
}

internal sealed record CycloneDxLicense
{
    public required CycloneDxLicenseId License { get; init; }
}

internal sealed record CycloneDxLicenseId
{
    public required string Id { get; init; }
}

internal sealed record CycloneDxProperty
{
    public required string Name { get; init; }
    public required string Value { get; init; }
}

internal sealed record CycloneDxDependency
{
    public required string Ref { get; init; }
    public required string[] DependsOn { get; init; }
}

internal static class ProvisioningWire
{
    internal const int MaximumReceiptBytes = 1_048_576;
    internal const int MaximumOwnerBytes = 16_384;
    internal const int MaximumSbomBytes = 1_048_576;
    private static readonly JsonSerializerOptions Options = CreateOptions();

    internal static byte[] Write<T>(T value) =>
        JsonSerializer.SerializeToUtf8Bytes(value, Options);

    internal static T Read<T>(
        ReadOnlyMemory<byte> bytes,
        int maximum,
        LocalSttProvisioningFailure failure,
        bool canonical = false)
    {
        if (bytes.IsEmpty || bytes.Length > maximum)
            throw new LocalSttProvisioningException(failure);
        try
        {
            RejectDuplicateProperties(bytes.Span);
            var value = JsonSerializer.Deserialize<T>(bytes.Span, Options)
                ?? throw new JsonException();
            if (canonical && !Write(value).AsSpan().SequenceEqual(bytes.Span))
                throw new JsonException();
            return value;
        }
        catch (Exception error) when (
            error is JsonException or NotSupportedException or OverflowException)
        {
            throw new LocalSttProvisioningException(failure);
        }
    }

    internal static byte[] WriteReceipt(PackageReceiptDocument document)
    {
        var unsealed = Write(document with { IntegritySha256 = "" });
        var integrity = Hash(unsealed);
        var bytes = Write(document with { IntegritySha256 = integrity });
        if (bytes.Length > MaximumReceiptBytes)
            throw new LocalSttProvisioningException(
                LocalSttProvisioningFailure.InvalidReceipt);
        return bytes;
    }

    internal static PackageReceiptDocument ReadReceipt(ReadOnlyMemory<byte> bytes)
    {
        var document = Read<PackageReceiptDocument>(
            bytes,
            MaximumReceiptBytes,
            LocalSttProvisioningFailure.InvalidReceipt,
            canonical: true);
        ProvisioningGuard.Sha256(
            document.IntegritySha256,
            LocalSttProvisioningFailure.InvalidReceipt);
        var unsealed = Write(document with { IntegritySha256 = "" });
        if (!FixedHashEquals(Hash(unsealed), document.IntegritySha256))
            throw new LocalSttProvisioningException(
                LocalSttProvisioningFailure.InvalidReceipt);
        return document;
    }

    internal static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    internal static bool FixedHashEquals(string first, string second)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(first),
                Convert.FromHexString(second));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static void RejectDuplicateProperties(ReadOnlySpan<byte> bytes)
    {
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 16
        });
        var objects = new Stack<HashSet<string>>();
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    objects.Push(new(StringComparer.Ordinal));
                    break;
                case JsonTokenType.EndObject:
                    if (objects.Count == 0)
                        throw new JsonException();
                    objects.Pop();
                    break;
                case JsonTokenType.PropertyName:
                    if (objects.Count == 0 ||
                        !objects.Peek().Add(reader.GetString()!))
                        throw new JsonException();
                    break;
            }
        }
        if (objects.Count != 0)
            throw new JsonException();
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
            MaxDepth = 16,
            AllowTrailingCommas = false,
            ReadCommentHandling = JsonCommentHandling.Disallow
        };
        options.Converters.Add(new JsonStringEnumConverter(
            JsonNamingPolicy.SnakeCaseLower,
            allowIntegerValues: false));
        return options;
    }
}
