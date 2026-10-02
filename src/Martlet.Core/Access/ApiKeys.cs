using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Core.Contracts;

namespace Martlet.Core.Access;

/// <summary>What an API key may do on the owner's hosts (docs/API.md). Hosts keep any scope name, so a newer desktop's
/// scopes pass through older hosts; a scope a host does not know grants nothing there.</summary>
public static class ApiKeyScopes
{
    /// <summary>See each host: version, capabilities, status, hardware, who does what, logs and commands.</summary>
    public const string Read = "read";
    /// <summary>Use the hosts' thinking, listening, speaking and lip-sync engines (the gateway's voice routes).</summary>
    public const string Voice = "voice";
    /// <summary>Use the hosts' screen-understanding engines (the gateway's perception routes).</summary>
    public const string Perception = "perception";
    /// <summary>Send hosts commands: update Martlet, add or remove a role, show status; cancel commands.</summary>
    public const string Manage = "manage";
    public static readonly IReadOnlyList<string> All = [Read, Voice, Perception, Manage];

    public static string Title(string scope) => scope switch
    {
        Read => "See status and logs",
        Voice => "Use thinking, listening, speaking and lip-sync",
        Perception => "Use screen understanding",
        Manage => "Update hosts and change their roles",
        _ => scope
    };
}

/// <summary>One API key of the owner's Martlet network. The secret itself is never stored: <see cref="Verifier"/> is the
/// SHA-256 of its 32 random bytes. <see cref="Revoked"/> is a tombstone that always wins a merge, so an older copy can never
/// bring a revoked key back.</summary>
public sealed record ApiKey
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public IReadOnlyList<string> Scopes { get; init; } = [];
    public string? Verifier { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required string CreatedBy { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public bool Revoked { get; init; }
    public required long Revision { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public required string UpdatedBy { get; init; }

    public bool Expired(DateTimeOffset now) => ExpiresAt is { } expires && expires <= now;

    public bool Live(DateTimeOffset now) => !Revoked && !Expired(now);

    public bool Allows(string scope) => Scopes.Contains(scope, StringComparer.Ordinal);

    internal string Content => $"{Name}|{string.Join(',', Scopes)}|{Verifier}|{ExpiresAt?.UtcTicks}|{Revoked}|{UpdatedAt.UtcTicks}";
}

/// <summary>A key just created: <see cref="Token"/> is shown to the owner once and kept nowhere.</summary>
public sealed record IssuedApiKey(ApiKey Key, string Token)
{
    public override string ToString() => $"API key {Key.Id} ({Key.Name})";
}

/// <summary>Why a presented key was refused, matching the gateway's failure codes.</summary>
public enum ApiKeyRefusal { None, Invalid, Revoked, Expired }

/// <summary>The text form of a key: <c>martlet_&lt;id&gt;.&lt;secret&gt;</c>, a 16-byte ID and 32 secret bytes in
/// base64url. The fixed prefix lets secret scanners recognize a leaked key.</summary>
public static class ApiKeyToken
{
    public const string Prefix = "martlet_";
    public const int Length = 8 + 22 + 1 + 43;

    public static string Format(string id, ReadOnlySpan<byte> secret) => Prefix + id + "." + Base64Url.EncodeToString(secret);

    public static bool TryParse(string? token, out string id, out byte[] secret)
    {
        id = "";
        secret = [];
        if (token is not { Length: Length } || !token.StartsWith(Prefix, StringComparison.Ordinal) || token[30] != '.') return false;
        var candidate = token[8..30];
        if (!ApiKeyList.IsId(candidate) || !TryDecode(token[31..], 32, out var bytes)) return false;
        id = candidate;
        secret = bytes;
        return true;
    }

    /// <summary>The verifier hosts keep for a secret: lowercase hex SHA-256.</summary>
    public static string Verifier(ReadOnlySpan<byte> secret) => Convert.ToHexStringLower(SHA256.HashData(secret));

    internal static bool TryDecode(string text, int length, out byte[] bytes)
    {
        bytes = [];
        if (text.Length != (length * 8 + 5) / 6 || !text.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) return false;
        try
        {
            var decoded = Base64Url.DecodeFromChars(text);
            if (decoded.Length != length || Base64Url.EncodeToString(decoded) != text) return false;
            bytes = decoded;
            return true;
        }
        catch (FormatException) { return false; }
    }
}

/// <summary>The API keys of the owner's Martlet network, shared like the cluster plan: every paired host keeps a copy
/// (api-keys.json beside host.json) and every desktop merges its copy into each host, so a key created or revoked on one
/// computer reaches every host. One last-writer-wins entry per key with hybrid revisions, except that a revoked entry
/// always wins. Holds no usable secret: names, scopes, SHA-256 verifiers and stamps.</summary>
public sealed record ApiKeyList
{
    public const int MaximumBytes = 32_768;
    public const int MaximumKeys = 64;
    public const int MaximumLiveKeys = 32;
    public const int MaximumScopes = 8;
    public const int MaximumNameCharacters = 64;
    private const long MaximumRevision = long.MaxValue / 4;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        MaxDepth = 6
    };

    public required int SchemaVersion { get; init; }
    public required IReadOnlyList<ApiKey> Keys { get; init; }

    public static ApiKeyList Empty { get; } = new() { SchemaVersion = 1, Keys = [] };

    [JsonIgnore]
    public long Revision => Keys.Select(k => k.Revision).DefaultIfEmpty(0).Max();

    public long NextRevision(DateTimeOffset now) => Math.Max(Revision + 1, now.ToUnixTimeMilliseconds());

    public ApiKey? Find(string id) => Keys.FirstOrDefault(k => k.Id == id);

    public IReadOnlyList<ApiKey> Live(DateTimeOffset now) => Keys.Where(k => k.Live(now)).ToArray();

    /// <summary>Creates a key named <paramref name="name"/> with <paramref name="scopes"/>, recorded by <paramref name="by"/>.
    /// The returned token is the only copy of its secret.</summary>
    public (ApiKeyList List, IssuedApiKey Issued) Create(string name, IEnumerable<string> scopes, DateTimeOffset? expiresAt,
        string by, DateTimeOffset now)
    {
        var chosen = scopes.Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToArray();
        ContractRules.Require(chosen.Length > 0, "Choose what the key may do.");
        ContractRules.Require(Keys.Count(k => !k.Revoked) < MaximumLiveKeys,
            $"Your network already has {MaximumLiveKeys} API keys; revoke one you no longer use first.");
        var secret = RandomNumberGenerator.GetBytes(32);
        try
        {
            string id;
            do id = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));
            while (Find(id) is not null);
            var at = now.ToUniversalTime();
            var key = new ApiKey
            {
                Id = id, Name = name.Trim(), Scopes = chosen, Verifier = ApiKeyToken.Verifier(secret), CreatedAt = at, CreatedBy = by,
                ExpiresAt = expiresAt?.ToUniversalTime(), Revision = NextRevision(now), UpdatedAt = at, UpdatedBy = by
            };
            var list = this with { Keys = Sorted(Keys.Append(key)) };
            list.Validate();
            return (list, new IssuedApiKey(key, ApiKeyToken.Format(id, secret)));
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }

    /// <summary>Revokes key <paramref name="id"/> everywhere this list reaches. Its verifier is dropped.</summary>
    public ApiKeyList Revoke(string id, string by, DateTimeOffset now)
    {
        var key = Find(id) ?? throw new ContractException(ErrorCode.InvalidContract, "No such API key.");
        if (key.Revoked) return this;
        var at = now.ToUniversalTime();
        var revoked = key with { Revoked = true, Verifier = null, Revision = NextRevision(now), UpdatedAt = at, UpdatedBy = by };
        return this with { Keys = Sorted(Keys.Where(k => k.Id != id).Append(revoked)) };
    }

    /// <summary>Finds the live key <paramref name="token"/> names and checks its secret in constant time.</summary>
    public (ApiKey? Key, ApiKeyRefusal Refusal) Match(string? token, DateTimeOffset now)
    {
        if (!ApiKeyToken.TryParse(token, out var id, out var secret)) return (null, ApiKeyRefusal.Invalid);
        try
        {
            if (Find(id) is not { } key) return (null, ApiKeyRefusal.Invalid);
            if (key.Revoked) return (null, ApiKeyRefusal.Revoked);
            var presented = Encoding.ASCII.GetBytes(ApiKeyToken.Verifier(secret));
            var expected = Encoding.ASCII.GetBytes(key.Verifier ?? "");
            if (!CryptographicOperations.FixedTimeEquals(presented, expected)) return (null, ApiKeyRefusal.Invalid);
            return key.Expired(now) ? (null, ApiKeyRefusal.Expired) : (key, ApiKeyRefusal.None);
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }

    /// <summary>Joins two copies: per key a revoked entry wins, otherwise the newest (revision, writer, content).</summary>
    public static ApiKeyList Merge(ApiKeyList left, ApiKeyList right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        var keys = left.Keys.Concat(right.Keys)
            .GroupBy(k => k.Id, StringComparer.Ordinal)
            .Select(group => group.Aggregate((a, b) => a.Revoked != b.Revoked ? (a.Revoked ? a : b)
                : Newer(a.Revision, a.UpdatedBy, a.Content, b.Revision, b.UpdatedBy, b.Content) ? a : b))
            .OrderBy(k => k.Revoked).ThenByDescending(k => k.Revision).ThenBy(k => k.Id, StringComparer.Ordinal)
            .Take(MaximumKeys).ToArray();
        return new() { SchemaVersion = 1, Keys = Sorted(keys) };
    }

    private static bool Newer(long revision, string by, string content, long otherRevision, string otherBy, string otherContent) =>
        revision != otherRevision ? revision > otherRevision
        : by != otherBy ? string.CompareOrdinal(by, otherBy) > 0
        : string.CompareOrdinal(content, otherContent) >= 0;

    public static bool IsId(string? value) => value is { Length: 22 } && ApiKeyToken.TryDecode(value, 16, out _);

    public static bool IsScope(string? value) => value is { Length: > 0 and <= 32 } && char.IsAsciiLetterLower(value[0]) &&
        value.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');

    public void Validate()
    {
        ContractRules.Require(SchemaVersion == 1, "This API key list was written by a newer Martlet.", ErrorCode.UnsupportedVersion);
        ContractRules.Require(Keys is { Count: <= MaximumKeys } && Keys.All(k => k is not null), "The API key list is invalid.");
        ContractRules.Require(Keys.Select(k => k.Id).Distinct(StringComparer.Ordinal).Count() == Keys.Count, "An API key is listed twice.");
        foreach (var key in Keys)
        {
            ContractRules.Require(IsId(key.Id), "An API key ID is invalid.");
            ContractRules.Require(key.Name is { Length: > 0 and <= MaximumNameCharacters } && key.Name.Trim().Length > 0 &&
                !key.Name.Any(char.IsControl), "An API key name is invalid.");
            ContractRules.Require(key.Scopes is { Count: <= MaximumScopes } && key.Scopes.All(IsScope) &&
                key.Scopes.Distinct(StringComparer.Ordinal).Count() == key.Scopes.Count, "An API key's scopes are invalid.");
            ContractRules.Require(key.Revoked ? key.Verifier is null
                : key.Scopes.Count > 0 && key.Verifier is { Length: 64 } verifier && verifier.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f'),
                "An API key's verifier is invalid.");
            ContractRules.Identifier(key.CreatedBy);
            ContractRules.Identifier(key.UpdatedBy);
            ContractRules.Require(key.Revision is > 0 and <= MaximumRevision, "An API key revision is out of range.");
            ContractRules.Require(key.ExpiresAt is null || key.ExpiresAt > key.CreatedAt, "An API key expires before it was created.");
        }
    }

    public byte[] Write()
    {
        Validate();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(this with { Keys = Sorted(Keys) }, Json);
        ContractRules.Require(bytes.Length <= MaximumBytes, "The API key list is too large.", ErrorCode.PayloadTooLarge);
        return bytes;
    }

    public static ApiKeyList Parse(ReadOnlySpan<byte> bytes)
    {
        ContractRules.Require(bytes.Length is > 0 and <= MaximumBytes, "The API key list is empty or too large.", ErrorCode.PayloadTooLarge);
        ApiKeyList? list;
        try { list = JsonSerializer.Deserialize<ApiKeyList>(bytes, Json); }
        catch (Exception error) when (error is JsonException or NotSupportedException or InvalidOperationException)
        {
            throw new ContractException(ErrorCode.InvalidContract, "The API key list is malformed.");
        }
        ContractRules.Require(list is not null, "The API key list is empty.");
        list!.Validate();
        return list with { Keys = Sorted(list.Keys) };
    }

    /// <summary>Identifies the list's content, to tell whether a copy is current.</summary>
    public string Digest() => Convert.ToHexStringLower(SHA256.HashData(Write()));

    private static ApiKey[] Sorted(IEnumerable<ApiKey> keys) => keys.OrderBy(k => k.Id, StringComparer.Ordinal).ToArray();

    public override string ToString() => $"API keys r{Revision} ({Keys.Count(k => !k.Revoked)} live, {Keys.Count(k => k.Revoked)} revoked)";
}
