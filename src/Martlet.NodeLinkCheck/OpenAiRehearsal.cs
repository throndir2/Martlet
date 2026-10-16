using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Core.Access;
using Martlet.Core.Contracts;
using Martlet.Core.Network;
using Martlet.Gateway;
using Martlet.Gateway.Ollama;

namespace Martlet.NodeLinkCheck;

/// <summary>
/// Rehearses the OpenAI-compatible endpoint (docs/OPENAI_ENDPOINT.md) end to end on this PC with the production code: two real
/// gateways (Kestrel, pinned TLS) on 127.0.0.1, each with the real Ollama relay over a fixture Ollama (canned text, NOT AI) and
/// its own model, joined in one Martlet network by a simulated desktop with its real pairing client and network sync. The
/// desktop makes keys with the Share page's scopes and syncs them to both hosts. An app outside the network is played by a plain
/// HttpClient that sends Authorization: Bearer to the front door, as an app behind a reverse proxy does. Nothing leaves
/// loopback, nothing is written to disk or the credential vault.
/// </summary>
internal static class OpenAiRehearsal
{
    private const string FrontModel = "fixture-alpha:1b";
    private const string GpuModel = "fixture-beta:7b";

    internal static async Task<(bool Ok, object Report)> RunAsync(CancellationToken token)
    {
        var steps = new List<(string Name, bool Ok, string Detail)>();
        var started = DateTimeOffset.UtcNow;
        await using var front = await LabHost.StartAsync("lab-front-door", FrontModel);
        await using var gpu = await LabHost.StartAsync("lab-gpu-box", GpuModel);
        using var networkKey = NetworkKey.Create("lab-desktop-share");
        var desktop = new LabDesktop(networkKey, "LAB-SHARE");
        var keys = ApiKeyList.Empty;
        IssuedApiKey? ana = null, ben = null, assistant = null;

        async Task Run(string name, Func<Task<(bool Ok, string Detail)>> action)
        {
            try
            {
                var (ok, detail) = await action();
                steps.Add((name, ok, detail));
            }
            catch (Exception error) when (error is not OperationCanceledException || !token.IsCancellationRequested)
            {
                steps.Add((name, false, $"{error.GetType().Name}: {error.Message}"));
            }
        }

        async Task<string> SyncKeysAsync()
        {
            var notes = new List<string>();
            foreach (var host in new[] { front, gpu })
            {
                var copy = await desktop.MergeKeysAsync(host, keys, token);
                keys = ApiKeyList.Merge(keys, copy.Keys);
                notes.Add($"{host.HostId}: {copy.Keys}");
            }
            return string.Join("; ", notes);
        }

        await Run("The desktop pairs with both hosts and joins them in one Martlet network (each host's roster lists the other)", async () =>
        {
            await desktop.PairByCodeAsync(front, token);
            await desktop.SyncAsync(token);
            await desktop.PairByCodeAsync(gpu, token);
            await desktop.SyncAsync(token);
            await desktop.SyncAsync(token);
            var frontKnows = front.Roster?.Host(gpu.HostId) is { Removed: false, Origin: { } origin } && origin == gpu.Origin;
            var gpuKnows = gpu.Roster?.Host(front.HostId) is { Removed: false };
            return (frontKnows && gpuKnows, $"front door lists lab-gpu-box: {frontKnows}; lab-gpu-box lists the front door: {gpuKnows}");
        });
        await Run("Share › Give someone access makes \"Ana\" (openai) and \"Ben\" (openai, equal priority); with \"Home Assistant\" (voice) they reach both hosts as verifiers only", async () =>
        {
            var now = DateTimeOffset.UtcNow;
            (keys, ana) = keys.Create("Ana", OpenAiEndpoint.Scopes(false), null, networkKey.DeviceId, now);
            (keys, ben) = keys.Create("Ben", OpenAiEndpoint.Scopes(true), null, networkKey.DeviceId, now);
            (keys, assistant) = keys.Create("Home Assistant", [ApiKeyScopes.Voice], null, networkKey.DeviceId, now);
            var detail = await SyncKeysAsync();
            var secrets = new[] { ana.Token, ben.Token }.Select(t => t[31..]).ToArray();
            var clean = new[] { front.SavedKeys, gpu.SavedKeys }.All(s => s is not null && !secrets.Any(s.Contains) && s.Contains(ana.Key.Verifier!));
            return (clean, $"{detail}; no secret on either host: {clean}");
        });
        await Run("GET /v1/models at the front door lists both hosts' models, each with the host that runs it", async () =>
        {
            var result = await App.CallAsync(front, ana!.Token, HttpMethod.Get, "/v1/models");
            var models = result.Json?.GetProperty("data").EnumerateArray()
                .ToDictionary(m => m.GetProperty("id").GetString()!, m => m.GetProperty("martlet_hosts")[0].GetString()) ?? [];
            return (result.Status == 200 && models.GetValueOrDefault(FrontModel) == front.HostId && models.GetValueOrDefault(GpuModel) == gpu.HostId,
                $"HTTP {result.Status}: {string.Join(", ", models.Select(m => $"{m.Key} on {m.Value}"))}");
        });
        await Run("The desktop reads each host's own models with its signed pairing, as the Share page does", async () =>
        {
            var seen = new List<string>();
            foreach (var host in new[] { front, gpu })
                seen.Add($"{host.HostId}: {string.Join(", ", await desktop.ModelsAsync(host, token))}");
            return (seen[0].EndsWith(FrontModel, StringComparison.Ordinal) && seen[1].EndsWith(GpuModel, StringComparison.Ordinal), string.Join("; ", seen));
        });
        await Run("A chat for the front door's own model runs there, with a context size Ollama already has and the model kept loaded (fixture Ollama, NOT AI)", async () =>
        {
            var result = await App.CallAsync(front, ana!.Token, HttpMethod.Post, "/v1/chat/completions", Chat(FrontModel, false));
            var text = result.Json?.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
            var native = front.Ollama.LastChat;
            var context = native?.GetProperty("options").GetProperty("num_ctx").GetInt32();
            var kept = native?.GetProperty("keep_alive").GetInt32();
            return (result.Status == 200 && text == FixtureOllama.Reply && result.Host == front.HostId && context > 0 && kept == -1,
                $"HTTP {result.Status} from {result.Host}: \"{text}\"; Ollama got num_ctx {context}, keep_alive {kept}");
        });
        await Run("A streamed chat for the other host's model is sent on to it with the same key and arrives as server-sent events", async () =>
        {
            var (status, host, text, done, _, _) = await App.StreamAsync(front, ana!.Token, Chat(GpuModel, true), token);
            return (status == 200 && host == gpu.HostId && text == FixtureOllama.Reply && done && gpu.Ollama.Chats == 1 && front.Ollama.Chats == 1,
                $"HTTP {status} from {host}: \"{text}\", [DONE] {done}; chats on the front door {front.Ollama.Chats}, on lab-gpu-box {gpu.Ollama.Chats}");
        });
        await Run("GET /v1/models lists the network model names that fit a model (martlet@any, martlet@fast), with the real models each may run on", async () =>
        {
            var result = await App.CallAsync(front, ana!.Token, HttpMethod.Get, "/v1/models");
            var entries = result.Json?.GetProperty("data").EnumerateArray().ToDictionary(m => m.GetProperty("id").GetString()!,
                m => m.TryGetProperty("martlet_models", out var real) ? real.EnumerateArray().Select(r => r.GetString()!).ToArray() : null) ?? [];
            var any = entries.GetValueOrDefault(OpenAiNetworkModels.Any) ?? [];
            var fast = entries.GetValueOrDefault(OpenAiNetworkModels.Fast) ?? [];
            return (result.Status == 200 && any.Order(StringComparer.Ordinal).SequenceEqual([FrontModel, GpuModel]) && fast.SequenceEqual([FrontModel]) &&
                    !entries.ContainsKey(OpenAiNetworkModels.Smart),
                $"HTTP {result.Status}: {string.Join("; ", entries.Where(e => e.Value is not null).Select(e => $"{e.Key} -> {string.Join(", ", e.Value!)}"))}; " +
                $"martlet@smart listed: {entries.ContainsKey(OpenAiNetworkModels.Smart)}");
        });
        await Run("A chat for martlet@fast runs on the front door's Fast model and names it; martlet@smart (no Smart model) and martlet@fast for embeddings are refused clearly", async () =>
        {
            var fast = await App.CallAsync(front, ana!.Token, HttpMethod.Post, "/v1/chat/completions", Chat(OpenAiNetworkModels.Fast, false));
            var smart = await App.CallAsync(front, ana.Token, HttpMethod.Post, "/v1/chat/completions", Chat(OpenAiNetworkModels.Smart, false));
            var embed = await App.CallAsync(front, ana.Token, HttpMethod.Post, "/v1/embeddings",
                JsonSerializer.Serialize(new { model = OpenAiNetworkModels.Fast, input = "hello" }));
            return (fast.Status == 200 && fast.Model == FrontModel && fast.Host == front.HostId && smart.Status == 404 && smart.Code == "model_not_found" &&
                    embed.Status == 400 && embed.Type == "invalid_request_error",
                $"martlet@fast: HTTP {fast.Status}, model {fast.Model} from {fast.Host}; martlet@smart: {smart.Status} {smart.Code}; " +
                $"martlet@fast embeddings: {embed.Status} {embed.Type}");
        });
        await Run("Calls without a key, with a voice-only key or for a model the network lacks are refused in OpenAI's error form", async () =>
        {
            var none = await App.CallAsync(front, null, HttpMethod.Post, "/v1/chat/completions", Chat(FrontModel, false));
            var voice = await App.CallAsync(front, assistant!.Token, HttpMethod.Get, "/v1/models");
            var missing = await App.CallAsync(front, ana!.Token, HttpMethod.Post, "/v1/chat/completions", Chat("gpt-4o", false));
            var other = await App.CallAsync(front, ana.Token, HttpMethod.Get, "/martlet/v1/status");
            return (none.Status == 401 && none.Type == "authentication_error" && voice.Status == 403 && voice.Type == "permission_error" &&
                    missing.Status == 404 && missing.Code == "model_not_found" && other.Status == 403,
                $"no key {none.Status} {none.Type}; voice key {voice.Status} {voice.Type}; unknown model {missing.Status} {missing.Code}; " +
                $"the openai key on /martlet/v1/status {other.Status}");
        });
        await Run("Your Martlet first: the owner's reply on the front door stops Ana's streamed request and takes its slot", async () =>
        {
            front.Ollama.Hold = true;
            var shared = App.StreamAsync(front, ana!.Token, Chat(FrontModel, true), token);
            await front.Ollama.FirstSent.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            front.Ollama.Hold = false;
            var reply = await desktop.ReplyAsync(front, token);
            var (status, _, _, _, error, _) = await shared;
            return (status == 200 && error == "owner_needs_it" && reply == FixtureOllama.Reply,
                $"Ana's stream ended with {error ?? "no error"}; the owner's reply: \"{reply}\"");
        });
        await Run("While the owner's reply holds the front door's model, martlet@any runs at once on lab-gpu-box's free model, and Ana's requests for the front door's model wait in the queue (her stream gets SSE comments) and run once the reply ends", async () =>
        {
            front.Ollama.Hold = true;
            var reply = desktop.ReplyAsync(front, token);
            await front.Ollama.FirstSent.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            front.Ollama.Hold = false;
            var any = await App.CallAsync(front, ana!.Token, HttpMethod.Post, "/v1/chat/completions", Chat(OpenAiNetworkModels.Any, false));
            var waiting = App.CallAsync(front, ana.Token, HttpMethod.Post, "/v1/chat/completions", Chat(FrontModel, false));
            var streamed = App.StreamAsync(front, ana.Token, Chat(FrontModel, true), token);
            // Past the first SSE comment, which comes 5 seconds into the wait.
            await Task.Delay(TimeSpan.FromSeconds(7), token);
            var held = !waiting.IsCompleted && !streamed.IsCompleted;
            front.Ollama.Release();
            var owner = await reply;
            var queued = await waiting;
            var (status, host, text, done, error, comments) = await streamed;
            return (any.Status == 200 && any.Model == GpuModel && any.Host == gpu.HostId && held && owner == FixtureOllama.Reply &&
                    queued.Status == 200 && queued.Host == front.HostId && queued.Queued > 0 &&
                    status == 200 && comments > 0 && text == FixtureOllama.Reply && done && error is null,
                $"martlet@any: HTTP {any.Status}, model {any.Model} from {any.Host}; Ana's requests still waiting after 7 s: {held}; " +
                $"the owner's reply: \"{owner}\"; then Ana's request: HTTP {queued.Status} from {queued.Host} after {queued.Queued} ms in the queue; " +
                $"her stream: HTTP {status}, {comments} SSE comments, \"{text}\", [DONE] {done}, error {error ?? "none"} (host header {host ?? "none: the stream started while it waited"})");
        });
        await Run("Revoking Ana's key on the desktop stops it on both hosts at the next sync; Ben keeps working", async () =>
        {
            keys = keys.Revoke(ana!.Key.Id, networkKey.DeviceId, DateTimeOffset.UtcNow);
            await SyncKeysAsync();
            var frontAnswer = await App.CallAsync(front, ana.Token, HttpMethod.Get, "/v1/models");
            var gpuAnswer = await App.CallAsync(gpu, ana.Token, HttpMethod.Get, "/v1/models");
            var benAnswer = await App.CallAsync(front, ben!.Token, HttpMethod.Post, "/v1/chat/completions", Chat(GpuModel, false));
            return (frontAnswer.Status == 401 && gpuAnswer.Status == 401 && benAnswer.Status == 200 && benAnswer.Host == gpu.HostId,
                $"Ana on the front door {frontAnswer.Status}, on lab-gpu-box {gpuAnswer.Status}; Ben {benAnswer.Status} from {benAnswer.Host}");
        });

        var ok = steps.All(s => s.Ok);
        return (ok, new
        {
            ok,
            started,
            seconds = Math.Round((DateTimeOffset.UtcNow - started).TotalSeconds, 1),
            note = "Two real gateways on 127.0.0.1 with fixture Ollama servers (NOT AI); no real model, proxy, certificate authority or second PC.",
            steps = steps.Select(s => new { name = s.Name, ok = s.Ok, detail = s.Detail })
        });
    }

    private static string Chat(string model, bool stream) => JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["model"] = model, ["stream"] = stream, ["messages"] = new[] { new { role = "user", content = "Hello!" } }
    });

    /// <summary>A simulated desktop: its network key, its pairings (secrets in memory) and its network state.</summary>
    private sealed class LabDesktop(NetworkKey key, string name)
    {
        private readonly Dictionary<string, (Audio2FaceHostPairing Pairing, string Secret)> pairings = new(StringComparer.Ordinal);
        private readonly NetworkSyncEngine engine = new(key, name);
        private NetworkLocalState state = NetworkLocalState.Empty;

        internal async Task PairByCodeAsync(LabHost host, CancellationToken token)
        {
            var card = host.Server.Pairing.OpenCodeWindow(new() { Roles = [GatewayRole.Voice] });
            var (pairing, secret) = await Audio2FaceHostClient.PairWithCodeAsync(host.Origin, card.Code.Reveal(), key.DeviceId, name, token);
            pairings[pairing.HostId] = (pairing, secret);
            state = state.WithAdopted(pairing.HostId);
        }

        internal async Task SyncAsync(CancellationToken token)
        {
            var result = await engine.SyncAsync(state, pairings.Values.Select(p => p.Pairing).ToArray(),
                pairing => new Audio2FaceHostConnection(pairing, pairings[pairing.HostId].Secret), token);
            foreach (var (pairing, secret) in result.Paired) pairings[pairing.HostId] = (pairing, secret);
            state = NetworkLocalState.Parse(result.State.Write());
        }

        private Audio2FaceHostConnection Connect(LabHost host) => new(pairings[host.HostId].Pairing, pairings[host.HostId].Secret);

        internal async Task<HostApiKeys> MergeKeysAsync(LabHost host, ApiKeyList list, CancellationToken token)
        {
            using var connection = Connect(host);
            return await connection.MergeApiKeysAsync(list, token);
        }

        internal async Task<IReadOnlyList<string>> ModelsAsync(LabHost host, CancellationToken token)
        {
            using var connection = Connect(host);
            return await connection.ReadOpenAiModelsAsync(token);
        }

        internal async Task<string> ReplyAsync(LabHost host, CancellationToken token)
        {
            using var connection = Connect(host);
            var route = (await connection.ReadRoutesAsync(token)).Single(r => r.RouteId == HostRoute.OllamaChatRouteId);
            var text = new StringBuilder();
            await foreach (var delta in connection.StreamChatAsync(route,
                new CorrelationIds { SessionId = Guid.NewGuid(), TurnId = Guid.NewGuid(), RequestId = Guid.NewGuid() }, 1,
                DateTimeOffset.UtcNow.AddSeconds(30), null, [], "Are you there?", 0.7, 256, 8_192, cancellationToken: token))
                text.Append(delta);
            return text.ToString();
        }
    }

    /// <summary>An app outside the network: a plain HTTPS client with the key as Authorization: Bearer. A reverse proxy in front
    /// of a host doesn't check the host's own certificate either (nginx proxy_ssl_verify off), so this one pins it.</summary>
    private static class App
    {
        internal sealed record Result(int Status, JsonElement? Json, string? Type, string? Code, string? Host, long Queued)
        {
            /// <summary>The model the answer names (the real model that ran it).</summary>
            internal string? Model => Json is { ValueKind: JsonValueKind.Object } root && root.TryGetProperty("model", out var model) ? model.GetString() : null;
        }

        private static HttpClient Client(LabHost host)
        {
            var handler = new SocketsHttpHandler();
            handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                certificate is X509Certificate2 presented &&
                "sha256:" + Convert.ToHexStringLower(SHA256.HashData(presented.PublicKey.ExportSubjectPublicKeyInfo())) == host.Identity.SpkiFingerprint;
            return new HttpClient(handler) { BaseAddress = new Uri(host.Origin), Timeout = TimeSpan.FromSeconds(30) };
        }

        private static HttpRequestMessage Request(HttpMethod method, string path, string? key, string? json)
        {
            var request = new HttpRequestMessage(method, path);
            if (key is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            return request;
        }

        internal static async Task<Result> CallAsync(LabHost host, string? key, HttpMethod method, string path, string? json = null)
        {
            using var http = Client(host);
            using var response = await http.SendAsync(Request(method, path, key, json));
            var body = await response.Content.ReadAsStringAsync();
            JsonElement? parsed = null;
            try { parsed = JsonDocument.Parse(body).RootElement.Clone(); }
            catch (JsonException) { }
            var error = parsed is { ValueKind: JsonValueKind.Object } root && root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object ? e : (JsonElement?)null;
            return new((int)response.StatusCode, parsed, error?.GetProperty("type").GetString(), error?.GetProperty("code").GetString(),
                response.Headers.TryGetValues("X-Martlet-Host", out var hosts) ? hosts.FirstOrDefault() : null, Queued(response));
        }

        // How long the request waited for a free slot (X-Martlet-Queued, milliseconds); 0 when it didn't wait.
        private static long Queued(HttpResponseMessage response) =>
            response.Headers.TryGetValues("X-Martlet-Queued", out var values) && long.TryParse(values.FirstOrDefault(), out var waited) ? waited : 0;

        /// <summary>Streams a chat to its end: the status, the host that answered, the text, whether [DONE] came, the code of
        /// an error event, and how many SSE comments (sent while it waited for a free slot) came.</summary>
        internal static async Task<(int Status, string? Host, string Text, bool Done, string? Error, int Comments)> StreamAsync(LabHost host,
            string key, string json, CancellationToken token)
        {
            using var http = Client(host);
            using var response = await http.SendAsync(Request(HttpMethod.Post, "/v1/chat/completions", key, json), HttpCompletionOption.ResponseHeadersRead, token);
            var from = response.Headers.TryGetValues("X-Martlet-Host", out var hosts) ? hosts.FirstOrDefault() : null;
            var text = new StringBuilder();
            string? error = null;
            var done = false;
            var comments = 0;
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(token));
            while (await reader.ReadLineAsync(token) is { } line)
            {
                if (line.StartsWith(':')) { comments++; continue; }
                if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
                if (line == "data: [DONE]") { done = true; break; }
                var chunk = JsonDocument.Parse(line[6..]).RootElement;
                if (chunk.TryGetProperty("error", out var failure)) { error = failure.GetProperty("code").GetString(); continue; }
                foreach (var choice in chunk.GetProperty("choices").EnumerateArray())
                    if (choice.GetProperty("delta").TryGetProperty("content", out var piece)) text.Append(piece.GetString());
            }
            return ((int)response.StatusCode, from, text.ToString(), done, error, comments);
        }

    }

    /// <summary>A real gateway on 127.0.0.1 with the Ollama relay over a fixture Ollama, an in-memory network.json and api-keys.json.</summary>
    private sealed class LabHost : IAsyncDisposable, IGatewayApiKeyStorage, IGatewayNetworkStorage, IGatewayAuditSink
    {
        private X509Certificate2 certificate = null!;
        private GatewayListenerHandle? listener;
        private OllamaRelayWorker? worker;
        private byte[]? savedKeys;
        private byte[]? savedRoster;
        internal GatewayServer Server { get; private set; } = null!;
        internal GatewayHostIdentity Identity { get; private set; } = null!;
        internal FixtureOllama Ollama { get; private set; } = null!;
        internal string HostId { get; private init; } = "";
        internal string Origin { get; private set; } = "";
        internal string? SavedKeys => savedKeys is null ? null : Encoding.UTF8.GetString(savedKeys);
        internal NetworkRoster? Roster => savedRoster is null ? null : NetworkRoster.Parse(savedRoster);

        internal static async Task<LabHost> StartAsync(string hostId, string model)
        {
            var host = new LabHost { HostId = hostId, certificate = Certificate() };
            try
            {
                host.Identity = GatewayHostIdentity.FromCertificate(hostId, host.certificate);
                host.Origin = $"https://127.0.0.1:{FreePort()}";
                var origin = new GatewayOrigin(host.Origin);
                host.Ollama = new FixtureOllama();
                host.worker = new OllamaRelayWorker(new Uri("http://127.0.0.1:11434/"), model, handler: host.Ollama);
                host.Server = new GatewayServer(host.Identity, origin, [], host, inferenceWorkers: [host.worker]);
                host.Server.AttachApiKeyStorage(host);
                host.Server.AttachNetworkStorage(host);
                host.listener = await host.Server.StartAsync(new GatewayTlsBinding(origin, host.Identity, host.certificate), new KestrelGatewayListenerFactory());
                return host;
            }
            catch
            {
                await host.DisposeAsync();
                throw;
            }
        }

        byte[]? IGatewayApiKeyStorage.Load() => savedKeys;
        void IGatewayApiKeyStorage.Save(byte[] bytes) => savedKeys = bytes;
        byte[]? IGatewayNetworkStorage.Load() => savedRoster;
        void IGatewayNetworkStorage.Save(byte[] bytes) => savedRoster = bytes;
        public void Record(GatewayAuditEvent gatewayEvent) { }

        public async ValueTask DisposeAsync()
        {
            if (listener is not null) await listener.DisposeAsync();
            if (worker is not null) await worker.DisposeAsync();
            certificate?.Dispose();
        }

        private static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            try { return ((IPEndPoint)probe.LocalEndpoint).Port; }
            finally { probe.Stop(); }
        }

        private static X509Certificate2 Certificate()
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest("CN=Martlet OpenAI rehearsal (fixture)", key, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
            var names = new SubjectAlternativeNameBuilder();
            names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
            var now = DateTimeOffset.UtcNow;
            using var ephemeral = request.CreateSelfSigned(now.AddDays(-1), now.AddDays(1));
            var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            var pfx = ephemeral.Export(X509ContentType.Pkcs12, password);
            try { return X509CertificateLoader.LoadPkcs12(pfx, password, X509KeyStorageFlags.UserKeySet); }
            finally { CryptographicOperations.ZeroMemory(pfx); }
        }
    }

    /// <summary>Stands in for Ollama's native /api/chat (FIXTURE, NOT AI): a canned reply, streamed in two pieces or whole. With
    /// <see cref="Hold"/> the second piece waits until <see cref="Release"/> or the request is dropped; each new hold starts a
    /// new <see cref="FirstSent"/>.</summary>
    internal sealed class FixtureOllama : HttpMessageHandler
    {
        internal const string Reply = "Hello from the fixture model.";
        private int chats;
        private bool hold;
        private TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Chats => Volatile.Read(ref chats);
        internal JsonElement? LastChat { get; private set; }
        internal TaskCompletionSource FirstSent { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Hold
        {
            get => hold;
            set
            {
                if (value && !hold)
                {
                    released = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    FirstSent = new(TaskCreationOptions.RunContinuationsAsynchronously);
                }
                hold = value;
            }
        }

        /// <summary>Lets the held requests finish.</summary>
        internal void Release() => released.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath != "/api/chat")
                return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"error":"not found"}""") };
            Interlocked.Increment(ref chats);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            LastChat = body.RootElement.Clone();
            if (!body.RootElement.TryGetProperty("stream", out var stream) || stream.ValueKind != JsonValueKind.True)
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        message = new { role = "assistant", content = Reply }, done = true, done_reason = "stop", prompt_eval_count = 5, eval_count = 6
                    }))
                };
            var first = Encoding.UTF8.GetBytes("""{"message":{"role":"assistant","content":"Hello "},"done":false}""" + "\n");
            var rest = Encoding.UTF8.GetBytes("""{"message":{"role":"assistant","content":"from the fixture model."},"done":false}""" + "\n" +
                """{"message":{"role":"assistant","content":""},"done":true,"done_reason":"stop"}""" + "\n");
            var content = new StreamContent(new HeldStream(first, rest, Hold ? released.Task : Task.CompletedTask, FirstSent));
            content.Headers.ContentType = new("application/x-ndjson");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }
    }

    private sealed class HeldStream(byte[] first, byte[] rest, Task release, TaskCompletionSource firstSent) : Stream
    {
        private int stage;
        private int offset;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (stage < 2)
            {
                var chunk = stage == 0 ? first : rest;
                if (offset < chunk.Length)
                {
                    var count = Math.Min(buffer.Length, chunk.Length - offset);
                    chunk.AsMemory(offset, count).CopyTo(buffer);
                    offset += count;
                    return count;
                }
                stage++;
                offset = 0;
                if (stage == 1)
                {
                    firstSent.TrySetResult();
                    await release.WaitAsync(cancellationToken);
                }
            }
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
