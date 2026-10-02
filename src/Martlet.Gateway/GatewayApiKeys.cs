using System.Collections.Concurrent;
using Martlet.Core.Access;
using Martlet.Core.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Martlet.Gateway;

/// <summary>Where a gateway keeps its copy of the network's API keys between restarts (api-keys.json beside host.json on
/// Linux hosts). <see cref="Load"/> returns null when there is none; either call may throw on storage failure.</summary>
public interface IGatewayApiKeyStorage
{
    byte[]? Load();
    void Save(byte[] bytes);
}

/// <summary>Who vouches for a principal while its work runs: the paired-device credential store, or the API key list.</summary>
internal interface IGatewayPrincipalAuthority
{
    T WithAuthority<T>(GatewayPrincipal principal, Func<T> operation);
}

/// <summary>Which callers an endpoint admits besides paired devices (which always may): none, any API key, or a key with
/// one of the listed scopes. <see cref="RoleScope"/> asks for the scope named by the request's role.</summary>
internal sealed record GatewayApiAccess(string[]? Scopes, bool RoleScope = false)
{
    internal static readonly GatewayApiAccess AnyKey = new([]);
    internal static readonly GatewayApiAccess Read = new([ApiKeyScopes.Read]);
    internal static readonly GatewayApiAccess Manage = new([ApiKeyScopes.Manage]);
    internal static readonly GatewayApiAccess ReadOrManage = new([ApiKeyScopes.Read, ApiKeyScopes.Manage]);
    internal static readonly GatewayApiAccess Role = new([], RoleScope: true);
}

/// <summary>This host's copy of the API keys of the owner's Martlet network (docs/API.md). Paired desktops read it and
/// merge their changes into it, so a key created or revoked on any of them reaches every host. Software outside the
/// network presents a key as <c>Authorization: Bearer martlet_...</c>; the key's scopes decide what it may call. A copy that
/// cannot be saved is still served from memory, and desktops push it again.</summary>
internal sealed class GatewayApiKeyStore(string hostId, TimeProvider clock) : IGatewayPrincipalAuthority
{
    internal const string Scheme = "Bearer";
    private readonly object gate = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> used = new(StringComparer.Ordinal);
    private ApiKeyList list = ApiKeyList.Empty;
    private string digest = ApiKeyList.Empty.Digest();
    private IGatewayApiKeyStorage? storage;

    internal ApiKeyList Current { get { lock (gate) return list; } }

    /// <summary>When each key was last accepted here since the gateway started (key ID → time).</summary>
    internal IReadOnlyDictionary<string, DateTimeOffset> Used => used.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

    internal void Attach(IGatewayApiKeyStorage value)
    {
        ArgumentNullException.ThrowIfNull(value);
        ApiKeyList? saved = null;
        try { if (value.Load() is { } bytes) saved = ApiKeyList.Parse(bytes); }
        // An unreadable or malformed copy is replaced by the next desktop that syncs.
        catch (Exception) { }
        lock (gate)
        {
            storage = value;
            if (saved is not null) Replace(ApiKeyList.Merge(list, saved), save: false);
        }
    }

    internal ApiKeyList Merge(ApiKeyList incoming)
    {
        lock (gate)
        {
            Replace(ApiKeyList.Merge(list, incoming), save: true);
            foreach (var id in used.Keys.Where(id => list.Find(id) is not { Revoked: false })) used.TryRemove(id, out _);
            return list;
        }
    }

    private void Replace(ApiKeyList next, bool save)
    {
        var nextDigest = next.Digest();
        if (nextDigest == digest) return;
        list = next;
        digest = nextDigest;
        if (!save || storage is null) return;
        try { storage.Save(next.Write()); }
        catch (Exception) { }
    }

    internal static bool IsBearer(HttpRequest request) =>
        request.Headers.TryGetValue("Authorization", out StringValues values) && values.Count >= 1 &&
        values[0] is { } value && value.StartsWith(Scheme + " ", StringComparison.OrdinalIgnoreCase);

    /// <summary>Checks the presented key and that <paramref name="access"/> admits it, and returns its principal: role
    /// <paramref name="role"/> (an inference route's), else the X-Martlet-Role header, else voice.</summary>
    internal GatewayPrincipal Authenticate(HttpRequest request, GatewayApiAccess access, GatewayRole? role)
    {
        if (access.Scopes is null) throw new GatewayProtocolException("key.scope");
        GatewayRules.Require(request.Headers.TryGetValue("Authorization", out StringValues values) && values.Count == 1 &&
            values[0] is { Length: <= 128 } header && header.All(char.IsAscii), "key.invalid");
        var token = values[0]![(Scheme.Length + 1)..].Trim();
        var effective = role ?? (request.Headers.TryGetValue(GatewayRequestSigner.RoleHeader, out var roleHeader)
            ? roleHeader.Count == 1 ? GatewayRequestSigner.ParseRole(roleHeader[0] ?? "") : throw new GatewayProtocolException("request.invalid")
            : GatewayRole.Voice);
        var now = clock.GetUtcNow();
        ApiKey key;
        lock (gate)
        {
            var (match, refusal) = list.Match(token, now);
            key = refusal switch
            {
                ApiKeyRefusal.None => match!,
                ApiKeyRefusal.Revoked => throw new GatewayProtocolException("key.revoked"),
                ApiKeyRefusal.Expired => throw new GatewayProtocolException("key.expired"),
                _ => throw new GatewayProtocolException("key.invalid")
            };
        }
        var needed = access.RoleScope ? [GatewayRequestSigner.RoleText(effective)] : access.Scopes;
        GatewayRules.Require(needed.Length == 0 || needed.Any(key.Allows), "key.scope");
        used[key.Id] = now;
        return new()
        {
            HostId = hostId,
            CredentialId = key.Id,
            DeviceId = "api-key-" + key.Id,
            Role = effective,
            CredentialLifetime = new PairedDeviceLifetime(),
            Authority = this,
            Key = key
        };
    }

    /// <summary>Runs <paramref name="operation"/> while the principal's key is still live and still allows its role, so
    /// revoking a key (or its expiry) stops work it started.</summary>
    public T WithAuthority<T>(GatewayPrincipal principal, Func<T> operation)
    {
        lock (gate)
        {
            GatewayRules.Require(ReferenceEquals(principal.Authority, this) && principal.HostId == hostId, "key.invalid");
            var key = list.Find(principal.CredentialId) ?? throw new GatewayProtocolException("key.invalid");
            GatewayRules.Require(!key.Revoked, "key.revoked");
            GatewayRules.Require(!key.Expired(clock.GetUtcNow()), "key.expired");
            GatewayRules.Require(principal.Key?.Verifier == key.Verifier, "key.invalid");
            GatewayRules.Require(key.Allows(GatewayRequestSigner.RoleText(principal.Role)), "key.scope");
            return operation();
        }
    }
}

internal sealed partial class GatewayHttpApplication
{
    internal const string ApiKeysPath = "/martlet/v1/api-keys";
    private const int MaximumApiKeysResponseBytes = ApiKeyList.MaximumBytes + 8_192;

    internal GatewayApiKeyStore ApiKeys { get; }

    /// <summary>A paired device (signed request) or, when <paramref name="access"/> admits it, an API key.</summary>
    private GatewayPrincipal Authorize(HttpRequest request, GatewayApiAccess access, GatewayRole? role = null) =>
        GatewayApiKeyStore.IsBearer(request) ? ApiKeys.Authenticate(request, access, role) : authenticator.Authenticate(request);

    private GatewayPrincipal Authorize(HttpRequest request, ReadOnlySpan<byte> bodyHash, GatewayApiAccess access, GatewayRole? role = null) =>
        GatewayApiKeyStore.IsBearer(request) ? ApiKeys.Authenticate(request, access, role) : authenticator.Authenticate(request, bodyHash);

    /// <summary>GET returns this host's copy of the network's API keys (verifiers, never secrets) and when each was last
    /// used here; POST merges a desktop's copy into it and returns the merged result. Paired devices only: an API key can
    /// never read or change keys.</summary>
    private async ValueTask InvokeApiKeysAsync(HttpContext context)
    {
        ApiKeyList result;
        if (context.Request.Method == HttpMethods.Get)
        {
            EnsureEmptyRequest(context.Request);
            _ = authenticator.Authenticate(context.Request);
            result = ApiKeys.Current;
        }
        else if (context.Request.Method == HttpMethods.Post)
        {
            var bytes = await ReadInferenceBodyAsync(context.Request, ApiKeyList.MaximumBytes, context.RequestAborted)
                .ConfigureAwait(false);
            var principal = authenticator.Authenticate(context.Request, crypto.Sha256(bytes));
            ApiKeyList incoming;
            try { incoming = ApiKeyList.Parse(bytes); }
            catch (ContractException) { throw new GatewayProtocolException("request.invalid"); }
            var before = ApiKeys.Current;
            result = ApiKeys.Merge(incoming);
            foreach (var key in result.Keys)
            {
                var old = before.Find(key.Id);
                if (old is null && !key.Revoked)
                    Logs.Own(Martlet.Core.Logs.LogLevels.Info, $"API key \"{key.Name}\" ({string.Join(", ", key.Scopes)}) added by {key.CreatedBy}, through {principal.DeviceId}.");
                else if (key.Revoked && old is { Revoked: false })
                    Logs.Own(Martlet.Core.Logs.LogLevels.Info, $"API key \"{key.Name}\" revoked by {key.UpdatedBy}, through {principal.DeviceId}.");
            }
        }
        else throw new GatewayProtocolException("request.invalid");
        await WriteJsonAsync(context, StatusCodes.Status200OK, new ApiKeysDocument
        {
            ProtocolVersion = GatewayProtocolVersion.Current,
            HostId = identity.HostId,
            Keys = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(result.Write()),
            Used = ApiKeys.Used
        }, MaximumApiKeysResponseBytes).ConfigureAwait(false);
    }

    private sealed record ApiKeysDocument
    {
        public required GatewayProtocolVersion ProtocolVersion { get; init; }
        public required string HostId { get; init; }
        public required System.Text.Json.JsonElement Keys { get; init; }
        public required IReadOnlyDictionary<string, DateTimeOffset> Used { get; init; }
    }
}
