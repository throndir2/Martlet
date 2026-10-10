using System.Net;
using System.Security.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Martlet.Gateway;

public interface IGatewayListenerFactory
{
    ValueTask<GatewayListenerHandle> StartAsync(
        GatewayTlsBinding binding,
        RequestDelegate application,
        CancellationToken cancellationToken);
}

public abstract class GatewayListenerHandle : IAsyncDisposable
{
    public abstract GatewayOrigin Origin { get; }
    public abstract ValueTask DisposeAsync();
}

public sealed class GatewayServer
{
    private readonly GatewayHostIdentity identity;
    private readonly GatewayOrigin origin;
    private readonly GatewayHttpApplication application;
    private readonly GatewayInferenceRouteRegistry inference;
    private int started;

    public GatewayCredentialStore Credentials { get; }
    public GatewayPairingService Pairing { get; }

    /// <summary>Host-reported hardware served to paired desktops at /martlet/v1/machine; null when not collected.</summary>
    public GatewayMachineReport? Machine
    {
        get => application.Machine;
        set => application.Machine = value is null || value.IsValid() ? value : throw new ArgumentException("Invalid machine report.", nameof(value));
    }

    /// <summary>Keeps this host's copy of the shared cluster plan (served at /martlet/v1/cluster) in <paramref name="storage"/>
    /// and loads the copy saved there.</summary>
    public void AttachClusterStorage(IGatewayClusterStorage storage) => application.Cluster.Attach(storage);

    /// <summary>Keeps this host's copy of the shared voice list (served at /martlet/v1/voices) in <paramref name="storage"/>
    /// and loads the copy saved there.</summary>
    public void AttachVoiceStorage(IGatewayVoiceStorage storage) => application.Voices.Attach(storage);

    /// <summary>Keeps this host's copy of the voices Martlet speaks with and their recordings (served at
    /// /martlet/v1/speaking-voices) in <paramref name="storage"/> and loads what was saved there.</summary>
    public void AttachSpeakingVoiceStorage(IGatewaySpeakingVoiceStorage storage) => application.SpeakingVoices.Attach(storage);

    /// <summary>Keeps this host's copy of the character models the owner added and their pieces (served at
    /// /martlet/v1/character-models) in <paramref name="storage"/> and loads what was saved there.</summary>
    public void AttachCharacterModelStorage(IGatewayCharacterModelStorage storage) => application.CharacterModels.Attach(storage);

    /// <summary>Keeps this host's copy of Martlet's creations and their pieces (served at /martlet/v1/creations) in
    /// <paramref name="storage"/> and loads what was saved there.</summary>
    public void AttachCreationStorage(IGatewayCreationStorage storage) => application.Creations.Attach(storage);

    /// <summary>Keeps this host's creation list of each account (served at /martlet/v1/creations/accounts/{32 hex}) in
    /// <paramref name="storage"/> and loads the lists saved there. Attach it before <see cref="AttachCreationStorage"/>, which
    /// keeps the pieces of every list.</summary>
    public void AttachAccountCreationStorage(IGatewayAccountCreationStorage storage) => application.Creations.AttachAccounts(storage);

    /// <summary>The accounts this host keeps a creation list for.</summary>
    public IReadOnlyList<Guid> CreationAccounts => application.Creations.Accounts;

    /// <summary>Keeps this host's shared Home Assistant connection (served at /martlet/v1/home-assistant) in
    /// <paramref name="storage"/> and loads the copy saved there.</summary>
    public void AttachHomeAssistantStorage(IGatewayHomeAssistantStorage storage) => application.HomeAssistant.Attach(storage);

    /// <summary>Keeps this host's copy of the owner's shared settings (served at /martlet/v1/settings, API keys included) in
    /// <paramref name="storage"/> and loads the copy saved there.</summary>
    public void AttachSettingsStorage(IGatewaySettingsStorage storage) => application.Settings.Attach(storage);

    /// <summary>Keeps this host's copy of everything Martlet remembers (served at /martlet/v1/memories) in
    /// <paramref name="storage"/> and loads the copy saved there.</summary>
    public void AttachMemoryStorage(IGatewayMemoryStorage storage) => application.Memories.Attach(storage);

    /// <summary>Keeps this host's copy of the household's account directory (served at /martlet/v1/accounts) in
    /// <paramref name="storage"/> and loads the copy saved there.</summary>
    public void AttachAccountStorage(IGatewayAccountStorage storage) => application.Accounts.Attach(storage);

    /// <summary>Keeps this host's memory spaces (served at /martlet/v1/memories/spaces/{space}) in <paramref name="storage"/>
    /// and loads the spaces saved there.</summary>
    public void AttachMemorySpaceStorage(IGatewayMemorySpaceStorage storage) => application.MemorySpaces.Attach(storage);

    /// <summary>The IDs of the memory spaces this host keeps.</summary>
    public IReadOnlyList<string> MemorySpaces => application.MemorySpaces.Spaces;

    /// <summary>Decides which paired device may read or write which memory space (default: the account directory's rules,
    /// GatewayMemorySpaceAccess.cs).</summary>
    internal GatewayMemorySpaceAccess MemorySpaceAccess
    {
        get => application.MemorySpaces.Access;
        set => application.MemorySpaces.Access = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>Keeps this host's copy of the network's API keys (served to paired desktops at /martlet/v1/api-keys and
    /// checked for every Authorization: Bearer request) in <paramref name="storage"/> and loads the copy saved there.</summary>
    public void AttachApiKeyStorage(IGatewayApiKeyStorage storage) => application.ApiKeys.Attach(storage);

    /// <summary>Keeps the Martlet network roster this host accepted (served at /martlet/v1/network) in
    /// <paramref name="storage"/> and loads the roster saved there.</summary>
    public void AttachNetworkStorage(IGatewayNetworkStorage storage) => application.Network.Attach(storage);

    /// <summary>This host's network state: "unbound", "bound" or "removed", and the ID of its network (null when unbound).</summary>
    public (string State, string? NetworkId) NetworkState => (application.Network.State, application.Network.Roster?.NetworkId);

    /// <summary>Keeps this host's sign-in settings (owner account, providers, allowed identities; served without secrets at
    /// /martlet/v1/signin) in <paramref name="storage"/>. Without it nobody can sign in here.</summary>
    public void AttachSignInStorage(IGatewaySignInStorage storage) => application.SignIn.Attach(storage);

    /// <summary>Null when someone can sign in to this host (an owner account, or a provider with an allowed identity);
    /// otherwise why not ("signin.not_set_up", "signin.no_allowed_identity"). A host is reachable from outside home only
    /// while this is null.</summary>
    public string? SignInBlockedReason => application.SignIn.BlockedReason();

    /// <summary>Sends this host's sign-in provider calls (discovery, keys, code exchange) through <paramref name="handler"/>
    /// instead of the network: for rehearsals with an in-process issuer only.</summary>
    public void UseSignInProviderHandler(HttpMessageHandler handler) =>
        application.SignIn.Providers = new GatewaySignInProviders(application.Clock, handler).Create;

    /// <summary>The sign-in service itself, for tests that plug in their own provider.</summary>
    internal GatewaySignInService SignIn => application.SignIn;

    /// <summary>Keeps the commands paired computers send this host (served at /martlet/v1/commands) in
    /// <paramref name="storage"/> and accepts <paramref name="agentToken"/> (32 random bytes, base64url, also written where
    /// only the host computer itself can read it) from the Martlet app that runs them there.</summary>
    public void AttachCommandStorage(IGatewayCommandStorage storage, string agentToken) =>
        application.Commands.Attach(storage, agentToken, application.Now);

    /// <summary>Keeps this host's log (its own activity and every computer's lines the owner's desktops share with it;
    /// served at /martlet/v1/logs) in <paramref name="storage"/> and loads the log saved there.</summary>
    public void AttachLogStorage(IGatewayLogStorage storage) => application.Logs.Attach(storage);

    /// <summary>Adds a line to this host's own log (for example a configuration problem the host found at start).</summary>
    public void RecordActivity(string level, string message) => application.Logs.Own(level, message);

    /// <summary>Rate limits, lockouts and the audit log of authentication and pairing decisions (also served to paired
    /// desktops at /martlet/v1/security/audit).</summary>
    public GatewayRequestGuard Guard => application.Guard;

    /// <summary>The owner's choices for reaching this host from outside home (see docs/NETWORK.md).</summary>
    public GatewayExposure Exposure
    {
        get => application.Guard.Exposure;
        set => application.Guard.Exposure = value;
    }

    public GatewayServer(
        GatewayHostIdentity identity,
        GatewayOrigin origin,
        IEnumerable<IGatewayWorker> workers,
        IGatewayAuditSink audit,
        TimeProvider? clock = null,
        IGatewayCrypto? crypto = null,
        TimeSpan? pairingWindow = null,
        IEnumerable<IGatewayInferenceWorker>? inferenceWorkers = null)
        : this(identity, origin, workers, audit, clock, crypto, pairingWindow, null, inferenceWorkers)
    {
    }

    internal GatewayServer(
        GatewayHostIdentity identity,
        GatewayOrigin origin,
        IEnumerable<IGatewayWorker> workers,
        IGatewayAuditSink audit,
        TimeProvider? clock,
        IGatewayCrypto? crypto,
        TimeSpan? pairingWindow,
        GatewayCredentialStore? ownedCredentials,
        IEnumerable<IGatewayInferenceWorker>? inferenceWorkers = null,
        GatewayInferenceRouteRegistry? inferenceRegistry = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(workers);
        ArgumentNullException.ThrowIfNull(audit);
        identity.Validate();
        this.identity = identity;
        this.origin = origin;
        var effectiveClock = clock ?? TimeProvider.System;
        var effectiveCrypto = crypto ?? new SystemGatewayCrypto();
        Credentials = ownedCredentials ?? new(identity, effectiveClock, effectiveCrypto);
        Pairing = new(identity, origin, Credentials, effectiveClock, effectiveCrypto, pairingWindow);
        inference = inferenceRegistry ?? new(inferenceWorkers ?? [], effectiveClock);
        application = new(identity, Pairing, Credentials, Credentials, new(workers),
            inference,
            effectiveClock, effectiveCrypto, audit);
    }

    public async ValueTask<GatewayListenerHandle> StartAsync(
        GatewayTlsBinding binding,
        IGatewayListenerFactory listenerFactory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(listenerFactory);
        GatewayRules.Require(binding.Identity == identity &&
            binding.Origin.CanonicalOrigin == origin.CanonicalOrigin, "binding.unsafe");
        GatewayRules.Require(Interlocked.CompareExchange(ref started, 1, 0) == 0, "request.invalid");
        // Account attestations are signed with the key the roster pins: this listener's TLS key (renewals keep the key).
        application.SigningCertificate = binding.CertificateSelector ?? (() => binding.Certificate);
        try
        {
            var listener = await listenerFactory.StartAsync(
                binding, application.InvokeAsync, cancellationToken).ConfigureAwait(false);
            application.Logs.Own(Martlet.Core.Logs.LogLevels.Info,
                $"Gateway {identity.HostId} started on {listener.Origin.CanonicalOrigin}" +
                (typeof(GatewayServer).Assembly.GetName().Version?.ToString(3) is { } version ? $" (Martlet {version})." : "."));
            application.LogGpuMap();
            return new InferenceListener(listener, inference, application.Logs);
        }
        catch
        {
            Interlocked.Exchange(ref started, 0);
            throw;
        }

    }

    private sealed class InferenceListener(
        GatewayListenerHandle listener, GatewayInferenceRouteRegistry inference, GatewayLogStore logs) : GatewayListenerHandle
    {
        private int stopping;
        public override GatewayOrigin Origin => listener.Origin;
        public override async ValueTask DisposeAsync()
        {
            var first = Interlocked.Exchange(ref stopping, 1) == 0;
            if (first) logs.Own(Martlet.Core.Logs.LogLevels.Info, "Gateway stopping.");
            var closing = inference.CloseAsync().AsTask();
            await listener.DisposeAsync().ConfigureAwait(false);
            await closing.ConfigureAwait(false);
            if (first) logs.Flush();
        }
    }
}

public sealed class KestrelGatewayListenerFactory : IGatewayListenerFactory
{
    public async ValueTask<GatewayListenerHandle> StartAsync(
        GatewayTlsBinding binding,
        RequestDelegate application,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(application);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(KestrelGatewayListenerFactory).Assembly.FullName,
            Args = []
        });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Limits.MaxRequestBodySize = GatewayHttpApplication.MaximumPairingRequestBytes;
            options.Limits.MaxConcurrentConnections = 64;
            options.Limits.MaxRequestHeaderCount = 32;
            options.Limits.MaxRequestHeadersTotalSize = 16_384;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(5);
            options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(30);
            options.Listen(binding.ListenAddress ?? binding.Origin.Address, binding.Origin.Port, listen =>
            {
                listen.Protocols = HttpProtocols.Http1;
                listen.UseHttps(new HttpsConnectionAdapterOptions
                {
                    ServerCertificate = binding.Certificate,
                    ServerCertificateSelector = binding.CertificateSelector is { } select
                        ? (_, _) => select() : null,
                    SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    ClientCertificateMode = ClientCertificateMode.NoCertificate
                });
            });
        });
        var app = builder.Build();
        app.Run(application);
        try
        {
            await app.StartAsync(cancellationToken).ConfigureAwait(false);
            var addresses = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()?.Addresses;
            var expected = binding.ListenAddress is { } any
                ? new UriBuilder("https", any.ToString(), binding.Origin.Port).Uri.GetLeftPart(UriPartial.Authority)
                : binding.Origin.CanonicalOrigin;
            GatewayRules.Require(addresses is { Count: 1 } &&
                addresses.Single() == expected, "binding.unsafe");
            return new KestrelGatewayListenerHandle(binding.Origin, app);
        }
        catch
        {
            await app.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private sealed class KestrelGatewayListenerHandle(
        GatewayOrigin origin,
        WebApplication application) : GatewayListenerHandle
    {
        private int disposed;
        public override GatewayOrigin Origin { get; } = origin;

        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
                return;
            await application.StopAsync().ConfigureAwait(false);
            await application.DisposeAsync().ConfigureAwait(false);
        }
    }
}
