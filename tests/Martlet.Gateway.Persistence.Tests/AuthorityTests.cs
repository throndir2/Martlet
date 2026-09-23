using Martlet.Gateway;
using Martlet.Gateway.Persistence;

namespace Martlet.Gateway.Persistence.Tests;

public sealed class AuthorityTests : NativeTest
{
    [Fact]
    public async Task Concurrent_duplicate_nonce_has_one_durable_winner()
    {
        using var host = new NativeAuthority(Store, new Clock(), create: true);
        var request = host.Signed(host.Issue());
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
        {
            try
            {
                host.Credentials.Authenticate(request);
                return "accepted";
            }
            catch (GatewayProtocolException error) { return error.Failure.Code; }
        })));
        Assert.Single(outcomes, value => value == "accepted");
        Assert.Equal(15, outcomes.Count(value => value == "auth.replay"));
        host.Clean();
    }

    [Theory]
    [InlineData("rollback")]
    [InlineData("cancel")]
    public void Final_commit_rechecks_before_removing_running_fence(string kind)
    {
        var clock = new Clock();
        using var cancellation = new CancellationTokenSource();
        var armed = false;
        using (var host = new NativeAuthority(Store, clock, create: true, fault: step =>
        {
            if (!armed || step != StoreStep.BeforeCleanFenceRemoval) return;
            if (kind == "rollback") clock.Utc -= TimeSpan.FromSeconds(1);
            if (kind == "cancel") cancellation.Cancel();
        }))
        {
            host.Issue();
            armed = true;
            if (kind == "cancel")
                Assert.Throws<OperationCanceledException>(() => host.Credentials.Complete(cancellation.Token));
            else
                Code("auth.clock_invalid", () => host.Credentials.Complete());
            Assert.True(File.Exists(Path.Combine(Store, "running")));
        }
    }

    [Theory]
    [InlineData((int)StoreStep.CheckpointCommitted)]
    [InlineData((int)StoreStep.CleanFenceRemoved)]
    public void Monotonic_time_spent_on_final_write_or_fence_removal_is_not_refunded_by_restart(int delayedStep)
    {
        var clock = new Clock();
        var armed = false;
        using (var host = new NativeAuthority(Store, clock, create: true, fault: step =>
        {
            if (armed && step == (StoreStep)delayedStep)
                clock.Advance(TimeSpan.FromSeconds(60), utc: false);
        }))
        {
            host.Issue();
            clock.Advance(TimeSpan.FromDays(90) - TimeSpan.FromSeconds(40), utc: false);
            armed = true;
            host.Clean();
        }
        using var reopened = new NativeAuthority(Store, clock);
        Assert.Empty(reopened.Credentials.ListRegistrations());
        reopened.Clean();
    }

    [Fact]
    public void Repeated_clean_restarts_do_not_refund_short_overlap_under_stalled_utc()
    {
        var clock = new Clock();
        IssuedDeviceCredential original;
        using (var host = new NativeAuthority(Store, clock, create: true))
        {
            original = host.Issue();
            host.Credentials.Rotate(original.CredentialId, TimeSpan.FromSeconds(3));
            host.Clean();
        }
        for (var attempt = 0; attempt < 3; attempt++)
        {
            clock.Advance(TimeSpan.FromSeconds(1), utc: false);
            using var next = new NativeAuthority(Store, clock);
            if (attempt < 2)
                next.Credentials.Authenticate(next.Signed(original));
            else
                Code("auth.invalid", () => next.Credentials.Authenticate(next.Signed(original)));
            next.Clean();
        }
    }

    [Fact]
    public void Pairing_expiring_during_commit_never_returns_a_secret_or_reusable_approval()
    {
        var clock = new Clock();
        var armed = false;
        using var host = new NativeAuthority(Store, clock, create: true, fault: step =>
        {
            if (armed && step == StoreStep.CheckpointCommitted)
                clock.Advance(TimeSpan.FromMinutes(6));
        });
        var card = host.Pairing.OpenWindow(new()
        {
            DeviceId = "device", DisplayName = "Device", Roles = [GatewayRole.Voice]
        });
        var proof = new GatewayPairingProof
        {
            ProtocolVersion = GatewayProtocolVersion.Current, PairingId = card.PairingId,
            PairingToken = card.Token.Reveal(), HostId = card.HostId,
            SpkiFingerprint = card.SpkiFingerprint, DeviceId = "device"
        };
        armed = true;
        Code("pairing.expired", () => host.Pairing.Exchange(proof));
        Code("pairing.closed", () => host.Pairing.Exchange(proof));
        armed = false;
        host.Clean();
    }

    [Fact]
    public void Clean_restart_preserves_identity_credential_and_live_replay_history()
    {
        var clock = new Clock();
        IssuedDeviceCredential credential;
        GatewaySignedRequest accepted;
        GatewayHostIdentity identity;
        using (var host = new NativeAuthority(Store, clock, create: true))
        {
            identity = host.Identity;
            credential = host.Issue();
            accepted = host.Signed(credential);
            host.Credentials.Authenticate(accepted);
            host.Clean();
        }
        using var reopened = new NativeAuthority(Store, clock);
        Assert.Equal(identity, reopened.Identity);
        Code("auth.replay", () => reopened.Credentials.Authenticate(accepted));
        Assert.Equal(credential.CredentialId, reopened.Credentials.Authenticate(reopened.Signed(credential)).CredentialId);
        reopened.Clean();
    }

    [Fact]
    public void Pending_pairing_is_never_restored_and_capacity_refusal_does_not_consume()
    {
        var clock = new Clock();
        GatewayPairingProof proof;
        using (var host = new NativeAuthority(Store, clock, create: true))
        {
            var card = host.Pairing.OpenWindow(new()
            {
                DeviceId = "device", DisplayName = "Device", Roles = [GatewayRole.Memory]
            });
            proof = new()
            {
                ProtocolVersion = GatewayProtocolVersion.Current, PairingId = card.PairingId,
                PairingToken = card.Token.Reveal(), HostId = card.HostId,
                SpkiFingerprint = card.SpkiFingerprint, DeviceId = "device"
            };
            for (var i = 0; i < GatewayCredentialStore.MaximumRegistrations; i++)
                host.Issue();
            Code("auth.capacity", () => host.Pairing.Exchange(proof));
            host.Credentials.RevokeDevice("fixture-device");
            var issued = host.Pairing.Exchange(proof);
            Assert.Equal(GatewayRole.Memory, Assert.Single(issued.Roles));
            Code("pairing.closed", () => host.Pairing.Exchange(proof));
            host.Clean();
        }
        using var next = new NativeAuthority(Store, clock);
        Code("pairing.closed", () => next.Pairing.Exchange(proof));
        next.Clean();
    }

    [Fact]
    public void Revocation_and_rotation_overlap_are_committed_together()
    {
        var clock = new Clock();
        IssuedDeviceCredential old;
        IssuedDeviceCredential replacement;
        using (var host = new NativeAuthority(Store, clock, create: true))
        {
            old = host.Issue();
            replacement = host.Credentials.Rotate(old.CredentialId, TimeSpan.FromMinutes(1));
            host.Clean();
        }
        using (var host = new NativeAuthority(Store, clock))
        {
            host.Credentials.Authenticate(host.Signed(old));
            host.Credentials.Authenticate(host.Signed(replacement));
            Assert.Equal(replacement.CredentialId,
                host.Credentials.ListRegistrations().Single(r => r.CredentialId == old.CredentialId).RotatedToCredentialId);
            clock.Advance(TimeSpan.FromMinutes(1));
            Code("auth.expired", () => host.Credentials.Authenticate(host.Signed(old)));
            Assert.True(host.Credentials.RevokeCredential(replacement.CredentialId));
            host.Clean();
        }
        using var reopened = new NativeAuthority(Store, clock);
        Assert.Empty(reopened.Credentials.ListRegistrations());
        Code("auth.invalid", () => reopened.Credentials.Authenticate(reopened.Signed(replacement)));
        reopened.Clean();
    }

    [Fact]
    public void Replay_saturation_survives_restart_and_monotonic_time_never_shortens_retention()
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
            Code("auth.rate", () => host.Credentials.Authenticate(host.Signed(credential)));
            clock.Advance(TimeSpan.FromMinutes(5), utc: false);
            Code("auth.replay", () => host.Credentials.Authenticate(first));
            Code("auth.rate", () => host.Credentials.Authenticate(host.Signed(credential)));
            host.Clean();
        }
        using var reopened = new NativeAuthority(Store, clock);
        Code("auth.replay", () => reopened.Credentials.Authenticate(first));
        Code("auth.rate", () => reopened.Credentials.Authenticate(reopened.Signed(credential)));
        clock.Advance(TimeSpan.FromMinutes(5));
        reopened.Credentials.Authenticate(reopened.Signed(credential));
        reopened.Clean();
    }

    [Fact]
    public void Saved_monotonic_budget_and_offline_time_do_not_renew_credentials()
    {
        var clock = new Clock();
        using (var host = new NativeAuthority(Store, clock, create: true))
        {
            host.Issue();
            clock.Advance(TimeSpan.FromDays(89), utc: false);
            host.Clean();
        }
        clock.Advance(TimeSpan.FromDays(1));
        using var reopened = new NativeAuthority(Store, clock);
        Assert.Empty(reopened.Credentials.ListRegistrations());
        reopened.Clean();
    }

    [Theory]
    [InlineData("utc")]
    [InlineData("monotonic")]
    [InlineData("frequency")]
    public void Clock_failure_prevents_clean_close_and_credential_revival(string kind)
    {
        var clock = new Clock();
        using (var host = new NativeAuthority(Store, clock, create: true))
        {
            host.Issue();
            if (kind == "utc") clock.Utc -= TimeSpan.FromSeconds(1);
            if (kind == "monotonic") clock.Ticks--;
            if (kind == "frequency") clock.Frequency++;
            Code("auth.clock_invalid", () => host.Credentials.ListRegistrations());
            Code("auth.clock_invalid", host.Clean);
        }
        Assert.Equal(GatewayPersistenceFailure.RecoveryRequired,
            Assert.Throws<GatewayPersistenceException>(() => new NativeAuthority(Store, new Clock())).Failure);
    }

    [Theory]
    [InlineData((int)StoreStep.PendingFlushed)]
    [InlineData((int)StoreStep.Replaced)]
    [InlineData((int)StoreStep.CheckpointCommitted)]
    public void Failed_mutation_poison_closes_all_authority_and_reset_never_revives_devices(int step)
    {
        var clock = new Clock();
        var armed = false;
        using (var host = new NativeAuthority(Store, clock, create: true, fault: value =>
        {
            if (armed && value == (StoreStep)step) throw new IOException("fixture only");
        }))
        {
            var credential = host.Issue();
            armed = true;
            Code("auth.storage", () => host.Credentials.RevokeCredential(credential.CredentialId));
            Code("auth.closed", () => host.Credentials.Authenticate(host.Signed(credential)));
            Code("auth.closed", host.Clean);
        }
        Assert.Equal(GatewayPersistenceFailure.RecoveryRequired,
            Assert.Throws<GatewayPersistenceException>(() => new NativeAuthority(Store, clock)).Failure);
        using var recovered = new NativeAuthority(Store, clock, reset: true);
        Assert.Empty(recovered.Credentials.ListRegistrations());
        recovered.Clean();
    }

    [Theory]
    [InlineData("clock")]
    [InlineData("cancel")]
    [InlineData("expiry")]
    public void Authentication_rechecks_after_durable_io_before_returning_principal(string kind)
    {
        var clock = new Clock();
        using var cancellation = new CancellationTokenSource();
        var armed = false;
        using var host = new NativeAuthority(Store, clock, create: true, fault: step =>
        {
            if (!armed || step != StoreStep.CheckpointCommitted) return;
            if (kind == "clock") clock.Advance(TimeSpan.FromMinutes(3));
            if (kind == "expiry") clock.Advance(TimeSpan.FromDays(91));
            if (kind == "cancel") cancellation.Cancel();
        });
        var credential = host.Issue();
        var request = host.Signed(credential, cancellation.Token);
        armed = true;
        if (kind == "cancel")
            Assert.Throws<OperationCanceledException>(() => host.Credentials.Authenticate(request));
        else
            Code(kind == "clock" ? "auth.clock" : "auth.expired", () => host.Credentials.Authenticate(request));
        armed = false;
        if (kind == "cancel")
            Code("auth.replay", () => host.Credentials.Authenticate(request with { CancellationToken = default }));
        host.Clean();
    }

    [Fact]
    public void Cancellation_after_commit_cannot_undo_revocation()
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
            Assert.Throws<OperationCanceledException>(() =>
                host.Credentials.RevokeCredential(credential.CredentialId, new(true)));
            Assert.Single(host.Credentials.ListRegistrations());
            armed = true;
            Assert.Throws<OperationCanceledException>(() =>
                host.Credentials.RevokeCredential(credential.CredentialId, cancellation.Token));
            Assert.Empty(host.Credentials.ListRegistrations());
            host.Clean();
        }
        using var next = new NativeAuthority(Store, clock);
        Assert.Empty(next.Credentials.ListRegistrations());
        next.Clean();
    }
}
