using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Martlet.Gateway.Trust;

namespace Martlet.Gateway.Persistence.Tests;

[SupportedOSPlatform("windows")]
public sealed class PersistenceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "Martlet-Gateway-" + Guid.NewGuid().ToString("N"));
    private string Store => Path.Combine(root, "host");

    public PersistenceTests() => Directory.CreateDirectory(root);

    [Fact]
    public void RealProtectedStorePreservesIdentityCredentialAndScopeAfterCleanRestart()
    {
        using var first = DurableGatewayHost.CreateNew(Store);
        var host = first.Identity;
        var credential = Pair(first, GatewayScope.Status | GatewayScope.Generation);
        using var secret = credential.Secret;
        Authorize(first, credential, GatewayScope.Generation);
        var file = File.ReadAllBytes(Path.Combine(Store, "authority.bin"));
        var raw = new byte[32];
        secret.CopyTo(raw);
        Assert.False(file.AsSpan().IndexOf(raw) >= 0);
        CryptographicOperations.ZeroMemory(raw);
        Assert.DoesNotContain(host.HostId.ToString(), System.Text.Encoding.UTF8.GetString(file));
        first.CloseCleanly();
        using var restarted = DurableGatewayHost.OpenExisting(Store);
        Assert.Equal(host, restarted.Identity);
        Assert.Single(restarted.ListDevices());
        Authorize(restarted, credential, GatewayScope.Status);
        Reject(GatewayTrustFailure.ScopeDenied, () => Authorize(restarted, credential, GatewayScope.MemoryWrite));
        restarted.CloseCleanly();
    }

    [Fact]
    public void ApprovalsNeverSurviveCleanRestartAndLostReplyCannotReplay()
    {
        using var first = DurableGatewayHost.CreateNew(Store);
        var pending = first.ApprovePairing(Guid.NewGuid(), GatewayScope.Status);
        using var token = pending.Token;
        var redeemed = first.ApprovePairing(Guid.NewGuid(), GatewayScope.Status);
        using var consumedToken = redeemed.Token;
        using var secret = Redeem(first, redeemed).Secret;
        first.CloseCleanly();
        using var next = DurableGatewayHost.OpenExisting(Store);
        Reject(GatewayTrustFailure.PairingRejected, () => Redeem(next, pending));
        Reject(GatewayTrustFailure.PairingRejected, () => Redeem(next, redeemed));
        next.CloseCleanly();
    }

    [Fact]
    public void DisposeIsUncleanAndExplicitRecoveryInvalidatesEveryDevice()
    {
        GatewayDeviceCredential credential;
        GatewayHostIdentity identity;
        using (var first = DurableGatewayHost.CreateNew(Store))
        {
            identity = first.Identity;
            credential = Pair(first);
        }
        using var secret = credential.Secret;
        Reject(GatewayPersistenceFailure.RecoveryRequired, () => DurableGatewayHost.OpenExisting(Store));
        using var reset = DurableGatewayHost.ResetDevicesForLocalRecovery(Store);
        Assert.Equal(identity, reset.Identity);
        Assert.Empty(reset.ListDevices());
        Reject(GatewayTrustFailure.CredentialRejected, () => Authorize(reset, credential));
        reset.CloseCleanly();
        using var next = DurableGatewayHost.OpenExisting(Store);
        Reject(GatewayTrustFailure.CredentialRejected, () => Authorize(next, credential));
        next.CloseCleanly();
    }

    [Fact]
    public void RevocationAndBothRotationGenerationsSurviveRestart()
    {
        var clock = new Clock();
        using var first = Open(clock, create: true);
        var old = Pair(first);
        using var oldSecret = old.Secret;
        var ticket = first.ApproveRotation(old.DeviceId);
        using var token = ticket.Token;
        var next = Redeem(first, ticket);
        using var nextSecret = next.Secret;
        clock.Advance(TimeSpan.FromMinutes(1));
        first.CloseCleanly();
        using var restarted = Open(clock);
        Authorize(restarted, old);
        Authorize(restarted, next);
        clock.Advance(TimeSpan.FromMinutes(1));
        Reject(GatewayTrustFailure.CredentialRejected, () => Authorize(restarted, old));
        restarted.RevokeDevice(next.DeviceId);
        restarted.CloseCleanly();
        using var final = Open(clock);
        Reject(GatewayTrustFailure.CredentialRejected, () => Authorize(final, old));
        Reject(GatewayTrustFailure.CredentialRejected, () => Authorize(final, next));
        final.CloseCleanly();
    }

    [Fact]
    public void MonotonicBudgetAndOfflineTimeCannotBeRestartedAtNinetyDays()
    {
        var clock = new Clock();
        using var first = Open(clock, create: true);
        var credential = Pair(first);
        using var secret = credential.Secret;
        clock.Advance(TimeSpan.FromDays(80), utc: false);
        first.CloseCleanly();
        var nextClock = new Clock { Utc = clock.Utc.AddDays(3) };
        using var next = Open(nextClock);
        Authorize(next, credential);
        nextClock.Advance(TimeSpan.FromDays(7));
        Reject(GatewayTrustFailure.CredentialRejected, () => Authorize(next, credential));
        next.CloseCleanly();
    }

    [Fact]
    public void OfflineExpirationAndBackwardsRestartClockFailClosed()
    {
        var clock = new Clock();
        using var first = Open(clock, create: true);
        var credential = Pair(first);
        using var secret = credential.Secret;
        first.CloseCleanly();
        clock.Advance(TimeSpan.FromDays(90));
        using var next = Open(clock);
        Assert.Empty(next.ListDevices());
        next.CloseCleanly();
        clock.Utc -= TimeSpan.FromSeconds(1);
        Reject(GatewayTrustFailure.ClockInvalid, () => Open(clock));
        clock.Utc += TimeSpan.FromSeconds(1);
        Reject(GatewayPersistenceFailure.RecoveryRequired, () => Open(clock));
    }

    [Fact]
    public void ClockFailureCannotBeCleanlySavedOrResurrected()
    {
        var clock = new Clock();
        using var host = Open(clock, create: true);
        using var secret = Pair(host).Secret;
        clock.Utc -= TimeSpan.FromSeconds(1);
        Reject(GatewayTrustFailure.ClockInvalid, () => host.ListDevices());
        Reject(GatewayTrustFailure.ClockInvalid, () => host.CloseCleanly());
        host.Dispose();
        Reject(GatewayPersistenceFailure.RecoveryRequired, () => Open(new Clock()));
    }

    [Fact]
    public void ConcurrentOwnerAndMissingCorruptStoresCannotBecomeNewAuthority()
    {
        Reject(GatewayPersistenceFailure.StoreMissing, () => DurableGatewayHost.OpenExisting(Store));
        using var host = DurableGatewayHost.CreateNew(Store);
        Reject(GatewayPersistenceFailure.StoreBusy, () => DurableGatewayHost.OpenExisting(Store));
        Reject(GatewayPersistenceFailure.InsecureStorage, () => DurableGatewayHost.CreateNew(Store));
        host.CloseCleanly();
        File.WriteAllBytes(Path.Combine(Store, "authority.bin"), new byte[20]);
        Reject(GatewayPersistenceFailure.InvalidState, () => DurableGatewayHost.OpenExisting(Store));
        Reject(GatewayPersistenceFailure.InvalidState, () => DurableGatewayHost.ResetDevicesForLocalRecovery(Store));
    }

    [Theory]
    [InlineData((int)StoreStep.PendingFlushed)]
    [InlineData((int)StoreStep.Replaced)]
    [InlineData((int)StoreStep.CheckpointCommitted)]
    public void FailedRevocationPoisonsRunningAuthorityAndPreventsStaleRestart(int stepValue)
    {
        var step = (StoreStep)stepValue;
        var armed = false;
        using var host = Open(new Clock(), create: true, fault: observed =>
        {
            if (armed && observed == step)
                throw new IOException("test fault must not escape");
        });
        var credential = Pair(host);
        using var secret = credential.Secret;
        armed = true;
        var error = Assert.Throws<GatewayPersistenceException>(() => host.RevokeDevice(credential.DeviceId));
        Assert.Equal(GatewayPersistenceFailure.StorageFailed, error.Failure);
        Assert.DoesNotContain("test fault", error.ToString());
        Reject(GatewayTrustFailure.AuthorityClosed, () => Authorize(host, credential));
        Reject(GatewayTrustFailure.AuthorityClosed, () => host.CloseCleanly());
        host.Dispose();
        Reject(GatewayPersistenceFailure.RecoveryRequired, () => Open(new Clock()));
        using var recovered = DurableGatewayHost.Start(Store, false, true, new Clock());
        Assert.Empty(recovered.ListDevices());
        Reject(GatewayTrustFailure.CredentialRejected, () => Authorize(recovered, credential));
        recovered.CloseCleanly();
    }

    [Fact]
    public void InterruptedCleanCloseRemainsDirty()
    {
        using var host = Open(new Clock(), create: true, fault: step =>
        {
            if (step == StoreStep.BeforeCleanFenceRemoval)
                throw new IOException("test");
        });
        Assert.Throws<GatewayPersistenceException>(() => host.CloseCleanly());
        host.Dispose();
        Reject(GatewayPersistenceFailure.RecoveryRequired, () => Open(new Clock()));
    }

    [Theory]
    [InlineData((int)StoreStep.PendingFlushed)]
    [InlineData((int)StoreStep.Replaced)]
    [InlineData((int)StoreStep.CheckpointCommitted)]
    public async Task ActualProcessTerminationDuringRevocationCannotRestoreOldCredential(int stepValue)
    {
        var step = (StoreStep)stepValue;
        using var host = DurableGatewayHost.CreateNew(Store);
        var credential = Pair(host);
        using var secret = credential.Secret;
        host.CloseCleanly();
        var dotnet = Path.Combine(Environment.GetEnvironmentVariable("DOTNET_ROOT")!, "dotnet.exe");
        var start = new ProcessStartInfo(dotnet) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--crash-probe");
        start.ArgumentList.Add(Store);
        start.ArgumentList.Add(credential.DeviceId.ToString());
        start.ArgumentList.Add(step.ToString());
        using var process = Process.Start(start)!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(deadline.Token); }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        Assert.Equal(71, process.ExitCode);
        Reject(GatewayPersistenceFailure.RecoveryRequired, () => DurableGatewayHost.OpenExisting(Store));
        using var recovered = DurableGatewayHost.ResetDevicesForLocalRecovery(Store);
        Reject(GatewayTrustFailure.CredentialRejected, () => Authorize(recovered, credential));
        recovered.CloseCleanly();
    }

    [Fact]
    public void CanceledAdmissionDoesNotCreateConsumeOrRevoke()
    {
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => DurableGatewayHost.CreateNew(Store, canceled.Token));
        Assert.False(Directory.Exists(Store));
        using var host = DurableGatewayHost.CreateNew(Store);
        var credential = Pair(host);
        using var secret = credential.Secret;
        Assert.Throws<OperationCanceledException>(() => host.RevokeDevice(credential.DeviceId, canceled.Token));
        Assert.Throws<OperationCanceledException>(() => host.CloseCleanly(canceled.Token));
        Authorize(host, credential);
        host.CloseCleanly();
        using var next = DurableGatewayHost.OpenExisting(Store);
        Authorize(next, credential);
        next.CloseCleanly();
    }

    [Fact]
    public void BroadenedDirectoryPermissionsAreRejectedWithoutRepair()
    {
        using var host = DurableGatewayHost.CreateNew(Store);
        host.CloseCleanly();
        var info = new DirectoryInfo(Store);
        var prior = info.GetAccessControl();
        var changed = info.GetAccessControl();
        changed.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        try
        {
            info.SetAccessControl(changed);
            var beforeOpen = info.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);
            Reject(GatewayPersistenceFailure.InsecureStorage, () => DurableGatewayHost.OpenExisting(Store));
            Assert.Equal(beforeOpen,
                info.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access));
        }
        finally { info.SetAccessControl(prior); }
    }

    [Fact]
    public void UnknownPendingFilesAndModifiedAuthorityFailClosed()
    {
        using var host = DurableGatewayHost.CreateNew(Store);
        var credential = Pair(host);
        using var secret = credential.Secret;
        File.WriteAllBytes(Path.Combine(Store, "unexpected"), [1]);
        Reject(GatewayPersistenceFailure.RecoveryRequired, () => host.RevokeDevice(credential.DeviceId));
        Reject(GatewayTrustFailure.AuthorityClosed, () => Authorize(host, credential));
        host.Dispose();
        Reject(GatewayPersistenceFailure.RecoveryRequired, () => DurableGatewayHost.ResetDevicesForLocalRecovery(Store));
    }

    [Fact]
    public void RawSecretAndStoreImportsAreNotPublicCapabilities()
    {
        Assert.Empty(typeof(DurableGatewayHost).GetConstructors());
        Assert.DoesNotContain(typeof(DurableGatewayHost).GetMethods(), method =>
            method.Name.Contains("Import", StringComparison.Ordinal) ||
            method.GetParameters().Any(parameter => parameter.ParameterType == typeof(byte[])));
        Assert.Equal(["Authorize", "Redeem", "ToString"],
            typeof(GatewayDeviceAccess).GetMethods().Where(m => m.DeclaringType == typeof(GatewayDeviceAccess))
                .Select(m => m.Name).Order().ToArray());
    }

    [Fact]
    public void SameKeyRenewalPreservesTrustButKeyReplacementRequiresPairing()
    {
        var clock = new Clock();
        using var first = Open(clock, create: true);
        var credential = Pair(first);
        using var secret = credential.Secret;
        var identity = first.Identity;
        first.CloseCleanly();
        clock.Advance(TimeSpan.FromDays(1));
        using var renewed = DurableGatewayHost.Start(Store, false, false, clock, certificateChange: CertificateChange.Renew);
        Assert.Equal(identity, renewed.Identity);
        Authorize(renewed, credential);
        renewed.CloseCleanly();
        using var replaced = DurableGatewayHost.Start(Store, false, true, clock, certificateChange: CertificateChange.ReplaceKey);
        Assert.Equal(identity.HostId, replaced.Identity.HostId);
        Assert.NotEqual(identity.SpkiSha256, replaced.Identity.SpkiSha256);
        Assert.Empty(replaced.ListDevices());
        Reject(GatewayTrustFailure.HostMismatch, () => Authorize(replaced, credential));
        Reject(GatewayTrustFailure.CredentialRejected, () => replaced.Devices.Authorize(
            replaced.Identity, credential.DeviceId, credential.Secret, GatewayScope.Status));
        replaced.CloseCleanly();
        using var reopened = Open(clock);
        Assert.Equal(replaced.Identity, reopened.Identity);
        reopened.CloseCleanly();
    }

    [Fact]
    public void HardLinkedStateIsDenied()
    {
        using var host = DurableGatewayHost.CreateNew(Store);
        host.CloseCleanly();
        Assert.True(CreateHardLink(Path.Combine(root, "state-link"), Path.Combine(Store, "authority.bin"), IntPtr.Zero));
        Reject(GatewayPersistenceFailure.InsecureStorage, () => DurableGatewayHost.OpenExisting(Store));
    }

    [Fact]
    public void JunctionAncestorIsDenied()
    {
        var alias = Path.Combine(root, "alias");
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec")!)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("/d");
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("mklink");
        start.ArgumentList.Add("/J");
        start.ArgumentList.Add(alias);
        start.ArgumentList.Add(Store);
        using var host = DurableGatewayHost.CreateNew(Store);
        host.CloseCleanly();
        using var process = Process.Start(start)!;
        Assert.True(process.WaitForExit(10000));
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
        try { Reject(GatewayPersistenceFailure.InsecureStorage, () => DurableGatewayHost.OpenExisting(alias)); }
        finally { Directory.Delete(alias); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(20)]
    [InlineData(StoreFormat.MaximumBytes + 1)]
    public void TruncatedOversizedAndCorruptProtectedFilesAreNotRecovered(int length)
    {
        using var host = DurableGatewayHost.CreateNew(Store);
        host.CloseCleanly();
        File.WriteAllBytes(Path.Combine(Store, "authority.bin"), new byte[length]);
        Reject(GatewayPersistenceFailure.InvalidState, () => DurableGatewayHost.OpenExisting(Store));
    }

    [Fact]
    public void DpapiTamperingCannotAuthenticateAuthority()
    {
        using var host = DurableGatewayHost.CreateNew(Store);
        host.CloseCleanly();
        var path = Path.Combine(Store, "authority.bin");
        var bytes = File.ReadAllBytes(path);
        bytes[^1] ^= 0x80;
        File.WriteAllBytes(path, bytes);
        Reject(GatewayPersistenceFailure.KeyProtectionFailed, () => DurableGatewayHost.OpenExisting(Store));
    }

    [Theory]
    [InlineData((int)StoreStep.FenceFlushed)]
    [InlineData((int)StoreStep.PendingFlushed)]
    [InlineData((int)StoreStep.Replaced)]
    [InlineData((int)StoreStep.CheckpointCommitted)]
    public void InterruptedInitializationNeverImplicitlyStartsOver(int value)
    {
        Assert.Throws<GatewayPersistenceException>(() => Open(new Clock(), create: true, fault: step =>
        {
            if ((int)step == value) throw new IOException("test");
        }));
        Reject(GatewayPersistenceFailure.RecoveryRequired, () => Open(new Clock()));
        Reject(GatewayPersistenceFailure.InsecureStorage, () => DurableGatewayHost.CreateNew(Store));
    }

    [Fact]
    public async Task ConcurrentRedemptionStillHasOneDurableWinner()
    {
        using var host = DurableGatewayHost.CreateNew(Store);
        var offer = host.ApprovePairing(Guid.NewGuid(), GatewayScope.Status);
        using var token = offer.Token;
        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
        {
            try { return Redeem(host, offer); }
            catch (GatewayTrustException error)
            {
                Assert.Equal(GatewayTrustFailure.PairingRejected, error.Failure);
                return null;
            }
        })));
        var credential = Assert.Single(results, value => value is not null)!;
        using var secret = credential.Secret;
        host.CloseCleanly();
        using var next = DurableGatewayHost.OpenExisting(Store);
        Authorize(next, credential);
        next.CloseCleanly();
    }

    [Fact]
    public void CancellationAfterCommitStartsCannotUndoRevocation()
    {
        using var cancellation = new CancellationTokenSource();
        var armed = false;
        using var host = Open(new Clock(), create: true, fault: step =>
        {
            if (armed && step == StoreStep.PendingFlushed) cancellation.Cancel();
        });
        var credential = Pair(host);
        using var secret = credential.Secret;
        armed = true;
        host.RevokeDevice(credential.DeviceId, cancellation.Token);
        Assert.True(cancellation.IsCancellationRequested);
        Reject(GatewayTrustFailure.CredentialRejected, () => Authorize(host, credential));
        host.CloseCleanly();
        using var next = Open(new Clock());
        Reject(GatewayTrustFailure.CredentialRejected, () => Authorize(next, credential));
        next.CloseCleanly();
    }

    [Fact]
    public void FailedRotationDoesNotExposeCredentialOrPermitCleanRecovery()
    {
        var armed = false;
        using var host = Open(new Clock(), create: true, fault: step =>
        {
            if (armed && step == StoreStep.PendingFlushed) throw new IOException("test");
        });
        var credential = Pair(host);
        using var secret = credential.Secret;
        var rotation = host.ApproveRotation(credential.DeviceId);
        using var token = rotation.Token;
        armed = true;
        Reject(GatewayPersistenceFailure.StorageFailed, () => Redeem(host, rotation));
        Reject(GatewayTrustFailure.AuthorityClosed, () => Authorize(host, credential));
        host.Dispose();
        Reject(GatewayPersistenceFailure.RecoveryRequired, () => Open(new Clock()));
        using var recovered = DurableGatewayHost.Start(Store, false, true, new Clock());
        Reject(GatewayTrustFailure.CredentialRejected, () => Authorize(recovered, credential));
        Reject(GatewayTrustFailure.PairingRejected, () => Redeem(recovered, rotation));
        recovered.CloseCleanly();
    }

    [Fact]
    public void UnapprovedFileRevisionCannotBeOverwritten()
    {
        using var host = DurableGatewayHost.CreateNew(Store);
        var credential = Pair(host);
        using var secret = credential.Secret;
        var path = Path.Combine(Store, "authority.bin");
        var bytes = File.ReadAllBytes(path);
        bytes[^1] ^= 0x80;
        File.WriteAllBytes(path, bytes);
        Reject(GatewayPersistenceFailure.RecoveryRequired, () => host.RevokeDevice(credential.DeviceId));
        Reject(GatewayTrustFailure.AuthorityClosed, () => Authorize(host, credential));
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void ProtectedCertificateWithDifferentHostIdentityIsRejected()
    {
        using var host = DurableGatewayHost.CreateNew(Store);
        host.CloseCleanly();
        var path = Path.Combine(Store, "authority.bin");
        var protectedBytes = StoreFormat.Unwrap(File.ReadAllBytes(path));
        var clear = WindowsProtection.Unprotect(protectedBytes);
        try
        {
            using var document = StoreFormat.Read(clear);
            using var different = HostCertificate.Create(Guid.NewGuid(), DateTimeOffset.UtcNow);
            var replacement = different.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pkcs12);
            var encoded = StoreFormat.Write(document with { Certificate = replacement });
            try { File.WriteAllBytes(path, StoreFormat.Wrap(WindowsProtection.Protect(encoded))); }
            finally
            {
                CryptographicOperations.ZeroMemory(replacement);
                CryptographicOperations.ZeroMemory(encoded);
            }
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
        Reject(GatewayPersistenceFailure.InvalidState, () => DurableGatewayHost.OpenExisting(Store));
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string newName, string existing, IntPtr attributes);

    private DurableGatewayHost Open(Clock clock, bool create = false, Action<StoreStep>? fault = null) =>
        DurableGatewayHost.Start(Store, create, false, clock, fault);

    private static GatewayDeviceCredential Pair(DurableGatewayHost host, GatewayScope scopes = GatewayScope.Status)
    {
        var offer = host.ApprovePairing(Guid.NewGuid(), scopes);
        using var token = offer.Token;
        return Redeem(host, offer);
    }

    private static GatewayDeviceCredential Redeem(DurableGatewayHost host, GatewayPairingOffer offer) =>
        host.Devices.Redeem(offer.Host, offer.DeviceId, offer.Scopes, offer.ApprovalId, offer.Token);

    private static void Authorize(DurableGatewayHost host, GatewayDeviceCredential credential, GatewayScope scope = GatewayScope.Status) =>
        host.Devices.Authorize(credential.Host, credential.DeviceId, credential.Secret, scope);

    private static void Reject(GatewayPersistenceFailure failure, Action action) =>
        Assert.Equal(failure, Assert.Throws<GatewayPersistenceException>(action).Failure);

    private static void Reject(GatewayTrustFailure failure, Action action) =>
        Assert.Equal(failure, Assert.Throws<GatewayTrustException>(action).Failure);

    public void Dispose() => Directory.Delete(root, recursive: true);

    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Utc { get; set; } = new(2026, 9, 23, 0, 0, 0, TimeSpan.Zero);
        internal long Ticks { get; set; }
        public override DateTimeOffset GetUtcNow() => Utc;
        public override long GetTimestamp() => Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        internal void Advance(TimeSpan time, bool utc = true)
        {
            if (utc) Utc += time;
            Ticks += time.Ticks;
        }
    }
}
