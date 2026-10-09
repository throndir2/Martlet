using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Providers.Tests;

public sealed class LocalModelServersTests
{
    [Theory]
    [InlineData("1234", "http://127.0.0.1:1234/v1")]
    [InlineData("localhost:1234", "http://127.0.0.1:1234/v1")]
    [InlineData("http://localhost:1234/", "http://127.0.0.1:1234/v1")]
    [InlineData("http://LOCALHOST:8080/v1/", "http://127.0.0.1:8080/v1")]
    [InlineData("127.0.0.1:8080/v1/chat/completions", "http://127.0.0.1:8080/v1")]
    [InlineData("http://127.0.0.1:5001/v1/models", "http://127.0.0.1:5001/v1")]
    [InlineData("localhost:12434", "http://127.0.0.1:12434/engines/v1")]
    [InlineData("localhost:13305", "http://127.0.0.1:13305/api/v1")]
    [InlineData("localhost:8000", "http://127.0.0.1:8000/v1")]
    [InlineData("http://[::1]:1337", "http://[::1]:1337/v1")]
    [InlineData("  localhost:9999  ", "http://127.0.0.1:9999/v1")]
    public void Addresses_on_this_computer_become_the_canonical_base_URL(string typed, string expected)
    {
        Assert.Equal(expected, LocalModelServers.Normalize(typed, out var problem));
        Assert.Equal("", problem);
        // Thinking's own check takes every address it returns.
        _ = ChatCompletionsSetup.BaseUri(expected);
    }

    [Theory]
    [InlineData("", "Enter the address")]
    [InlineData("192.168.1.20:1234", "isn't this computer")]
    [InlineData("gpu-box.local:8080", "isn't this computer")]
    [InlineData("https://api.example.com/v1", "isn't this computer")]
    [InlineData("localhost", "Add the app's port")]
    [InlineData("http://localhost:1234/v1?x=1", "Leave out")]
    [InlineData("http://user:secret@localhost:1234/v1", "Leave out")]
    [InlineData("ftp://localhost:21", "isn't an address")]
    public void Other_addresses_are_refused_in_words(string typed, string reason)
    {
        Assert.Null(LocalModelServers.Normalize(typed, out var problem));
        Assert.Contains(reason, problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Names_come_only_from_a_port_no_other_known_app_uses()
    {
        Assert.Equal("LM Studio", LocalModelServers.Name("http://127.0.0.1:1234/v1"));
        Assert.Equal("Jan", LocalModelServers.Name("http://127.0.0.1:1337/v1"));
        Assert.Equal("Docker Model Runner", LocalModelServers.Name("http://127.0.0.1:12434/engines/v1"));
        Assert.Equal("Lemonade Server", LocalModelServers.Name("http://127.0.0.1:8000/api/v1"));
        // llama.cpp, LocalAI and others share 8080, and vLLM and NVIDIA's servers 8000: only the model list tells them apart.
        Assert.Equal("the model app on port 8080", LocalModelServers.Name("http://127.0.0.1:8080/v1"));
        Assert.Equal("the model app on port 8000", LocalModelServers.Name("http://127.0.0.1:8000/v1"));
        Assert.Equal("the model app on port 9999", LocalModelServers.Name("http://127.0.0.1:9999/v1"));
        Assert.Null(LocalModelServers.AppAt("https://openrouter.ai/api/v1"));
        Assert.True(LocalModelServers.IsOnThisComputer("http://127.0.0.1:1234/v1"));
        Assert.False(LocalModelServers.IsOnThisComputer("https://openrouter.ai/api/v1"));
        Assert.All(LocalModelServers.Apps, app =>
        {
            Assert.True(IPAddress.IsLoopback(IPAddress.Parse(new Uri(app.BaseUrl).Host)));
            _ = ChatCompletionsSetup.BaseUri(app.BaseUrl);
            Assert.False(string.IsNullOrWhiteSpace(app.HowToStart));
        });
    }

    [Fact]
    public void Parses_Ollama_and_OpenAI_model_lists()
    {
        Assert.Equal(["gemma4:e4b", "qwen3-vl:8b"],
            LocalModelServers.ParseModels("""{"models":[{"name":"gemma4:e4b"},{"name":"qwen3-vl:8b"}]}""", ollamaTags: true));
        Assert.Equal(["ai/smollm2"], LocalModelServers.ParseModels("""{"object":"list","data":[{"id":"ai/smollm2"}]}""", ollamaTags: false));
        Assert.Null(LocalModelServers.ParseModels("<html>not a model server</html>", ollamaTags: false));
        Assert.Null(LocalModelServers.ParseModels("""{"error":"nope"}""", ollamaTags: true));

        // Ollama says each model's download size in bytes; an OpenAI-style list doesn't.
        var sizes = LocalModelServers.ParseSizes("""{"models":[{"name":"llama3.3:70b","size":42520413916},{"name":"gemma4:e2b"}]}""", ollamaTags: true);
        Assert.Equal(42.52, Assert.Single(sizes, s => s.Key == "llama3.3:70b").Value);
        Assert.False(sizes.ContainsKey("gemma4:e2b"));
        Assert.Empty(LocalModelServers.ParseSizes("""{"data":[{"id":"qwen3-32b","size":1000}]}""", ollamaTags: false));
    }

    [Fact]
    public async Task Finds_every_app_that_answers_and_tells_apps_on_a_shared_port_apart()
    {
        var asked = new List<string>();
        var handler = new Stub(request =>
        {
            lock (asked) asked.Add(request.RequestUri!.AbsoluteUri);
            Assert.True(IPAddress.IsLoopback(IPAddress.Parse(request.RequestUri!.Host)));
            return request.RequestUri!.AbsoluteUri switch
            {
                "http://127.0.0.1:11434/api/tags" => Json("""{"models":[{"name":"gemma4:e4b","size":9608350718}]}"""),
                "http://127.0.0.1:1234/v1/models" => Json("""{"data":[{"id":"qwen/qwen3-8b","owned_by":"organization_owner"},{"id":"bad name@q4"}]}"""),
                "http://127.0.0.1:8080/v1/models" => Json("""{"object":"list","data":[{"id":"gemma-3-4b-it-q4_k_m.gguf","owned_by":"llamacpp"}]}"""),
                // vLLM started with --api-key.
                "http://127.0.0.1:8000/v1/models" => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("""{"error":"Unauthorized"}""") },
                // A program that isn't a model app (AirPlay on a Mac answers 403 with no body).
                "http://127.0.0.1:5000/v1/models" => new HttpResponseMessage(HttpStatusCode.Forbidden),
                "http://127.0.0.1:4000/v1/models" => Json("<html>dashboard</html>"),
                _ => throw new HttpRequestException("connection refused")
            };
        });
        var servers = await LocalModelServers.DetectAsync(handler, cancellationToken: CancellationToken.None);

        // A server that wants a key can't say which app it is, so it is named by its port.
        Assert.Equal(["ollama", "lm-studio", "llama-cpp", "port-8000"], servers.Select(s => s.Id));
        Assert.Equal("http://127.0.0.1:11434/v1", servers[0].ChatCompletionsBaseUrl);
        Assert.Equal(["gemma4:e4b"], servers[0].Models);
        Assert.Equal(9.61, servers[0].SizesGb["gemma4:e4b"]);
        Assert.Empty(servers[1].SizesGb);
        Assert.Equal(["qwen/qwen3-8b"], servers[1].Models);
        Assert.Equal(["bad name@q4"], servers[1].Unusable);
        Assert.Equal("llama.cpp server", servers[2].Name);
        Assert.Contains("--jinja", servers[2].HowToStart, StringComparison.Ordinal);
        Assert.True(servers[3].NeedsKey);
        Assert.Equal("Model app on port 8000", servers[3].Name);
        Assert.Empty(servers[3].Models);
        Assert.All(LocalModelServers.Apps.Where(a => !a.OllamaTags), app => Assert.Contains(app.BaseUrl + "/models", asked));
    }

    [Fact]
    public async Task An_unknown_app_on_a_shared_port_is_named_by_its_port()
    {
        var handler = new Stub(request => request.RequestUri!.Port == 8080
            ? Json("""{"object":"list","data":[{"id":"phi-4","object":"model"}]}""")
            : throw new HttpRequestException("connection refused"));
        var server = Assert.Single(await LocalModelServers.DetectAsync(handler, cancellationToken: CancellationToken.None));
        Assert.Equal("Model app on port 8080", server.Name);
        Assert.Equal("port-8080", server.Id);
        Assert.Null(server.HowToStart);
    }

    [Fact]
    public async Task Asking_one_address_says_why_it_cant_be_used()
    {
        var handler = new Stub(request => request.RequestUri!.Port switch
        {
            1234 => Json("""{"data":[{"id":"qwen/qwen3-8b"}]}"""),
            8000 => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("""{"error":{"message":"bad key"}}""") },
            3000 => Json("<html></html>"),
            _ => throw new HttpRequestException("connection refused")
        });
        var models = await LocalModelServers.AskAsync("http://127.0.0.1:1234/v1", null, handler);
        Assert.Equal(LocalServerAnswerKind.Models, models.Kind);
        Assert.Equal(["qwen/qwen3-8b"], models.Models);
        var key = await LocalModelServers.AskAsync("http://127.0.0.1:8000/v1", "wrong", handler);
        Assert.Equal(LocalServerAnswerKind.NeedsKey, key.Kind);
        Assert.Contains("refused this key", key.Problem, StringComparison.Ordinal);
        Assert.Equal(LocalServerAnswerKind.NotAModelServer, (await LocalModelServers.AskAsync("http://127.0.0.1:3000/v1", null, handler)).Kind);
        var none = await LocalModelServers.AskAsync("http://127.0.0.1:9/v1", null, handler);
        Assert.Equal(LocalServerAnswerKind.NoAnswer, none.Kind);
        Assert.Contains("Start the app's server", none.Problem, StringComparison.Ordinal);
        // Never anything off this computer.
        Assert.Equal(LocalServerAnswerKind.NoAnswer, (await LocalModelServers.AskAsync("https://openrouter.ai/api/v1", null, handler)).Kind);
    }

    [Fact]
    public async Task The_key_goes_only_as_a_bearer_token_to_the_address_asked()
    {
        string? authorization = null;
        var handler = new Stub(request =>
        {
            authorization = request.Headers.Authorization?.ToString();
            return Json("""{"data":[{"id":"m"}]}""");
        });
        await LocalModelServers.AskAsync("http://127.0.0.1:8000/v1", "sk-local", handler);
        Assert.Equal("Bearer sk-local", authorization);
        await LocalModelServers.AskAsync("http://127.0.0.1:8000/v1", null, handler);
        Assert.Null(authorization);
    }

    [Fact]
    public async Task Testing_streams_a_reply_like_a_reply_does_and_offers_a_tool()
    {
        string? sent = null;
        var handler = new Stub(request =>
        {
            if (request.Method == HttpMethod.Get) return Json("""{"data":[{"id":"qwen/qwen3-8b"}]}""");
            sent = request.Content!.ReadAsStringAsync().Result;
            return Stream("""{"choices":[{"delta":{"reasoning_content":"hmm"}}]}""", """{"choices":[{"delta":{"content":"Hello there!"}}]}""",
                """{"choices":[{"delta":{},"finish_reason":"stop"}]}""", "[DONE]");
        });
        var lines = new List<string>();
        var result = await LocalModelServers.TestAsync("http://127.0.0.1:1234/v1", "qwen/qwen3-8b", null, 4096, ReasoningControl.ChatTemplate,
            false, TimeSpan.FromMinutes(2), new Collect(lines), handler);

        Assert.False(result.Warning);
        Assert.False(result.ToolsRejected);
        Assert.StartsWith("qwen/qwen3-8b works in LM Studio on this PC.", result.Summary, StringComparison.Ordinal);
        Assert.Contains(lines, l => l == "LM Studio lists qwen/qwen3-8b.");
        Assert.Contains(lines, l => l == "Reply: Hello there!");
        using var request = JsonDocument.Parse(sent!);
        var root = request.RootElement;
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.Equal(4096, root.GetProperty("max_tokens").GetInt32());
        Assert.Equal("get_time", root.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString());
        Assert.False(root.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
        Assert.Equal(LocalModelServers.TestPrompt, root.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task An_app_that_refuses_tools_is_asked_again_without_them_and_the_result_says_how_to_fix_it()
    {
        var requests = new List<string>();
        var handler = new Stub(request =>
        {
            if (request.Method == HttpMethod.Get) return Json("""{"data":[{"id":"gemma","owned_by":"llamacpp"}]}""");
            var body = request.Content!.ReadAsStringAsync().Result;
            lock (requests) requests.Add(body);
            return body.Contains("\"tools\"", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                    { Content = new StringContent("""{"error":{"code":500,"message":"tools param requires --jinja flag","type":"server_error"}}""") }
                : Stream("""{"choices":[{"delta":{"content":"Hi!"},"finish_reason":"stop"}]}""", "[DONE]");
        });
        var lines = new List<string>();
        var result = await LocalModelServers.TestAsync("http://127.0.0.1:8080/v1", "gemma", null, null, ReasoningControl.ChatTemplate,
            null, TimeSpan.FromMinutes(2), new Collect(lines), handler);

        Assert.Equal(2, requests.Count);
        Assert.True(result.ToolsRejected);
        Assert.True(result.Warning);
        Assert.Contains("doesn't take tools", result.Summary, StringComparison.Ordinal);
        Assert.Contains("--jinja", result.Summary, StringComparison.Ordinal);
        Assert.Contains(lines, l => l.Contains("tools param requires --jinja flag", StringComparison.Ordinal));
        Assert.DoesNotContain("\"tools\"", requests[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_model_that_calls_the_tool_works_and_a_server_error_unrelated_to_tools_is_not_retried()
    {
        var calls = new Stub(request => request.Method == HttpMethod.Get
            ? Json("""{"data":[{"id":"m"}]}""")
            : Stream("""{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"c1","function":{"name":"get_time","arguments":"{}"}}]}}]}""",
                """{"choices":[{"delta":{},"finish_reason":"tool_calls"}]}""", "[DONE]"));
        var lines = new List<string>();
        var result = await LocalModelServers.TestAsync("http://127.0.0.1:1337/v1", "m", null, null, ReasoningControl.ChatTemplate, null,
            TimeSpan.FromMinutes(2), new Collect(lines), calls);
        Assert.False(result.Warning);
        Assert.Contains(lines, l => l.Contains("can use tools", StringComparison.Ordinal));

        var posts = 0;
        var broken = new Stub(request =>
        {
            if (request.Method == HttpMethod.Get) return Json("""{"data":[{"id":"m"}]}""");
            Interlocked.Increment(ref posts);
            return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("""{"error":"out of memory"}""") };
        });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => LocalModelServers.TestAsync("http://127.0.0.1:1337/v1", "m", null,
            null, ReasoningControl.ChatTemplate, null, TimeSpan.FromMinutes(2), new Collect([]), broken));
        Assert.Equal(1, posts);
        Assert.Equal("m didn't answer in Jan (error 500): out of memory.", error.Message);
    }

    [Theory]
    [InlineData(1, "Nothing answers at http://127.0.0.1:1234/v1. Start LM Studio's server, then test again.")]
    [InlineData(2, "LM Studio asks for an API key. Enter the key you set in the app, then test again.")]
    [InlineData(3, "qwen used its whole reply budget (64 tokens) before finishing, thinking before it answered. Raise or clear Max reply length in Companion › Replies, or choose a chat model.")]
    [InlineData(4, "qwen answered with nothing in LM Studio.")]
    [InlineData(5, "qwen's answer was cut off: LM Studio ended the reply early.")]
    public async Task Whatever_stops_a_reply_fails_the_test_in_words(int scenario, string message)
    {
        var handler = new Stub(request =>
        {
            if (scenario == 1) throw new HttpRequestException("connection refused");
            if (request.Method == HttpMethod.Get)
                return scenario == 2
                    ? new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("""{"error":"no key"}""") }
                    : Json("""{"data":[{"id":"qwen"}]}""");
            return scenario switch
            {
                3 => Stream("""{"choices":[{"delta":{"reasoning_content":"..."}}]}""", """{"choices":[{"delta":{},"finish_reason":"length"}]}""", "[DONE]"),
                4 => Stream("""{"choices":[{"delta":{},"finish_reason":"stop"}]}""", "[DONE]"),
                _ => Stream("""{"choices":[{"delta":{"content":"Hel"}}]}""")
            };
        });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => LocalModelServers.TestAsync("http://127.0.0.1:1234/v1", "qwen",
            null, scenario == 3 ? 64 : null, ReasoningControl.ChatTemplate, null, TimeSpan.FromMinutes(2), new Collect([]), handler));
        Assert.Equal(message, error.Message);
    }

    [Fact]
    public async Task A_slow_first_word_passes_with_a_warning()
    {
        var handler = new Stub(request => request.Method == HttpMethod.Get
            ? Json("""{"data":[{"id":"big"}]}""")
            : Stream("""{"choices":[{"delta":{"content":"Hi"},"finish_reason":"stop"}]}""", "[DONE]"));
        var result = await LocalModelServers.TestAsync("http://127.0.0.1:1234/v1", "big", null, null, ReasoningControl.ChatTemplate, null,
            TimeSpan.Zero, new Collect([]), handler);
        Assert.True(result.Warning);
        Assert.Contains("starts too slowly", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Testing_refuses_anything_off_this_computer()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => LocalModelServers.TestAsync("https://openrouter.ai/api/v1", "m",
            null, null, ReasoningControl.OpenRouter, null, TimeSpan.FromMinutes(2), new Collect([]),
            new Stub(_ => throw new InvalidOperationException("asked"))));
        Assert.Equal("Martlet tests only model apps on this computer.", error.Message);
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Stream(params string[] events) =>
        new(HttpStatusCode.OK) { Content = new StringContent(string.Concat(events.Select(e => $"data: {e}\n\n")), Encoding.UTF8, "text/event-stream") };

    private sealed class Collect(List<string> lines) : IProgress<string>
    {
        public void Report(string value) { lock (lines) lines.Add(value); }
    }

    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(answer(request));
    }
}
