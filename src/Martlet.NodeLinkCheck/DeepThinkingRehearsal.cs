using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Gateway;
using Martlet.Gateway.Ollama;

namespace Martlet.NodeLinkCheck;

/// <summary>
/// Rehearses the Deep thinking host role end to end on this PC with the production code: one real gateway (Kestrel, pinned TLS)
/// on 127.0.0.1 serving both of a host's Ollama roles the way <c>martlet-host</c> publishes them (the conversation model's
/// <c>ollama</c> route and the <c>deep-thinking</c> role's own route), each relay in front of its own fixture Ollama (canned
/// text, NOT AI), and a simulated desktop that pairs and streams through the desktop's real paired client. Nothing leaves
/// loopback; nothing is written to disk or the credential vault.
/// </summary>
internal static class DeepThinkingRehearsal
{
    private const string ThinkingModel = "gemma4:e4b";
    private const string DeepModel = "qwen3:8b";
    /// <summary>Thinks at once on the Deep thinking role, as martlet-host publishes a role added with OLLAMA_NUM_PARALLEL 2.</summary>
    private const int Slots = 2;

    internal static async Task<(bool Ok, object Report)> RunAsync(CancellationToken token)
    {
        var steps = new List<(string Name, bool Ok, string Detail)>();
        var started = DateTimeOffset.UtcNow;
        var conversation = new FixtureOllama("Sure, ", "here you go.");
        var deep = new FixtureOllama("I thought it over: ", "here's the plan.") { Held = true };
        await using var host = await LabHost.StartAsync("lab-deep", conversation, deep);
        var (pairing, secret) = await host.PairAsync("lab-desktop");
        using var replies = new Audio2FaceHostConnection(pairing, secret);
        using var thinks = new Audio2FaceHostConnection(pairing, secret);
        HostRoute? thinkingRoute = null, deepRoute = null;

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

        await Run("The host advertises Thinking's route and the Deep thinking role's own route, each with its own model, and how many thinks run at once", async () =>
        {
            var routes = await replies.ReadRoutesAsync(token);
            thinkingRoute = routes.FirstOrDefault(r => r.RouteId == HostRoute.OllamaChatRouteId);
            deepRoute = routes.FirstOrDefault(r => r.RouteId == HostRoute.DeepThinkingRouteId);
            var ok = thinkingRoute is { Path: HostRoute.OllamaChatPath, ModelId: "gemma4-e4b", MaximumConcurrency: 1 } &&
                deepRoute is { Path: HostRoute.DeepThinkingPath, ModelId: "qwen3-8b", MaximumConcurrency: Slots } &&
                deepRoute.ContractId == thinkingRoute.ContractId && deepRoute.MaximumDuration == thinkingRoute.MaximumDuration;
            return (ok, string.Join("; ", routes.Select(r =>
                $"{r.RouteId} {r.Path} {r.ModelId} (up to {r.MaximumDuration.TotalMinutes:0} min, {r.MaximumConcurrency} at once)")));
        });
        await Run("This PC can move Thinking to the host: the route it advertises (with its long-think bound) saves as the job's route", () =>
        {
            if (thinkingRoute is null) return Task.FromResult((false, "NOT RUN: Thinking's route is missing"));
            var endpoint = new GatewayEndpointSettings
            {
                SchemaVersion = 1, Origin = pairing.Origin, HostId = pairing.HostId, SpkiFingerprint = pairing.SpkiFingerprint,
                DeviceRole = SelfHostSetup.GatewayRole
            };
            var settings = HostHandoff.ToHost(SetupSettings.Begin(null), SetupRouteType.GatewayOllama, endpoint, Guid.NewGuid(),
                pairing.DeviceId, thinkingRoute.Snapshot(SetupRouteType.GatewayOllama));
            settings.Validate();
            var saved = settings.Setup!.Routes.Single(r => r.Role == SetupRole.Llm);
            var ok = saved is { Enabled: true, RouteType: SetupRouteType.GatewayOllama } && saved.GatewaySnapshot is { } snapshot &&
                snapshot.ModelId == thinkingRoute.ModelId &&
                snapshot.MaximumDurationSeconds == (int)thinkingRoute.MaximumDuration.TotalSeconds;
            return Task.FromResult((ok, $"saved Thinking on {saved.Gateway?.HostId} with model {saved.GatewaySnapshot?.ModelId}, " +
                $"up to {saved.GatewaySnapshot?.MaximumDurationSeconds} s a request"));
        });
        await Run("A think on the Deep thinking route runs while a reply streams on Thinking's route; the reply finishes first", async () =>
        {
            if (thinkingRoute is null || deepRoute is null) return (false, "NOT RUN: a route is missing");
            var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
            var thinkText = new StringBuilder();
            var firstThought = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var think = Task.Run(async () =>
            {
                await foreach (var delta in thinks.StreamChatAsync(deepRoute, Ids(), 1, deadline, "You are Martlet.", [new(false, "Plan my week.")],
                    "Think it over: plan my week.", 0.7, 4_096, GenerationSettings.MaximumHostContextTokens,
                    sampling: new GenerationSettings { Reasoning = true, ContextTokens = GenerationSettings.MaximumHostContextTokens },
                    cancellationToken: token))
                {
                    thinkText.Append(delta);
                    firstThought.TrySetResult();
                }
            }, token);
            await firstThought.Task.WaitAsync(TimeSpan.FromSeconds(20), token);
            var reply = new StringBuilder();
            await foreach (var delta in replies.StreamChatAsync(thinkingRoute, Ids(), 2, deadline, "You are Martlet.", [], "Are you still there?",
                0.7, 256, 8_192, cancellationToken: token))
                reply.Append(delta);
            var thinkingWhileReplied = !think.IsCompleted;
            deep.Release();
            await think.WaitAsync(TimeSpan.FromSeconds(20), token);
            return (thinkingWhileReplied && reply.ToString() == "Sure, here you go." && thinkText.ToString() == "I thought it over: here's the plan.",
                $"reply \"{reply}\" while the think was {(thinkingWhileReplied ? "still running" : "already done")}; think \"{thinkText}\"");
        });
        await Run("Each request reached its own Ollama: the think with Thinking steps on and its own model, the reply without", () =>
        {
            var toDeep = deep.Requests.SingleOrDefault();
            var toConversation = conversation.Requests.SingleOrDefault();
            var ok = toDeep is not null && toConversation is not null &&
                toDeep.Model == DeepModel && toDeep.Think == true && toDeep.ContextTokens == GenerationSettings.MaximumHostContextTokens &&
                toConversation.Model == ThinkingModel && toConversation.Think is null;
            return Task.FromResult((ok, $"Deep thinking's Ollama got {deep.Requests.Count} request(s): {toDeep}; " +
                $"the conversation model's got {conversation.Requests.Count}: {toConversation}"));
        });
        await Run("A Thinking pool job (remembering) asks the role for its largest context window with a smaller budget of its own, and the gateway takes it", async () =>
        {
            if (deepRoute is null) return (false, "NOT RUN: the Deep thinking route is missing");
            // What the desktop's pool job asks a paired computer for (DeepThinkTarget.OneShot): its input bound (the window less
            // a medium think's 8,192 output tokens) plus its own 1,024, and the role's largest window so the model stays loaded.
            // Martlet 0.54.0 sent that budget as the bound and the gateway refused the pair as request.invalid.
            const int window = GenerationSettings.MaximumHostContextTokens, output = 1_024, budget = window - 8_192 + output;
            var before = deep.Requests.Count;
            var text = new StringBuilder();
            await foreach (var delta in thinks.StreamChatAsync(deepRoute, Ids(), 4, DateTimeOffset.UtcNow.AddSeconds(30), "Pick what to remember.",
                [], "The user likes green tea.", 0.7, output, budget,
                sampling: new GenerationSettings { Reasoning = false, ContextTokens = window }, cancellationToken: token))
                text.Append(delta);
            Asked? asked;
            lock (deep.Requests) asked = deep.Requests.Skip(before).SingleOrDefault();
            var ok = text.ToString() == "I thought it over: here's the plan." && asked is { Model: DeepModel, Think: false, ContextTokens: window };
            return (ok, $"a budget of {budget:N0} tokens with a {window:N0}-token window: the role answered \"{text}\"; its Ollama got {asked}");
        });
        await Run($"{Slots} thinks run at once on the Deep thinking role's {Slots} slots while a reply streams; one more is turned away (job.busy) until a slot frees", async () =>
        {
            if (thinkingRoute is null || deepRoute is null) return (false, "NOT RUN: a route is missing");
            deep.Hold();
            var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
            var connections = Enumerable.Range(0, Slots).Select(_ => new Audio2FaceHostConnection(pairing, secret)).ToArray();
            try
            {
                var started = Enumerable.Range(0, Slots).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
                var texts = Enumerable.Range(0, Slots).Select(_ => new StringBuilder()).ToArray();
                var running = Enumerable.Range(0, Slots).Select(i => Task.Run(async () =>
                {
                    await foreach (var delta in connections[i].StreamChatAsync(deepRoute, Ids(), 10 + i, deadline, "You are Martlet.", [],
                        $"Think it over: task {i + 1}.", 0.7, 1_024, 8_192, cancellationToken: token))
                    {
                        texts[i].Append(delta);
                        started[i].TrySetResult();
                    }
                }, token)).ToArray();
                await Task.WhenAll(started.Select(s => s.Task)).WaitAsync(TimeSpan.FromSeconds(20), token);
                var allAtOnce = running.All(t => !t.IsCompleted);
                string? turnedAway = null;
                try
                {
                    await foreach (var _ in thinks.StreamChatAsync(deepRoute, Ids(), 20, deadline, null, [], "One more think.", 0.7, 64, 4_096,
                        cancellationToken: token)) { }
                }
                catch (Audio2FaceHostException error) { turnedAway = error.Code; }
                var reply = new StringBuilder();
                await foreach (var delta in replies.StreamChatAsync(thinkingRoute, Ids(), 21, deadline, "You are Martlet.", [], "Still there?",
                    0.7, 256, 8_192, cancellationToken: token))
                    reply.Append(delta);
                var thinkingWhileReplied = running.All(t => !t.IsCompleted);
                deep.Release();
                await Task.WhenAll(running).WaitAsync(TimeSpan.FromSeconds(20), token);
                var done = texts.All(t => t.ToString() == "I thought it over: here's the plan.");
                return (allAtOnce && turnedAway == "job.busy" && thinkingWhileReplied && reply.ToString() == "Sure, here you go." && done,
                    $"{running.Length} thinks {(allAtOnce ? "streamed at the same time" : "did not overlap")}; one more got " +
                    $"{turnedAway ?? "no refusal"}; the reply \"{reply}\" finished while {(thinkingWhileReplied ? "both" : "not both")} still " +
                    $"thought; then each think finished: {string.Join(" | ", texts.Select(t => $"\"{t}\""))}");
            }
            finally
            {
                deep.Release();
                foreach (var connection in connections) connection.Dispose();
            }
        });
        await Run("The desktop's chat client refuses a host route that is neither conversation model", async () =>
        {
            if (deepRoute is null) return (false, "NOT RUN: the Deep thinking route is missing");
            var wrong = deepRoute with { Path = HostRoute.OllamaChatPath };
            try
            {
                await foreach (var _ in thinks.StreamChatAsync(wrong, Ids(), 3, DateTimeOffset.UtcNow.AddSeconds(30), null, [], "Hello", 0.7, 64,
                    4_096, cancellationToken: token)) { }
                return (false, "the mismatched route was sent");
            }
            catch (ArgumentException error) { return (true, error.Message); }
        });

        var ok = steps.All(s => s.Ok);
        return (ok, new
        {
            ok,
            passed = steps.Count(s => s.Ok),
            total = steps.Count,
            seconds = Math.Round((DateTimeOffset.UtcNow - started).TotalSeconds, 1),
            scope = "One real gateway on 127.0.0.1 (Kestrel, pinned TLS) with the real Ollama relay on Thinking's route and the Deep " +
                $"thinking role's relay on its own route with {Slots} thinks at once (its OLLAMA_NUM_PARALLEL slots), each over its own " +
                "fixture Ollama (canned text, NOT AI) and placed on its own graphics card, and a simulated desktop using the desktop's " +
                "paired client. Not covered: martlet-host installing the role, a real Ollama or model (its slots' graphics memory), " +
                "a GPU and a real LAN.",
            steps = steps.Select(s => new { step = s.Name, ok = s.Ok, detail = s.Detail })
        });
    }

    private static CorrelationIds Ids() => new() { SessionId = Guid.NewGuid(), TurnId = Guid.NewGuid(), RequestId = Guid.NewGuid() };

    /// <summary>A real gateway on 127.0.0.1 publishing both Ollama roles, as martlet-host does on a host that runs them.</summary>
    private sealed class LabHost : IAsyncDisposable, IGatewayAuditSink
    {
        private X509Certificate2 certificate = null!;
        private GatewayListenerHandle? listener;
        private readonly List<OllamaRelayWorker> workers = [];
        private GatewayServer server = null!;
        private GatewayHostIdentity identity = null!;
        private string hostId = "";
        private string origin = "";

        internal static async Task<LabHost> StartAsync(string hostId, FixtureOllama conversation, FixtureOllama deep)
        {
            var host = new LabHost { hostId = hostId, certificate = Certificate() };
            try
            {
                host.identity = GatewayHostIdentity.FromCertificate(hostId, host.certificate);
                host.origin = $"https://127.0.0.1:{FreePort()}";
                var gatewayOrigin = new GatewayOrigin(host.origin);
                // Each Ollama server on its own graphics card (martlet-host pins them with CUDA_VISIBLE_DEVICES), so thinks run
                // beside replies; on one shared card a reply stops the thinks there (live turn first, gpu_priority_selftest).
                var thinking = new OllamaRelayWorker(new Uri("http://127.0.0.1:11434/"), ThinkingModel, handler: conversation);
                thinking.Route.PlaceOn(["GPU-1ab0-lab-thinking"]);
                var deepThinking = OllamaRelayWorker.DeepThinking(new Uri("http://127.0.0.1:11435/"), DeepModel, handler: deep, slots: Slots);
                deepThinking.Route.PlaceOn(["GPU-2ab0-lab-deep-thinking"]);
                host.workers.Add(thinking);
                host.workers.Add(deepThinking);
                host.server = new GatewayServer(host.identity, gatewayOrigin, [], host, inferenceWorkers: host.workers);
                host.listener = await host.server.StartAsync(new GatewayTlsBinding(gatewayOrigin, host.identity, host.certificate),
                    new KestrelGatewayListenerFactory());
                return host;
            }
            catch
            {
                await host.DisposeAsync();
                throw;
            }
        }

        internal async Task<(Audio2FaceHostPairing Pairing, string Secret)> PairAsync(string deviceId)
        {
            var card = server.Pairing.OpenWindow(new() { DeviceId = deviceId, DisplayName = "LAB-DESKTOP", Roles = [GatewayRole.Voice] });
            return await Audio2FaceHostClient.PairAsync(origin, hostId, identity.SpkiFingerprint, deviceId, card.PairingId, card.Token.Reveal());
        }

        public void Record(GatewayAuditEvent gatewayEvent) { }

        public async ValueTask DisposeAsync()
        {
            if (listener is not null) await listener.DisposeAsync();
            foreach (var worker in workers) await worker.DisposeAsync();
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
            var request = new CertificateRequest("CN=Martlet Deep thinking rehearsal (fixture)", key, HashAlgorithmName.SHA256);
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

    /// <summary>What a fixture Ollama was asked: the model, Thinking steps and context size (never the text).</summary>
    private sealed record Asked(string? Model, bool? Think, int? ContextTokens)
    {
        public override string ToString() => $"model {Model}, think {(Think is { } on ? on.ToString().ToLowerInvariant() : "unset")}, num_ctx {ContextTokens}";
    }

    /// <summary>Stands in for one Ollama server's /api/chat (FIXTURE, NOT AI): streams a canned reply in two chunks and, while
    /// <see cref="Held"/>, waits before the second until <see cref="Release"/>, as a long think does.</summary>
    private sealed class FixtureOllama(string first, string rest) : HttpMessageHandler
    {
        private TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Held { get; set; }
        internal List<Asked> Requests { get; } = [];

        internal void Release() => released.TrySetResult();

        /// <summary>Holds the requests that come from now on until the next <see cref="Release"/>.</summary>
        internal void Hold()
        {
            released = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Held = true;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using (var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)))
            {
                var root = body.RootElement;
                lock (Requests)
                    Requests.Add(new(root.GetProperty("model").GetString(),
                        root.TryGetProperty("think", out var think) ? think.GetBoolean() : null,
                        root.GetProperty("options").TryGetProperty("num_ctx", out var context) ? context.GetInt32() : null));
            }
            var content = new StreamContent(new HeldStream(Line(first, false), Line(rest, false) + Line("", true), Held ? released.Task : Task.CompletedTask));
            content.Headers.ContentType = new("application/x-ndjson");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }

        private static string Line(string text, bool done) =>
            JsonSerializer.Serialize(new { message = new { role = "assistant", content = text }, done }) + "\n";
    }

    private sealed class HeldStream(string first, string rest, Task gate) : Stream
    {
        private readonly byte[][] chunks = [Encoding.UTF8.GetBytes(first), Encoding.UTF8.GetBytes(rest)];
        private int stage;
        private int offset;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (stage < chunks.Length)
            {
                var chunk = chunks[stage];
                if (offset < chunk.Length)
                {
                    var count = Math.Min(buffer.Length, chunk.Length - offset);
                    chunk.AsMemory(offset, count).CopyTo(buffer);
                    offset += count;
                    return count;
                }
                stage++;
                offset = 0;
                if (stage == 1) await gate.WaitAsync(cancellationToken);
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
