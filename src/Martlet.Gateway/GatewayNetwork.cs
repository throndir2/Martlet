using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Core.Contracts;
using Martlet.Core.Logs;
using Martlet.Core.Network;
using Microsoft.AspNetCore.Http;

namespace Martlet.Gateway;

/// <summary>Where a gateway keeps the Martlet network roster it accepted (network.json beside host.json on Linux hosts).
/// <see cref="Load"/> returns null when the host was never bound; <see cref="Save"/> may throw on storage failure.</summary>
public interface IGatewayNetworkStorage
{
    byte[]? Load();
    void Save(byte[] bytes);
}

/// <summary>A desktop that paired with this host (with the owner's approval) and asks to join the host's network. A member
/// desktop approves it by signing it into the roster.</summary>
internal sealed record GatewayJoinRequest(string DeviceId, string DisplayName, string Key, string CheckNumber, DateTimeOffset RequestedAt);

/// <summary>
/// This host's place in the owner's Martlet network (<see cref="NetworkRoster"/>). The first paired desktop that brings a
/// roster listing this host binds it to that network; from then on the host merges only rosters of the same network,
/// accepting entries signed by its current members, and lets any active member desktop pair by itself
/// (<see cref="NetworkPairing"/>). When a desktop is removed the host revokes its credentials; when this host is removed it
/// revokes every network desktop and keeps the roster (so desktops learn of the removal) until a member adds it again.
/// </summary>
internal sealed class GatewayNetworkStore(GatewayHostIdentity identity, GatewayCredentialStore credentials, TimeProvider clock,
    Action<string, string> log)
{
    internal const int MaximumJoins = 8;
    internal static readonly TimeSpan JoinLifetime = TimeSpan.FromHours(1);
    private const int MaximumNonces = 512;
    private const int MaximumPairingsPerMinute = 30;

    private readonly object gate = new();
    private readonly Dictionary<string, GatewayJoinRequest> joins = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> nonces = new(StringComparer.Ordinal);
    private readonly Queue<DateTimeOffset> attempts = new();
    private NetworkRoster? roster;
    private IGatewayNetworkStorage? storage;

    internal NetworkRoster? Roster { get { lock (gate) return roster; } }

    /// <summary>Called (inside the store's lock) whenever the accepted roster is loaded or changes.</summary>
    internal Action<NetworkRoster?>? Changed { get; set; }

    /// <summary>The outside addresses this host's own roster entry lists (none when unbound or removed).</summary>
    internal IReadOnlyList<string> OwnAddresses { get { lock (gate) return roster?.Host(identity.HostId) is { Removed: false } self ? self.Addresses ?? [] : []; } }

    private bool BoundLocked => roster?.Host(identity.HostId) is { Removed: false };

    internal string State { get { lock (gate) return StateLocked; } }

    private string StateLocked => roster is null ? "unbound" : BoundLocked ? "bound" : "removed";

    internal void Attach(IGatewayNetworkStorage value)
    {
        ArgumentNullException.ThrowIfNull(value);
        NetworkRoster? saved = null;
        try { if (value.Load() is { } bytes) saved = NetworkRoster.Parse(bytes); }
        catch (Exception error) when (error is ContractException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            log(LogLevels.Warn, "network.json is unreadable or invalid, so this host is in no Martlet network until a paired desktop binds it again.");
        }
        catch (Exception)
        {
            log(LogLevels.Warn, "network.json could not be read, so this host is in no Martlet network until a paired desktop binds it again.");
        }
        lock (gate)
        {
            storage = value;
            roster ??= saved;
            Changed?.Invoke(roster);
        }
    }

    /// <summary>The roster and, for a member desktop, the pending join requests.</summary>
    internal (NetworkRoster? Roster, string State, IReadOnlyList<GatewayJoinRequest> Joins) Snapshot(string deviceId)
    {
        lock (gate)
        {
            SweepJoinsLocked(clock.GetUtcNow());
            var member = roster?.Desktop(deviceId) is { Removed: false };
            return (roster, StateLocked, member && BoundLocked ? joins.Values.OrderBy(j => j.RequestedAt).ToArray() : []);
        }
    }

    /// <summary>The computers paired with this host (any of the owner's paired devices may see them), most recently seen
    /// first, at most <see cref="MaximumDevices"/>.</summary>
    internal IReadOnlyList<GatewayPairedDevice> Devices() => credentials.PairedDevices()
        .OrderByDescending(d => d.LastSeen ?? DateTimeOffset.MinValue).ThenByDescending(d => d.PairedAt).Take(MaximumDevices).ToArray();

    internal const int MaximumDevices = 16;

    /// <summary>Merges a paired desktop's copy of the roster. An unbound host (or one removed from its last network) is
    /// bound only by a roster in which the caller is an active member desktop and this host is listed with its own key.</summary>
    internal NetworkRoster Merge(NetworkRoster incoming, string callerDeviceId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        lock (gate)
        {
            NetworkRoster next;
            if (roster is null || !BoundLocked && incoming.NetworkId != roster.NetworkId)
            {
                NetworkRoster boot;
                try { boot = NetworkRoster.Bootstrap(incoming).Roster; }
                catch (ContractException) { throw new GatewayProtocolException("network.invalid"); }
                GatewayRules.Require(boot.ActiveDesktops.Any(d => d.Id == callerDeviceId), "network.denied");
                GatewayRules.Require(boot.Host(identity.HostId) is { Removed: false } self && self.Spki == identity.SpkiFingerprint,
                    "network.invalid");
                next = boot;
            }
            else
            {
                GatewayRules.Require(incoming.NetworkId == roster.NetworkId, "network.other");
                try { next = NetworkRoster.Accept(roster, incoming).Roster; }
                catch (ContractException) { throw new GatewayProtocolException("network.invalid"); }
            }
            ApplyLocked(next, callerDeviceId, cancellationToken);
            return roster!;
        }
    }

    private void ApplyLocked(NetworkRoster next, string by, CancellationToken cancellationToken)
    {
        var before = roster?.NetworkId == next.NetworkId ? roster : null;
        var wasBound = BoundLocked && before is not null;
        var isBound = next.Host(identity.HostId) is { Removed: false };
        var revoke = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var desktop in next.Members.Where(m => m.IsDesktop))
        {
            var prior = before?.Desktop(desktop.Id);
            if (desktop.Removed && !(prior is { Removed: true } && prior.Key == desktop.Key) ||
                !desktop.Removed && prior is { Removed: false } && prior.Key != desktop.Key)
                revoke.Add(desktop.Id);
        }
        if (wasBound && !isBound)
            foreach (var desktop in next.Members.Where(m => m.IsDesktop)) revoke.Add(desktop.Id);
        var revoked = 0;
        foreach (var id in revoke)
        {
            revoked += credentials.RevokeDevice(id, cancellationToken);
            joins.Remove(id);
        }
        foreach (var join in joins.Values.Where(j => next.Trusts(j.DeviceId, j.Key)).ToArray()) joins.Remove(join.DeviceId);
        var changed = roster is null || roster.Digest() != next.Digest();
        roster = next;
        if (changed) Changed?.Invoke(next);
        if (changed && storage is not null)
        {
            try { storage.Save(next.Write()); }
            catch (Exception) { log(LogLevels.Warn, "Could not save network.json; this host keeps the network in memory until it restarts."); }
        }
        if (!wasBound && isBound)
            log(LogLevels.Info, $"Joined Martlet network {next.NetworkId} (bound by {by}); its member desktops can pair with this host by themselves.");
        else if (wasBound && !isBound)
            log(LogLevels.Warn, $"Removed from Martlet network {next.NetworkId} (by {next.Host(identity.HostId)?.UpdatedBy ?? by}); revoked {revoked} credential(s) of its desktops.");
        else if (revoked > 0)
            log(LogLevels.Info, $"Revoked {revoked} credential(s) of desktops removed from the network: {string.Join(", ", revoke)}.");
    }

    /// <summary>Records a paired desktop's request to join this host's network (one per device, newest wins).</summary>
    internal (string State, string NetworkId, string CheckNumber) Join(string deviceId, string displayName, string key)
    {
        lock (gate)
        {
            GatewayRules.Require(roster is not null && BoundLocked, "network.unbound");
            GatewayRules.Require(NetworkKey.IsPublicKey(key), "request.invalid");
            var network = roster!.NetworkId;
            var check = NetworkKey.CheckNumber(network, deviceId, key);
            if (roster.Trusts(deviceId, key)) return ("member", network, check);
            GatewayRules.Require(!(roster.Desktop(deviceId) is { Removed: true } removed && removed.Key == key), "network.denied");
            var now = clock.GetUtcNow();
            SweepJoinsLocked(now);
            if (!joins.ContainsKey(deviceId) && joins.Count >= MaximumJoins)
                joins.Remove(joins.Values.OrderBy(j => j.RequestedAt).First().DeviceId);
            var name = NetworkRoster.CleanName(displayName, deviceId);
            if (!joins.TryGetValue(deviceId, out var existing) || existing.Key != key)
                log(LogLevels.Info, $"{deviceId} ({name}) asks to join Martlet network {network} (check number {check}).");
            joins[deviceId] = new(deviceId, name, key, check, existing?.Key == key ? existing.RequestedAt : now);
            return ("pending", network, check);
        }
    }

    /// <summary>A member desktop turns down a join request.</summary>
    internal bool Deny(string callerDeviceId, string deviceId)
    {
        lock (gate)
        {
            GatewayRules.Require(roster is not null && BoundLocked && roster.Desktop(callerDeviceId) is { Removed: false }, "network.denied");
            var removed = joins.Remove(deviceId);
            if (removed) log(LogLevels.Info, $"{callerDeviceId} turned down {deviceId}'s request to join the network.");
            return removed;
        }
    }

    /// <summary>Issues a credential to an active member desktop that proves it holds its network key (replacing any older
    /// credential of that device here).</summary>
    internal IssuedDeviceCredential PairMember(GatewayMemberPairingProof proof, CancellationToken cancellationToken)
    {
        proof.Validate();
        lock (gate)
        {
            var now = clock.GetUtcNow();
            while (attempts.Count > 0 && attempts.Peek() <= now - TimeSpan.FromMinutes(1)) attempts.Dequeue();
            GatewayRules.Require(attempts.Count < MaximumPairingsPerMinute, "auth.rate");
            attempts.Enqueue(now);
            GatewayRules.Require(roster is not null && BoundLocked, "network.unbound");
            GatewayRules.Require(proof.NetworkId == roster!.NetworkId, "network.other");
            GatewayRules.Require(roster.Desktop(proof.DeviceId) is { Removed: false }, "network.denied");
            var member = roster.Desktop(proof.DeviceId)!;
            var bytes = NetworkPairing.ProofBytes(identity.HostId, identity.SpkiFingerprint, roster.NetworkId, proof.DeviceId,
                proof.DisplayName, proof.Timestamp, proof.Nonce);
            GatewayRules.Require(NetworkKey.Verify(member.Key!, bytes, proof.Signature), "pairing.invalid");
            var at = DateTimeOffset.FromUnixTimeMilliseconds(proof.Timestamp);
            GatewayRules.Require(at >= now - NetworkPairing.ClockSkew && at <= now + NetworkPairing.ClockSkew, "auth.clock");
            foreach (var stale in nonces.Where(n => n.Value <= now).Select(n => n.Key).ToArray()) nonces.Remove(stale);
            GatewayRules.Require(!nonces.ContainsKey(proof.Nonce), "auth.replay");
            GatewayRules.Require(nonces.Count < MaximumNonces, "auth.rate");
            nonces[proof.Nonce] = now + NetworkPairing.ClockSkew + NetworkPairing.ClockSkew;
            credentials.RevokeDevice(proof.DeviceId, cancellationToken);
            var issued = credentials.Issue(proof.DeviceId, NetworkRoster.CleanName(proof.DisplayName, proof.DeviceId),
                [GatewayRole.Voice], cancellationToken);
            joins.Remove(proof.DeviceId);
            return issued;
        }
    }

    private void SweepJoinsLocked(DateTimeOffset now)
    {
        foreach (var stale in joins.Values.Where(j => j.RequestedAt + JoinLifetime <= now).ToArray()) joins.Remove(stale.DeviceId);
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record GatewayMemberPairingProof
{
    public required GatewayProtocolVersion ProtocolVersion { get; init; }
    public required string NetworkId { get; init; }
    public required string DeviceId { get; init; }
    public required string DisplayName { get; init; }
    public required long Timestamp { get; init; }
    public required string Nonce { get; init; }
    public required string Signature { get; init; }

    internal void Validate()
    {
        GatewayRules.Require(ProtocolVersion is not null, "request.invalid");
        ProtocolVersion!.Validate();
        GatewayRules.Require(Base64Url.TryDecode(NetworkId, 16, out _), "request.invalid");
        GatewayRules.Identifier(DeviceId);
        GatewayRules.Token(DisplayName, 64);
        GatewayRules.Require(Timestamp > 0, "request.invalid");
        GatewayRules.Require(Base64Url.TryDecode(Nonce, 16, out _), "request.invalid");
        GatewayRules.Require(Base64Url.TryDecode(Signature, 64, out _), "request.invalid");
    }
}

internal sealed partial class GatewayHttpApplication
{
    internal const string NetworkPath = "/martlet/v1/network";
    internal const string NetworkJoinPath = "/martlet/v1/network/join";
    internal const string NetworkDenyPath = "/martlet/v1/network/deny";
    // Desktops read at most the roster plus 16 KiB; joins (8) and devices (16) stay well inside that.
    private const int MaximumNetworkResponseBytes = NetworkRoster.MaximumBytes + 16_384;
    private const int MaximumNetworkRequestBytes = 4_096;

    internal GatewayNetworkStore Network { get; }

    private static bool IsNetworkTarget(string rawTarget) => rawTarget is NetworkPath or NetworkJoinPath or NetworkDenyPath;

    /// <summary>
    /// GET network: this host's roster, the computers paired with it (with when each was last seen) and, for a member
    /// desktop, pending join requests. POST network: merge a desktop's copy (binding an unbound host). POST network/join: a
    /// paired desktop asks to join. POST network/deny: a member turns a request down. All over the caller's signed, pinned
    /// connection; none of it is secret.
    /// </summary>
    private async ValueTask InvokeNetworkAsync(HttpContext context, string rawTarget)
    {
        if (rawTarget == NetworkPath && context.Request.Method == HttpMethods.Get)
        {
            EnsureEmptyRequest(context.Request);
            var principal = authenticator.Authenticate(context.Request);
            await WriteNetworkAsync(context, principal.DeviceId).ConfigureAwait(false);
            return;
        }
        GatewayRules.Require(context.Request.Method == HttpMethods.Post, "request.invalid");
        if (rawTarget == NetworkPath)
        {
            var bytes = await ReadInferenceBodyAsync(context.Request, NetworkRoster.MaximumBytes, context.RequestAborted).ConfigureAwait(false);
            var principal = authenticator.Authenticate(context.Request, crypto.Sha256(bytes));
            NetworkRoster incoming;
            try { incoming = NetworkRoster.Parse(bytes); }
            catch (ContractException) { throw new GatewayProtocolException("network.invalid"); }
            _ = Network.Merge(incoming, principal.DeviceId, context.RequestAborted);
            await WriteNetworkAsync(context, principal.DeviceId).ConfigureAwait(false);
            return;
        }
        var body = await ReadInferenceBodyAsync(context.Request, MaximumNetworkRequestBytes, context.RequestAborted).ConfigureAwait(false);
        var caller = authenticator.Authenticate(context.Request, crypto.Sha256(body));
        if (rawTarget == NetworkJoinPath)
        {
            var join = ParseNetworkBody<JoinBody>(body);
            GatewayRules.Require(join.ProtocolVersion is not null, "request.invalid");
            join.ProtocolVersion!.Validate();
            GatewayRules.Token(join.DisplayName, 64);
            var (state, networkId, check) = Network.Join(caller.DeviceId, join.DisplayName, join.Key);
            SignIn.RememberJoinKey(caller.DeviceId, join.Key);
            await WriteJsonAsync(context, StatusCodes.Status200OK, new JoinDocument
            {
                ProtocolVersion = GatewayProtocolVersion.Current, HostId = identity.HostId, State = state, NetworkId = networkId,
                CheckNumber = check
            }).ConfigureAwait(false);
            return;
        }
        var deny = ParseNetworkBody<DenyBody>(body);
        GatewayRules.Require(deny.ProtocolVersion is not null, "request.invalid");
        deny.ProtocolVersion!.Validate();
        GatewayRules.Identifier(deny.DeviceId);
        var denied = Network.Deny(caller.DeviceId, deny.DeviceId);
        await WriteJsonAsync(context, StatusCodes.Status200OK, new DenyDocument
        {
            ProtocolVersion = GatewayProtocolVersion.Current, HostId = identity.HostId, Denied = denied
        }).ConfigureAwait(false);
    }

    private ValueTask WriteNetworkAsync(HttpContext context, string deviceId)
    {
        var (roster, state, joins) = Network.Snapshot(deviceId);
        var member = state == "bound" && roster?.Desktop(deviceId) is { Removed: false };
        return WriteJsonAsync(context, StatusCodes.Status200OK, new NetworkDocument
        {
            ProtocolVersion = GatewayProtocolVersion.Current,
            HostId = identity.HostId,
            MartletVersion = MartletVersion,
            AdvertisedAddresses = Guard.Exposure.OutsideAddressesSetAt is null ? null : Guard.Exposure.OutsideAddresses ?? [],
            AdvertisedAt = Guard.Exposure.OutsideAddressesSetAt,
            State = state,
            Roster = roster is null ? null : JsonSerializer.Deserialize<JsonElement>(roster.Write()),
            Joins = joins.Select(j => new JoinRequestDocument
            {
                DeviceId = j.DeviceId, DisplayName = j.DisplayName, Key = j.Key, CheckNumber = j.CheckNumber, RequestedAt = j.RequestedAt,
                SignIn = SignIn.Attestation(j.DeviceId) is { } signedIn
                    ? new() { Provider = signedIn.Identity.Provider, Subject = signedIn.Identity.Subject, Label = signedIn.Identity.Label, At = signedIn.At }
                    : null
            }).ToArray(),
            Devices = Network.Devices().Select(d => new PairedDeviceDocument
            {
                DeviceId = d.DeviceId, DisplayName = d.DisplayName, PairedAt = d.PairedAt, LastSeen = d.LastSeen,
                Access = d.Access == GatewayAccess.Friend ? GatewaySignInDocument.FriendAccess : null
            }).ToArray(),
            SignInRemovals = member ? SignIn.Removals(roster).Select(r => new SignInRemovalDocument
            {
                DeviceId = r.DeviceId, Key = r.Key, Provider = r.Provider, Subject = r.Subject, Label = r.Label, At = r.At
            }).ToArray() : null
        }, MaximumNetworkResponseBytes);
    }

    private static T ParseNetworkBody<T>(byte[] bytes) where T : class
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
            InspectJson(document.RootElement);
            return document.Deserialize<T>(Json) ?? throw new GatewayProtocolException("request.invalid");
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            throw new GatewayProtocolException("request.invalid");
        }
    }

    private void LogMemberPaired(IssuedDeviceCredential credential) =>
        Logs.Own(LogLevels.Info, $"Paired network member {credential.DeviceId} by its network key (roles: {string.Join(", ", credential.Roles).ToLowerInvariant()}).");

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record JoinBody
    {
        public required GatewayProtocolVersion ProtocolVersion { get; init; }
        public required string DisplayName { get; init; }
        public required string Key { get; init; }
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record DenyBody
    {
        public required GatewayProtocolVersion ProtocolVersion { get; init; }
        public required string DeviceId { get; init; }
    }

    private sealed record NetworkDocument
    {
        public required GatewayProtocolVersion ProtocolVersion { get; init; }
        public required string HostId { get; init; }
        /// <summary>The Martlet release this host runs, announced to every computer that syncs the network with it, so an
        /// update made anywhere reaches them all on their next sync. Desktops older than this field ignore it.</summary>
        public string? MartletVersion { get; init; }
        /// <summary>Outside addresses the owner set on this host itself, and when; member desktops sign them into the
        /// host's roster entry when they are newer than it. Absent when never set there.</summary>
        public IReadOnlyList<string>? AdvertisedAddresses { get; init; }
        public DateTimeOffset? AdvertisedAt { get; init; }
        public required string State { get; init; }
        public JsonElement? Roster { get; init; }
        public required JoinRequestDocument[] Joins { get; init; }
        /// <summary>The computers paired with this host. Desktops older than this list ignore it.</summary>
        public required PairedDeviceDocument[] Devices { get; init; }
        /// <summary>For a member desktop: computers that joined through a sign-in the owner no longer allows on this host. The
        /// member removes them from the roster (signed by it), so every host revokes them. Older desktops ignore it.</summary>
        public SignInRemovalDocument[]? SignInRemovals { get; init; }
    }

    private sealed record SignInRemovalDocument
    {
        public required string DeviceId { get; init; }
        public string? Key { get; init; }
        public required string Provider { get; init; }
        public required string Subject { get; init; }
        public string? Label { get; init; }
        public required DateTimeOffset At { get; init; }
    }

    private sealed record PairedDeviceDocument
    {
        public required string DeviceId { get; init; }
        public required string DisplayName { get; init; }
        public required DateTimeOffset PairedAt { get; init; }
        public DateTimeOffset? LastSeen { get; init; }
        /// <summary>"friend" for a friend's computer (this host's engines only, never in the network); absent for the owner's.</summary>
        public string? Access { get; init; }
    }

    private sealed record JoinRequestDocument
    {
        public required string DeviceId { get; init; }
        public required string DisplayName { get; init; }
        public required string Key { get; init; }
        public required string CheckNumber { get; init; }
        public required DateTimeOffset RequestedAt { get; init; }
        /// <summary>Who this desktop signed in as to pair here (<see cref="GatewaySignInService.Attestation"/>); null when it
        /// paired another way. A member desktop lets such a request in without a check number.</summary>
        public SignInAttestationDocument? SignIn { get; init; }
    }

    private sealed record SignInAttestationDocument
    {
        public required string Provider { get; init; }
        public required string Subject { get; init; }
        public string? Label { get; init; }
        public required DateTimeOffset At { get; init; }
    }

    private sealed record JoinDocument
    {
        public required GatewayProtocolVersion ProtocolVersion { get; init; }
        public required string HostId { get; init; }
        public required string State { get; init; }
        public required string NetworkId { get; init; }
        public required string CheckNumber { get; init; }
    }

    private sealed record DenyDocument
    {
        public required GatewayProtocolVersion ProtocolVersion { get; init; }
        public required string HostId { get; init; }
        public required bool Denied { get; init; }
    }
}
