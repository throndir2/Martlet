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

        internal static async Task<FakeOllama> StartAsync(int status, params string[] lines)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            var fake = new FakeOllama(app);
            app.MapPost("/api/chat", async context =>
            {
                fake.Requests.Add(await JsonDocument.ParseAsync(context.Request.Body));
                context.Response.StatusCode = status;
                context.Response.ContentType = "application/x-ndjson";
                foreach (var line in lines)
                {
                    await context.Response.WriteAsync(line + "\n");
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

        // Out of range: rejected at the gateway before it reaches Ollama.
        await Assert.ThrowsAsync<Audio2FaceHostException>(async () =>
        {
            await foreach (var _ in connection.StreamChatAsync(route, NewIds(), 2, host.Clock.GetUtcNow().AddSeconds(30),
                null, [], "Hello", 0.7, 64, 4_096, null, new() { ContextTokens = 8_192 })) { }
        });
        Assert.Single(ollama.Requests);
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
    }
}
