using System.Text.Json;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Core.Contracts;
using Martlet.Gateway.Ollama;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Martlet.Gateway.Tests;

// NOT AI: a controlled HTTP fixture stands in for the host's loopback Ollama /api/chat.
public sealed class OllamaRelayTests
{
    private sealed class FakeOllama : IAsyncDisposable
    {
        private readonly WebApplication app;
        internal List<JsonDocument> Requests { get; } = [];
        internal Uri Endpoint { get; private set; } = null!;

        private FakeOllama(WebApplication app) => this.app = app;

        internal static async Task<FakeOllama> StartAsync(int status, params string[] lines) => await StartAsync(status, null, lines);

        /// <summary>With <paramref name="holdLast"/>, the last line waits for it, as a long think does.</summary>
        internal static async Task<FakeOllama> StartAsync(int status, Task? holdLast, params string[] lines)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            var fake = new FakeOllama(app);
            app.MapPost("/api/chat", async context =>
            {
                var request = await JsonDocument.ParseAsync(context.Request.Body);
                lock (fake.Requests) fake.Requests.Add(request);
                context.Response.StatusCode = status;
                context.Response.ContentType = "application/x-ndjson";
                for (var i = 0; i < lines.Length; i++)
                {
                    if (i == lines.Length - 1 && holdLast is not null) await holdLast.WaitAsync(context.RequestAborted);
                    await context.Response.WriteAsync(lines[i] + "\n");
                    await context.Response.Body.FlushAsync();
                }
            });
            await app.StartAsync();
            fake.Endpoint = new Uri(app.Urls.First() + "/");
            return fake;
        }

        public async ValueTask DisposeAsync()
        {
            await app.DisposeAsync();
            foreach (var request in Requests) request.Dispose();
        }
    }

    private static CorrelationIds NewIds() => new() { SessionId = Guid.NewGuid(), TurnId = Guid.NewGuid(), RequestId = Guid.NewGuid() };

    private static async Task<(Audio2FaceHostConnection Connection, HostRoute Route)> ConnectAsync(GatewayTestHost host)
    {
        var card = host.OpenPairing(GatewayRole.Voice, "desktop-test");
        var (pairing, secret) = await Audio2FaceHostClient.PairAsync(host.Origin.CanonicalOrigin, card.HostId,
            card.SpkiFingerprint, "desktop-test", card.PairingId, card.Token.Reveal());
        var connection = new Audio2FaceHostConnection(pairing, secret, host.Clock);
        var route = Assert.Single(await connection.ReadRoutesAsync(), r => r.RouteId == HostRoute.OllamaChatRouteId);
        return (connection, route);
    }

    [Fact]
    public async Task Paired_desktop_streams_a_conversation_reply_from_the_hosts_ollama_through_the_gateway()
    {
        await using var ollama = await FakeOllama.StartAsync(200,
            "{\"message\":{\"role\":\"assistant\",\"content\":\"Hello\"},\"done\":false}",
            "{\"message\":{\"role\":\"assistant\",\"content\":\" there\"},\"done\":false}",
            "{\"message\":{\"role\":\"assistant\",\"content\":\"\"},\"done\":true,\"done_reason\":\"stop\"}");
        await using var worker = new OllamaRelayWorker(ollama.Endpoint, "llama3.2:3b");
        await using var host = await GatewayTestHost.StartAsync(inferenceWorkers: [worker]);
        var (connection, route) = await ConnectAsync(host);
        using var owned = connection;
        Assert.Equal("llama3.2-3b", route.ModelId);

        var text = new List<string>();
        await foreach (var delta in connection.StreamChatAsync(route, NewIds(), 2, host.Clock.GetUtcNow().AddSeconds(30),
            "You are Martlet.\nBe brief.", [new(false, "Hi"), new(true, "Hey!")], "How are you?", 0.7, 256, 32_768))
            text.Add(delta);

        Assert.Equal(["Hello", " there"], text);
        var sent = Assert.Single(ollama.Requests).RootElement;
        Assert.Equal("llama3.2:3b", sent.GetProperty("model").GetString());
        Assert.True(sent.GetProperty("stream").GetBoolean());
        var messages = sent.GetProperty("messages").EnumerateArray()
            .Select(m => (m.GetProperty("role").GetString(), m.GetProperty("content").GetString())).ToArray();
        Assert.Equal([("system", "You are Martlet.\nBe brief."), ("user", "Hi"), ("assistant", "Hey!"), ("user", "How are you?")], messages);
        Assert.Equal(256, sent.GetProperty("options").GetProperty("num_predict").GetInt32());
        Assert.Equal(OllamaRelayWorker.MaximumContextTokens, sent.GetProperty("options").GetProperty("num_ctx").GetInt32());

        // The route admits the next turn once the previous one finished.
        await foreach (var _ in connection.StreamChatAsync(route, NewIds(), 3, host.Clock.GetUtcNow().AddSeconds(30),
            null, [], "Again", 0.7, 64, 4_096)) { }
    }

    [Fact]
    public async Task Relay_forwards_one_screen_image_to_the_hosts_ollama_on_the_current_message()
    {
        await using var ollama = await FakeOllama.StartAsync(200,
            "{\"message\":{\"role\":\"assistant\",\"content\":\"[pass]\"},\"done\":true,\"done_reason\":\"stop\"}");
        await using var worker = new OllamaRelayWorker(ollama.Endpoint, "gemma3:4b");
        await using var host = await GatewayTestHost.StartAsync(inferenceWorkers: [worker]);
        var (connection, route) = await ConnectAsync(host);
        using var owned = connection;
        var jpeg = Convert.ToBase64String(new byte[] { 0xFF, 0xD8, 0xFF }.Concat(new byte[64]).ToArray());

        var text = new List<string>();
        await foreach (var delta in connection.StreamChatAsync(route, NewIds(), 1, host.Clock.GetUtcNow().AddSeconds(30),
            "Stay quiet unless it matters.", [new(false, "Hi"), new(true, "Hey!")], "(Screen glance.)", 0.7, 64, 4_096, [jpeg]))
            text.Add(delta);

        Assert.Equal(["[pass]"], text);
        var messages = Assert.Single(ollama.Requests).RootElement.GetProperty("messages").EnumerateArray().ToArray();
        Assert.False(messages[1].TryGetProperty("images", out _));
        Assert.Equal(jpeg, Assert.Single(messages[^1].GetProperty("images").EnumerateArray()).GetString());

        // Not a JPEG/PNG: rejected at the gateway before it reaches Ollama.
        await Assert.ThrowsAsync<Audio2FaceHostException>(async () =>
        {
            await foreach (var _ in connection.StreamChatAsync(route, NewIds(), 2, host.Clock.GetUtcNow().AddSeconds(30),
                null, [], "(Screen glance.)", 0.7, 64, 4_096, [Convert.ToBase64String(new byte[64])])) { }
        });
        Assert.Single(ollama.Requests);
    }

    [Fact]
    public async Task Relay_forwards_optional_sampling_settings_and_context_size_to_the_hosts_ollama()
    {
        await using var ollama = await FakeOllama.StartAsync(200,
            "{\"message\":{\"role\":\"assistant\",\"content\":\"Hi\"},\"done\":true,\"done_reason\":\"stop\"}");
        await using var worker = new OllamaRelayWorker(ollama.Endpoint, "llama3.2:3b");
        await using var host = await GatewayTestHost.StartAsync(inferenceWorkers: [worker]);
        var (connection, route) = await ConnectAsync(host);
        using var owned = connection;
        var sampling = new Martlet.Core.Settings.GenerationSettings
        {
            TopP = 0.9, TopK = 40, MinP = 0.05, RepeatPenalty = 1.15, FrequencyPenalty = 0.2, PresencePenalty = -0.5,
            ContextTokens = 16_384, Reasoning = false
        };

        await foreach (var _ in connection.StreamChatAsync(route, NewIds(), 1, host.Clock.GetUtcNow().AddSeconds(30),
            null, [], "Hello", 1.2, 512, 32_768, null, sampling)) { }

        var sent = Assert.Single(ollama.Requests).RootElement;
        // Thinking steps Off is Ollama's own top-level think, not an option.
        Assert.False(sent.GetProperty("think").GetBoolean());
        var options = sent.GetProperty("options");
        Assert.Equal(1.2, options.GetProperty("temperature").GetDouble());
        Assert.Equal(512, options.GetProperty("num_predict").GetInt32());
        Assert.Equal(16_384, options.GetProperty("num_ctx").GetInt32());
        Assert.Equal(0.9, options.GetProperty("top_p").GetDouble());
        Assert.Equal(40, options.GetProperty("top_k").GetInt32());
        Assert.Equal(0.05, options.GetProperty("min_p").GetDouble());
        Assert.Equal(1.15, options.GetProperty("repeat_penalty").GetDouble());
        Assert.Equal(0.2, options.GetProperty("frequency_penalty").GetDouble());
        Assert.Equal(-0.5, options.GetProperty("presence_penalty").GetDouble());

        // Out of range (a context window no larger than the reply budget): rejected at the gateway before it reaches Ollama.
        var invalid = await Assert.ThrowsAsync<Audio2FaceHostException>(async () =>
        {
            await foreach (var _ in connection.StreamChatAsync(route, NewIds(), 2, host.Clock.GetUtcNow().AddSeconds(30),
                null, [], "Hello", 0.7, 4_096, 4_096, null, new() { ContextTokens = 4_096 })) { }
        });
        Assert.Equal("request.invalid", invalid.Code);
        Assert.Single(ollama.Requests);
    }

    [Fact]
    public async Task Thinking_pool_job_loads_the_hosts_largest_context_window_above_its_own_budget()
    {
        await using var ollama = await FakeOllama.StartAsync(200,
            "{\"message\":{\"role\":\"assistant\",\"content\":\"Noted.\"},\"done\":true,\"done_reason\":\"stop\"}");
        await using var worker = OllamaRelayWorker.DeepThinking(ollama.Endpoint, "gemma4:e2b");
        await using var host = await GatewayTestHost.StartAsync(inferenceWorkers: [worker]);
        var card = host.OpenPairing(GatewayRole.Voice, "desktop-test");
        var (pairing, secret) = await Audio2FaceHostClient.PairAsync(host.Origin.CanonicalOrigin, card.HostId,
            card.SpkiFingerprint, "desktop-test", card.PairingId, card.Token.Reveal());
        using var connection = new Audio2FaceHostConnection(pairing, secret, host.Clock);
        var route = Assert.Single(await connection.ReadRoutesAsync(), r => r.RouteId == HostRoute.DeepThinkingRouteId);
        // What a Thinking pool job (remembering, 1,024 output tokens) asked for in 0.54.0: a budget of its input bound plus its
        // output, and the role's largest window so the model stays loaded. The gateway refused that pair as request.invalid.
        const int window = Martlet.Core.Settings.GenerationSettings.MaximumHostContextTokens;
        const int budget = window - 8_192 + 1_024;

        var text = new List<string>();
        await foreach (var delta in connection.StreamChatAsync(route, NewIds(), 1, host.Clock.GetUtcNow().AddSeconds(30),
            "Pick what to remember.", [], "The user likes tea.", 0.7, 1_024, budget, null,
            new() { Reasoning = false, ContextTokens = window }))
            text.Add(delta);

        Assert.Equal(["Noted."], text);
        var options = Assert.Single(ollama.Requests).RootElement.GetProperty("options");
        Assert.Equal(window, options.GetProperty("num_ctx").GetInt32());
        Assert.Equal(1_024, options.GetProperty("num_predict").GetInt32());
    }

    [Fact]
    public async Task Relay_accepts_the_desktops_reply_budget_and_context_bound()
    {
        await using var ollama = await FakeOllama.StartAsync(200,
            "{\"message\":{\"role\":\"assistant\",\"thinking\":\"Hmm.\",\"content\":\"\"},\"done\":false}",
            "{\"message\":{\"role\":\"assistant\",\"content\":\"Hi!\"},\"done\":true,\"done_reason\":\"stop\"}");
        await using var worker = new OllamaRelayWorker(ollama.Endpoint, "qwen3-vl:8b");
        await using var host = await GatewayTestHost.StartAsync(inferenceWorkers: [worker]);
        var (connection, route) = await ConnectAsync(host);
        using var owned = connection;

        var text = new List<string>();
        await foreach (var delta in connection.StreamChatAsync(route, NewIds(), 1, host.Clock.GetUtcNow().AddSeconds(30),
            null, [], "Hello", 0.7, Martlet.Core.Settings.GenerationSettings.ChatReplyTokens, 98_304 + 4_096))
            text.Add(delta);

        Assert.Equal(["Hi!"], text);
        var request = Assert.Single(ollama.Requests).RootElement;
        // Without a Thinking steps choice the model keeps its own default.
        Assert.False(request.TryGetProperty("think", out _));
        var options = request.GetProperty("options");
        Assert.Equal(4_096, options.GetProperty("num_predict").GetInt32());
        Assert.Equal(OllamaRelayWorker.MaximumContextTokens, options.GetProperty("num_ctx").GetInt32());
    }

    [Fact]
    public async Task Relay_reports_a_missing_model_or_stopped_ollama_as_unavailable()
    {
        await using var ollama = await FakeOllama.StartAsync(404, "{\"error\":\"model 'llama3.2:3b' not found\"}");
        await using var worker = new OllamaRelayWorker(ollama.Endpoint, "llama3.2:3b");
        await using var host = await GatewayTestHost.StartAsync(inferenceWorkers: [worker]);
        var (connection, route) = await ConnectAsync(host);
        using var owned = connection;
        var failure = await Assert.ThrowsAsync<Audio2FaceHostException>(async () =>
        {
            await foreach (var _ in connection.StreamChatAsync(route, NewIds(), 0, host.Clock.GetUtcNow().AddSeconds(30),
                null, [], "Hello", 0.7, 64, 4_096)) { }
        });
        Assert.Equal("worker.unavailable", failure.Code);
    }

    [Fact]
    public void Worker_only_relays_to_loopback_and_derives_a_route_model_id_from_the_tag()
    {
        Assert.Equal("qwen2.5-7b", OllamaRelayWorker.Alias("qwen2.5:7b"));
        Assert.Throws<ArgumentException>(() => new OllamaRelayWorker(new Uri("http://192.168.1.5:11434/"), "llama3.2:3b"));
        Assert.Throws<ContractException>(() => new OllamaRelayWorker(new Uri("http://127.0.0.1:11434/"), "llama3.2:cloud"));
        Assert.Throws<ArgumentException>(() => OllamaRelayWorker.DeepThinking(new Uri("http://192.168.1.5:11435/"), "qwen3:8b"));
    }

    [Fact]
    public async Task Deep_thinking_role_has_its_own_route_and_thinks_while_the_conversation_model_replies()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var conversation = await FakeOllama.StartAsync(200,
            "{\"message\":{\"role\":\"assistant\",\"content\":\"Still here.\"},\"done\":true,\"done_reason\":\"stop\"}");
        await using var deep = await FakeOllama.StartAsync(200, release.Task,
            "{\"message\":{\"role\":\"assistant\",\"content\":\"Plan: \"},\"done\":false}",
            "{\"message\":{\"role\":\"assistant\",\"content\":\"rest on Sunday.\"},\"done\":true,\"done_reason\":\"stop\"}");
        await using var thinking = new OllamaRelayWorker(conversation.Endpoint, "gemma4:e4b");
        await using var deepWorker = OllamaRelayWorker.DeepThinking(deep.Endpoint, "qwen3:8b");
        // Each Ollama server on its own graphics card, so the think runs beside the reply. On a shared card a live reply stops
        // the think (live turn first, GpuPriorityTests).
        thinking.Route.PlaceOn(["GPU-aaaa-0000"]);
        deepWorker.Route.PlaceOn(["GPU-bbbb-1111"]);

        // Its own route ID and path, with the conversation model's contract and bounds, which clients accept like any route.
        Assert.Equal((Martlet.Core.Settings.SelfHostSetup.DeepThinkingRouteId, Martlet.Core.Settings.SelfHostSetup.DeepThinkingPath,
                GatewayInferenceKind.OllamaChat, OllamaRelayWorker.DeepThinkingWorkerId),
            (deepWorker.Route.RouteId, deepWorker.Route.Path, deepWorker.Route.Kind, deepWorker.Route.WorkerId));
        Assert.Equal((thinking.Route.ContractId, thinking.Route.MaximumDuration, thinking.Route.MaximumRequestBytes),
            (deepWorker.Route.ContractId, deepWorker.Route.MaximumDuration, deepWorker.Route.MaximumRequestBytes));
        var capability = GatewayInferenceRouteCapability.From(deepWorker.Route);
        Assert.Equal(deepWorker.Route.RouteId, GatewayInferenceRoute.FromCapability(capability).RouteId);
        Assert.Throws<GatewayProtocolException>(() => GatewayInferenceRoute.FromCapability(capability with { Path = thinking.Route.Path }));
        Assert.Throws<GatewayProtocolException>(() => GatewayInferenceRoute.FromCapability(capability with { RouteId = "martlet.gateway.other-chat.v1" }));

        await using var host = await GatewayTestHost.StartAsync(inferenceWorkers: [thinking, deepWorker]);
        var card = host.OpenPairing(GatewayRole.Voice, "desktop-test");
        var (pairing, secret) = await Audio2FaceHostClient.PairAsync(host.Origin.CanonicalOrigin, card.HostId,
            card.SpkiFingerprint, "desktop-test", card.PairingId, card.Token.Reveal());
        using var replies = new Audio2FaceHostConnection(pairing, secret, host.Clock);
        using var thinks = new Audio2FaceHostConnection(pairing, secret, host.Clock);
        var routes = await replies.ReadRoutesAsync();
        var replyRoute = Assert.Single(routes, r => r.RouteId == HostRoute.OllamaChatRouteId);
        var deepRoute = Assert.Single(routes, r => r.RouteId == HostRoute.DeepThinkingRouteId);
        Assert.Equal(("qwen3-8b", HostRoute.DeepThinkingPath), (deepRoute.ModelId, deepRoute.Path));
        // Handing Thinking to this host saves the route it advertises, long-think bound included.
        var saved = replyRoute.Snapshot(Martlet.Core.Settings.SetupRouteType.GatewayOllama);
        saved.Validate();
        Assert.Equal((int)GatewayInferenceProtocol.MaximumJobDuration.TotalSeconds, saved.MaximumDurationSeconds);

        var thought = new List<string>();
        var firstThought = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var think = Task.Run(async () =>
        {
            await foreach (var delta in thinks.StreamChatAsync(deepRoute, NewIds(), 1, host.Clock.GetUtcNow().AddSeconds(60), null, [],
                "Plan my week.", 0.7, 4_096, 32_768, null, new() { Reasoning = true, ContextTokens = 32_768 }))
            {
                thought.Add(delta);
                firstThought.TrySetResult();
            }
        });
        await firstThought.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var reply = new List<string>();
        await foreach (var delta in replies.StreamChatAsync(replyRoute, NewIds(), 2, host.Clock.GetUtcNow().AddSeconds(30), null, [],
            "Are you there?", 0.7, 256, 8_192))
            reply.Add(delta);

        // The reply finished on Thinking's route while the think was still running on its own.
        Assert.Equal(["Still here."], reply);
        Assert.False(think.IsCompleted);
        release.SetResult();
        await think.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(["Plan: ", "rest on Sunday."], thought);
        var toDeep = Assert.Single(deep.Requests).RootElement;
        Assert.Equal("qwen3:8b", toDeep.GetProperty("model").GetString());
        Assert.True(toDeep.GetProperty("think").GetBoolean());
        Assert.Equal(32_768, toDeep.GetProperty("options").GetProperty("num_ctx").GetInt32());
        var toConversation = Assert.Single(conversation.Requests).RootElement;
        Assert.Equal("gemma4:e4b", toConversation.GetProperty("model").GetString());
        Assert.False(toConversation.TryGetProperty("think", out _));

        // The desktop's client sends only a route whose ID and path belong together.
        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await foreach (var _ in thinks.StreamChatAsync(deepRoute with { Path = HostRoute.OllamaChatPath }, NewIds(), 3,
                host.Clock.GetUtcNow().AddSeconds(30), null, [], "Hello", 0.7, 64, 4_096)) { }
        });
    }

    [Fact]
    public async Task Deep_thinking_role_with_slots_runs_that_many_thinks_at_once_and_turns_one_more_away()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var deep = await FakeOllama.StartAsync(200, release.Task,
            "{\"message\":{\"role\":\"assistant\",\"content\":\"Plan: \"},\"done\":false}",
            "{\"message\":{\"role\":\"assistant\",\"content\":\"rest.\"},\"done\":true,\"done_reason\":\"stop\"}");
        await using var deepWorker = OllamaRelayWorker.DeepThinking(deep.Endpoint, "qwen3:8b", slots: 2);
        Assert.Equal(2, deepWorker.Route.MaximumConcurrency);
        // Only the Deep thinking route may run several at once, within its bound; clients read the slots from its capability.
        var capability = GatewayInferenceRouteCapability.From(deepWorker.Route);
        Assert.Equal(2, GatewayInferenceRoute.FromCapability(capability).MaximumConcurrency);
        Assert.Throws<GatewayProtocolException>(() => GatewayInferenceRoute.FromCapability(capability with
            { MaximumConcurrency = Martlet.Core.Settings.SelfHostSetup.DeepThinkingMaximumSlots + 1 }));
        await using var conversation = new OllamaRelayWorker(new Uri("http://127.0.0.1:11434/"), "gemma4:e4b");
        Assert.Throws<GatewayProtocolException>(() => GatewayInferenceRoute.FromCapability(
            GatewayInferenceRouteCapability.From(conversation.Route) with { MaximumConcurrency = 2 }));
        Assert.Throws<GatewayProtocolException>(() => OllamaRelayWorker.DeepThinking(deep.Endpoint, "qwen3:8b",
            slots: Martlet.Core.Settings.SelfHostSetup.DeepThinkingMaximumSlots + 1));
        Assert.Throws<GatewayProtocolException>(() => new OllamaRelayWorker(deep.Endpoint, "gemma4:e4b", slots: 2));

        await using var host = await GatewayTestHost.StartAsync(inferenceWorkers: [deepWorker]);
        var card = host.OpenPairing(GatewayRole.Voice, "desktop-test");
        var (pairing, secret) = await Audio2FaceHostClient.PairAsync(host.Origin.CanonicalOrigin, card.HostId,
            card.SpkiFingerprint, "desktop-test", card.PairingId, card.Token.Reveal());
        var connections = Enumerable.Range(0, 3).Select(_ => new Audio2FaceHostConnection(pairing, secret, host.Clock)).ToArray();
        try
        {
            var route = Assert.Single(await connections[0].ReadRoutesAsync(), r => r.RouteId == HostRoute.DeepThinkingRouteId);
            Assert.Equal(2, route.MaximumConcurrency);
            var started = new[] { new TaskCompletionSource(), new TaskCompletionSource() };
            var thinks = Enumerable.Range(0, 2).Select(i => Task.Run(async () =>
            {
                var text = "";
                await foreach (var delta in connections[i].StreamChatAsync(route, NewIds(), 1 + i, host.Clock.GetUtcNow().AddSeconds(60),
                    null, [], $"Task {i}", 0.7, 256, 8_192))
                {
                    text += delta;
                    started[i].TrySetResult();
                }
                return text;
            })).ToArray();
            await Task.WhenAll(started.Select(s => s.Task)).WaitAsync(TimeSpan.FromSeconds(20));
            Assert.All(thinks, t => Assert.False(t.IsCompleted));

            var busy = await Assert.ThrowsAsync<Audio2FaceHostException>(async () =>
            {
                await foreach (var _ in connections[2].StreamChatAsync(route, NewIds(), 3, host.Clock.GetUtcNow().AddSeconds(30),
                    null, [], "One more", 0.7, 64, 4_096)) { }
            });
            Assert.Equal("job.busy", busy.Code);

            release.SetResult();
            Assert.Equal(["Plan: rest.", "Plan: rest."], await Task.WhenAll(thinks).WaitAsync(TimeSpan.FromSeconds(20)));
            // A slot is free again once a think finished.
            var again = "";
            await foreach (var delta in connections[2].StreamChatAsync(route, NewIds(), 4, host.Clock.GetUtcNow().AddSeconds(30),
                null, [], "Now", 0.7, 64, 4_096))
                again += delta;
            Assert.Equal("Plan: rest.", again);
            Assert.Equal(3, deep.Requests.Count);
        }
        finally
        {
            foreach (var connection in connections) connection.Dispose();
        }
    }
}
