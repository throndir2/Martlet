using System.IO;
using System.Text;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Mcp;

/// <summary>local_model_servers: the model apps Martlet looks for on this PC (Companion › Thinking › This PC › A model app you
/// already use), which of them answer now on 127.0.0.1 with their models (the production LocalModelServers.DetectAsync), what
/// an address typed for one turns into and what it says (address), and with test=true the production Test model request to
/// one model there (a short streamed reply with a tool offered, as replies ask). With fixture=true it rehearses the same code
/// against fixture servers on 127.0.0.1 shaped like llama.cpp (refuses tools without --jinja) and an app that asks for a key
/// (canned words, NOT AI). Everything asks this PC's loopback only; reads no credentials; saves nothing.</summary>
internal static class LocalModelServersCheck
{
    internal static async Task<object> RunAsync(string? address, string? model, bool test, bool fixture,
        CancellationToken cancellation)
    {
        var found = await LocalModelServers.DetectAsync(cancellationToken: cancellation);
        object? typed = null;
        string? baseUrl = null;
        if (address is not null)
        {
            baseUrl = LocalModelServers.Normalize(address, out var problem);
            var answer = baseUrl is null ? null : await LocalModelServers.AskAsync(baseUrl, null, cancellationToken: cancellation);
            typed = new
            {
                typed = address, baseUrl, problem = baseUrl is null ? problem : null,
                name = baseUrl is null ? null : LocalModelServers.Name(baseUrl),
                answer = answer is null ? null : new { kind = answer.Kind.ToString(), answer.Models, answer.Unusable, answer.OwnedBy, answer.Problem }
            };
        }
        object? tested = null;
        if (test)
        {
            var target = baseUrl ?? found.FirstOrDefault(s => s.Id != "ollama" && (model is null || s.Models.Contains(model)))?.ChatCompletionsBaseUrl;
            var chosen = model ?? found.FirstOrDefault(s => s.ChatCompletionsBaseUrl == target)?.Models.FirstOrDefault();
            tested = target is null || chosen is null
                ? new { error = "test needs an address or a found model app other than Ollama, and a model." }
                : await TestAsync(target, chosen, cancellation);
        }
        return new
        {
            apps = LocalModelServers.Apps.Select(a => new { a.Id, a.Name, a.BaseUrl, a.OwnedBy, a.HowToStart }).ToArray(),
            found = found.Select(s => new { s.Id, s.Name, baseUrl = s.ChatCompletionsBaseUrl, s.Models, s.Unusable, s.NeedsKey, s.HowToStart }).ToArray(),
            address = typed,
            test = tested,
            fixture = fixture ? await FixtureAsync(cancellation) : null
        };
    }

    private static async Task<object> TestAsync(string baseUrl, string model, CancellationToken cancellation)
    {
        var lines = new List<string>();
        var output = new Collect(lines);
        try
        {
            // As Companion › Thinking's Test model sends it with the default Replies settings.
            var result = await LocalModelServers.TestAsync(baseUrl, model, null,
                GenerationSupport.ReplyTokens(SetupRouteType.ChatCompletions, null), GenerationSupport.ChatReasoning(baseUrl),
                GenerationSettings.ThinkingSteps(null), TimeSpan.FromMinutes(2), output, cancellationToken: cancellation);
            return new
            {
                baseUrl, model, passed = true, result.Summary, result.Warning, result.ToolsRejected,
                firstWordsMs = (int)result.FirstWords.TotalMilliseconds, totalMs = (int)result.Total.TotalMilliseconds, output = lines
            };
        }
        catch (InvalidOperationException error)
        {
            return new { baseUrl, model, passed = false, error = error.Message, output = lines };
        }
    }

    private static async Task<object> FixtureAsync(CancellationToken cancellation)
    {
        // llama.cpp started without --jinja: lists its model as llamacpp's and refuses a request with tools.
        await using var llama = new Fixture(request => (request.Method, request.Path) switch
        {
            ("GET", "/v1/models") => (200, "application/json", """{"object":"list","data":[{"id":"gemma-3-4b-it","owned_by":"llamacpp"}]}"""),
            ("POST", "/v1/chat/completions") when request.Body.Contains("\"tools\"", StringComparison.Ordinal) =>
                (500, "application/json", """{"error":{"code":500,"message":"tools param requires --jinja flag","type":"server_error"}}"""),
            ("POST", "/v1/chat/completions") => (200, "text/event-stream",
                "data: {\"choices\":[{\"delta\":{\"content\":\"Hello from the fixture!\"}}]}\n\n" +
                "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n"),
            _ => (404, "application/json", """{"error":"not found"}""")
        });
        // An app started with an API key (vLLM's --api-key, LM Studio's authentication).
        await using var keyed = new Fixture(request => request.Authorization == "Bearer fixture-key"
            ? (200, "application/json", """{"data":[{"id":"qwen/qwen3-8b","owned_by":"vllm"}]}""")
            : (401, "application/json", """{"error":"Unauthorized"}"""));

        var llamaAddress = $"localhost:{llama.Port}";
        var llamaUrl = LocalModelServers.Normalize(llamaAddress, out _);
        var llamaAnswer = await LocalModelServers.AskAsync(llamaUrl!, null, cancellationToken: cancellation);
        var lines = new List<string>();
        LocalServerTestResult? result = null;
        string? failure = null;
        try
        {
            result = await LocalModelServers.TestAsync(llamaUrl!, "gemma-3-4b-it", null, 4096, ReasoningControl.ChatTemplate, false,
                TimeSpan.FromMinutes(2), new Collect(lines), cancellationToken: cancellation);
        }
        catch (InvalidOperationException error) { failure = error.Message; }
        var posts = llama.Requests.Where(r => r.Method == "POST").Select(r => r.Body).ToList();

        var keyedUrl = $"http://127.0.0.1:{keyed.Port}/v1";
        var noKey = await LocalModelServers.AskAsync(keyedUrl, null, cancellationToken: cancellation);
        var withKey = await LocalModelServers.AskAsync(keyedUrl, "fixture-key", cancellationToken: cancellation);
        var offComputer = LocalModelServers.Normalize("192.168.1.20:1234", out var offProblem);

        var ok = llamaUrl == $"http://127.0.0.1:{llama.Port}/v1" && llamaAnswer.Kind == LocalServerAnswerKind.Models &&
            llamaAnswer.OwnedBy == "llamacpp" && result is { ToolsRejected: true, Warning: true } && posts.Count == 2 &&
            posts[0].Contains("\"tools\"", StringComparison.Ordinal) && !posts[1].Contains("\"tools\"", StringComparison.Ordinal) &&
            noKey.Kind == LocalServerAnswerKind.NeedsKey && withKey.Kind == LocalServerAnswerKind.Models && offComputer is null;
        return new
        {
            ok,
            normalized = new { typed = llamaAddress, baseUrl = llamaUrl },
            llamaCpp = new
            {
                answer = new { kind = llamaAnswer.Kind.ToString(), llamaAnswer.Models, llamaAnswer.OwnedBy },
                test = result is null ? null : new { result.Summary, result.Warning, result.ToolsRejected },
                failure, requests = posts.Count, firstHadTools = posts.FirstOrDefault()?.Contains("\"tools\"", StringComparison.Ordinal),
                output = lines
            },
            keyed = new
            {
                withoutKey = new { kind = noKey.Kind.ToString(), noKey.Problem },
                withKey = new { kind = withKey.Kind.ToString(), withKey.Models }
            },
            offComputer = new { typed = "192.168.1.20:1234", baseUrl = offComputer, problem = offProblem }
        };
    }

    private sealed class Collect(List<string> lines) : IProgress<string>
    {
        public void Report(string value) { lock (lines) lines.Add(value); }
    }

    internal sealed record Request(string Method, string Path, string? Authorization, string Body);

    /// <summary>An HTTP/1.1 server on 127.0.0.1 (a free port) that answers each request with the canned status, type and body.</summary>
    internal sealed class Fixture : IAsyncDisposable
    {
        private readonly LoopbackHttpServer server;
        internal readonly List<Request> Requests = [];

        internal Fixture(Func<Request, (int Status, string Type, string Body)> answer) => server = new(exchange =>
        {
            var request = new Request(exchange.Request.Method, exchange.Request.Target, exchange.Request.Header("Authorization"),
                exchange.Request.Text);
            lock (Requests) Requests.Add(request);
            var (status, type, body) = answer(request);
            return exchange.RespondAsync(status, body, type);
        });

        internal int Port => server.Port;

        public ValueTask DisposeAsync() => server.DisposeAsync();
    }
}
