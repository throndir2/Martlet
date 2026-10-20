using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Martlet.HostClient;
using Martlet.Core.Access;
using Martlet.Core.Network;
using Martlet.Gateway;
using Martlet.Gateway.Ollama;

namespace Martlet.NodeLinkCheck;

/// <summary>
/// Rehearses the front door (docs/OPENAI_ENDPOINT.md) end to end on this PC with the production code: two real gateways
/// (Kestrel, pinned TLS) on 127.0.0.1, each with the real Ollama relay over a fixture Ollama (canned text, NOT AI), joined in
/// one Martlet network by a simulated desktop's real pairing and network sync. The first host's front door is open on its own
/// port. A browser and an app outside the network are played by plain HTTPS clients on that port only, as a reverse proxy,
/// Tailscale Funnel or a tunnel sends them. Nothing leaves loopback; nothing is written to disk or the credential vault.
/// </summary>
internal static partial class FrontDoorRehearsal
{
    private const string FrontModel = "fixture-alpha:1b";
    private const string GpuModel = "fixture-beta:7b";
    private const string Reply = "Hello from the fixture model.";

    internal static async Task<(bool Ok, object Report)> RunAsync(CancellationToken token)
    {
        var steps = new List<(string Name, bool Ok, string Detail)>();
        var started = DateTimeOffset.UtcNow;
        var doorPort = FreePort();
        await using var front = await LabHost.StartAsync("lab-front-door", FrontModel, doorPort);
        await using var gpu = await LabHost.StartAsync("lab-gpu-box", GpuModel, null);
        using var networkKey = NetworkKey.Create("lab-desktop-front-door");
        var desktop = new LabDesktop(networkKey, "LAB-FRONT-DOOR");
        var keys = ApiKeyList.Empty;
        IssuedApiKey? ana = null, assistant = null;
        using var browser = new Visitor(front, doorPort);
        string? cookies = null;

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

        async Task SyncKeysAsync()
        {
            foreach (var host in new[] { front, gpu })
                keys = ApiKeyList.Merge(keys, (await desktop.MergeKeysAsync(host, keys, token)).Keys);
        }

        await Run("The desktop pairs with both hosts, joins them in one network and makes \"Ana\" (openai) and \"Home Assistant\" (voice) keys on both", async () =>
        {
            await desktop.PairByCodeAsync(front, token);
            await desktop.SyncAsync(token);
            await desktop.PairByCodeAsync(gpu, token);
            await desktop.SyncAsync(token);
            await desktop.SyncAsync(token);
            var now = DateTimeOffset.UtcNow;
            (keys, ana) = keys.Create("Ana", OpenAiEndpoint.Scopes(false), null, networkKey.DeviceId, now);
            (keys, assistant) = keys.Create("Home Assistant", [ApiKeyScopes.Voice], null, networkKey.DeviceId, now);
            await SyncKeysAsync();
            var joined = front.Roster?.Host(gpu.HostId) is { Removed: false };
            return (joined, $"lab-front-door lists lab-gpu-box: {joined}; keys: {keys}");
        });
        await Run("The host's security audit (what the Share page reads) says where the front door listens", async () =>
        {
            var audit = await desktop.SecurityAuditAsync(front, token);
            var listening = audit.FrontDoorListeners ?? [];
            return (audit.FrontDoorPort == doorPort && listening.Contains($"127.0.0.1:{doorPort}"),
                $"front_door_port {audit.FrontDoorPort}, listeners {string.Join(", ", listening)}, sessions {audit.FrontDoorSessions}");
        });
        await Run("Anyone gets the public page: Online and the sign-in form, with no model, host or address", async () =>
        {
            var page = await browser.GetAsync("/");
            var hidden = new[] { FrontModel, GpuModel, front.HostId, gpu.HostId, front.Origin }.Where(page.Body.Contains).ToArray();
            var ok = page.Status == 200 && page.Body.Contains("Online") && page.Body.Contains("Sign in with your key") && hidden.Length == 0 &&
                !page.Body.Contains("<script", StringComparison.OrdinalIgnoreCase) && page.Header("Content-Security-Policy")?.StartsWith("default-src 'none'") == true;
            return (ok, $"HTTP {page.Status}, {page.Body.Length} bytes, CSP {page.Header("Content-Security-Policy")?[..20]}..., private names shown: {hidden.Length}");
        });
        await Run("Nothing of the gateway's private API is on the front door: 404 for every other path, also with a key", async () =>
        {
            var answers = new List<string>();
            foreach (var path in new[] { "/martlet/v1/status", "/martlet/v1/security", "/martlet/v1/network", "/health/ready", "/admin" })
                answers.Add($"{path} {(await browser.GetAsync(path, ana!.Token)).Status}");
            var pair = await browser.PostAsync("/martlet/v1/pair", new StringContent("{}", Encoding.UTF8, "application/json"));
            answers.Add($"POST /martlet/v1/pair {pair.Status}");
            return (answers.All(a => a.EndsWith(" 404", StringComparison.Ordinal)), string.Join("; ", answers));
        });
        await Run("Without a key /v1 answers 401 missing_api_key (what Check address looks for); a voice-only key gets 403", async () =>
        {
            var none = await browser.GetAsync("/v1/models");
            var voice = await browser.GetAsync("/v1/models", assistant!.Token);
            return (none.Status == 401 && none.ErrorCode == OpenAiEndpoint.FrontDoorMissingKey && voice.Status == 403,
                $"no key: HTTP {none.Status} {none.ErrorCode}; voice key: HTTP {voice.Status} {voice.ErrorCode}");
        });
        await Run("With Ana's key the front door lists both hosts' models and sends a streamed chat on to the host that has the model", async () =>
        {
            var models = await browser.GetAsync("/v1/models", ana!.Token);
            var ids = models.Json?.GetProperty("data").EnumerateArray().Select(m => m.GetProperty("id").GetString()!).ToArray() ?? [];
            var (status, host, text) = await browser.StreamChatAsync(ana.Token, GpuModel, token);
            return (models.Status == 200 && ids.Contains(FrontModel) && ids.Contains(GpuModel) && status == 200 && host == gpu.HostId && text == Reply,
                $"models: {string.Join(", ", ids)}; chat HTTP {status} from {host}: \"{text}\"");
        });
        await Run("Fifteen wrong keys through one proxy answer 401 each time and never lock the right key out", async () =>
        {
            var statuses = new List<int>();
            for (var i = 0; i < 15; i++) statuses.Add((await browser.GetAsync("/v1/models", "martlet_" + new string((char)('a' + i), 66))).Status);
            var right = await browser.GetAsync("/v1/models", ana!.Token);
            return (statuses.All(s => s == 401) && right.Status == 200, $"wrong keys: {string.Join(",", statuses.Distinct())}; then Ana: HTTP {right.Status}");
        });
        await Run("A forged form, another site and a wrong key can't sign in; Ana's key can, and the page then shows both models and how to connect", async () =>
        {
            var (formCookie, form) = await browser.OpenFormAsync();
            var forged = await browser.SignInAsync(Visitor.NewToken(), ana!.Token, formCookie, browser.Origin);
            var crossSite = await browser.SignInAsync(form, ana.Token, formCookie, "https://evil.example");
            var wrong = await browser.SignInAsync(form, "martlet_" + new string('z', 66), formCookie, browser.Origin);
            var good = await browser.SignInAsync(form, ana.Token, formCookie, browser.Origin);
            var session = good.Cookie("__Host-martlet");
            cookies = session is null ? null : $"__Host-martlet-form={formCookie}; __Host-martlet={session}";
            var page = await browser.GetAsync("/", cookies: cookies);
            var ok = forged.Status == 400 && crossSite.Status == 400 && wrong.Status == 400 && good.Status == 303 && session is not null &&
                page.Body.Contains($"<code>{FrontModel}</code>") && page.Body.Contains($"<code>{GpuModel}</code>") &&
                page.Body.Contains($"{browser.Origin}/v1") && !page.Body.Contains(ana.Token);
            return (ok, $"forged form {forged.Status}, other site {crossSite.Status}, wrong key {wrong.Status}, Ana {good.Status}; " +
                $"signed-in page lists both models: {page.Body.Contains(FrontModel) && page.Body.Contains(GpuModel)}; front door sessions {front.Server.FrontDoorSessions}");
        });
        await Run("Revoking Ana on both hosts ends her web session and her key at once", async () =>
        {
            keys = keys.Revoke(ana!.Key.Id, networkKey.DeviceId, DateTimeOffset.UtcNow);
            await SyncKeysAsync();
            var page = await browser.GetAsync("/", cookies: cookies);
            var call = await browser.GetAsync("/v1/models", ana.Token);
            return (page.Body.Contains("Sign in with your key") && !page.Body.Contains(GpuModel) && call.Status == 401 && front.Server.FrontDoorSessions == 0,
                $"page after revoke shows sign-in: {page.Body.Contains("Sign in with your key")}; /v1/models HTTP {call.Status}; sessions {front.Server.FrontDoorSessions}");
        });

        var ok = steps.All(s => s.Ok);
        return (ok, new
        {
            ok,
            check = "front_door_selftest",
            note = "Two real gateways on 127.0.0.1 with the real Ollama relay over fixture Ollama servers (NOT AI); the front door on its own port.",
            frontDoor = $"https://127.0.0.1:{doorPort}",
            seconds = Math.Round((DateTimeOffset.UtcNow - started).TotalSeconds, 1),
            steps = steps.Select(s => new { name = s.Name, ok = s.Ok, detail = s.Detail }).ToArray()
        });
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try { return ((IPEndPoint)probe.LocalEndpoint).Port; }
        finally { probe.Stop(); }
    }

    [GeneratedRegex("name=\"form\" value=\"([^\"]+)\"")]
    private static partial Regex FormField();

    /// <summary>A browser or app outside the network: plain HTTPS to the front door's port only, cookies by hand, no redirects.
    /// A proxy doesn't check the host's own certificate either (nginx proxy_ssl_verify off), so this one pins it.</summary>
    private sealed class Visitor : IDisposable
    {
        private readonly HttpClient client;
        internal string Origin { get; }

        internal Visitor(LabHost host, int port)
        {
            Origin = $"https://127.0.0.1:{port}";
            var handler = new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false };
            handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                certificate is X509Certificate2 presented &&
                "sha256:" + Convert.ToHexStringLower(SHA256.HashData(presented.PublicKey.ExportSubjectPublicKeyInfo())) == host.Identity.SpkiFingerprint;
            client = new HttpClient(handler) { BaseAddress = new Uri(Origin), Timeout = TimeSpan.FromSeconds(30) };
        }

        internal static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        internal sealed record Answer(int Status, string Body, HttpResponseMessage Response)
        {
            internal JsonElement? Json
            {
                get
                {
                    try { return JsonDocument.Parse(Body).RootElement.Clone(); }
                    catch (JsonException) { return null; }
                }
            }

            internal string? ErrorCode => Json is { ValueKind: JsonValueKind.Object } root && root.TryGetProperty("error", out var e) &&
                e.TryGetProperty("code", out var code) ? code.GetString() : null;

            internal string? Header(string name) =>
                Response.Headers.TryGetValues(name, out var values) || Response.Content.Headers.TryGetValues(name, out values) ? values.First() : null;

            internal string? Cookie(string name) => Response.Headers.TryGetValues("Set-Cookie", out var values)
                ? values.Where(v => v.StartsWith(name + "=", StringComparison.Ordinal)).Select(v => v[(name.Length + 1)..].Split(';')[0]).FirstOrDefault()
                : null;
        }

        private async Task<Answer> SendAsync(HttpRequestMessage request, string? key, string? cookies, string? origin)
        {
            if (key is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            if (cookies is not null) request.Headers.Add("Cookie", cookies);
            if (origin is not null) request.Headers.Add("Origin", origin);
            var response = await client.SendAsync(request);
            return new((int)response.StatusCode, await response.Content.ReadAsStringAsync(), response);
        }

        internal Task<Answer> GetAsync(string path, string? key = null, string? cookies = null) =>
            SendAsync(new HttpRequestMessage(HttpMethod.Get, path), key, cookies, null);

        internal Task<Answer> PostAsync(string path, HttpContent content, string? cookies = null, string? origin = null) =>
            SendAsync(new HttpRequestMessage(HttpMethod.Post, path) { Content = content }, null, cookies, origin);

        internal async Task<(string Cookie, string Form)> OpenFormAsync()
        {
            var page = await GetAsync("/");
            return (page.Cookie("__Host-martlet-form") ?? throw new InvalidOperationException("The page set no form cookie."),
                WebUtility.HtmlDecode(FormField().Match(page.Body).Groups[1].Value));
        }

        internal Task<Answer> SignInAsync(string form, string key, string formCookie, string origin) =>
            PostAsync("/portal/sign-in", new FormUrlEncodedContent([new("form", form), new("key", key)]), $"__Host-martlet-form={formCookie}", origin);

        internal async Task<(int Status, string? Host, string Text)> StreamChatAsync(string key, string model, CancellationToken token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
            {
                Content = new StringContent(JsonSerializer.Serialize(new { model, stream = true, messages = new[] { new { role = "user", content = "Hello!" } } }),
                    Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            var host = response.Headers.TryGetValues("X-Martlet-Host", out var hosts) ? hosts.FirstOrDefault() : null;
            var text = new StringBuilder();
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(token));
            while (await reader.ReadLineAsync(token) is { } line)
            {
                if (!line.StartsWith("data: ", StringComparison.Ordinal) || line == "data: [DONE]") continue;
                var chunk = JsonDocument.Parse(line[6..]).RootElement;
                if (!chunk.TryGetProperty("choices", out var choices)) continue;
                foreach (var choice in choices.EnumerateArray())
                    if (choice.GetProperty("delta").TryGetProperty("content", out var piece)) text.Append(piece.GetString());
            }
            return ((int)response.StatusCode, host, text.ToString());
        }

        public void Dispose() => client.Dispose();
    }

    /// <summary>A simulated desktop: its network key, its pairings (secrets in memory) and its network state.</summary>
    private sealed class LabDesktop(NetworkKey key, string name)
    {
        private readonly Dictionary<string, (HostPairing Pairing, string Secret)> pairings = new(StringComparer.Ordinal);
        private readonly NetworkSyncEngine engine = new(key, name);
        private NetworkLocalState state = NetworkLocalState.Empty;

        internal async Task PairByCodeAsync(LabHost host, CancellationToken token)
        {
            var card = host.Server.Pairing.OpenCodeWindow(new() { Roles = [GatewayRole.Voice] });
            var (pairing, secret) = await HostTransport.PairWithCodeAsync(host.Origin, card.Code.Reveal(), key.DeviceId, name, token);
            pairings[pairing.HostId] = (pairing, secret);
            state = state.WithAdopted(pairing.HostId);
        }

        internal async Task SyncAsync(CancellationToken token)
        {
            var result = await engine.SyncAsync(state, pairings.Values.Select(p => p.Pairing).ToArray(),
                pairing => new HostConnection(pairing, pairings[pairing.HostId].Secret), token);
            foreach (var (pairing, secret) in result.Paired) pairings[pairing.HostId] = (pairing, secret);
            state = NetworkLocalState.Parse(result.State.Write());
        }

        private HostConnection Connect(LabHost host) => new(pairings[host.HostId].Pairing, pairings[host.HostId].Secret);

        internal async Task<HostApiKeys> MergeKeysAsync(LabHost host, ApiKeyList list, CancellationToken token)
        {
            using var connection = Connect(host);
            return await connection.MergeApiKeysAsync(list, token);
        }

        internal async Task<HostSecurityAudit> SecurityAuditAsync(LabHost host, CancellationToken token)
        {
            using var connection = Connect(host);
            return await connection.ReadSecurityAuditAsync(token);
        }
    }

    /// <summary>A real gateway on 127.0.0.1 with the Ollama relay over a fixture Ollama, an in-memory network.json and api-keys.json,
    /// and (for the first host) the front door open on <c>frontDoorPort</c>.</summary>
    private sealed class LabHost : IAsyncDisposable, IGatewayApiKeyStorage, IGatewayNetworkStorage, IGatewayAuditSink
    {
        private X509Certificate2 certificate = null!;
        private GatewayListenerHandle? listener;
        private OllamaRelayWorker? worker;
        private byte[]? savedKeys;
        private byte[]? savedRoster;
        internal GatewayServer Server { get; private set; } = null!;
        internal GatewayHostIdentity Identity { get; private set; } = null!;
        internal string HostId { get; private init; } = "";
        internal string Origin { get; private set; } = "";
        internal NetworkRoster? Roster => savedRoster is null ? null : NetworkRoster.Parse(savedRoster);

        internal static async Task<LabHost> StartAsync(string hostId, string model, int? frontDoorPort)
        {
            var host = new LabHost { HostId = hostId, certificate = Certificate() };
            try
            {
                host.Identity = GatewayHostIdentity.FromCertificate(hostId, host.certificate);
                host.Origin = $"https://127.0.0.1:{FreePort()}";
                var origin = new GatewayOrigin(host.Origin);
                host.worker = new OllamaRelayWorker(new Uri("http://127.0.0.1:11434/"), model, handler: new FixtureOllama());
                host.Server = new GatewayServer(host.Identity, origin, [], host, inferenceWorkers: [host.worker]);
                host.Server.AttachApiKeyStorage(host);
                host.Server.AttachNetworkStorage(host);
                if (frontDoorPort is { } door) host.Server.Exposure = new() { FrontDoorPort = door };
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

        private static X509Certificate2 Certificate()
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest("CN=Martlet front door rehearsal (fixture)", key, HashAlgorithmName.SHA256);
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

    /// <summary>Stands in for Ollama's native /api/chat (FIXTURE, NOT AI): a canned reply, streamed in two pieces or whole.</summary>
    private sealed class FixtureOllama : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath != "/api/chat")
                return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"error":"not found"}""") };
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var stream = body.RootElement.TryGetProperty("stream", out var flag) && flag.ValueKind == JsonValueKind.True;
            var text = stream
                ? """{"message":{"role":"assistant","content":"Hello "},"done":false}""" + "\n" +
                  """{"message":{"role":"assistant","content":"from the fixture model."},"done":false}""" + "\n" +
                  """{"message":{"role":"assistant","content":""},"done":true,"done_reason":"stop"}""" + "\n"
                : JsonSerializer.Serialize(new { message = new { role = "assistant", content = Reply }, done = true, done_reason = "stop", prompt_eval_count = 5, eval_count = 6 });
            var content = new StringContent(text, Encoding.UTF8);
            content.Headers.ContentType = new(stream ? "application/x-ndjson" : "application/json");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }
    }
}
