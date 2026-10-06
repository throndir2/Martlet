using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Core.Contracts;

namespace Martlet.Core.Network;

public static class NetworkKinds
{
    public const string Desktop = "desktop";
    public const string Host = "host";
}

/// <summary>One computer in the owner's Martlet network: a desktop (with its network public key) or a host (with the
/// address and TLS key fingerprint desktops pin when they pair with it). Each entry is signed by the desktop that wrote it
/// (<see cref="UpdatedBy"/>). A removed entry keeps its key, and a removed desktop key never counts again.</summary>
public sealed record NetworkMember
{
    public required string Kind { get; init; }
    public required string Id { get; init; }
    public required string Name { get; init; }
    /// <summary>Desktops: base64url SubjectPublicKeyInfo of the desktop's ECDSA P-256 network key.</summary>
    public string? Key { get; init; }
    /// <summary>Hosts: the gateway's https://&lt;private IP&gt;:&lt;port&gt; origin.</summary>
    public string? Origin { get; init; }
    /// <summary>Hosts: the gateway's TLS key fingerprint (sha256:&lt;hex&gt;).</summary>
    public string? Spki { get; init; }
    /// <summary>Hosts: owner-set addresses that reach the same gateway from outside home (an overlay network such as
    /// Tailscale, or a router port forward): "name:port", "IPv4:port" or "[IPv6]:port", at most
    /// <see cref="NetworkRoster.MaximumAddresses"/>. Desktops try <see cref="Origin"/> first and these when it doesn't answer,
    /// always pinned to <see cref="Spki"/>. Null when there are none (entries without them sign exactly as before).</summary>
    public IReadOnlyList<string>? Addresses { get; init; }
    public bool Removed { get; init; }
    public required long Revision { get; init; }
    public required string UpdatedBy { get; init; }
    public required string Signature { get; init; }

    [JsonIgnore] public bool IsDesktop => Kind == NetworkKinds.Desktop;
    [JsonIgnore] public bool IsHost => Kind == NetworkKinds.Host;
    [JsonIgnore] internal string Slot => Kind + ":" + Id;

    /// <summary>About when this entry was written (revisions are hybrid millisecond clocks).</summary>
    [JsonIgnore] public DateTimeOffset ChangedAt => DateTimeOffset.FromUnixTimeMilliseconds(Math.Clamp(Revision, 0, 253_402_300_799_000));

    internal byte[] SigningBytes(string networkId) => Encoding.UTF8.GetBytes(Addresses is { Count: > 0 } addresses
        ? $"martlet-network-member-v2\n{networkId}\n{Kind}\n{Id}\n{Name}\n{Key}\n{Origin}\n{Spki}\n{(Removed ? 1 : 0)}\n{Revision}\n{UpdatedBy}\n{string.Join(',', addresses)}"
        : $"martlet-network-member-v1\n{networkId}\n{Kind}\n{Id}\n{Name}\n{Key}\n{Origin}\n{Spki}\n{(Removed ? 1 : 0)}\n{Revision}\n{UpdatedBy}");

    internal string Content => $"{Name}|{Key}|{Origin}|{Spki}|{string.Join(',', Addresses ?? [])}|{Removed}|{Signature}";
}

/// <summary>The result of joining a roster into another: the joined roster and how many incoming entries were refused
/// (not signed by a current member of the network).</summary>
public sealed record NetworkMerge(NetworkRoster Roster, int Rejected);

/// <summary>
/// The owner's Martlet network: which desktops belong to it (by network key) and which hosts trust them. Pairing one
/// desktop with a host binds that host to the desktop's network; every member desktop then pairs with every member host
/// by itself (<see cref="NetworkPairing"/>), and a new desktop joins once a member approves it.
/// <list type="bullet">
/// <item>The network ID is derived from the founding desktop's key (<see cref="NetworkKey.NetworkIdFor"/>), so anyone can
/// check which entry founded it.</item>
/// <item>Every entry is signed by the member desktop that wrote it. Receivers (hosts and desktops) accept an incoming
/// entry only when its signer is an active desktop in the roster they already accepted (<see cref="Accept"/>); entries
/// they accepted earlier stay accepted. A removed desktop's later entries are refused wherever its removal is known, and
/// hosts revoke its credentials.</item>
/// <item>Per computer, the newest entry wins (revision, writer, content), except that a removed desktop key never becomes
/// active again: rejoining needs a new key.</item>
/// </list>
/// Nonsecret: IDs, names, public keys, host addresses and fingerprints. JSON, snake case, schema 1.
/// </summary>
public sealed record NetworkRoster
{
    public const int MaximumBytes = 40_960;
    public const int MaximumMembers = 64;
    public const int MaximumNameLength = 64;
    public const int MaximumAddresses = 4;
    public const int MaximumAddressLength = 128;
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
    public required string NetworkId { get; init; }
    public required IReadOnlyList<NetworkMember> Members { get; init; }

    [JsonIgnore] public long Revision => Members.Select(m => m.Revision).DefaultIfEmpty(0).Max();

    /// <summary>The desktop whose key the network ID was derived from (it may have been removed since).</summary>
    [JsonIgnore]
    public NetworkMember? Founder => Members.FirstOrDefault(m => m.IsDesktop && m.Key is not null && NetworkKey.NetworkIdFor(m.Key) == NetworkId);

    [JsonIgnore] public IEnumerable<NetworkMember> ActiveDesktops => Members.Where(m => m.IsDesktop && !m.Removed);
    [JsonIgnore] public IEnumerable<NetworkMember> ActiveHosts => Members.Where(m => m.IsHost && !m.Removed);

    public NetworkMember? Desktop(string id) => Members.FirstOrDefault(m => m.IsDesktop && m.Id == id);
    public NetworkMember? Host(string id) => Members.FirstOrDefault(m => m.IsHost && m.Id == id);

    /// <summary>Whether <paramref name="deviceId"/> with <paramref name="publicKey"/> is an active desktop member.</summary>
    public bool Trusts(string deviceId, string publicKey) => Desktop(deviceId) is { Removed: false } member && member.Key == publicKey;

    /// <summary>A revision newer than everything in this roster and, normally, than anything written before now.</summary>
    public long NextRevision(DateTimeOffset now) => Math.Max(Revision + 1, Math.Max(1, now.ToUnixTimeMilliseconds()));

    /// <summary>Starts a new network whose only member is <paramref name="founder"/>.</summary>
    public static NetworkRoster Found(INetworkSigner founder, string name, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(founder);
        var id = NetworkKey.NetworkIdFor(founder.PublicKey);
        var member = Sign(id, founder, new()
        {
            Kind = NetworkKinds.Desktop, Id = founder.DeviceId, Name = CleanName(name, founder.DeviceId), Key = founder.PublicKey,
            Revision = Math.Max(1, now.ToUnixTimeMilliseconds()), UpdatedBy = founder.DeviceId, Signature = ""
        });
        var roster = new NetworkRoster { SchemaVersion = 1, NetworkId = id, Members = [member] };
        roster.Validate();
        return roster;
    }

    /// <summary>Adds (or re-adds with a new key) a desktop, signed by member <paramref name="by"/>.</summary>
    public NetworkRoster AddDesktop(INetworkSigner by, string id, string name, string publicKey, DateTimeOffset now)
    {
        if (Desktop(id) is { Removed: true } removed && removed.Key == publicKey)
            throw new InvalidOperationException($"{id}'s key was removed from this network, so it can't come back with it. Martlet on that computer makes a new key when it asks to join again.");
        return Put(by, new()
        {
            Kind = NetworkKinds.Desktop, Id = id, Name = CleanName(name, id), Key = publicKey,
            Revision = NextRevision(now), UpdatedBy = by.DeviceId, Signature = ""
        });
    }

    /// <summary>Adds (or updates the address of) a host, signed by member <paramref name="by"/>. Outside addresses it already
    /// had are kept.</summary>
    public NetworkRoster AddHost(INetworkSigner by, string id, string name, string origin, string spki, DateTimeOffset now) =>
        Put(by, new()
        {
            Kind = NetworkKinds.Host, Id = id, Name = CleanName(name, id), Origin = origin, Spki = spki,
            Addresses = Host(id)?.Addresses, Revision = NextRevision(now), UpdatedBy = by.DeviceId, Signature = ""
        });

    /// <summary>Sets a host's outside addresses (empty clears them), signed by member <paramref name="by"/>. Each is
    /// normalized by <see cref="NormalizeAddress"/>; an invalid one throws.</summary>
    public NetworkRoster SetHostAddresses(INetworkSigner by, string id, IEnumerable<string> addresses, DateTimeOffset now)
    {
        var current = Host(id) is { Removed: false } host ? host : throw new InvalidOperationException($"{id} is not a host in this network.");
        var list = addresses.Select(a => NormalizeAddress(a) ?? throw new ContractException(ErrorCode.InvalidContract,
                $"\"{a}\" is not an address Martlet can use: type name:port, IPv4:port or [IPv6]:port, for example home.example.net:9443."))
            .Distinct(StringComparer.Ordinal).ToArray();
        ContractRules.Require(list.Length <= MaximumAddresses, $"A host can have at most {MaximumAddresses} outside addresses.");
        return Put(by, current with
        {
            Addresses = list.Length == 0 ? null : list, Revision = NextRevision(now), UpdatedBy = by.DeviceId, Signature = ""
        });
    }

    /// <summary>Removes a desktop or host from the network, signed by member <paramref name="by"/>. The entry stays as a
    /// tombstone with its key, so an older copy cannot bring it back.</summary>
    public NetworkRoster Remove(INetworkSigner by, string kind, string id, DateTimeOffset now)
    {
        var current = Members.FirstOrDefault(m => m.Kind == kind && m.Id == id) ??
            throw new InvalidOperationException($"{id} is not in this network.");
        return Put(by, current with { Removed = true, Revision = NextRevision(now), UpdatedBy = by.DeviceId, Signature = "" });
    }

    private NetworkRoster Put(INetworkSigner by, NetworkMember unsigned)
    {
        ArgumentNullException.ThrowIfNull(by);
        if (!Trusts(by.DeviceId, by.PublicKey))
            throw new InvalidOperationException("This PC is not a member of the network, so it can't change it.");
        var signed = Sign(NetworkId, by, unsigned);
        var next = this with { Members = Sorted(Members.Where(m => m.Slot != signed.Slot).Append(signed)) };
        next.Validate();
        return next;
    }

    private static NetworkMember Sign(string networkId, INetworkSigner by, NetworkMember member) =>
        member with { Signature = System.Buffers.Text.Base64Url.EncodeToString(by.Sign(member.SigningBytes(networkId))) };

    /// <summary>
    /// Joins <paramref name="incoming"/> into <paramref name="current"/>, which this computer already accepted. An incoming
    /// entry is accepted when it is signed by a desktop that is active in the roster accepted so far (removals are taken
    /// first, so a desktop removed in the same copy cannot vouch for anything else in it); then the newest entry per
    /// computer wins. Different networks are refused.
    /// </summary>
    public static NetworkMerge Accept(NetworkRoster current, NetworkRoster incoming)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(incoming);
        ContractRules.Require(current.NetworkId == incoming.NetworkId, "That roster belongs to another Martlet network.");
        var accepted = current.Members.ToList();
        var known = accepted.Select(m => m.Slot + "|" + m.Signature).ToHashSet(StringComparer.Ordinal);
        var pending = incoming.Members.Where(m => !known.Contains(m.Slot + "|" + m.Signature))
            .OrderByDescending(m => m.Removed).ThenBy(m => m.Revision).ToList();
        var verified = new Dictionary<string, bool>(StringComparer.Ordinal);
        while (pending.Count > 0)
        {
            var signers = Resolve(accepted).Where(m => m.IsDesktop && !m.Removed).ToDictionary(m => m.Id, m => m.Key!, StringComparer.Ordinal);
            var next = pending.FirstOrDefault(m => signers.TryGetValue(m.UpdatedBy, out var key) && Verified(verified, current.NetworkId, m, key));
            if (next is null) break;
            accepted.Add(next);
            pending.Remove(next);
        }
        return new(current with { Members = Sorted(Cap(Resolve(accepted))) }, pending.Count);
    }

    /// <summary>
    /// Accepts a whole roster on a computer that has none yet (a host being bound, or a desktop that was just approved),
    /// from a source it already trusts through its owner-approved pairing. Each entry must carry a valid signature by a
    /// desktop listed in the roster (removed signers still count here, since the source accepted their earlier entries);
    /// entries that don't are dropped. From then on <see cref="Accept"/> applies.
    /// </summary>
    public static NetworkMerge Bootstrap(NetworkRoster incoming)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        incoming.Validate();
        var keys = incoming.Members.Where(m => m.IsDesktop).ToDictionary(m => m.Id, m => m.Key!, StringComparer.Ordinal);
        var verified = new Dictionary<string, bool>(StringComparer.Ordinal);
        var accepted = incoming.Members
            .Where(m => keys.TryGetValue(m.UpdatedBy, out var key) && Verified(verified, incoming.NetworkId, m, key)).ToList();
        ContractRules.Require(accepted.Any(m => m.IsDesktop && !m.Removed), "The roster has no member desktop.");
        return new(incoming with { Members = Sorted(accepted) }, incoming.Members.Count - accepted.Count);
    }

    private static bool Verified(Dictionary<string, bool> cache, string networkId, NetworkMember member, string key)
    {
        var id = member.Slot + "|" + member.Signature + "|" + key;
        if (!cache.TryGetValue(id, out var ok))
            cache[id] = ok = NetworkKey.Verify(key, member.SigningBytes(networkId), member.Signature);
        return ok;
    }

    /// <summary>The newest entry per computer; a desktop key that was removed never counts as active again.</summary>
    private static List<NetworkMember> Resolve(IEnumerable<NetworkMember> members)
    {
        var result = new List<NetworkMember>();
        foreach (var group in members.GroupBy(m => m.Slot, StringComparer.Ordinal))
        {
            var removedKeys = group.Where(m => m.IsDesktop && m.Removed && m.Key is not null).Select(m => m.Key!).ToHashSet(StringComparer.Ordinal);
            var candidates = group.Where(m => m.Removed || !m.IsDesktop || !removedKeys.Contains(m.Key!)).ToList();
            result.Add(candidates.Aggregate((a, b) => Newer(a, b) ? a : b));
        }
        return result;
    }

    private static IEnumerable<NetworkMember> Cap(IEnumerable<NetworkMember> members) =>
        members.OrderBy(m => m.Removed).ThenByDescending(m => m.Revision).ThenBy(m => m.Slot, StringComparer.Ordinal).Take(MaximumMembers);

    private static bool Newer(NetworkMember a, NetworkMember b) =>
        a.Revision != b.Revision ? a.Revision > b.Revision
        : a.UpdatedBy != b.UpdatedBy ? string.CompareOrdinal(a.UpdatedBy, b.UpdatedBy) > 0
        : string.CompareOrdinal(a.Content, b.Content) >= 0;

    public void Validate()
    {
        ContractRules.Require(SchemaVersion == 1, "This Martlet network roster was written by a newer Martlet.", ErrorCode.UnsupportedVersion);
        ContractRules.Require(NetworkKey.TryDecode(NetworkId, 16, out var id) && id.Length == 16 &&
            System.Buffers.Text.Base64Url.EncodeToString(id) == NetworkId, "The network ID is invalid.");
        ContractRules.Require(Members is { Count: > 0 and <= MaximumMembers } && Members.All(m => m is not null),
            "The network roster lists no computers or too many.");
        ContractRules.Require(Members.Select(m => m.Slot).Distinct(StringComparer.Ordinal).Count() == Members.Count,
            "The network roster lists a computer twice.");
        foreach (var member in Members)
        {
            ContractRules.Identifier(member.Id);
            ContractRules.Identifier(member.UpdatedBy);
            ContractRules.Require(IsName(member.Name), "A network member's name is invalid.");
            ContractRules.Require(member.Revision is > 0 and <= MaximumRevision, "A network revision is out of range.");
            ContractRules.Require(NetworkKey.TryDecode(member.Signature, 64, out var signature) && signature.Length == 64,
                "A network member's signature is malformed.");
            if (member.IsDesktop)
                ContractRules.Require(NetworkKey.IsPublicKey(member.Key) && member.Origin is null && member.Spki is null &&
                    member.Addresses is null, "A network desktop's key is invalid.");
            else if (member.IsHost)
                ContractRules.Require(member.Key is null && IsOrigin(member.Origin) && IsFingerprint(member.Spki) &&
                    (member.Addresses is null || member.Addresses is { Count: > 0 and <= MaximumAddresses } a &&
                        a.All(x => NormalizeAddress(x) == x) && a.Distinct(StringComparer.Ordinal).Count() == a.Count),
                    "A network host's address or fingerprint is invalid.");
            else
                ContractRules.Require(false, "A network member's kind is invalid.");
        }
    }

    public byte[] Write()
    {
        Validate();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(this with { Members = Sorted(Members) }, Json);
        ContractRules.Require(bytes.Length <= MaximumBytes, "The network roster is too large.", ErrorCode.PayloadTooLarge);
        return bytes;
    }

    public static NetworkRoster Parse(ReadOnlySpan<byte> bytes)
    {
        ContractRules.Require(bytes.Length is > 0 and <= MaximumBytes, "The network roster is empty or too large.", ErrorCode.PayloadTooLarge);
        NetworkRoster? roster;
        try { roster = JsonSerializer.Deserialize<NetworkRoster>(bytes, Json); }
        catch (Exception error) when (error is JsonException or NotSupportedException or InvalidOperationException)
        {
            throw new ContractException(ErrorCode.InvalidContract, "The network roster is malformed.");
        }
        ContractRules.Require(roster is not null, "The network roster is empty.");
        roster!.Validate();
        return roster with { Members = Sorted(roster.Members) };
    }

    /// <summary>Identifies the roster's content, to tell whether a copy is current.</summary>
    public string Digest() => Convert.ToHexStringLower(SHA256.HashData(Write()));

    private static NetworkMember[] Sorted(IEnumerable<NetworkMember> members) =>
        members.OrderBy(m => m.Kind, StringComparer.Ordinal).ThenBy(m => m.Id, StringComparer.Ordinal).ToArray();

    /// <summary>A display name the roster accepts: printable ASCII, trimmed, at most 64 characters, or <paramref name="fallback"/>.</summary>
    public static string CleanName(string? name, string fallback)
    {
        var text = new string((name ?? "").Where(c => c is >= ' ' and <= '~').Take(MaximumNameLength).ToArray()).Trim();
        return text.Length > 0 ? text : fallback;
    }

    private static bool IsName(string? text) => text is { Length: > 0 and <= MaximumNameLength } &&
        text.All(c => c is >= ' ' and <= '~') && text.Trim() == text;

    public static bool IsFingerprint(string? value) => value is { Length: 71 } && value.StartsWith("sha256:", StringComparison.Ordinal) &&
        value[7..].All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>Whether <paramref name="text"/> is a canonical https://&lt;private or loopback IP&gt;:&lt;port&gt; origin.</summary>
    public static bool IsOrigin(string? text)
    {
        if (text is not { Length: > 0 and <= 64 } || !Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            uri.UserInfo.Length != 0 || !IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address))
            return false;
        var bytes = address.GetAddressBytes();
        var isPrivate = IPAddress.IsLoopback(address) || address.AddressFamily == AddressFamily.InterNetwork &&
            (bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 || bytes[0] == 192 && bytes[1] == 168) ||
            address.AddressFamily == AddressFamily.InterNetworkV6 && (bytes[0] & 0xFE) == 0xFC;
        var canonical = address.AddressFamily == AddressFamily.InterNetworkV6 ? $"https://[{address}]:{uri.Port}" : $"https://{address}:{uri.Port}";
        return isPrivate && canonical == text;
    }

    /// <summary>
    /// The canonical form of an outside address ("name:port", "IPv4:port" or "[IPv6]:port"; a leading https:// and a
    /// trailing slash are dropped, names are lowercased), or null when it isn't one: names are DNS labels (letters, digits,
    /// hyphens), ports 1-65535, at most <see cref="MaximumAddressLength"/> characters.
    /// </summary>
    public static string? NormalizeAddress(string? text)
    {
        var value = (text ?? "").Trim();
        if (value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) value = value[8..];
        value = value.TrimEnd('/');
        if (value.Length is 0 or > MaximumAddressLength) return null;
        var colon = value.LastIndexOf(':');
        if (colon <= 0 || colon == value.Length - 1 || !int.TryParse(value[(colon + 1)..], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
            return null;
        var host = value[..colon];
        if (host.StartsWith('[') && host.EndsWith(']'))
            return IPAddress.TryParse(host[1..^1], out var v6) && v6.AddressFamily == AddressFamily.InterNetworkV6
                ? $"[{v6}]:{port}" : null;
        if (host.Contains(':')) return null;
        if (IPAddress.TryParse(host, out var v4))
            return v4.AddressFamily == AddressFamily.InterNetwork && host.Count(c => c == '.') == 3 ? $"{v4}:{port}" : null;
        host = host.ToLowerInvariant().TrimEnd('.');
        var labels = host.Split('.');
        return host.Length <= 253 && labels.All(l => l.Length is > 0 and <= 63 && l[0] != '-' && l[^1] != '-' &&
            l.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-')) && !labels[^1].All(char.IsAsciiDigit)
            ? $"{host}:{port}" : null;
    }

    public override string ToString() => $"Martlet network {NetworkId} ({ActiveDesktops.Count()} desktops, {ActiveHosts.Count()} hosts)";
}

/// <summary>How a member desktop pairs with a member host by itself: it signs this host's identity, the network, its device
/// ID and name, a timestamp and a fresh nonce with its network key, and posts it to <see cref="Path"/> over TLS pinned to
/// the host's fingerprint from the roster. The host checks the key against its roster and issues a credential.</summary>
public static class NetworkPairing
{
    public const string Path = "/martlet/v1/pair/member";
    public static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(2);

    public static byte[] ProofBytes(string hostId, string spki, string networkId, string deviceId, string displayName, long timestamp, string nonce) =>
        Encoding.UTF8.GetBytes($"martlet-member-pair-v1\n{hostId}\n{spki}\n{networkId}\n{deviceId}\n{displayName}\n{timestamp}\n{nonce}");
}
