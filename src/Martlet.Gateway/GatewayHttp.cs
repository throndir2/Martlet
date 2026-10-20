using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Core.Logs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Martlet.Gateway;

/// <summary>A host's HTTPS application: the request guard, health, version, capabilities, status and pairing routes here,
/// and every other area of routes in its own handler (<see cref="GatewayRouteHandler"/>), which gets the stores of its area.
/// This class makes the shared stores, gives each handler the ones it needs and sends each request to its handler.</summary>
internal sealed class GatewayHttpApplication : GatewayRouteHandler
{
    private readonly IGatewayPairingExchange pairing;
    private readonly IGatewayAdmissionStatus admission;
    private readonly GatewayWorkerRegistry workers;
    private volatile GatewayMachineReport? machine;
    private readonly GatewayAccountRoutes accountRoutes;
    private readonly GatewayApiKeyRoutes apiKeyRoutes;
    private readonly GatewayCharacterModelRoutes characterModelRoutes;
    private readonly GatewayClusterRoutes clusterRoutes;
    private readonly GatewayCommandRoutes commandRoutes;
    private readonly GatewayConversationRoutes conversationRoutes;
    private readonly GatewayCreationRoutes creationRoutes;
    private readonly GatewayFrontDoorRoutes frontDoorRoutes;
    private readonly GatewayHomeAssistantRoutes homeAssistantRoutes;
    private readonly GatewayInferenceRoutes inferenceRoutes;
    private readonly GatewayLogRoutes logRoutes;
    private readonly GatewayMemoryRoutes memoryRoutes;
    private readonly GatewayNetworkRoutes networkRoutes;
    private readonly GatewayOpenAiRoutes openAiRoutes;
    private readonly GatewayPriorityRoutes priorityRoutes;
    private readonly GatewaySecurityAuditRoutes securityAuditRoutes;
    private readonly GatewaySettingsRoutes settingsRoutes;
    private readonly GatewaySignInRoutes signInRoutes;
    private readonly GatewaySpeakingVoiceRoutes speakingVoiceRoutes;
    private readonly GatewayVoiceRoutes voiceRoutes;

    internal GatewayMachineReport? Machine { get => machine; set => machine = value; }
    internal DateTimeOffset Now => clock.GetUtcNow();
    internal TimeProvider Clock => clock;

    internal GatewayHttpApplication(
        GatewayHostIdentity identity,
        IGatewayPairingExchange pairing,
        GatewayCredentialStore credentials,
        IGatewayAdmissionStatus admission,
        GatewayWorkerRegistry workers,
        GatewayInferenceRouteRegistry inference,
        TimeProvider clock,
        IGatewayCrypto crypto,
        IGatewayAuditSink audit) : base(CreateServices(identity, credentials, inference, clock, crypto, audit))
    {
        this.pairing = pairing;
        this.admission = admission;
        this.workers = workers;
        // Each signed request saves its nonce before it is admitted; a slow disk shows up here (issue #1005).
        credentials.SlowCommitObserved = elapsed => Logs.Own(LogLevels.Warn,
            $"Saving this host's pairings took {elapsed.TotalMilliseconds:0} ms; signed requests wait for this save.", "authority:slow-commit");
        Network = new(identity, credentials, clock, (level, message) => Logs.Own(level, message));
        // A host whose roster entry lists outside addresses is reachable from outside: the guard's limits apply to every source.
        Network.Changed = roster =>
        {
            Guard.Listed = roster?.Host(identity.HostId) is { Removed: false, Addresses.Count: > 0 };
            OutsideAddressesChanged?.Invoke();
        };
        var services = Services;
        var signIn = GatewaySignInRoutes.CreateService(services, credentials, Network);
        accountRoutes = new(services, Accounts, Network);
        apiKeyRoutes = new(services);
        characterModelRoutes = new(services, CharacterModels);
        clusterRoutes = new(services, Cluster);
        commandRoutes = new(services, Commands);
        conversationRoutes = new(services, Conversations, MemorySpaces);
        creationRoutes = new(services, Creations, Accounts, MemorySpaces);
        openAiRoutes = new(services, Network);
        frontDoorRoutes = new(services, FrontDoorSessions, openAiRoutes);
        homeAssistantRoutes = new(services, HomeAssistant);
        inferenceRoutes = new(services, SpeakingVoices);
        logRoutes = new(services);
        memoryRoutes = new(services, Memories, MemorySpaces, Accounts, Settings, Network);
        networkRoutes = new(services, Network, pairing, credentials, signIn);
        priorityRoutes = new(services);
        securityAuditRoutes = new(services, credentials, Network, FrontDoorSessions, () => OutsideListeners(), () => FrontDoorListeners());
        settingsRoutes = new(services, Settings, AccountSettings, Accounts, MemorySpaces);
        signInRoutes = new(services, signIn, Network, Accounts, pairing, credentials);
        speakingVoiceRoutes = new(services, SpeakingVoices, Voices);
        voiceRoutes = new(services, Voices);
        // The account directory decides which device may use which memory space (GatewayMemorySpaceAccess.cs).
        MemorySpaces.Access = memoryRoutes.MayUseMemorySpace;
        Guard.SignInBlocked = () => GatewayOutsideAccess.SignInBlockedReason(SignIn);
        // Preemptions and refusals of pool work go into this host's own log, which paired desktops show.
        inference.Activity = (message, repeatKey) => Logs.Own(LogLevels.Info, message, repeatKey);
    }

    private static GatewayHttpServices CreateServices(GatewayHostIdentity identity, GatewayCredentialStore credentials,
        GatewayInferenceRouteRegistry inference, TimeProvider clock, IGatewayCrypto crypto, IGatewayAuditSink audit)
    {
        var logs = new GatewayLogStore(identity.HostId, clock);
        return new()
        {
            Identity = identity,
            Clock = clock,
            Crypto = crypto,
            Audit = audit,
            Authenticator = new(identity, credentials),
            Inference = inference,
            Logs = logs,
            StoreHealth = new(clock, (level, message, repeatKey) => logs.Own(level, message, repeatKey)),
            ApiKeys = new(identity.HostId, clock),
            Guard = new(clock, (level, message, repeatKey) => logs.Own(level, message, repeatKey))
        };
    }

    // The shared stores. Each handler gets the ones its routes use; the server attaches their storage.
    internal GatewayNetworkStore Network { get; }
    internal GatewayAccountDirectoryStore Accounts { get; } = new();
    internal GatewaySettingsStore Settings { get; } = new();
    internal GatewayAccountSettings AccountSettings { get; } = new();
    internal GatewayMemoryStore Memories { get; } = new();
    internal GatewayMemorySpaces MemorySpaces { get; } = new();
    internal GatewayConversationRecords Conversations { get; } = new();
    internal GatewayCreationStore Creations { get; } = new();
    internal GatewayClusterStore Cluster { get; } = new();
    internal GatewayVoiceStore Voices { get; } = new();
    internal GatewaySpeakingVoiceStore SpeakingVoices { get; } = new();
    internal GatewayCharacterModelStore CharacterModels { get; } = new();
    internal GatewayHomeAssistantStore HomeAssistant { get; } = new();
    internal GatewayCommandStore Commands { get; } = new();
    internal GatewayFrontDoorSessions FrontDoorSessions { get; } = new();

    internal GatewaySignInService SignIn => signInRoutes.SignIn;

    /// <summary>The host's current TLS certificate with its private key, which signs account attestations (its key is the one
    /// the roster pins). Set when the listener starts; null before.</summary>
    internal Func<System.Security.Cryptography.X509Certificates.X509Certificate2>? SigningCertificate
    {
        get => signInRoutes.SigningCertificate;
        set => signInRoutes.SigningCertificate = value;
    }

    internal GatewayOpenAiQueue OpenAiQueue { get => openAiRoutes.OpenAiQueue; set => openAiRoutes.OpenAiQueue = value; }
    internal Func<IReadOnlyList<GatewayOpenAiPeer>>? OpenAiPeers { get => openAiRoutes.OpenAiPeers; set => openAiRoutes.OpenAiPeers = value; }
    internal Func<GatewayOpenAiPeer, HttpMessageHandler>? OpenAiPeerHandler
    {
        get => openAiRoutes.OpenAiPeerHandler;
        set => openAiRoutes.OpenAiPeerHandler = value;
    }

    internal void CloseOpenAi() => openAiRoutes.CloseOpenAi();
    internal void LogGpuMap() => priorityRoutes.LogGpuMap();
    internal Task InvokeFrontDoorAsync(HttpContext context) => frontDoorRoutes.InvokeFrontDoorAsync(context);
    internal long FrontDoorRequestsOf(string keyId) => frontDoorRoutes.FrontDoorRequestsOf(keyId);

    /// <summary>The addresses this host listens on besides its home origin (<see cref="GatewayOverlayListeners"/>).</summary>
    internal Func<IReadOnlyList<string>> OutsideListeners { get; set; } = static () => [];

    /// <summary>Called when this host's roster entry may list other outside addresses.</summary>
    internal Action? OutsideAddressesChanged { get; set; }

    /// <summary>Where this host's front door listens ("ip:port"; <see cref="GatewayFrontDoorListener"/>).</summary>
    internal Func<IReadOnlyList<string>> FrontDoorListeners { get; set; } = static () => [];

    internal async Task InvokeAsync(HttpContext context)
    {
        var traceId = TraceId();
        var routeClass = "credential";
        try
        {
            GatewayRules.Require(context.Request.IsHttps, "binding.unsafe");
            var rawTarget = context.Features.Get<IHttpRequestFeature>()?.RawTarget;
            GatewayRules.Require(rawTarget is not null && rawTarget.Length <= 256, "request.invalid");
            routeClass = GatewayRequestGuard.RouteClass(rawTarget!);
            context.Items[GatewayGuard.ContextItem] = Guard;
            Guard.Admit(context, routeClass, rawTarget);
            if (context.Request.Method == HttpMethods.Get && rawTarget == "/health/live")
            {
                EnsureEmptyRequest(context.Request);
                await WriteJsonAsync(context, 200, new LivenessDocument { Status = "live" }).ConfigureAwait(false);
                return;
            }
            if (context.Request.Method == HttpMethods.Get && rawTarget == "/health/ready")
            {
                EnsureEmptyRequest(context.Request);
                var open = admission.AdmissionsOpen;
                await WriteJsonAsync(context, open ? 200 : 503, new
                {
                    schemaVersion = 1,
                    scope = "listener-auth-admission",
                    listener = "listening",
                    authAdmission = open ? "open" : "closed",
                    modelReadiness = "not-probed"
                }).ConfigureAwait(false);
                return;
            }
            if (context.Request.Method == HttpMethods.Post && rawTarget == "/martlet/v1/pair")
            {
                var proof = await ReadPairingAsync<GatewayPairingProof>(context.Request, context.RequestAborted).ConfigureAwait(false);
                var credential = pairing.Exchange(proof, context.RequestAborted);
                LogPaired(credential);
                Guard.Succeeded(context, routeClass, "paired", credential.DeviceId);
                await WritePairingAsync(context, credential, null).ConfigureAwait(false);
                return;
            }
            if (context.Request.Method == HttpMethods.Post && rawTarget == GatewayPairingCode.Path)
            {
                var proof = await ReadPairingAsync<GatewayCodePairingProof>(context.Request, context.RequestAborted).ConfigureAwait(false);
                var result = pairing.Exchange(proof, context.RequestAborted);
                LogPaired(result.Credential);
                Guard.Succeeded(context, routeClass, "paired", result.Credential.DeviceId);
                await WritePairingAsync(context, result.Credential, result.HostProof).ConfigureAwait(false);
                return;
            }
            if (context.Request.Method == HttpMethods.Post && rawTarget == Martlet.Core.Network.NetworkPairing.Path)
            {
                // A member this host hasn't heard of yet brings its copy of the roster, so this route takes a roster's size.
                var proof = await ReadPairingAsync<GatewayMemberPairingProof>(context.Request, context.RequestAborted,
                    MaximumMemberPairingRequestBytes).ConfigureAwait(false);
                var credential = Network.PairMember(proof, context.RequestAborted,
                    context.Connection.RemoteIpAddress?.ToString() ?? "");
                networkRoutes.LogMemberPaired(credential);
                Guard.Succeeded(context, routeClass, "paired", credential.DeviceId);
                await WritePairingAsync(context, credential, null).ConfigureAwait(false);
                return;
            }
            if (rawTarget == GatewaySecurityAuditRoutes.SecurityAuditPath)
            {
                await securityAuditRoutes.InvokeSecurityAuditAsync(context).ConfigureAwait(false);
                return;
            }
            if (GatewaySignInRoutes.IsSignInTarget(rawTarget!))
            {
                await signInRoutes.InvokeSignInAsync(context, rawTarget!).ConfigureAwait(false);
                return;
            }
            if (GatewayNetworkRoutes.IsNetworkTarget(rawTarget!))
            {
                await networkRoutes.InvokeNetworkAsync(context, rawTarget!).ConfigureAwait(false);
                return;
            }

            if (rawTarget == GatewayClusterRoutes.ClusterPath)
            {
                await clusterRoutes.InvokeClusterAsync(context).ConfigureAwait(false);
                return;
            }
            if (rawTarget == GatewayVoiceRoutes.VoicesPath || rawTarget == GatewayVoiceRoutes.LinkedVoicesPath)
            {
                await voiceRoutes.InvokeVoicesAsync(context, linked: rawTarget == GatewayVoiceRoutes.LinkedVoicesPath).ConfigureAwait(false);
                return;
            }
            if (GatewaySpeakingVoiceRoutes.IsSpeakingVoicesTarget(rawTarget!))
            {
                await speakingVoiceRoutes.InvokeSpeakingVoicesAsync(context, rawTarget!).ConfigureAwait(false);
                return;
            }
            if (GatewayCharacterModelRoutes.IsCharacterModelsTarget(rawTarget!))
            {
                await characterModelRoutes.InvokeCharacterModelsAsync(context, rawTarget!).ConfigureAwait(false);
                return;
            }
            if (GatewayCreationRoutes.IsCreationsTarget(rawTarget!))
            {
                await creationRoutes.InvokeCreationsAsync(context, rawTarget!).ConfigureAwait(false);
                return;
            }
            if (GatewayConversationRoutes.IsConversationsTarget(rawTarget!))
            {
                await conversationRoutes.InvokeConversationsAsync(context, rawTarget!).ConfigureAwait(false);
                return;
            }
            if (rawTarget == GatewayHomeAssistantRoutes.HomeAssistantPath)
            {
                await homeAssistantRoutes.InvokeHomeAssistantAsync(context).ConfigureAwait(false);
                return;
            }
            if (GatewaySettingsRoutes.IsSettingsTarget(rawTarget!))
            {
                await settingsRoutes.InvokeSettingsAsync(context, rawTarget!).ConfigureAwait(false);
                return;
            }
            if (GatewayMemoryRoutes.IsMemoriesTarget(rawTarget!))
            {
                await memoryRoutes.InvokeMemoriesAsync(context, rawTarget!).ConfigureAwait(false);
                return;
            }
            if (GatewayAccountRoutes.IsAccountsTarget(rawTarget!))
            {
                await accountRoutes.InvokeAccountsAsync(context, rawTarget!).ConfigureAwait(false);
                return;
            }
            if (rawTarget == GatewayApiKeyRoutes.ApiKeysPath)
            {
                await apiKeyRoutes.InvokeApiKeysAsync(context).ConfigureAwait(false);
                return;
            }
            if (rawTarget == GatewayCommandRoutes.CommandsPath || rawTarget!.StartsWith(GatewayCommandRoutes.CommandsPath + "/", StringComparison.Ordinal))
            {
                await commandRoutes.InvokeCommandsAsync(context, rawTarget).ConfigureAwait(false);
                return;
            }
            if (GatewayLogRoutes.IsLogsTarget(rawTarget!))
            {
                await logRoutes.InvokeLogsAsync(context, rawTarget!).ConfigureAwait(false);
                return;
            }
            if (Martlet.Core.Access.OpenAiEndpoint.IsPath(rawTarget!))
            {
                await openAiRoutes.InvokeOpenAiAsync(context, rawTarget!, traceId).ConfigureAwait(false);
                return;
            }

            if (context.Request.Method == HttpMethods.Post &&
                rawTarget == "/martlet/v1/inference/cancel")
            {
                await inferenceRoutes.InvokeInferenceCancellationAsync(context, traceId).ConfigureAwait(false);
                return;
            }
            if (GatewayPriorityRoutes.IsPriorityTarget(rawTarget!))
            {
                await priorityRoutes.InvokePriorityAsync(context, rawTarget!).ConfigureAwait(false);
                return;
            }
            if (context.Request.Method == HttpMethods.Post &&
                inference.TryGetByPath(rawTarget!, out var route))
            {
                await inferenceRoutes.InvokeInferenceAsync(context, traceId, route).ConfigureAwait(false);
                return;
            }

            if (context.Request.Method != HttpMethods.Get ||
                rawTarget is not ("/martlet/v1/version" or
                    "/martlet/v1/capabilities" or "/martlet/v1/status" or "/martlet/v1/machine"))
                throw new GatewayProtocolException("request.invalid");
            EnsureEmptyRequest(context.Request);
            // Any API key may ask who it is and what this host offers (it needs that to call a route); status and hardware
            // need read access. A friend may ask the same, and status, to use the engines; never the hardware.
            var principal = Authorize(context.Request, rawTarget switch
            {
                "/martlet/v1/version" or "/martlet/v1/capabilities" => GatewayApiAccess.AnyKeyOrFriend,
                "/martlet/v1/status" => GatewayApiAccess.ReadOrFriend,
                _ => GatewayApiAccess.Read
            });
            if (rawTarget == "/martlet/v1/machine")
            {
                // What the Ollama roles have loaded now, for the paired desktops' measured memory; an older desktop skips it.
                GatewayLoadedModel[]? loaded = null;
                try { loaded = await inference.LoadedModelsAsync(context.RequestAborted).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested) { }
                await WriteJsonAsync(context, 200, new MachineDocument
                {
                    ProtocolVersion = GatewayProtocolVersion.Current,
                    HostId = identity.HostId,
                    GeneratedAt = clock.GetUtcNow(),
                    MartletVersion = MartletVersion,
                    Machine = machine,
                    LoadedModels = loaded
                }).ConfigureAwait(false);
                return;
            }
            if (rawTarget == "/martlet/v1/version")
            {
                await WriteJsonAsync(context, 200, new VersionDocument
                {
                    ProtocolVersion = GatewayProtocolVersion.Current,
                    GatewayVersion = "0.2.0",
                    HostId = identity.HostId,
                    AuthorizedRole = principal.Role,
                    CredentialLifetime = principal.CredentialLifetime,
                    MartletVersion = MartletVersion,
                    ApiKey = principal.Key is { } key ? new ApiKeyIdentity
                    {
                        Id = key.Id, Name = key.Name, Scopes = key.Scopes, ExpiresAt = key.ExpiresAt
                    } : null,
                    Access = principal.Access == GatewayAccess.Friend ? "friend" : null
                }).ConfigureAwait(false);
                return;
            }
            if (rawTarget == "/martlet/v1/capabilities")
            {
                await WriteJsonAsync(context, 200, new CapabilitiesDocument
                {
                    ProtocolVersion = GatewayProtocolVersion.Current,
                    HostId = identity.HostId,
                    GeneratedAt = clock.GetUtcNow(),
                    AuthorizedRole = principal.Role,
                    Workers = workers.CapabilitiesFor(principal.Role),
                    RegistryId = GatewayInferenceProtocol.RegistryId,
                    RegistryVersion = GatewayInferenceProtocol.RegistryVersion,
                    Routes = inference.CapabilitiesFor(principal.Role, principal.Access == GatewayAccess.Friend)
                }).ConfigureAwait(false);
                return;
            }

            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2), clock);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                context.RequestAborted, deadline.Token);
            GatewayWorkerStatusDocument[] statuses;
            try
            {
                statuses = await workers.StatusForAsync(
                    principal.Role, clock.GetUtcNow(), linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
            {
                throw new GatewayProtocolException("worker.unavailable");
            }
            await WriteJsonAsync(context, 200, new StatusDocument
            {
                ProtocolVersion = GatewayProtocolVersion.Current,
                HostId = identity.HostId,
                GeneratedAt = clock.GetUtcNow(),
                AuthorizedRole = principal.Role,
                Workers = statuses
            }).ConfigureAwait(false);
        }
        catch (GatewayProtocolException error)
        {
            Guard.Failed(context, routeClass, error.Failure.Code);
            await WriteFailureAsync(context, traceId, error.Failure, error.Detail).ConfigureAwait(false);
        }
        catch (BadHttpRequestException error)
        {
            var code = error.StatusCode == StatusCodes.Status413PayloadTooLarge
                ? "request.too_large"
                : "request.invalid";
            await WriteFailureAsync(context, traceId, GatewayFailures.Get(code)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            context.Abort();
        }
        catch (Exception)
        {
            await WriteFailureAsync(context, traceId, GatewayFailures.Get("gateway.internal")).ConfigureAwait(false);
        }
    }

    private void LogPaired(IssuedDeviceCredential credential) =>
        Logs.Own(LogLevels.Info, $"Paired device {credential.DeviceId} (roles: {string.Join(", ", credential.Roles).ToLowerInvariant()}).");

    private ValueTask WritePairingAsync(HttpContext context, IssuedDeviceCredential credential, string? hostProof) =>
        WriteJsonAsync(context, 201, new PairingResponseDocument
        {
            ProtocolVersion = GatewayProtocolVersion.Current,
            HostId = identity.HostId,
            CredentialId = credential.CredentialId,
            CredentialSecret = credential.Secret.Reveal(),
            DeviceId = credential.DeviceId,
            Roles = credential.Roles,
            Lifetime = credential.Lifetime,
            HostProof = hostProof
        });

    private async ValueTask WriteFailureAsync(
        HttpContext context,
        Guid traceId,
        GatewayFailure failure,
        string? detail = null)
    {
        audit.Record(new()
        {
            TraceId = traceId,
            Code = failure.Code,
            HttpStatus = failure.HttpStatus
        });
        LogFailure(context, traceId, failure.Code, failure.HttpStatus);
        if (context.Response.HasStarted)
        {
            context.Abort();
            return;
        }
        // An early rejection may leave a body larger than Kestrel's drain limit.
        // Do not advertise that connection as reusable or retry the next signed POST.
        if (context.Request.Protocol == "HTTP/1.1" &&
            (context.Request.ContentLength is > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding")))
            context.Response.Headers.Connection = "close";
        await WriteJsonAsync(context, failure.HttpStatus, new FailureDocument
        {
            ProtocolVersion = GatewayProtocolVersion.Current,
            Code = failure.Code,
            Summary = failure.Summary,
            Remedy = failure.Remedy,
            Detail = detail,
            TraceId = traceId
        }).ConfigureAwait(false);
    }

    private sealed record PairingResponseDocument
    {
        public required GatewayProtocolVersion ProtocolVersion { get; init; }
        public required string HostId { get; init; }
        public required string CredentialId { get; init; }
        public required string CredentialSecret { get; init; }
        public required string DeviceId { get; init; }
        public required IReadOnlyList<GatewayRole> Roles { get; init; }
        public required GatewayCredentialLifetime Lifetime { get; init; }
        /// <summary>Short-code pairing only: the host's proof that it knows the typed code (see <see cref="GatewayPairingCode"/>).</summary>
        public string? HostProof { get; init; }
    }

    private sealed record VersionDocument
    {
        public required GatewayProtocolVersion ProtocolVersion { get; init; }
        public required string GatewayVersion { get; init; }
        public required string HostId { get; init; }
        public required GatewayRole AuthorizedRole { get; init; }
        public required GatewayCredentialLifetime CredentialLifetime { get; init; }
        public string? MartletVersion { get; init; }
        /// <summary>The API key that asked (never its secret); absent for a paired device.</summary>
        public ApiKeyIdentity? ApiKey { get; init; }
        /// <summary>"friend" for a friend's device, which reaches only this host's engines; absent for the owner's computers
        /// and API keys.</summary>
        public string? Access { get; init; }
    }

    private sealed record ApiKeyIdentity
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required IReadOnlyList<string> Scopes { get; init; }
        public DateTimeOffset? ExpiresAt { get; init; }
    }

    private sealed record CapabilitiesDocument
    {
        public required GatewayProtocolVersion ProtocolVersion { get; init; }
        public required string HostId { get; init; }
        public required DateTimeOffset GeneratedAt { get; init; }
        public required GatewayRole AuthorizedRole { get; init; }
        public required GatewayWorkerCapabilities[] Workers { get; init; }
        public required string RegistryId { get; init; }
        public required string RegistryVersion { get; init; }
        public required GatewayInferenceRouteCapability[] Routes { get; init; }
    }

    private sealed record StatusDocument
    {
        public required GatewayProtocolVersion ProtocolVersion { get; init; }
        public required string HostId { get; init; }
        public required DateTimeOffset GeneratedAt { get; init; }
        public required GatewayRole AuthorizedRole { get; init; }
        public required GatewayWorkerStatusDocument[] Workers { get; init; }
    }

    private sealed record MachineDocument
    {
        public required GatewayProtocolVersion ProtocolVersion { get; init; }
        public required string HostId { get; init; }
        public required DateTimeOffset GeneratedAt { get; init; }
        /// <summary>The Martlet release this gateway was built from, so desktops can offer to update older hosts.</summary>
        public string? MartletVersion { get; init; }
        public GatewayMachineReport? Machine { get; init; }
        /// <summary>What the host's Ollama roles have loaded now (<c>/api/ps</c>); absent when none answers and on older hosts.</summary>
        public GatewayLoadedModel[]? LoadedModels { get; init; }
    }

    private sealed record FailureDocument
    {
        public required GatewayProtocolVersion ProtocolVersion { get; init; }
        public required string Code { get; init; }
        public required string Summary { get; init; }
        public required string Remedy { get; init; }
        /// <summary>One word beyond the code (see <see cref="GatewayProtocolException.Detail"/>); omitted when null.</summary>
        public string? Detail { get; init; }
        public required Guid TraceId { get; init; }
    }
}
