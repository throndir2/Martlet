using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Core.Contracts;
using Martlet.Gateway.Ollama;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Martlet.Gateway.Tests;

// NOT AI: controlled HTTP fixtures stand in for the host's two loopback Ollama servers; no GPU is used. The tests check the
// gateway's wire format (route metadata, failure JSON and stream events), not how a desktop classifies it.
public sealed class GpuPriorityTests
{
    private const string CardA = "GPU-1a2b3c4d-0000-1111-2222-333344445555";
    private const string CardB = "GPU-9f8e7d6c-0000-1111-2222-333344445555";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    /// <summary>One Ollama server's /api/chat: the first chunk at once, then (while held) the rest only after
    /// <see cref="Release"/>. <see cref="Aborted"/> completes when the gateway's relay drops the request mid-stream.</summary>
    private sealed class HeldOllama : IAsyncDisposable
    {
        private readonly WebApplication app;
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int requests;
        internal TaskCompletionSource Aborted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Uri Endpoint { get; private set; } = null!;
        internal int Requests => Volatile.Read(ref requests);

        private HeldOllama(WebApplication app) => this.app = app;

        internal void Release() => release.TrySetResult();

        internal static async Task<HeldOllama> StartAsync(string first, string rest, bool held)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            var fake = new HeldOllama(app);
            if (!held) fake.Release();
            app.MapPost("/api/chat", async context =>
            {
                Interlocked.Increment(ref fake.requests);
                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/x-ndjson";
                await context.Response.WriteAsync(Line(first, false));
                await context.Response.Body.FlushAsync();
                try { await fake.release.Task.WaitAsync(context.RequestAborted); }
                catch (OperationCanceledException)
                {
                    fake.Aborted.TrySetResult();
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

    /// <summary>A gateway with the conversation model's route (live) and Deep thinking's (pool), each on its own fixture Ollama,
    /// placed on the given cards; a paired device with a raw request signer and the desktop's paired connection.</summary>
    private sealed class Lab : IAsyncDisposable
    {
        internal HeldOllama Conversation { get; private init; } = null!;
        internal HeldOllama Deep { get; private init; } = null!;
        internal GatewayTestHost Host { get; private set; } = null!;
        internal GatewayRequestSigner Signer { get; private set; } = null!;
        internal Audio2FaceHostConnection Connection { get; private set; } = null!;
        internal (Audio2FaceHostPairing Pairing, string Secret) Pairing { get; private set; }
        internal Dictionary<string, JsonElement> Routes { get; } = new(StringComparer.Ordinal);
        private readonly List<IAsyncDisposable> owned = [];

        internal static async Task<Lab> StartAsync(string[]? conversationGpus, string[]? deepGpus, bool holdConversation = false,
            IEnumerable<IGatewayInferenceWorker>? others = null)
        {
            var lab = new Lab
            {
                Conversation = await HeldOllama.StartAsync("Sure, ", "here you go.", holdConversation),
                Deep = await HeldOllama.StartAsync("Plan: ", "rest on Sunday.", held: true)
            };
            var thinking = new OllamaRelayWorker(lab.Conversation.Endpoint, "gemma4:e4b");
            var deep = OllamaRelayWorker.DeepThinking(lab.Deep.Endpoint, "qwen3:8b");
            if (conversationGpus is not null) thinking.Route.PlaceOn(conversationGpus);
            if (deepGpus is not null) deep.Route.PlaceOn(deepGpus);
            lab.owned.AddRange([thinking, deep]);
            lab.Host = await GatewayTestHost.StartAsync(inferenceWorkers: [thinking, deep, .. others ?? []]);
            var credential = await lab.Host.PairAsync(GatewayRole.Voice, "desktop-raw");
            lab.Signer = new GatewayRequestSigner(lab.Host.Identity, credential, lab.Host.Clock);
            var card = lab.Host.OpenPairing(GatewayRole.Voice, "desktop-test");
            lab.Pairing = await Audio2FaceHostClient.PairAsync(lab.Host.Origin.CanonicalOrigin, card.HostId, card.SpkiFingerprint,
                "desktop-test", card.PairingId, card.Token.Reveal());
            lab.Connection = new Audio2FaceHostConnection(lab.Pairing.Pairing, lab.Pairing.Secret, lab.Host.Clock);
            using var capabilities = await lab.GetJsonAsync("/martlet/v1/capabilities");
            foreach (var route in capabilities.RootElement.GetProperty("routes").EnumerateArray())
                lab.Routes[route.GetProperty("route_id").GetString()!] = route.Clone();
            return lab;
        }

        internal JsonElement Thinking => Routes[Martlet.Core.Settings.SelfHostSetup.OllamaRouteId];
        internal JsonElement DeepThinking => Routes[Martlet.Core.Settings.SelfHostSetup.DeepThinkingRouteId];

        internal async Task<JsonDocument> GetJsonAsync(string path)
        {
            using var request = Host.SignedGet(path, GatewayRole.Voice, Signer);
            using var response = await Host.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        }

        internal Task<HttpResponseMessage> PostAsync(string path, string json) =>
            Host.Client.SendAsync(Host.SignedPost(path, GatewayRole.Voice, Signer, Encoding.UTF8.GetBytes(json))).AsTask();

        /// <summary>Starts a chat request on <paramref name="route"/> with a raw signed request; the response streams.</summary>
        internal Task<HttpResponseMessage> ChatAsync(JsonElement route, string input = "Think it over.")
        {
            string Text(string name) => route.GetProperty(name).GetString()!;
            var body = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
            {
                ["protocol_version"] = new Dictionary<string, int> { ["major"] = 2, ["minor"] = 0 },
                ["route_id"] = Text("route_id"), ["contract_id"] = Text("contract_id"), ["contract_version"] = Text("contract_version"),
                ["destination_id"] = Text("destination_id"), ["worker_id"] = Text("worker_id"), ["adapter_version"] = Text("adapter_version"),
                ["model_id"] = Text("model_id"), ["model_revision"] = Text("model_revision"), ["model_sha256"] = Text("model_sha256"),
                ["artifact_identity_sha256"] = Text("artifact_identity_sha256"),
                ["session_id"] = Guid.NewGuid(), ["turn_id"] = Guid.NewGuid(), ["request_id"] = Guid.NewGuid(), ["epoch"] = 1,
                ["deadline_utc"] = Host.Clock.GetUtcNow().AddSeconds(60).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                ["payload"] = new Dictionary<string, object>
                {
                    ["input"] = input, ["temperature"] = 0.7, ["maximum_output_tokens"] = 256, ["maximum_context_tokens"] = 8_192
                }
            });
            return Host.Client.SendAsync(Host.SignedPost(Text("path"), GatewayRole.Voice, Signer, body)).AsTask();
        }

        /// <summary>Streams the desktop's reply on Thinking's route to the end.</summary>
        internal async Task<string> ReplyAsync()
        {
            var route = Assert.Single(await Connection.ReadRoutesAsync(), r => r.RouteId == HostRoute.OllamaChatRouteId);
            var text = new StringBuilder();
            await foreach (var delta in Connection.StreamChatAsync(route, Ids(), 1, Host.Clock.GetUtcNow().AddSeconds(30), null, [],
                "Are you there?", 0.7, 256, 8_192))
                text.Append(delta);
            return text.ToString();
        }

        public async ValueTask DisposeAsync()
        {
            Connection?.Dispose();
            if (Host is not null) await Host.DisposeAsync();
            foreach (var item in owned) await item.DisposeAsync();
            await Conversation.DisposeAsync();
            await Deep.DisposeAsync();
        }
    }

    private static CorrelationIds Ids() => new() { SessionId = Guid.NewGuid(), TurnId = Guid.NewGuid(), RequestId = Guid.NewGuid() };

    private static async Task<JsonDocument> FailureAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    /// <summary>Reads stream lines until one of <paramref name="type"/> arrives.</summary>
    private static async Task<JsonDocument> UntilAsync(StreamReader reader, string type)
    {
        while (await reader.ReadLineAsync().WaitAsync(Wait) is { } line)
        {
            var document = JsonDocument.Parse(line);
            if (document.RootElement.GetProperty("type").GetString() == type) return document;
            document.Dispose();
        }
        throw new InvalidOperationException($"The stream ended before a {type} event.");
    }

    private static string[] Strings(JsonElement array) => array.EnumerateArray().Select(item => item.GetString()!).ToArray();

    private static JsonElement Gpu(JsonDocument priority, string id) =>
        Assert.Single(priority.RootElement.GetProperty("gpus").EnumerateArray(), gpu => gpu.GetProperty("id").GetString() == id);

    [Fact]
    public async Task Routes_advertise_their_graphics_cards_and_lane()
    {
        await using var stt = new Martlet.Gateway.Stt.SttRelayWorker(new Uri("http://127.0.0.1:8178/"), "large-v3-turbo");
        await using var lab = await Lab.StartAsync([CardA], [CardB, "1"], others: [stt]);

        // Wire format: "gpus" (empty = unknown, the whole host) and "lane" ("live" or "pool") on every route.
        Assert.Equal([CardA], Strings(lab.Thinking.GetProperty("gpus")));
        Assert.Equal("live", lab.Thinking.GetProperty("lane").GetString());
        Assert.Equal([CardB, "1"], Strings(lab.DeepThinking.GetProperty("gpus")));
        Assert.Equal("pool", lab.DeepThinking.GetProperty("lane").GetString());
        var listening = lab.Routes[GatewayInferenceRoute.TranscriptionRouteId];
        Assert.Empty(Strings(listening.GetProperty("gpus")));
        Assert.Equal("live", listening.GetProperty("lane").GetString());

        // The gateway's own client keeps them, and a lane that contradicts the route ID is refused.
        await using var deepProbe = OllamaRelayWorker.DeepThinking(new Uri("http://127.0.0.1:11435/"), "qwen3:8b");
        var capability = GatewayInferenceRouteCapability.From(deepProbe.Route.PlaceOn([CardB]));
        var parsed = GatewayInferenceRoute.FromCapability(capability);
        Assert.Equal([CardB], parsed.Gpus);
        Assert.Equal(GatewayLane.Pool, parsed.Lane);
        Assert.Throws<GatewayProtocolException>(() => GatewayInferenceRoute.FromCapability(capability with { Lane = GatewayLane.Live }));
        Assert.Empty(GatewayInferenceRoute.FromCapability(capability with { Gpus = [], Lane = null }).Gpus);

        // Placement takes UUIDs, CUDA indexes or "cpu" alone, once, and never after the route is registered.
        await using var probe = new OllamaRelayWorker(new Uri("http://127.0.0.1:11434/"), "gemma4:e4b");
        foreach (var bad in new[] { new[] { "gpu0" }, ["64"], ["01"], ["cpu", CardA], [CardA, CardA], [""], ["GPU-"], ["GPU-a b"],
            Enumerable.Range(0, 9).Select(i => i.ToString(CultureInfo.InvariantCulture)).ToArray() })
            Assert.Throws<GatewayProtocolException>(() => probe.Route.PlaceOn(bad));
        probe.Route.PlaceOn(["cpu"]);
        Assert.Throws<GatewayProtocolException>(() => probe.Route.PlaceOn(["0"]));
        await using var registered = new OllamaRelayWorker(new Uri("http://127.0.0.1:11434/"), "gemma4:e4b");
        _ = new GatewayInferenceRouteRegistry([registered]);
        Assert.Throws<GatewayProtocolException>(() => registered.Route.PlaceOn([CardA]));
    }

    [Fact]
    public async Task A_live_request_stops_running_pool_work_on_its_graphics_card_and_aborts_the_ollama_request()
    {
        await using var lab = await Lab.StartAsync([CardA], [CardA]);
        using var think = await lab.ChatAsync(lab.DeepThinking);
        Assert.Equal(HttpStatusCode.OK, think.StatusCode);
        using var stream = new StreamReader(await think.Content.ReadAsStreamAsync());
        using (await UntilAsync(stream, "text_delta")) { }

        // The live reply runs at once; the think on the same card ends with job.preempted and its Ollama request is dropped.
        Assert.Equal("Sure, here you go.", await lab.ReplyAsync());
        using var failed = await UntilAsync(stream, "failed");
        Assert.Equal("job.preempted", failed.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrEmpty(failed.RootElement.GetProperty("summary").GetString()));
        Assert.False(string.IsNullOrEmpty(failed.RootElement.GetProperty("remedy").GetString()));
        await lab.Deep.Aborted.Task.WaitAsync(Wait);

        using var priority = await lab.GetJsonAsync("/martlet/v1/priority");
        var root = priority.RootElement;
        Assert.Equal(1, root.GetProperty("preempted").GetInt64());
        var last = Assert.Single(root.GetProperty("last_preemptions").EnumerateArray());
        Assert.Equal(Martlet.Core.Settings.SelfHostSetup.DeepThinkingRouteId, last.GetProperty("route_id").GetString());
        Assert.Equal([CardA], Strings(last.GetProperty("gpus")));
        Assert.Contains("Thinking request from desktop-test", last.GetProperty("by").GetString(), StringComparison.Ordinal);
        Assert.Contains(root.GetProperty("warnings").EnumerateArray(), warning =>
            warning.GetString()!.Contains("pin each Ollama server to its own GPU (CUDA_VISIBLE_DEVICES)", StringComparison.Ordinal));

        // Once the live reply ended the card is free: the next think runs to its end.
        lab.Deep.Release();
        using var again = await lab.ChatAsync(lab.DeepThinking);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        using var rest = new StreamReader(await again.Content.ReadAsStreamAsync());
        using (await UntilAsync(rest, "completed")) { }
    }

    [Fact]
    public async Task A_pool_request_is_turned_away_with_job_busy_live_while_a_live_request_holds_its_card()
    {
        await using var lab = await Lab.StartAsync([CardA], [CardA], holdConversation: true);
        lab.Deep.Release();
        var reply = lab.ReplyAsync();
        await Task.Delay(50);
        using (var priority = await WaitForAsync(lab, root => Gpu(root, CardA).GetProperty("live").GetInt32() == 1))
            Assert.True(Gpu(priority, CardA).GetProperty("held").GetBoolean());

        using (var refused = await lab.ChatAsync(lab.DeepThinking))
        using (var failure = await FailureAsync(refused, HttpStatusCode.TooManyRequests))
        {
            Assert.Equal("job.busy", failure.RootElement.GetProperty("code").GetString());
            Assert.Equal("live", failure.RootElement.GetProperty("detail").GetString());
            Assert.False(string.IsNullOrEmpty(failure.RootElement.GetProperty("remedy").GetString()));
        }
        Assert.Equal(0, lab.Deep.Requests);
        using (var priority = await lab.GetJsonAsync("/martlet/v1/priority"))
        {
            Assert.Equal(1, priority.RootElement.GetProperty("refused").GetInt64());
            var pool = Assert.Single(priority.RootElement.GetProperty("routes").EnumerateArray(),
                route => route.GetProperty("lane").GetString() == "pool");
            Assert.True(pool.GetProperty("held").GetBoolean());
            Assert.Contains("Thinking request", Assert.Single(priority.RootElement.GetProperty("last_refusals").EnumerateArray())
                .GetProperty("by").GetString(), StringComparison.Ordinal);
        }

        // Once the live reply ended, the card is free again.
        lab.Conversation.Release();
        Assert.Equal("Sure, here you go.", await reply.WaitAsync(Wait));
        using var admitted = await lab.ChatAsync(lab.DeepThinking);
        Assert.Equal(HttpStatusCode.OK, admitted.StatusCode);
        using var stream = new StreamReader(await admitted.Content.ReadAsStreamAsync());
        using (await UntilAsync(stream, "completed")) { }
    }

    [Fact]
    public async Task Pool_work_on_another_card_runs_beside_live_work_and_holds_never_touch_live_requests()
    {
        await using var lab = await Lab.StartAsync([CardA], [CardB]);
        using var think = await lab.ChatAsync(lab.DeepThinking);
        Assert.Equal(HttpStatusCode.OK, think.StatusCode);
        using var stream = new StreamReader(await think.Content.ReadAsStreamAsync());
        using (await UntilAsync(stream, "text_delta")) { }
        // A second think on the role's one slot is turned away as before: job.busy without a detail.
        using (var second = await lab.ChatAsync(lab.DeepThinking))
        using (var failure = await FailureAsync(second, HttpStatusCode.TooManyRequests))
        {
            Assert.Equal("job.busy", failure.RootElement.GetProperty("code").GetString());
            Assert.False(failure.RootElement.TryGetProperty("detail", out _));
        }

        // A hold on the live route's card: the live reply still runs, and the think on the other card goes on.
        using (var hold = await lab.PostAsync(GatewayHttpApplication.PriorityHoldPath,
            $"{{\"routes\":[\"{Martlet.Core.Settings.SelfHostSetup.OllamaRouteId}\"],\"ttl_ms\":10000}}"))
            Assert.Equal(HttpStatusCode.OK, hold.StatusCode);
        Assert.Equal("Sure, here you go.", await lab.ReplyAsync());
        lab.Deep.Release();
        using (await UntilAsync(stream, "completed")) { }
        Assert.False(lab.Deep.Aborted.Task.IsCompleted);
        using var priority = await lab.GetJsonAsync("/martlet/v1/priority");
        Assert.Equal(0, priority.RootElement.GetProperty("preempted").GetInt64());
        Assert.True(Gpu(priority, CardA).GetProperty("held").GetBoolean());
        Assert.False(Gpu(priority, CardB).GetProperty("held").GetBoolean());
        Assert.Empty(priority.RootElement.GetProperty("warnings").EnumerateArray());
    }

    [Fact]
    public async Task A_hold_keeps_the_card_until_it_is_released_or_expires_and_renews_in_place()
    {
        await using var lab = await Lab.StartAsync([CardA], [CardA]);
        lab.Deep.Release();
        var holdBody = $"{{\"routes\":[\"{Martlet.Core.Settings.SelfHostSetup.OllamaRouteId}\"],\"ttl_ms\":10000}}";
        var now = lab.Host.Clock.GetUtcNow();
        using (var hold = await lab.PostAsync(GatewayHttpApplication.PriorityHoldPath, holdBody))
        {
            Assert.Equal(HttpStatusCode.OK, hold.StatusCode);
            using var granted = JsonDocument.Parse(await hold.Content.ReadAsStringAsync());
            Assert.Equal([CardA], Strings(granted.RootElement.GetProperty("gpus")));
            Assert.Equal(now.AddSeconds(10), DateTimeOffset.Parse(granted.RootElement.GetProperty("until").GetString()!,
                CultureInfo.InvariantCulture));
        }
        using (var refused = await lab.ChatAsync(lab.DeepThinking))
        using (var failure = await FailureAsync(refused, HttpStatusCode.TooManyRequests))
            Assert.Equal(("job.busy", "live"), (failure.RootElement.GetProperty("code").GetString(),
                failure.RootElement.GetProperty("detail").GetString()));

        // Calling again renews the one hold of this client.
        lab.Host.Clock.Advance(TimeSpan.FromSeconds(8));
        using (var renew = await lab.PostAsync(GatewayHttpApplication.PriorityHoldPath, holdBody))
            Assert.Equal(HttpStatusCode.OK, renew.StatusCode);
        lab.Host.Clock.Advance(TimeSpan.FromSeconds(8));
        using (var priority = await lab.GetJsonAsync("/martlet/v1/priority"))
        {
            var hold = Assert.Single(priority.RootElement.GetProperty("holds").EnumerateArray());
            Assert.Equal("desktop-raw", hold.GetProperty("holder").GetString());
            Assert.True(Gpu(priority, CardA).GetProperty("held").GetBoolean());
            Assert.Equal(1, Gpu(priority, CardA).GetProperty("holds").GetInt32());
        }

        // Released: pool work runs again.
        using (var release = await lab.PostAsync(GatewayHttpApplication.PriorityReleasePath, "{}"))
        using (var released = JsonDocument.Parse(await release.Content.ReadAsStringAsync()))
            Assert.True(released.RootElement.GetProperty("released").GetBoolean());
        using (var released = await lab.PostAsync(GatewayHttpApplication.PriorityReleasePath, "{}"))
        using (var nothing = JsonDocument.Parse(await released.Content.ReadAsStringAsync()))
            Assert.False(nothing.RootElement.GetProperty("released").GetBoolean());
        await RunThinkAsync(lab);

        // Expired: a short hold ends on its own.
        using (var hold = await lab.PostAsync(GatewayHttpApplication.PriorityHoldPath,
            $"{{\"routes\":[\"{Martlet.Core.Settings.SelfHostSetup.OllamaRouteId}\"],\"ttl_ms\":1000}}"))
            Assert.Equal(HttpStatusCode.OK, hold.StatusCode);
        using (var refused = await lab.ChatAsync(lab.DeepThinking))
            Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        lab.Host.Clock.Advance(TimeSpan.FromSeconds(2));
        await RunThinkAsync(lab);
        using (var priority = await lab.GetJsonAsync("/martlet/v1/priority"))
            Assert.Empty(priority.RootElement.GetProperty("holds").EnumerateArray());

        // A route this host doesn't have (or with unknown placement) holds the whole host: empty "gpus".
        using (var whole = await lab.PostAsync(GatewayHttpApplication.PriorityHoldPath, "{\"routes\":[\"martlet.gateway.other.v1\"],\"ttl_ms\":5000}"))
        using (var granted = JsonDocument.Parse(await whole.Content.ReadAsStringAsync()))
            Assert.Empty(granted.RootElement.GetProperty("gpus").EnumerateArray());
        using (var priority = await lab.GetJsonAsync("/martlet/v1/priority"))
            Assert.True(priority.RootElement.GetProperty("whole_host_held").GetBoolean());
    }

    [Fact]
    public async Task A_hold_stops_running_pool_work_at_once()
    {
        await using var lab = await Lab.StartAsync([CardA], [CardA]);
        using var think = await lab.ChatAsync(lab.DeepThinking);
        using var stream = new StreamReader(await think.Content.ReadAsStreamAsync());
        using (await UntilAsync(stream, "text_delta")) { }

        using (var hold = await lab.PostAsync(GatewayHttpApplication.PriorityHoldPath,
            $"{{\"routes\":[\"{Martlet.Core.Settings.SelfHostSetup.OllamaRouteId}\"],\"ttl_ms\":10000}}"))
            Assert.Equal(HttpStatusCode.OK, hold.StatusCode);
        using var failed = await UntilAsync(stream, "failed");
        Assert.Equal("job.preempted", failed.RootElement.GetProperty("code").GetString());
        await lab.Deep.Aborted.Task.WaitAsync(Wait);
        using var priority = await lab.GetJsonAsync("/martlet/v1/priority");
        Assert.Contains("live turn of desktop-raw", Assert.Single(priority.RootElement.GetProperty("last_preemptions").EnumerateArray())
            .GetProperty("by").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_placement_counts_as_the_whole_host()
    {
        // The conversation model's server says nothing about its card: its live request stops pool work on any card.
        await using var lab = await Lab.StartAsync(null, [CardB]);
        Assert.Empty(Strings(lab.Thinking.GetProperty("gpus")));
        using var think = await lab.ChatAsync(lab.DeepThinking);
        using var stream = new StreamReader(await think.Content.ReadAsStreamAsync());
        using (await UntilAsync(stream, "text_delta")) { }
        Assert.Equal("Sure, here you go.", await lab.ReplyAsync());
        using var failed = await UntilAsync(stream, "failed");
        Assert.Equal("job.preempted", failed.RootElement.GetProperty("code").GetString());
        using var priority = await lab.GetJsonAsync("/martlet/v1/priority");
        Assert.Contains(priority.RootElement.GetProperty("warnings").EnumerateArray(), warning =>
            warning.GetString()!.Contains("the whole host", StringComparison.Ordinal) ||
            warning.GetString()!.Contains(CardB, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Hold_requests_are_signed_bounded_and_one_per_client()
    {
        await using var lab = await Lab.StartAsync([CardA], [CardA]);
        var route = Martlet.Core.Settings.SelfHostSetup.OllamaRouteId;
        foreach (var bad in new[]
        {
            $"{{\"routes\":[\"{route}\"],\"ttl_ms\":0}}", $"{{\"routes\":[\"{route}\"],\"ttl_ms\":15001}}",
            $"{{\"routes\":[\"{route}\"],\"ttl_ms\":1.5}}", "{\"routes\":[],\"ttl_ms\":1000}", "{\"ttl_ms\":1000}",
            $"{{\"routes\":[\"{route}\",\"{route}\"],\"ttl_ms\":1000}}", $"{{\"routes\":[\"{route}\"],\"ttl_ms\":1000,\"extra\":1}}",
            "{\"routes\":[\"not valid\"],\"ttl_ms\":1000}", "{\"routes\":[1],\"ttl_ms\":1000}", "[]"
        })
        {
            using var response = await lab.PostAsync(GatewayHttpApplication.PriorityHoldPath, bad);
            using var failure = await FailureAsync(response, HttpStatusCode.BadRequest);
            Assert.Equal("request.invalid", failure.RootElement.GetProperty("code").GetString());
        }
        using (var notEmpty = await lab.PostAsync(GatewayHttpApplication.PriorityReleasePath, "{\"all\":true}"))
            Assert.Equal(HttpStatusCode.BadRequest, notEmpty.StatusCode);
        using (var unsigned = new HttpRequestMessage(HttpMethod.Post, lab.Host.Origin.CanonicalOrigin + GatewayHttpApplication.PriorityHoldPath)
        {
            Content = new StringContent($"{{\"routes\":[\"{route}\"],\"ttl_ms\":1000}}", Encoding.UTF8, "application/json")
        })
        using (var response = await lab.Host.Client.SendAsync(unsigned))
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        // Each client has one hold, and the host keeps at most 32 clients' holds at once.
        for (var i = 0; i < 2; i++)
            using (var hold = await lab.PostAsync(GatewayHttpApplication.PriorityHoldPath, $"{{\"routes\":[\"{route}\"],\"ttl_ms\":10000}}"))
                Assert.Equal(HttpStatusCode.OK, hold.StatusCode);
        for (var i = 1; i < GatewayInferenceRouteRegistry.MaximumHolds; i++)
            Assert.Equal(HttpStatusCode.OK, await HoldAsAsync(lab, $"desktop-{i}", route));
        using (var priority = await lab.GetJsonAsync("/martlet/v1/priority"))
            Assert.Equal(GatewayInferenceRouteRegistry.MaximumHolds, priority.RootElement.GetProperty("holds").GetArrayLength());
        Assert.Equal(HttpStatusCode.TooManyRequests, await HoldAsAsync(lab, "desktop-one-too-many", route));
    }

    private static async Task<HttpStatusCode> HoldAsAsync(Lab lab, string device, string route)
    {
        var credential = await lab.Host.PairAsync(GatewayRole.Voice, device);
        var signer = new GatewayRequestSigner(lab.Host.Identity, credential, lab.Host.Clock);
        using var response = await lab.Host.Client.SendAsync(lab.Host.SignedPost(GatewayHttpApplication.PriorityHoldPath, GatewayRole.Voice,
            signer, Encoding.UTF8.GetBytes($"{{\"routes\":[\"{route}\"],\"ttl_ms\":10000}}")));
        if (response.StatusCode != HttpStatusCode.OK)
        {
            using var failure = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(("job.busy", "holds"), (failure.RootElement.GetProperty("code").GetString(),
                failure.RootElement.GetProperty("detail").GetString()));
        }
        return response.StatusCode;
    }

    [Fact]
    public async Task The_desktops_hold_client_holds_renews_and_releases_through_the_signed_endpoints()
    {
        await using var lab = await Lab.StartAsync([CardA], [CardA]);
        lab.Deep.Release();
        var logged = new List<string>();
        using var holds = new HostLiveGpuHold(id => id == lab.Pairing.Pairing.HostId
            ? new Audio2FaceHostConnection(lab.Pairing.Pairing, lab.Pairing.Secret, lab.Host.Clock) : null, logged.Add, lab.Host.Clock);
        var hostId = lab.Pairing.Pairing.HostId;

        // A minute is shortened to the longest hold, 15 s.
        await holds.HoldAsync(hostId, [Martlet.Core.Settings.SelfHostSetup.OllamaRouteId], TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.Equal(lab.Host.Clock.GetUtcNow().AddSeconds(15), holds.HeldUntil(hostId));
        using (var refused = await lab.ChatAsync(lab.DeepThinking))
            Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        using (var priority = await lab.GetJsonAsync("/martlet/v1/priority"))
            Assert.Equal("desktop-test", Assert.Single(priority.RootElement.GetProperty("holds").EnumerateArray()).GetProperty("holder").GetString());

        await holds.HoldAsync(hostId, [Martlet.Core.Settings.SelfHostSetup.OllamaRouteId], TimeSpan.FromSeconds(10), CancellationToken.None);
        await holds.ReleaseAsync(hostId, CancellationToken.None);
        Assert.Null(holds.HeldUntil(hostId));
        using (var priority = await lab.GetJsonAsync("/martlet/v1/priority"))
            Assert.Empty(priority.RootElement.GetProperty("holds").EnumerateArray());
        await RunThinkAsync(lab);

        // A host this PC isn't paired with: noted once, nothing thrown.
        await holds.HoldAsync("other-host", [Martlet.Core.Settings.SelfHostSetup.OllamaRouteId], TimeSpan.FromSeconds(10), CancellationToken.None);
        await holds.HoldAsync("other-host", [Martlet.Core.Settings.SelfHostSetup.OllamaRouteId], TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.Single(logged, line => line.Contains("other-host", StringComparison.Ordinal));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            holds.HoldAsync(hostId, [Martlet.Core.Settings.SelfHostSetup.OllamaRouteId], TimeSpan.FromSeconds(10), new CancellationToken(true)));
    }

    /// <summary>An older host: every call is refused with request.invalid (its gateway has no such path).</summary>
    private sealed class OldHostChannel : IHostGpuHoldChannel
    {
        internal int Calls;
        public Task<HostGpuHold> HoldGpusAsync(IReadOnlyList<string> routeIds, TimeSpan ttl, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            throw new Audio2FaceHostException("request.invalid", "The Martlet host refused the request (request.invalid).");
        }

        public Task<bool> ReleaseGpusAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            throw new Audio2FaceHostException("request.invalid", "The Martlet host refused the request (request.invalid).");
        }

        public void Dispose() { }
    }

    /// <summary>A host that doesn't answer: every call fails as unreachable.</summary>
    private sealed class UnreachableChannel : IHostGpuHoldChannel
    {
        internal bool Disposed;
        public Task<HostGpuHold> HoldGpusAsync(IReadOnlyList<string> routeIds, TimeSpan ttl, CancellationToken cancellationToken = default) =>
            throw new Audio2FaceHostException("host.unreachable", "Could not reach the Martlet host.");
        public Task<bool> ReleaseGpusAsync(CancellationToken cancellationToken = default) =>
            throw new Audio2FaceHostException("host.unreachable", "Could not reach the Martlet host.");
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public async Task An_older_host_without_holds_never_breaks_the_live_turn_and_is_logged_once()
    {
        var clock = new ManualGatewayClock(new DateTimeOffset(2026, 10, 7, 18, 0, 0, TimeSpan.Zero));
        var old = new OldHostChannel();
        var logged = new List<string>();
        using var holds = new HostLiveGpuHold(_ => old, logged.Add, clock);
        for (var i = 0; i < 3; i++)
            await holds.HoldAsync("old-host", [Martlet.Core.Settings.SelfHostSetup.OllamaRouteId], TimeSpan.FromSeconds(10), CancellationToken.None);
        await holds.ReleaseAsync("old-host", CancellationToken.None);
        // Asked once, then left alone for a while; the problem is logged once.
        Assert.Equal(1, old.Calls);
        Assert.Single(logged);
        Assert.Contains("old-host", logged[0], StringComparison.Ordinal);
        Assert.Null(holds.HeldUntil("old-host"));
        clock.Advance(HostLiveGpuHold.OldHostPause + TimeSpan.FromSeconds(1));
        await holds.HoldAsync("old-host", [Martlet.Core.Settings.SelfHostSetup.OllamaRouteId], TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.Equal(2, old.Calls);
        Assert.Single(logged);

        // An unreachable host: the call returns, the connection is dropped for a fresh one next time, logged once.
        var channels = new List<UnreachableChannel>();
        var problems = new List<string>();
        using var flaky = new HostLiveGpuHold(_ => { var c = new UnreachableChannel(); channels.Add(c); return c; }, problems.Add, clock);
        await flaky.HoldAsync("away-host", [Martlet.Core.Settings.SelfHostSetup.OllamaRouteId], TimeSpan.FromSeconds(10), CancellationToken.None);
        await flaky.HoldAsync("away-host", [Martlet.Core.Settings.SelfHostSetup.OllamaRouteId], TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.Equal(2, channels.Count);
        Assert.All(channels, c => Assert.True(c.Disposed));
        Assert.Single(problems);
    }

    private static async Task RunThinkAsync(Lab lab)
    {
        using var think = await lab.ChatAsync(lab.DeepThinking);
        Assert.Equal(HttpStatusCode.OK, think.StatusCode);
        using var stream = new StreamReader(await think.Content.ReadAsStreamAsync());
        using (await UntilAsync(stream, "completed")) { }
    }

    private static async Task<JsonDocument> WaitForAsync(Lab lab, Func<JsonDocument, bool> condition)
    {
        var until = DateTime.UtcNow + Wait;
        while (true)
        {
            var priority = await lab.GetJsonAsync("/martlet/v1/priority");
            if (condition(priority)) return priority;
            priority.Dispose();
            if (DateTime.UtcNow > until) throw new TimeoutException("The host never reached the expected priority state.");
            await Task.Delay(50);
        }
    }
}
