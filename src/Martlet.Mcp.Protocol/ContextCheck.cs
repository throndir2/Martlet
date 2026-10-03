using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Mcp;

/// <summary>context_check: the Thinking model's context as Martlet uses it, from a data directory (the saved route, Companion ›
/// Replies › Context size and model-limits.json, through the production <see cref="ContextBudget"/>), then two rehearsals that
/// stay on 127.0.0.1: the production <see cref="ModelContextProbe"/> against fixture servers shaped like OpenRouter, vLLM, Groq,
/// llama.cpp and Ollama (NOT the real services; a fixture key, never a real one), and the production history fit
/// (<see cref="BoundedTextInput.HistoryStart"/>) of a long synthetic conversation into each route's context.</summary>
internal static class ContextCheck
{
    private const string FixtureKey = "fixture-key-not-real";

    internal static async Task<object> RunAsync(string dataDirectory, CancellationToken cancellation)
    {
        var loaded = await new SettingsStore(dataDirectory).LoadAsync(cancellation);
        var thinking = loaded.Settings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Llm);
        var generation = loaded.Settings?.Generation;
        var limits = ModelLimits.Load(dataDirectory);
        var budget = thinking is null ? null : ContextBudget.For(thinking, generation, limits);
        var found = thinking is null ? null : limits.Find(thinking.Origin, thinking.ModelId);
        return new
        {
            settings = loaded.State switch { SettingsLoadState.Loaded => "loaded", SettingsLoadState.FirstRun => "none", _ => "unreadable" },
            thinking = thinking is null ? null : new
            {
                routeType = thinking.RouteType?.ToString() ?? "OpenAi",
                localOllama = ContextBudget.IsLocalOllama(thinking.RouteType, thinking.Origin),
                model = thinking.ModelId
            },
            savedContextTokens = generation?.ContextTokens,
            modelLimit = found is null ? null : new
            {
                found.ContextTokens, found.ModelMaximum, found.Source, checkedAt = found.CheckedAt.ToString("O")
            },
            modelLimitsKept = limits.Models.Count,
            context = budget is null ? null : new
            {
                budget.Tokens, source = budget.Source.ToString(), budget.ReplyTokens, budget.InputTokens, budget.ModelTokens,
                described = budget.Describe()
            },
            probe = await ProbeAsync(cancellation),
            fit = Fit()
        };
    }

    private static async Task<object> ProbeAsync(CancellationToken cancellation)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var origin = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
        var seen = new List<(string Path, bool Key)>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var serving = ServeAsync(listener, seen, stop.Token);
        try
        {
            using var client = ModelContextProbe.CreateClient(loopback: true);
            async Task<object> Chat(string base_, string model, string? key, int? expected)
            {
                var report = await ModelContextProbe.ChatCompletionsAsync(client, origin + base_, model, key, "the fixture", cancellation);
                return new { model, report.ContextTokens, report.Source, report.Reached, report.Summary, ok = report.ContextTokens == expected };
            }
            var openRouter = await Chat("/openrouter/v1", "google/gemma-4-26b-a4b-it", FixtureKey, 131_072);
            var keySent = Seen(seen, "/openrouter/v1/models")?.Key == true;
            var vllm = await Chat("/vllm/v1", "Qwen/Qwen3-8B", null, 40_960);
            var groq = await Chat("/groq/v1", "llama-3.3-70b-versatile", null, 131_072);
            var llamaCpp = await Chat("/llamacpp/v1", "gemma-3-4b-it-q4.gguf", null, 131_072);
            var unlisted = await Chat("/openrouter/v1", "nobody/unknown-model", null, null);
            var redirected = await Chat("/redirect/v1", "google/gemma-4-26b-a4b-it", FixtureKey, null);
            var redirectFollowed = Seen(seen, "/followed/v1/models") is not null;
            var ollamaOrigin = new Uri(origin + "/ollama/");
            var loadedModel = await ModelContextProbe.OllamaAsync(client, ollamaOrigin, "gemma4:e4b", load: false, cancellation);
            var notLoaded = await ModelContextProbe.OllamaAsync(client, ollamaOrigin, "qwen3:8b", load: false, cancellation);
            var loadedOnCheck = await ModelContextProbe.OllamaAsync(client, ollamaOrigin, "qwen3:8b", load: true, cancellation);
            var missing = await ModelContextProbe.OllamaAsync(client, ollamaOrigin, "missing:latest", load: false, cancellation);
            var ok = Ok(openRouter) && keySent && Ok(vllm) && Ok(groq) && Ok(llamaCpp) && Ok(unlisted) &&
                Ok(redirected) && !redirectFollowed &&
                loadedModel is { ContextTokens: 32_768, ModelMaximum: 131_072 } &&
                notLoaded is { ContextTokens: null, ModelMaximum: 40_960 } &&
                loadedOnCheck.ContextTokens == 4_096 && missing is { ContextTokens: null, Reached: true };
            return new
            {
                ok,
                note = "Fixture servers on 127.0.0.1 shaped like each service's model list (NOT the real services).",
                openRouter, keySentToItsOwnBaseUrl = keySent, vllm, groq, llamaCpp, unlisted,
                redirect = new { redirected, followed = redirectFollowed },
                ollama = new
                {
                    loaded = Report(loadedModel), notLoaded = Report(notLoaded), loadedOnCheck = Report(loadedOnCheck), missing = Report(missing)
                },
                requests = seen.Count
            };
        }
        finally
        {
            stop.Cancel();
            listener.Stop();
            try { await serving; } catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException or IOException) { }
        }
    }

    private static bool Ok(object result) => (bool)result.GetType().GetProperty("ok")!.GetValue(result)!;

    private static object Report(ModelContextReport report) =>
        new { report.ContextTokens, report.ModelMaximum, report.Reached, report.Summary };

    private static (string Path, bool Key)? Seen(List<(string Path, bool Key)> seen, string path)
    {
        lock (seen) return seen.Any(s => s.Path == path) ? seen.Last(s => s.Path == path) : null;
    }

    /// <summary>A long synthetic conversation (1,000 exchanges, about 900 KB; NOT anything said) fitted the way a reply fits it,
    /// for each kind of route: a cloud model at Martlet's default and at 1,000,000 tokens, a paired host and Ollama on this PC.</summary>
    private static object Fit()
    {
        var history = new List<TextHistoryMessage>();
        for (var i = 0; i < 1_000; i++)
        {
            history.Add(new(TextHistoryRole.User, $"Exchange {i}: " + new string('u', 400)));
            history.Add(new(TextHistoryRole.Assistant, $"Reply {i}: " + new string('a', 480)));
        }
        var prompt = new BoundedTextInput("And what about now?", "You are Martlet, a friendly companion. " + new string('p', 2_000));
        object Route(string name, ContextBudget budget, int maxBytes, int maxMessages)
        {
            var start = BoundedTextInput.HistoryStart(prompt, history, maxBytes, budget.InputTokens, budget.InputTokens, maxMessages);
            if (start is not { } first) return new { name, budget.Tokens, budget.InputTokens, fits = false, ok = false };
            var sent = history.Skip(first).ToArray();
            var input = new BoundedTextInput(prompt.UserText, prompt.Personality, sent);
            var more = first >= 2 ? new BoundedTextInput(prompt.UserText, prompt.Personality, history.Skip(first - 2).ToArray()) : null;
            // The newest exchanges are sent, within the context, and one more (older) exchange would not have fit.
            var ok = input.InputTokenReservation <= budget.InputTokens && input.Utf8Bytes <= maxBytes && sent.Length <= maxMessages &&
                (more is null || more.InputTokenReservation > budget.InputTokens || more.Utf8Bytes > maxBytes || sent.Length + 2 > maxMessages);
            return new
            {
                name, budget.Tokens, budget.InputTokens, exchangesSent = sent.Length / 2, exchangesLeftOut = first / 2,
                estimatedTokens = input.InputTokenReservation, bytes = input.Utf8Bytes, ok
            };
        }
        var cloud = ContextBudget.For(SetupRouteType.ChatCompletions, ChatCompletionsEndpointCatalog.OpenRouterBaseUrl, null, 262_144);
        var million = ContextBudget.For(SetupRouteType.OpenAi, null, new() { ContextTokens = 1_000_000 }, ModelContextCatalog.Gpt41ContextTokens);
        var host = ContextBudget.For(SetupRouteType.GatewayOllama, null, null, null);
        var local = ContextBudget.For(SetupRouteType.ChatCompletions, GenerationSupport.LocalOllamaChatBaseUrl, null, 32_768);
        var results = new[]
        {
            Route("cloud, Martlet's default", cloud, BoundedTextInput.HardMaxInputUtf8Bytes, BoundedTextInput.HardMaxHistoryMessages),
            Route("OpenAI gpt-4.1 at 1,000,000", million, BoundedTextInput.HardMaxInputUtf8Bytes, BoundedTextInput.HardMaxHistoryMessages),
            Route("paired host's Ollama", host, BoundedTextInput.HardMaxUtf8Bytes, TextGenerationLimits.DefaultMaxHistoryMessages),
            Route("Ollama on this PC at 32,768", local, BoundedTextInput.HardMaxInputUtf8Bytes, BoundedTextInput.HardMaxHistoryMessages)
        };
        return new { ok = results.All(Ok), exchanges = history.Count / 2, routes = results };
    }

    // A minimal HTTP/1.1 server: each path answers like the service it stands in for.
    private static async Task ServeAsync(TcpListener listener, List<(string Path, bool Key)> seen, CancellationToken cancellation)
    {
        var loaded = new HashSet<string> { "gemma4:e4b" };
        while (!cancellation.IsCancellationRequested)
        {
            using var client = await listener.AcceptTcpClientAsync(cancellation);
            await using var stream = client.GetStream();
            var (method, path, key, body) = await ReadAsync(stream, cancellation);
            lock (seen) seen.Add((path, key));
            var (status, json) = Answer(method, path, body, loaded);
            var payload = Encoding.UTF8.GetBytes(json);
            var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: application/json\r\n" +
                (status.StartsWith("302", StringComparison.Ordinal) ? "Location: /followed/v1/models\r\n" : "") +
                $"Content-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(head, cancellation);
            await stream.WriteAsync(payload, cancellation);
            await stream.FlushAsync(cancellation);
        }
    }

    private static (string Status, string Json) Answer(string method, string path, string body, HashSet<string> loaded)
    {
        const string ok = "200 OK";
        switch (path)
        {
            case "/openrouter/v1/models":
                return (ok, """{"data":[{"id":"google/gemma-4-26b-a4b-it","context_length":262144,"top_provider":{"context_length":131072,"max_completion_tokens":8192}},{"id":"openai/gpt-4.1","context_length":1047576}]}""");
            case "/vllm/v1/models":
                return (ok, """{"object":"list","data":[{"id":"Qwen/Qwen3-8B","object":"model","owned_by":"vllm","max_model_len":40960}]}""");
            case "/groq/v1/models":
                return (ok, """{"object":"list","data":[{"id":"llama-3.3-70b-versatile","object":"model","context_window":131072}]}""");
            case "/llamacpp/v1/models":
                return (ok, """{"object":"list","data":[{"id":"gemma-3-4b-it-q4.gguf","object":"model","meta":{"n_ctx_train":131072,"n_vocab":262144}}]}""");
            case "/redirect/v1/models":
                return ("302 Found", "{}");
            case "/ollama/api/show":
                using (var request = JsonDocument.Parse(body))
                {
                    var model = request.RootElement.GetProperty("model").GetString();
                    return model switch
                    {
                        "gemma4:e4b" => (ok, """{"capabilities":["completion","vision"],"model_info":{"general.architecture":"gemma3","gemma3.context_length":131072},"parameters":"stop \"<end_of_turn>\""}"""),
                        "qwen3:8b" => (ok, """{"capabilities":["completion","tools"],"model_info":{"general.architecture":"qwen3","qwen3.context_length":40960},"parameters":"temperature 0.6"}"""),
                        _ => ("404 Not Found", """{"error":"model 'missing' not found"}""")
                    };
                }
            case "/ollama/api/generate":
                using (var request = JsonDocument.Parse(body))
                    lock (loaded) loaded.Add(request.RootElement.GetProperty("model").GetString() ?? "");
                return (ok, """{"model":"qwen3:8b","done":true,"done_reason":"load"}""");
            case "/ollama/api/ps":
                lock (loaded)
                    return (ok, "{\"models\":[" + string.Join(",", loaded.Select(m =>
                        $"{{\"name\":\"{m}\",\"model\":\"{m}\",\"size\":1,\"size_vram\":1,\"context_length\":{(m == "gemma4:e4b" ? 32768 : 4096)}}}")) + "]}");
            default:
                return ("404 Not Found", """{"error":"not found"}""");
        }
    }

    private static async Task<(string Method, string Path, bool Key, string Body)> ReadAsync(NetworkStream stream, CancellationToken cancellation)
    {
        var buffer = new MemoryStream();
        var one = new byte[8192];
        int headerEnd;
        while ((headerEnd = buffer.GetBuffer().AsSpan(0, (int)buffer.Length).IndexOf("\r\n\r\n"u8)) < 0)
        {
            var read = await stream.ReadAsync(one, cancellation);
            if (read == 0) throw new IOException("The request ended early.");
            buffer.Write(one, 0, read);
        }
        var lines = Encoding.ASCII.GetString(buffer.GetBuffer(), 0, headerEnd).Split("\r\n");
        var start = lines[0].Split(' ');
        var length = lines.Select(h => h.Split(':', 2)).Where(h => h.Length == 2 &&
            h[0].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase)).Select(h => int.Parse(h[1].Trim())).FirstOrDefault(0);
        var key = lines.Any(h => h.StartsWith("Authorization: Bearer " + FixtureKey, StringComparison.OrdinalIgnoreCase));
        var body = new MemoryStream();
        body.Write(buffer.GetBuffer(), headerEnd + 4, (int)buffer.Length - headerEnd - 4);
        while (body.Length < length)
        {
            var read = await stream.ReadAsync(one, cancellation);
            if (read == 0) break;
            body.Write(one, 0, read);
        }
        return (start[0], start.Length > 1 ? start[1] : "/", key, Encoding.UTF8.GetString(body.ToArray()));
    }
}
