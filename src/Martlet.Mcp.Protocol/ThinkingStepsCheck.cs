using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Mcp;

/// <summary>thinking_steps_check: Companion › Replies › Thinking steps as replies use it. From a data directory: the saved
/// choice, the Thinking route and exactly what its replies send for it (the production <see cref="GenerationSupport"/>), and
/// what each kind of route sends for Off and On. Then the production Chat Completions adapter against a fixture endpoint on
/// 127.0.0.1 (canned reply, NOT AI), checking each choice's request; and, only with <c>live</c>, the same adapter against
/// Ollama on this PC with a fixed question (never anything the owner said), the model's default against Off, beside one plain
/// request each that shows whether Ollama thought first. Nothing leaves loopback; no credentials are read.</summary>
internal static class ThinkingStepsCheck
{
    private const string Question = "Is 9.11 bigger than 9.9? Answer in one short sentence.";
    private static readonly (string Name, bool? Value)[] Choices = [("Default", null), ("Off", false), ("On", true)];

    internal static async Task<object> RunAsync(string dataDirectory, string? model, bool live, CancellationToken cancellation)
    {
        if (model is not null)
        {
            try { ChatCompletionsSetup.ModelId(model); }
            catch (ContractException error) { throw new ArgumentException(error.Message); }
        }
        var loaded = await new SettingsStore(dataDirectory).LoadAsync(cancellation);
        var thinking = loaded.Settings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Llm);
        var saved = loaded.Settings?.Generation?.Reasoning;
        var localOllama = thinking is not null && ContextBudget.IsLocalOllama(thinking.RouteType, thinking.Origin);
        var liveModel = model ?? (localOllama ? thinking!.ModelId : null);
        return new
        {
            settings = loaded.State switch { SettingsLoadState.Loaded => "loaded", SettingsLoadState.FirstRun => "none", _ => "unreadable" },
            thinkingSteps = Name(saved),
            thinking = thinking is null ? null : new
            {
                routeType = thinking.RouteType?.ToString() ?? "OpenAi",
                localOllama,
                model = thinking.ModelId,
                control = GenerationSupport.Reasoning(thinking.RouteType, thinking.Origin).ToString(),
                use = GenerationSupport.Use(thinking.RouteType, thinking.Origin, GenerationSetting.Reasoning).ToString(),
                sends = GenerationSupport.ReasoningJson(thinking.RouteType, thinking.Origin, saved)
            },
            routes = Routes(),
            fixture = await FixtureAsync(cancellation),
            live = !live ? NotRun("Pass live: true to ask Ollama on this PC (loopback only; it loads the model).")
                : liveModel is null ? NotRun("Thinking isn't Ollama on this PC; pass model to name a model Ollama has.")
                : await LiveAsync(liveModel, cancellation)
        };
    }

    private static string Name(bool? reasoning) => Choices.First(c => c.Value == reasoning).Name;

    private static object NotRun(string why) => new { ran = false, why };

    private static object[] Routes() =>
    [
        Route("Ollama on this PC", SetupRouteType.ChatCompletions, GenerationSupport.LocalOllamaChatBaseUrl),
        Route("OpenRouter", SetupRouteType.ChatCompletions, ChatCompletionsEndpointCatalog.OpenRouterBaseUrl),
        Route("NVIDIA Build", SetupRouteType.ChatCompletions, ChatCompletionsEndpointCatalog.NvidiaBuildBaseUrl),
        Route("OpenAI Chat Completions", SetupRouteType.ChatCompletions, "https://api.openai.com/v1"),
        Route("Gemini", SetupRouteType.ChatCompletions, "https://generativelanguage.googleapis.com/v1beta/openai"),
        Route("Another server (vLLM, SGLang, llama.cpp, LM Studio)", SetupRouteType.ChatCompletions, "http://127.0.0.1:8000/v1"),
        Route("A paired host's Ollama", SetupRouteType.GatewayOllama, null),
        Route("OpenAI", SetupRouteType.OpenAi, null)
    ];

    private static object Route(string name, SetupRouteType type, string? baseUrl) => new
    {
        name,
        control = GenerationSupport.Reasoning(type, baseUrl).ToString(),
        use = GenerationSupport.Use(type, baseUrl, GenerationSetting.Reasoning).ToString(),
        off = GenerationSupport.ReasoningJson(type, baseUrl, false),
        on = GenerationSupport.ReasoningJson(type, baseUrl, true)
    };

    private static async Task<object> FixtureAsync(CancellationToken cancellation)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var baseUrl = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/v1";
        var requests = new List<byte[]>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var serving = ServeAsync(listener, requests, stop.Token);
        try
        {
            using var adapter = ChatCompletionsTextGenerationAdapter.Create(baseUrl);
            var results = new List<object>();
            var ok = true;
            foreach (var (name, value) in Choices)
            {
                int before;
                lock (requests) before = requests.Count;
                var asked = await AskAsync(adapter, baseUrl, "fixture-model", value, new TextGenerationLimits(), cancellation);
                byte[]? body;
                lock (requests) body = requests.Count > before ? requests[^1] : null;
                var sent = body is null ? null : ReasoningFields(body);
                var expected = GenerationSupport.ReasoningJson(SetupRouteType.ChatCompletions, baseUrl, value);
                var match = asked.Outcome == "Completed" && sent == expected;
                ok &= match;
                results.Add(new { choice = name, asked.Outcome, sent, expected, ok = match });
            }
            return new
            {
                ok,
                endpoint = baseUrl,
                control = GenerationSupport.ChatReasoning(baseUrl).ToString(),
                note = "Fixture endpoint on 127.0.0.1 with a canned reply (NOT AI); shows what the production adapter sends.",
                choices = results
            };
        }
        finally
        {
            stop.Cancel();
            listener.Stop();
            try { await serving; } catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException or IOException) { }
        }
    }

    /// <summary>The model default against Off on Ollama on this PC: the production adapter's reply (how soon its first words
    /// came) and one plain request each with the same thinking fields, whose answer says how much Ollama thought first.</summary>
    private static async Task<object> LiveAsync(string model, CancellationToken cancellation)
    {
        const string baseUrl = GenerationSupport.LocalOllamaChatBaseUrl;
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromMinutes(3) };
        try
        {
            using var version = await client.GetAsync("http://127.0.0.1:11434/api/version", cancellation);
            if (!version.IsSuccessStatusCode) return NotRun($"Ollama on this PC answered {(int)version.StatusCode}.");
        }
        catch (HttpRequestException) { return NotRun("Ollama isn't running on this PC."); }
        var limits = new TextGenerationLimits
        {
            MaxOutputTokens = 4096, MaxEvents = 4094, FirstDeltaTimeout = TimeSpan.FromMinutes(2), IdleTimeout = TimeSpan.FromMinutes(2),
            MaxRequestTime = TimeSpan.FromMinutes(2)
        };
        using var adapter = ChatCompletionsTextGenerationAdapter.Create(baseUrl);
        var results = new List<(string Name, Asked Reply, Plain Plain)>();
        // The model default first, so its run also loads the model and Off is timed warm.
        foreach (var (name, value) in Choices.Where(c => c.Value != true))
        {
            var plain = await PlainAsync(client, model, value, cancellation);
            var reply = await AskAsync(adapter, baseUrl, model, value, limits, cancellation);
            results.Add((name, reply, plain));
        }
        var standard = results[0];
        var off = results[1];
        var ok = off.Reply.Outcome == "Completed" && off.Plain.Error is null && off.Plain.ReasoningCharacters == 0;
        return new
        {
            ran = true,
            ok,
            model,
            question = Question,
            note = "Ollama on this PC over loopback; a fixed question, never anything the owner said.",
            thinksByDefault = standard.Plain.ReasoningCharacters > 0,
            choices = results.Select(r => new
            {
                choice = r.Name,
                sends = GenerationSupport.ReasoningJson(SetupRouteType.ChatCompletions, baseUrl, Choices.First(c => c.Name == r.Name).Value),
                reply = new { r.Reply.Outcome, r.Reply.Failure, r.Reply.FirstWordsMs, r.Reply.TotalMs, text = r.Reply.Reply },
                plain = new { r.Plain.ReasoningCharacters, r.Plain.CompletionTokens, r.Plain.Ms, r.Plain.Error }
            }).ToArray()
        };
    }

    private sealed record Plain(int ReasoningCharacters, long? CompletionTokens, long Ms, string? Error);

    // One non-streamed request with the production thinking fields: Ollama returns its thinking as message.reasoning.
    private static async Task<Plain> PlainAsync(HttpClient client, string model, bool? reasoning, CancellationToken cancellation)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("model", model);
            writer.WriteBoolean("stream", false);
            if (reasoning is { } value)
                GenerationSupport.WriteReasoning(writer, GenerationSupport.ChatReasoning(GenerationSupport.LocalOllamaChatBaseUrl), value);
            writer.WriteStartArray("messages");
            writer.WriteStartObject();
            writer.WriteString("role", "user");
            writer.WriteString("content", Question);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        var clock = Stopwatch.StartNew();
        using var content = new ByteArrayContent(buffer.ToArray());
        content.Headers.ContentType = new("application/json");
        using var response = await client.PostAsync(GenerationSupport.LocalOllamaChatBaseUrl + "/chat/completions", content, cancellation);
        var text = await response.Content.ReadAsStringAsync(cancellation);
        if (!response.IsSuccessStatusCode) return new(0, null, clock.ElapsedMilliseconds, $"{(int)response.StatusCode}: {Trim(text)}");
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        var message = root.GetProperty("choices")[0].GetProperty("message");
        var thought = message.TryGetProperty("reasoning", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString()!.Length : 0;
        long? tokens = root.TryGetProperty("usage", out var usage) && usage.TryGetProperty("completion_tokens", out var count) &&
            count.TryGetInt64(out var n) ? n : null;
        return new(thought, tokens, clock.ElapsedMilliseconds, null);
    }

    private static string Trim(string text) => text.Length > 300 ? text[..300] + "…" : text;

    private sealed record Asked(string? Outcome, string? Failure, string Reply, long? FirstWordsMs, long TotalMs);

    private static async Task<Asked> AskAsync(ChatCompletionsTextGenerationAdapter adapter, string baseUrl, string model, bool? reasoning,
        TextGenerationLimits limits, CancellationToken cancellation)
    {
        var ids = new CorrelationIds { SessionId = Guid.NewGuid(), TurnId = Guid.NewGuid(), RequestId = Guid.NewGuid() };
        var deadline = DateTimeOffset.UtcNow + limits.MaxRequestTime;
        var selection = new TextModelSelection(ChatCompletionsSetup.Alias, model);
        var authorization = new TextDisclosureAuthorization(new(ChatCompletionsSetup.BaseUri(baseUrl), ProviderRole.Llm, model),
            selection, ids, 1, limits, deadline, true, true);
        var generation = reasoning is null ? null : new GenerationSettings { Reasoning = reasoning };
        var clock = Stopwatch.StartNew();
        long? first = null;
        var stream = adapter.Stream(new() { Ids = ids, Epoch = 1, Deadline = deadline }, selection,
            new BoundedTextInput(Question, "Answer briefly."), limits, authorization, cancellation, generation);
        var reply = new StringBuilder();
        await foreach (var item in stream.WithCancellation(cancellation))
            if (item.Kind == ProviderEventKind.TextDelta)
            {
                first ??= clock.ElapsedMilliseconds;
                reply.Append(item.Text);
            }
        return new(stream.Result?.Outcome.ToString(), stream.Result?.Failure?.Code.ToString(), Trim(reply.ToString().Trim()), first,
            clock.ElapsedMilliseconds);
    }

    // The request's thinking properties, in the order sent, as compact JSON ({} when none).
    private static string ReasoningFields(byte[] body)
    {
        using var document = JsonDocument.Parse(body);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject())
                if (property.Name is "reasoning_effort" or "reasoning" or "chat_template_kwargs" or "think")
                    property.WriteTo(writer);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    // A minimal HTTP/1.1 endpoint: records each request body and streams one canned Chat Completions reply.
    private static async Task ServeAsync(TcpListener listener, List<byte[]> requests, CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            using var client = await listener.AcceptTcpClientAsync(cancellation);
            await using var stream = client.GetStream();
            var body = await HearingCheck.ReadRequestAsync(stream, cancellation);
            lock (requests) requests.Add(body);
            const string chunk = "{\"id\":\"fixture\",\"object\":\"chat.completion.chunk\",\"model\":\"fixture\",\"choices\":[{\"index\":0,";
            var events = "data: " + chunk + "\"delta\":{\"role\":\"assistant\",\"content\":\"Fixture reply (not AI).\"},\"finish_reason\":null}]}\n\n" +
                "data: " + chunk + "\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
            var payload = Encoding.UTF8.GetBytes(events);
            var head = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\n" +
                $"Content-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(head, cancellation);
            await stream.WriteAsync(payload, cancellation);
            await stream.FlushAsync(cancellation);
        }
    }
}
