using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Martlet.Core.Contracts;

namespace Martlet.Providers.Tests;

public sealed class ChatCompletionsTests
{
    private const string BaseUrl = "https://example.test/api/v1";
    private static TextModelSelection Model => new("chat-completions", "org/model:q4");

    private static TextDisclosureAuthorization Authorize(ProviderRequestContext context, TextGenerationLimits limits,
        string baseUrl = BaseUrl) => new(new(new(baseUrl), ProviderRole.Llm, Model.UpstreamModelId),
        Model, context.Ids, context.Epoch, limits, context.Deadline, true, true);

    internal static string Chunk(string? text = null, string? finish = null, object? delta = null) =>
        "data: " + JsonSerializer.Serialize(new
        {
            id = "chat-fixture", @object = "chat.completion.chunk", model = "canonical-server-model",
            choices = new[] { new { index = 0, delta = delta ?? new { content = text }, finish_reason = finish } }
        }) + "\n\n";

    private static string Trace => Chunk(delta: new { role = "assistant", content = "" }) +
        Chunk("Hello ") + Chunk("world.", "stop") + "data: [DONE]\n\n";

    [Fact]
    public async Task Production_http_path_sends_explicit_context_and_optional_scoped_key()
    {
        foreach (var keyed in new[] { false, true })
        {
            await using var server = new LoopbackServer(Trace);
            var context = ProviderFixtures.Context() with { Deadline = DateTimeOffset.UtcNow.AddMinutes(1) };
            var limits = new TextGenerationLimits();
            var credentials = new FixtureCredentials();
            using var adapter = ChatCompletionsTextGenerationAdapter.Create(server.BaseUrl, keyed ? credentials : null);
            var stream = adapter.Stream(context, Model,
                new("Current message", "Persona and explicitly selected memory",
                    [new(TextHistoryRole.User, "History question"), new(TextHistoryRole.Assistant, "History answer")]),
                limits, Authorize(context, limits, server.BaseUrl));
            var result = await TextFixtures.Collect(stream);
            Assert.Equal(TextGenerationOutcome.Completed, result.Result.Outcome);
            Assert.Equal("Hello world.", string.Concat(result.Events.Where(e => e.Kind == ProviderEventKind.TextDelta).Select(e => e.Text)));
            var request = await server.Request;
            Assert.StartsWith("POST /api/v1/chat/completions HTTP/1.1\r\n", request.Headers);
            Assert.Equal(keyed, request.Headers.Contains("Authorization: Bearer " + ProviderFixtures.Secret, StringComparison.Ordinal));
            Assert.DoesNotContain("Cookie:", request.Headers);
            using var json = JsonDocument.Parse(request.Body);
            var root = json.RootElement;
            Assert.Equal(new[] { "max_tokens", "messages", "model", "stream" }, root.EnumerateObject().Select(p => p.Name).Order());
            Assert.Equal(Model.UpstreamModelId, root.GetProperty("model").GetString());
            Assert.True(root.GetProperty("stream").GetBoolean());
            Assert.Equal(limits.MaxOutputTokens, root.GetProperty("max_tokens").GetInt32());
            var messages = root.GetProperty("messages").EnumerateArray().ToArray();
            Assert.Equal(new[] { "system", "user", "assistant", "user" }, messages.Select(m => m.GetProperty("role").GetString()));
            Assert.Equal("Persona and explicitly selected memory", messages[0].GetProperty("content").GetString());
            Assert.Equal("Current message", messages[3].GetProperty("content").GetString());
            Assert.Equal(keyed ? 1 : 0, credentials.Calls);
        }
    }

    [Fact]
    public async Task Production_redirect_does_not_forward_body_or_key()
    {
        using var destination = new TcpListener(IPAddress.Loopback, 0);
        destination.Start();
        var port = ((IPEndPoint)destination.LocalEndpoint).Port;
        await using var server = new LoopbackServer("", $"307 Temporary Redirect\r\nLocation: http://127.0.0.1:{port}/leak");
        var context = ProviderFixtures.Context() with { Deadline = DateTimeOffset.UtcNow.AddMinutes(1) };
        var limits = new TextGenerationLimits();
        using var adapter = ChatCompletionsTextGenerationAdapter.Create(server.BaseUrl, new FixtureCredentials());
        var result = await TextFixtures.Collect(adapter.Stream(context, Model, new("Private context"),
            limits, Authorize(context, limits, server.BaseUrl)));
        Assert.Equal(ProviderFailureCode.RedirectRejected, result.Result.Failure!.Code);
        await server.Request;
        Assert.False(destination.Pending());
    }

    [Theory]
    [InlineData("missing", ProviderFailureCode.ConsentMissing)]
    [InlineData("endpoint", ProviderFailureCode.OriginRejected)]
    [InlineData("fragment", ProviderFailureCode.OriginRejected)]
    [InlineData("model", ProviderFailureCode.ConsentMismatch)]
    [InlineData("role", ProviderFailureCode.ConsentMismatch)]
    [InlineData("expired", ProviderFailureCode.ConsentExpired)]
    [InlineData("missing-key", ProviderFailureCode.CredentialUnavailable)]
    [InlineData("wrong-key-binding", ProviderFailureCode.CredentialBindingMismatch)]
    public async Task Authorization_and_configured_credentials_fail_closed(string scenario, ProviderFailureCode expected)
    {
        var context = ProviderFixtures.Context();
        var limits = new TextGenerationLimits();
        var auth = Authorize(context, limits);
        if (scenario is "endpoint" or "fragment" or "model" or "role" or "expired")
            auth = new(auth.Binding with
            {
                Origin = new(scenario == "endpoint" ? "https://example.test/other/v1" : scenario == "fragment" ? BaseUrl + "#unbound" : BaseUrl),
                Role = scenario == "role" ? ProviderRole.Tts : ProviderRole.Llm,
                UpstreamModelId = scenario == "model" ? "other-model" : Model.UpstreamModelId
            }, Model, context.Ids, context.Epoch, limits,
                scenario == "expired" ? ProviderFixtures.Now.AddSeconds(-1) : context.Deadline, true, true);
        var source = new FixtureCredentials
        {
            Resolve = (binding, _) => ValueTask.FromResult<BoundProviderCredential?>(scenario == "missing-key" ? null :
                new(binding with { Origin = new(scenario == "wrong-key-binding" ? "https://example.test/other" : BaseUrl) }, ProviderFixtures.Secret))
        };
        var handler = new TextRecordingHandler();
        using var adapter = ChatCompletionsTextGenerationAdapter.CreateForFixture(BaseUrl, handler, source, new FixtureClock());
        var result = await TextFixtures.Collect(adapter.Stream(context, Model, new("Private context"), limits,
            scenario == "missing" ? null : auth));
        Assert.Equal(expected, result.Result.Failure!.Code);
        Assert.Equal(0, handler.Calls);
        Assert.Equal(scenario is "missing-key" or "wrong-key-binding" ? 1 : 0, source.Calls);
    }

    [Theory]
    [InlineData("truncated", ProviderFailureCode.ResponseTruncated)]
    [InlineData("early-done", ProviderFailureCode.ResponseTruncated)]
    [InlineData("tools", ProviderFailureCode.UnsupportedOutput)]
    [InlineData("length", ProviderFailureCode.OutputTokenLimit)]
    [InlineData("filter", ProviderFailureCode.ContentFiltered)]
    [InlineData("unknown-finish", ProviderFailureCode.UnsupportedOutput)]
    [InlineData("trailing-data", ProviderFailureCode.ResponseSchema)]
    [InlineData("malformed", ProviderFailureCode.ResponseSchema)]
    [InlineData("empty", ProviderFailureCode.ResponseSchema)]
    [InlineData("too-large", ProviderFailureCode.ResponseTooLarge)]
    [InlineData("string-index", ProviderFailureCode.ResponseSchema)]
    public async Task Incomplete_or_unsupported_streams_are_not_success(string scenario, ProviderFailureCode expected)
    {
        var trace = scenario switch
        {
            "truncated" => Chunk("partial"),
            "early-done" => Chunk("partial") + "data: [DONE]\n\n",
            "tools" => Chunk(delta: new { tool_calls = new[] { new { id = "call" } } }),
            "length" => Chunk("partial", "length") + "data: [DONE]\n\n",
            "filter" => Chunk("partial", "content_filter") + "data: [DONE]\n\n",
            "unknown-finish" => Chunk("partial", "unknown"),
            "trailing-data" => Trace + Chunk("late"),
            "malformed" => "data: {\"choices\":1,\"choices\":2}\n\n",
            "empty" => Chunk("", "stop") + "data: [DONE]\n\n",
            "too-large" => Chunk(new string('x', 17)),
            "string-index" => Chunk("text").Replace("\"index\":0", "\"index\":\"0\"", StringComparison.Ordinal),
            _ => throw new InvalidOperationException()
        };
        var result = await RunFixture(trace, new() { MaxTextCharacters = 16 });
        Assert.Equal(expected, result.Result.Failure!.Code);
        Assert.NotEqual(TextGenerationOutcome.Completed, result.Result.Outcome);
    }

    [Fact]
    public async Task Refusal_and_usage_are_normalized_without_speaking_refusal_deltas()
    {
        var trace = Chunk(delta: new { refusal = "Cannot comply." }) + Chunk(finish: "stop") +
            "data: {\"id\":\"chat-fixture\",\"object\":\"chat.completion.chunk\",\"model\":\"canonical-server-model\",\"choices\":[],\"usage\":{\"prompt_tokens\":4,\"completion_tokens\":3,\"total_tokens\":7}}\n\n" +
            "data: [DONE]\n\n";
        var result = await RunFixture(trace);
        Assert.Equal(TextGenerationOutcome.Refused, result.Result.Outcome);
        Assert.Equal("Cannot comply.", result.Result.RefusalText);
        Assert.Equal(new TextGenerationUsage(4, 3, 7), result.Result.Usage);
        Assert.DoesNotContain(result.Events, item => item.Kind == ProviderEventKind.TextDelta);
    }

    [Fact]
    public async Task Cancellation_after_delta_closes_stream_and_never_completes()
    {
        var context = ProviderFixtures.Context();
        var limits = new TextGenerationLimits();
        var body = new FragmentedTextBody(Encoding.UTF8.GetBytes(Trace));
        var handler = new TextRecordingHandler { Respond = (_, _) => Task.FromResult(TextRecordingHandler.Sse(body)) };
        using var adapter = ChatCompletionsTextGenerationAdapter.CreateForFixture(BaseUrl, handler, clock: new FixtureClock());
        using var stop = new CancellationTokenSource();
        var stream = adapter.Stream(context, Model, new("message"), limits, Authorize(context, limits), stop.Token);
        await foreach (var item in stream)
            if (item.Kind == ProviderEventKind.TextDelta) stop.Cancel();
        Assert.Equal(TextGenerationOutcome.Canceled, stream.Result!.Outcome);
        Assert.True(body.Disposed);
        Assert.Throws<InvalidOperationException>(() => stream.GetAsyncEnumerator());
    }

    [Fact]
    public async Task Used_permission_cannot_resolve_or_send_again()
    {
        var context = ProviderFixtures.Context();
        var limits = new TextGenerationLimits();
        var auth = Authorize(context, limits);
        var source = new FixtureCredentials();
        var handler = new TextRecordingHandler { Respond = (_, _) => Task.FromResult(TextRecordingHandler.Sse(Trace)) };
        using var adapter = ChatCompletionsTextGenerationAdapter.CreateForFixture(BaseUrl, handler, source, new FixtureClock());
        Assert.Equal(TextGenerationOutcome.Completed,
            (await TextFixtures.Collect(adapter.Stream(context, Model, new("message"), limits, auth))).Result.Outcome);
        Assert.Equal(ProviderFailureCode.ConsentConsumed,
            (await TextFixtures.Collect(adapter.Stream(context, Model, new("message"), limits, auth))).Result.Failure!.Code);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(1, source.Calls);
    }

    [Theory]
    [InlineData(false, ProviderFailureCode.FirstDeltaTimeout)]
    [InlineData(true, ProviderFailureCode.IdleTimeout)]
    public async Task Silent_response_or_unfinished_terminal_eof_is_bounded(bool afterText, ProviderFailureCode expected)
    {
        var context = ProviderFixtures.Context() with { Deadline = DateTimeOffset.UtcNow.AddMinutes(1) };
        var limits = new TextGenerationLimits
        {
            FirstDeltaTimeout = TimeSpan.FromMilliseconds(300),
            IdleTimeout = TimeSpan.FromMilliseconds(600)
        };
        var bytes = Encoding.UTF8.GetBytes(afterText ? Trace : "");
        var body = new FragmentedTextBody(bytes, 4096);
        body.BeforeRead = token => body.BytesRead == bytes.Length ? Task.Delay(Timeout.Infinite, token) : Task.CompletedTask;
        var handler = new TextRecordingHandler { Respond = (_, _) => Task.FromResult(TextRecordingHandler.Sse(body)) };
        using var adapter = ChatCompletionsTextGenerationAdapter.CreateForFixture(BaseUrl, handler);
        var result = await TextFixtures.Collect(adapter.Stream(context, Model, new("message"), limits, Authorize(context, limits)));
        Assert.Equal(expected, result.Result.Failure!.Code);
        Assert.True(body.Disposed);
    }

    private static async Task<(List<ProviderEvent> Events, TextGenerationResult Result)> RunFixture(string trace, TextGenerationLimits? limits = null)
    {
        var context = ProviderFixtures.Context();
        limits ??= new();
        var handler = new TextRecordingHandler
        {
            Respond = (_, _) => Task.FromResult(TextRecordingHandler.Sse(trace, fragment: 1))
        };
        using var adapter = ChatCompletionsTextGenerationAdapter.CreateForFixture(BaseUrl, handler, clock: new FixtureClock());
        return await TextFixtures.Collect(adapter.Stream(context, Model, new("message"), limits, Authorize(context, limits)));
    }

    private sealed class LoopbackServer : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(15));
        public string BaseUrl { get; }
        public Task<(string Headers, byte[] Body)> Request { get; }

        public LoopbackServer(string response, string status = "200 OK")
        {
            listener.Start();
            BaseUrl = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/api/v1";
            Request = Serve(response, status);
        }

        private async Task<(string Headers, byte[] Body)> Serve(string response, string status)
        {
            using var connection = await listener.AcceptTcpClientAsync(lifetime.Token);
            await using var stream = connection.GetStream();
            using var headers = new MemoryStream();
            var one = new byte[1];
            while (headers.Length < 16_384)
            {
                await stream.ReadExactlyAsync(one, lifetime.Token);
                headers.WriteByte(one[0]);
                if (headers.Length >= 4 && headers.GetBuffer().AsSpan((int)headers.Length - 4, 4).SequenceEqual("\r\n\r\n"u8))
                    break;
            }
            var text = Encoding.ASCII.GetString(headers.ToArray());
            var length = int.Parse(text.Split("\r\n").Single(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).Split(':')[1]);
            Assert.InRange(length, 1, 131_072);
            var body = new byte[length];
            await stream.ReadExactlyAsync(body, lifetime.Token);
            var payload = Encoding.UTF8.GetBytes(response);
            var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: text/event-stream\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(head, lifetime.Token);
            foreach (var value in payload)
                await stream.WriteAsync(new[] { value }, lifetime.Token);
            return (text, body);
        }

        public async ValueTask DisposeAsync()
        {
            await lifetime.CancelAsync();
            listener.Stop();
            try { await Request; }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            finally { lifetime.Dispose(); }
        }
    }
}
