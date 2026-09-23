using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using static Martlet.Gateway.Persistence.PersistenceFailure;

namespace Martlet.Gateway.Persistence;

public enum LocalGatewayDecision { No, Enable }
internal enum HostOpenMode { Create, Open, RenewCertificate }

// Local host-control capability. Never register this object in handler DI.
public sealed class DurableGatewayHost : IAsyncDisposable
{
    public static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly GatewayServer? server;
    private readonly IDisposable? storage;
    private X509Certificate2? certificate;
    private readonly object certificateGate = new();
    private readonly List<X509Certificate2> retiredCertificates = [];
    private readonly Action<byte[], GatewayCheckpoint>? replaceCertificate;
    private readonly TimeProvider clock;
    private readonly GatewayOrigin? origin;
    private GatewayListenerHandle? listener;
    private Task? stopTask;
    private bool closed;
    public GatewayHostIdentity? Identity { get; }
    public bool Enabled => server is not null;

    private DurableGatewayHost() => clock = TimeProvider.System;

    private DurableGatewayHost(GatewayOrigin origin, GatewayHostIdentity identity,
        GatewayServer server, IDisposable storage, X509Certificate2 certificate, TimeProvider clock,
        Action<byte[], GatewayCheckpoint> replaceCertificate)
    {
        this.origin = origin;
        Identity = identity;
        this.server = server;
        this.storage = storage;
        this.certificate = certificate;
        this.clock = clock;
        this.replaceCertificate = replaceCertificate;
    }

    public static DurableGatewayHost CreateNew(string directory, string hostId, GatewayOrigin origin,
        IEnumerable<IGatewayWorker> workers, IGatewayAuditSink audit,
        LocalGatewayDecision decision = LocalGatewayDecision.No, CancellationToken cancellationToken = default,
        IEnumerable<IGatewayInferenceWorker>? inferenceWorkers = null) =>
        Open(directory, hostId, origin, workers, audit, decision, HostOpenMode.Create,
            TimeProvider.System, null, cancellationToken, inferenceWorkers);

    public static DurableGatewayHost OpenExisting(string directory, GatewayOrigin origin,
        IEnumerable<IGatewayWorker> workers, IGatewayAuditSink audit,
        LocalGatewayDecision decision = LocalGatewayDecision.No, CancellationToken cancellationToken = default,
        IEnumerable<IGatewayInferenceWorker>? inferenceWorkers = null) =>
        Open(directory, null, origin, workers, audit, decision, HostOpenMode.Open,
            TimeProvider.System, null, cancellationToken, inferenceWorkers);

    public static DurableGatewayHost RenewCertificateForLocalHost(string directory, GatewayOrigin origin,
        IEnumerable<IGatewayWorker> workers, IGatewayAuditSink audit,
        LocalGatewayDecision decision = LocalGatewayDecision.No, CancellationToken cancellationToken = default,
        IEnumerable<IGatewayInferenceWorker>? inferenceWorkers = null) =>
        Open(directory, null, origin, workers, audit, decision, HostOpenMode.RenewCertificate,
            TimeProvider.System, null, cancellationToken, inferenceWorkers);

    internal static DurableGatewayHost Open(string directory, string? hostId, GatewayOrigin origin,
        IEnumerable<IGatewayWorker> workers, IGatewayAuditSink audit, LocalGatewayDecision decision,
        HostOpenMode mode, TimeProvider clock, Action<StoreStep>? fault = null,
        CancellationToken cancellationToken = default,
        IEnumerable<IGatewayInferenceWorker>? inferenceWorkers = null)
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
        if (mode == HostOpenMode.Create)
            GatewayRules.Identifier(hostId!);
        cancellationToken.ThrowIfCancellationRequested();
        var inferenceRegistry = new GatewayInferenceRouteRegistry(inferenceWorkers ?? [], clock);
        try { return OpenWindows(directory, hostId, origin, workers, audit, mode, clock, fault, cancellationToken, inferenceRegistry); }
        catch (IOException) { throw Error(GatewayPersistenceFailure.StorageFailed); }
        catch (UnauthorizedAccessException) { throw Error(GatewayPersistenceFailure.InsecureStorage); }
        catch (CryptographicException) { throw Error(GatewayPersistenceFailure.KeyProtectionFailed); }
    }

    [SupportedOSPlatform("windows")]
    private static DurableGatewayHost OpenWindows(string directory, string? hostId, GatewayOrigin origin,
        IEnumerable<IGatewayWorker> workers, IGatewayAuditSink audit, HostOpenMode mode,
        TimeProvider clock, Action<StoreStep>? fault, CancellationToken cancellationToken,
        GatewayInferenceRouteRegistry inferenceRegistry)
    {
        WindowsAuthorityStore? store = null;
        X509Certificate2? certificate = null;
        GatewayCredentialStore? credentials = null;
        try
        {
            var now = clock.GetUtcNow();
            if (now.Offset != TimeSpan.Zero || now <= DateTimeOffset.MinValue ||
                now > DateTimeOffset.MaxValue - TimeSpan.FromDays(90))
                throw Error(GatewayPersistenceFailure.InvalidState);
            if (mode == HostOpenMode.Create)
            {
                using var generated = HostCertificate.Create(now);
                var bytes = generated.Export(X509ContentType.Pkcs12);
                try { store = WindowsAuthorityStore.Create(directory, hostId!, bytes, now, fault, clock); }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
            else
                store = WindowsAuthorityStore.Open(directory, now, fault);
            var encoded = store.CopyCertificate();
            try { certificate = HostCertificate.Load(encoded); }
            finally { CryptographicOperations.ZeroMemory(encoded); }
            cancellationToken.ThrowIfCancellationRequested();
            var identity = GatewayHostIdentity.FromCertificate(store.HostId, certificate);
            credentials = new(identity, clock, store.Initial, store, store.InitialSameBoot);
            store.Initial.Dispose();
            var server = new GatewayServer(identity, origin, workers, audit, clock, null, null, credentials,
                inferenceRegistry: inferenceRegistry);
            var host = new DurableGatewayHost(origin, identity, server, store, certificate, clock, store.ReplaceCertificate);
            try { host.SelectCertificate(mode == HostOpenMode.RenewCertificate); }
            catch
            {
                host.DisposeCertificates();
                throw;
            }
            return host;
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
            var binding = new GatewayTlsBinding(origin!, Identity!, SelectCertificate(), clock)
            {
                CertificateSelector = () => SelectCertificate()
            };
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
        Local(() => server!.Pairing.RevokeDevice(deviceId, cancellationToken), cancellationToken);

    public IReadOnlyList<GatewayDeviceRegistration> ListRegistrations(CancellationToken cancellationToken = default) =>
        Local(() => server!.Credentials.ListRegistrations(), cancellationToken);

    private X509Certificate2 SelectCertificate(bool forceRenewal = false)
    {
        lock (certificateGate)
        {
            if (closed || certificate is null)
                throw Error(GatewayPersistenceFailure.Closed);
            server!.Credentials.ObserveTime(clock);
            var now = clock.GetUtcNow();
            if (forceRenewal || now >= new DateTimeOffset(certificate.NotAfter.ToUniversalTime()).AddDays(-30))
            {
                server.Credentials.CommitMaintenance(checkpoint =>
                {
                    using var key = certificate.GetECDsaPrivateKey();
                    using var generated = HostCertificate.Create(checkpoint.ObservedAt, key);
                    var bytes = generated.Export(X509ContentType.Pkcs12);
                    X509Certificate2? next = null;
                    try
                    {
                        next = HostCertificate.Load(bytes);
                        _ = new GatewayTlsBinding(origin!, Identity!, next, clock);
                        replaceCertificate!(bytes, checkpoint);
                        retiredCertificates.Add(certificate);
                        certificate = next;
                        next = null;
                    }
                    finally
                    {
                        next?.Dispose();
                        CryptographicOperations.ZeroMemory(bytes);
                    }
                });
            }
            _ = new GatewayTlsBinding(origin!, Identity!, certificate, clock);
            return certificate;
        }
    }

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
                DisposeCertificates();
                storage!.Dispose();
                closed = true;
            }
        }
        finally { gate.Release(); }
    }

    private void DisposeCertificates()
    {
        lock (certificateGate)
        {
            certificate?.Dispose();
            foreach (var previous in retiredCertificates)
                previous.Dispose();
            retiredCertificates.Clear();
        }
    }

    public override string ToString() => nameof(DurableGatewayHost);
}
