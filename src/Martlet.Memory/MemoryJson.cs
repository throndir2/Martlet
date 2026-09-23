using System.Text.Json;
using System.Text.Json.Serialization;

namespace Martlet.Memory;

internal sealed record MemoryStoreDocument
{
    public required int SchemaVersion { get; init; }
    public required Guid StoreId { get; init; }
    public required long StoreRevision { get; init; }
    public required DateTimeOffset UpdatedAtUtc { get; init; }
    public required MemoryFact[] Facts { get; init; }
}

internal sealed record MemoryExportDocument
{
    public required int SchemaVersion { get; init; }
    public required Guid ExportId { get; init; }
    public required DateTimeOffset ExportedAtUtc { get; init; }
    public required long StoreRevision { get; init; }
    public required MemoryFact[] Facts { get; init; }
}

internal static class MemoryJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        MaxDepth = 24,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false) }
    };

    internal static byte[] WriteStore(MemoryStoreDocument document)
    {
        ValidateStore(document);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, Options);
        MemoryGuard.Require(bytes.Length <= MemoryLimits.MaximumStoreBytes, MemoryFailure.LimitExceeded);
        return bytes;
    }

    internal static MemoryStoreDocument ReadStore(ReadOnlySpan<byte> bytes)
    {
        MemoryGuard.Require(bytes is { Length: > 0 } && bytes.Length <= MemoryLimits.MaximumStoreBytes,
            MemoryFailure.LimitExceeded);
        try
        {
            RejectDuplicateProperties(bytes);
            var document = JsonSerializer.Deserialize<MemoryStoreDocument>(bytes, Options);
            MemoryGuard.Require(document is not null, MemoryFailure.CorruptStore);
            ValidateStore(document!);
            return document!;
        }
        catch (MemoryException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw new MemoryException(MemoryFailure.CorruptStore);
        }
        catch (NotSupportedException)
        {
            throw new MemoryException(MemoryFailure.CorruptStore);
        }
    }

    internal static byte[] WriteExport(MemoryExportDocument document)
    {
        MemoryGuard.Require(document.SchemaVersion == MemoryLimits.SchemaVersion, MemoryFailure.UnsupportedVersion);
        MemoryGuard.Require(document.ExportId != Guid.Empty && document.StoreRevision >= 0 &&
            document.Facts is { Length: <= MemoryLimits.MaximumFacts });
        MemoryGuard.Utc(document.ExportedAtUtc);
        ValidateFacts(document.Facts);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, Options);
        MemoryGuard.Require(bytes.Length <= MemoryLimits.MaximumExportBytes, MemoryFailure.LimitExceeded);
        return bytes;
    }

    private static void ValidateStore(MemoryStoreDocument document)
    {
        MemoryGuard.Require(document.SchemaVersion == MemoryLimits.SchemaVersion, MemoryFailure.UnsupportedVersion);
        MemoryGuard.Require(document.StoreId != Guid.Empty &&
            document.StoreRevision is >= 0 and <= MemoryLimits.MaximumRevision &&
            document.Facts is { Length: <= MemoryLimits.MaximumFacts }, MemoryFailure.CorruptStore);
        MemoryGuard.Utc(document.UpdatedAtUtc);
        ValidateFacts(document.Facts);
        MemoryGuard.Require(document.Facts.All(fact => fact.UpdatedAtUtc <= document.UpdatedAtUtc),
            MemoryFailure.CorruptStore);
    }

    private static void ValidateFacts(MemoryFact[] facts)
    {
        var ids = new HashSet<Guid>();
        foreach (var fact in facts)
        {
            if (fact is null || !ids.Add(fact.Id))
                throw new MemoryException(MemoryFailure.CorruptStore);
            fact.ValidatePersisted();
        }
    }

    private static void RejectDuplicateProperties(ReadOnlySpan<byte> bytes)
    {
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = Options.MaxDepth
        });
        var scopes = new Stack<HashSet<string>?>();
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    scopes.Push(new(StringComparer.Ordinal));
                    break;
                case JsonTokenType.StartArray:
                    scopes.Push(null);
                    break;
                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                    MemoryGuard.Require(scopes.Count > 0, MemoryFailure.CorruptStore);
                    scopes.Pop();
                    break;
                case JsonTokenType.PropertyName:
                    MemoryGuard.Require(scopes.TryPeek(out var names) && names is not null &&
                        names.Add(reader.GetString()!), MemoryFailure.CorruptStore);
                    break;
            }
        }
        MemoryGuard.Require(scopes.Count == 0, MemoryFailure.CorruptStore);
    }
}
