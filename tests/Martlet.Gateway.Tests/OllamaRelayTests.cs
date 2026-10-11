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
            // What Ollama has loaded: one model, mostly on the graphics card.
            app.MapGet("/api/ps", () => Results.Text(
                "{\"models\":[{\"name\":\"llama3.2:3b\",\"model\":\"llama3.2:3b\",\"size\":3000000000,\"size_vram\":2900000000," +
                "\"digest\":\"abc123\",\"context_length\":8192}]}", "application/json"));
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
    public async Task The_machine_report_says_what_the_hosts_ollama_roles_have_loaded()
    {
        await using var ollama = await FakeOllama.StartAsync(200, "{\"done\":true}");
        await using var worker = new OllamaRelayWorker(ollama.Endpoint, "llama3.2:3b");
        await using var deep = OllamaRelayWorker.DeepThinking(ollama.Endpoint, "llama3.2:3b", card: 2);
        Assert.Equal("deep-thinking-2", Assert.Single((await deep.ReadLoadedAsync(CancellationToken.None))!).Role);

        await using var host = await GatewayTestHost.StartAsync(inferenceWorkers: [worker]);
        host.Server.Machine = GatewayMachineReport.Parse(System.Text.Encoding.UTF8.GetBytes(
            "{\"collected_at\":\"2026-10-10T18:00:00Z\",\"method\":\"native\",\"operating_system\":\"Ubuntu 24.04\",\"gpus\":[]}"));
        var (connection, _) = await ConnectAsync(host);
        using var owned = connection;
        var (hardware, _) = await connection.ReadMachineReportAsync();
        var loaded = Assert.Single(hardware!.Loaded!);
        Assert.Equal(("ollama", "llama3.2:3b", 3_000_000_000L, 2_900_000_000L, (int?)8192, "abc123"),
            (loaded.Role, loaded.Model, loaded.Bytes, loaded.GraphicsBytes, loaded.ContextTokens, loaded.Digest));
        // The relay only reads: nothing went to /api/chat, so no model loaded or unloaded.
        Assert.Empty(ollama.Requests);
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
    public async Task Relay_forwards_a_recording_to_the_hosts_ollama_on_the_current_message()
    {
        await using var ollama = await FakeOllama.StartAsync(200,
            "{\"message\":{\"role\":\"assistant\",\"content\":\"Banana.\"},\"done\":true,\"done_reason\":\"stop\"}");
        await using var worker = new OllamaRelayWorker(ollama.Endpoint, "gemma4:e4b");
        await using var host = await GatewayTestHost.StartAsync(inferenceWorkers: [worker]);
        var (connection, route) = await ConnectAsync(host);
        using var owned = connection;
        Assert.True(route.CarriesAudio);
        // The longest recording Martlet sends (30 seconds at 48 kHz) fits the route next to a screen image.
        var format = new Martlet.Core.Audio.PcmFormat
        {
            SampleRate = 48_000, Channels = 1, Encoding = Martlet.Core.Audio.PcmEncoding.Signed16LittleEndian
        };
        var wave = Martlet.Providers.BoundedWaveAudio.FromPcm(format, new byte[48_000 * 2 * 30]).ToBase64();
        var jpeg = Convert.ToBase64String(new byte[] { 0xFF, 0xD8, 0xFF }.Concat(new byte[64]).ToArray());

        var text = new List<string>();
        await foreach (var delta in connection.StreamChatAsync(route, NewIds(), 1, host.Clock.GetUtcNow().AddSeconds(30),
            "Answer with the word only.", [new(false, "Hi"), new(true, "Hey!")], "(Voice message.)", 0, 32, 4_096, [jpeg],
            new() { Reasoning = false }, audio: wave))
            text.Add(delta);

        Assert.Equal(["Banana."], text);
        var messages = Assert.Single(ollama.Requests).RootElement.GetProperty("messages").EnumerateArray().ToArray();
        Assert.False(messages[1].TryGetProperty("images", out _));
        Assert.Equal(new[] { jpeg, wave }, messages[^1].GetProperty("images").EnumerateArray().Select(i => i.GetString()!).ToArray());

        // Not a WAV, or longer than a check-in's minute: rejected at the gateway before it reaches Ollama.
        var notWave = await Assert.ThrowsAsync<Audio2FaceHostException>(async () =>
        {
            await foreach (var _ in connection.StreamChatAsync(route, NewIds(), 2, host.Clock.GetUtcNow().AddSeconds(30),
                null, [], "(Voice message.)", 0.7, 64, 4_096, null, null, audio: Convert.ToBase64String(new byte[64]))) { }
        });
        Assert.Equal("request.invalid", notWave.Code);
        var tooLong = Martlet.Providers.BoundedWaveAudio.FromPcm(format with { SampleRate = 16_000 }, new byte[16_000 * 2 * 61]).ToBase64();
        var tooLarge = await Assert.ThrowsAsync<Audio2FaceHostException>(async () =>
        {
            await foreach (var _ in connection.StreamChatAsync(route, NewIds(), 3, host.Clock.GetUtcNow().AddSeconds(30),
                null, [], "(Voice message.)", 0.7, 64, 4_096, null, null, audio: tooLong)) { }
        });
        Assert.Equal("request.too_large", tooLarge.Code);
        Assert.Single(ollama.Requests);
    }

    [Theory]
    [InlineData(400, "{\"error\":\"this model is missing data required for image input\"}", "request.invalid")]
    [InlineData(500, "{\"error\":\"model does not support audio input\"}", "request.invalid")]
    [InlineData(400, "{\"error\":\"\\\"qwen3:8b\\\" does not support thinking\"}", "worker.failed")]
    [InlineData(500, "{\"error\":\"out of memory\"}", "worker.failed")]
    public async Task Relay_reports_a_refused_recording_so_the_desktop_sends_the_words_alone(int status, string body, string code)
    {
        await using var ollama = await FakeOllama.StartAsync(status, body);
        await using var worker = new OllamaRelayWorker(ollama.Endpoint, "qwen3:8b");
        await using var host = await GatewayTestHost.StartAsync(inferenceWorkers: [worker]);
        var (connection, route) = await ConnectAsync(host);
        using var owned = connection;
        var format = new Martlet.Core.Audio.PcmFormat
        {
            SampleRate = 16_000, Channels = 1, Encoding = Martlet.Core.Audio.PcmEncoding.Signed16LittleEndian
        };
        var wave = Martlet.Providers.BoundedWaveAudio.FromPcm(format, new byte[3_200]).ToBase64();

        var failure = await Assert.ThrowsAsync<Audio2FaceHostException>(async () =>
        {
            await foreach (var _ in connection.StreamChatAsync(route, NewIds(), 1, host.Clock.GetUtcNow().AddSeconds(30),
                null, [], "(Voice message.)", 0.7, 64, 4_096, null, null, audio: wave)) { }
        });
        Assert.Equal(code, failure.Code);
    }

    [Fact]
    public async Task Relay_reports_a_recording_refused_in_the_stream_as_invalid()
    {
        await using var ollama = await FakeOllama.StartAsync(200, "{\"error\":\"audio input is not supported by this model\"}");
        await using var worker = new OllamaRelayWorker(ollama.Endpoint, "qwen3:8b");
        await using var host = await GatewayTestHost.StartAsync(inferenceWorkers: [worker]);
        var (connection, route) = await ConnectAsync(host);
        using var owned = connection;
        var format = new Martlet.Core.Audio.PcmFormat
        {
            SampleRate = 16_000, Channels = 1, Encoding = Martlet.Core.Audio.PcmEncoding.Signed16LittleEndian
        };
        var wave = Martlet.Providers.BoundedWaveAudio.FromPcm(format, new byte[3_200]).ToBase64();

        var failure = await Assert.ThrowsAsync<Audio2FaceHostException>(async () =>
        {
            await foreach (var _ in connection.StreamChatAsync(route, NewIds(), 1, host.Clock.GetUtcNow().AddSeconds(30),
                null, [], "(Voice message.)", 0.7, 64, 4_096, null, null, audio: wave)) { }
        });
        Assert.Equal("request.invalid", failure.Code);
        // Without a recording the same error is the model's own failure.
        var plain = await Assert.ThrowsAsync<Audio2FaceHostException>(async () =>
        {
            await foreach (var _ in connection.StreamChatAsync(route, NewIds(), 2, host.Clock.GetUtcNow().AddSeconds(30),
                null, [], "Hello", 0.7, 64, 4_096)) { }
        });
        Assert.Equal("worker.failed", plain.Code);
    }

    [Fact]
    public void Only_a_current_hosts_conversation_routes_carry_a_recording()
    {
        static HostRoute Route(string id, int bytes) =>
            new(id, "/x", "martlet.ollama-chat", "1", "ollama", "ollama", "1", "gemma4-e4b", "1", new string('0', 64),
                new string('0', 64), bytes, 98_304, 65_536, 4_096, 4_096, 1_048_576, TimeSpan.FromMinutes(5), "request_abort");
        Assert.True(Route(HostRoute.OllamaChatRouteId, Martlet.Core.Settings.SelfHostSetup.OllamaRequestBytes).CarriesAudio);
        Assert.True(Route(HostRoute.DeepThinkingRouteId, Martlet.Core.Settings.SelfHostSetup.OllamaRequestBytes).CarriesAudio);
        // An older host advertised room for one screen image only.
        Assert.False(Route(HostRoute.OllamaChatRouteId, 1_497_432).CarriesAudio);
        Assert.False(Route(HostRoute.F5RouteId, Martlet.Core.Settings.SelfHostSetup.OllamaRequestBytes).CarriesAudio);
        Assert.True(OllamaRelayWorker.RefusesAudio("model does not support audio"));
        Assert.False(OllamaRelayWorker.RefusesAudio("\"qwen3:8b\" does not support thinking"));
        Assert.False(OllamaRelayWorker.RefusesAudio("out of memory"));
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
    public async Task Each_graphics_cards_Thinking_pool_model_has_a_route_of_its_own_with_its_own_slots()
    {
        await using var first = await FakeOllama.StartAsync(200,
            "{\"message\":{\"role\":\"assistant\",\"content\":\"Card one.\"},\"done\":true,\"done_reason\":\"stop\"}");
        await using var second = await FakeOllama.StartAsync(200,
            "{\"message\":{\"role\":\"assistant\",\"content\":\"Card two.\"},\"done\":true,\"done_reason\":\"stop\"}");
        await using var card1 = OllamaRelayWorker.DeepThinking(first.Endpoint, "qwen3:8b", slots: 2);
        await using var card2 = OllamaRelayWorker.DeepThinking(second.Endpoint, "qwen3:8b", card: 2);
        card1.Route.PlaceOn(["GPU-aaaa-0000"]);
        card2.Route.PlaceOn(["GPU-bbbb-1111"]);
        // Card 1 keeps the route every host had; card 2 has its own route, path, destination and worker, on the pool lane too.
        Assert.Equal((Martlet.Core.Settings.SelfHostSetup.DeepThinkingRouteIdFor(2), "/martlet/v1/inference/deep-thinking-2-chat",
                "deep-thinking-2-host", "deep-thinking-2-relay", GatewayLane.Pool, 1),
            (card2.Route.RouteId, card2.Route.Path, card2.Route.DestinationId, card2.Route.WorkerId, card2.Route.Lane, card2.Route.MaximumConcurrency));
        Assert.Equal(Martlet.Core.Settings.SelfHostSetup.DeepThinkingRouteId, card1.Route.RouteId);
        var capability = GatewayInferenceRouteCapability.From(card2.Route);
        Assert.Equal(card2.Route.RouteId, GatewayInferenceRoute.FromCapability(capability).RouteId);
        Assert.Equal(3, GatewayInferenceRoute.FromCapability(capability with { MaximumConcurrency = 3 }).MaximumConcurrency);
        // A card's route with another card's path, or a card beyond the bound, is refused.
        Assert.Throws<GatewayProtocolException>(() => GatewayInferenceRoute.FromCapability(capability with
            { Path = Martlet.Core.Settings.SelfHostSetup.DeepThinkingPath }));
        Assert.Throws<GatewayProtocolException>(() => GatewayInferenceRoute.FromCapability(capability with
            { RouteId = "martlet.gateway.deep-thinking-5-chat.v1", Path = "/martlet/v1/inference/deep-thinking-5-chat" }));
        Assert.Throws<GatewayProtocolException>(() => OllamaRelayWorker.DeepThinking(second.Endpoint, "qwen3:8b",
            card: Martlet.Core.Settings.SelfHostSetup.DeepThinkingMaximumCards + 1));

        await using var host = await GatewayTestHost.StartAsync(inferenceWorkers: [card1, card2]);
        var pairingCard = host.OpenPairing(GatewayRole.Voice, "desktop-test");
        var (pairing, secret) = await Audio2FaceHostClient.PairAsync(host.Origin.CanonicalOrigin, pairingCard.HostId,
            pairingCard.SpkiFingerprint, "desktop-test", pairingCard.PairingId, pairingCard.Token.Reveal());
        using var connection = new Audio2FaceHostConnection(pairing, secret, host.Clock);
        var routes = await connection.ReadRoutesAsync();
        var one = Assert.Single(routes, r => r.RouteId == HostRoute.DeepThinkingRouteId);
        var two = Assert.Single(routes, r => r.RouteId == Martlet.Core.Settings.SelfHostSetup.DeepThinkingRouteIdFor(2));
        Assert.Equal((2, 1), (one.MaximumConcurrency, two.MaximumConcurrency));
        Assert.Equal(["GPU-aaaa-0000"], one.Gpus);
        Assert.Equal(["GPU-bbbb-1111"], two.Gpus);
        Assert.Equal((HostRoute.PoolLane, HostRoute.PoolLane), (one.Lane, two.Lane));
        Assert.True(two.CarriesAudio);
        var text = new List<string>();
        await foreach (var delta in connection.StreamChatAsync(two, NewIds(), 1, host.Clock.GetUtcNow().AddSeconds(30), null, [],
            "Think on card two.", 0.7, 256, 8_192))
            text.Add(delta);
        Assert.Equal(["Card two."], text);
        Assert.Single(second.Requests);
        Assert.Empty(first.Requests);
        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await foreach (var _ in connection.StreamChatAsync(two with { Path = HostRoute.DeepThinkingPath }, NewIds(), 2,
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
