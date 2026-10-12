using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Core.Contracts;

namespace Martlet.Core.Sync;

/// <summary>One recorded exchange as it travels between an account's computers: a last-writer-wins register keyed by the
/// exchange's ID. <see cref="Exchange"/> is the exchange's line in the record (Martlet.Conversation's month file line, a JSON
/// object in canonical form), or null once it was deleted (a tombstone, so the deletion reaches every computer and the exchange
/// never comes back). Hosts never look inside an exchange, so a newer Martlet's lines pass through them unchanged.</summary>
public sealed record SharedExchange
{
    public required Guid Id { get; init; }
    /// <summary>1 when recorded, +1 per edit made on any computer; a tombstone is the revision it deleted plus one.</summary>
    public required long Revision { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public required string UpdatedBy { get; init; }
    /// <remarks>Always in <see cref="SharedConversations.Canonical(string)"/> form, so every computer compares it the same way.</remarks>
    [JsonConverter(typeof(SharedConversations.RawExchangeConverter))]
    public string? Exchange { get; init; }

    [JsonIgnore] public bool Deleted => Exchange is null;

    public override string ToString() => $"Shared exchange {Id} r{Revision} by {UpdatedBy}{(Deleted ? " (deleted)" : " [content redacted]")}";
}

/// <summary>One month of an account's record of conversations (docs/MEMORY.md, "One record on every computer"), the same on
/// every computer where the account is signed in: one <see cref="SharedExchange"/> per exchange recorded in that UTC month,
/// merged per exchange by the highest (revision, time, deleted, writer, content). The merge is commutative, associative and
/// idempotent, so every copy converges whatever order changes arrive in. Paired hosts keep a copy per account and month (0600)
/// and give it only to paired devices where the account is signed in, over their signed, pinned connection; desktops keep the
/// exchanges in their own month files.</summary>
public sealed record SharedConversations
{
    public const int SchemaVersion1 = 1;
    /// <summary>A month's copy is read whole (by hosts too), so it keeps a byte bound.</summary>
    public const int MaximumBytes = 256 * 1024 * 1024;
    /// <summary>The most exchanges one month keeps, and the most tombstones besides them.</summary>
    public const int MaximumExchanges = 100_000;
    public const int MaximumDeleted = 100_000;
    /// <summary>One exchange's canonical JSON: the longest exchange of the record (8,192 characters said and 16,384 replied,
    /// each escaped at worst) with its fields.</summary>
    public const int MaximumExchangeBytes = 256 * 1024;
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
    /// <summary>The UTC month the exchanges were recorded in, <c>yyyy-MM</c>.</summary>
    public required string Month { get; init; }
    public required IReadOnlyList<SharedExchange> Exchanges { get; init; }

    /// <summary>A month with nothing in it.</summary>
    public static SharedConversations Empty(string month)
    {
        RequireMonth(month);
        return new() { SchemaVersion = SchemaVersion1, Month = month, Exchanges = [] };
    }

    [JsonIgnore] public IEnumerable<SharedExchange> Live => Exchanges.Where(e => !e.Deleted);
    [JsonIgnore] public IEnumerable<SharedExchange> Deleted => Exchanges.Where(e => e.Deleted);

    public SharedExchange? Find(Guid id) => Exchanges.FirstOrDefault(e => e.Id == id);

    /// <summary>Whether <paramref name="month"/> is a month ID: <c>yyyy-MM</c>, year 2000-2999.</summary>
    public static bool IsMonth([NotNullWhen(true)] string? month) =>
        month is { Length: 7 } && month[0] == '2' && char.IsAsciiDigit(month[1]) && char.IsAsciiDigit(month[2]) && char.IsAsciiDigit(month[3]) &&
        month[4] == '-' && (month[5], month[6]) is ('0', >= '1' and <= '9') or ('1', >= '0' and <= '2');

    /// <summary>The month an exchange recorded at <paramref name="at"/> belongs to (its UTC month).</summary>
    public static string MonthOf(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Joins two copies of the same month: per exchange the newest entry wins.</summary>
    public static SharedConversations Merge(SharedConversations left, SharedConversations right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        ContractRules.Require(left.Month == right.Month, "Two different months can't be merged.");
        return Of(left.Month, left.Exchanges.Concat(right.Exchanges));
    }

    /// <summary>A copy of <paramref name="month"/> holding <paramref name="entries"/> (each checked): per exchange the newest
    /// entry wins.</summary>
    public static SharedConversations Of(string month, IEnumerable<SharedExchange> entries)
    {
        RequireMonth(month);
        ArgumentNullException.ThrowIfNull(entries);
        var newest = new Dictionary<Guid, SharedExchange>();
        foreach (var raw in entries)
        {
            Validate(raw);
            // One form of each time everywhere, so equal entries write equal bytes.
            var entry = raw.UpdatedAt.Offset == TimeSpan.Zero ? raw : raw with { UpdatedAt = raw.UpdatedAt.ToUniversalTime() };
            if (!newest.TryGetValue(entry.Id, out var existing) || !Newer(existing, entry)) newest[entry.Id] = entry;
        }
        return Bounded(month, newest.Values);
    }

    /// <summary>Whether <paramref name="a"/> wins over <paramref name="b"/>: the higher revision, then the later change, then a
    /// deletion, then the writer and the content (only so equal stamps still decide the same way everywhere).</summary>
    public static bool Newer(SharedExchange a, SharedExchange b) =>
        a.Revision != b.Revision ? a.Revision > b.Revision
        : a.UpdatedAt != b.UpdatedAt ? a.UpdatedAt > b.UpdatedAt
        : a.Deleted != b.Deleted ? a.Deleted
        : a.UpdatedBy != b.UpdatedBy ? string.CompareOrdinal(a.UpdatedBy, b.UpdatedBy) > 0
        : string.CompareOrdinal(a.Exchange, b.Exchange) >= 0;

    // The newest exchanges and the newest tombstones up to their limits, sorted by ID so equal content writes equal bytes.
    private static SharedConversations Bounded(string month, IEnumerable<SharedExchange> entries)
    {
        var all = entries.ToArray();
        var live = all.Where(e => !e.Deleted).OrderByDescending(e => e.UpdatedAt).ThenBy(e => e.Id).Take(MaximumExchanges);
        var gone = all.Where(e => e.Deleted).OrderByDescending(e => e.UpdatedAt).ThenBy(e => e.Id).Take(MaximumDeleted);
        return new() { SchemaVersion = SchemaVersion1, Month = month, Exchanges = live.Concat(gone).OrderBy(e => e.Id).ToArray() };
    }

    /// <summary>An exchange's JSON in the one form every computer writes it (compact, the same escaping), so the same exchange
    /// always compares equal. Throws <see cref="ContractException"/> for anything but a JSON object within the size limit.</summary>
    public static string Canonical(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 24 });
            return Canonical(document.RootElement);
        }
        catch (JsonException)
        {
            throw new ContractException(ErrorCode.InvalidContract, "A shared exchange is not valid JSON.");
        }
    }

    private static string Canonical(JsonElement element)
    {
        ContractRules.Require(element.ValueKind == JsonValueKind.Object, "A shared exchange must be a JSON object.");
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, CanonicalWriter)) element.WriteTo(writer);
        ContractRules.Require(buffer.WrittenCount <= MaximumExchangeBytes, "A shared exchange is too large.", ErrorCode.PayloadTooLarge);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void RequireMonth(string month) =>
        ContractRules.Require(IsMonth(month), "A month of the record must be yyyy-MM.");

    private static void Validate(SharedExchange entry)
    {
        ContractRules.Require(entry is not null, "A shared exchange is missing.");
        ContractRules.Require(entry!.Id != Guid.Empty, "A shared exchange needs an ID.");
        ContractRules.Require(entry.Revision is > 0 and <= MaximumRevision, "A shared exchange's revision is out of range.");
        ContractRules.Identifier(entry.UpdatedBy);
        if (entry.Exchange is { } exchange)
            ContractRules.Require(Encoding.UTF8.GetByteCount(exchange) <= MaximumExchangeBytes, "A shared exchange is too large.",
                ErrorCode.PayloadTooLarge);
    }

    public void Validate()
    {
        ContractRules.Require(SchemaVersion == SchemaVersion1, "This record was shared by a newer Martlet.", ErrorCode.UnsupportedVersion);
        RequireMonth(Month);
        ContractRules.Require(Exchanges is not null && Exchanges.Count <= MaximumExchanges + MaximumDeleted, "Too many shared exchanges.");
        foreach (var entry in Exchanges!) Validate(entry);
        ContractRules.Require(Exchanges.Select(e => e.Id).Distinct().Count() == Exchanges.Count, "A shared exchange is listed twice.");
    }

    public byte[] Write()
    {
        Validate();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(Bounded(Month, Exchanges), Json);
        ContractRules.Require(bytes.Length <= MaximumBytes, "The shared month of the record is too large.", ErrorCode.PayloadTooLarge);
        return bytes;
    }

    public static SharedConversations Parse(ReadOnlySpan<byte> bytes)
    {
        ContractRules.Require(bytes.Length is > 0 and <= MaximumBytes, "The shared month of the record is empty or too large.",
            ErrorCode.PayloadTooLarge);
        SharedConversations? month;
        try { month = JsonSerializer.Deserialize<SharedConversations>(bytes, Json); }
        catch (ContractException) { throw; }
        catch (Exception error) when (error is JsonException or NotSupportedException or InvalidOperationException or FormatException)
        {
            throw new ContractException(ErrorCode.InvalidContract, "The shared month of the record is malformed.");
        }
        ContractRules.Require(month is not null, "The shared month of the record is empty.");
        month!.Validate();
        return Of(month.Month, month.Exchanges);
    }

    /// <summary>Identifies the content, to tell whether a copy is current.</summary>
    public string Digest() => Convert.ToHexStringLower(SHA256.HashData(Write()));

    public static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public override string ToString() => $"Shared record {Month} ({Live.Count()} exchanges, {Deleted.Count()} deleted)";

    /// <summary>Writes an exchange as the JSON object it is and reads one back in canonical form.</summary>
    internal sealed class RawExchangeConverter : JsonConverter<string?>
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

/// <summary>Why a host couldn't be used for the record of conversations.</summary>
public enum ConversationHostFailure
{
    /// <summary>The host didn't answer, or answered something unusable; tried again on the next check.</summary>
    Unreachable,
    /// <summary>The host runs a Martlet older than the shared record.</summary>
    Old,
    /// <summary>The host doesn't let this computer use the account's record (it hasn't learned that the account signed in here).</summary>
    Denied
}

public sealed class ConversationHostException(string message, ConversationHostFailure failure, Exception? inner = null) : Exception(message, inner)
{
    public ConversationHostFailure Failure { get; } = failure;
}

/// <summary>One paired host as the record sync sees it, for one account: the months it keeps (each with its digest), one month's
/// copy, and merging a month into it. Failures throw <see cref="ConversationHostException"/>.</summary>
public interface IConversationRecordHost
{
    string HostId { get; }

    /// <summary>Each month the host keeps for the account, with the digest of its copy (<see cref="SharedConversations.Digest"/>).</summary>
    Task<IReadOnlyDictionary<string, string>> MonthsAsync(CancellationToken token);

    /// <summary>The host's copy of <paramref name="month"/>; empty when it keeps none.</summary>
    Task<SharedConversations> ReadAsync(string month, CancellationToken token);

    /// <summary>Merges <paramref name="copy"/> into the host's copy of its month and returns the merged copy.</summary>
    Task<SharedConversations> MergeAsync(SharedConversations copy, CancellationToken token);
}
