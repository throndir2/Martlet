using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Martlet.Gateway.Trust;
using static Martlet.Gateway.Persistence.PersistenceFailure;

namespace Martlet.Gateway.Persistence;

// Host-control capability: do not give this object to remote handlers.
public sealed class DurableGatewayHost : IDisposable
{
    private readonly GatewayHostAuthority authority;
    private readonly IDisposable storage;
    private readonly X509Certificate2 certificate;
    public GatewayHostIdentity Identity { get; }
    public GatewayDeviceAccess Devices => authority.Devices;

    private DurableGatewayHost(GatewayHostIdentity identity, GatewayHostAuthority authority,
        IDisposable storage, X509Certificate2 certificate)
    {
        Identity = identity;
        this.authority = authority;
        this.storage = storage;
        this.certificate = certificate;
    }

    public static DurableGatewayHost CreateNew(string directory, CancellationToken cancellationToken = default) =>
        Start(directory, create: true, resetDevices: false, TimeProvider.System, null, cancellationToken);

    public static DurableGatewayHost OpenExisting(string directory, CancellationToken cancellationToken = default) =>
        Start(directory, create: false, resetDevices: false, TimeProvider.System, null, cancellationToken);

    // The local host controller must obtain explicit approval; recovery never restores devices.
    public static DurableGatewayHost ResetDevicesForLocalRecovery(string directory,
        CancellationToken cancellationToken = default) =>
        Start(directory, create: false, resetDevices: true, TimeProvider.System, null, cancellationToken);

    public static DurableGatewayHost RenewCertificateForLocalHost(string directory,
        CancellationToken cancellationToken = default) =>
        Start(directory, false, false, TimeProvider.System, null, cancellationToken, CertificateChange.Renew);

    public static DurableGatewayHost ReplaceHostKeyForLocalRecovery(string directory,
        CancellationToken cancellationToken = default) =>
        Start(directory, false, true, TimeProvider.System, null, cancellationToken, CertificateChange.ReplaceKey);

    internal static DurableGatewayHost Start(string directory, bool create, bool resetDevices, TimeProvider clock,
        Action<StoreStep>? fault = null, CancellationToken cancellationToken = default,
        CertificateChange certificateChange = CertificateChange.None)
    {
        if (!OperatingSystem.IsWindows())
            throw Error(GatewayPersistenceFailure.UnsupportedPlatform);
        cancellationToken.ThrowIfCancellationRequested();
        try { return StartWindows(directory, create, resetDevices, clock, fault, certificateChange); }
        catch (IOException) { throw Error(GatewayPersistenceFailure.StorageFailed); }
        catch (UnauthorizedAccessException) { throw Error(GatewayPersistenceFailure.InsecureStorage); }
        catch (CryptographicException) { throw Error(GatewayPersistenceFailure.KeyProtectionFailed); }
    }

    [SupportedOSPlatform("windows")]
    private static DurableGatewayHost StartWindows(string directory, bool create, bool resetDevices, TimeProvider clock,
        Action<StoreStep>? fault, CertificateChange certificateChange)
    {
        WindowsAuthorityStore? store = null;
        X509Certificate2? certificate = null;
        try
        {
            var now = clock.GetUtcNow();
            if (now.Offset != TimeSpan.Zero || now <= DateTimeOffset.MinValue ||
                now > DateTimeOffset.MaxValue - TimeSpan.FromDays(90))
                throw Error(GatewayPersistenceFailure.InvalidState);
            if (create)
            {
                var id = Guid.NewGuid();
                certificate = HostCertificate.Create(id, now);
                var bytes = certificate.Export(X509ContentType.Pkcs12);
                try { store = WindowsAuthorityStore.Create(directory, id, bytes, now, fault); }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
            else
            {
                store = WindowsAuthorityStore.Open(directory, resetDevices, now, fault);
                var bytes = store.CopyCertificate();
                try { certificate = HostCertificate.Load(bytes, store.HostId); }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
            using var empty = resetDevices ? new TrustCheckpoint(now, []) : null;
            if (certificateChange != CertificateChange.None)
            {
                if (now < store.Initial.ObservedAt)
                    throw Error(GatewayPersistenceFailure.RecoveryRequired);
                using var retained = certificateChange == CertificateChange.Renew ? certificate.GetECDsaPrivateKey() : null;
                var replacement = HostCertificate.Create(store.HostId, now, retained);
                var bytes = replacement.Export(X509ContentType.Pkcs12);
                try { store.ReplaceCertificate(bytes, empty ?? store.Initial); }
                catch
                {
                    replacement.Dispose();
                    throw;
                }
                finally { CryptographicOperations.ZeroMemory(bytes); }
                certificate.Dispose();
                certificate = replacement;
            }
            var identity = HostCertificate.Identity(store.HostId, certificate);
            var authority = new GatewayHostAuthority(identity, clock, empty ?? store.Initial, store);
            return new(identity, authority, store, certificate);
        }
        catch
        {
            certificate?.Dispose();
            store?.Dispose();
            throw;
        }
    }

    public GatewayPairingOffer ApprovePairing(Guid deviceId, GatewayScope scopes, CancellationToken cancellationToken = default) =>
        authority.ApprovePairing(deviceId, scopes, cancellationToken);

    public GatewayPairingOffer ApproveRotation(Guid deviceId, CancellationToken cancellationToken = default) =>
        authority.ApproveRotation(deviceId, cancellationToken);

    public void CancelApproval(Guid approvalId, CancellationToken cancellationToken = default) =>
        authority.CancelApproval(approvalId, cancellationToken);

    public void RevokeDevice(Guid deviceId, CancellationToken cancellationToken = default) =>
        authority.RevokeDevice(deviceId, cancellationToken);

    public IReadOnlyList<GatewayDeviceInfo> ListDevices(CancellationToken cancellationToken = default) =>
        authority.ListDevices(cancellationToken);

    public void CloseCleanly(CancellationToken cancellationToken = default)
    {
        authority.Complete(cancellationToken);
        Dispose();
    }

    public void Dispose()
    {
        authority.Dispose();
        certificate.Dispose();
        storage.Dispose();
    }

    public override string ToString() => nameof(DurableGatewayHost);
}

internal enum CertificateChange { None, Renew, ReplaceKey }
