using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Martlet.Core.Network;
using Martlet.Gateway;

namespace Martlet.Gateway.Tests;

/// <summary>A member desktop that joined the network while nobody could tell this host (LUMINE joined through another host
/// after MIKO, the only computer that reached this host, last reached that one) pairs by itself by bringing its copy of the
/// roster. The host takes only entries signed by desktops it already trusts (docs/NETWORK.md, "When a host is added where
/// your other computers can't reach").</summary>
public sealed class MemberPairingRosterTests
{
    private sealed class RosterStorage(byte[] bytes) : IGatewayNetworkStorage
    {
        internal byte[]? Bytes = bytes;
        public byte[]? Load() => Bytes;
        public void Save(byte[] value) => Bytes = value;
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> PairAsync(GatewayTestHost host, NetworkKey key, string networkId,
        NetworkRoster? bring)
    {
        var timestamp = host.Clock.GetUtcNow().ToUnixTimeMilliseconds();
        var nonce = System.Buffers.Text.Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));
        var signature = System.Buffers.Text.Base64Url.EncodeToString(key.Sign(NetworkPairing.ProofBytes(host.Identity.HostId, host.Identity.SpkiFingerprint,
            networkId, key.DeviceId, "LUMINE", timestamp, nonce)));
        var proof = new Dictionary<string, object>
        {
            ["protocol_version"] = new { major = 2, minor = 0 }, ["network_id"] = networkId, ["device_id"] = key.DeviceId,
            ["display_name"] = "LUMINE", ["timestamp"] = timestamp, ["nonce"] = nonce, ["signature"] = signature
        };
        if (bring is not null) proof["roster"] = JsonSerializer.Deserialize<JsonElement>(bring.Write());
        using var request = new HttpRequestMessage(HttpMethod.Post, host.Origin.CanonicalOrigin + NetworkPairing.Path)
        {
            Content = new StringContent(JsonSerializer.Serialize(proof), Encoding.UTF8, "application/json")
        };
        using var response = await host.Client.SendAsync(request);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        return (response.StatusCode, document.RootElement.Clone());
    }

    [Fact]
    public async Task A_member_the_host_never_heard_of_pairs_by_bringing_a_roster_its_members_signed()
    {
        await using var host = await GatewayTestHost.StartAsync();
        var now = host.Clock.GetUtcNow();
        using var miko = NetworkKey.Create("desktop-miko");
        using var diva = NetworkKey.Create("desktop-diva");
        using var lumine = NetworkKey.Create("desktop-lumine");
        // What MIKO bound this host with: MIKO, DIVA and this host.
        var known = NetworkRoster.Found(miko, "MIKO", now)
            .AddDesktop(miko, diva.DeviceId, "DIVA", diva.PublicKey, now)
            .AddHost(miko, host.Identity.HostId, "Fixture", host.Origin.CanonicalOrigin, host.Identity.SpkiFingerprint, now);
        var storage = new RosterStorage(known.Write());
        host.Server.AttachNetworkStorage(storage);
        // DIVA let LUMINE in later, through another host.
        var lumines = known.AddDesktop(diva, lumine.DeviceId, "LUMINE", lumine.PublicKey, now.AddMinutes(1));

        var (refused, why) = await PairAsync(host, lumine, known.NetworkId, null);
        Assert.NotEqual(HttpStatusCode.Created, refused);
        Assert.Equal("network.denied", why.GetProperty("code").GetString());

        var (status, body) = await PairAsync(host, lumine, known.NetworkId, lumines);
        Assert.True(status == HttpStatusCode.Created, body.GetRawText());
        Assert.Equal(lumine.DeviceId, body.GetProperty("device_id").GetString());
        var saved = NetworkRoster.Parse(storage.Bytes);
        Assert.True(saved.Trusts(lumine.DeviceId, lumine.PublicKey));
        Assert.True(saved.Trusts(miko.DeviceId, miko.PublicKey));
    }

    [Fact]
    public async Task A_roster_signed_by_someone_the_host_doesnt_trust_or_of_another_network_lets_nobody_in()
    {
        await using var host = await GatewayTestHost.StartAsync();
        var now = host.Clock.GetUtcNow();
        using var miko = NetworkKey.Create("desktop-miko");
        using var intruder = NetworkKey.Create("desktop-intruder");
        var known = NetworkRoster.Found(miko, "MIKO", now)
            .AddHost(miko, host.Identity.HostId, "Fixture", host.Origin.CanonicalOrigin, host.Identity.SpkiFingerprint, now);
        var storage = new RosterStorage(known.Write());
        host.Server.AttachNetworkStorage(storage);

        // The intruder signs itself into a copy of the network: its entry is signed by a desktop the host doesn't trust.
        var self = NetworkRoster.Found(intruder, "INTRUDER", now).Members[0];
        var forged = known with { Members = known.Members.Append(self with { Signature = Resign(known.NetworkId, intruder, self) }).ToArray() };
        var (status, body) = await PairAsync(host, intruder, known.NetworkId, forged);
        Assert.NotEqual(HttpStatusCode.Created, status);
        Assert.Equal("network.denied", body.GetProperty("code").GetString());
        Assert.Null(NetworkRoster.Parse(storage.Bytes).Desktop(intruder.DeviceId));

        // Its own network's roster names another network.
        var (other, otherWhy) = await PairAsync(host, intruder, known.NetworkId, NetworkRoster.Found(intruder, "INTRUDER", now));
        Assert.NotEqual(HttpStatusCode.Created, other);
        Assert.Equal("network.other", otherWhy.GetProperty("code").GetString());
    }

    /// <summary>The member's entry signed for <paramref name="networkId"/> by <paramref name="by"/> (as a member desktop would).</summary>
    private static string Resign(string networkId, NetworkKey by, NetworkMember member)
    {
        var bytes = Encoding.UTF8.GetBytes($"martlet-network-member-v1\n{networkId}\n{member.Kind}\n{member.Id}\n{member.Name}\n{member.Key}\n" +
            $"{member.Origin}\n{member.Spki}\n{(member.Removed ? 1 : 0)}\n{member.Revision}\n{member.UpdatedBy}");
        return System.Buffers.Text.Base64Url.EncodeToString(by.Sign(bytes));
    }
}
