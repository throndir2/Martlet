using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Martlet.Core.Access;
using Martlet.Core.Logs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Martlet.Gateway;

/// <summary>Another host of this host's Martlet network that a front-door host may send an OpenAI-compatible request to: its
/// home origin and the TLS key it is pinned to (from the roster).</summary>
internal sealed record GatewayOpenAiPeer(string HostId, string Origin, string Spki);

/// <summary>
/// The OpenAI-compatible endpoint (docs/OPENAI_ENDPOINT.md). Every host serves it, so any host can be the front door that a
/// reverse proxy with a real certificate sends people to:
/// <list type="bullet">
/// <item><c>GET /v1/models</c> lists the language models this host runs and, for an API key, the models of every other host
/// of the network (asked with the same key, so each host checks it itself).</item>
/// <item><c>POST /v1/chat/completions</c>, <c>/v1/completions</c> and <c>/v1/embeddings</c> run on this host when it has the
/// model and a free slot; otherwise the front door sends the request on, with the same key, to a host of the network that has
/// it. A host never sends a request on twice.</item>
/// </list>
/// Only API keys with the <c>openai</c> scope may run requests; paired computers and friends may list the models. Requests
/// rank below all of the owner's work (or with the owner's background work for a key with <c>equal-priority</c>), so sharing
/// never makes the owner's own replies wait. Only each host's own Ollama is reachable: never a cloud provider or a key.
/// </summary>
internal sealed partial class GatewayHttpApplication
{
    private static readonly long OpenAiCreated = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private static readonly TimeSpan PeerModelsFresh = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PeerModelsRetry = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PeerModelsTimeout = TimeSpan.FromSeconds(3);
    private const int MaximumPeerModelsBytes = 256 * 1024;
    private const int MaximumModels = 64;
    private readonly object openAiGate = new();
    private readonly Dictionary<string, (string Origin, string Spki, HttpClient Client)> openAiClients = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Host, string Key), (DateTimeOffset Until, string[] Models)> peerModels = [];

    /// <summary>The other hosts a front door may send requests to; null reads them from this host's network roster. Set by
    /// tests.</summary>
    internal Func<IReadOnlyList<GatewayOpenAiPeer>>? OpenAiPeers { get; set; }

    /// <summary>The handler that reaches another host; null pins its TLS key from the roster. Set by tests.</summary>
    internal Func<GatewayOpenAiPeer, HttpMessageHandler>? OpenAiPeerHandler { get; set; }

    private IReadOnlyList<GatewayOpenAiPeer> Peers() => OpenAiPeers?.Invoke() ?? (Network.Roster is { } roster
        ? roster.ActiveHosts.Where(h => h.Id != identity.HostId && h.Origin is not null && h.Spki is not null)
            .Select(h => new GatewayOpenAiPeer(h.Id, h.Origin!, h.Spki!)).ToArray()
        : []);

    private sealed class OpenAiFailure(int status, string message, string code, string? param = null, int? retryAfter = null)
        : Exception(message)
    {
        internal int Status { get; } = status;
        internal string Code { get; } = code;
        internal string? Param { get; } = param;
        internal int? RetryAfter { get; } = retryAfter;
    }

    private async ValueTask InvokeOpenAiAsync(HttpContext context, string rawTarget, Guid traceId)
    {
        var path = rawTarget.Split('?', 2)[0];
        try
        {
            if (context.Request.Method == HttpMethods.Get && path == OpenAiEndpoint.ModelsPath)
                await ListModelsAsync(context, null).ConfigureAwait(false);
            else if (context.Request.Method == HttpMethods.Get && path.StartsWith(OpenAiEndpoint.ModelsPath + "/", StringComparison.Ordinal))
                await ListModelsAsync(context, Uri.UnescapeDataString(path[(OpenAiEndpoint.ModelsPath.Length + 1)..])).ConfigureAwait(false);
            else if (context.Request.Method == HttpMethods.Post && OpenAiEndpoint.RelayPaths.Contains(path))
                await RelayOpenAiAsync(context, path, traceId).ConfigureAwait(false);
            else
                throw new OpenAiFailure(404, "Martlet's OpenAI-compatible endpoint has GET /v1/models and POST /v1/chat/completions, " +
                    "/v1/completions and /v1/embeddings.", "not_found");
        }
        catch (GatewayProtocolException error)
        {
            Guard.Failed(context, "openai", error.Failure.Code);
            var status = error.Failure.HttpStatus;
            await WriteOpenAiFailureAsync(context, traceId, status, $"{error.Failure.Summary} {error.Failure.Remedy}",
                status == 401 ? "invalid_api_key" : error.Failure.Code, null, status == 429 ? 5 : null).ConfigureAwait(false);
        }
        catch (OpenAiFailure failure)
        {
            await WriteOpenAiFailureAsync(context, traceId, failure.Status, failure.Message, failure.Code, failure.Param, failure.RetryAfter)
                .ConfigureAwait(false);
        }
    }

    private static string? OpenAiToken(HttpRequest request)
    {
        // A paired computer's signed request carries its own Authorization scheme.
        if (!GatewayApiKeyStore.IsBearer(request)) return null;
        var values = request.Headers.Authorization;
        GatewayRules.Require(values.Count == 1 && values[0] is { Length: <= 128 } header && header.All(char.IsAscii) &&
            header.StartsWith(GatewayApiKeyStore.Scheme + " ", StringComparison.OrdinalIgnoreCase), "key.invalid");
        return values[0]![(GatewayApiKeyStore.Scheme.Length + 1)..].Trim();
    }

    /// <summary>GET /v1/models and /v1/models/{id}: an API key with the openai scope sees the models of the whole network
    /// (this host's own only when another host asked); a paired computer or a friend sees this host's.</summary>
    private async ValueTask ListModelsAsync(HttpContext context, string? id)
    {
        EnsureEmptyRequest(context.Request);
        GatewayPrincipal? key = null;
        if (OpenAiToken(context.Request) is { } token) key = ApiKeys.AuthenticateOpenAi(token);
        else _ = authenticator.Authenticate(context.Request, friends: true);
        var models = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var model in inference.OpenAiRelays().Select(r => r.Relay.OpenAiModel).Distinct(StringComparer.Ordinal))
            models[model] = [identity.HostId];
        if (key is not null && !context.Request.Headers.ContainsKey(OpenAiEndpoint.ForwardedHeader))
            foreach (var (peer, list) in await PeerModelsAsync(context.Request.Headers.Authorization.ToString(), key.CredentialId,
                         context.RequestAborted).ConfigureAwait(false))
                foreach (var model in list)
                    (models.TryGetValue(model, out var hosts) ? hosts : models[model] = []).Add(peer.HostId);
        object Entry(string model) => new Dictionary<string, object?>
        {
            ["id"] = model, ["object"] = "model", ["created"] = OpenAiCreated, ["owned_by"] = "martlet",
            ["martlet_hosts"] = models[model]
        };
        if (id is not null)
        {
            if (!models.ContainsKey(id))
                throw new OpenAiFailure(404, $"The model '{id}' isn't on this Martlet network. GET /v1/models lists the models.", "model_not_found", "model");
            await WriteJsonAsync(context, 200, Entry(id), options: OpenAiJson).ConfigureAwait(false);
            return;
        }
        await WriteJsonAsync(context, 200, new Dictionary<string, object?>
        {
            ["object"] = "list",
            ["data"] = models.Keys.Order(StringComparer.Ordinal).Take(MaximumModels).Select(Entry).ToArray()
        }, options: OpenAiJson).ConfigureAwait(false);
    }

    private static readonly JsonSerializerOptions OpenAiJson = new() { MaxDepth = 8 };

    /// <summary>The models each other host of the network lists for the same key, asked at most every 30 seconds per host and
    /// key (10 seconds after a host didn't answer). A host that doesn't answer in 3 seconds lists none this time.</summary>
    private async Task<IReadOnlyList<(GatewayOpenAiPeer Peer, string[] Models)>> PeerModelsAsync(string authorization, string keyId,
        CancellationToken cancellationToken)
    {
        var peers = Peers();
        if (peers.Count == 0) return [];
        var results = await Task.WhenAll(peers.Select(async peer =>
        {
            var now = clock.GetUtcNow();
            lock (openAiGate)
                if (peerModels.TryGetValue((peer.HostId, keyId), out var cached) && cached.Until > now) return (peer, cached.Models);
            string[] models;
            DateTimeOffset until;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(PeerModelsTimeout);
                using var request = new HttpRequestMessage(HttpMethod.Get, peer.Origin + OpenAiEndpoint.ModelsPath);
                request.Headers.TryAddWithoutValidation("Authorization", authorization);
                request.Headers.TryAddWithoutValidation(OpenAiEndpoint.ForwardedHeader, identity.HostId);
                using var response = await PeerClient(peer).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                    .ConfigureAwait(false);
                if (response.StatusCode != HttpStatusCode.OK) throw new HttpRequestException("status " + (int)response.StatusCode);
                var bytes = await ReadAllAsync(response, MaximumPeerModelsBytes, timeout.Token).ConfigureAwait(false);
                using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
                models = document.RootElement.GetProperty("data").EnumerateArray()
                    .Select(m => m.GetProperty("id").GetString()).OfType<string>()
                    .Where(m => m.Length is > 0 and <= 128).Distinct(StringComparer.Ordinal).Take(MaximumModels).ToArray();
                until = clock.GetUtcNow() + PeerModelsFresh;
            }
            catch (Exception error) when (error is HttpRequestException or OperationCanceledException or JsonException or
                KeyNotFoundException or InvalidOperationException or IOException or GatewayProtocolException)
            {
                if (cancellationToken.IsCancellationRequested) throw;
                models = [];
                until = clock.GetUtcNow() + PeerModelsRetry;
            }
            lock (openAiGate) peerModels[(peer.HostId, keyId)] = (until, models);
            return (peer, models);
        })).ConfigureAwait(false);
        return results;
    }

    private void ForgetPeerModels(string hostId)
    {
        lock (openAiGate)
            foreach (var key in peerModels.Keys.Where(k => k.Host == hostId).ToArray()) peerModels.Remove(key);
    }

    private static async Task<byte[]> ReadAllAsync(HttpResponseMessage response, int maximum, CancellationToken token)
    {
        await using var body = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await body.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            if (output.Length + read > maximum) throw new IOException("The answer is too large.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    /// <summary>A client for another host of the network, pinned to its TLS key from the roster, kept while its origin and key
    /// stay the same. A request runs at most as long as the longest job.</summary>
    private HttpClient PeerClient(GatewayOpenAiPeer peer)
    {
        lock (openAiGate)
        {
            if (openAiClients.TryGetValue(peer.HostId, out var kept) && kept.Origin == peer.Origin && kept.Spki == peer.Spki)
                return kept.Client;
            kept.Client?.Dispose();
            HttpMessageHandler handler;
            if (OpenAiPeerHandler is { } make) handler = make(peer);
            else
            {
                _ = new GatewayOrigin(peer.Origin);
                var pinned = new GatewayHostIdentity { HostId = peer.HostId, SpkiFingerprint = peer.Spki };
                pinned.Validate();
                var sockets = new SocketsHttpHandler
                {
                    AllowAutoRedirect = false, UseCookies = false, UseProxy = false, Credentials = null,
                    AutomaticDecompression = DecompressionMethods.None, ConnectTimeout = TimeSpan.FromSeconds(3),
                    PooledConnectionIdleTimeout = TimeSpan.FromSeconds(20), PooledConnectionLifetime = TimeSpan.FromMinutes(2)
                };
                sockets.SslOptions.EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
                sockets.SslOptions.CertificateChainPolicy = new() { RevocationMode = X509RevocationMode.NoCheck, DisableCertificateDownloads = true };
                sockets.SslOptions.RemoteCertificateValidationCallback = (_, certificate, chain, errors) =>
                    PinnedGatewayClient.ValidateCertificate(certificate, chain, errors, pinned, clock, crypto);
                handler = sockets;
            }
            var client = new HttpClient(handler, disposeHandler: true) { Timeout = OpenAiEndpoint.MaximumDuration + TimeSpan.FromMinutes(1) };
            openAiClients[peer.HostId] = (peer.Origin, peer.Spki, client);
            return client;
        }
    }

    private static async Task<byte[]> ReadOpenAiBodyAsync(HttpRequest request, CancellationToken token)
    {
        if (request.ContentType is not { } type || !type.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
            throw new OpenAiFailure(400, "Send the request as JSON (Content-Type: application/json).", "invalid_request");
        if (request.Headers.ContainsKey("Content-Encoding"))
            throw new OpenAiFailure(400, "Send the request without Content-Encoding.", "invalid_request");
        if (request.ContentLength > OpenAiEndpoint.MaximumRequestBytes)
            throw new OpenAiFailure(413, "The request is larger than 32 MB.", "request_too_large");
        if (request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
            feature.MaxRequestBodySize = OpenAiEndpoint.MaximumRequestBytes;
        using var output = new MemoryStream();
        var buffer = new byte[16_384];
        int read;
        while ((read = await request.Body.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            if (output.Length + read > OpenAiEndpoint.MaximumRequestBytes)
                throw new OpenAiFailure(413, "The request is larger than 32 MB.", "request_too_large");
            output.Write(buffer, 0, read);
        }
        if (output.Length == 0) throw new OpenAiFailure(400, "The request has no body.", "invalid_request");
        return output.ToArray();
    }

    // The request as JSON: an object whose top-level names appear once, so the model Martlet checks is the model Ollama runs.
    private static JsonDocument ParseOpenAiBody(byte[] bytes)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 }); }
        catch (JsonException) { throw new OpenAiFailure(400, "The request isn't valid JSON.", "invalid_json"); }
        var root = document.RootElement;
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (root.ValueKind != JsonValueKind.Object || !root.EnumerateObject().All(p => names.Add(p.Name)))
        {
            document.Dispose();
            throw new OpenAiFailure(400, "The request must be one JSON object, with each name once.", "invalid_request");
        }
        return document;
    }

    private static string KindOf(string path) => path switch
    {
        OpenAiEndpoint.ChatPath => "chat",
        OpenAiEndpoint.CompletionsPath => "completion",
        _ => "embedding"
    };

    /// <summary>POST /v1/chat/completions, /v1/completions, /v1/embeddings: here when this host has the model and a slot for
    /// the key's rank, else on another host of the network that has it, else 429 (busy) or 404 (no such model).</summary>
    private async ValueTask RelayOpenAiAsync(HttpContext context, string path, Guid traceId)
    {
        var token = OpenAiToken(context.Request) ?? throw new GatewayProtocolException("auth.missing");
        var principal = ApiKeys.AuthenticateOpenAi(token);
        var bytes = await ReadOpenAiBodyAsync(context.Request, context.RequestAborted).ConfigureAwait(false);
        using var document = ParseOpenAiBody(bytes);
        if (!document.RootElement.TryGetProperty("model", out var named) || named.ValueKind != JsonValueKind.String ||
            named.GetString() is not { Length: > 0 and <= 128 } model)
            throw new OpenAiFailure(400, "Name the model in model. GET /v1/models lists the models.", "model_required", "model");
        var kind = KindOf(path);
        context.Items[RouteItem] = "openai-" + kind;
        context.Items[DeviceItem] = principal.Caller;
        string? busy = null;
        foreach (var (routeId, relay) in inference.OpenAiRelays().Where(r => r.Relay.OpenAiModel == model))
        {
            GatewayInferenceRouteRegistry.GatewayOutsideJob job;
            try { job = inference.BeginOutside(principal, routeId, ApiKeys.EqualPriority(principal)); }
            catch (GatewayProtocolException error) when (error.Failure.Code is "job.busy" or "worker.quarantined" or "worker.unavailable")
            {
                busy = error.Detail == GatewayInferenceRouteRegistry.OwnerDetail ? "owner" : "busy";
                continue;
            }
            using (job) await RunHereAsync(context, path, kind, document.RootElement, relay, job, model).ConfigureAwait(false);
            return;
        }
        if (!context.Request.Headers.ContainsKey(OpenAiEndpoint.ForwardedHeader))
        {
            var authorization = context.Request.Headers.Authorization.ToString();
            foreach (var (peer, models) in await PeerModelsAsync(authorization, principal.CredentialId, context.RequestAborted).ConfigureAwait(false))
            {
                if (!models.Contains(model, StringComparer.Ordinal)) continue;
                var sent = await ForwardOpenAiAsync(context, peer, path, kind, bytes, authorization, principal, model).ConfigureAwait(false);
                if (sent is null) return;
                busy ??= sent;
            }
        }
        if (busy is not null)
            throw new OpenAiFailure(429, busy == "owner"
                ? $"'{model}' is busy with its owner's own work right now. Martlet shares it when its owner doesn't need it; try again in a moment."
                : $"'{model}' is busy right now. Try again in a moment.", "model_busy", null, 5);
        throw new OpenAiFailure(404, $"The model '{model}' isn't on this Martlet network. GET /v1/models lists the models.", "model_not_found", "model");
    }

    /// <summary>Runs the request on this host's own model and streams its answer. Work of a higher rank, a revoked key, the
    /// deadline or the caller hanging up stops it.</summary>
    private async ValueTask RunHereAsync(HttpContext context, string path, string kind, JsonElement request, IGatewayOpenAiRelay relay,
        GatewayInferenceRouteRegistry.GatewayOutsideJob job, string model)
    {
        using var deadline = new CancellationTokenSource(OpenAiEndpoint.MaximumDuration, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, job.Stopped, deadline.Token);
        var started = clock.GetTimestamp();
        var stream = false;
        try
        {
            var answer = await relay.RunOpenAiAsync(path, request, linked.Token).ConfigureAwait(false);
            stream = answer.ContentType == "text/event-stream";
            context.Response.StatusCode = answer.Status;
            context.Response.ContentType = stream ? "text/event-stream; charset=utf-8" : "application/json; charset=utf-8";
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers["X-Martlet-Host"] = identity.HostId;
            if (stream) context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            await foreach (var piece in answer.Body.WithCancellation(linked.Token).ConfigureAwait(false))
            {
                await context.Response.Body.WriteAsync(piece, linked.Token).ConfigureAwait(false);
                if (stream) await context.Response.Body.FlushAsync(linked.Token).ConfigureAwait(false);
            }
            Logs.Own(LogLevels.Info, $"OpenAI-compatible {kind} request from {job.Principal.Caller} on {model} finished here in " +
                $"{clock.GetElapsedTime(started).TotalMilliseconds:0} ms (HTTP {answer.Status}).", "openai.done|" + kind);
        }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        {
            var (status, message, code) = job.Preempted
                ? (503, "Its owner needed this model, so Martlet stopped your request. Try again in a moment.", "owner_needs_it")
                : job.RevokedCode is { } revoked
                    ? (401, "Your API key stopped working (" + revoked + "), so Martlet stopped your request.", "invalid_api_key")
                    : (504, "The request ran longer than 15 minutes.", "deadline");
            Logs.Own(LogLevels.Info, job.Preempted
                ? $"Owner first: stopped an OpenAI-compatible {kind} request from {job.Principal.Caller} on {model} for {job.PreemptedBy}."
                : $"Stopped an OpenAI-compatible {kind} request from {job.Principal.Caller} on {model}: {code}.", "openai.stopped|" + code);
            if (!context.Response.HasStarted)
            {
                context.Response.Headers.RetryAfter = "5";
                throw new OpenAiFailure(status, message, code);
            }
            if (stream)
            {
                // The stream already started: say why it ends, as OpenAI's own streams do, then end it.
                try
                {
                    var error = SseEvent(GatewayOpenAiAnswer.ErrorBody(status, message, code));
                    await context.Response.Body.WriteAsync(error, context.RequestAborted).ConfigureAwait(false);
                    await context.Response.Body.WriteAsync("data: [DONE]\n\n"u8.ToArray(), context.RequestAborted).ConfigureAwait(false);
                    await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
                }
                catch (Exception error) when (error is IOException or OperationCanceledException) { context.Abort(); }
            }
            else context.Abort();
        }

        static byte[] SseEvent(byte[] json)
        {
            var bytes = new byte[json.Length + 8];
            "data: "u8.CopyTo(bytes);
            json.CopyTo(bytes, 6);
            bytes[^2] = (byte)'\n';
            bytes[^1] = (byte)'\n';
            return bytes;
        }
    }

    /// <summary>Sends the request on to <paramref name="peer"/> with the caller's own key and streams its answer back. Returns
    /// null when it answered, or "owner" or "busy" when it turned the request away, so the next host can be tried.</summary>
    private async Task<string?> ForwardOpenAiAsync(HttpContext context, GatewayOpenAiPeer peer, string path, string kind, byte[] body,
        string authorization, GatewayPrincipal principal, string model)
    {
        using var deadline = new CancellationTokenSource(OpenAiEndpoint.MaximumDuration, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, deadline.Token);
        using var request = new HttpRequestMessage(HttpMethod.Post, peer.Origin + path) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new("application/json");
        request.Headers.TryAddWithoutValidation("Authorization", authorization);
        request.Headers.TryAddWithoutValidation(OpenAiEndpoint.ForwardedHeader, identity.HostId);
        var started = clock.GetTimestamp();
        HttpResponseMessage response;
        try { response = await PeerClient(peer).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false); }
        catch (Exception error) when (error is HttpRequestException or GatewayProtocolException ||
            error is OperationCanceledException && !context.RequestAborted.IsCancellationRequested)
        {
            ForgetPeerModels(peer.HostId);
            Logs.Own(LogLevels.Warn, $"Couldn't send an OpenAI-compatible {kind} request on to {peer.HostId}: it didn't answer.",
                "openai.peer|" + peer.HostId);
            return "busy";
        }
        using (response)
        {
            var status = (int)response.StatusCode;
            if (status is 429 or 503) return response.Headers.TryGetValues("X-Martlet-Busy", out var why) && why.Contains("owner") ? "owner" : "busy";
            var type = response.Content.Headers.ContentType?.MediaType ?? "application/json";
            var stream = type == "text/event-stream";
            context.Response.StatusCode = status;
            context.Response.ContentType = stream ? "text/event-stream; charset=utf-8" : "application/json; charset=utf-8";
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers["X-Martlet-Host"] = response.Headers.TryGetValues("X-Martlet-Host", out var host)
                ? host.FirstOrDefault() ?? peer.HostId : peer.HostId;
            if (stream) context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            try
            {
                await using var answer = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
                var buffer = new byte[16_384];
                int read;
                while ((read = await answer.ReadAsync(buffer, linked.Token).ConfigureAwait(false)) > 0)
                {
                    await context.Response.Body.WriteAsync(buffer.AsMemory(0, read), linked.Token).ConfigureAwait(false);
                    if (stream) await context.Response.Body.FlushAsync(linked.Token).ConfigureAwait(false);
                }
            }
            catch (Exception error) when (error is IOException or HttpRequestException ||
                error is OperationCanceledException && !context.RequestAborted.IsCancellationRequested)
            {
                context.Abort();
            }
            Logs.Own(LogLevels.Info, $"OpenAI-compatible {kind} request from {principal.Caller} on {model} sent on to {peer.HostId} " +
                $"(HTTP {status}, {clock.GetElapsedTime(started).TotalMilliseconds:0} ms).", "openai.sent|" + peer.HostId);
            return null;
        }
    }

    private async ValueTask WriteOpenAiFailureAsync(HttpContext context, Guid traceId, int status, string message, string code,
        string? param, int? retryAfter)
    {
        audit.Record(new() { TraceId = traceId, Code = code.Length <= 64 ? code : code[..64], HttpStatus = status });
        Logs.Own(status >= 500 ? LogLevels.Warn : LogLevels.Info,
            $"OpenAI-compatible {context.Request.Method} {context.Request.Path} from " +
            $"{context.Items[DeviceItem] as string ?? GatewayRequestGuard.Address(context)} failed: {code} (HTTP {status}). Trace {traceId}.",
            "openai.failed|" + code);
        if (context.Response.HasStarted)
        {
            context.Abort();
            return;
        }
        if (retryAfter is { } seconds) context.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (status is 429 or 503 && code is "model_busy" or "owner_needs_it")
            context.Response.Headers["X-Martlet-Busy"] = message.Contains("owner", StringComparison.Ordinal) ? "owner" : "busy";
        if (context.Request.Protocol == "HTTP/1.1" &&
            (context.Request.ContentLength is > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding")))
            context.Response.Headers.Connection = "close";
        var bytes = GatewayOpenAiAnswer.ErrorBody(status, message, code, param);
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength = bytes.Length;
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        await context.Response.Body.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>Drops the clients to other hosts (when the gateway stops).</summary>
    internal void CloseOpenAi()
    {
        lock (openAiGate)
        {
            foreach (var kept in openAiClients.Values) kept.Client.Dispose();
            openAiClients.Clear();
        }
    }
}
