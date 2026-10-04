using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Core.Contracts;

namespace Martlet.Core.Sync;

/// <summary>One remembered fact as it travels between the owner's computers: a last-writer-wins register keyed by the fact's
/// ID. <see cref="Fact"/> is the fact itself (Martlet.Memory's fact JSON, in canonical form), or null once it was forgotten (a
/// tombstone, so the deletion reaches every computer). Hosts never look inside a fact, so a newer Martlet's facts pass through
/// them unchanged.</summary>
public sealed record SharedMemory
{
    public required Guid Id { get; init; }
    /// <summary>The fact's own revision (1 when saved, +1 per edit); a tombstone is the revision it forgot plus one.</summary>
    public required long Revision { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public required string UpdatedBy { get; init; }
    [JsonConverter(typeof(SharedMemories.RawFactConverter))]
    public string? Fact { get; init; }

    [JsonIgnore] public bool Forgotten => Fact is null;

    public override string ToString() => $"Shared memory {Id} r{Revision} by {UpdatedBy}{(Forgotten ? " (forgotten)" : " [content redacted]")}";
}

/// <summary>Everything Martlet remembers, the same on every computer of the owner: one <see cref="SharedMemory"/> per fact,
/// merged per fact by the highest (revision, time, forgotten, writer, content). The merge is commutative, associative and
/// idempotent, so every copy converges whatever order changes arrive in; an edit made on top of another wins by its revision,
/// and two edits of the same revision made apart keep the later one. Paired hosts keep a copy (0600) and give it only to
/// paired devices over their signed, pinned connection; desktops keep the facts in their own memory store.</summary>
public sealed record SharedMemories
{
    public const int SchemaVersion1 = 1;
    public const int MaximumBytes = 12 * 1024 * 1024;
    public const int MaximumFacts = 1024;
    public const int MaximumForgotten = 4096;
    public const int MaximumFactBytes = 64 * 1024;
    private const long MaximumRevision = long.MaxValue / 4;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        MaxDepth = 32
    };

    private static readonly JsonWriterOptions CanonicalWriter = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, Indented = false, MaxDepth = 24
    };

    public required int SchemaVersion { get; init; }
    public required IReadOnlyList<SharedMemory> Facts { get; init; }

    public static SharedMemories Empty { get; } = new() { SchemaVersion = SchemaVersion1, Facts = [] };

    [JsonIgnore] public IEnumerable<SharedMemory> Live => Facts.Where(f => !f.Forgotten);
    [JsonIgnore] public IEnumerable<SharedMemory> Forgotten => Facts.Where(f => f.Forgotten);

    public SharedMemory? Find(Guid id) => Facts.FirstOrDefault(f => f.Id == id);

    /// <summary>Adds <paramref name="entry"/>, keeping whichever of it and this copy's entry for the same fact is newer.</summary>
    public SharedMemories With(SharedMemory entry)
    {
        Validate(entry);
        return Bounded(Facts.Where(f => f.Id != entry.Id).Append(Find(entry.Id) is { } existing && Newer(existing, entry) ? existing : entry));
    }

    /// <summary>Joins two copies: per fact the newest entry wins.</summary>
    public static SharedMemories Merge(SharedMemories left, SharedMemories right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return Bounded(left.Facts.Concat(right.Facts).GroupBy(f => f.Id).Select(group => group.Aggregate((a, b) => Newer(a, b) ? a : b)));
    }

    /// <summary>Whether <paramref name="a"/> wins over <paramref name="b"/>: the higher revision, then the later change, then a
    /// forgotten fact, then the writer and the content (only so equal stamps still decide the same way everywhere).</summary>
    public static bool Newer(SharedMemory a, SharedMemory b) =>
        a.Revision != b.Revision ? a.Revision > b.Revision
        : a.UpdatedAt != b.UpdatedAt ? a.UpdatedAt > b.UpdatedAt
        : a.Forgotten != b.Forgotten ? a.Forgotten
        : a.UpdatedBy != b.UpdatedBy ? string.CompareOrdinal(a.UpdatedBy, b.UpdatedBy) > 0
        : string.CompareOrdinal(a.Fact, b.Fact) >= 0;

    // The newest facts and the newest tombstones up to their limits, sorted by ID so equal content writes equal bytes.
    private static SharedMemories Bounded(IEnumerable<SharedMemory> entries)
    {
        var all = entries.ToArray();
        var live = all.Where(f => !f.Forgotten).OrderByDescending(f => f.UpdatedAt).ThenBy(f => f.Id).Take(MaximumFacts);
        var gone = all.Where(f => f.Forgotten).OrderByDescending(f => f.UpdatedAt).ThenBy(f => f.Id).Take(MaximumForgotten);
        return new() { SchemaVersion = SchemaVersion1, Facts = live.Concat(gone).OrderBy(f => f.Id).ToArray() };
    }

    /// <summary>A fact's JSON in the one form every computer writes it (compact, the same escaping), so the same fact always
    /// compares equal. Throws <see cref="ContractException"/> for anything but a JSON object within the size limit.</summary>
    public static string Canonical(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 24 });
            return Canonical(document.RootElement);
        }
        catch (JsonException)
        {
            throw new ContractException(ErrorCode.InvalidContract, "A shared memory is not valid JSON.");
        }
    }

    private static string Canonical(JsonElement element)
    {
        ContractRules.Require(element.ValueKind == JsonValueKind.Object, "A shared memory must be a JSON object.");
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, CanonicalWriter)) element.WriteTo(writer);
        ContractRules.Require(buffer.WrittenCount <= MaximumFactBytes, "A shared memory is too large.", ErrorCode.PayloadTooLarge);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void Validate(SharedMemory entry)
    {
        ContractRules.Require(entry is not null, "A shared memory is missing.");
        ContractRules.Require(entry!.Id != Guid.Empty, "A shared memory needs an ID.");
        ContractRules.Require(entry.Revision is > 0 and <= MaximumRevision, "A shared memory's revision is out of range.");
        ContractRules.Identifier(entry.UpdatedBy);
        if (entry.Fact is { } fact)
            ContractRules.Require(Encoding.UTF8.GetByteCount(fact) <= MaximumFactBytes, "A shared memory is too large.", ErrorCode.PayloadTooLarge);
    }

    public void Validate()
    {
        ContractRules.Require(SchemaVersion == SchemaVersion1, "These memories were shared by a newer Martlet.", ErrorCode.UnsupportedVersion);
        ContractRules.Require(Facts is not null && Facts.Count <= MaximumFacts + MaximumForgotten, "Too many shared memories.");
        foreach (var entry in Facts!) Validate(entry);
        ContractRules.Require(Facts.Select(f => f.Id).Distinct().Count() == Facts.Count, "A shared memory is listed twice.");
    }

    public byte[] Write()
    {
        Validate();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(Bounded(Facts), Json);
        ContractRules.Require(bytes.Length <= MaximumBytes, "The shared memories are too large.", ErrorCode.PayloadTooLarge);
        return bytes;
    }

    public static SharedMemories Parse(ReadOnlySpan<byte> bytes)
    {
        ContractRules.Require(bytes.Length is > 0 and <= MaximumBytes, "The shared memories are empty or too large.", ErrorCode.PayloadTooLarge);
        SharedMemories? memories;
        try { memories = JsonSerializer.Deserialize<SharedMemories>(bytes, Json); }
        catch (ContractException) { throw; }
        catch (Exception error) when (error is JsonException or NotSupportedException or InvalidOperationException or FormatException)
        {
            throw new ContractException(ErrorCode.InvalidContract, "The shared memories are malformed.");
        }
        ContractRules.Require(memories is not null, "The shared memories are empty.");
        memories!.Validate();
        return Bounded(memories.Facts);
    }

    /// <summary>Identifies the content, to tell whether a copy is current.</summary>
    public string Digest() => Convert.ToHexStringLower(SHA256.HashData(Write()));

    public static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public override string ToString() => $"Shared memories ({Live.Count()} facts, {Forgotten.Count()} forgotten)";

    /// <summary>Writes a fact as the JSON object it is and reads one back in canonical form.</summary>
    internal sealed class RawFactConverter : JsonConverter<string?>
    {
        public override bool HandleNull => false;

        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            return Canonical(document.RootElement);
        }

        public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
        {
            if (value is null) writer.WriteNullValue();
            else writer.WriteRawValue(value, skipInputValidation: false);
        }
    }
}
