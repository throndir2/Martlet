using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;

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
    public async Task Screen_image_needs_its_own_permission_and_rides_as_an_image_url_part()
    {
        var image = new BoundedImage([0xFF, 0xD8, 0xFF, .. new byte[40]], ImageMediaType.Jpeg, 16, 9);
        var limits = new TextGenerationLimits();
        await using (var refused = new LoopbackServer(Trace))
        {
            var context = ProviderFixtures.Context() with { Deadline = DateTimeOffset.UtcNow.AddMinutes(1) };
            using var adapter = ChatCompletionsTextGenerationAdapter.Create(refused.BaseUrl, null);
            var result = await TextFixtures.Collect(adapter.Stream(context, Model, new("(Screen glance.)", image: image),
                limits, Authorize(context, limits, refused.BaseUrl)));
            Assert.Equal(ProviderFailureCode.ConsentMissing, result.Result.Failure?.Code);
        }
        await using var server = new LoopbackServer(Trace);
        var allowed = ProviderFixtures.Context() with { Deadline = DateTimeOffset.UtcNow.AddMinutes(1) };
        using var vision = ChatCompletionsTextGenerationAdapter.Create(server.BaseUrl, null);
        var permission = new TextDisclosureAuthorization(new(new(server.BaseUrl), ProviderRole.Llm, Model.UpstreamModelId),
            Model, allowed.Ids, allowed.Epoch, limits, allowed.Deadline, true, true, allowImageDisclosure: true);
        var completed = await TextFixtures.Collect(vision.Stream(allowed, Model, new("(Screen glance.)", "Stay quiet", image: image),
            limits, permission));
        Assert.Equal(TextGenerationOutcome.Completed, completed.Result.Outcome);
        using var json = JsonDocument.Parse((await server.Request).Body);
        var parts = json.RootElement.GetProperty("messages")[1].GetProperty("content").EnumerateArray().ToArray();
        Assert.Equal("(Screen glance.)", parts[0].GetProperty("text").GetString());
        Assert.Equal(image.ToDataUrl(), parts[1].GetProperty("image_url").GetProperty("url").GetString());
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
    [InlineData(ChatCompletionsEndpointCatalog.OpenRouterBaseUrl, true)]
    [InlineData(ChatCompletionsEndpointCatalog.NvidiaBuildBaseUrl, false)]
    [InlineData(BaseUrl, false)]
    public async Task Named_and_custom_bases_share_scoped_wire_path_but_only_openrouter_disables_upstream_fallback(
        string baseUrl, bool openRouter)
    {
        var context = ProviderFixtures.Context();
        var limits = new TextGenerationLimits();
        var credentials = new FixtureCredentials();
        Uri? requestUri = null;
        string? authorization = null;
        bool hasCookie = false;
        var handler = new TextRecordingHandler
        {
            Inspect = request =>
            {
                requestUri = request.RequestUri;
                authorization = request.Headers.Authorization?.ToString();
                hasCookie = request.Headers.Contains("Cookie");
            },
            Respond = (_, _) => Task.FromResult(TextRecordingHandler.Sse(Trace))
        };
        using var adapter = ChatCompletionsTextGenerationAdapter.CreateForFixture(baseUrl, handler,
            credentials, new FixtureClock());
        var result = await TextFixtures.Collect(adapter.Stream(context, Model, new("Private context"), limits,
            Authorize(context, limits, baseUrl)));
        Assert.Equal(TextGenerationOutcome.Completed, result.Result.Outcome);
        Assert.Equal(1, credentials.Calls);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(baseUrl + "/chat/completions", requestUri!.AbsoluteUri);
        Assert.Equal("Bearer " + ProviderFixtures.Secret, authorization);
        Assert.False(hasCookie);
        using var json = JsonDocument.Parse(handler.Body);
        var root = json.RootElement;
        string[] expectedFields = openRouter
            ? ["max_tokens", "messages", "model", "provider", "stream"]
            : ["max_tokens", "messages", "model", "stream"];
        Assert.Equal(expectedFields,
            root.EnumerateObject().Select(property => property.Name).Order());
        Assert.Equal(Model.UpstreamModelId, root.GetProperty("model").GetString());
        Assert.Equal(limits.MaxOutputTokens, root.GetProperty("max_tokens").GetInt32());
        Assert.True(root.GetProperty("stream").GetBoolean());
        if (openRouter)
        {
            var provider = root.GetProperty("provider");
            Assert.Equal(new[] { "allow_fallbacks" }, provider.EnumerateObject().Select(property => property.Name));
            Assert.False(provider.GetProperty("allow_fallbacks").GetBoolean());
        }
        Assert.Equal("Private context", root.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task Openrouter_repeated_terminal_usage_frame_is_accounting_not_a_second_finish()
    {
        var accounting = "data: " + JsonSerializer.Serialize(new
        {
            id = "chat-fixture", @object = "chat.completion.chunk", model = "canonical-server-model",
            choices = new[] { new
            {
                index = 0, delta = new { role = "assistant", content = "" },
                finish_reason = "stop", native_finish_reason = "stop"
            } },
            usage = new { prompt_tokens = 3, completion_tokens = 2, total_tokens = 5 }
        }) + "\n\n";
        var trace = Chunk("hello", "stop") + accounting + "data: [DONE]\n\n";
        var result = await RunFixture(trace, baseUrl: ChatCompletionsEndpointCatalog.OpenRouterBaseUrl);
        Assert.Equal(TextGenerationOutcome.Completed, result.Result.Outcome);
        Assert.Equal(new TextGenerationUsage(3, 2, 5), result.Result.Usage);
        Assert.Equal(new[] { "hello" }, result.Events.Where(item => item.Kind == ProviderEventKind.TextDelta)
            .Select(item => item.Text));
    }

    [Theory]
    [InlineData("no-usage")]
    [InlineData("extra-content")]
    [InlineData("changed-finish")]
    [InlineData("duplicate-usage")]
    public async Task Repeated_terminal_chunk_cannot_extend_content_or_duplicate_accounting(string variant)
    {
        var content = variant == "extra-content" ? "late" : "";
        var reason = variant == "changed-finish" ? "length" : "stop";
        var accounting = "data: " + JsonSerializer.Serialize(new
        {
            id = "chat-fixture", @object = "chat.completion.chunk", model = "canonical-server-model",
            choices = new[] { new
            {
                index = 0, delta = new { role = "assistant", content }, finish_reason = reason
            } },
            usage = variant == "no-usage" ? null : (object)new { prompt_tokens = 3, completion_tokens = 2, total_tokens = 5 }
        }) + "\n\n";
        var trace = Chunk("hello", "stop") + accounting +
            (variant == "duplicate-usage" ? accounting : "") + "data: [DONE]\n\n";
        var result = await RunFixture(trace, baseUrl: ChatCompletionsEndpointCatalog.OpenRouterBaseUrl);
        Assert.Equal(ProviderFailureCode.ResponseSchema, result.Result.Failure!.Code);
        Assert.NotEqual(TextGenerationOutcome.Completed, result.Result.Outcome);
    }

    [Theory]
    [InlineData(ChatCompletionsEndpointCatalog.OpenRouterBaseUrl, 401, ProviderFailureCode.Authentication)]
    [InlineData(ChatCompletionsEndpointCatalog.OpenRouterBaseUrl, 402, ProviderFailureCode.QuotaExceeded)]
    [InlineData(ChatCompletionsEndpointCatalog.OpenRouterBaseUrl, 429, ProviderFailureCode.RateLimited)]
    [InlineData(ChatCompletionsEndpointCatalog.NvidiaBuildBaseUrl, 402, ProviderFailureCode.QuotaExceeded)]
    [InlineData(ChatCompletionsEndpointCatalog.NvidiaBuildBaseUrl, 422, ProviderFailureCode.RequestRejected)]
    [InlineData(ChatCompletionsEndpointCatalog.NvidiaBuildBaseUrl, 410, ProviderFailureCode.ModelRetired)]
    [InlineData(BaseUrl, 402, ProviderFailureCode.QuotaExceeded)]
    public async Task Named_endpoint_failures_are_actionable_and_do_not_include_provider_body(
        string baseUrl, int status, ProviderFailureCode expected)
    {
        const string privateError = "synthetic-private-provider-error";
        var context = ProviderFixtures.Context();
        var limits = new TextGenerationLimits();
        var handler = new TextRecordingHandler
        {
            Respond = (_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = new StringContent($"{{\"error\":{{\"message\":\"{privateError}\"}}}}")
            })
        };
        using var adapter = ChatCompletionsTextGenerationAdapter.CreateForFixture(baseUrl, handler,
            new FixtureCredentials(), new FixtureClock());
        var result = await TextFixtures.Collect(adapter.Stream(context, Model, new("Private context"),
            limits, Authorize(context, limits, baseUrl)));
        Assert.Equal(expected, result.Result.Failure!.Code);
        Assert.DoesNotContain(privateError, result.Result.Failure.Error.Summary);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Retired_model_is_classified_and_its_reason_reaches_only_the_local_diagnostics_sink()
    {
        const string reason = "The model 'org/model:q4' has reached its end of life on 2026-08-26T09:00:00Z and is no longer available.";
        var lines = new List<string>();
        ProviderDiagnostics.SetSink(line => { lock (lines) lines.Add(line); });
        try
        {
            var context = ProviderFixtures.Context();
            var limits = new TextGenerationLimits();
            var handler = new TextRecordingHandler
            {
                Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Gone)
                {
                    Content = new StringContent(
                        $"{{\"type\":\"about:blank\",\"title\":\"Gone\",\"status\":410,\"detail\":\"{reason}\"}}",
                        Encoding.UTF8, "application/problem+json")
                })
            };
            using var adapter = ChatCompletionsTextGenerationAdapter.CreateForFixture(ChatCompletionsEndpointCatalog.NvidiaBuildBaseUrl,
                handler, new FixtureCredentials(), new FixtureClock());
            var result = await TextFixtures.Collect(adapter.Stream(context, Model, new("Private context"),
                limits, Authorize(context, limits, ChatCompletionsEndpointCatalog.NvidiaBuildBaseUrl)));
            Assert.Equal(ProviderFailureCode.ModelRetired, result.Result.Failure!.Code);
            Assert.DoesNotContain("end of life", result.Result.Failure.Error.Summary);
            string line;
            lock (lines) line = Assert.Single(lines, l => l.Contains(reason, StringComparison.Ordinal));
            Assert.Contains("Chat Completions request failed: ModelRetired", line);
            Assert.Contains("(model org/model:q4)", line);
            Assert.Contains("https://integrate.api.nvidia.com/v1/chat/completions", line);
            Assert.Contains("HTTP 410", line);
            Assert.Contains(reason, line);
            Assert.DoesNotContain("Private context", line);
        }
        finally { ProviderDiagnostics.SetSink(null); }
    }

    [Theory]
    [InlineData("{\"error\":{\"message\":\"Incorrect API key provided: nvapi-abcdefghijklmnop1234\",\"code\":\"invalid_api_key\"}}",
        "[invalid_api_key]; Incorrect API key provided: [redacted]")]
    [InlineData("{\"detail\":[{\"loc\":[\"body\",\"temperature\"],\"msg\":\"Input should be less than or equal to 1\"}]}",
        "body.temperature: Input should be less than or equal to 1")]
    [InlineData("404 page not found\n", "404 page not found")]
    public void Provider_error_bodies_are_described_on_one_redacted_line(string body, string expected) =>
        Assert.Equal(expected, ProviderDiagnostics.Describe(Encoding.UTF8.GetBytes(body)));

    [Fact]
    public async Task Openrouter_midstream_error_fails_without_disclosing_provider_message()
    {
        const string privateError = "synthetic-private-provider-error";
        var trace = Chunk("partial") +
            $"data: {{\"error\":{{\"code\":\"server_error\",\"message\":\"{privateError}\"}}}}\n\n";
        var result = await RunFixture(trace, baseUrl: ChatCompletionsEndpointCatalog.OpenRouterBaseUrl);
        Assert.Equal(ProviderFailureCode.RequestRejected, result.Result.Failure!.Code);
        Assert.DoesNotContain(privateError, result.Result.Failure.Error.Summary);
        Assert.NotEqual(TextGenerationOutcome.Completed, result.Result.Outcome);
    }

    [Theory]
    [InlineData("endpoint", ProviderFailureCode.OriginRejected)]
    [InlineData("model", ProviderFailureCode.ConsentMismatch)]
    [InlineData("credential", ProviderFailureCode.CredentialBindingMismatch)]
    public async Task Named_destination_rejects_stale_consent_or_cross_origin_credential_before_send(
        string scenario, ProviderFailureCode expected)
    {
        var context = ProviderFixtures.Context();
        var limits = new TextGenerationLimits();
        var baseUrl = ChatCompletionsEndpointCatalog.OpenRouterBaseUrl;
        var other = ChatCompletionsEndpointCatalog.NvidiaBuildBaseUrl;
        var binding = new ProviderCredentialBinding(
            new(scenario == "endpoint" ? other : baseUrl), ProviderRole.Llm,
            scenario == "model" ? "synthetic/other-model" : Model.UpstreamModelId);
        var authorization = new TextDisclosureAuthorization(binding, Model, context.Ids, context.Epoch,
            limits, context.Deadline, true, true);
        var credentials = new FixtureCredentials
        {
            Resolve = (scope, _) => ValueTask.FromResult<BoundProviderCredential?>(
                new(scope with { Origin = new(other) }, ProviderFixtures.Secret))
        };
        var handler = new TextRecordingHandler();
        using var adapter = ChatCompletionsTextGenerationAdapter.CreateForFixture(baseUrl, handler,
            credentials, new FixtureClock());
        var result = await TextFixtures.Collect(adapter.Stream(context, Model, new("Private context"),
            limits, authorization));
        Assert.Equal(expected, result.Result.Failure!.Code);
        Assert.Equal(0, handler.Calls);
        Assert.Equal(scenario == "credential" ? 1 : 0, credentials.Calls);
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

    [Theory]
    [InlineData(ChatCompletionsEndpointCatalog.NvidiaBuildBaseUrl)]
    [InlineData(ChatCompletionsEndpointCatalog.OpenRouterBaseUrl)]
    public async Task Provider_reasoning_traces_and_null_roles_are_not_spoken(string baseUrl)
    {
        static string Raw(string delta, string? finish = null, string usage = "null") =>
            "data: {\"id\":\"chat-fixture\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"canonical-server-model\"," +
            $"\"choices\":[{{\"index\":0,\"delta\":{delta},\"logprobs\":null,\"finish_reason\":{(finish is null ? "null" : $"\"{finish}\"")}}}],\"usage\":{usage}}}\n\n";
        var trace = Raw("{\"role\":\"assistant\",\"content\":\"\",\"reasoning_content\":null}") +
            Raw("{\"role\":null,\"content\":null,\"reasoning_content\":\"Thinking privately.\"}") +
            Raw("{\"content\":\"\",\"reasoning\":\"More thought.\",\"reasoning_details\":[{\"type\":\"reasoning.text\",\"text\":\"More thought.\"}]}") +
            Raw("{\"role\":null,\"content\":\"Hello \",\"tool_calls\":[],\"reasoning\":null,\"reasoning_details\":[]}") +
            Raw("{\"role\":null,\"content\":\"world.\"}", "stop") +
            Raw("{\"role\":\"assistant\",\"content\":\"\",\"reasoning\":null}", "stop",
                "{\"prompt_tokens\":4,\"completion_tokens\":9,\"total_tokens\":13}") +
            "data: [DONE]\n\n";
        var result = await RunFixture(trace, baseUrl: baseUrl);
        Assert.Equal(TextGenerationOutcome.Completed, result.Result.Outcome);
        Assert.Equal(new TextGenerationUsage(4, 9, 13), result.Result.Usage);
        Assert.Equal(new[] { "Hello ", "world." }, result.Events.Where(item => item.Kind == ProviderEventKind.TextDelta)
            .Select(item => item.Text));
    }

    [Fact]
    public async Task Nvidia_build_default_model_stream_completes()
    {
        // Recorded from google/diffusiongemma-26b-a4b-it on NVIDIA Build (vLLM) on 2026-10-01: the whole reply in one chunk.
        const string trace =
            """data: {"id":"chatcmpl-98dd098f7c431da6","object":"chat.completion.chunk","created":1790909198,"model":"google/diffusiongemma-26b-a4b-it","choices":[{"index":0,"delta":{"role":"assistant","content":""},"logprobs":null,"finish_reason":null}],"prompt_token_ids":null,"prompt_text":null}""" + "\n\n" +
            """data: {"id":"chatcmpl-98dd098f7c431da6","object":"chat.completion.chunk","created":1790909198,"model":"google/diffusiongemma-26b-a4b-it","choices":[{"index":0,"delta":{"content":"Hi! I am Martlet! Welcome!"},"logprobs":null,"finish_reason":"stop","stop_reason":null,"token_ids":null}],"system_fingerprint":"vllm-0.21.0-d9f6bf25"}""" + "\n\n" +
            "data: [DONE]\n\n";
        var result = await RunFixture(trace, baseUrl: ChatCompletionsEndpointCatalog.NvidiaBuildBaseUrl);
        Assert.Equal(TextGenerationOutcome.Completed, result.Result.Outcome);
        Assert.Equal("Hi! I am Martlet! Welcome!", string.Concat(result.Events.Where(item => item.Kind == ProviderEventKind.TextDelta)
            .Select(item => item.Text)));
    }

    private static async Task<(List<ProviderEvent> Events, TextGenerationResult Result)> RunFixture(
        string trace, TextGenerationLimits? limits = null, string baseUrl = BaseUrl)
    {
        var context = ProviderFixtures.Context();
        limits ??= new();
        var handler = new TextRecordingHandler
        {
            Respond = (_, _) => Task.FromResult(TextRecordingHandler.Sse(trace, fragment: 1))
        };
        using var adapter = ChatCompletionsTextGenerationAdapter.CreateForFixture(baseUrl, handler, clock: new FixtureClock());
        return await TextFixtures.Collect(adapter.Stream(context, Model, new("message"), limits, Authorize(context, limits, baseUrl)));
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
