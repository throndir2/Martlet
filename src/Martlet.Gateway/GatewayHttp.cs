using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Core.Logs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Martlet.Gateway;

internal sealed partial class GatewayHttpApplication
{
    internal const int MaximumPairingRequestBytes = 8_192;
    private static readonly JsonSerializerOptions Json = CreateJson();
    private static readonly string? MartletVersion = typeof(GatewayHttpApplication).Assembly.GetName().Version?.ToString(3);
    private readonly GatewayHostIdentity identity;
    private readonly IGatewayPairingExchange pairing;
    private readonly GatewayRequestAuthenticator authenticator;
    private readonly IGatewayAdmissionStatus admission;
    private readonly GatewayWorkerRegistry workers;
    private readonly GatewayInferenceRouteRegistry inference;
    private readonly TimeProvider clock;
    private readonly IGatewayCrypto crypto;
    private readonly IGatewayAuditSink audit;
    private volatile GatewayMachineReport? machine;

    internal GatewayMachineReport? Machine { get => machine; set => machine = value; }
    internal DateTimeOffset Now => clock.GetUtcNow();
    internal GatewayHttpApplication(
        GatewayHostIdentity identity,
        IGatewayPairingExchange pairing,
        GatewayCredentialStore credentials,
        IGatewayAdmissionStatus admission,
        GatewayWorkerRegistry workers,
        GatewayInferenceRouteRegistry inference,
        TimeProvider clock,
        IGatewayCrypto crypto,
        IGatewayAuditSink audit)
    {
        this.identity = identity;
        this.pairing = pairing;
        this.admission = admission;
        authenticator = new(identity, credentials);
        this.workers = workers;
        this.inference = inference;
        this.clock = clock;
        this.crypto = crypto;
        this.audit = audit;
        Logs = new(identity.HostId, clock);
        Network = new(identity, credentials, clock, (level, message) => Logs.Own(level, message));
        ApiKeys = new(identity.HostId, clock);
    }

    internal async Task InvokeAsync(HttpContext context)
    {
        var traceId = TraceId();
        try
        {
            GatewayRules.Require(context.Request.IsHttps, "binding.unsafe");
            var rawTarget = context.Features.Get<IHttpRequestFeature>()?.RawTarget;
            GatewayRules.Require(rawTarget is not null && rawTarget.Length <= 256, "request.invalid");
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
                await WritePairingAsync(context, credential, null).ConfigureAwait(false);
                return;
            }
            if (context.Request.Method == HttpMethods.Post && rawTarget == GatewayPairingCode.Path)
            {
                var proof = await ReadPairingAsync<GatewayCodePairingProof>(context.Request, context.RequestAborted).ConfigureAwait(false);
                var result = pairing.Exchange(proof, context.RequestAborted);
                LogPaired(result.Credential);
                await WritePairingAsync(context, result.Credential, result.HostProof).ConfigureAwait(false);
                return;
            }
            if (context.Request.Method == HttpMethods.Post && rawTarget == Martlet.Core.Network.NetworkPairing.Path)
            {
                var proof = await ReadPairingAsync<GatewayMemberPairingProof>(context.Request, context.RequestAborted).ConfigureAwait(false);
                var credential = Network.PairMember(proof, context.RequestAborted);
                LogMemberPaired(credential);
                await WritePairingAsync(context, credential, null).ConfigureAwait(false);
                return;
            }
            if (IsNetworkTarget(rawTarget!))
            {
                await InvokeNetworkAsync(context, rawTarget!).ConfigureAwait(false);
                return;
            }

            if (rawTarget == ClusterPath)
            {
                await InvokeClusterAsync(context).ConfigureAwait(false);
                return;
            }
            if (rawTarget == VoicesPath)
            {
                await InvokeVoicesAsync(context).ConfigureAwait(false);
                return;
            }
            if (rawTarget == HomeAssistantPath)
            {
                await InvokeHomeAssistantAsync(context).ConfigureAwait(false);
                return;
            }
            if (rawTarget == ApiKeysPath)
            {
                await InvokeApiKeysAsync(context).ConfigureAwait(false);
                return;
            }
            if (rawTarget == CommandsPath || rawTarget!.StartsWith(CommandsPath + "/", StringComparison.Ordinal))
            {
                await InvokeCommandsAsync(context, rawTarget).ConfigureAwait(false);
                return;
            }
            if (IsLogsTarget(rawTarget!))
            {
                await InvokeLogsAsync(context, rawTarget!).ConfigureAwait(false);
                return;
            }

            if (context.Request.Method == HttpMethods.Post &&
                rawTarget == "/martlet/v1/inference/cancel")
            {
                await InvokeInferenceCancellationAsync(context, traceId).ConfigureAwait(false);
                return;
            }
            if (context.Request.Method == HttpMethods.Post &&
                inference.TryGetByPath(rawTarget!, out var route))
            {
                await InvokeInferenceAsync(context, traceId, route).ConfigureAwait(false);
                return;
            }

            if (context.Request.Method != HttpMethods.Get ||
                rawTarget is not ("/martlet/v1/version" or
                    "/martlet/v1/capabilities" or "/martlet/v1/status" or "/martlet/v1/machine"))
                throw new GatewayProtocolException("request.invalid");
            EnsureEmptyRequest(context.Request);
            // Any API key may ask who it is and what this host offers (it needs that to call a route); status and hardware
            // need read access.
            var principal = Authorize(context.Request, rawTarget is "/martlet/v1/version" or "/martlet/v1/capabilities"
                ? GatewayApiAccess.AnyKey : GatewayApiAccess.Read);
            if (rawTarget == "/martlet/v1/machine")
            {
                await WriteJsonAsync(context, 200, new MachineDocument
                {
                    ProtocolVersion = GatewayProtocolVersion.Current,
                    HostId = identity.HostId,
                    GeneratedAt = clock.GetUtcNow(),
                    MartletVersion = MartletVersion,
                    Machine = machine
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
                    } : null
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
                    Routes = inference.CapabilitiesFor(principal.Role)
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
            await WriteFailureAsync(context, traceId, error.Failure).ConfigureAwait(false);
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

    private static void EnsureEmptyRequest(HttpRequest request)
    {
        GatewayRules.Require(request.ContentLength is null or 0 &&
            !request.Headers.ContainsKey("Transfer-Encoding"), "request.invalid");
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

    private static async ValueTask<T> ReadPairingAsync<T>(
        HttpRequest request,
        CancellationToken cancellationToken) where T : class
    {
        GatewayRules.Require(request.ContentType is "application/json" or
            "application/json; charset=utf-8", "request.invalid");
        GatewayRules.Require(!request.Headers.ContainsKey("Content-Encoding") &&
            request.ContentLength is null or >= 1 and <= MaximumPairingRequestBytes,
            request.ContentLength > MaximumPairingRequestBytes ? "request.too_large" : "request.invalid");
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var read = await request.Body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            if (output.Length + read > MaximumPairingRequestBytes)
                throw new GatewayProtocolException("request.too_large");
            output.Write(buffer, 0, read);
        }
        GatewayRules.Require(output.Length > 0, "request.invalid");
        var bytes = output.ToArray();
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
            InspectJson(document.RootElement);
            var proof = document.Deserialize<T>(Json);
            GatewayRules.Require(proof is not null, "request.invalid");
            return proof!;
        }
        catch (JsonException)
        {
            throw new GatewayProtocolException("request.invalid");
        }
        catch (InvalidOperationException)
        {
            throw new GatewayProtocolException("request.invalid");
        }
    }

    private static void InspectJson(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                GatewayRules.Require(names.Add(property.Name), "request.invalid");
                InspectJson(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                InspectJson(item);
        }
        else if (element.ValueKind == JsonValueKind.String)
        {
            _ = element.GetString();
        }
    }

    private async ValueTask WriteFailureAsync(
        HttpContext context,
        Guid traceId,
        GatewayFailure failure)
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
            TraceId = traceId
        }).ConfigureAwait(false);
    }

    private static async ValueTask WriteJsonAsync<T>(HttpContext context, int status, T value, int maximumBytes = GatewayRules.MaximumResponseBytes)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        GatewayRules.Require(bytes.Length <= maximumBytes, "gateway.internal");
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength = bytes.Length;
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        await context.Response.Body.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
    }

    private Guid TraceId()
    {
        var bytes = crypto.RandomBytes(16);
        return new(bytes);
    }

    private static JsonSerializerOptions CreateJson()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            RespectNullableAnnotations = true,
            MaxDepth = 8
        };
        options.Converters.Add(new JsonStringEnumConverter(
            JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false));
        return options;
    }

    private sealed record LivenessDocument
    {
        public required string Status { get; init; }
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
    }

    private sealed record FailureDocument
    {
        public required GatewayProtocolVersion ProtocolVersion { get; init; }
        public required string Code { get; init; }
        public required string Summary { get; init; }
        public required string Remedy { get; init; }
        public required Guid TraceId { get; init; }
    }
}
