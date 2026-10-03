using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Martlet.Core.Contracts;
using Martlet.Core.Network;

namespace Martlet.Avatar.Audio2Face.Remote;

/// <summary>A desktop that paired with a host and asks to join the host's Martlet network, as that host reports it to members.</summary>
public sealed record HostJoinRequest(string HostId, string DeviceId, string DisplayName, string Key, string CheckNumber, DateTimeOffset RequestedAt);

/// <summary>A computer paired with a host, as that host reports it: when its current pairing was made and when it last made
/// a signed request (null when it hasn't since the host's gateway started).</summary>
public sealed record HostPairedDevice(string HostId, string DeviceId, string DisplayName, DateTimeOffset PairedAt, DateTimeOffset? LastSeen);

/// <summary>A host's place in the owner's Martlet network: <see cref="State"/> is "unbound", "bound" or "removed", with
/// the roster it accepted (null when unbound) and, for a member desktop, pending join requests. <see cref="Supported"/>
/// is false for hosts older than the network; <see cref="Devices"/> (the computers paired with it) is null for hosts older
/// than that list.</summary>
public sealed record HostNetworkView(string HostId, string State, NetworkRoster? Roster, IReadOnlyList<HostJoinRequest> Joins, bool Supported = true,
    IReadOnlyList<HostPairedDevice>? Devices = null)
{
    public static HostNetworkView Unsupported(string hostId) => new(hostId, "unsupported", null, [], false);
    public bool Bound => State == "bound" && Roster is not null;
}

/// <summary>The host's Martlet network: read it, merge this PC's roster into it, ask to join it, turn a request down.</summary>
public sealed partial class Audio2FaceHostConnection
{
    private const string NetworkPath = "/martlet/v1/network";

    /// <summary>Reads the host's network state. Hosts older than the network refuse with code <c>request.invalid</c>.</summary>
    public async Task<HostNetworkView> ReadNetworkAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, pairing.Origin + NetworkPath);
        Sign(request, []);
        return await NetworkAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Merges <paramref name="roster"/> into the host's copy (binding a host in no network) and returns the result.</summary>
    public async Task<HostNetworkView> MergeNetworkAsync(NetworkRoster roster, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roster);
        var body = roster.Write();
        using var request = new HttpRequestMessage(HttpMethod.Post, pairing.Origin + NetworkPath) { Content = Audio2FaceHostClient.JsonContent(body) };
        Sign(request, body);
        return await NetworkAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Asks to join the host's network with this PC's network key. Returns "pending" (a member must allow it) or
    /// "member", the network ID and the check number both screens show.</summary>
    public async Task<(string State, string NetworkId, string CheckNumber)> RequestJoinAsync(string displayName, string publicKey,
        CancellationToken cancellationToken = default)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            protocol_version = new { major = 2, minor = 0 },
            display_name = NetworkRoster.CleanName(displayName, pairing.DeviceId), key = publicKey
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, pairing.Origin + NetworkPath + "/join") { Content = Audio2FaceHostClient.JsonContent(body) };
        Sign(request, body);
        using var root = await SendNetworkAsync(request, cancellationToken).ConfigureAwait(false);
        try
        {
            var state = root.RootElement.GetProperty("state").GetString();
            var network = root.RootElement.GetProperty("network_id").GetString()!;
            var check = root.RootElement.GetProperty("check_number").GetString()!;
            if (state is not ("pending" or "member") || check.Length != 7) throw new FormatException();
            return (state, network, check);
        }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new Audio2FaceHostException("response.invalid", "The host's answer to the join request was invalid.");
        }
    }

    /// <summary>Turns down <paramref name="deviceId"/>'s request to join (this PC must be a member).</summary>
    public async Task<bool> DenyJoinAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new { protocol_version = new { major = 2, minor = 0 }, device_id = deviceId });
        using var request = new HttpRequestMessage(HttpMethod.Post, pairing.Origin + NetworkPath + "/deny") { Content = Audio2FaceHostClient.JsonContent(body) };
        Sign(request, body);
        using var root = await SendNetworkAsync(request, cancellationToken).ConfigureAwait(false);
        return root.RootElement.TryGetProperty("denied", out var denied) && denied.ValueKind == JsonValueKind.True;
    }

    private async Task<HostNetworkView> NetworkAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var document = await SendNetworkAsync(request, cancellationToken).ConfigureAwait(false);
        try
        {
            var root = document.RootElement;
            var state = root.GetProperty("state").GetString();
            if (state is not ("unbound" or "bound" or "removed")) throw new FormatException();
            NetworkRoster? roster = null;
            if (root.TryGetProperty("roster", out var element) && element.ValueKind == JsonValueKind.Object)
                roster = NetworkRoster.Parse(JsonSerializer.SerializeToUtf8Bytes(element));
            var joins = new List<HostJoinRequest>();
            if (root.TryGetProperty("joins", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var join in list.EnumerateArray().Take(16))
                {
                    var device = join.GetProperty("device_id").GetString()!;
                    var key = join.GetProperty("key").GetString()!;
                    Audio2FaceHostClient.RequireIdentifier(device, "device ID");
                    if (!NetworkKey.IsPublicKey(key)) throw new FormatException();
                    joins.Add(new(pairing.HostId, device, NetworkRoster.CleanName(join.GetProperty("display_name").GetString(), device), key,
                        join.GetProperty("check_number").GetString()!, join.GetProperty("requested_at").GetDateTimeOffset()));
                }
            List<HostPairedDevice>? devices = null;
            if (root.TryGetProperty("devices", out var paired) && paired.ValueKind == JsonValueKind.Array)
            {
                devices = [];
                // The list only informs; an entry this PC can't read is left out rather than failing the network sync.
                foreach (var item in paired.EnumerateArray().Take(32))
                    try
                    {
                        var device = item.GetProperty("device_id").GetString()!;
                        Audio2FaceHostClient.RequireIdentifier(device, "device ID");
                        devices.Add(new(pairing.HostId, device, NetworkRoster.CleanName(item.GetProperty("display_name").GetString(), device),
                            item.GetProperty("paired_at").GetDateTimeOffset(),
                            item.TryGetProperty("last_seen", out var seen) && seen.ValueKind == JsonValueKind.String ? seen.GetDateTimeOffset() : null));
                    }
                    catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or FormatException or Audio2FaceHostException) { }
            }
            return new(pairing.HostId, state, roster, joins, Devices: devices);
        }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or FormatException or ContractException)
        {
            throw new Audio2FaceHostException("response.invalid", "The host's Martlet network was invalid.");
        }
    }

    private async Task<JsonDocument> SendNetworkAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        using var response = await Audio2FaceHostClient.Send(http, request, timeout.Token).ConfigureAwait(false);
        var document = await Audio2FaceHostClient.ReadJson(response, NetworkRoster.MaximumBytes + 16_384, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.OK)
        {
            if (document.RootElement.TryGetProperty("host_id", out var host) && host.GetString() == pairing.HostId) return document;
            document.Dispose();
            throw new Audio2FaceHostException("response.invalid", "The host identity changed; pair again.");
        }
        try { throw Audio2FaceHostClient.Remote(document.RootElement); }
        finally { document.Dispose(); }
    }
}

/// <summary>Pairs this desktop with a host of its Martlet network by itself: no code, no console. The connection is pinned
/// to the host's fingerprint from the roster, and the request is signed with this PC's network key.</summary>
public static class HostNetworkPairing
{
    public static async Task<(Audio2FaceHostPairing Pairing, string Secret)> PairAsMemberAsync(NetworkMember host, string networkId,
        INetworkSigner signer, string displayName, CancellationToken cancellationToken = default, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(signer);
        if (!host.IsHost || host.Removed || host.Origin is null || host.Spki is null)
            throw new Audio2FaceHostException("network.invalid", $"{host.Id} is not an active host of this network.");
        var origin = Audio2FaceHostClient.CanonicalOrigin(host.Origin);
        var name = NetworkRoster.CleanName(displayName, signer.DeviceId);
        var timestamp = (clock ?? TimeProvider.System).GetUtcNow().ToUnixTimeMilliseconds();
        var nonce = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));
        var signature = Base64Url.EncodeToString(signer.Sign(
            NetworkPairing.ProofBytes(host.Id, host.Spki, networkId, signer.DeviceId, name, timestamp, nonce)));
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            protocol_version = new { major = 2, minor = 0 },
            network_id = networkId, device_id = signer.DeviceId, display_name = name, timestamp, nonce, signature
        });
        try
        {
            using var http = Audio2FaceHostClient.CreateHttpClient(origin, host.Spki);
            using var request = new HttpRequestMessage(HttpMethod.Post, origin + NetworkPairing.Path) { Content = Audio2FaceHostClient.JsonContent(body) };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await Audio2FaceHostClient.Send(http, request, timeout.Token).ConfigureAwait(false);
            using var document = await Audio2FaceHostClient.ReadJson(response, 16 * 1024, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.Created) throw Audio2FaceHostClient.Remote(document.RootElement);
            var root = document.RootElement;
            var credentialId = root.GetProperty("credential_id").GetString()!;
            var secret = root.GetProperty("credential_secret").GetString()!;
            if (root.GetProperty("protocol_version").GetProperty("major").GetInt32() != 2 || root.GetProperty("host_id").GetString() != host.Id ||
                root.GetProperty("device_id").GetString() != signer.DeviceId ||
                root.GetProperty("lifetime").GetProperty("kind").GetString() != "paired" ||
                !root.GetProperty("roles").EnumerateArray().Any(role => role.GetString() == "voice") ||
                !Audio2FaceHostClient.TryBase64Url(credentialId, 16, out _) || !Audio2FaceHostClient.TryBase64Url(secret, 32, out var raw))
                throw new Audio2FaceHostException("pairing.invalid", $"{host.Id} returned an unexpected pairing.");
            CryptographicOperations.ZeroMemory(raw);
            return (new Audio2FaceHostPairing
            {
                Origin = origin, HostId = host.Id, SpkiFingerprint = host.Spki, DeviceId = signer.DeviceId, CredentialId = credentialId
            }, secret);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new Audio2FaceHostException("response.invalid", $"{host.Id}'s pairing response was invalid.");
        }
        finally { CryptographicOperations.ZeroMemory(body); }
    }
}
