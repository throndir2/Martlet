using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using Martlet.Core.Logs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Martlet.Gateway;

/// <summary>What every route handler of a host uses: who the host is, its clock, crypto, audit, request authentication, its
/// log, its request guard, its store health and its API keys. <see cref="GatewayHttpApplication"/> makes one and gives it to
/// each handler; a handler gets the stores of its own area through its constructor.</summary>
internal sealed class GatewayHttpServices
{
    internal required GatewayHostIdentity Identity { get; init; }
    internal required TimeProvider Clock { get; init; }
    internal required IGatewayCrypto Crypto { get; init; }
    internal required IGatewayAuditSink Audit { get; init; }
    internal required GatewayRequestAuthenticator Authenticator { get; init; }
    internal required GatewayInferenceRouteRegistry Inference { get; init; }
    internal required GatewayLogStore Logs { get; init; }
    internal required GatewayRequestGuard Guard { get; init; }
    internal required GatewayStoreHealth StoreHealth { get; init; }
    internal required GatewayApiKeyStore ApiKeys { get; init; }
}

/// <summary>The base of a host's route handlers (one class for each area of routes): the shared request plumbing (reading
/// and checking bodies, writing JSON, authorizing a paired device or an API key, logging a failure).</summary>
internal abstract class GatewayRouteHandler
{
    internal const int MaximumPairingRequestBytes = 8_192;
    /// <summary>A member pairing with its copy of the roster (<see cref="GatewayMemberPairingProof.Roster"/>).</summary>
    internal const int MaximumMemberPairingRequestBytes = Martlet.Core.Network.NetworkRoster.MaximumBytes + 4_096;
    internal static readonly TimeSpan PairingBodyDeadline = TimeSpan.FromSeconds(10);
    protected const string RouteItem = "martlet.route", DeviceItem = "martlet.device";
    protected static readonly JsonSerializerOptions Json = CreateJson();
    protected static readonly string? MartletVersion = typeof(GatewayRouteHandler).Assembly.GetName().Version?.ToString(3);
    private static JsonSerializerOptions? unescapedJson;

    protected readonly GatewayHostIdentity identity;
    protected readonly TimeProvider clock;
    protected readonly IGatewayCrypto crypto;
    protected readonly IGatewayAuditSink audit;
    protected readonly GatewayRequestAuthenticator authenticator;
    protected readonly GatewayInferenceRouteRegistry inference;

    protected GatewayRouteHandler(GatewayHttpServices services)
    {
        Services = services;
        identity = services.Identity;
        clock = services.Clock;
        crypto = services.Crypto;
        audit = services.Audit;
        authenticator = services.Authenticator;
        inference = services.Inference;
    }

    protected GatewayHttpServices Services { get; }

    internal GatewayLogStore Logs => Services.Logs;

    /// <summary>Rate limits, lockouts and the audit log every request passes (see <see cref="GatewayRequestGuard"/>).</summary>
    internal GatewayRequestGuard Guard => Services.Guard;

    /// <summary>The load and save failures of this host's shared stores (see <see cref="GatewayStoreHealth"/>).</summary>
    internal GatewayStoreHealth StoreHealth => Services.StoreHealth;

    internal GatewayApiKeyStore ApiKeys => Services.ApiKeys;

    /// <summary>A paired device (signed request; a friend's only where <paramref name="access"/> admits friends) or, when
    /// <paramref name="access"/> admits it, an API key.</summary>
    protected GatewayPrincipal Authorize(HttpRequest request, GatewayApiAccess access, GatewayRole? role = null) =>
        GatewayApiKeyStore.IsBearer(request) ? ApiKeys.Authenticate(request, access, role) : authenticator.Authenticate(request, access.Friends);

    protected GatewayPrincipal Authorize(HttpRequest request, ReadOnlySpan<byte> bodyHash, GatewayApiAccess access, GatewayRole? role = null) =>
        GatewayApiKeyStore.IsBearer(request) ? ApiKeys.Authenticate(request, access, role) : authenticator.Authenticate(request, bodyHash, access.Friends);

    /// <summary>Records a refused or failed request in this host's log: what was asked (the route and device for model
    /// requests), the stable code and HTTP status, and the trace ID the desktop also shows. Never the request body.</summary>
    protected void LogFailure(HttpContext? context, Guid traceId, string code, int status)
    {
        var failure = GatewayFailures.Get(code);
        var what = context?.Items[RouteItem] is string route
            ? $"{route} request from {context.Items[DeviceItem] as string ?? "a paired device"}"
            : context is null ? "A request" : $"{context.Request.Method} {context.Request.Path} from {GatewayRequestGuard.Address(context)}";
        var level = code == "job.canceled" ? LogLevels.Info : status >= 500 ? LogLevels.Error : LogLevels.Warn;
        Logs.Own(level, $"{what} failed: {code} (HTTP {status}). {failure.Summary} Trace {traceId}.",
            repeatKey: $"{code}|{(context?.Items[RouteItem] as string) ?? context?.Request.Method}");
    }

    protected Guid TraceId()
    {
        var bytes = crypto.RandomBytes(16);
        return new(bytes);
    }

    protected static void EnsureEmptyRequest(HttpRequest request)
    {
        GatewayRules.Require(request.ContentLength is null or 0 &&
            !request.Headers.ContainsKey("Transfer-Encoding"), "request.invalid");
    }

    protected static async ValueTask<T> ReadPairingAsync<T>(
        HttpRequest request,
        CancellationToken cancellationToken,
        int maximumBytes = MaximumPairingRequestBytes) where T : class
    {
        GatewayRules.Require(request.ContentType is "application/json" or
            "application/json; charset=utf-8", "request.invalid");
        GatewayRules.Require(!request.Headers.ContainsKey("Content-Encoding") &&
            (request.ContentLength is null || request.ContentLength >= 1 && request.ContentLength <= maximumBytes),
            request.ContentLength > maximumBytes ? "request.too_large" : "request.invalid");
        if (maximumBytes > MaximumPairingRequestBytes &&
            request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = maximumBytes;
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        // Anyone may call these routes, so a slow sender can't hold the connection open: the whole body within 10 seconds.
        using var deadline = new CancellationTokenSource(PairingBodyDeadline);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            while (true)
            {
                var read = await request.Body.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
                if (read == 0)
                    break;
                if (output.Length + read > maximumBytes)
                    throw new GatewayProtocolException("request.too_large");
                output.Write(buffer, 0, read);
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new GatewayProtocolException("request.timeout");
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

    protected static void InspectJson(JsonElement element)
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

    protected static async ValueTask<byte[]> ReadInferenceBodyAsync(
        HttpRequest request,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        GatewayRules.Require(
            (request.ContentType is "application/json" or
                "application/json; charset=utf-8") &&
            !request.Headers.ContainsKey("Content-Encoding") &&
            (request.ContentLength is null or >= 1) &&
            (request.ContentLength is null ||
                request.ContentLength <= maximumBytes),
            request.ContentLength > maximumBytes
                ? "request.too_large"
                : "request.invalid");
        var feature = request.HttpContext.Features
            .Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false })
            feature.MaxRequestBodySize = maximumBytes;

        using var output = request.ContentLength is > 0
            ? new MemoryStream(checked((int)request.ContentLength.Value))
            : new MemoryStream();
        var buffer = new byte[8_192];
        try
        {
            while (true)
            {
                var read = await request.Body.ReadAsync(
                    buffer,
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;
                if (output.Length + read > maximumBytes)
                    throw new GatewayProtocolException("request.too_large");
                output.Write(buffer, 0, read);
            }
            GatewayRules.Require(output.Length > 0 &&
                (request.ContentLength is null ||
                    output.Length == request.ContentLength),
                "request.invalid");
            return output.ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            if (output.TryGetBuffer(out var segment))
                CryptographicOperations.ZeroMemory(
                    segment.AsSpan(0, checked((int)output.Length)));
        }
    }

    protected static async ValueTask WriteJsonAsync<T>(HttpContext context, int status, T value, int maximumBytes = GatewayRules.MaximumResponseBytes,
        JsonSerializerOptions? options = null)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, options ?? Json);
        GatewayRules.Require(bytes.Length <= maximumBytes, "gateway.internal");
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength = bytes.Length;
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        await context.Response.Body.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>The same JSON options with characters such as <c>+</c> and <c>&lt;</c> left as they are, for bodies a desktop
    /// stores byte for byte.</summary>
    protected static JsonSerializerOptions UnescapedJson =>
        unescapedJson ??= new(Json) { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

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

    protected sealed record LivenessDocument
    {
        public required string Status { get; init; }
    }
}
