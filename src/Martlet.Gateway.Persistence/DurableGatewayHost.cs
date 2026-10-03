using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using static Martlet.Gateway.Persistence.PersistenceFailure;

namespace Martlet.Gateway.Persistence;

public enum LocalGatewayDecision { No, Enable }
internal enum HostOpenMode { Create, Open, RenewCertificate, Rebind }

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

    public static DurableGatewayHost CreateNewForBinding(string directory, string hostId, GatewayHostBinding binding,
        GatewayStorageBackend storageBackend, IEnumerable<IGatewayWorker> workers, IGatewayAuditSink audit,
        LocalGatewayDecision decision = LocalGatewayDecision.No, CancellationToken cancellationToken = default,
        IEnumerable<IGatewayInferenceWorker>? inferenceWorkers = null) =>
        decision == LocalGatewayDecision.No ? new() : Listening(binding,
        Open(directory, hostId, binding.Origin, workers, audit, decision, HostOpenMode.Create,
            TimeProvider.System, cancellationToken: cancellationToken, inferenceWorkers: inferenceWorkers,
            storageBackend: storageBackend, explicitBinding: true));

    public static DurableGatewayHost OpenExistingForBinding(string directory, GatewayHostBinding binding,
        GatewayStorageBackend storageBackend, GatewayHostIdentity expectedIdentity,
        IEnumerable<IGatewayWorker> workers, IGatewayAuditSink audit,
        LocalGatewayDecision decision = LocalGatewayDecision.No, CancellationToken cancellationToken = default,
        IEnumerable<IGatewayInferenceWorker>? inferenceWorkers = null) =>
        decision == LocalGatewayDecision.No ? new() : Listening(binding,
        Open(directory, null, binding.Origin, workers, audit, decision, HostOpenMode.Open,
            TimeProvider.System, cancellationToken: cancellationToken, inferenceWorkers: inferenceWorkers,
            storageBackend: storageBackend, explicitBinding: true,
            expectedIdentity: expectedIdentity ?? throw new ArgumentNullException(nameof(expectedIdentity))));

    public static DurableGatewayHost OpenForLocalAdministration(string directory, string expectedHostId,
        GatewayHostBinding binding, GatewayStorageBackend storageBackend, IEnumerable<IGatewayWorker> workers,
        IGatewayAuditSink audit, LocalGatewayDecision decision = LocalGatewayDecision.No,
        CancellationToken cancellationToken = default, IEnumerable<IGatewayInferenceWorker>? inferenceWorkers = null) =>
        decision == LocalGatewayDecision.No ? new() : Listening(binding,
        Open(directory, expectedHostId ?? throw new ArgumentNullException(nameof(expectedHostId)), binding.Origin, workers, audit, decision, HostOpenMode.Open,
            TimeProvider.System, cancellationToken: cancellationToken, inferenceWorkers: inferenceWorkers,
            storageBackend: storageBackend, explicitBinding: true));

    public static DurableGatewayHost RebindForLocalHost(string directory, GatewayHostBinding binding,
        GatewayStorageBackend storageBackend, GatewayHostIdentity expectedIdentity,
        IEnumerable<IGatewayWorker> workers, IGatewayAuditSink audit,
        LocalGatewayDecision decision = LocalGatewayDecision.No, CancellationToken cancellationToken = default) =>
        decision == LocalGatewayDecision.No ? new() : Listening(binding,
        Open(directory, null, binding.Origin, workers, audit, decision, HostOpenMode.Rebind,
            TimeProvider.System, cancellationToken: cancellationToken, storageBackend: storageBackend,
            explicitBinding: true, expectedIdentity: expectedIdentity ?? throw new ArgumentNullException(nameof(expectedIdentity))));

    private IPAddress? listenAddress;

    private static DurableGatewayHost Listening(GatewayHostBinding binding, DurableGatewayHost host)
    {
        host.listenAddress = binding.ListenAddress;
        return host;
    }

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

    public static DurableGatewayHost CreateNew(string directory, string hostId, GatewayOrigin origin,
        GatewayStorageBackend storageBackend, IEnumerable<IGatewayWorker> workers, IGatewayAuditSink audit,
        LocalGatewayDecision decision = LocalGatewayDecision.No, CancellationToken cancellationToken = default,
        IEnumerable<IGatewayInferenceWorker>? inferenceWorkers = null) =>
        Open(directory, hostId, origin, workers, audit, decision, HostOpenMode.Create,
            TimeProvider.System, null, cancellationToken, inferenceWorkers, storageBackend);

    public static DurableGatewayHost OpenExisting(string directory, GatewayOrigin origin,
        GatewayStorageBackend storageBackend, IEnumerable<IGatewayWorker> workers, IGatewayAuditSink audit,
        LocalGatewayDecision decision = LocalGatewayDecision.No, CancellationToken cancellationToken = default,
        IEnumerable<IGatewayInferenceWorker>? inferenceWorkers = null) =>
        Open(directory, null, origin, workers, audit, decision, HostOpenMode.Open,
            TimeProvider.System, null, cancellationToken, inferenceWorkers, storageBackend);

    public static DurableGatewayHost RenewCertificateForLocalHost(string directory, GatewayOrigin origin,
        GatewayStorageBackend storageBackend, IEnumerable<IGatewayWorker> workers, IGatewayAuditSink audit,
        LocalGatewayDecision decision = LocalGatewayDecision.No, CancellationToken cancellationToken = default,
        IEnumerable<IGatewayInferenceWorker>? inferenceWorkers = null) =>
        Open(directory, null, origin, workers, audit, decision, HostOpenMode.RenewCertificate,
            TimeProvider.System, null, cancellationToken, inferenceWorkers, storageBackend);

    internal static DurableGatewayHost Open(string directory, string? hostId, GatewayOrigin origin,
        IEnumerable<IGatewayWorker> workers, IGatewayAuditSink audit, LocalGatewayDecision decision,
        HostOpenMode mode, TimeProvider clock, Action<StoreStep>? fault = null,
        CancellationToken cancellationToken = default,
        IEnumerable<IGatewayInferenceWorker>? inferenceWorkers = null,
        GatewayStorageBackend storageBackend = GatewayStorageBackend.WindowsCurrentUserDpapi,
        ILinuxFileSystem? linuxFileSystem = null, bool explicitBinding = false,
        GatewayHostIdentity? expectedIdentity = null)
    {
        if (decision == LocalGatewayDecision.No)
            return new();
        if (decision != LocalGatewayDecision.Enable)
            throw Error(GatewayPersistenceFailure.InvalidState);
        if (!Enum.IsDefined(storageBackend))
            throw Error(GatewayPersistenceFailure.InvalidState);
        if (storageBackend == GatewayStorageBackend.WindowsCurrentUserDpapi && !OperatingSystem.IsWindows() ||
            storageBackend == GatewayStorageBackend.LinuxServicePermissions &&
                !OperatingSystem.IsLinux() && linuxFileSystem is null)
            throw Error(GatewayPersistenceFailure.UnsupportedPlatform);
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(workers);
        ArgumentNullException.ThrowIfNull(audit);
        if (!explicitBinding && !IPAddress.IsLoopback(origin.Address))
            throw Error(GatewayPersistenceFailure.InvalidState);
        if (mode == HostOpenMode.Create)
            GatewayRules.Identifier(hostId!);
        cancellationToken.ThrowIfCancellationRequested();
        var inferenceRegistry = new GatewayInferenceRouteRegistry(inferenceWorkers ?? [], clock);
        try { return OpenCore(directory, hostId, origin, workers, audit, mode, clock, fault,
            cancellationToken, inferenceRegistry, storageBackend, linuxFileSystem, expectedIdentity); }
        catch (IOException) { throw Error(GatewayPersistenceFailure.StorageFailed); }
        catch (UnauthorizedAccessException) { throw Error(GatewayPersistenceFailure.InsecureStorage); }
        catch (CryptographicException) { throw Error(GatewayPersistenceFailure.KeyProtectionFailed); }
        catch (DllNotFoundException) { throw Error(GatewayPersistenceFailure.UnsupportedPlatform); }
        catch (EntryPointNotFoundException) { throw Error(GatewayPersistenceFailure.UnsupportedPlatform); }
    }

    private static DurableGatewayHost OpenCore(string directory, string? hostId, GatewayOrigin origin,
        IEnumerable<IGatewayWorker> workers, IGatewayAuditSink audit, HostOpenMode mode,
        TimeProvider clock, Action<StoreStep>? fault, CancellationToken cancellationToken,
        GatewayInferenceRouteRegistry inferenceRegistry, GatewayStorageBackend storageBackend,
        ILinuxFileSystem? linuxFileSystem, GatewayHostIdentity? expectedIdentity)
    {
        AuthorityStore? store = null;
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
                using var generated = HostCertificate.Create(now, privateAddress:
                    IPAddress.IsLoopback(origin.Address) ? null : origin.Address);
                var bytes = generated.Export(X509ContentType.Pkcs12);
                try { store = OpenStore(directory, hostId, bytes, now, mode, clock, fault, storageBackend, linuxFileSystem); }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
            else
                store = OpenStore(directory, hostId, null, now, mode, clock, fault, storageBackend, linuxFileSystem);
            var encoded = store.CopyCertificate();
            try { certificate = HostCertificate.Load(encoded); }
            finally { CryptographicOperations.ZeroMemory(encoded); }
            cancellationToken.ThrowIfCancellationRequested();
            var identity = GatewayHostIdentity.FromCertificate(store.HostId, certificate);
            if (hostId is not null && identity.HostId != hostId ||
                expectedIdentity is not null && identity != expectedIdentity)
                throw Error(GatewayPersistenceFailure.InvalidState);
            credentials = new(identity, clock, store.Initial, store, store.InitialSameBoot);
            store.Initial.Dispose();
            var server = new GatewayServer(identity, origin, workers, audit, clock, null, null, credentials,
                inferenceRegistry: inferenceRegistry);
            var host = new DurableGatewayHost(origin, identity, server, store, certificate, clock, store.ReplaceCertificate);
            try { host.SelectCertificate(mode is HostOpenMode.RenewCertificate or HostOpenMode.Rebind,
                mode == HostOpenMode.Rebind); }
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

    private static AuthorityStore OpenStore(string directory, string? hostId, byte[]? certificate,
        DateTimeOffset now, HostOpenMode mode, TimeProvider clock, Action<StoreStep>? fault,
        GatewayStorageBackend backend, ILinuxFileSystem? linuxFileSystem)
    {
        if (backend == GatewayStorageBackend.WindowsCurrentUserDpapi && OperatingSystem.IsWindows())
            return mode == HostOpenMode.Create
                ? WindowsAuthorityStore.Create(directory, hostId!, certificate!, now, fault, clock).Store
                : WindowsAuthorityStore.Open(directory, now, fault).Store;
        if (backend != GatewayStorageBackend.LinuxServicePermissions)
            throw Error(GatewayPersistenceFailure.UnsupportedPlatform);
        var fileSystem = linuxFileSystem ?? new LinuxFileSystem();
        var boot = fileSystem.BootIdentity();
        var owned = LinuxOwnedDirectory.Open(directory, mode == HostOpenMode.Create, fileSystem);
        var envelope = new LinuxPermissionEnvelope();
        return mode == HostOpenMode.Create
            ? AuthorityStore.Create(owned, envelope, boot, hostId!, certificate!, now, fault, clock)
            : AuthorityStore.Open(owned, envelope, boot, now, fault);
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
                CertificateSelector = () => SelectCertificate(),
                ListenAddress = listenAddress
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

    /// <summary>Opens a short-code pairing window (the host shows its address and an XXXX-XXXX code to type on a desktop).</summary>
    public GatewayCodePairingCard OpenCodePairing(GatewayCodePairingApproval approval,
        CancellationToken cancellationToken = default) =>
        Local(() => server!.Pairing.OpenCodeWindow(approval), cancellationToken);

    public IssuedDeviceCredential Rotate(string credentialId, TimeSpan overlap,
        CancellationToken cancellationToken = default) =>
        Local(() => server!.Credentials.Rotate(credentialId, overlap, cancellationToken), cancellationToken);

    public bool RevokeCredential(string credentialId, CancellationToken cancellationToken = default) =>
        Local(() => server!.Credentials.RevokeCredential(credentialId, cancellationToken), cancellationToken);

    public int RevokeDevice(string deviceId, CancellationToken cancellationToken = default) =>
        Local(() => server!.Pairing.RevokeDevice(deviceId, cancellationToken), cancellationToken);

    public IReadOnlyList<GatewayDeviceRegistration> ListRegistrations(CancellationToken cancellationToken = default) =>
        Local(() => server!.Credentials.ListRegistrations(), cancellationToken);

    private X509Certificate2 SelectCertificate(bool forceRenewal = false, bool rebind = false)
    {
        lock (certificateGate)
        {
            if (closed || certificate is null)
                throw Error(GatewayPersistenceFailure.Closed);
            server!.Credentials.ObserveTime(clock);
            if (!rebind && !certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single()
                .EnumerateIPAddresses().Contains(origin!.Address))
                throw Error(GatewayPersistenceFailure.InvalidState);
            var now = clock.GetUtcNow();
            if (forceRenewal || now >= new DateTimeOffset(certificate.NotAfter.ToUniversalTime()).AddDays(-30))
            {
                server.Credentials.CommitMaintenance(checkpoint =>
                {
                    using var key = certificate.GetECDsaPrivateKey();
                    var address = rebind
                        ? IPAddress.IsLoopback(origin!.Address) ? null : origin.Address
                        : HostCertificate.PrivateAddress(certificate);
                    using var generated = HostCertificate.Create(checkpoint.ObservedAt, key, address);
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

    /// <summary>Publishes host-reported hardware to paired desktops (read-only; grants no authority).</summary>
    public void PublishMachine(GatewayMachineReport? report)
    {
        RequireOpen();
        server!.Machine = report;
    }

    /// <summary>Keeps the shared cluster plan paired desktops sync through this host in <paramref name="storage"/>.</summary>
    public void AttachCluster(IGatewayClusterStorage storage)
    {
        RequireOpen();
        server!.AttachClusterStorage(storage);
    }

    /// <summary>Keeps the shared voice list paired desktops sync through this host in <paramref name="storage"/>.</summary>
    public void AttachVoices(IGatewayVoiceStorage storage)
    {
        RequireOpen();
        server!.AttachVoiceStorage(storage);
    }

    /// <summary>Keeps the voices Martlet speaks with, and their recordings, paired desktops sync through this host in
    /// <paramref name="storage"/>.</summary>
    public void AttachSpeakingVoices(IGatewaySpeakingVoiceStorage storage)
    {
        RequireOpen();
        server!.AttachSpeakingVoiceStorage(storage);
    }

    /// <summary>Keeps the shared Home Assistant connection paired desktops sync through this host in <paramref name="storage"/>.</summary>
    public void AttachHomeAssistant(IGatewayHomeAssistantStorage storage)
    {
        RequireOpen();
        server!.AttachHomeAssistantStorage(storage);
    }

    /// <summary>Keeps the network's API keys paired desktops sync through this host in <paramref name="storage"/>.</summary>
    public void AttachApiKeys(IGatewayApiKeyStorage storage)
    {
        RequireOpen();
        server!.AttachApiKeyStorage(storage);
    }

    /// <summary>Keeps the Martlet network roster this host accepted in <paramref name="storage"/>.</summary>
    public void AttachNetwork(IGatewayNetworkStorage storage)
    {
        RequireOpen();
        server!.AttachNetworkStorage(storage);
    }

    /// <summary>This host's network state ("unbound", "bound" or "removed") and network ID.</summary>
    public (string State, string? NetworkId) NetworkState
    {
        get
        {
            RequireOpen();
            return server!.NetworkState;
        }
    }

    /// <summary>Keeps the commands paired computers send through this host in <paramref name="storage"/> and accepts
    /// <paramref name="agentToken"/> from the Martlet app on this host that runs them.</summary>
    public void AttachCommands(IGatewayCommandStorage storage, string agentToken)
    {
        RequireOpen();
        server!.AttachCommandStorage(storage, agentToken);
    }

    /// <summary>Keeps this host's log (its own activity and the lines desktops send it as the log host) in
    /// <paramref name="storage"/>.</summary>
    public void AttachLogs(IGatewayLogStorage storage)
    {
        RequireOpen();
        server!.AttachLogStorage(storage);
    }

    /// <summary>Adds a line to this host's own log, which paired desktops can read.</summary>
    public void RecordActivity(string level, string message)
    {
        if (server is not null && !closed) server.RecordActivity(level, message);
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
