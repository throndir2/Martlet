using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using static Martlet.Gateway.Persistence.PersistenceFailure;

namespace Martlet.Gateway.Persistence;

public enum LocalGatewayDecision { No, Enable }
internal enum HostOpenMode { Create, Open, ResetDevices, RenewCertificate, ReplaceIdentity }

// Local host-control capability. Never register this object in handler DI.
public sealed class DurableGatewayHost : IAsyncDisposable
{
    public static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly GatewayServer? server;
    private readonly IDisposable? storage;
    private readonly X509Certificate2? certificate;
    private readonly TimeProvider clock;
    private readonly GatewayOrigin? origin;
    private GatewayListenerHandle? listener;
    private Task? stopTask;
    private bool closed;
    public GatewayHostIdentity? Identity { get; }
    public bool Enabled => server is not null;

    private DurableGatewayHost() => clock = TimeProvider.System;

    private DurableGatewayHost(GatewayOrigin origin, GatewayHostIdentity identity,
        GatewayServer server, IDisposable storage, X509Certificate2 certificate, TimeProvider clock)
    {
        this.origin = origin;
        Identity = identity;
        this.server = server;
        this.storage = storage;
        this.certificate = certificate;
        this.clock = clock;
    }

    public static DurableGatewayHost CreateNew(string directory, string hostId, GatewayOrigin origin,
        IEnumerable<IGatewayWorker> workers, IGatewayAuditSink audit,
        LocalGatewayDecision decision = LocalGatewayDecision.No, CancellationToken cancellationToken = default) =>
        Open(directory, hostId, origin, workers, audit, decision, HostOpenMode.Create,
            TimeProvider.System, null, cancellationToken);

    public static DurableGatewayHost OpenExisting(string directory, GatewayOrigin origin,
        IEnumerable<IGatewayWorker> workers, IGatewayAuditSink audit,
        LocalGatewayDecision decision = LocalGatewayDecision.No, CancellationToken cancellationToken = default) =>
        Open(directory, null, origin, workers, audit, decision, HostOpenMode.Open,
            TimeProvider.System, null, cancellationToken);

    public static DurableGatewayHost ResetDevicesForLocalRecovery(string directory, GatewayOrigin origin,
        IEnumerable<IGatewayWorker> workers, IGatewayAuditSink audit,
        LocalGatewayDecision decision = LocalGatewayDecision.No, CancellationToken cancellationToken = default) =>
        Open(directory, null, origin, workers, audit, decision, HostOpenMode.ResetDevices,
            TimeProvider.System, null, cancellationToken);

    public static DurableGatewayHost RenewCertificateForLocalHost(string directory, GatewayOrigin origin,
        IEnumerable<IGatewayWorker> workers, IGatewayAuditSink audit,
        LocalGatewayDecision decision = LocalGatewayDecision.No, CancellationToken cancellationToken = default) =>
        Open(directory, null, origin, workers, audit, decision, HostOpenMode.RenewCertificate,
            TimeProvider.System, null, cancellationToken);

    public static DurableGatewayHost ReplaceIdentityForLocalRecovery(string directory, string newHostId,
        GatewayOrigin origin, IEnumerable<IGatewayWorker> workers, IGatewayAuditSink audit,
        LocalGatewayDecision decision = LocalGatewayDecision.No, CancellationToken cancellationToken = default) =>
        Open(directory, newHostId, origin, workers, audit, decision, HostOpenMode.ReplaceIdentity,
            TimeProvider.System, null, cancellationToken);

    internal static DurableGatewayHost Open(string directory, string? hostId, GatewayOrigin origin,
        IEnumerable<IGatewayWorker> workers, IGatewayAuditSink audit, LocalGatewayDecision decision,
        HostOpenMode mode, TimeProvider clock, Action<StoreStep>? fault = null,
        CancellationToken cancellationToken = default)
    {
        if (decision == LocalGatewayDecision.No)
            return new();
        if (decision != LocalGatewayDecision.Enable)
            throw Error(GatewayPersistenceFailure.InvalidState);
        if (!OperatingSystem.IsWindows())
            throw Error(GatewayPersistenceFailure.UnsupportedPlatform);
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(workers);
        ArgumentNullException.ThrowIfNull(audit);
        if (!IPAddress.IsLoopback(origin.Address))
            throw Error(GatewayPersistenceFailure.InvalidState);
        if (mode is HostOpenMode.Create or HostOpenMode.ReplaceIdentity)
            GatewayRules.Identifier(hostId!);
        cancellationToken.ThrowIfCancellationRequested();
        try { return OpenWindows(directory, hostId, origin, workers, audit, mode, clock, fault, cancellationToken); }
        catch (IOException) { throw Error(GatewayPersistenceFailure.StorageFailed); }
        catch (UnauthorizedAccessException) { throw Error(GatewayPersistenceFailure.InsecureStorage); }
        catch (CryptographicException) { throw Error(GatewayPersistenceFailure.KeyProtectionFailed); }
    }

    [SupportedOSPlatform("windows")]
    private static DurableGatewayHost OpenWindows(string directory, string? hostId, GatewayOrigin origin,
        IEnumerable<IGatewayWorker> workers, IGatewayAuditSink audit, HostOpenMode mode,
        TimeProvider clock, Action<StoreStep>? fault, CancellationToken cancellationToken)
    {
        WindowsAuthorityStore? store = null;
        X509Certificate2? certificate = null;
        GatewayCredentialStore? credentials = null;
        try
        {
            var now = clock.GetUtcNow();
            if (now.Offset != TimeSpan.Zero || now <= DateTimeOffset.MinValue ||
                now > DateTimeOffset.MaxValue - GatewayCredentialStore.MaximumLifetime)
                throw Error(GatewayPersistenceFailure.InvalidState);
            if (mode == HostOpenMode.Create)
            {
                using var generated = HostCertificate.Create(now);
                var bytes = generated.Export(X509ContentType.Pkcs12);
                try { store = WindowsAuthorityStore.Create(directory, hostId!, bytes, now, fault, clock); }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
            else
                store = WindowsAuthorityStore.Open(directory,
                    mode is HostOpenMode.ResetDevices or HostOpenMode.ReplaceIdentity, now, fault, clock);
            var encoded = store.CopyCertificate();
            try { certificate = HostCertificate.Load(encoded); }
            finally { CryptographicOperations.ZeroMemory(encoded); }
            if (mode is HostOpenMode.RenewCertificate or HostOpenMode.ReplaceIdentity)
            {
                using var retained = mode == HostOpenMode.RenewCertificate ? certificate.GetECDsaPrivateKey() : null;
                using var replacement = HostCertificate.Create(now, retained);
                var bytes = replacement.Export(X509ContentType.Pkcs12);
                try
                {
                    store.ReplaceCertificate(bytes, mode == HostOpenMode.ReplaceIdentity ? hostId : null);
                    certificate.Dispose();
                    certificate = HostCertificate.Load(bytes);
                }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
            cancellationToken.ThrowIfCancellationRequested();
            var identity = GatewayHostIdentity.FromCertificate(store.HostId, certificate);
            credentials = new(identity, clock, store.Initial, store);
            store.Initial.Dispose();
            var server = new GatewayServer(identity, origin, workers, audit, clock, null, null, null, credentials);
            return new(origin, identity, server, store, certificate, clock);
        }
        catch
        {
            credentials?.Close();
            certificate?.Dispose();
            store?.Dispose();
            throw;
        }
    }

    public ValueTask StartAsync(CancellationToken cancellationToken = default) =>
        StartAsync(new KestrelGatewayListenerFactory(), cancellationToken);

    internal async ValueTask StartAsync(IGatewayListenerFactory listenerFactory, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireOpen();
            server!.Credentials.ObserveTime(clock);
            var binding = new GatewayTlsBinding(origin!, Identity!, certificate!, clock);
            listener = await server.StartAsync(binding, listenerFactory, cancellationToken)
                .ConfigureAwait(false);
            server.Credentials.ObserveTime(clock);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch
        {
            server?.Credentials.Close();
            server?.Pairing.Close();
            throw;
        }
        finally { gate.Release(); }
    }

    public GatewayPairingCard OpenPairing(GatewayPairingApproval approval,
        CancellationToken cancellationToken = default) =>
        Local(() => server!.Pairing.OpenWindow(approval), cancellationToken);

    public IssuedDeviceCredential Rotate(string credentialId, TimeSpan overlap,
        CancellationToken cancellationToken = default) =>
        Local(() => server!.Credentials.Rotate(credentialId, overlap, cancellationToken), cancellationToken);

    public bool RevokeCredential(string credentialId, CancellationToken cancellationToken = default) =>
        Local(() => server!.Credentials.RevokeCredential(credentialId, cancellationToken), cancellationToken);

    public int RevokeDevice(string deviceId, CancellationToken cancellationToken = default) =>
        Local(() => server!.Credentials.RevokeDevice(deviceId, cancellationToken), cancellationToken);

    public IReadOnlyList<GatewayDeviceRegistration> ListRegistrations(CancellationToken cancellationToken = default) =>
        Local(() => server!.Credentials.ListRegistrations(), cancellationToken);

    private T Local<T>(Func<T> action, CancellationToken cancellationToken)
    {
        gate.Wait(cancellationToken);
        try
        {
            RequireOpen();
            cancellationToken.ThrowIfCancellationRequested();
            return action();
        }
        finally { gate.Release(); }
    }

    private void RequireOpen()
    {
        if (server is null)
            throw Error(GatewayPersistenceFailure.Disabled);
        if (closed || stopTask is not null)
            throw Error(GatewayPersistenceFailure.Closed);
    }

    public ValueTask CloseCleanlyAsync(CancellationToken cancellationToken = default) =>
        StopAsync(clean: true, cancellationToken);

    public ValueTask DisposeAsync() => StopAsync(clean: false, CancellationToken.None);

    private async ValueTask StopAsync(bool clean, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (closed)
                return;
            if (server is null)
            {
                closed = true;
                return;
            }
            server.Credentials.StopAdmissions();
            server.Pairing.Close();
            if (listener is not null)
            {
                stopTask ??= listener.DisposeAsync().AsTask();
                try { await stopTask.WaitAsync(ShutdownTimeout, cancellationToken).ConfigureAwait(false); }
                catch
                {
                    // Keep ownership and the dirty fence until the listener has actually stopped.
                    server.Credentials.Close();
                    throw;
                }
            }
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (clean)
                    server.Credentials.Complete(cancellationToken);
            }
            finally
            {
                server.Credentials.Close();
                certificate!.Dispose();
                storage!.Dispose();
                closed = true;
            }
        }
        finally { gate.Release(); }
    }

    public override string ToString() => nameof(DurableGatewayHost);
}
