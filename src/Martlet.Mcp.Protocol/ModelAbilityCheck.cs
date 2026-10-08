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

/// <summary>model_ability_check: what models were found to hear and see (model-abilities.json in a data directory, the
/// <c>model-abilities</c> shared setting), then the production detection rehearsed against fixture servers on 127.0.0.1 shaped
/// like OpenRouter's model list (<c>architecture.input_modalities</c>), llama.cpp (<c>/props</c> modalities) and Ollama
/// (<c>/api/show</c> capabilities), Test hearing (<see cref="ModelHearingTest"/>) and Test vision (<see cref="ModelVisionTest"/>,
/// with the desktop's own picture of a word) against a fixture Chat Completions endpoint that "hears" or "sees" only when the
/// request carries the recording or the picture (it is told the word; NOT AI), the hearing and vision decisions replies use
/// (<see cref="HearingModelCatalog.ForRoute"/>, <see cref="VisionModelCatalog.ForRoute"/>) and the shared value's round trip.
/// With <c>baseUrl</c> (a server on this PC only, for example Ollama's http://127.0.0.1:11434/v1) and <c>modelId</c> it also
/// asks that real server; with <c>test</c> it runs Test hearing against it with a word said by Windows speech, and with
/// <c>testVision</c> Test vision with a word drawn on this PC. Nothing leaves this PC; no credentials are read; nothing is
/// saved.</summary>
internal static class ModelAbilityCheck
{
    private const string FixtureWord = "pineapple";

    internal static async Task<object> RunAsync(string dataDirectory, string? baseUrl, string? modelId, bool test, bool testVision,
        CancellationToken cancellation)
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
                models = saved.Models.Select(m => new { m.Origin, m.ModelId, m.Hears, m.Sees, m.Source, m.CheckedAt }).ToArray()
            },
            fixture = await FixtureAsync(cancellation),
            decisions = Decisions(),
            shared = Shared(),
            real = real is null ? null : await RealAsync(real.AbsoluteUri.TrimEnd('/'), modelId!, test, testVision, cancellation)
        };
    }

    private static object Decisions()
    {
        const string ollama = GenerationSupport.LocalOllamaChatBaseUrl, openRouter = ChatCompletionsEndpointCatalog.OpenRouterBaseUrl;
        var at = DateTimeOffset.UtcNow;
        var found = new ModelAbilities()
            .With(new() { Origin = ollama, ModelId = "gemma4:12b", Hears = true, Sees = true, Source = "Ollama on this PC", CheckedAt = at })
            .With(new() { Origin = openRouter, ModelId = "x-ai/grok-4.3", Hears = false, Sees = true, Source = "OpenRouter's model list", CheckedAt = at })
            .With(new() { Origin = ollama, ModelId = "qwen3:8b", Hears = false, Sees = false, Source = "Ollama on this PC", CheckedAt = at });
        string Hear(SetupRouteType? type, string origin, string model, ModelAbilities? abilities) =>
            HearingModelCatalog.ForRoute(type, origin, model, abilities).ToString();
        var rows = new
        {
            ollamaGemma4E2bByName = Hear(SetupRouteType.ChatCompletions, ollama, "gemma4:e2b", null),
            ollamaGemma4_12bByName = Hear(SetupRouteType.ChatCompletions, ollama, "gemma4:12b", null),
            ollamaGemma4_12bFound = Hear(SetupRouteType.ChatCompletions, ollama, "gemma4:12b", found),
            openRouterGrokFound = Hear(SetupRouteType.ChatCompletions, openRouter, "x-ai/grok-4.3", found),
            hostOllamaGemma4E2b = Hear(SetupRouteType.GatewayOllama, "gpu-pc", "gemma4:e2b", found),
            openAiResponses = Hear(SetupRouteType.OpenAi, "https://api.openai.com", "gpt-4.1-mini-2025-04-14", found),
            retired = HearingModelCatalog.ForRoute(SetupRouteType.ChatCompletions, ollama, "gemma4:e2b", found, retired: true).ToString(),
            ollamaQwen3SeesFound = VisionModelCatalog.ForRoute(ollama, "qwen3:8b", found).ToString(),
            ollamaGemma4_12bSeesFound = VisionModelCatalog.ForRoute(ollama, "gemma4:12b", found).ToString()
        };
        var ok = rows is
        {
            ollamaGemma4E2bByName: "Supported", ollamaGemma4_12bByName: "Unsupported", ollamaGemma4_12bFound: "Supported",
            openRouterGrokFound: "Unsupported", hostOllamaGemma4E2b: "Unsupported", openAiResponses: "Unsupported", retired: "Unsupported",
            ollamaQwen3SeesFound: "Unsupported", ollamaGemma4_12bSeesFound: "Supported"
        };
        return new { ok, rows };
    }

    private static object Shared()
    {
        var at = new DateTimeOffset(2026, 10, 3, 20, 0, 0, TimeSpan.FromHours(-7));
        // Metadata that says only what the model sees, then a test that says it hears: both are kept.
        var abilities = new ModelAbilities()
            .With(new() { Origin = "http://127.0.0.1:8080/v1", ModelId = "voxtral", Sees = false, Source = "the server's llama.cpp settings", CheckedAt = at })
            .With(new() { Origin = "http://127.0.0.1:8080/v1", ModelId = "voxtral", Hears = true, Source = "a test request", CheckedAt = at.AddMinutes(1) })
            .With(new() { Origin = "https://openrouter.ai/api/v1", ModelId = "google/gemini-2.5-flash", Hears = true, Sees = true,
                Source = "OpenRouter's model list", CheckedAt = at });
        var value = abilities.Share();
        var parsed = ModelAbilities.Parse(value);
        var again = parsed?.Share();
        var merged = abilities.Find("http://127.0.0.1:8080/v1", "voxtral");
        return new
        {
            ok = again == value && merged is { Hears: true, Sees: false, Source: "a test request" } && ModelAbilities.Parse("{\"SchemaVersion\":2}") is null &&
                ModelAbilities.Parse("not json") is null && Martlet.Core.Sync.SharedSettings.IsKey("model-abilities"),
            key = "model-abilities",
            characters = value.Length,
            roundTrip = again == value,
            keptBoth = merged is { Hears: true, Sees: false },
            newerRefused = ModelAbilities.Parse("{\"SchemaVersion\":2}") is null
        };
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
            ? (200, "{\"data\":[{\"id\":\"acme/omni\",\"context_length\":32768,\"architecture\":{\"input_modalities\":[\"text\",\"image\",\"audio\"]}}," +
                "{\"id\":\"acme/sight\",\"context_length\":8192,\"architecture\":{\"input_modalities\":[\"text\",\"image\"]}}]}")
            : NotFound);
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
            string Reply(string text) => "{\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":" + JsonSerializer.Serialize(text) + "}}]}";
            return model switch
            {
                "hears" => (200, Reply(heard ? "Pineapple." : "I didn't get a recording.")),
                "drops-audio" => (200, Reply("Sorry, I can't listen to recordings, only read text.")),
                "refuses-audio" => (400, "{\"error\":{\"message\":\"This model does not support audio input.\",\"type\":\"invalid_request_error\"}}"),
                "sees" => (200, Reply(saw ? "Pineapple." : "I didn't get a picture.")),
                "drops-image" => (200, Reply("Sorry, I can only read text.")),
                "refuses-image" => (400, "{\"error\":{\"message\":\"This model does not support image input.\",\"type\":\"invalid_request_error\"}}"),
                "wrong-key" => (401, "{\"error\":{\"message\":\"Invalid API key.\"}}"),
                _ => (404, "{\"error\":{\"message\":\"model not found\"}}")
            };
        });

        using var client = ModelContextProbe.CreateClient(loopback: true);
        async Task<object> Context(string baseUrl, string model, string name)
        {
            var report = await ModelContextProbe.ChatCompletionsAsync(client, baseUrl, model, null, name, cancellation);
            return new { report.Reached, report.ContextTokens, report.Hears, report.Sees, report.AbilitySource };
        }
        async Task<object> Ollama(string model)
        {
            var report = await ModelContextProbe.OllamaAsync(client, new Uri(ollama.Origin + "/"), model, load: false, cancellation);
            return new { report.Reached, report.ModelMaximum, report.Hears, report.Sees, report.AbilitySource };
        }
        var clip = Tone();
        async Task<object> Test(string model)
        {
            var report = await ModelHearingTest.RunAsync(client, chat.Origin + "/v1", model, null, clip, FixtureWord, "the fixture", cancellation);
            return new { report.Hears, report.Reached, report.Summary };
        }
        var omni = await Context(openRouter.Origin + "/api/v1", "acme/omni", "the OpenRouter fixture");
        var sight = await Context(openRouter.Origin + "/api/v1", "acme/sight", "the OpenRouter fixture");
        var unlisted = await Context(openRouter.Origin + "/api/v1", "acme/unlisted", "the OpenRouter fixture");
        var llamaCpp = await Context(llama.Origin + "/v1", "model.gguf", "the llama.cpp fixture");
        var gemma = await Ollama("gemma4:e2b");
        var qwen = await Ollama("qwen3:8b");
        var hears = await Test("hears");
        var drops = await Test("drops-audio");
        var refuses = await Test("refuses-audio");
        var key = await Test("wrong-key");
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
        var ok = Json(omni).Contains("\"Hears\":true,\"Sees\":true", StringComparison.Ordinal) &&
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
            openRouterOmni = omni, openRouterSight = sight, openRouterUnlisted = unlisted, llamaCpp, ollamaGemma4E2b = gemma, ollamaQwen3 = qwen,
            testHears = hears, testDropsAudio = drops, testRefusesAudio = refuses, testWrongKey = key, testRequest = request,
            testSees = sees, testDropsImage = dropsImage, testRefusesImage = refusesImage, testWrongKeyVision = keyVision, visionRequest,
            picture = drawn
        };
    }

    // ---------- a real server on this PC ----------

    private static async Task<object> RealAsync(string baseUrl, string modelId, bool test, bool testVision, CancellationToken cancellation)
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
        var found = report.Hears is null && report.Sees is null ? null : new ModelAbilities().With(new()
        {
            Origin = baseUrl, ModelId = modelId, Hears = report.Hears, Sees = report.Sees, Source = report.AbilitySource ?? "metadata",
            CheckedAt = DateTimeOffset.UtcNow
        });
        return new
        {
            baseUrl, modelId, metadata = new { report.Reached, report.ContextTokens, report.Hears, report.Sees, report.AbilitySource, report.Summary, ms = metadataMs },
            routeHearing = HearingModelCatalog.ForRoute(SetupRouteType.ChatCompletions, baseUrl, modelId, found).ToString(),
            routeVision = VisionModelCatalog.ForRoute(baseUrl, modelId, found).ToString(),
            hearingTest = hearing,
            visionTest = vision
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
