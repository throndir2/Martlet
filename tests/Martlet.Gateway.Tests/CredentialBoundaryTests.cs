using System.Net;
using System.Security.Cryptography;
using Martlet.Gateway;

namespace Martlet.Gateway.Tests;

public sealed class CredentialBoundaryTests
{
    private static readonly GatewayHostIdentity Identity = new()
    {
        HostId = "fixture-host",
        SpkiFingerprint = "sha256:" + new string('0', 64)
    };

    [Fact]
    public void Capacity_refuses_issue_and_rotation_without_changing_live_credentials()
    {
        var clock = Clock();
        var store = new GatewayCredentialStore(Identity, clock);
        var original = Issue(store);
        var acceptedRequest = Signed(original, clock);
        store.Authenticate(acceptedRequest);
        for (var index = 1; index < GatewayCredentialStore.MaximumRegistrations; index++)
            Issue(store);
        AssertCode("auth.capacity", () => Issue(store));
        AssertCode("auth.capacity", () => store.Rotate(original.CredentialId, TimeSpan.Zero));
        var registration = store.ListRegistrations().Single(item =>
            item.CredentialId == original.CredentialId);
        Assert.Equal(original.Lifetime, registration.Lifetime);
        Assert.Null(registration.RotatedToCredentialId);
        AssertCode("auth.replay", () => store.Authenticate(acceptedRequest));
        Assert.Equal(original.CredentialId,
            store.Authenticate(Signed(original, clock)).CredentialId);
        Assert.Equal(GatewayCredentialStore.MaximumRegistrations, store.ListRegistrations().Count);
    }

    [Fact]
    public void Only_explicit_revocation_reclaims_permanent_registration_slots()
    {
        var clock = Clock();
        var store = new GatewayCredentialStore(Identity, clock);
        for (var cycle = 0; cycle < 3; cycle++)
        {
            var credentials = Enumerable.Range(0, GatewayCredentialStore.MaximumRegistrations)
                .Select(_ => Issue(store)).ToArray();
            AssertCode("auth.capacity", () => Issue(store));
            foreach (var credential in credentials)
                Assert.True(store.RevokeCredential(credential.CredentialId));
            Assert.Empty(store.ListRegistrations());

            clock.Advance(TimeSpan.FromDays(3650));
            var fresh = Issue(store);
            Assert.Single(store.ListRegistrations());
            AssertCode("auth.invalid", () => store.Authenticate(Signed(credentials[0], clock)));
            store.RevokeCredential(fresh.CredentialId);
        }
    }

    [Fact]
    public async Task Concurrent_admission_has_one_winner_for_the_last_slot()
    {
        var store = new GatewayCredentialStore(Identity, Clock());
        for (var index = 1; index < GatewayCredentialStore.MaximumRegistrations; index++)
            Issue(store);
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
        {
            try
            {
                Issue(store);
                return "issued";
            }
            catch (GatewayProtocolException error)
            {
                return error.Failure.Code;
            }
        })));
        Assert.Single(outcomes, outcome => outcome == "issued");
        Assert.Equal(15, outcomes.Count(outcome => outcome == "auth.capacity"));
        Assert.Equal(GatewayCredentialStore.MaximumRegistrations, store.ListRegistrations().Count);
    }

    [Fact]
    public async Task Capacity_refusal_preserves_pairing_approval_until_a_slot_is_reclaimed()
    {
        await using var host = await GatewayTestHost.StartAsync();
        var original = Issue(host.Server.Credentials);
        for (var index = 1; index < GatewayCredentialStore.MaximumRegistrations; index++)
            Issue(host.Server.Credentials);
        var card = host.OpenPairing();
        using (var denied = await host.SendPairingAsync(card, card.SpkiFingerprint))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, denied.StatusCode);
            Assert.Equal("auth.capacity", await GatewayTestHost.FailureCode(denied));
        }
        host.Server.Credentials.RevokeCredential(original.CredentialId);
        using (var paired = await host.SendPairingAsync(card, card.SpkiFingerprint))
            Assert.Equal(HttpStatusCode.Created, paired.StatusCode);
        using var replay = await host.SendPairingAsync(card, card.SpkiFingerprint);
        Assert.Equal("pairing.closed", await GatewayTestHost.FailureCode(replay));
    }

    [Fact]
    public void Small_clock_step_back_holds_observed_time_instead_of_closing()
    {
        var clock = Clock();
        var store = new GatewayCredentialStore(Identity, clock);
        var credential = Issue(store);
        var first = Signed(credential, clock);
        store.Authenticate(first);
        clock.Advance(TimeSpan.FromMilliseconds(-1_300));
        store.Authenticate(Signed(credential, clock));
        AssertCode("auth.replay", () => store.Authenticate(first));
        clock.Advance(TimeSpan.FromMilliseconds(1_300) - GatewayCredentialStore.MaximumClockStepBack);
        store.Authenticate(Signed(credential, clock));
        Assert.Single(store.ListRegistrations());
        clock.Advance(TimeSpan.FromTicks(-1));
        AssertCode("auth.clock_invalid", () => store.Authenticate(Signed(credential, clock)));
    }

    [Fact]
    public void Observed_rotation_cannot_be_reversed_by_clock_rollback()
    {
        var clock = Clock();
        var store = new GatewayCredentialStore(Identity, clock);
        var original = Issue(store);
        store.Rotate(original.CredentialId, TimeSpan.FromMinutes(1));
        clock.Advance(TimeSpan.FromMinutes(1));
        AssertCode("auth.expired", () => store.Authenticate(Signed(original, clock)));
        clock.Advance(TimeSpan.FromMinutes(-1));
        AssertCode("auth.clock_invalid", () => store.Authenticate(Signed(original, clock)));
        clock.Advance(TimeSpan.FromHours(1));
        AssertCode("auth.clock_invalid", () => Issue(store));
        AssertCode("auth.clock_invalid", () => store.ListRegistrations());
        AssertCode("auth.clock_invalid", () => store.Rotate(original.CredentialId, TimeSpan.Zero));
    }

    [Fact]
    public void Replay_after_nonce_cleanup_and_clock_rollback_permanently_closes_authority()
    {
        var clock = Clock();
        var store = new GatewayCredentialStore(Identity, clock);
        var credential = Issue(store);
        var first = Signed(credential, clock);
        store.Authenticate(first);
        clock.Advance(TimeSpan.FromMinutes(5));
        store.Authenticate(Signed(credential, clock));
        clock.Advance(TimeSpan.FromMinutes(-5));
        AssertCode("auth.clock_invalid", () => store.Authenticate(first));
        clock.Advance(TimeSpan.FromMinutes(5));
        AssertCode("auth.clock_invalid", () => store.Authenticate(Signed(credential, clock)));
    }

    [Fact]
    public async Task Store_clock_observations_close_pending_pairing_and_cannot_be_recovered()
    {
        await using var host = await GatewayTestHost.StartAsync();
        var card = host.OpenPairing();
        host.Clock.Advance(TimeSpan.FromMinutes(6));
        host.Server.Credentials.ListRegistrations();
        host.Clock.Advance(TimeSpan.FromMinutes(-6));
        using (var denied = await host.SendPairingAsync(card, card.SpkiFingerprint))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, denied.StatusCode);
            Assert.Equal("auth.clock_invalid", await GatewayTestHost.FailureCode(denied));
        }
        host.Clock.Advance(TimeSpan.FromMinutes(6));
        AssertCode("auth.clock_invalid", () => host.OpenPairing());
        using var stillDenied = await host.SendPairingAsync(card, card.SpkiFingerprint);
        Assert.Equal("auth.clock_invalid", await GatewayTestHost.FailureCode(stillDenied));
    }

    [Fact]
    public void Pairing_clock_observations_also_close_previously_issued_credentials()
    {
        var clock = Clock();
        var store = new GatewayCredentialStore(Identity, clock);
        var pairing = new GatewayPairingService(Identity,
            new GatewayOrigin("https://127.0.0.1:9443"), store, clock);
        var credential = Issue(store);
        clock.Advance(TimeSpan.FromMinutes(1));
        pairing.OpenWindow(new()
        {
            DeviceId = "fixture-device",
            DisplayName = "Fixture device",
            Roles = [GatewayRole.Voice]
        });
        clock.Advance(TimeSpan.FromMinutes(-1));
        AssertCode("auth.clock_invalid", () => store.Authenticate(Signed(credential, clock)));
    }

    private static ManualGatewayClock Clock() =>
        new(new DateTimeOffset(2026, 9, 21, 20, 0, 0, TimeSpan.Zero));

    private static IssuedDeviceCredential Issue(GatewayCredentialStore store) =>
        store.Issue("fixture-device", "Fixture device", [GatewayRole.Voice]);

    private static void AssertCode(string code, Action action) =>
        Assert.Equal(code, Assert.Throws<GatewayProtocolException>(action).Failure.Code);

    private static GatewaySignedRequest Signed(
        IssuedDeviceCredential credential, ManualGatewayClock clock)
    {
        Assert.True(Base64Url.TryDecode(credential.Secret.Reveal(), 32, out var secret));
        var verifier = SHA256.HashData(secret);
        CryptographicOperations.ZeroMemory(secret);
        var timestamp = clock.GetUtcNow();
        var nonce = Base64Url.Encode(RandomNumberGenerator.GetBytes(24));
        var canonical = GatewayRequestSigner.Canonical(Identity.HostId,
            credential.CredentialId, "GET", "/martlet/v1/status", "voice",
            timestamp.ToUnixTimeSeconds(), nonce, SHA256.HashData([]));
        var signature = HMACSHA256.HashData(verifier, canonical);
        CryptographicOperations.ZeroMemory(verifier);
        return new()
        {
            CredentialId = credential.CredentialId,
            Signature = signature,
            Nonce = nonce,
            Timestamp = timestamp,
            Role = GatewayRole.Voice,
            CanonicalBytes = canonical
        };
    }
}
