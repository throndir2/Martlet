using System.Diagnostics;
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
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Martlet.NodeLinkCheck;

/// <summary>
/// Rehearses GPU priority (live turn first) end to end on this PC with the production code: real gateways (Kestrel, pinned TLS)
/// on 127.0.0.1 with the conversation model's Ollama relay (live lane) and the Deep thinking role's (pool lane) placed on
/// graphics cards as martlet-host places them, each in front of a fixture Ollama server on loopback (canned text, NOT AI), a
/// simulated desktop with the desktop's paired client, and the desktop's hold client (<see cref="HostLiveGpuHold"/>). Checks
/// preemption (job.preempted, the Ollama request aborted), refusal while a card is held, holds (renew, release, expiry, a hold
/// that stops running work), that live requests never wait, that pool work on its own card runs on, and that an older host
/// without holds leaves the live turn alone. Nothing leaves loopback; no GPU is used; nothing is written to disk or the vault.
/// </summary>
internal static class GpuPriorityRehearsal
{
    private const string Shared = "GPU-5a1e-lab-shared";
    private const string Own = "GPU-0b2c-lab-own";

    internal static async Task<(bool Ok, object Report)> RunAsync(CancellationToken token)
    {
        var steps = new List<(string Name, bool Ok, string Detail)>();
        var started = DateTimeOffset.UtcNow;
        await using var conversation = await FixtureOllama.StartAsync("Sure, ", "here you go.");
        await using var deep = await FixtureOllama.StartAsync("I thought it over: ", "here's the plan.");
        await using var host = await LabHost.StartAsync("lab-gpu", conversation, [Shared], deep, [Shared]);
        var (pairing, secret) = await host.PairAsync("lab-desktop");
        using var replies = new Audio2FaceHostConnection(pairing, secret);
        using var thinks = new Audio2FaceHostConnection(pairing, secret);
        var logged = new List<string>();
        using var holds = new HostLiveGpuHold(id => id == pairing.HostId ? new Audio2FaceHostConnection(pairing, secret) : null,
            line => { lock (logged) logged.Add(line); });
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

        Task<JsonElement> Priority() => replies.ReadPriorityAsync(token);

        await Run("The host's GPU map: Thinking's route is live and Deep thinking's is pool, both on one graphics card, with a warning to pin each Ollama server to its own GPU", async () =>
        {
            var routes = await replies.ReadRoutesAsync(token);
            thinkingRoute = routes.FirstOrDefault(r => r.RouteId == HostRoute.OllamaChatRouteId);
            deepRoute = routes.FirstOrDefault(r => r.RouteId == HostRoute.DeepThinkingRouteId);
            var priority = await Priority();
            var map = priority.GetProperty("routes").EnumerateArray().ToDictionary(r => r.GetProperty("route_id").GetString()!,
                r => (Lane: r.GetProperty("lane").GetString(), Gpus: string.Join(",", r.GetProperty("gpus").EnumerateArray().Select(g => g.GetString()))));
            var warnings = priority.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()!).ToArray();
            var ok = thinkingRoute is not null && deepRoute is not null &&
                map.GetValueOrDefault(HostRoute.OllamaChatRouteId) == ("live", Shared) &&
                map.GetValueOrDefault(HostRoute.DeepThinkingRouteId) == ("pool", Shared) &&
                warnings.Any(w => w.Contains("pin each Ollama server to its own GPU (CUDA_VISIBLE_DEVICES)", StringComparison.Ordinal));
            return (ok, string.Join("; ", map.Select(p => $"{p.Key} {p.Value.Lane} on {p.Value.Gpus}")) + $"; warning: {warnings.FirstOrDefault()}");
        });
        await Run("A live reply stops a running think on the same card at once: the think ends with job.preempted and its Ollama request is aborted", async () =>
        {
            if (thinkingRoute is null || deepRoute is null) return (false, "NOT RUN: a route is missing");
            deep.Hold();
            var (think, first) = StartThink(thinks, deepRoute, token);
            await first.WaitAsync(TimeSpan.FromSeconds(20), token);
            var (reply, firstText) = await ReplyAsync(replies, thinkingRoute, token);
            var code = await Ended(think);
            var aborted = await deep.Aborted.WaitAsync(TimeSpan.FromSeconds(10), token).ContinueWith(t => t.IsCompletedSuccessfully);
            var priority = await Priority();
            var preemption = priority.GetProperty("last_preemptions").EnumerateArray().FirstOrDefault();
            return (reply == "Sure, here you go." && code == "job.preempted" && aborted && priority.GetProperty("preempted").GetInt64() == 1,
                $"reply \"{reply}\" (first text after {firstText} ms); the think ended with {code}; Deep thinking's Ollama saw its request " +
                $"{(aborted ? "aborted" : "NOT aborted")}; host: preempted {priority.GetProperty("preempted")}, by " +
                (preemption.ValueKind == JsonValueKind.Object ? preemption.GetProperty("by").GetString() : "nothing"));
        });
        await Run("While a live reply runs, a new think on that card is turned away at once (job.busy; the host records the live request that held it) and never reaches Ollama", async () =>
        {
            if (thinkingRoute is null || deepRoute is null) return (false, "NOT RUN: a route is missing");
            deep.Release();
            conversation.Hold();
            var requests = deep.Requests;
            var replying = ReplyAsync(replies, thinkingRoute, token);
            var held = await Until(async () => (await Priority()).GetProperty("gpus").EnumerateArray()
                .Any(g => g.GetProperty("id").GetString() == Shared && g.GetProperty("live").GetInt32() == 1), token);
            var code = await Ended(StartThink(thinks, deepRoute, token).Done);
            var priority = await Priority();
            var refusal = priority.GetProperty("last_refusals").EnumerateArray().FirstOrDefault();
            conversation.Release();
            var (reply, _) = await replying;
            return (held && code == "job.busy" && deep.Requests == requests && reply == "Sure, here you go." &&
                    refusal.ValueKind == JsonValueKind.Object && refusal.GetProperty("by").GetString()!.Contains("Thinking request", StringComparison.Ordinal),
                $"card held by the reply: {held}; the think was turned away with {code}; Ollama requests {deep.Requests - requests}; host " +
                $"recorded: refused {priority.GetProperty("refused")}, by {(refusal.ValueKind == JsonValueKind.Object ? refusal.GetProperty("by").GetString() : "nothing")}");
        });
        await Run("The desktop's hold client keeps the card: a think is turned away while held, renewing keeps one hold, release frees the card", async () =>
        {
            if (deepRoute is null) return (false, "NOT RUN: a route is missing");
            await holds.HoldAsync(pairing.HostId, [HostRoute.OllamaChatRouteId], TimeSpan.FromSeconds(10), token);
            var first = holds.HeldUntil(pairing.HostId);
            var code = await Ended(StartThink(thinks, deepRoute, token).Done);
            await Task.Delay(50, token);
            await holds.HoldAsync(pairing.HostId, [HostRoute.OllamaChatRouteId], TimeSpan.FromSeconds(10), token);
            var renewed = holds.HeldUntil(pairing.HostId);
            var priority = await Priority();
            var holdCount = priority.GetProperty("holds").GetArrayLength();
            var gpu = priority.GetProperty("gpus").EnumerateArray().First(g => g.GetProperty("id").GetString() == Shared);
            await holds.ReleaseAsync(pairing.HostId, token);
            var released = (await Priority()).GetProperty("holds").GetArrayLength() == 0;
            var after = await Ended(StartThink(thinks, deepRoute, token).Done);
            return (first is not null && renewed > first && code == "job.busy" && holdCount == 1 && gpu.GetProperty("held").GetBoolean() &&
                    released && after == "completed",
                $"held until {first:HH:mm:ss.fff}, renewed to {renewed:HH:mm:ss.fff} ({holdCount} hold); a think while held: {code}; " +
                $"card {Shared} held {gpu.GetProperty("held")}; after release: holds {(released ? 0 : 1)}, the next think {after}");
        });
        await Run("A hold ends on its own: after a 1 s hold a think runs again", async () =>
        {
            if (deepRoute is null) return (false, "NOT RUN: a route is missing");
            await holds.HoldAsync(pairing.HostId, [HostRoute.OllamaChatRouteId], TimeSpan.FromSeconds(1), token);
            var during = await Ended(StartThink(thinks, deepRoute, token).Done);
            await Task.Delay(TimeSpan.FromMilliseconds(1_300), token);
            var after = await Ended(StartThink(thinks, deepRoute, token).Done);
            return (during == "job.busy" && after == "completed", $"during the hold: {during}; after it ended: {after}");
        });
        await Run("A hold stops a running think at once (job.preempted), and live replies never wait for a hold", async () =>
        {
            if (thinkingRoute is null || deepRoute is null) return (false, "NOT RUN: a route is missing");
            deep.Hold();
            var (think, first) = StartThink(thinks, deepRoute, token);
            await first.WaitAsync(TimeSpan.FromSeconds(20), token);
            await holds.HoldAsync(pairing.HostId, [HostRoute.OllamaChatRouteId], TimeSpan.FromSeconds(10), token);
            var code = await Ended(think);
            var aborted = await deep.Aborted.WaitAsync(TimeSpan.FromSeconds(10), token).ContinueWith(t => t.IsCompletedSuccessfully);
            var (reply, firstText) = await ReplyAsync(replies, thinkingRoute, token);
            await holds.ReleaseAsync(pairing.HostId, token);
            deep.Release();
            return (code == "job.preempted" && aborted && reply == "Sure, here you go.",
                $"the think ended with {code} (Ollama request {(aborted ? "aborted" : "NOT aborted")}); a reply during the hold: \"{reply}\" " +
                $"(first text after {firstText} ms)");
        });
        var sharedStatus = await Priority();

        await Run("Pool work on its own card runs beside live replies and holds: a think on another card is not stopped", async () =>
        {
            await using var conversation2 = await FixtureOllama.StartAsync("Sure, ", "here you go.");
            await using var deep2 = await FixtureOllama.StartAsync("I thought it over: ", "here's the plan.");
            deep2.Hold();
            await using var separate = await LabHost.StartAsync("lab-gpu-two", conversation2, [Shared], deep2, [Own]);
            var (pairing2, secret2) = await separate.PairAsync("lab-desktop");
            using var replies2 = new Audio2FaceHostConnection(pairing2, secret2);
            using var thinks2 = new Audio2FaceHostConnection(pairing2, secret2);
            var routes = await replies2.ReadRoutesAsync(token);
            var reply2 = routes.Single(r => r.RouteId == HostRoute.OllamaChatRouteId);
            var deep2Route = routes.Single(r => r.RouteId == HostRoute.DeepThinkingRouteId);
            var (think, first) = StartThink(thinks2, deep2Route, token);
            await first.WaitAsync(TimeSpan.FromSeconds(20), token);
            var (reply, _) = await ReplyAsync(replies2, reply2, token);
            await replies2.HoldGpusAsync([HostRoute.OllamaChatRouteId], TimeSpan.FromSeconds(10), token);
            var stillRunning = !think.IsCompleted;
            deep2.Release();
            var code = await Ended(think);
            var priority = await replies2.ReadPriorityAsync(token);
            return (reply == "Sure, here you go." && stillRunning && code == "completed" && priority.GetProperty("preempted").GetInt64() == 0 &&
                    priority.GetProperty("warnings").GetArrayLength() == 0,
                $"Thinking on {Shared}, Deep thinking on {Own}: the reply finished while the think ran ({stillRunning}) and a hold on " +
                $"{Shared} was taken; the think {code}; preempted {priority.GetProperty("preempted")}; warnings {priority.GetProperty("warnings").GetArrayLength()}");
        });
        await Run("An older host without holds never breaks the live turn: the hold client notes it once and stops asking for a while", async () =>
        {
            var old = new OldHost();
            var notes = new List<string>();
            using var client = new HostLiveGpuHold(_ => old, notes.Add);
            for (var i = 0; i < 3; i++)
                await client.HoldAsync("old-host", [HostRoute.OllamaChatRouteId], TimeSpan.FromSeconds(10), token);
            await client.ReleaseAsync("old-host", token);
            return (old.Calls == 1 && notes.Count == 1, $"asked {old.Calls} time(s) for 3 holds and a release; noted: {notes.FirstOrDefault()}");
        });

        var ok = steps.All(s => s.Ok);
        return (ok, new
        {
            ok,
            passed = steps.Count(s => s.Ok),
            total = steps.Count,
            seconds = Math.Round((DateTimeOffset.UtcNow - started).TotalSeconds, 1),
            scope = "Real gateways on 127.0.0.1 (Kestrel, pinned TLS) with the real Ollama relays on Thinking's route (live) and the " +
                "Deep thinking role's route (pool), placed on graphics cards as martlet-host places them (CUDA_VISIBLE_DEVICES), each " +
                "over its own fixture Ollama server on loopback (canned text, NOT AI); a simulated desktop with the desktop's paired " +
                "client and its hold client. Not covered: a real GPU, a real Ollama or model (how fast it stops), martlet-host on a " +
                "host with two or more cards, and the desktop's live turn deciding when to hold.",
            steps = steps.Select(s => new { step = s.Name, ok = s.Ok, detail = s.Detail }),
            // What the shared-card host reported at the end: its GPU map, each card's hold state, the holds, the last
            // preemptions and refusals, and its warnings.
            priority = sharedStatus,
            holdClientLog = logged.ToArray()
        });
    }

    private static CorrelationIds Ids() => new() { SessionId = Guid.NewGuid(), TurnId = Guid.NewGuid(), RequestId = Guid.NewGuid() };

    /// <summary>Starts a think on <paramref name="route"/>: <c>Done</c> ends with "completed" or the code it failed with;
    /// <c>First</c> completes with its first text.</summary>
    private static (Task<string> Done, Task First) StartThink(Audio2FaceHostConnection connection, HostRoute route, CancellationToken token)
    {
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var done = Task.Run(async () =>
        {
            try
            {
                await foreach (var _ in connection.StreamChatAsync(route, Ids(), 1, DateTimeOffset.UtcNow.AddSeconds(60), "You are Martlet.", [],
                    "Think it over: plan my week.", 0.7, 1_024, 8_192, cancellationToken: token))
                    first.TrySetResult();
                return "completed";
            }
            catch (Audio2FaceHostException error) { return error.Code; }
            finally { first.TrySetResult(); }
        }, token);
        return (done, first.Task);
    }

    private static async Task<string> Ended(Task<string> think) => await think.WaitAsync(TimeSpan.FromSeconds(20));

    private static async Task<(string Text, long FirstTextMilliseconds)> ReplyAsync(Audio2FaceHostConnection connection, HostRoute route,
        CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        long? first = null;
        var text = new StringBuilder();
        await foreach (var delta in connection.StreamChatAsync(route, Ids(), 2, DateTimeOffset.UtcNow.AddSeconds(30), "You are Martlet.", [],
            "Are you still there?", 0.7, 256, 8_192, cancellationToken: token))
        {
            first ??= clock.ElapsedMilliseconds;
            text.Append(delta);
        }
        return (text.ToString(), first ?? clock.ElapsedMilliseconds);
    }

    private static async Task<bool> Until(Func<Task<bool>> condition, CancellationToken token)
    {
        for (var i = 0; i < 200; i++)
        {
            if (await condition()) return true;
            await Task.Delay(50, token);
        }
        return false;
    }

    /// <summary>A host older than GPU priority: its gateway has no hold path and answers request.invalid.</summary>
    private sealed class OldHost : IHostGpuHoldChannel
    {
        internal int Calls;
        public Task<HostGpuHold> HoldGpusAsync(IReadOnlyList<string> routeIds, TimeSpan ttl, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new Audio2FaceHostException("request.invalid", "The Martlet host refused the request (request.invalid).");
        }

        public Task<bool> ReleaseGpusAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new Audio2FaceHostException("request.invalid", "The Martlet host refused the request (request.invalid).");
        }

        public void Dispose() { }
    }

    /// <summary>A real gateway on 127.0.0.1 with Thinking's and Deep thinking's Ollama relays placed on the given cards.</summary>
    private sealed class LabHost : IAsyncDisposable, IGatewayAuditSink
    {
        private X509Certificate2 certificate = null!;
        private GatewayListenerHandle? listener;
        private readonly List<OllamaRelayWorker> workers = [];
        private GatewayServer server = null!;
        private GatewayHostIdentity identity = null!;
        private string hostId = "";
        private string origin = "";

        internal static async Task<LabHost> StartAsync(string hostId, FixtureOllama conversation, string[] conversationGpus,
            FixtureOllama deep, string[] deepGpus)
        {
            var host = new LabHost { hostId = hostId, certificate = Certificate() };
            try
            {
                host.identity = GatewayHostIdentity.FromCertificate(hostId, host.certificate);
                host.origin = $"https://127.0.0.1:{FreePort()}";
                var gatewayOrigin = new GatewayOrigin(host.origin);
                var thinking = new OllamaRelayWorker(conversation.Endpoint, "gemma4:e4b");
                thinking.Route.PlaceOn(conversationGpus);
                var deepThinking = OllamaRelayWorker.DeepThinking(deep.Endpoint, "qwen3:8b");
                deepThinking.Route.PlaceOn(deepGpus);
                host.workers.AddRange([thinking, deepThinking]);
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
            var request = new CertificateRequest("CN=Martlet GPU priority rehearsal (fixture)", key, HashAlgorithmName.SHA256);
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

    /// <summary>One Ollama server's /api/chat on a real loopback port (FIXTURE, NOT AI): the first chunk at once, then, while
    /// held, the rest only after <see cref="Release"/>. <see cref="Aborted"/> completes when the gateway's relay drops a held
    /// request, which is what makes a real Ollama stop generating.</summary>
    private sealed class FixtureOllama : IAsyncDisposable
    {
        private readonly WebApplication app;
        private TaskCompletionSource release = Completed();
        private TaskCompletionSource aborted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int requests;
        internal Uri Endpoint { get; private set; } = null!;
        internal int Requests => Volatile.Read(ref requests);
        internal Task Aborted => Volatile.Read(ref aborted).Task;

        private FixtureOllama(WebApplication app) => this.app = app;

        private static TaskCompletionSource Completed()
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            done.SetResult();
            return done;
        }

        /// <summary>Holds the requests that come from now on until the next <see cref="Release"/>.</summary>
        internal void Hold()
        {
            Volatile.Write(ref release, new(TaskCreationOptions.RunContinuationsAsynchronously));
            Volatile.Write(ref aborted, new(TaskCreationOptions.RunContinuationsAsynchronously));
        }

        internal void Release() => Volatile.Read(ref release).TrySetResult();

        internal static async Task<FixtureOllama> StartAsync(string first, string rest)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            var fake = new FixtureOllama(app);
            app.MapPost("/api/chat", async context =>
            {
                Interlocked.Increment(ref fake.requests);
                var gate = Volatile.Read(ref fake.release).Task;
                var abort = Volatile.Read(ref fake.aborted);
                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/x-ndjson";
                await context.Response.WriteAsync(Line(first, false));
                await context.Response.Body.FlushAsync();
                try { await gate.WaitAsync(context.RequestAborted); }
                catch (OperationCanceledException)
                {
                    abort.TrySetResult();
                    return;
                }
                await context.Response.WriteAsync(Line(rest, false) + Line("", true));
                await context.Response.Body.FlushAsync();
            });
            await app.StartAsync();
            fake.Endpoint = new Uri(app.Urls.First() + "/");
            return fake;
        }

        private static string Line(string text, bool done) =>
            JsonSerializer.Serialize(new { message = new { role = "assistant", content = text }, done }) + "\n";

        public async ValueTask DisposeAsync()
        {
            Release();
            await app.DisposeAsync();
        }
    }
}
