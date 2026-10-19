using System.Security.Cryptography;
using Martlet.Core.Access;
using Martlet.Core.Network;
using Martlet.Gateway;

namespace Martlet.Gateway.Tests;

/// <summary>Pairing edge cases (#1008) and shared store failures (#1006).</summary>
public sealed class GatewayReliabilityTests
{
    private static readonly GatewayHostIdentity Identity = new()
    {
        HostId = "fixture-host",
        SpkiFingerprint = "sha256:" + new string('0', 64)
    };

    private sealed class RosterStorage(byte[] bytes) : IGatewayNetworkStorage
    {
        public byte[]? Load() => bytes;
        public void Save(byte[] value) => bytes = value;
    }

    private static ManualGatewayClock Clock() => new(new DateTimeOffset(2026, 9, 21, 20, 0, 0, TimeSpan.Zero));

    private static void AssertCode(string code, Action action) =>
        Assert.Equal(code, Assert.Throws<GatewayProtocolException>(action).Failure.Code);

    private static GatewayMemberPairingProof Proof(NetworkKey key, string networkId, ManualGatewayClock clock, bool forged = false)
    {
        var timestamp = clock.GetUtcNow().ToUnixTimeMilliseconds();
        var nonce = Base64Url.Encode(RandomNumberGenerator.GetBytes(16));
        var signature = key.Sign(NetworkPairing.ProofBytes(Identity.HostId, Identity.SpkiFingerprint, networkId, key.DeviceId,
            "MIKO", timestamp, nonce));
        if (forged) signature[0] ^= 0xff;
        return new()
        {
            ProtocolVersion = GatewayProtocolVersion.Current, NetworkId = networkId, DeviceId = key.DeviceId,
            DisplayName = "MIKO", Timestamp = timestamp, Nonce = nonce, Signature = Base64Url.Encode(signature)
        };
    }

    [Fact]
    public void False_member_proofs_from_one_address_do_not_block_members_pairing_from_another()
    {
        var clock = Clock();
        var credentials = new GatewayCredentialStore(Identity, clock);
        var network = new GatewayNetworkStore(Identity, credentials, clock, (_, _) => { });
        using var miko = NetworkKey.Create("desktop-miko");
        var roster = NetworkRoster.Found(miko, "MIKO", clock.GetUtcNow())
            .AddHost(miko, Identity.HostId, "Fixture", "https://192.168.1.2:47100", Identity.SpkiFingerprint, clock.GetUtcNow());
        network.Attach(new RosterStorage(roster.Write()));

        for (var i = 0; i < GatewayNetworkStore.MaximumFailedPairingsPerMinute; i++)
            AssertCode("pairing.invalid", () => network.PairMember(Proof(miko, roster.NetworkId, clock, forged: true), default, "192.168.1.66"));
        AssertCode("auth.rate", () => network.PairMember(Proof(miko, roster.NetworkId, clock), default, "192.168.1.66"));

        // The real member on its own address pairs, and successful pairings never count toward the limit.
        for (var i = 0; i < GatewayNetworkStore.MaximumFailedPairingsPerMinute + 5; i++)
            network.PairMember(Proof(miko, roster.NetworkId, clock), default, "192.168.1.10");
        // Each re-pair replaced the older credential in the same commit: one live credential remains.
        Assert.Single(credentials.ListRegistrations(), r => !r.Revoked);

        clock.Advance(TimeSpan.FromMinutes(1));
        network.PairMember(Proof(miko, roster.NetworkId, clock), default, "192.168.1.66");
    }

    [Fact]
    public void Re_pairing_at_capacity_replaces_the_credential_and_a_failed_issue_keeps_the_old_one()
    {
        var clock = Clock();
        var store = new GatewayCredentialStore(Identity, clock);
        var old = store.Issue("desktop-miko", "MIKO", [GatewayRole.Voice]);
        for (var i = 1; i < GatewayCredentialStore.MaximumRegistrations; i++)
            store.Issue("other-" + i, "Other", [GatewayRole.Voice]);

        // Full: a new device is refused and nothing changes.
        AssertCode("auth.capacity", () => store.IssueReplacing("desktop-new", "New", [GatewayRole.Voice]));
        Assert.Equal(GatewayCredentialStore.MaximumRegistrations, store.ListRegistrations().Count);

        // The paired device's re-pair frees its own slot, so it still works when the host is full.
        var replacement = store.IssueReplacing("desktop-miko", "MIKO", [GatewayRole.Voice]);
        var miko = store.ListRegistrations().Where(r => r.DeviceId == "desktop-miko").ToArray();
        Assert.Equal(replacement.CredentialId, Assert.Single(miko).CredentialId);
        Assert.DoesNotContain(store.ListRegistrations(), r => r.CredentialId == old.CredentialId);
    }

    [Fact]
    public void An_expired_card_window_issues_no_credential()
    {
        var clock = Clock();
        var store = new GatewayCredentialStore(Identity, clock);
        var pairing = new GatewayPairingService(Identity, new("https://192.168.1.2:47100"), store, clock);
        var card = pairing.OpenWindow(new() { DeviceId = "desktop-miko", DisplayName = "MIKO", Roles = [GatewayRole.Voice] });
        clock.Advance(GatewayPairingService.DefaultWindow);
        AssertCode("pairing.expired", () => pairing.Exchange(new GatewayPairingProof
        {
            ProtocolVersion = GatewayProtocolVersion.Current, PairingId = card.PairingId, PairingToken = card.Token.Reveal(),
            HostId = card.HostId, SpkiFingerprint = card.SpkiFingerprint, DeviceId = "desktop-miko"
        }));
        Assert.Empty(store.ListRegistrations());
    }

    private sealed class FlakyKeyStorage : IGatewayApiKeyStorage
    {
        internal bool Broken = true;
        internal int Saves;
        internal byte[]? Bytes;
        public byte[]? Load() => Broken ? throw new IOException("disk gone") : Bytes;
        public void Save(byte[] bytes)
        {
            Saves++;
            if (Broken) throw new IOException("disk full");
            Bytes = bytes;
        }
    }

    [Fact]
    public void Store_failures_are_logged_and_reported_and_an_unsaved_revocation_is_saved_at_the_next_sync()
    {
        var clock = Clock();
        var lines = new List<(string Level, string Message, string Key)>();
        var health = new GatewayStoreHealth(clock, (level, message, key) => lines.Add((level, message, key)));
        var storage = new FlakyKeyStorage();
        var keys = new GatewayApiKeyStore(Identity.HostId, clock);
        keys.Attach(storage, health);
        Assert.Contains(lines, l => l.Key == "store:API keys:load" && l.Message.Contains("disk gone"));

        var (list, key) = ApiKeyList.Empty.Create("Home Assistant", [ApiKeyScopes.Voice], null, "desktop-miko", clock.GetUtcNow());
        list = list.Revoke(key.Key.Id, "desktop-miko", clock.GetUtcNow());
        keys.Merge(list);
        // Security state is tried twice, and the revocation applies in memory at once.
        Assert.Equal(2, storage.Saves);
        Assert.True(keys.Current.Find(key.Key.Id)!.Revoked);
        Assert.False(health.Healthy);
        var status = Assert.Single(health.Snapshot(), s => s.Store == "API keys");
        Assert.Equal("save", status.Failing);
        Assert.Contains("disk full", status.LastFailure);
        Assert.Contains(lines, l => l.Level == "ERROR" && l.Key == "store:API keys:save");

        // The next sync brings nothing new, but the copy is still saved because the last save failed.
        storage.Broken = false;
        keys.Merge(list);
        Assert.NotNull(storage.Bytes);
        Assert.True(ApiKeyList.Parse(storage.Bytes).Find(key.Key.Id)!.Revoked);
        Assert.Null(Assert.Single(health.Snapshot(), s => s.Store == "API keys").Failing);
        var saves = storage.Saves;
        keys.Merge(list);
        Assert.Equal(saves, storage.Saves);
    }
}
