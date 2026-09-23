using Martlet.Gateway;
using Martlet.Gateway.Persistence;

namespace Martlet.Gateway.Persistence.Tests;

public sealed class AuthorityTests : NativeTest
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Pairing_survives_years_reboots_and_clean_or_dirty_restarts(bool clean)
    {
        var clock = new Clock();
        IssuedDeviceCredential credential;
        GatewayHostIdentity identity;
        using (var host = new NativeAuthority(Store, clock, create: true))
        {
            identity = host.Identity;
            credential = host.Issue();
            Assert.IsType<PairedDeviceLifetime>(credential.Lifetime);
            if (clean) host.Clean();
        }
        clock.Advance(TimeSpan.FromDays(3650));
        clock.Ticks = 0;
        using var next = new NativeAuthority(Store, clock, boot: Guid.NewGuid());
        Assert.Equal(identity, next.Identity);
        Assert.IsType<PairedDeviceLifetime>(Assert.Single(next.Credentials.ListRegistrations()).Lifetime);
        Assert.Equal(credential.CredentialId, next.Credentials.Authenticate(next.Signed(credential)).CredentialId);
        next.Clean();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Live_nonce_and_rate_history_survive_restart_and_stalled_utc(bool clean)
    {
        var clock = new Clock();
        IssuedDeviceCredential credential;
        GatewaySignedRequest first;
        using (var host = new NativeAuthority(Store, clock, create: true))
        {
            credential = host.Issue();
            first = host.Signed(credential);
            host.Credentials.Authenticate(first);
            for (var i = 1; i < GatewayCredentialStore.MaximumNoncesPerCredential; i++)
                host.Credentials.Authenticate(host.Signed(credential));
            clock.Advance(TimeSpan.FromMinutes(5), utc: false);
            Code("auth.replay", () => host.Credentials.Authenticate(first));
            Code("auth.rate", () => host.Credentials.Authenticate(host.Signed(credential)));
            if (clean) host.Clean();
        }
        using var next = new NativeAuthority(Store, clock, boot: Guid.NewGuid());
        Code("auth.replay", () => next.Credentials.Authenticate(first));
        Code("auth.rate", () => next.Credentials.Authenticate(next.Signed(credential)));
        clock.Advance(TimeSpan.FromMinutes(5));
        next.Credentials.Authenticate(next.Signed(credential));
        next.Clean();
    }

    [Fact]
    public void Rotation_retires_only_the_old_key_not_the_device_pairing()
    {
        var clock = new Clock();
        IssuedDeviceCredential old;
        IssuedDeviceCredential replacement;
        using (var host = new NativeAuthority(Store, clock, create: true))
        {
            old = host.Issue();
            replacement = host.Credentials.Rotate(old.CredentialId, TimeSpan.FromSeconds(3));
            host.Clean();
        }
        for (var i = 0; i < 3; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1), utc: false);
            using var next = new NativeAuthority(Store, clock);
            if (i < 2)
                next.Credentials.Authenticate(next.Signed(old));
            else
                Code("auth.invalid", () => next.Credentials.Authenticate(next.Signed(old)));
            next.Credentials.Authenticate(next.Signed(replacement));
            next.Clean();
        }
        clock.Advance(TimeSpan.FromDays(3650));
        using var rebooted = new NativeAuthority(Store, clock, boot: Guid.NewGuid());
        rebooted.Credentials.Authenticate(rebooted.Signed(replacement));
        Assert.IsType<PairedDeviceLifetime>(Assert.Single(rebooted.Credentials.ListRegistrations()).Lifetime);
    }

    [Theory]
    [InlineData((int)StoreStep.PendingFlushed, "revoke")]
    [InlineData((int)StoreStep.Replaced, "revoke")]
    [InlineData((int)StoreStep.CheckpointCommitted, "revoke")]
    [InlineData((int)StoreStep.PendingFlushed, "nonce")]
    [InlineData((int)StoreStep.Replaced, "nonce")]
    [InlineData((int)StoreStep.CheckpointCommitted, "nonce")]
    public void Failed_mutation_recovers_successor_without_resurrecting_authority(int stage, string operation)
    {
        var clock = new Clock();
        var armed = false;
        IssuedDeviceCredential credential;
        IssuedDeviceCredential unaffected;
        GatewaySignedRequest request;
        using (var host = new NativeAuthority(Store, clock, create: true, fault: step =>
        {
            if (armed && step == (StoreStep)stage) throw new IOException("fixture");
        }))
        {
            credential = host.Issue();
            unaffected = host.Issue("other-device");
            request = host.Signed(credential);
            armed = true;
            Code("auth.storage", () =>
            {
                if (operation == "revoke") host.Credentials.RevokeCredential(credential.CredentialId);
                else host.Credentials.Authenticate(request);
            });
            Code("auth.closed", () => host.Credentials.ListRegistrations());
        }
        using var next = new NativeAuthority(Store, clock);
        Code(operation == "revoke" ? "auth.invalid" : "auth.replay",
            () => next.Credentials.Authenticate(request));
        next.Credentials.Authenticate(next.Signed(unaffected));
        Assert.False(File.Exists(Path.Combine(Store, "pending.bin")));
        next.Clean();
    }

    [Theory]
    [InlineData("clock")]
    [InlineData("cancel")]
    public void Authentication_rechecks_after_commit_without_deleting_pairing(string kind)
    {
        var clock = new Clock();
        using var cancellation = new CancellationTokenSource();
        var armed = false;
        using var host = new NativeAuthority(Store, clock, create: true, fault: step =>
        {
            if (!armed || step != StoreStep.CheckpointCommitted) return;
            if (kind == "clock") clock.Advance(TimeSpan.FromMinutes(3));
            else cancellation.Cancel();
        });
        var credential = host.Issue();
        var request = host.Signed(credential, cancellation.Token);
        armed = true;
        if (kind == "clock") Code("auth.clock", () => host.Credentials.Authenticate(request));
        else Assert.Throws<OperationCanceledException>(() => host.Credentials.Authenticate(request));
        armed = false;
        Assert.Single(host.Credentials.ListRegistrations());
        if (kind == "cancel")
            Code("auth.replay", () => host.Credentials.Authenticate(request with { CancellationToken = default }));
        host.Clean();
    }

    [Fact]
    public void Clock_error_blocks_access_but_correcting_clock_restores_same_pairing()
    {
        var clock = new Clock();
        IssuedDeviceCredential credential;
        using (var host = new NativeAuthority(Store, clock, create: true))
        {
            credential = host.Issue();
            clock.Advance(TimeSpan.FromSeconds(10));
            host.Credentials.Authenticate(host.Signed(credential));
            clock.Utc -= TimeSpan.FromSeconds(5);
            Code("auth.clock_invalid", () => host.Credentials.ListRegistrations());
        }
        var bytes = File.ReadAllBytes(Path.Combine(Store, "authority.bin"));
        Assert.Equal(GatewayPersistenceFailure.ClockUnavailable,
            Assert.Throws<GatewayPersistenceException>(() => new NativeAuthority(Store, clock)).Failure);
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(Store, "authority.bin")));
        clock.Advance(TimeSpan.FromSeconds(5));
        using var next = new NativeAuthority(Store, clock);
        next.Credentials.Authenticate(next.Signed(credential));
        Assert.Single(next.Credentials.ListRegistrations());
        next.Clean();
    }

    [Fact]
    public async Task Duplicate_nonce_concurrency_has_one_durable_winner()
    {
        using var host = new NativeAuthority(Store, new Clock(), create: true);
        var request = host.Signed(host.Issue());
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
        {
            try { host.Credentials.Authenticate(request); return "accepted"; }
            catch (GatewayProtocolException error) { return error.Failure.Code; }
        })));
        Assert.Single(outcomes, result => result == "accepted");
        Assert.Equal(15, outcomes.Count(result => result == "auth.replay"));
    }

    [Fact]
    public void Pairing_invitation_still_expires_and_never_survives_restart()
    {
        var clock = new Clock();
        GatewayPairingProof proof;
        using (var host = new NativeAuthority(Store, clock, create: true))
        {
            var card = host.Pairing.OpenWindow(new()
            {
                DeviceId = "device", DisplayName = "Device", Roles = [GatewayRole.Voice]
            });
            proof = new()
            {
                ProtocolVersion = GatewayProtocolVersion.Current, PairingId = card.PairingId,
                PairingToken = card.Token.Reveal(), HostId = card.HostId,
                SpkiFingerprint = card.SpkiFingerprint, DeviceId = "device"
            };
            clock.Advance(TimeSpan.FromMinutes(6), utc: false);
            Code("pairing.expired", () => host.Pairing.Exchange(proof));
        }
        using var next = new NativeAuthority(Store, clock);
        Code("pairing.closed", () => next.Pairing.Exchange(proof));
    }

    [Fact]
    public void Cancellation_after_commit_does_not_undo_revocation_or_unpair_other_devices()
    {
        var clock = new Clock();
        using var cancellation = new CancellationTokenSource();
        var armed = false;
        using (var host = new NativeAuthority(Store, clock, create: true, fault: step =>
        {
            if (armed && step == StoreStep.PendingFlushed) cancellation.Cancel();
        }))
        {
            var credential = host.Issue();
            host.Issue("other");
            Assert.Throws<OperationCanceledException>(() =>
                host.Credentials.RevokeCredential(credential.CredentialId, new(true)));
            armed = true;
            Assert.Throws<OperationCanceledException>(() =>
                host.Credentials.RevokeCredential(credential.CredentialId, cancellation.Token));
        }
        using var next = new NativeAuthority(Store, clock);
        Assert.Equal("other", Assert.Single(next.Credentials.ListRegistrations()).DeviceId);
    }
}
