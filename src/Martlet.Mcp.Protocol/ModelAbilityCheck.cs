using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Martlet.Core.Audio;
using Martlet.Core.Settings;
using Martlet.Mcp.Shared;
using Martlet.Providers;

namespace Martlet.Mcp;

/// <summary>model_ability_check: what models were found to take on each route (model-abilities.json in a data directory, the
/// <c>model-abilities</c> shared setting: hearing, seeing, video, tools and retired, each with its source and date), then the
/// production detection rehearsed against fixture servers on 127.0.0.1 shaped like OpenRouter's model list
/// (<c>architecture.input_modalities</c>, <c>supported_parameters</c>), llama.cpp (<c>/props</c> modalities), Ollama
/// (<c>/api/show</c> capabilities) and NVIDIA Build (its IDs-only model list, <c>models.md</c> and a model page), Test hearing
/// (<see cref="ModelHearingTest"/>), Test vision (<see cref="ModelVisionTest"/>, with the desktop's own picture of a word) and
/// Test tools (<see cref="ModelToolTest"/>) against a fixture Chat Completions endpoint that "hears" or "sees" only when the
/// request carries the recording or the picture (it is told the word; NOT AI), the decisions replies use
/// (<see cref="HearingModelCatalog.ForRoute"/>, <see cref="VisionModelCatalog.ForRoute"/>, <see cref="RouteAbilities"/>,
/// <see cref="ChatCompletionsEndpointCatalog.RetiredOn(string?, string?, ModelAbilities?)"/>) and the shared value's round trip,
/// including what an older Martlet reads. With <c>baseUrl</c> (a server on this PC only, for example Ollama's
/// http://127.0.0.1:11434/v1) and <c>modelId</c> it also asks that real server; with <c>test</c> it runs Test hearing against it
/// with a word said by Windows speech, with <c>testVision</c> Test vision with a word drawn on this PC, and with <c>testTools</c>
/// Test tools with one made-up tool. Nothing leaves this PC; no credentials are read; nothing is saved.</summary>
internal static class ModelAbilityCheck
{
    private const string FixtureWord = "pineapple";

    internal static async Task<object> RunAsync(string dataDirectory, string? baseUrl, string? modelId, bool test, bool testVision,
        bool testTools, CancellationToken cancellation)
    {
        Uri? real = null;
        if (baseUrl is not null)
        {
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out real) || real.Scheme != Uri.UriSchemeHttp || !ModelContextProbe.IsLoopback(real))
                throw new ArgumentException("baseUrl must be an http:// server on this PC (127.0.0.1 or ::1).");
            if (string.IsNullOrWhiteSpace(modelId)) throw new ArgumentException("modelId is required with baseUrl.");
        }
        var saved = ModelAbilities.Load(dataDirectory);
        return new
        {
            saved = new
            {
                file = File.Exists(Path.Combine(dataDirectory, ModelAbilities.FileName)) ? "loaded" : "none",
                count = saved.Models.Count,
                models = saved.Models.Select(m => new
                {
                    m.Origin, m.ModelId, m.Hears, m.Sees, m.Video, m.Tools, m.Retired, m.Source, m.CheckedAt,
                    sources = Enum.GetValues<ModelFact>().Where(f => m.SourceOf(f) is not null)
                        .ToDictionary(f => f.ToString(), f => m.SourceOf(f)!)
                }).ToArray()
            },
            fixture = await FixtureAsync(cancellation),
            decisions = Decisions(),
            hostRoute = HostRouteAudio(),
            shared = Shared(),
            real = real is null ? null : await RealAsync(real.AbsoluteUri.TrimEnd('/'), modelId!, test, testVision, testTools, cancellation)
        };
    }

    private static object Decisions()
    {
        const string ollama = GenerationSupport.LocalOllamaChatBaseUrl, openRouter = ChatCompletionsEndpointCatalog.OpenRouterBaseUrl,
            nvidia = ChatCompletionsEndpointCatalog.NvidiaBuildBaseUrl, host = "https://gpu-pc.local:8443";
        var at = DateTimeOffset.UtcNow;
        var found = new ModelAbilities()
            .With(new() { Origin = ollama, ModelId = "gemma4:12b", Hears = true, Sees = true, Source = "Ollama on this PC", CheckedAt = at })
            .With(new() { Origin = openRouter, ModelId = "x-ai/grok-4.3", Hears = false, Sees = true, Source = "OpenRouter's model list", CheckedAt = at })
            .With(new() { Origin = ollama, ModelId = "qwen3:8b", Hears = false, Sees = false, Video = false, Tools = true, Source = "Ollama on this PC", CheckedAt = at })
            .With(new() { Origin = host, ModelId = "gemma4-e2b", Hears = false, Source = ModelAbility.RefusedRecording, CheckedAt = at })
            .With(new() { Origin = openRouter, ModelId = "google/gemma-4-26b-a4b-it", Hears = false, Sees = true, Video = true, Tools = true,
                Source = "OpenRouter's model list", CheckedAt = at })
            .With(new() { Origin = openRouter, ModelId = "acme/no-tools", Tools = false, Source = "OpenRouter's model list", CheckedAt = at })
            .With(new() { Origin = nvidia, ModelId = "acme/gone", Hears = true, Sees = true, Source = "NVIDIA Build's model page", CheckedAt = at })
            .With(new() { Origin = nvidia, ModelId = "acme/gone", Retired = at, Source = ModelAbility.GoneAnswer, CheckedAt = at });
        string Hear(SetupRouteType? type, string origin, string model, ModelAbilities? abilities) =>
            HearingModelCatalog.ForRoute(type, origin, model, abilities).ToString();
        // A test's answer stays when the server's metadata says otherwise later; another test replaces it.
        var trust = new ModelAbilities()
            .With(new() { Origin = ollama, ModelId = "acme/omni", Hears = true, Source = ModelAbility.TestRequest, CheckedAt = at })
            .With(new() { Origin = ollama, ModelId = "acme/omni", Hears = false, Sees = true, Source = "Ollama on this PC", CheckedAt = at.AddMinutes(1) });
        var retested = trust.With(new() { Origin = ollama, ModelId = "acme/omni", Hears = false, Source = ModelAbility.RefusedRecording, CheckedAt = at.AddMinutes(2) });
        var answered = found.Answered(nvidia, "acme/gone");
        var rows = new
        {
            ollamaGemma4E2bByName = Hear(SetupRouteType.ChatCompletions, ollama, "gemma4:e2b", null),
            ollamaGemma4_12bByName = Hear(SetupRouteType.ChatCompletions, ollama, "gemma4:12b", null),
            ollamaGemma4_12bFound = Hear(SetupRouteType.ChatCompletions, ollama, "gemma4:12b", found),
            openRouterGrokFound = Hear(SetupRouteType.ChatCompletions, openRouter, "x-ai/grok-4.3", found),
            // Thinking in Ollama on a paired computer takes recordings through its gateway, decided like any other model.
            hostOllamaGemma4E2b = Hear(SetupRouteType.GatewayOllama, "gpu-pc", "gemma4:e2b", found),
            hostOllamaGemma4E4bAlias = Hear(SetupRouteType.GatewayOllama, host, "gemma4-e4b", found),
            hostOllamaRefusedFound = Hear(SetupRouteType.GatewayOllama, host, "gemma4-e2b", found),
            openAiResponses = Hear(SetupRouteType.OpenAi, "https://api.openai.com", "gpt-4.1-mini-2025-04-14", found),
            retired = HearingModelCatalog.ForRoute(SetupRouteType.ChatCompletions, ollama, "gemma4:e2b", found, retired: true).ToString(),
            ollamaQwen3SeesFound = VisionModelCatalog.ForRoute(ollama, "qwen3:8b", found).ToString(),
            ollamaGemma4_12bSeesFound = VisionModelCatalog.ForRoute(ollama, "gemma4:12b", found).ToString(),
            // Video and tools: only what Martlet found out, never a name guess.
            gemma4VideoFound = RouteAbilities.Video(openRouter, "google/gemma-4-26b-a4b-it", found).ToString(),
            qwen3VideoFound = RouteAbilities.Video(ollama, "qwen3:8b", found).ToString(),
            unknownVideo = RouteAbilities.Video(openRouter, "acme/unlisted", found).ToString(),
            qwen3ToolsFound = RouteAbilities.Tools(SetupRouteType.ChatCompletions, ollama, "qwen3:8b", found).ToString(),
            noToolsFound = RouteAbilities.Tools(SetupRouteType.ChatCompletions, openRouter, "acme/no-tools", found).ToString(),
            unknownTools = RouteAbilities.Tools(SetupRouteType.ChatCompletions, openRouter, "acme/unlisted", found).ToString(),
            openAiTools = RouteAbilities.Tools(SetupRouteType.OpenAi, "https://api.openai.com", "gpt-4.1-mini-2025-04-14", found).ToString(),
            hostTools = RouteAbilities.Tools(SetupRouteType.GatewayOllama, host, "gemma4-e2b", found).ToString(),
            // A route that answered HTTP 410 is retired: it neither sees nor hears; a later answer clears it. The built-in list
            // stays as the fallback.
            goneSees = VisionModelCatalog.ForRoute(nvidia, "acme/gone", found).ToString(),
            goneHears = Hear(SetupRouteType.ChatCompletions, nvidia, "acme/gone", found),
            goneRetired = ChatCompletionsEndpointCatalog.RetiredOn(nvidia, "acme/gone", found) is { Since: not null, Server: "NVIDIA Build" } r &&
                r.Source == ModelAbility.GoneAnswer,
            goneKeptSees = found.Find(nvidia, "acme/gone")?.Sees == true,
            answeredRetired = ChatCompletionsEndpointCatalog.RetiredOn(nvidia, "acme/gone", answered) is not null,
            answeredSees = VisionModelCatalog.ForRoute(nvidia, "acme/gone", answered).ToString(),
            builtInRetired = ChatCompletionsEndpointCatalog.RetiredOn(nvidia, "meta/llama-3.3-70b-instruct", found)?.Source,
            testKeptOverMetadata = trust.Find(ollama, "acme/omni") is { Hears: true, Sees: true } kept &&
                kept.SourceOf(ModelFact.Hears)?.Source == ModelAbility.TestRequest && kept.SourceOf(ModelFact.Sees)?.Source == "Ollama on this PC",
            laterTestReplaces = retested.Find(ollama, "acme/omni")?.Hears == false
        };
        var ok = rows is
        {
            ollamaGemma4E2bByName: "Supported", ollamaGemma4_12bByName: "Unsupported", ollamaGemma4_12bFound: "Supported",
            openRouterGrokFound: "Unsupported", hostOllamaGemma4E2b: "Supported", hostOllamaGemma4E4bAlias: "Supported",
            hostOllamaRefusedFound: "Unsupported", openAiResponses: "Unsupported", retired: "Unsupported",
            ollamaQwen3SeesFound: "Unsupported", ollamaGemma4_12bSeesFound: "Supported",
            gemma4VideoFound: "Supported", qwen3VideoFound: "Unsupported", unknownVideo: "Unknown",
            qwen3ToolsFound: "Supported", noToolsFound: "Unsupported", unknownTools: "Unknown", openAiTools: "Supported", hostTools: "Unsupported",
            goneSees: "Unsupported", goneHears: "Unsupported", goneRetired: true, goneKeptSees: true, answeredRetired: false,
            answeredSees: "Supported", builtInRetired: "Martlet's list of retired models", testKeptOverMetadata: true, laterTestReplaces: true
        };
        return new { ok, rows };
    }

    /// <summary>Whether a paired computer's Thinking route takes a recording (<see cref="Martlet.Avatar.Audio2Face.Remote.HostRoute.CarriesAudio"/>):
    /// a host of this version advertises room for one; an older host's route (room for a screen image only) gets the transcript and
    /// should be updated.</summary>
    private static object HostRouteAudio()
    {
        const int olderBytes = 1_497_432;
        static Martlet.Avatar.Audio2Face.Remote.HostRoute Route(string id, string path, int bytes) =>
            new(id, path, "martlet.ollama-chat", "1", "ollama", "ollama", "1", "gemma4-e4b", "1", new string('0', 64),
                new string('0', 64), bytes, 98_304, 65_536, 4_096, 4_096, 1_048_576, TimeSpan.FromMinutes(5), "request_abort");
        var rows = new
        {
            requestBytes = SelfHostSetup.OllamaRequestBytes,
            thisVersion = Route(SelfHostSetup.OllamaRouteId, SelfHostSetup.OllamaPath, SelfHostSetup.OllamaRequestBytes).CarriesAudio,
            deepThinking = Route(SelfHostSetup.DeepThinkingRouteId, SelfHostSetup.DeepThinkingPath, SelfHostSetup.OllamaRequestBytes).CarriesAudio,
            olderHost = Route(SelfHostSetup.OllamaRouteId, SelfHostSetup.OllamaPath, olderBytes).CarriesAudio,
            voiceRoute = Route("martlet.gateway.f5-synthesis.v1", "/martlet/v1/inference/f5-synthesis", SelfHostSetup.OllamaRequestBytes).CarriesAudio
        };
        return new { ok = rows is { thisVersion: true, deepThinking: true, olderHost: false, voiceRoute: false }, rows };
    }

    private static object Shared()
    {
        var at = new DateTimeOffset(2026, 10, 3, 20, 0, 0, TimeSpan.FromHours(-7));
        // Metadata that says only what the model sees, then a test that says it hears: both are kept. A route found retired, and
        // one with only tools, say neither hearing nor seeing: they go apart (MoreModels) so an older Martlet still reads the rest.
        var abilities = new ModelAbilities()
            .With(new() { Origin = "http://127.0.0.1:8080/v1", ModelId = "voxtral", Sees = false, Source = "the server's llama.cpp settings", CheckedAt = at })
            .With(new() { Origin = "http://127.0.0.1:8080/v1", ModelId = "voxtral", Hears = true, Source = "a test request", CheckedAt = at.AddMinutes(1) })
            .With(new() { Origin = "https://openrouter.ai/api/v1", ModelId = "google/gemini-2.5-flash", Hears = true, Sees = true, Video = true,
                Tools = true, Source = "OpenRouter's model list", CheckedAt = at })
            .With(new() { Origin = ChatCompletionsEndpointCatalog.NvidiaBuildBaseUrl, ModelId = "acme/gone", Retired = at,
                Source = ModelAbility.GoneAnswer, CheckedAt = at })
            .With(new() { Origin = "http://127.0.0.1:11434/v1", ModelId = "qwen3:8b", Tools = true, Source = "Ollama on this PC", CheckedAt = at });
        var value = abilities.Share();
        var parsed = ModelAbilities.Parse(value);
        var again = parsed?.Share();
        var merged = abilities.Find("http://127.0.0.1:8080/v1", "voxtral");
        var older = OlderMartletReads(value);
        using var document = JsonDocument.Parse(value);
        var more = document.RootElement.TryGetProperty("MoreModels", out var list) ? list.GetArrayLength() : 0;
        return new
        {
            ok = again == value && merged is { Hears: true, Sees: false, Source: "a test request" } && ModelAbilities.Parse("{\"SchemaVersion\":2}") is null &&
                ModelAbilities.Parse("not json") is null && Martlet.Core.Sync.SharedSettings.IsKey("model-abilities") && older == 2 && more == 2 &&
                parsed?.Models.Count == 4 && parsed.Find(ChatCompletionsEndpointCatalog.NvidiaBuildBaseUrl, "acme/gone")?.Retired is not null &&
                parsed.Find("https://openrouter.ai/api/v1", "google/gemini-2.5-flash") is { Video: true, Tools: true },
            key = "model-abilities",
            characters = value.Length,
            roundTrip = again == value,
            keptBoth = merged is { Hears: true, Sees: false },
            newerRefused = ModelAbilities.Parse("{\"SchemaVersion\":2}") is null,
            // An older Martlet (before video, tools and retired) reads the routes that say hearing or seeing and skips the rest.
            olderMartletReads = older,
            keptApart = more
        };
    }

    // The shared value as Martlet read it before video, tools and retired were kept: each of Models must say hearing or seeing,
    // and unknown fields are skipped. The number of routes it reads; -1 when it would refuse the whole value.
    private static int OlderMartletReads(string json)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<OlderAbilities>(json, new JsonSerializerOptions
            {
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull, RespectNullableAnnotations = true, MaxDepth = 8
            });
            return parsed is { SchemaVersion: 1, Models: not null } && parsed.Models.Count <= ModelAbilities.MaximumModels &&
                parsed.Models.All(m => m is { Origin.Length: > 0 and <= 2048, ModelId.Length: > 0 and <= 256, Source.Length: > 0 and <= 200 } &&
                    (m.Hears is not null || m.Sees is not null))
                ? parsed.Models.Count : -1;
        }
        catch (Exception error) when (error is JsonException or NotSupportedException) { return -1; }
    }

    private sealed record OlderAbilities
    {
        public int SchemaVersion { get; init; } = 1;
        public IReadOnlyList<OlderAbility> Models { get; init; } = [];
    }

    private sealed record OlderAbility
    {
        public required string Origin { get; init; }
        public required string ModelId { get; init; }
        public bool? Hears { get; init; }
        public bool? Sees { get; init; }
        public required string Source { get; init; }
        public required DateTimeOffset CheckedAt { get; init; }
    }

    // ---------- fixtures (model_lab uses them too) ----------

    internal sealed record Request(string Method, string Path, byte[] Body);

    /// <summary>A one-request-per-connection HTTP server on 127.0.0.1 that answers with <c>answer</c>: JSON, or a streamed
    /// (server-sent events) answer when the body starts with <c>data:</c>.</summary>
    internal sealed class Fixture : IAsyncDisposable
    {
        private readonly TcpListener listener;
        private readonly CancellationTokenSource stop = new();
        private readonly Task serving;
        internal readonly List<Request> Requests = [];

        /// <param name="port">The port on 127.0.0.1; 0 (the default) takes a free one.</param>
        internal Fixture(Func<Request, (int Status, string Body)> answer, int port = 0)
        {
            listener = new(IPAddress.Loopback, port);
            listener.Start();
            serving = ServeAsync(answer);
        }

        internal string Origin => $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";

        private async Task ServeAsync(Func<Request, (int Status, string Body)> answer)
        {
            while (!stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(stop.Token); }
                catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException) { return; }
                using (client)
                {
                    try
                    {
                        await using var stream = client.GetStream();
                        var request = await ReadAsync(stream, stop.Token);
                        lock (Requests) Requests.Add(request);
                        var (status, body) = answer(request);
                        var payload = Encoding.UTF8.GetBytes(body);
                        var type = body.StartsWith("data:", StringComparison.Ordinal) ? "text/event-stream" : "application/json";
                        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}\r\nContent-Type: {type}\r\n" +
                            $"Content-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
                        await stream.WriteAsync(head, stop.Token);
                        await stream.WriteAsync(payload, stop.Token);
                    }
                    catch (Exception error) when (error is IOException or OperationCanceledException or SocketException) { }
                }
            }
        }

        private static async Task<Request> ReadAsync(NetworkStream stream, CancellationToken token)
        {
            var buffer = new MemoryStream();
            var one = new byte[8192];
            int end;
            while ((end = buffer.GetBuffer().AsSpan(0, (int)buffer.Length).IndexOf("\r\n\r\n"u8)) < 0)
            {
                var read = await stream.ReadAsync(one, token);
                if (read == 0) throw new IOException("The request ended early.");
                buffer.Write(one, 0, read);
            }
            var lines = Encoding.ASCII.GetString(buffer.GetBuffer(), 0, end).Split("\r\n");
            var first = lines[0].Split(' ');
            var length = lines.Skip(1).Select(h => h.Split(':', 2)).Where(h => h.Length == 2 &&
                h[0].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase)).Select(h => int.Parse(h[1].Trim())).FirstOrDefault(0);
            var body = new MemoryStream();
            body.Write(buffer.GetBuffer(), end + 4, (int)buffer.Length - end - 4);
            while (body.Length < length)
            {
                var read = await stream.ReadAsync(one, token);
                if (read == 0) break;
                body.Write(one, 0, read);
            }
            return new(first[0], first.Length > 1 ? first[1] : "/", body.ToArray());
        }

        public async ValueTask DisposeAsync()
        {
            stop.Cancel();
            listener.Stop();
            try { await serving; } catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException) { }
            stop.Dispose();
        }
    }

    private static (int, string) NotFound => (404, "{\"error\":\"not found\"}");

    private static async Task<object> FixtureAsync(CancellationToken cancellation)
    {
        await using var openRouter = new Fixture(r => r.Path == "/api/v1/models"
            ? (200, "{\"data\":[{\"id\":\"acme/omni\",\"context_length\":32768,\"architecture\":{\"input_modalities\":[\"text\",\"image\",\"audio\",\"video\"]}," +
                "\"supported_parameters\":[\"tools\",\"tool_choice\",\"temperature\"]}," +
                "{\"id\":\"acme/sight\",\"context_length\":8192,\"architecture\":{\"input_modalities\":[\"text\",\"image\"]},\"supported_parameters\":[\"temperature\"]}]}")
            : NotFound);
        // NVIDIA Build: its model list gives only IDs; models.md links each model's page (twice, as NVIDIA's does), and the page's
        // header and Specifications say what it takes. A page for another publisher's model of the same name is skipped.
        const string page = "---\ntitle: \"gemma-4-31b-it\"\npublisher: \"google\"\ntype: \"endpoint\"\n" +
            "canonical: \"https://build.nvidia.com/google/gemma-4-31b-it\"\n---\n\n# Gemma 4 31B IT\n\n**Data Modality:** Text, Image\n\n" +
            "## Specifications\n\n- **Context Length:** 262,144 tokens\n- **Input:** Text, Image, Video\n- **Output:** Text\n\n" +
            "## Capabilities\n\n- **Function Calling:** Supported\n";
        await using var nvidia = new Fixture(r => r.Path switch
        {
            "/v1/models" => (200, "{\"object\":\"list\",\"data\":[{\"id\":\"google/gemma-4-31b-it\",\"object\":\"model\"},{\"id\":\"acme/speech\",\"object\":\"model\"}]}"),
            "/models.md" => (200, "# Models\n\n- [gemma-4-31b-it](/qc69jvmznzxy/gemma-4-31b-it.md) — Gemma 4.\n" +
                "- [speech](/qc69jvmznzxy/speech.md) — A speech service.\n- [gemma-4-31b-it](/qc69jvmznzxy/gemma-4-31b-it.md) — again.\n"),
            "/qc69jvmznzxy/gemma-4-31b-it.md" => (200, page),
            "/qc69jvmznzxy/speech.md" => (200, "---\ntitle: \"speech\"\npublisher: \"acme\"\ncanonical: \"https://build.nvidia.com/acme/speech\"\n---\n" +
                "## Specifications\n\n- **Input:** Audio\n"),
            _ => NotFound
        });
        await using var llama = new Fixture(r => r.Path switch
        {
            "/v1/models" => (200, "{\"object\":\"list\",\"data\":[{\"id\":\"model.gguf\",\"meta\":{\"n_ctx_train\":131072}}]," +
                "\"models\":[{\"name\":\"model.gguf\",\"capabilities\":[\"completion\",\"multimodal\"]}]}"),
            "/props" => (200, "{\"default_generation_settings\":{\"n_ctx\":8192},\"modalities\":{\"vision\":false,\"video\":false,\"audio\":true}}"),
            _ => NotFound
        });
        await using var ollama = new Fixture(r => r.Path switch
        {
            "/api/show" when Encoding.UTF8.GetString(r.Body).Contains("gemma4:e2b", StringComparison.Ordinal) =>
                (200, "{\"model_info\":{\"gemma4.context_length\":131072},\"capabilities\":[\"completion\",\"vision\",\"audio\",\"tools\",\"thinking\"]}"),
            "/api/show" when Encoding.UTF8.GetString(r.Body).Contains("qwen3:8b", StringComparison.Ordinal) =>
                (200, "{\"model_info\":{\"qwen3.context_length\":40960},\"capabilities\":[\"completion\",\"tools\",\"thinking\"]}"),
            "/api/show" => (404, "{\"error\":\"model not found\"}"),
            "/api/ps" => (200, "{\"models\":[]}"),
            _ => NotFound
        });
        await using var chat = new Fixture(r =>
        {
            if (r.Path != "/v1/chat/completions") return NotFound;
            using var document = JsonDocument.Parse(r.Body);
            var model = document.RootElement.GetProperty("model").GetString();
            var heard = Encoding.UTF8.GetString(r.Body).Contains("\"input_audio\"", StringComparison.Ordinal);
            var saw = Encoding.UTF8.GetString(r.Body).Contains("\"image_url\"", StringComparison.Ordinal);
            var offered = document.RootElement.TryGetProperty("tools", out var tools) && tools.ValueKind == JsonValueKind.Array && tools.GetArrayLength() > 0;
            string Reply(string text) => "{\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":" + JsonSerializer.Serialize(text) + "}}]}";
            string Call() => "{\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":null,\"tool_calls\":[{\"id\":\"call_1\"," +
                "\"type\":\"function\",\"function\":{\"name\":\"" + ModelToolTest.ToolName + "\",\"arguments\":\"{\\\"word\\\":\\\"" + FixtureWord + "\\\"}\"}}]}," +
                "\"finish_reason\":\"tool_calls\"}]}";
            return model switch
            {
                "hears" => (200, Reply(heard ? "Pineapple." : "I didn't get a recording.")),
                "drops-audio" => (200, Reply("Sorry, I can't listen to recordings, only read text.")),
                "refuses-audio" => (400, "{\"error\":{\"message\":\"This model does not support audio input.\",\"type\":\"invalid_request_error\"}}"),
                "sees" => (200, Reply(saw ? "Pineapple." : "I didn't get a picture.")),
                "drops-image" => (200, Reply("Sorry, I can only read text.")),
                "refuses-image" => (400, "{\"error\":{\"message\":\"This model does not support image input.\",\"type\":\"invalid_request_error\"}}"),
                "calls-tools" => (200, offered ? Call() : Reply("No tool was offered.")),
                "words-only" => (200, Reply($"The word is {FixtureWord}.")),
                "refuses-tools" => (400, "{\"error\":{\"message\":\"registry.ollama.ai/library/refuses-tools does not support tools\"}}"),
                "gone" => (410, "{\"error\":{\"message\":\"This model reached its end of life.\"}}"),
                "wrong-key" => (401, "{\"error\":{\"message\":\"Invalid API key.\"}}"),
                _ => (404, "{\"error\":{\"message\":\"model not found\"}}")
            };
        });

        using var client = ModelContextProbe.CreateClient(loopback: true);
        async Task<object> Context(string baseUrl, string model, string name, Uri? pages = null)
        {
            var report = await ModelContextProbe.ChatCompletionsAsync(client, baseUrl, model, null, name, cancellation, pages);
            return new { report.Reached, report.ContextTokens, report.Hears, report.Sees, report.Video, report.Tools, report.AbilitySource };
        }
        async Task<object> Ollama(string model)
        {
            var report = await ModelContextProbe.OllamaAsync(client, new Uri(ollama.Origin + "/"), model, load: false, cancellation);
            return new { report.Reached, report.ModelMaximum, report.Hears, report.Sees, report.Video, report.Tools, report.AbilitySource };
        }
        var clip = Tone();
        async Task<object> Test(string model)
        {
            var report = await ModelHearingTest.RunAsync(client, chat.Origin + "/v1", model, null, clip, FixtureWord, "the fixture", cancellation);
            return new { report.Hears, report.Reached, report.Status, report.Summary };
        }
        async Task<object> Tools(string model)
        {
            var report = await ModelToolTest.RunAsync(client, chat.Origin + "/v1", model, null, FixtureWord, "the fixture", cancellation);
            return new { report.Tools, report.Reached, report.Status, report.Reply, report.Summary };
        }
        var omni = await Context(openRouter.Origin + "/api/v1", "acme/omni", "the OpenRouter fixture");
        var sight = await Context(openRouter.Origin + "/api/v1", "acme/sight", "the OpenRouter fixture");
        var unlisted = await Context(openRouter.Origin + "/api/v1", "acme/unlisted", "the OpenRouter fixture");
        var llamaCpp = await Context(llama.Origin + "/v1", "model.gguf", "the llama.cpp fixture");
        var nvidiaGemma = await Context(nvidia.Origin + "/v1", "google/gemma-4-31b-it", "the NVIDIA Build fixture", new Uri(nvidia.Origin + "/"));
        var nvidiaService = await Context(nvidia.Origin + "/v1", "acme/speech", "the NVIDIA Build fixture", new Uri(nvidia.Origin + "/"));
        var gemma = await Ollama("gemma4:e2b");
        var qwen = await Ollama("qwen3:8b");
        var hears = await Test("hears");
        var drops = await Test("drops-audio");
        var refuses = await Test("refuses-audio");
        var key = await Test("wrong-key");
        var goneHearing = await Test("gone");
        var callsTools = await Tools("calls-tools");
        var wordsOnly = await Tools("words-only");
        var refusesTools = await Tools("refuses-tools");
        var goneTools = await Tools("gone");
        var keyTools = await Tools("wrong-key");
        // Test vision with the desktop's own picture of the word (drawn on the STA thread, as the desktop draws it).
        var picture = await WpfThread.RunAsync(() => VisionTestPicture.Render(FixtureWord));
        async Task<object> See(string model)
        {
            var report = await ModelVisionTest.RunAsync(client, chat.Origin + "/v1", model, null, picture, FixtureWord, "the fixture", cancellation);
            return new { report.Sees, report.Reached, report.Summary };
        }
        var sees = await See("sees");
        var dropsImage = await See("drops-image");
        var refusesImage = await See("refuses-image");
        var keyVision = await See("wrong-key");
        Request? sent, shown;
        lock (chat.Requests)
        {
            sent = chat.Requests.FirstOrDefault();
            shown = chat.Requests.FirstOrDefault(r => Encoding.UTF8.GetString(r.Body).Contains("\"image_url\"", StringComparison.Ordinal));
        }
        object? request = null;
        if (sent is not null)
        {
            using var body = JsonDocument.Parse(sent.Body);
            var content = body.RootElement.GetProperty("messages")[0].GetProperty("content");
            var parts = content.EnumerateArray().Select(p => p.GetProperty("type").GetString()).ToArray();
            var audio = content.EnumerateArray().FirstOrDefault(p => p.GetProperty("type").GetString() == "input_audio");
            var wav = audio.ValueKind == JsonValueKind.Object ? Convert.FromBase64String(audio.GetProperty("input_audio").GetProperty("data").GetString()!) : [];
            request = new
            {
                parts, format = audio.ValueKind == JsonValueKind.Object ? audio.GetProperty("input_audio").GetProperty("format").GetString() : null,
                wavValid = wav.Length > 44 && wav.AsSpan(0, 4).SequenceEqual("RIFF"u8) && wav.AsSpan(8, 4).SequenceEqual("WAVE"u8),
                stream = body.RootElement.GetProperty("stream").GetBoolean(),
                thinkingStepsOff = body.RootElement.TryGetProperty("chat_template_kwargs", out var kwargs) &&
                    kwargs.GetProperty("enable_thinking").ValueKind == JsonValueKind.False,
                wordInRequestText = Encoding.UTF8.GetString(sent.Body).Contains(FixtureWord, StringComparison.OrdinalIgnoreCase)
            };
        }
        object? visionRequest = null;
        if (shown is not null)
        {
            using var body = JsonDocument.Parse(shown.Body);
            var content = body.RootElement.GetProperty("messages")[0].GetProperty("content");
            var parts = content.EnumerateArray().Select(p => p.GetProperty("type").GetString()).ToArray();
            var url = content.EnumerateArray().FirstOrDefault(p => p.GetProperty("type").GetString() == "image_url") is { ValueKind: JsonValueKind.Object } part
                ? part.GetProperty("image_url").GetProperty("url").GetString() ?? "" : "";
            const string prefix = "data:image/png;base64,";
            var png = url.StartsWith(prefix, StringComparison.Ordinal) ? Convert.FromBase64String(url[prefix.Length..]) : [];
            visionRequest = new
            {
                parts,
                pngDataUrl = png.Length > 8 && png.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
                samePicture = png.AsSpan().SequenceEqual(picture.Content.Span),
                stream = body.RootElement.GetProperty("stream").GetBoolean(),
                thinkingStepsOff = body.RootElement.TryGetProperty("chat_template_kwargs", out var kwargs) &&
                    kwargs.GetProperty("enable_thinking").ValueKind == JsonValueKind.False,
                wordInRequestText = Encoding.UTF8.GetString(shown.Body).Contains(FixtureWord, StringComparison.OrdinalIgnoreCase)
            };
        }
        var pixels = TouchZoneImages.Decode(picture.Content.ToArray());
        var dark = 0;
        for (var i = 0; i < pixels.Bgra.Length; i += 4) if (pixels.Bgra[i] < 128) dark++;
        var drawn = new
        {
            picture.Width, picture.Height, bytes = picture.ByteCount, picture.MimeType,
            // The word's letters cover part of the white picture: neither blank nor filled.
            darkShare = Math.Round(dark / (double)(pixels.Width * pixels.Height), 3)
        };
        string Json(object value) => JsonSerializer.Serialize(value);
        Request? toolSent;
        lock (chat.Requests) toolSent = chat.Requests.FirstOrDefault(r => Encoding.UTF8.GetString(r.Body).Contains("\"tools\"", StringComparison.Ordinal));
        object? toolRequest = null;
        if (toolSent is not null)
        {
            using var body = JsonDocument.Parse(toolSent.Body);
            var offered = body.RootElement.GetProperty("tools");
            toolRequest = new
            {
                tools = offered.GetArrayLength(),
                name = offered[0].GetProperty("function").GetProperty("name").GetString(),
                messages = body.RootElement.GetProperty("messages").GetArrayLength(),
                stream = body.RootElement.GetProperty("stream").GetBoolean()
            };
        }
        var routeFacts = Json(omni).Contains("\"Video\":true,\"Tools\":true", StringComparison.Ordinal) &&
            Json(sight).Contains("\"Video\":false,\"Tools\":false", StringComparison.Ordinal) &&
            Json(unlisted).Contains("\"Video\":null,\"Tools\":null", StringComparison.Ordinal) &&
            Json(nvidiaGemma).Contains("\"ContextTokens\":262144,\"Hears\":false,\"Sees\":true,\"Video\":true,\"Tools\":true", StringComparison.Ordinal) &&
            Json(nvidiaGemma).Contains("model page", StringComparison.Ordinal) &&
            Json(nvidiaService).Contains("\"Hears\":null,\"Sees\":null,\"Video\":null,\"Tools\":null", StringComparison.Ordinal) &&
            Json(gemma).Contains("\"Video\":false,\"Tools\":true", StringComparison.Ordinal) &&
            Json(qwen).Contains("\"Video\":false,\"Tools\":true", StringComparison.Ordinal) &&
            Json(goneHearing).Contains("\"Hears\":null,\"Reached\":true,\"Status\":410", StringComparison.Ordinal) &&
            Json(callsTools).Contains("\"Tools\":true", StringComparison.Ordinal) && Json(wordsOnly).Contains("\"Tools\":false", StringComparison.Ordinal) &&
            Json(refusesTools).Contains("\"Tools\":false", StringComparison.Ordinal) &&
            Json(goneTools).Contains("\"Tools\":null,\"Reached\":true,\"Status\":410", StringComparison.Ordinal) &&
            Json(keyTools).Contains("\"Tools\":null", StringComparison.Ordinal) &&
            Json(toolRequest ?? "").Contains($"\"tools\":1,\"name\":\"{ModelToolTest.ToolName}\",\"messages\":1,\"stream\":false", StringComparison.Ordinal);
        var ok = routeFacts && Json(omni).Contains("\"Hears\":true,\"Sees\":true", StringComparison.Ordinal) &&
            Json(sight).Contains("\"Hears\":false,\"Sees\":true", StringComparison.Ordinal) &&
            Json(unlisted).Contains("\"Hears\":null,\"Sees\":null", StringComparison.Ordinal) &&
            Json(llamaCpp).Contains("\"Hears\":true,\"Sees\":false", StringComparison.Ordinal) &&
            Json(gemma).Contains("\"Hears\":true,\"Sees\":true", StringComparison.Ordinal) &&
            Json(qwen).Contains("\"Hears\":false,\"Sees\":false", StringComparison.Ordinal) &&
            Json(hears).Contains("\"Hears\":true", StringComparison.Ordinal) && Json(drops).Contains("\"Hears\":false", StringComparison.Ordinal) &&
            Json(refuses).Contains("\"Hears\":false", StringComparison.Ordinal) && Json(key).Contains("\"Hears\":null", StringComparison.Ordinal) &&
            Json(request ?? "").Contains("\"wavValid\":true", StringComparison.Ordinal) &&
            Json(request ?? "").Contains("\"wordInRequestText\":false", StringComparison.Ordinal) &&
            Json(sees).Contains("\"Sees\":true", StringComparison.Ordinal) && Json(dropsImage).Contains("\"Sees\":false", StringComparison.Ordinal) &&
            Json(refusesImage).Contains("\"Sees\":false", StringComparison.Ordinal) && Json(keyVision).Contains("\"Sees\":null", StringComparison.Ordinal) &&
            Json(visionRequest ?? "").Contains("\"pngDataUrl\":true,\"samePicture\":true,\"stream\":false,\"thinkingStepsOff\":true,\"wordInRequestText\":false",
                StringComparison.Ordinal) &&
            drawn is { Width: VisionTestPicture.Width, Height: VisionTestPicture.Height, darkShare: > 0.01 and < 0.5 };
        return new
        {
            ok, note = "FIXTURE servers on 127.0.0.1, NOT the real services and NOT AI: the chat fixture is told the test word.",
            routeFacts,
            openRouterOmni = omni, openRouterSight = sight, openRouterUnlisted = unlisted, llamaCpp, nvidiaGemma, nvidiaService,
            ollamaGemma4E2b = gemma, ollamaQwen3 = qwen,
            testHears = hears, testDropsAudio = drops, testRefusesAudio = refuses, testWrongKey = key, testGone = goneHearing, testRequest = request,
            testSees = sees, testDropsImage = dropsImage, testRefusesImage = refusesImage, testWrongKeyVision = keyVision, visionRequest,
            testCallsTools = callsTools, testWordsOnly = wordsOnly, testRefusesTools = refusesTools, testGoneTools = goneTools,
            testWrongKeyTools = keyTools, toolRequest,
            picture = drawn
        };
    }

    // ---------- a real server on this PC ----------

    private static async Task<object> RealAsync(string baseUrl, string modelId, bool test, bool testVision, bool testTools, CancellationToken cancellation)
    {
        using var client = ModelContextProbe.CreateClient(loopback: true);
        var started = System.Diagnostics.Stopwatch.StartNew();
        var report = await ModelContextProbe.ChatCompletionsAsync(client, baseUrl, modelId, null, "the server on this PC", cancellation);
        var metadataMs = started.ElapsedMilliseconds;
        object? hearing = null;
        if (test)
        {
            var word = ModelHearingTest.Words[Random.Shared.Next(ModelHearingTest.Words.Count)];
            var clip = Speak(ModelHearingTest.Spoken(word));
            var result = await ModelHearingTest.RunAsync(client, baseUrl, modelId, null, clip, word, "the server on this PC", cancellation);
            hearing = new { word, clipSeconds = Math.Round(clip.Duration.TotalSeconds, 2), result.Hears, result.Reply, result.Milliseconds, result.Summary };
        }
        object? vision = null;
        if (testVision)
        {
            var word = ModelVisionTest.Words[Random.Shared.Next(ModelVisionTest.Words.Count)];
            var picture = await WpfThread.RunAsync(() => VisionTestPicture.Render(word));
            var result = await ModelVisionTest.RunAsync(client, baseUrl, modelId, null, picture, word, "the server on this PC", cancellation);
            vision = new { word, pictureBytes = picture.ByteCount, result.Sees, result.Reply, result.Milliseconds, result.Summary };
        }
        object? toolsTest = null;
        if (testTools)
        {
            var word = ModelVisionTest.Words[Random.Shared.Next(ModelVisionTest.Words.Count)];
            var result = await ModelToolTest.RunAsync(client, baseUrl, modelId, null, word, "the server on this PC", cancellation);
            toolsTest = new { word, result.Tools, result.Reply, result.Status, result.Milliseconds, result.Summary };
        }
        var found = !report.AbilitiesKnown ? null : new ModelAbilities().With(new()
        {
            Origin = baseUrl, ModelId = modelId, Hears = report.Hears, Sees = report.Sees, Video = report.Video, Tools = report.Tools,
            Source = report.AbilitySource ?? "metadata", CheckedAt = DateTimeOffset.UtcNow
        });
        return new
        {
            baseUrl, modelId, metadata = new
            {
                report.Reached, report.ContextTokens, report.Hears, report.Sees, report.Video, report.Tools, report.AbilitySource, report.Summary,
                ms = metadataMs
            },
            routeHearing = HearingModelCatalog.ForRoute(SetupRouteType.ChatCompletions, baseUrl, modelId, found).ToString(),
            routeVision = VisionModelCatalog.ForRoute(baseUrl, modelId, found).ToString(),
            routeVideo = RouteAbilities.Video(baseUrl, modelId, found).ToString(),
            routeTools = RouteAbilities.Tools(SetupRouteType.ChatCompletions, baseUrl, modelId, found).ToString(),
            hearingTest = hearing,
            visionTest = vision,
            toolsTest
        };
    }

    // The test word said by an English Windows voice, 16 kHz mono (the clip Test hearing sends; never anyone's voice).
    private static BoundedWaveAudio Speak(string text)
    {
        using var stream = new MemoryStream();
        using (var voice = new System.Speech.Synthesis.SpeechSynthesizer())
        {
            if (voice.GetInstalledVoices().FirstOrDefault(v => v.Enabled && v.VoiceInfo.Culture.Name.StartsWith("en", StringComparison.OrdinalIgnoreCase)) is { } english)
                voice.SelectVoice(english.VoiceInfo.Name);
            voice.SetOutputToAudioStream(stream, new System.Speech.AudioFormat.SpeechAudioFormatInfo(16_000,
                System.Speech.AudioFormat.AudioBitsPerSample.Sixteen, System.Speech.AudioFormat.AudioChannel.Mono));
            voice.Speak(text);
        }
        return BoundedWaveAudio.FromPcm(new PcmFormat { SampleRate = 16_000, Channels = 1, Encoding = PcmEncoding.Signed16LittleEndian }, stream.ToArray());
    }

    // Half a second of a 220 Hz tone: the fixture doesn't listen.
    private static BoundedWaveAudio Tone()
    {
        var pcm = new byte[16_000];
        for (var i = 0; i < pcm.Length / 2; i++)
            BitConverter.TryWriteBytes(pcm.AsSpan(i * 2), (short)(Math.Sin(2 * Math.PI * 220 * i / 16_000.0) * 6000));
        return BoundedWaveAudio.FromPcm(new PcmFormat { SampleRate = 16_000, Channels = 1, Encoding = PcmEncoding.Signed16LittleEndian }, pcm);
    }
}
