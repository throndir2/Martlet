using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using Martlet.Core.Network;
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

    /// <summary>Keeps this host's copy of each account's settings (served at /martlet/v1/settings/accounts/{account}) in
    /// <paramref name="storage"/> and loads the copies saved there.</summary>
    public void AttachAccountSettingsStorage(IGatewayAccountSettingsStorage storage) => application.AccountSettings.Attach(storage);

    /// <summary>The accounts (32 lowercase hex) this host keeps settings for.</summary>
    public IReadOnlyList<string> AccountSettings => application.AccountSettings.Accounts;

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

    /// <summary>Keeps this host's copies of each account's record of conversations (served at
    /// /martlet/v1/conversations/accounts/{32 hex}/{yyyy-MM}) in <paramref name="storage"/> and loads the months saved there.</summary>
    public void AttachConversationStorage(IGatewayConversationStorage storage) => application.Conversations.Attach(storage);

    /// <summary>The keys (<c>&lt;32 hex account&gt;-&lt;yyyy-MM&gt;</c>) of the months of conversations this host keeps, in order.</summary>
    public IReadOnlyList<string> ConversationRecords => application.Conversations.Keys;

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

    /// <summary>For tests: the other hosts this host's OpenAI-compatible endpoint sends requests on to (instead of its roster's),
    /// given as host ID, home origin and TLS key fingerprint.</summary>
    internal void UseOpenAiPeers(Func<IReadOnlyList<(string HostId, string Origin, string Spki)>> peers) =>
        application.OpenAiPeers = () => peers().Select(p => new GatewayOpenAiPeer(p.HostId, p.Origin, p.Spki)).ToArray();

    /// <summary>For tests: this host's copy of the network's API keys.</summary>
    internal Martlet.Core.Access.ApiKeyList MergeApiKeys(Martlet.Core.Access.ApiKeyList keys) => application.ApiKeys.Merge(keys);

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
        set
        {
            application.Guard.Exposure = value;
            overlay?.Wake();
        }
    }

    /// <summary>The addresses ("ip:port") this host listens on besides its home origin: its overlay network addresses while
    /// it has outside addresses (empty for a host inside a container or on loopback).</summary>
    public IReadOnlyList<string> OutsideListeners => application.OutsideListeners();

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
            // A host listening on its exact home address (not inside a container, whose runtime publishes the port) follows its
            // computer to another home address and answers desktops looking for it there (docs/NETWORK.md).
            var follow = listenerFactory is KestrelGatewayListenerFactory && binding.ListenAddress is null &&
                !IPAddress.IsLoopback(binding.Origin.Address);
            GatewayListenerHandle listener;
            var away = false;
            try { listener = await listenerFactory.StartAsync(binding, application.InvokeAsync, cancellationToken).ConfigureAwait(false); }
            catch (Exception error) when (follow && AddressMissing(error))
            {
                // This computer doesn't have the home address now: GatewayHomeListeners listens where it is.
                away = true;
                listener = new GatewayAwayListenerHandle(binding.Origin);
            }
            application.Logs.Own(Martlet.Core.Logs.LogLevels.Info,
                $"Gateway {identity.HostId} started on {listener.Origin.CanonicalOrigin}" +
                (typeof(GatewayServer).Assembly.GetName().Version?.ToString(3) is { } version ? $" (Martlet {version})." : "."));
            if (away)
                application.Logs.Own(Martlet.Core.Logs.LogLevels.Warn, $"This computer doesn't have its home address {binding.Origin.Address} " +
                    "now (the network gave it another one, or isn't connected yet), so the host listens on the address it has.");
            application.LogGpuMap();
            // A host listening on its exact home address also listens on its overlay addresses while it has outside addresses.
            var overlay = follow
                ? new GatewayOverlayListeners(binding, application.InvokeAsync, OutsideAddresses, GatewayOverlayListeners.LocalAddresses,
                    (level, message, repeatKey) => application.Logs.Own(level, message, repeatKey))
                : null;
            this.overlay = overlay;
            var home = follow
                ? new GatewayHomeListeners(binding, application.InvokeAsync, !away, GatewayOverlayListeners.LocalAddresses, HostFind.HomeAddresses,
                    (level, message, repeatKey) => application.Logs.Own(level, message, repeatKey))
                : null;
            this.home = home;
            finds = follow ? AnswerFinds() : null;
            application.OutsideListeners = overlay is null ? static () => [] : () => overlay.Listening;
            application.OutsideAddressesChanged = overlay is null ? null : overlay.Wake;
            return new InferenceListener(listener, inference, application.Logs, overlay, home, finds, application.CloseOpenAi);
        }
        catch
        {
            Interlocked.Exchange(ref started, 0);
            throw;
        }

    }

    private volatile GatewayOverlayListeners? overlay;
    private volatile GatewayHomeListeners? home;
    private HostFindResponder? finds;

    /// <summary>The home addresses this host listens on besides its home origin, after its computer got another address.</summary>
    public IReadOnlyList<string> MovedHomeListeners => home?.Listening ?? [];

    /// <summary>Whether this host answers desktops looking for it on the home network (<see cref="HostFind"/>).</summary>
    public bool AnswersFinds => finds is not null;

    /// <summary>Whether a listener couldn't start because this computer doesn't have the address (EADDRNOTAVAIL).</summary>
    internal static bool AddressMissing(Exception? error)
    {
        for (var depth = 0; error is not null && depth < 8; depth++, error = error.InnerException)
            if (error is SocketException { SocketErrorCode: SocketError.AddressNotAvailable }) return true;
        return false;
    }

    private HostFindResponder? AnswerFinds()
    {
        try
        {
            var responder = HostFindResponder.Start(() => [identity.HostId]);
            application.Logs.Own(Martlet.Core.Logs.LogLevels.Info, $"Answering on UDP {HostFind.Port} when your computers look for this host " +
                "on the home network (after its address changed).");
            return responder;
        }
        catch (SocketException error)
        {
            application.Logs.Own(Martlet.Core.Logs.LogLevels.Warn, $"Couldn't answer on UDP {HostFind.Port} ({error.SocketErrorCode}), so your " +
                "computers can't find this host by themselves if its address changes.", "find-port");
            return null;
        }
    }

    /// <summary>Every outside address of this host: those set on the host itself and those its roster entry lists.</summary>
    private IReadOnlyList<string> OutsideAddresses() => [.. application.Guard.Exposure.OutsideAddresses ?? [], .. application.Network.OwnAddresses];

    private sealed class InferenceListener(
        GatewayListenerHandle listener, GatewayInferenceRouteRegistry inference, GatewayLogStore logs,
        GatewayOverlayListeners? overlay, GatewayHomeListeners? home, HostFindResponder? finds, Action closeOpenAi) : GatewayListenerHandle
    {
        private int stopping;
        public override GatewayOrigin Origin => listener.Origin;
        public override async ValueTask DisposeAsync()
        {
            var first = Interlocked.Exchange(ref stopping, 1) == 0;
            if (first) logs.Own(Martlet.Core.Logs.LogLevels.Info, "Gateway stopping.");
            var closing = inference.CloseAsync().AsTask();
            finds?.Dispose();
            if (overlay is not null) await overlay.DisposeAsync().ConfigureAwait(false);
            if (home is not null) await home.DisposeAsync().ConfigureAwait(false);
            await listener.DisposeAsync().ConfigureAwait(false);
            await closing.ConfigureAwait(false);
            closeOpenAi();
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
        var app = Build(binding.ListenAddress ?? binding.Origin.Address, binding.Origin.Port, binding, application);
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

    /// <summary>A gateway listener on <paramref name="address"/>:<paramref name="port"/> with <paramref name="binding"/>'s TLS
    /// key and the gateway's limits (also used for the overlay listeners, <see cref="GatewayOverlayListeners"/>).</summary>
    internal static WebApplication Build(IPAddress address, int port, GatewayTlsBinding binding, RequestDelegate application)
    {
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
            options.Listen(address, port, listen =>
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
        return app;
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
