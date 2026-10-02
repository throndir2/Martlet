using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Core.Access;
using Martlet.Gateway;
using Martlet.Gateway.Ollama;

namespace Martlet.NodeLinkCheck;

/// <summary>
/// Rehearses API keys end to end on this PC with the production code: two real gateways (Kestrel, pinned TLS) on 127.0.0.1,
/// each with the real Ollama relay route in front of a fixture Ollama (canned text, NOT AI), and a simulated desktop that
/// creates, syncs and revokes keys through its real paired client. Software outside the network is played by a plain
/// HttpClient that pins the host key and sends Authorization: Bearer, exactly as docs/API.md tells integrators to.
/// Nothing leaves loopback, nothing is written to disk or the credential vault.
/// </summary>
internal static class ApiRehearsal
{
    internal static async Task<(bool Ok, object Report)> RunAsync(CancellationToken token)
    {
        var steps = new List<(string Name, bool Ok, string Detail)>();
        var started = DateTimeOffset.UtcNow;
        await using var h1 = await ApiHost.StartAsync("lab-api-1");
        await using var h2 = await ApiHost.StartAsync("lab-api-2");
        var desktop = new LabDesktop("lab-desktop");
        var list = ApiKeyList.Empty;
        IssuedApiKey? assistant = null, updater = null;

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

        async Task<string> SyncAsync()
        {
            var notes = new List<string>();
            foreach (var host in new[] { h1, h2 })
            {
                var copy = await desktop.MergeAsync(host, list, token);
                list = ApiKeyList.Merge(list, copy.Keys);
                notes.Add($"{host.HostId}: {copy.Keys}");
            }
            return string.Join("; ", notes);
        }

        await Run("The desktop pairs with lab-api-1 and lab-api-2 (signed, pinned connections)", async () =>
        {
            await desktop.PairAsync(h1);
            await desktop.PairAsync(h2);
            return (true, "paired with both");
        });
        await Run("It creates \"Home Assistant\" (read, voice) and \"Updater\" (manage); both hosts keep only verifiers", async () =>
        {
            (list, assistant) = list.Create("Home Assistant", [ApiKeyScopes.Read, ApiKeyScopes.Voice], null, desktop.DeviceId, DateTimeOffset.UtcNow);
            (list, updater) = list.Create("Updater", [ApiKeyScopes.Manage], null, desktop.DeviceId, DateTimeOffset.UtcNow);
            var detail = await SyncAsync();
            var secrets = new[] { assistant.Token, updater.Token }.Select(t => t[31..]).ToArray();
            var saved = new[] { h1.SavedKeys, h2.SavedKeys };
            var clean = saved.All(s => s is not null && !secrets.Any(s.Contains) && s.Contains(assistant.Key.Verifier!));
            return (clean && h1.Server is not null, $"{detail}; api-keys.json on both hosts has verifiers and no secret: {clean}");
        });
        await Run("Calls without a key, with a malformed key or with a wrong secret are refused", async () =>
        {
            var none = await Bearer.CallAsync(h1, null, HttpMethod.Get, "/martlet/v1/status");
            var malformed = await Bearer.CallAsync(h1, "Bearer not-a-martlet-key", HttpMethod.Get, "/martlet/v1/status");
            var wrong = await Bearer.CallAsync(h1, "Bearer " + assistant!.Token[..31] + new string('A', 42) + "E", HttpMethod.Get, "/martlet/v1/status");
            return (none.Code == "auth.missing" && malformed.Code == "key.invalid" && wrong.Code == "key.invalid",
                $"no key: {none.Status} {none.Code}; malformed: {malformed.Status} {malformed.Code}; wrong secret: {wrong.Status} {wrong.Code}");
        });
        await Run("The read+voice key: version names it; status and capabilities answer", async () =>
        {
            var version = await Bearer.CallAsync(h1, "Bearer " + assistant!.Token, HttpMethod.Get, "/martlet/v1/version");
            var status = await Bearer.CallAsync(h1, "Bearer " + assistant.Token, HttpMethod.Get, "/martlet/v1/status");
            var capabilities = await Bearer.CallAsync(h1, "Bearer " + assistant.Token, HttpMethod.Get, "/martlet/v1/capabilities");
            var name = version.Json?.GetProperty("api_key").GetProperty("name").GetString();
            var routes = capabilities.Json?.GetProperty("routes").EnumerateArray().Select(r => r.GetProperty("route_id").GetString()).ToArray() ?? [];
            return (version.Status == 200 && name == "Home Assistant" && status.Status == 200 && routes.Contains(HostRoute.OllamaChatRouteId),
                $"version {version.Status} as \"{name}\"; status {status.Status}; routes: {string.Join(", ", routes)}");
        });
        await Run("The documented curl request (curl -k --pinnedpubkey ... -H \"Authorization: Bearer ...\") works, and a wrong pin is refused", async () =>
        {
            var curl = Path.Combine(Environment.SystemDirectory, "curl.exe");
            if (!File.Exists(curl)) return (true, "NOT RUN: curl.exe isn't in System32 on this PC");
            var pin = "sha256//" + Convert.ToBase64String(Convert.FromHexString(h1.Identity.SpkiFingerprint["sha256:".Length..]));
            async Task<(int Exit, string Output)> CurlAsync(string pinned)
            {
                var start = new System.Diagnostics.ProcessStartInfo(curl)
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
                };
                foreach (var argument in new[] { "-s", "-k", "--pinnedpubkey", pinned, "-H", "Authorization: Bearer " + assistant!.Token,
                    h1.Origin + "/martlet/v1/version" })
                    start.ArgumentList.Add(argument);
                using var process = System.Diagnostics.Process.Start(start)!;
                var output = await process.StandardOutput.ReadToEndAsync(token);
                await process.WaitForExitAsync(token);
                return (process.ExitCode, output);
            }
            var good = await CurlAsync(pin);
            var bad = await CurlAsync("sha256//" + Convert.ToBase64String(new byte[32]));
            return (good.Exit == 0 && good.Output.Contains("\"name\":\"Home Assistant\"") && bad.Exit == 90,
                $"pinned: exit {good.Exit}, {(good.Output.Contains("Home Assistant") ? "names the key" : "unexpected answer")}; wrong pin: exit {bad.Exit} (90 = pin mismatch)");
        });
        await Run("The read+voice key chats with the host's model through the gateway's native route (fixture Ollama, NOT AI)", async () =>
        {
            var reply = await Bearer.ChatAsync(h1, assistant!.Token, "Is the washing machine done?", token);
            return (reply.Completed && reply.Text == FixtureOllama.Reply, $"events: {string.Join(" ", reply.Events)}; text: \"{reply.Text}\"");
        });
        await Run("When the model's speculative-decoding draft doesn't fit on the GPU (Gemma 4 \"requires ctx_other\"), the relay turns the draft off and the reply still arrives (fixture Ollama, NOT AI)", async () =>
        {
            h1.Ollama.DraftFails = true;
            var reply = await Bearer.ChatAsync(h1, assistant!.Token, "Is the dryer done?", token);
            return (reply.Completed && reply.Text == FixtureOllama.Reply && h1.Ollama.DraftTurnedOff == "fixture-model:1b",
                $"draft_num_predict 0 saved on: {h1.Ollama.DraftTurnedOff ?? "nothing"}; events: {string.Join(" ", reply.Events)}; text: \"{reply.Text}\"");
        });
        await Run("It can't send commands, pair, read voiceprints, read or change the network, change the plan or manage keys", async () =>
        {
            var auth = "Bearer " + assistant!.Token;
            var results = new Dictionary<string, string?>
            {
                ["POST commands"] = (await Bearer.CallAsync(h1, auth, HttpMethod.Post, "/martlet/v1/commands", """{"kind":"host.status"}""")).Code,
                ["GET voices"] = (await Bearer.CallAsync(h1, auth, HttpMethod.Get, "/martlet/v1/voices")).Code,
                ["GET network"] = (await Bearer.CallAsync(h1, auth, HttpMethod.Get, "/martlet/v1/network")).Code,
                ["GET api-keys"] = (await Bearer.CallAsync(h1, auth, HttpMethod.Get, "/martlet/v1/api-keys")).Code,
                ["POST api-keys"] = (await Bearer.CallAsync(h1, auth, HttpMethod.Post, "/martlet/v1/api-keys", Encoding.UTF8.GetString(list.Write()))).Code,
                ["POST cluster"] = (await Bearer.CallAsync(h1, auth, HttpMethod.Post, "/martlet/v1/cluster", """{"schema_version":1,"assignments":[],"nodes":[]}""")).Code,
                ["POST logs"] = (await Bearer.CallAsync(h1, auth, HttpMethod.Post, "/martlet/v1/logs", "{}")).Code
            };
            return (results.Values.All(code => code == "key.scope"), string.Join("; ", results.Select(r => $"{r.Key}: {r.Value}")));
        });
        await Run("The manage key sends host.status and follows it, but can't chat or read status", async () =>
        {
            var auth = "Bearer " + updater!.Token;
            var sent = await Bearer.CallAsync(h1, auth, HttpMethod.Post, "/martlet/v1/commands", """{"kind":"host.status"}""");
            var id = sent.Json?.GetProperty("command").GetProperty("id").GetString();
            var follow = await Bearer.CallAsync(h1, auth, HttpMethod.Get, "/martlet/v1/commands/" + id);
            var by = follow.Json?.GetProperty("command").GetProperty("requested_by").GetString();
            var chat = await Bearer.ChatAsync(h1, updater.Token, "hello", token);
            var status = await Bearer.CallAsync(h1, auth, HttpMethod.Get, "/martlet/v1/status");
            return (sent.Status == 202 && follow.Status == 200 && chat.Code == "key.scope" && status.Code == "key.scope",
                $"sent {sent.Status} ({id}, requested by {by}); followed {follow.Status}; chat {chat.Code}; status {status.Code}");
        });
        await Run("The same key works on lab-api-2 (synced), and after lab-api-2 restarts from its saved copy", async () =>
        {
            var before = await Bearer.CallAsync(h2, "Bearer " + assistant!.Token, HttpMethod.Get, "/martlet/v1/version");
            await h2.RestartAsync();
            var after = await Bearer.CallAsync(h2, "Bearer " + assistant.Token, HttpMethod.Get, "/martlet/v1/version");
            var reply = await Bearer.ChatAsync(h2, assistant.Token, "Still there?", token);
            return (before.Status == 200 && after.Status == 200 && reply.Completed,
                $"before restart {before.Status}; after restart {after.Status}; chat after restart completed: {reply.Completed}");
        });
        await Run("The desktop sees when each key was last used on each host", async () =>
        {
            var copy = await desktop.ReadAsync(h1, token);
            return (copy.Used.ContainsKey(assistant!.Key.Id) && copy.Used.ContainsKey(updater!.Key.Id),
                $"lab-api-1 reports {copy.Used.Count} used keys");
        });
        await Run("Revoking a key stops its reply mid-stream; after sync every host refuses it", async () =>
        {
            h1.Ollama.Pause = TimeSpan.FromSeconds(4);
            var reply = Bearer.ChatAsync(h1, assistant!.Token, "Tell me a long story.", token, onFirstText: async () =>
            {
                list = list.Revoke(assistant.Key.Id, desktop.DeviceId, DateTimeOffset.UtcNow);
                await desktop.MergeAsync(h1, list, token);
            });
            var result = await reply;
            h1.Ollama.Pause = TimeSpan.Zero;
            await SyncAsync();
            var one = await Bearer.CallAsync(h1, "Bearer " + assistant.Token, HttpMethod.Get, "/martlet/v1/version");
            var two = await Bearer.CallAsync(h2, "Bearer " + assistant.Token, HttpMethod.Get, "/martlet/v1/version");
            return (!result.Completed && result.Events.Contains("text_delta") && one.Code == "key.revoked" && two.Code == "key.revoked",
                $"stream: {string.Join(" ", result.Events)}{(result.Code is null ? "" : " " + result.Code)}; lab-api-1 {one.Code}; lab-api-2 {two.Code}");
        });
        await Run("An older copy (from before the revocation) can't bring the key back", async () =>
        {
            var stale = list with { Keys = list.Keys.Select(k => k.Id == assistant!.Key.Id ? assistant.Key : k).ToArray() };
            var merged = await desktop.MergeAsync(h1, stale, token);
            var call = await Bearer.CallAsync(h1, "Bearer " + assistant!.Token, HttpMethod.Get, "/martlet/v1/version");
            return (merged.Keys.Find(assistant.Key.Id)?.Revoked == true && call.Code == "key.revoked", $"after merging the stale copy: {call.Code}");
        });
        await Run("A key past its expiry is refused", async () =>
        {
            (list, var shortLived) = list.Create("Short-lived", [ApiKeyScopes.Read], DateTimeOffset.UtcNow.AddSeconds(2), desktop.DeviceId, DateTimeOffset.UtcNow);
            await desktop.MergeAsync(h1, list, token);
            var live = await Bearer.CallAsync(h1, "Bearer " + shortLived.Token, HttpMethod.Get, "/martlet/v1/status");
            await Task.Delay(TimeSpan.FromSeconds(2.5), token);
            var expired = await Bearer.CallAsync(h1, "Bearer " + shortLived.Token, HttpMethod.Get, "/martlet/v1/status");
            return (live.Status == 200 && expired.Code == "key.expired", $"before expiry {live.Status}; after {expired.Code}");
        });

        var ok = steps.All(s => s.Ok);
        return (ok, new
        {
            ok,
            passed = steps.Count(s => s.Ok),
            total = steps.Count,
            seconds = Math.Round((DateTimeOffset.UtcNow - started).TotalSeconds, 1),
            scope = "Two real gateways on 127.0.0.1 (Kestrel, pinned TLS, the real Ollama relay route over a fixture Ollama, NOT AI), " +
                "a simulated desktop using the desktop's paired client, and a plain HTTPS client with Authorization: Bearer. " +
                "Not covered: the desktop window, api-keys.json on a Linux host, a real model and a real LAN.",
            steps = steps.Select(s => new { step = s.Name, ok = s.Ok, detail = s.Detail })
        });
    }

    /// <summary>A simulated desktop: its pairings (secrets in memory) and the paired client calls the real desktop makes.</summary>
    private sealed class LabDesktop(string deviceId)
    {
        private readonly Dictionary<string, (Audio2FaceHostPairing Pairing, string Secret)> pairings = new(StringComparer.Ordinal);
        internal string DeviceId => deviceId;

        internal async Task PairAsync(ApiHost host)
        {
            var card = host.Server.Pairing.OpenWindow(new() { DeviceId = deviceId, DisplayName = "LAB-DESKTOP", Roles = [GatewayRole.Voice] });
            pairings[host.HostId] = await Audio2FaceHostClient.PairAsync(host.Origin, host.HostId, host.Identity.SpkiFingerprint, deviceId,
                card.PairingId, card.Token.Reveal());
        }

        internal async Task<HostApiKeys> MergeAsync(ApiHost host, ApiKeyList keys, CancellationToken token)
        {
            using var connection = Connect(host);
            return await connection.MergeApiKeysAsync(keys, token);
        }

        internal async Task<HostApiKeys> ReadAsync(ApiHost host, CancellationToken token)
        {
            using var connection = Connect(host);
            return await connection.ReadApiKeysAsync(token);
        }

        // A restarted lab host has a new port and forgets volatile pairings; the desktop pairs again, as it would after a reset.
        private Audio2FaceHostConnection Connect(ApiHost host)
        {
            var (pairing, secret) = pairings[host.HostId];
            if (pairing.Origin != host.Origin)
            {
                PairAsync(host).GetAwaiter().GetResult();
                (pairing, secret) = pairings[host.HostId];
            }
            return new Audio2FaceHostConnection(pairing, secret);
        }
    }

    /// <summary>A real gateway on 127.0.0.1 with the Ollama relay route over a fixture Ollama and an in-memory api-keys.json.</summary>
    private sealed class ApiHost : IAsyncDisposable, IGatewayApiKeyStorage, IGatewayAuditSink
    {
        private X509Certificate2 certificate = null!;
        private GatewayListenerHandle? listener;
        private OllamaRelayWorker? worker;
        private byte[]? saved;
        internal GatewayServer Server { get; private set; } = null!;
        internal GatewayHostIdentity Identity { get; private set; } = null!;
        internal FixtureOllama Ollama { get; private set; } = null!;
        internal string HostId { get; private init; } = "";
        internal string Origin { get; private set; } = "";
        internal string? SavedKeys => saved is null ? null : Encoding.UTF8.GetString(saved);

        internal static async Task<ApiHost> StartAsync(string hostId)
        {
            var host = new ApiHost { HostId = hostId, certificate = Certificate() };
            host.Identity = GatewayHostIdentity.FromCertificate(hostId, host.certificate);
            try
            {
                await host.ListenAsync();
                return host;
            }
            catch
            {
                await host.DisposeAsync();
                throw;
            }
        }

        private async Task ListenAsync()
        {
            Origin = $"https://127.0.0.1:{FreePort()}";
            var origin = new GatewayOrigin(Origin);
            Ollama = new FixtureOllama();
            worker = new OllamaRelayWorker(new Uri("http://127.0.0.1:11434/"), "fixture-model:1b", handler: Ollama);
            Server = new GatewayServer(Identity, origin, [], this, inferenceWorkers: [worker]);
            Server.AttachApiKeyStorage(this);
            listener = await Server.StartAsync(new GatewayTlsBinding(origin, Identity, certificate), new KestrelGatewayListenerFactory());
        }

        /// <summary>Stops the gateway and starts a new one with the same key and the saved api-keys.json.</summary>
        internal async Task RestartAsync()
        {
            await StopAsync();
            await ListenAsync();
        }

        public byte[]? Load() => saved;
        public void Save(byte[] bytes) => saved = bytes;
        public void Record(GatewayAuditEvent gatewayEvent) { }

        private async Task StopAsync()
        {
            if (listener is not null) await listener.DisposeAsync();
            if (worker is not null) await worker.DisposeAsync();
            listener = null;
            worker = null;
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
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
            var request = new CertificateRequest("CN=Martlet API rehearsal (fixture)", key, HashAlgorithmName.SHA256);
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

    /// <summary>Stands in for Ollama's /api/chat (FIXTURE, NOT AI): streams a canned reply in two chunks, optionally pausing
    /// between them so a revocation can land mid-reply. With <see cref="DraftFails"/> chat answers Ollama's Gemma 4
    /// draft-model load failure until /api/create saves draft_num_predict 0 on the model.</summary>
    internal sealed class FixtureOllama : HttpMessageHandler
    {
        internal const string Reply = "Hello from the fixture model.";
        internal const string DraftError = "llama-server process has terminated: exit status 1: llama_init_from_model: failed to " +
            "initialize the context: Gemma4Assistant requires ctx_other to be set (this warning is normal during memory fitting) " +
            "error loading model: vector";
        internal TimeSpan Pause { get; set; }
        internal bool DraftFails { get; set; }
        internal string? DraftTurnedOff { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/api/create")
            {
                using var create = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                var root = create.RootElement;
                if (root.GetProperty("parameters").GetProperty("draft_num_predict").GetInt32() == 0 &&
                    root.GetProperty("from").GetString() == root.GetProperty("model").GetString())
                {
                    DraftTurnedOff = root.GetProperty("model").GetString();
                    DraftFails = false;
                }
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"status":"success"}""") };
            }
            if (DraftFails)
                return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new { error = DraftError }))
                };
            var first = Encoding.UTF8.GetBytes("""{"message":{"role":"assistant","content":"Hello "},"done":false}""" + "\n");
            var rest = Encoding.UTF8.GetBytes("""{"message":{"role":"assistant","content":"from the fixture model."},"done":false}""" + "\n" +
                """{"message":{"role":"assistant","content":""},"done":true}""" + "\n");
            var content = new StreamContent(new PausingStream(first, rest, Pause));
            content.Headers.ContentType = new("application/x-ndjson");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }
    }

    private sealed class PausingStream(byte[] first, byte[] rest, TimeSpan pause) : Stream
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
                if (stage == 1 && pause > TimeSpan.Zero) await Task.Delay(pause, cancellationToken);
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

    /// <summary>Software outside the Martlet network: a plain HTTPS client that pins the host's key (as curl --pinnedpubkey
    /// does) and sends the API key as Authorization: Bearer.</summary>
    private static class Bearer
    {
        internal sealed record Result(int Status, string Body, JsonElement? Json, string? Code);
        internal sealed record Reply(bool Completed, string Text, IReadOnlyList<string> Events, string? Code);

        private static HttpClient Client(ApiHost host)
        {
            var handler = new SocketsHttpHandler();
            handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                certificate is X509Certificate2 presented &&
                "sha256:" + Convert.ToHexStringLower(SHA256.HashData(presented.PublicKey.ExportSubjectPublicKeyInfo())) == host.Identity.SpkiFingerprint;
            return new HttpClient(handler) { BaseAddress = new Uri(host.Origin) };
        }

        internal static async Task<Result> CallAsync(ApiHost host, string? authorization, HttpMethod method, string path, string? json = null)
        {
            using var http = Client(host);
            using var request = new HttpRequestMessage(method, path);
            if (authorization is not null) request.Headers.TryAddWithoutValidation("Authorization", authorization);
            if (json is not null)
            {
                request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(json));
                request.Content.Headers.ContentType = new("application/json") { CharSet = "utf-8" };
            }
            using var response = await http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            JsonElement? parsed = null;
            try { parsed = JsonDocument.Parse(body).RootElement.Clone(); }
            catch (JsonException) { }
            var code = parsed is { ValueKind: JsonValueKind.Object } root && (int)response.StatusCode >= 400 &&
                root.TryGetProperty("code", out var value) ? value.GetString() : null;
            return new((int)response.StatusCode, body, parsed, code);
        }

        /// <summary>Calls the host's conversation model the documented way: read the route from capabilities, echo its
        /// identity, add fresh IDs and a deadline, and read the NDJSON events.</summary>
        internal static async Task<Reply> ChatAsync(ApiHost host, string key, string input, CancellationToken token, Func<Task>? onFirstText = null)
        {
            var capabilities = await CallAsync(host, "Bearer " + key, HttpMethod.Get, "/martlet/v1/capabilities");
            if (capabilities.Status != 200) return new(false, "", [], capabilities.Code);
            var route = capabilities.Json!.Value.GetProperty("routes").EnumerateArray()
                .First(r => r.GetProperty("route_id").GetString() == HostRoute.OllamaChatRouteId);
            var body = new Dictionary<string, object>
            {
                ["protocol_version"] = new { major = 2, minor = 0 },
                ["session_id"] = Guid.NewGuid(), ["turn_id"] = Guid.NewGuid(), ["request_id"] = Guid.NewGuid(), ["epoch"] = 0,
                ["deadline_utc"] = DateTimeOffset.UtcNow.AddSeconds(60).ToString("O"),
                ["payload"] = new { input, temperature = 0.7, maximum_output_tokens = 256, maximum_context_tokens = 4096 }
            };
            foreach (var field in new[] { "route_id", "contract_id", "contract_version", "destination_id", "worker_id", "adapter_version",
                "model_id", "model_revision", "model_sha256", "artifact_identity_sha256" })
                body[field] = route.GetProperty(field).GetString()!;
            using var http = Client(host);
            using var request = new HttpRequestMessage(HttpMethod.Post, route.GetProperty("path").GetString())
            {
                Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body))
            };
            request.Content.Headers.ContentType = new("application/json") { CharSet = "utf-8" };
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                var failure = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token)).RootElement;
                return new(false, "", [], failure.GetProperty("code").GetString());
            }
            var events = new List<string>();
            var text = new StringBuilder();
            string? code = null;
            try
            {
                using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(token));
                while (await reader.ReadLineAsync(token) is { } line)
                {
                    var item = JsonDocument.Parse(line).RootElement;
                    var type = item.GetProperty("type").GetString()!;
                    events.Add(type);
                    if (type == "text_delta")
                    {
                        text.Append(item.GetProperty("text").GetString());
                        if (onFirstText is not null && events.Count(e => e == "text_delta") == 1) await onFirstText();
                    }
                    if (item.TryGetProperty("code", out var error) && error.ValueKind == JsonValueKind.String) code = error.GetString();
                    if (type is "completed" or "failed" or "canceled") break;
                }
            }
            catch (Exception error) when (error is IOException or HttpRequestException) { events.Add("connection-closed"); }
            return new(events.Contains("completed"), text.ToString(), events, code);
        }
    }
}
