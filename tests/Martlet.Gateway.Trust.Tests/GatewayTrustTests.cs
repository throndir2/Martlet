using System.Security.Cryptography;
using System.Text.Json;
using Martlet.Gateway.Trust;

namespace Martlet.Gateway.Trust.Tests;

public sealed class GatewayTrustTests
{
    private static readonly GatewayHostIdentity Host = new(Guid.NewGuid(), new string('A', 64));

    [Fact]
    public void DeviceSurfaceCannotApproveOrListAndNoApprovalMeansNoAuthority()
    {
        using var owner = new GatewayHostAuthority(Host);
        using var arbitrary = GatewaySecret.Import(new byte[32]);
        var id = Guid.NewGuid();
        Reject(GatewayTrustFailure.PairingRejected, () =>
            owner.Devices.Redeem(Host, id, GatewayScope.Generation, Guid.NewGuid(), arbitrary));
        Reject(GatewayTrustFailure.CredentialRejected, () =>
            owner.Devices.Authorize(Host, id, arbitrary, GatewayScope.Generation));
        Assert.Empty(owner.ListDevices());
        Assert.Empty(typeof(GatewayDeviceAccess).GetConstructors());
        Assert.Equal(["Authorize", "Redeem", "ToString"],
            typeof(GatewayDeviceAccess).GetMethods().Where(m => m.DeclaringType == typeof(GatewayDeviceAccess))
                .Select(m => m.Name).Order().ToArray());
    }

    [Fact]
    public void ExplicitApprovalBindsExactHostDeviceAndScopesAndIsOneUse()
    {
        using var owner = new GatewayHostAuthority(Host);
        var offer = owner.ApprovePairing(Guid.NewGuid(), GatewayScope.Generation | GatewayScope.Status);
        using var token = offer.Token;
        var wrongId = new GatewayHostIdentity(Guid.NewGuid(), Host.SpkiSha256);
        var wrongPin = new GatewayHostIdentity(Host.HostId, new string('B', 64));
        foreach (var wrongHost in new[] { wrongId, wrongPin })
            Reject(GatewayTrustFailure.HostMismatch, () => Redeem(owner, offer, host: wrongHost));
        Reject(GatewayTrustFailure.PairingRejected, () => owner.Devices.Redeem(
            Host, Guid.NewGuid(), offer.Scopes, offer.ApprovalId, token));
        Reject(GatewayTrustFailure.PairingRejected, () => owner.Devices.Redeem(
            Host, offer.DeviceId, GatewayScope.Generation, offer.ApprovalId, token));
        using var wrongSecret = GatewaySecret.Import(new byte[32]);
        Reject(GatewayTrustFailure.PairingRejected, () => owner.Devices.Redeem(
            Host, offer.DeviceId, offer.Scopes, offer.ApprovalId, wrongSecret));
        var credential = Redeem(owner, offer);
        using var secret = credential.Secret;
        Assert.Equal(offer.DeviceId, credential.DeviceId);
        Assert.Equal(offer.Scopes, credential.Scopes);
        Assert.Single(owner.ListDevices());
        Reject(GatewayTrustFailure.PairingRejected, () => Redeem(owner, offer));
        foreach (var wrongHost in new[] { wrongId, wrongPin })
            Reject(GatewayTrustFailure.HostMismatch, () => Authorize(owner, credential, host: wrongHost));
        Reject(GatewayTrustFailure.CredentialRejected, () => owner.Devices.Authorize(
            Host, Guid.NewGuid(), secret, GatewayScope.Generation));
        Reject(GatewayTrustFailure.CredentialRejected, () => owner.Devices.Authorize(
            Host, credential.DeviceId, wrongSecret, GatewayScope.Generation));
        Reject(GatewayTrustFailure.ScopeDenied, () => Authorize(owner, credential, GatewayScope.Synthesis));
        Reject(GatewayTrustFailure.ScopeDenied, () => Authorize(owner, credential,
            GatewayScope.Generation | GatewayScope.MemoryRead));
        var decision = Authorize(owner, credential);
        Assert.Equal(GatewayScope.Generation, decision.Scopes);
        Assert.Equal(Host, decision.Host);
        Assert.Equal(GatewayScope.Status, Authorize(owner, credential, GatewayScope.Status).Scopes);
    }

    [Theory]
    [InlineData(GatewayScope.None)]
    [InlineData((GatewayScope)128)]
    [InlineData((GatewayScope)(-1))]
    public void InvalidScopesNeverGrantAccess(GatewayScope scopes)
    {
        using var owner = new GatewayHostAuthority(Host);
        Reject(GatewayTrustFailure.ScopeDenied, () => owner.ApprovePairing(Guid.NewGuid(), scopes));
        var credential = Pair(owner);
        using var secret = credential.Secret;
        Reject(GatewayTrustFailure.ScopeDenied, () => Authorize(owner, credential, scopes));
    }

    [Theory]
    [InlineData(GatewayScope.Status)]
    [InlineData(GatewayScope.Transcription)]
    [InlineData(GatewayScope.Generation)]
    [InlineData(GatewayScope.Synthesis)]
    [InlineData(GatewayScope.Perception)]
    [InlineData(GatewayScope.MemoryRead)]
    [InlineData(GatewayScope.MemoryWrite)]
    public void EveryScopeIsIndependent(GatewayScope scope)
    {
        using var owner = new GatewayHostAuthority(Host);
        var offer = owner.ApprovePairing(Guid.NewGuid(), scope);
        using var token = offer.Token;
        var credential = Redeem(owner, offer);
        using var secret = credential.Secret;
        Assert.Equal(scope, Authorize(owner, credential, scope).Scopes);
        foreach (var other in Enum.GetValues<GatewayScope>().Where(s => s != scope && s != GatewayScope.None))
            Reject(GatewayTrustFailure.ScopeDenied, () => Authorize(owner, credential, other));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PairingExpiresAtFiveMinutesOnEitherClock(bool utc)
    {
        var clock = new TestClock();
        using var owner = new GatewayHostAuthority(Host, clock);
        var offer = owner.ApprovePairing(Guid.NewGuid(), GatewayScope.Generation);
        using var token = offer.Token;
        Assert.Equal(clock.Utc + TimeSpan.FromMinutes(5), offer.ExpiresAt);
        clock.Advance(TimeSpan.FromMinutes(5), utc, !utc);
        Reject(GatewayTrustFailure.PairingRejected, () => Redeem(owner, offer));
        Assert.Empty(owner.ListDevices());
    }

    [Fact]
    public void PairingRemainsValidImmediatelyBeforeExpiry()
    {
        var clock = new TestClock();
        using var owner = new GatewayHostAuthority(Host, clock);
        var offer = owner.ApprovePairing(Guid.NewGuid(), GatewayScope.Generation);
        using var token = offer.Token;
        clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromTicks(1));
        using var secret = Redeem(owner, offer).Secret;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CredentialsExpireAtNinetyDaysOnEitherClock(bool utc)
    {
        var clock = new TestClock();
        using var owner = new GatewayHostAuthority(Host, clock);
        var credential = Pair(owner);
        using var secret = credential.Secret;
        Assert.Equal(clock.Utc + TimeSpan.FromDays(90), credential.ExpiresAt);
        clock.Advance(TimeSpan.FromDays(90) - TimeSpan.FromTicks(1), utc, !utc);
        Authorize(owner, credential);
        clock.Advance(TimeSpan.FromTicks(1), utc, !utc);
        Reject(GatewayTrustFailure.CredentialRejected, () => Authorize(owner, credential));
        Reject(GatewayTrustFailure.CredentialRejected, () => owner.ApproveRotation(credential.DeviceId));
        Assert.Empty(owner.ListDevices());
    }

    [Fact]
    public async Task ConcurrentRedemptionHasExactlyOneWinner()
    {
        using var owner = new GatewayHostAuthority(Host);
        var offer = owner.ApprovePairing(Guid.NewGuid(), GatewayScope.Generation);
        using var token = offer.Token;
        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
        {
            try
            {
                using var credential = Redeem(owner, offer).Secret;
                return true;
            }
            catch (GatewayTrustException error)
            {
                Assert.Equal(GatewayTrustFailure.PairingRejected, error.Failure);
                return false;
            }
        })));
        Assert.Equal(1, results.Count(success => success));
        Assert.Single(owner.ListDevices());
    }

    [Fact]
    public void RotationNeedsNewHostApprovalAndHasBoundedOverlap()
    {
        var clock = new TestClock();
        using var owner = new GatewayHostAuthority(Host, clock);
        var old = Pair(owner);
        using var oldSecret = old.Secret;
        clock.Advance(TimeSpan.FromDays(30));
        var offer = owner.ApproveRotation(old.DeviceId);
        using var token = offer.Token;
        Reject(GatewayTrustFailure.DeviceConflict, () => owner.ApproveRotation(old.DeviceId));
        Authorize(owner, old);
        var renewed = Redeem(owner, offer);
        using var newSecret = renewed.Secret;
        Assert.Equal(old.DeviceId, renewed.DeviceId);
        Assert.Equal(old.Scopes, renewed.Scopes);
        Assert.Equal(clock.Utc + TimeSpan.FromDays(90), renewed.ExpiresAt);
        Assert.NotEqual(Bytes(oldSecret), Bytes(newSecret));
        Reject(GatewayTrustFailure.PairingRejected, () => Redeem(owner, offer));
        Reject(GatewayTrustFailure.DeviceConflict, () => owner.ApproveRotation(old.DeviceId));
        clock.Advance(TimeSpan.FromMinutes(2) - TimeSpan.FromTicks(1));
        Authorize(owner, old);
        Authorize(owner, renewed);
        clock.Advance(TimeSpan.FromTicks(1));
        Reject(GatewayTrustFailure.CredentialRejected, () => Authorize(owner, old));
        Authorize(owner, renewed);
        using var nextToken = owner.ApproveRotation(old.DeviceId).Token;
    }

    [Fact]
    public void RotationNeverExtendsOldExpiry()
    {
        var clock = new TestClock();
        using var owner = new GatewayHostAuthority(Host, clock);
        var old = Pair(owner);
        using var oldSecret = old.Secret;
        clock.Advance(TimeSpan.FromDays(90) - TimeSpan.FromSeconds(1));
        var offer = owner.ApproveRotation(old.DeviceId);
        using var token = offer.Token;
        var renewed = Redeem(owner, offer);
        using var newSecret = renewed.Secret;
        clock.Advance(TimeSpan.FromSeconds(1));
        Reject(GatewayTrustFailure.CredentialRejected, () => Authorize(owner, old));
        Authorize(owner, renewed);
    }

    [Fact]
    public void ExpiredDeviceAlsoInvalidatesItsOutstandingRotation()
    {
        var clock = new TestClock();
        using var owner = new GatewayHostAuthority(Host, clock);
        var old = Pair(owner);
        using var secret = old.Secret;
        clock.Advance(TimeSpan.FromDays(90) - TimeSpan.FromSeconds(1));
        var offer = owner.ApproveRotation(old.DeviceId);
        using var token = offer.Token;
        clock.Advance(TimeSpan.FromSeconds(1));
        Reject(GatewayTrustFailure.PairingRejected, () => Redeem(owner, offer));
    }

    [Fact]
    public void RevocationRemovesBothGenerationsAndPendingApprovals()
    {
        var clock = new TestClock();
        using var owner = new GatewayHostAuthority(Host, clock);
        var old = Pair(owner);
        using var oldSecret = old.Secret;
        var offer = owner.ApproveRotation(old.DeviceId);
        using var token = offer.Token;
        owner.RevokeDevice(old.DeviceId);
        Reject(GatewayTrustFailure.PairingRejected, () => Redeem(owner, offer));
        Reject(GatewayTrustFailure.CredentialRejected, () => Authorize(owner, old));
        var replacement = Pair(owner, old.DeviceId);
        using var replacementSecret = replacement.Secret;
        Reject(GatewayTrustFailure.CredentialRejected, () => Authorize(owner, old));
        var rotation = owner.ApproveRotation(old.DeviceId);
        using var rotationToken = rotation.Token;
        var renewed = Redeem(owner, rotation);
        using var renewedSecret = renewed.Secret;
        owner.RevokeDevice(old.DeviceId);
        Reject(GatewayTrustFailure.CredentialRejected, () => Authorize(owner, replacement));
        Reject(GatewayTrustFailure.CredentialRejected, () => Authorize(owner, renewed));
        Assert.Empty(owner.ListDevices());
    }

    [Fact]
    public void CancelApprovalDoesNotRevokeDevice()
    {
        using var owner = new GatewayHostAuthority(Host);
        var old = Pair(owner);
        using var secret = old.Secret;
        var offer = owner.ApproveRotation(old.DeviceId);
        using var token = offer.Token;
        owner.CancelApproval(offer.ApprovalId);
        Reject(GatewayTrustFailure.PairingRejected, () => Redeem(owner, offer));
        Authorize(owner, old);
        var pairing = owner.ApprovePairing(Guid.NewGuid(), GatewayScope.MemoryRead);
        using var pairingToken = pairing.Token;
        owner.CancelApproval(pairing.ApprovalId);
        Reject(GatewayTrustFailure.PairingRejected, () => Redeem(owner, pairing));
    }

    [Fact]
    public void RestartAndDisposeLoseAuthorityEvenWithSameIdentityAndCopiedSecret()
    {
        var owner = new GatewayHostAuthority(Host);
        var credential = Pair(owner);
        using var secret = credential.Secret;
        using var copy = GatewaySecret.Import(Bytes(secret));
        using var restarted = new GatewayHostAuthority(Host);
        Reject(GatewayTrustFailure.CredentialRejected, () =>
            restarted.Devices.Authorize(Host, credential.DeviceId, copy, GatewayScope.Generation));
        var pending = owner.ApprovePairing(Guid.NewGuid(), GatewayScope.Generation);
        using var token = pending.Token;
        Reject(GatewayTrustFailure.PairingRejected, () => Redeem(restarted, pending));
        owner.Dispose();
        owner.Dispose();
        Reject(GatewayTrustFailure.AuthorityClosed, () => Authorize(owner, credential));
        Reject(GatewayTrustFailure.AuthorityClosed, () => owner.ListDevices());
        Reject(GatewayTrustFailure.AuthorityClosed, () => Redeem(owner, pending));
        Reject(GatewayTrustFailure.AuthorityClosed, () => owner.ApprovePairing(Guid.NewGuid(), GatewayScope.Status));
    }

    [Fact]
    public void PendingApprovalsAreBoundedDeduplicatedAndReclaimed()
    {
        var clock = new TestClock();
        using var owner = new GatewayHostAuthority(Host, clock);
        var id = Guid.NewGuid();
        using var first = owner.ApprovePairing(id, GatewayScope.Generation).Token;
        Reject(GatewayTrustFailure.DeviceConflict, () => owner.ApprovePairing(id, GatewayScope.Generation));
        for (var i = 1; i < GatewayTrustLimits.MaximumPendingApprovals; i++)
        {
            using var token = owner.ApprovePairing(Guid.NewGuid(), GatewayScope.Status).Token;
        }
        Reject(GatewayTrustFailure.ResourceLimit, () => owner.ApprovePairing(Guid.NewGuid(), GatewayScope.Generation));
        clock.Advance(TimeSpan.FromMinutes(5));
        using var recovered = owner.ApprovePairing(Guid.NewGuid(), GatewayScope.Generation).Token;
    }

    [Fact]
    public void DeviceSlotsIncludePendingReservationsAndCanBeReclaimed()
    {
        var clock = new TestClock();
        using var owner = new GatewayHostAuthority(Host, clock);
        Guid firstId = default;
        for (var i = 0; i < GatewayTrustLimits.MaximumDevices - 1; i++)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            var credential = Pair(owner);
            using var secret = credential.Secret;
            if (i == 0)
                firstId = credential.DeviceId;
        }
        var lastOffer = owner.ApprovePairing(Guid.NewGuid(), GatewayScope.Generation);
        using var token = lastOffer.Token;
        Reject(GatewayTrustFailure.ResourceLimit, () => owner.ApprovePairing(Guid.NewGuid(), GatewayScope.Status));
        using var lastSecret = Redeem(owner, lastOffer).Secret;
        Assert.Equal(128, owner.ListDevices().Count);
        Reject(GatewayTrustFailure.ResourceLimit, () => owner.ApprovePairing(Guid.NewGuid(), GatewayScope.Status));
        using var rotationToken = owner.ApproveRotation(firstId).Token;
        owner.RevokeDevice(firstId);
        using var freed = owner.ApprovePairing(Guid.NewGuid(), GatewayScope.Status).Token;
        clock.Advance(TimeSpan.FromDays(90));
        Assert.Empty(owner.ListDevices());
        using var expiredSlots = owner.ApprovePairing(Guid.NewGuid(), GatewayScope.Status).Token;
    }

    [Fact]
    public void RedemptionRateLimitIncludesUnknownTicketsAndHostMismatchesAndResets()
    {
        var clock = new TestClock();
        using var owner = new GatewayHostAuthority(Host, clock);
        var offer = owner.ApprovePairing(Guid.NewGuid(), GatewayScope.Generation);
        using var token = offer.Token;
        var wrongHost = new GatewayHostIdentity(Guid.NewGuid(), Host.SpkiSha256);
        for (var i = 0; i < GatewayTrustLimits.MaximumRedemptionsPerWindow; i++)
            Reject(GatewayTrustFailure.HostMismatch, () => Redeem(owner, offer, host: wrongHost));
        Reject(GatewayTrustFailure.RateLimited, () => Redeem(owner, offer));
        clock.Advance(TimeSpan.FromMinutes(1));
        using var secret = Redeem(owner, offer).Secret;
        for (var i = 1; i < GatewayTrustLimits.MaximumRedemptionsPerWindow; i++)
            Reject(GatewayTrustFailure.PairingRejected, () => owner.Devices.Redeem(
                Host, offer.DeviceId, offer.Scopes, Guid.NewGuid(), token));
        Reject(GatewayTrustFailure.RateLimited, () => Redeem(owner, offer));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ClockRollbackPermanentlyClosesAuthority(bool utc)
    {
        var clock = new TestClock();
        using var owner = new GatewayHostAuthority(Host, clock);
        var credential = Pair(owner);
        using var secret = credential.Secret;
        var offer = owner.ApproveRotation(credential.DeviceId);
        using var token = offer.Token;
        clock.Advance(TimeSpan.FromTicks(-1), utc, !utc);
        Reject(GatewayTrustFailure.ClockInvalid, () => Authorize(owner, credential));
        clock.Advance(TimeSpan.FromDays(1));
        Reject(GatewayTrustFailure.ClockInvalid, () => Redeem(owner, offer));
        Reject(GatewayTrustFailure.ClockInvalid, () => owner.ApprovePairing(Guid.NewGuid(), GatewayScope.Status));
    }

    [Fact]
    public void InvalidClockRangeAndChangedFrequencyFailClosed()
    {
        var clock = new TestClock();
        using var owner = new GatewayHostAuthority(Host, clock);
        var credential = Pair(owner);
        using var secret = credential.Secret;
        clock.Frequency++;
        Reject(GatewayTrustFailure.ClockInvalid, () => Authorize(owner, credential));
        var farFuture = new TestClock { Utc = DateTimeOffset.MaxValue };
        using var other = new GatewayHostAuthority(Host, farFuture);
        Reject(GatewayTrustFailure.ClockInvalid, () => other.ApprovePairing(Guid.NewGuid(), GatewayScope.Status));
        farFuture.Utc = DateTimeOffset.UtcNow;
        Reject(GatewayTrustFailure.ClockInvalid, () => other.ListDevices());
        Assert.Throws<ArgumentException>(() => new GatewayHostAuthority(Host, new TestClock { Frequency = 0 }));
    }

    [Fact]
    public void CancellationBeforeCommitPreservesApprovalAndCredential()
    {
        var clock = new TestClock();
        using var owner = new GatewayHostAuthority(Host, clock);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            owner.ApprovePairing(Guid.NewGuid(), GatewayScope.Status, canceled.Token));
        var offer = owner.ApprovePairing(Guid.NewGuid(), GatewayScope.Generation);
        using var token = offer.Token;
        Assert.Throws<OperationCanceledException>(() => Redeem(owner, offer, ct: canceled.Token));
        Assert.Throws<OperationCanceledException>(() => owner.CancelApproval(offer.ApprovalId, canceled.Token));
        using var duringClockRead = new CancellationTokenSource();
        clock.OnRead = duringClockRead.Cancel;
        Assert.Throws<OperationCanceledException>(() => Redeem(owner, offer, ct: duringClockRead.Token));
        clock.OnRead = null;
        var credential = Redeem(owner, offer);
        using var secret = credential.Secret;
        Assert.Throws<OperationCanceledException>(() => owner.ApproveRotation(credential.DeviceId, canceled.Token));
        Assert.Throws<OperationCanceledException>(() => owner.RevokeDevice(credential.DeviceId, canceled.Token));
        Assert.Throws<OperationCanceledException>(() => owner.ListDevices(canceled.Token));
        var error = Assert.Throws<OperationCanceledException>(() =>
            owner.Devices.Authorize(Host, credential.DeviceId, secret, GatewayScope.Generation, canceled.Token));
        Assert.Equal(canceled.Token, error.CancellationToken);
        Authorize(owner, credential);
        Assert.Single(owner.ListDevices());
    }

    [Fact]
    public void SecretOwnershipRedactionAndSerializationFailClosed()
    {
        var source = RandomNumberGenerator.GetBytes(32);
        using var secret = GatewaySecret.Import(source);
        var snapshot = source.ToArray();
        Array.Clear(source);
        Assert.Equal(snapshot, Bytes(secret));
        var output = Bytes(secret);
        Array.Clear(output);
        Assert.Equal(snapshot, Bytes(secret));
        Assert.Equal("[gateway secret redacted]", secret.ToString());
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(secret));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize<object>(secret));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize<GatewaySecret>("\"token\""));
        Assert.Throws<ArgumentException>(() => GatewaySecret.Import(new byte[31]));
        Assert.Throws<ArgumentException>(() => secret.CopyTo(new byte[33]));
        using var owner = new GatewayHostAuthority(Host);
        var offer = owner.ApprovePairing(Guid.NewGuid(), GatewayScope.Generation);
        using var token = offer.Token;
        Assert.Contains("[gateway secret redacted]", offer.ToString());
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(offer));
        var credential = Redeem(owner, offer);
        using var deviceSecret = credential.Secret;
        Assert.Contains("[gateway secret redacted]", credential.ToString());
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(credential));
        secret.Dispose();
        Assert.Throws<ObjectDisposedException>(() => secret.CopyTo(new byte[32]));
        Assert.Throws<ObjectDisposedException>(() => owner.Devices.Authorize(
            Host, credential.DeviceId, secret, GatewayScope.Generation));
    }

    [Fact]
    public void IdentityAndDeviceInputsAreCanonicalAndBounded()
    {
        Assert.Throws<ArgumentException>(() => new GatewayHostIdentity(Guid.Empty, Host.SpkiSha256));
        Assert.Throws<ArgumentException>(() => new GatewayHostIdentity(Guid.NewGuid(), new string('a', 64)));
        Assert.Throws<ArgumentException>(() => new GatewayHostIdentity(Guid.NewGuid(), new string('G', 64)));
        Assert.Throws<ArgumentException>(() => new GatewayHostIdentity(Guid.NewGuid(), new string('A', 65)));
        using var owner = new GatewayHostAuthority(Host);
        Assert.Throws<ArgumentException>(() => owner.ApprovePairing(Guid.Empty, GatewayScope.Status));
        Reject(GatewayTrustFailure.CredentialRejected, () => owner.ApproveRotation(Guid.NewGuid()));
        Reject(GatewayTrustFailure.CredentialRejected, () => owner.RevokeDevice(Guid.NewGuid()));
        Reject(GatewayTrustFailure.PairingRejected, () => owner.CancelApproval(Guid.NewGuid()));
    }

    [Fact]
    public void CallerModifiedMetadataAndCopiedSecretsCannotWidenOrReplayAuthority()
    {
        using var owner = new GatewayHostAuthority(Host);
        var offer = owner.ApprovePairing(Guid.NewGuid(), GatewayScope.Generation);
        using var token = offer.Token;
        using var tokenCopy = GatewaySecret.Import(Bytes(token));
        var widenedOffer = offer with { Scopes = offer.Scopes | GatewayScope.MemoryWrite };
        Reject(GatewayTrustFailure.PairingRejected, () => Redeem(owner, widenedOffer));
        var credential = Redeem(owner, offer with { Token = tokenCopy });
        using var secret = credential.Secret;
        Reject(GatewayTrustFailure.PairingRejected, () => Redeem(owner, offer));
        var widenedCredential = credential with { Scopes = credential.Scopes | GatewayScope.MemoryWrite };
        Reject(GatewayTrustFailure.ScopeDenied, () => Authorize(owner, widenedCredential, GatewayScope.MemoryWrite));
        var snapshot = owner.ListDevices();
        var widenedInfo = snapshot[0] with { Scopes = GatewayScope.MemoryWrite };
        Assert.NotEqual(widenedInfo, owner.ListDevices()[0]);
        var list = Assert.IsAssignableFrom<IList<GatewayDeviceInfo>>(snapshot);
        Assert.Throws<NotSupportedException>(() => list[0] = widenedInfo);
        owner.RevokeDevice(credential.DeviceId);
        Assert.Single(snapshot);
        Assert.Empty(owner.ListDevices());
        Reject(GatewayTrustFailure.CredentialRejected, () => Authorize(owner, credential));
    }

    [Fact]
    public async Task ConcurrentRotationRedemptionAndRevocationNeverResurrectDevice()
    {
        var clock = new TestClock();
        using var owner = new GatewayHostAuthority(Host, clock);
        var old = Pair(owner);
        using var oldSecret = old.Secret;
        clock.Advance(TimeSpan.FromMinutes(1));
        var offer = owner.ApproveRotation(old.DeviceId);
        using var token = offer.Token;
        GatewayDeviceCredential? renewed = null;
        await Task.WhenAll(
            Task.Run(() =>
            {
                try { renewed = Redeem(owner, offer); }
                catch (GatewayTrustException error)
                {
                    Assert.Equal(GatewayTrustFailure.PairingRejected, error.Failure);
                }
            }),
            Task.Run(() => owner.RevokeDevice(old.DeviceId)));
        using var newSecret = renewed?.Secret;
        Assert.Empty(owner.ListDevices());
        Reject(GatewayTrustFailure.CredentialRejected, () => Authorize(owner, old));
        if (renewed is not null)
            Reject(GatewayTrustFailure.CredentialRejected, () => Authorize(owner, renewed));
        Reject(GatewayTrustFailure.PairingRejected, () => Redeem(owner, offer));
    }

    private static GatewayDeviceCredential Pair(GatewayHostAuthority owner, Guid? id = null)
    {
        var offer = owner.ApprovePairing(id ?? Guid.NewGuid(), GatewayScope.Generation);
        using var token = offer.Token;
        return Redeem(owner, offer);
    }

    private static GatewayDeviceCredential Redeem(GatewayHostAuthority owner, GatewayPairingOffer offer,
        GatewayHostIdentity? host = null, CancellationToken ct = default) =>
        owner.Devices.Redeem(host ?? Host, offer.DeviceId, offer.Scopes, offer.ApprovalId, offer.Token, ct);

    private static GatewayAccessDecision Authorize(GatewayHostAuthority owner, GatewayDeviceCredential credential,
        GatewayScope scope = GatewayScope.Generation, GatewayHostIdentity? host = null) =>
        owner.Devices.Authorize(host ?? Host, credential.DeviceId, credential.Secret, scope);

    private static void Reject(GatewayTrustFailure failure, Action action) =>
        Assert.Equal(failure, Assert.Throws<GatewayTrustException>(action).Failure);

    private static byte[] Bytes(GatewaySecret secret)
    {
        var result = new byte[32];
        secret.CopyTo(result);
        return result;
    }

    private sealed class TestClock : TimeProvider
    {
        internal DateTimeOffset Utc { get; set; } = new(2026, 9, 23, 0, 0, 0, TimeSpan.Zero);
        internal long Timestamp { get; set; }
        internal long Frequency { get; set; } = TimeSpan.TicksPerSecond;
        internal Action? OnRead { get; set; }
        public override long TimestampFrequency => Frequency;
        public override DateTimeOffset GetUtcNow()
        {
            OnRead?.Invoke();
            return Utc;
        }
        public override long GetTimestamp() => Timestamp;
        internal void Advance(TimeSpan duration, bool utc = true, bool timestamp = true)
        {
            if (utc)
                Utc += duration;
            if (timestamp)
                Timestamp += duration.Ticks;
        }
    }
}
