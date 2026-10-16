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

/// <summary>How many OpenAI-compatible requests may wait for a free slot on one host, and how long
/// (docs/OPENAI_ENDPOINT.md#when-every-model-is-busy-the-queue). A waiting request holds no slot.</summary>
internal sealed record GatewayOpenAiQueue
{
    /// <summary>Requests that may wait on one host at once.</summary>
    internal int Waiting { get; init; } = 16;
    /// <summary>Requests of one key that may wait on one host at once.</summary>
    internal int WaitingPerKey { get; init; } = 4;
    /// <summary>The longest a request without <c>stream</c> waits. Cloudflare and other proxies end a response that hasn't
    /// started after about 100 seconds, so the answer still has time after the wait.</summary>
    internal TimeSpan WholeWait { get; init; } = TimeSpan.FromSeconds(45);
    /// <summary>The longest a streamed request waits. It gets SSE comments while it waits, so proxies keep it open.</summary>
    internal TimeSpan StreamWait { get; init; } = TimeSpan.FromMinutes(2);
    /// <summary>When a waiting streamed request gets its first SSE comment (and its answer starts), and how often after.</summary>
    internal TimeSpan KeepAliveAfter { get; init; } = TimeSpan.FromSeconds(5);
    internal TimeSpan KeepAliveEvery { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>How often a request waiting on this host asks the other hosts that have its model again.</summary>
    internal TimeSpan PeerRetry { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// The OpenAI-compatible endpoint (docs/OPENAI_ENDPOINT.md). Every host serves it, so any host can be the front door that a
/// reverse proxy with a real certificate sends people to:
/// <list type="bullet">
/// <item><c>GET /v1/models</c> lists the language models this host runs and, for an API key, the models of every other host
/// of the network (asked with the same key, so each host checks it itself) and the network model names
/// (<see cref="OpenAiNetworkModels"/>) that fit one of them.</item>
/// <item><c>POST /v1/chat/completions</c>, <c>/v1/completions</c> and <c>/v1/embeddings</c> run on this host when it has the
/// model and a free slot; otherwise the front door sends the request on, with the same key, to a host of the network that has
/// it. A host never sends a request on twice. A network model name runs on a model it fits, a free one first. When every such
/// model is busy, the request waits for a free slot (<see cref="GatewayOpenAiQueue"/>) instead of being turned away at once.</item>
/// </list>
/// Only API keys with the <c>openai</c> scope may run requests; paired computers and friends may list the models. Requests
/// rank below all of the owner's work (or with the owner's background work for a key with <c>equal-priority</c>), so sharing
/// never makes the owner's own replies wait: the owner's requests never look at the queue. Only each host's own Ollama is
/// reachable: never a cloud provider or a key.
/// </summary>
internal sealed partial class GatewayHttpApplication
{
    private static readonly long OpenAiCreated = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private static readonly TimeSpan PeerModelsFresh = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PeerModelsRetry = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PeerModelsTimeout = TimeSpan.FromSeconds(3);
    private const int MaximumPeerModelsBytes = 256 * 1024;
    private const int MaximumModels = 64;
    /// <summary>Set by a front door on a request it sends on: how long (whole seconds) that host may keep it waiting for a free
    /// slot. Without it the host answers busy at once, so the front door can try the next host.</summary>
    private const string WaitHeader = "X-Martlet-Wait";
    /// <summary>On an answer: how long (milliseconds) the request waited for a free slot.</summary>
    private const string QueuedHeader = "X-Martlet-Queued";
    private readonly object openAiGate = new();
    private readonly Dictionary<string, (string Origin, string Spki, HttpClient Client)> openAiClients = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Host, string Key), (DateTimeOffset Until, string[] Models)> peerModels = [];

    /// <summary>The queue's limits on this host. Set by tests.</summary>
    internal GatewayOpenAiQueue OpenAiQueue { get; set; } = new();

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
    /// (this host's own only when another host asked) and the network model names that fit one of them, first; a paired
    /// computer or a friend sees this host's.</summary>
    private async ValueTask ListModelsAsync(HttpContext context, string? id)
    {
        EnsureEmptyRequest(context.Request);
        GatewayPrincipal? key = null;
        if (OpenAiToken(context.Request) is { } token) key = ApiKeys.AuthenticateOpenAi(token);
        else _ = authenticator.Authenticate(context.Request, friends: true);
        var models = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var model in inference.OpenAiRelays().Select(r => r.Relay.OpenAiModel).Distinct(StringComparer.Ordinal))
            models[model] = [identity.HostId];
        var whole = key is not null && !context.Request.Headers.ContainsKey(OpenAiEndpoint.ForwardedHeader);
        if (whole)
            foreach (var (peer, list) in await PeerModelsAsync(context.Request.Headers.Authorization.ToString(), key!.CredentialId,
                         context.RequestAborted).ConfigureAwait(false))
                foreach (var model in list)
                    (models.TryGetValue(model, out var hosts) ? hosts : models[model] = []).Add(peer.HostId);
        var network = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (whole)
            foreach (var name in OpenAiNetworkModels.All)
                if (OpenAiNetworkModels.Resolving(name, models.Keys) is { Count: > 0 } real)
                    network[name] = real;
        object Entry(string model) => network.TryGetValue(model, out var real)
            ? new Dictionary<string, object?>
            {
                ["id"] = model, ["object"] = "model", ["created"] = OpenAiCreated, ["owned_by"] = "martlet",
                ["martlet_hosts"] = real.SelectMany(m => models[m]).Distinct(StringComparer.Ordinal).ToArray(),
                ["martlet_models"] = real
            }
            : new Dictionary<string, object?>
            {
                ["id"] = model, ["object"] = "model", ["created"] = OpenAiCreated, ["owned_by"] = "martlet",
                ["martlet_hosts"] = models[model]
            };
        if (id is not null)
        {
            if (!models.ContainsKey(id) && !network.ContainsKey(id))
                throw new OpenAiFailure(404, OpenAiNetworkModels.IsNetworkModel(id)
                    ? OpenAiNetworkModels.NoModel(id, OpenAiEndpoint.ChatPath).Message
                    : $"The model '{id}' isn't on this Martlet network. GET /v1/models lists the models.", "model_not_found", "model");
            await WriteJsonAsync(context, 200, Entry(id), options: OpenAiJson).ConfigureAwait(false);
            return;
        }
        await WriteJsonAsync(context, 200, new Dictionary<string, object?>
        {
            ["object"] = "list",
            ["data"] = network.Keys.Concat(models.Keys.Order(StringComparer.Ordinal).Take(MaximumModels)).Select(Entry).ToArray()
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
                    .Where(m => m.Length is > 0 and <= 128 && !OpenAiNetworkModels.IsNetworkModel(m)).Distinct(StringComparer.Ordinal).Take(MaximumModels).ToArray();
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

    /// <summary>POST /v1/chat/completions, /v1/completions, /v1/embeddings: here when this host has the model (or, for a network
    /// model name, a model it fits) and a slot for the key's rank, else on another host of the network that has it. When every
    /// such host is busy, the request waits for a free slot (in this host's queue, or another host's) up to
    /// <see cref="GatewayOpenAiQueue"/>'s limits, else 429 (busy); 404 when no host has such a model.</summary>
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
        var network = OpenAiNetworkModels.IsNetworkModel(model);
        if (network && path == OpenAiEndpoint.EmbeddingsPath && model != OpenAiNetworkModels.Any)
            throw new OpenAiFailure(400, OpenAiNetworkModels.NoModel(model, path).Message, "invalid_request", "model");
        var stream = path != OpenAiEndpoint.EmbeddingsPath && document.RootElement.TryGetProperty("stream", out var streamed) &&
            streamed.ValueKind == JsonValueKind.True;
        var forwarded = context.Request.Headers.ContainsKey(OpenAiEndpoint.ForwardedHeader);
        var request = new OpenAiRequest(context, path, kind, model, network, stream, bytes, document.RootElement, principal,
            ApiKeys.EqualPriority(principal));
        bool Fits(string candidate) => network ? OpenAiNetworkModels.Answers(model, candidate, path) : candidate == model;

        // 1. A free slot now: this host's own models first, then the other hosts that have one (they answer busy at once).
        var here = inference.OpenAiRelays().Where(r => Fits(r.Relay.OpenAiModel)).ToArray();
        string? busy = null;
        foreach (var (routeId, relay) in here)
        {
            GatewayInferenceRouteRegistry.GatewayOutsideJob job;
            try { job = inference.BeginOutside(principal, routeId, request.EqualPriority); }
            catch (GatewayProtocolException error) when (error.Failure.Code is "job.busy" or "worker.quarantined" or "worker.unavailable")
            {
                busy = Busier(busy, error.Detail == GatewayInferenceRouteRegistry.OwnerDetail ? "owner" : "busy");
                continue;
            }
            using (job) await RunHereAsync(request, relay, job, TimeSpan.Zero).ConfigureAwait(false);
            return;
        }
        var authorization = context.Request.Headers.Authorization.ToString();
        var there = forwarded ? [] : (await PeerModelsAsync(authorization, principal.CredentialId, context.RequestAborted).ConfigureAwait(false))
            .SelectMany(p => p.Models.Where(Fits).Select(m => (p.Peer, Model: m))).ToArray();
        var reachable = new List<(GatewayOpenAiPeer Peer, string Model)>();
        foreach (var (peer, real) in there)
        {
            var sent = await ForwardOpenAiAsync(request, peer, real, authorization, TimeSpan.Zero, null).ConfigureAwait(false);
            if (sent is null) return;
            if (sent != "down") reachable.Add((peer, real));
            busy = Busier(busy, sent == "owner" ? "owner" : "busy");
        }
        if (here.Length == 0 && there.Length == 0)
            throw new OpenAiFailure(404, network ? OpenAiNetworkModels.NoModel(model, path).Message
                : $"The model '{model}' isn't on this Martlet network. GET /v1/models lists the models.", "model_not_found", "model");

        // 2. Wait for a free slot: in this host's queue when it has such a model (asking the other hosts again now and then),
        // else in the queue of another host that has it. A host a front door sent the request to waits only as long as asked.
        var wait = forwarded ? ForwardedWait(context.Request, stream) : stream ? OpenAiQueue.StreamWait : OpenAiQueue.WholeWait;
        var waitStarted = clock.GetTimestamp();
        var waited = false;
        if (wait > TimeSpan.Zero)
        {
            if (here.Length > 0)
            {
                waited = true;
                if (await WaitHereAsync(request, here, reachable, authorization, wait).ConfigureAwait(false)) return;
            }
            else
            {
                foreach (var (peer, real) in reachable)
                {
                    var remaining = wait - clock.GetElapsedTime(waitStarted);
                    if (remaining < TimeSpan.FromSeconds(1)) break;
                    waited = true;
                    var sent = await ForwardOpenAiAsync(request, peer, real, authorization, remaining, null).ConfigureAwait(false);
                    if (sent is null) return;
                    busy = Busier(busy, sent == "owner" ? "owner" : "busy");
                }
            }
        }
        var kept = waited ? $" Martlet kept it waiting {clock.GetElapsedTime(waitStarted).TotalSeconds:0} seconds for a free slot." : "";
        throw new OpenAiFailure(429, busy == "owner"
            ? $"'{model}' is busy with its owner's own work right now.{kept} Martlet shares it when its owner doesn't need it; try again in a moment."
            : $"'{model}' is busy right now.{kept} Try again in a moment.", "model_busy", null, 5);
    }

    private static string Busier(string? busy, string next) => busy == "owner" || next == "owner" ? "owner" : next;

    /// <summary>One OpenAI-compatible request as it arrived: the model it names (a network model name or a real model) and its
    /// body (read whole), with the key that sent it.</summary>
    private sealed record OpenAiRequest(HttpContext Context, string Path, string Kind, string Model, bool Network, bool Stream,
        byte[] Bytes, JsonElement Root, GatewayPrincipal Principal, bool EqualPriority)
    {
        /// <summary>" (for martlet@fast)" for a network model name, for the log.</summary>
        internal string For => Network ? $" (for {Model})" : "";
    }

    // How long a host may keep a request a front door sent on waiting (X-Martlet-Wait, whole seconds), at most its own limit; a
    // front door of an older version doesn't say, and then the request doesn't wait.
    private TimeSpan ForwardedWait(HttpRequest request, bool stream)
    {
        if (!request.Headers.TryGetValue(WaitHeader, out var values) || values.Count != 1 ||
            !int.TryParse(values[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var seconds) ||
            seconds <= 0)
            return TimeSpan.Zero;
        var most = stream ? OpenAiQueue.StreamWait : OpenAiQueue.WholeWait;
        return TimeSpan.FromSeconds(Math.Min(seconds, most.TotalSeconds));
    }

    /// <summary>Waits in this host's queue, for at most <paramref name="wait"/>, for a slot of one of <paramref name="here"/>. A
    /// streamed request gets SSE comments while it waits, so proxies keep it open; the other hosts that have such a model
    /// (<paramref name="there"/>) are asked again every few seconds. True when the request was answered, here or there; false
    /// when the wait ended first.</summary>
    private async Task<bool> WaitHereAsync(OpenAiRequest request, (string RouteId, IGatewayOpenAiRelay Relay)[] here,
        List<(GatewayOpenAiPeer Peer, string Model)> there, string authorization, TimeSpan wait)
    {
        var context = request.Context;
        var limits = OpenAiQueue;
        var waiter = inference.EnqueueOutside(request.Principal, here.Select(h => h.RouteId).ToArray(), request.EqualPriority,
            limits.Waiting, limits.WaitingPerKey);
        if (waiter is null)
        {
            Logs.Own(LogLevels.Info, $"Turned away an OpenAI-compatible {request.Kind} request from {request.Principal.Caller} on " +
                $"{request.Model}: the queue is full.", "openai.full|" + request.Kind);
            throw new OpenAiFailure(429, $"'{request.Model}' is busy and too many requests already wait for it. Try again in a moment.",
                "model_busy", null, 5);
        }
        var since = clock.GetTimestamp();
        Logs.Own(LogLevels.Info, $"An OpenAI-compatible {request.Kind} request from {request.Principal.Caller} on {request.Model} " +
            $"waits for a free slot ({inference.OutsideWaiting} waiting).", "openai.queued|" + request.Kind);
        GatewayInferenceRouteRegistry.GatewayOutsideJob? job = null;
        var left = false;
        var beat = limits.KeepAliveAfter;
        var ask = limits.PeerRetry;
        try
        {
            while (job is null)
            {
                var next = wait;
                if (request.Stream && beat < next) next = beat;
                if (there.Count > 0 && ask < next) next = ask;
                job = await inference.WaitOutsideAsync(waiter, next - clock.GetElapsedTime(since), context.RequestAborted).ConfigureAwait(false);
                if (job is not null) break;
                var now = clock.GetElapsedTime(since);
                if (now >= wait) return false;
                if (request.Stream && now >= beat)
                {
                    await KeepAliveAsync(context, now).ConfigureAwait(false);
                    beat = now + limits.KeepAliveEvery;
                }
                if (there.Count > 0 && now >= ask)
                {
                    // A slot given here first wins; while other hosts are asked, this request takes no slot here.
                    if (inference.StepAway(waiter) is { } early)
                    {
                        job = early;
                        break;
                    }
                    try
                    {
                        foreach (var (peer, real) in there.ToArray())
                        {
                            var sent = await ForwardOpenAiAsync(request, peer, real, authorization, TimeSpan.Zero, () =>
                            {
                                // Another host took it: give up the place here.
                                left = true;
                                inference.LeaveOutside(waiter)?.Dispose();
                            }).ConfigureAwait(false);
                            if (sent is null) return true;
                            if (sent == "down") there.Remove((peer, real));
                        }
                    }
                    finally
                    {
                        if (!left) inference.StepBack(waiter);
                    }
                    ask = clock.GetElapsedTime(since) + limits.PeerRetry;
                }
            }
        }
        finally
        {
            if (job is null && !left) inference.LeaveOutside(waiter)?.Dispose();
        }
        var relay = here.First(h => h.RouteId == job.Registration.Route.RouteId).Relay;
        using (job) await RunHereAsync(request, relay, job, clock.GetElapsedTime(since)).ConfigureAwait(false);
        return true;
    }

    private static readonly byte[] KeepAliveComment = ": waiting for a free model\n\n"u8.ToArray();
    private static readonly byte[] DoneEvent = "data: [DONE]\n\n"u8.ToArray();

    // Starts the streamed answer (when it hasn't started) and sends one SSE comment, which OpenAI's clients skip.
    private async Task KeepAliveAsync(HttpContext context, TimeSpan waited)
    {
        if (!context.Response.HasStarted)
        {
            StartAnswer(context, 200, true, null);
            context.Response.Headers[QueuedHeader] = Milliseconds(waited);
        }
        await context.Response.Body.WriteAsync(KeepAliveComment, context.RequestAborted).ConfigureAwait(false);
        await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
    }

    private static string Milliseconds(TimeSpan span) =>
        ((long)Math.Max(0, span.TotalMilliseconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static void StartAnswer(HttpContext context, int status, bool stream, string? host)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = stream ? "text/event-stream; charset=utf-8" : "application/json; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        if (host is not null) context.Response.Headers["X-Martlet-Host"] = host;
        if (stream) context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
    }

    private static bool Streaming(HttpContext context) =>
        context.Response.HasStarted && context.Response.ContentType?.StartsWith("text/event-stream", StringComparison.Ordinal) == true;

    private static byte[] SseEvent(byte[] json)
    {
        var bytes = new byte[json.Length + 8];
        "data: "u8.CopyTo(bytes);
        json.CopyTo(bytes, 6);
        bytes[^2] = (byte)'\n';
        bytes[^1] = (byte)'\n';
        return bytes;
    }

    // An answer of one JSON document (an error) to a request whose stream the wait already started: one event, then [DONE].
    private static async Task WriteAsEventAsync(HttpContext context, byte[] json, CancellationToken token)
    {
        byte[] line;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
            line = JsonSerializer.SerializeToUtf8Bytes(document.RootElement);
        }
        catch (JsonException)
        {
            line = GatewayOpenAiAnswer.ErrorBody(502, "The model's host gave an answer Martlet couldn't read.", "worker.failed");
        }
        await context.Response.Body.WriteAsync(SseEvent(line), token).ConfigureAwait(false);
        await context.Response.Body.WriteAsync(DoneEvent, token).ConfigureAwait(false);
        await context.Response.Body.FlushAsync(token).ConfigureAwait(false);
    }

    // The request body with `model` in place of the network model name, for the host that runs `model`.
    private static byte[] WithModel(JsonElement root, string model)
    {
        var output = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions
               { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, MaxDepth = 128 }))
        {
            writer.WriteStartObject();
            foreach (var property in root.EnumerateObject())
                if (property.NameEquals("model")) writer.WriteString("model", model);
                else property.WriteTo(writer);
            writer.WriteEndObject();
        }
        return output.WrittenSpan.ToArray();
    }

    /// <summary>Runs the request on this host's own model and streams its answer. Work of a higher rank, a revoked key, the
    /// deadline or the caller hanging up stops it. <paramref name="waited"/>: how long it waited in the queue.</summary>
    private async ValueTask RunHereAsync(OpenAiRequest request, IGatewayOpenAiRelay relay,
        GatewayInferenceRouteRegistry.GatewayOutsideJob job, TimeSpan waited)
    {
        var context = request.Context;
        var kind = request.Kind;
        var model = relay.OpenAiModel;
        using var deadline = new CancellationTokenSource(OpenAiEndpoint.MaximumDuration, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, job.Stopped, deadline.Token);
        var started = clock.GetTimestamp();
        var queued = waited > TimeSpan.Zero ? $" after waiting {waited.TotalMilliseconds:0} ms for a slot" : "";
        try
        {
            var answer = await relay.RunOpenAiAsync(request.Path, request.Root, linked.Token).ConfigureAwait(false);
            var stream = answer.ContentType == "text/event-stream";
            if (!context.Response.HasStarted)
            {
                StartAnswer(context, answer.Status, stream, identity.HostId);
                if (waited > TimeSpan.Zero) context.Response.Headers[QueuedHeader] = Milliseconds(waited);
            }
            else if (!stream)
            {
                // The wait already started the stream: the answer (an error) becomes its one event.
                using var whole = new MemoryStream();
                await foreach (var piece in answer.Body.WithCancellation(linked.Token).ConfigureAwait(false)) whole.Write(piece.Span);
                await WriteAsEventAsync(context, whole.ToArray(), linked.Token).ConfigureAwait(false);
                Logs.Own(LogLevels.Info, $"OpenAI-compatible {kind} request from {job.Principal.Caller} on {model}{request.For} ended " +
                    $"here{queued} with HTTP {answer.Status} in its stream.", "openai.done|" + kind);
                return;
            }
            await foreach (var piece in answer.Body.WithCancellation(linked.Token).ConfigureAwait(false))
            {
                await context.Response.Body.WriteAsync(piece, linked.Token).ConfigureAwait(false);
                if (stream) await context.Response.Body.FlushAsync(linked.Token).ConfigureAwait(false);
            }
            Logs.Own(LogLevels.Info, $"OpenAI-compatible {kind} request from {job.Principal.Caller} on {model}{request.For} finished " +
                $"here{queued} in {clock.GetElapsedTime(started).TotalMilliseconds:0} ms (HTTP {answer.Status}).", "openai.done|" + kind);
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
            if (Streaming(context))
            {
                // The stream already started: say why it ends, as OpenAI's own streams do, then end it.
                try
                {
                    await context.Response.Body.WriteAsync(SseEvent(GatewayOpenAiAnswer.ErrorBody(status, message, code)), context.RequestAborted)
                        .ConfigureAwait(false);
                    await context.Response.Body.WriteAsync(DoneEvent, context.RequestAborted).ConfigureAwait(false);
                    await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
                }
                catch (Exception error) when (error is IOException or OperationCanceledException) { context.Abort(); }
            }
            else context.Abort();
        }
    }

    /// <summary>Sends the request on to <paramref name="peer"/>, for its model <paramref name="model"/>, with the caller's own key,
    /// and streams its answer back. <paramref name="wait"/>: how long that host may keep it waiting for a free slot (zero: it
    /// answers busy at once). Returns null when it answered (<paramref name="accepted"/> runs first), "owner" or "busy" when it
    /// turned the request away, or "down" when it didn't answer, so the next host can be tried.</summary>
    private async Task<string?> ForwardOpenAiAsync(OpenAiRequest request, GatewayOpenAiPeer peer, string model, string authorization,
        TimeSpan wait, Action? accepted)
    {
        var context = request.Context;
        var kind = request.Kind;
        var body = request.Network ? WithModel(request.Root, model) : request.Bytes;
        using var deadline = new CancellationTokenSource(OpenAiEndpoint.MaximumDuration, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, deadline.Token);
        using var message = new HttpRequestMessage(HttpMethod.Post, peer.Origin + request.Path) { Content = new ByteArrayContent(body) };
        message.Content.Headers.ContentType = new("application/json");
        message.Headers.TryAddWithoutValidation("Authorization", authorization);
        message.Headers.TryAddWithoutValidation(OpenAiEndpoint.ForwardedHeader, identity.HostId);
        if (wait >= TimeSpan.FromSeconds(1))
            message.Headers.TryAddWithoutValidation(WaitHeader, ((int)wait.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture));
        var started = clock.GetTimestamp();
        HttpResponseMessage response;
        try { response = await PeerClient(peer).SendAsync(message, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false); }
        catch (Exception error) when (error is HttpRequestException or GatewayProtocolException ||
            error is OperationCanceledException && !context.RequestAborted.IsCancellationRequested)
        {
            ForgetPeerModels(peer.HostId);
            Logs.Own(LogLevels.Warn, $"Couldn't send an OpenAI-compatible {kind} request on to {peer.HostId}: it didn't answer.",
                "openai.peer|" + peer.HostId);
            return "down";
        }
        using (response)
        {
            var status = (int)response.StatusCode;
            if (status is 429 or 503) return response.Headers.TryGetValues("X-Martlet-Busy", out var why) && why.Contains("owner") ? "owner" : "busy";
            accepted?.Invoke();
            var type = response.Content.Headers.ContentType?.MediaType ?? "application/json";
            var stream = type == "text/event-stream";
            try
            {
                if (!context.Response.HasStarted)
                {
                    StartAnswer(context, status, stream, response.Headers.TryGetValues("X-Martlet-Host", out var host)
                        ? host.FirstOrDefault() ?? peer.HostId : peer.HostId);
                    if (response.Headers.TryGetValues(QueuedHeader, out var queued) && queued.FirstOrDefault() is { Length: <= 20 } waited)
                        context.Response.Headers[QueuedHeader] = waited;
                }
                else if (!stream)
                {
                    // This host's wait already started the stream: the other host's answer (an error) becomes its one event.
                    await WriteAsEventAsync(context, await ReadAllAsync(response, 4 * 1024 * 1024, linked.Token).ConfigureAwait(false), linked.Token)
                        .ConfigureAwait(false);
                    return null;
                }
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
            Logs.Own(LogLevels.Info, $"OpenAI-compatible {kind} request from {request.Principal.Caller} on {model}{request.For} sent on to " +
                $"{peer.HostId} (HTTP {status}, {clock.GetElapsedTime(started).TotalMilliseconds:0} ms).", "openai.sent|" + peer.HostId);
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
            if (!Streaming(context))
            {
                context.Abort();
                return;
            }
            // The wait already started the stream: say why it ends, as OpenAI's own streams do.
            try { await WriteAsEventAsync(context, GatewayOpenAiAnswer.ErrorBody(status, message, code, param), context.RequestAborted).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or OperationCanceledException) { context.Abort(); }
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
