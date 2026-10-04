using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Martlet.Core.Audio;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Providers.Tests;

public sealed class ModelAbilityDetectionTests
{
    private const string Ollama = GenerationSupport.LocalOllamaChatBaseUrl;
    private static readonly DateTimeOffset At = new(2026, 10, 3, 20, 0, 0, TimeSpan.Zero);

    private static ModelAbilities Found(string origin, string model, bool? hears, bool? sees) =>
        new ModelAbilities().With(new() { Origin = origin, ModelId = model, Hears = hears, Sees = sees, Source = "test", CheckedAt = At });

    [Fact]
    public void Ollama_on_this_PC_hears_by_name_and_by_what_it_says()
    {
        Assert.Equal(HearingSupport.Supported, HearingModelCatalog.ForRoute(SetupRouteType.ChatCompletions, Ollama, "gemma4:e2b", null));
        Assert.Equal(HearingSupport.Unsupported, HearingModelCatalog.ForRoute(SetupRouteType.ChatCompletions, Ollama, "gemma4:12b", null));
        Assert.Equal(HearingSupport.Supported,
            HearingModelCatalog.ForRoute(SetupRouteType.ChatCompletions, Ollama, "gemma4:12b", Found(Ollama, "gemma4:12b", true, true)));
    }

    [Fact]
    public void What_was_found_overrides_the_name_but_never_the_route()
    {
        var found = Found(ChatCompletionsEndpointCatalog.OpenRouterBaseUrl, "google/gemini-2.5-flash", false, null);
        Assert.Equal(HearingSupport.Unsupported, HearingModelCatalog.ForRoute(SetupRouteType.ChatCompletions,
            ChatCompletionsEndpointCatalog.OpenRouterBaseUrl, "google/gemini-2.5-flash", found));
        // The same model on another server isn't covered by what was found there.
        Assert.Equal(HearingSupport.Supported, HearingModelCatalog.ForRoute(SetupRouteType.ChatCompletions,
            "https://example.test/v1", "google/gemini-2.5-flash", found));
        var hears = Found("gpu-pc", "gemma4:e2b", true, true);
        Assert.Equal(HearingSupport.Unsupported, HearingModelCatalog.ForRoute(SetupRouteType.GatewayOllama, "gpu-pc", "gemma4:e2b", hears));
        Assert.Equal(HearingSupport.Unsupported, HearingModelCatalog.ForRoute(SetupRouteType.OpenAi, "gpu-pc", "gemma4:e2b", hears));
        Assert.Equal(HearingSupport.Unsupported, HearingModelCatalog.ForRoute(null, "gpu-pc", "gemma4:e2b", hears));
        Assert.Equal(HearingSupport.Unsupported, HearingModelCatalog.ForRoute(SetupRouteType.ChatCompletions, Ollama, "gemma4:e2b", null, retired: true));
    }

    [Fact]
    public void Answer_from_my_voice_needs_a_model_that_hears_and_a_recording_that_stays_here_or_consent()
    {
        // Gemma 4 E2B in Ollama on this PC: the recording never leaves this PC, so no further consent is needed.
        Assert.True(HearingModelCatalog.StaysOnThisPc(SetupRouteType.ChatCompletions, Ollama, "gemma4:e2b"));
        Assert.True(HearingModelCatalog.AnswersFromVoice(SetupRouteType.ChatCompletions, Ollama, "gemma4:e2b", null, hearConsent: false));
        // Ollama's cloud models forward what they get to ollama.com.
        foreach (var cloud in new[] { "gemma4:e2b-cloud", "gpt-oss:120b-cloud", "qwen3-omni:cloud" })
        {
            Assert.False(HearingModelCatalog.StaysOnThisPc(SetupRouteType.ChatCompletions, Ollama, cloud));
            Assert.False(HearingModelCatalog.AnswersFromVoice(SetupRouteType.ChatCompletions, Ollama, cloud, null, hearConsent: false));
        }
        // Another loopback server may forward what it gets, and a cloud model is elsewhere: both need Let Thinking hear my voice.
        Assert.False(HearingModelCatalog.StaysOnThisPc(SetupRouteType.ChatCompletions, "http://127.0.0.1:8080/v1", "gemma-4-e2b"));
        Assert.False(HearingModelCatalog.AnswersFromVoice(SetupRouteType.ChatCompletions, "http://127.0.0.1:8080/v1", "gemma-4-e2b", null, false));
        Assert.True(HearingModelCatalog.AnswersFromVoice(SetupRouteType.ChatCompletions, "http://127.0.0.1:8080/v1", "gemma-4-e2b", null, true));
        Assert.False(HearingModelCatalog.AnswersFromVoice(SetupRouteType.ChatCompletions,
            ChatCompletionsEndpointCatalog.OpenRouterBaseUrl, "google/gemini-2.5-flash", null, hearConsent: false));
        Assert.True(HearingModelCatalog.AnswersFromVoice(SetupRouteType.ChatCompletions,
            ChatCompletionsEndpointCatalog.OpenRouterBaseUrl, "google/gemini-2.5-flash", null, hearConsent: true));
        // A model that doesn't hear (by name or by what was found), a host's Ollama or a retired model never does.
        Assert.False(HearingModelCatalog.AnswersFromVoice(SetupRouteType.ChatCompletions, Ollama, "gemma4:12b", null, true));
        Assert.False(HearingModelCatalog.AnswersFromVoice(SetupRouteType.ChatCompletions, Ollama, "gemma4:e2b",
            Found(Ollama, "gemma4:e2b", false, true), true));
        Assert.False(HearingModelCatalog.AnswersFromVoice(SetupRouteType.GatewayOllama, "gpu-pc", "gemma4:e2b", null, true));
        Assert.False(HearingModelCatalog.AnswersFromVoice(SetupRouteType.ChatCompletions, Ollama, "gemma4:e2b", null, true, retired: true));
    }

    [Fact]
    public void Vision_follows_what_was_found()
    {
        Assert.Equal(VisionSupport.Unsupported, VisionModelCatalog.ForRoute(Ollama, "gemma4:e2b", Found(Ollama, "gemma4:e2b", true, false)));
        Assert.Equal(VisionSupport.Supported, VisionModelCatalog.ForRoute(Ollama, "acme-unknown", Found(Ollama, "acme-unknown", null, true)));
        Assert.Equal(VisionSupport.Supported, VisionModelCatalog.ForRoute(Ollama, "gemma4:e2b", null));
        Assert.Equal(VisionSupport.Unsupported, VisionModelCatalog.ForRoute(Ollama, "gemma4:e2b", null, retired: true));
    }

    [Theory]
    [InlineData("{\"id\":\"m\",\"architecture\":{\"input_modalities\":[\"text\",\"image\",\"audio\"]}}", true, true)]
    [InlineData("{\"id\":\"m\",\"architecture\":{\"input_modalities\":[\"text\",\"image\",\"file\"]}}", false, true)]
    [InlineData("{\"id\":\"m\",\"input_modalities\":[\"text\"]}", false, false)]
    [InlineData("{\"id\":\"m\",\"capabilities\":[\"completion\",\"vision\",\"audio\",\"tools\"]}", true, true)]
    [InlineData("{\"id\":\"m\",\"capabilities\":[\"completion\",\"tools\"]}", false, false)]
    [InlineData("{\"id\":\"m\",\"capabilities\":[\"completion\",\"multimodal\"]}", null, null)]
    [InlineData("{\"id\":\"m\",\"capabilities\":{\"completion_chat\":true,\"vision\":true}}", null, true)]
    [InlineData("{\"id\":\"m\",\"type\":\"vlm\"}", null, true)]
    [InlineData("{\"id\":\"m\",\"context_length\":8192}", null, null)]
    public void A_model_list_entry_says_what_the_model_takes(string entry, bool? hears, bool? sees)
    {
        using var document = JsonDocument.Parse(entry);
        var inputs = ModelContextProbe.InputsOf(document.RootElement);
        Assert.Equal(hears, inputs.Hears);
        Assert.Equal(sees, inputs.Sees);
    }

    [Fact]
    public async Task OpenRouter_s_model_list_says_whether_a_model_hears()
    {
        using var client = new HttpClient(new Stub(request => request.RequestUri!.AbsolutePath == "/api/v1/models"
            ? Json("{\"data\":[{\"id\":\"x-ai/grok-4.3\",\"context_length\":1000000,\"architecture\":{\"input_modalities\":[\"text\",\"image\",\"file\"]}}]}")
            : new HttpResponseMessage(HttpStatusCode.NotFound)));
        var report = await ModelContextProbe.ChatCompletionsAsync(client, "https://openrouter.example/api/v1", "x-ai/grok-4.3", "key", "OpenRouter",
            CancellationToken.None);
        Assert.Equal(1_000_000, report.ContextTokens);
        Assert.False(report.Hears);
        Assert.True(report.Sees);
        Assert.Equal("OpenRouter's model list", report.AbilitySource);
    }

    [Fact]
    public async Task A_llama_cpp_server_on_this_PC_says_it_through_props()
    {
        var asked = new List<string>();
        using var client = new HttpClient(new Stub(request =>
        {
            asked.Add(request.RequestUri!.AbsolutePath);
            return request.RequestUri.AbsolutePath switch
            {
                "/v1/models" => Json("{\"data\":[{\"id\":\"model.gguf\",\"meta\":{\"n_ctx_train\":131072}}]}"),
                "/props" => Json("{\"default_generation_settings\":{\"n_ctx\":8192},\"modalities\":{\"vision\":false,\"audio\":true}}"),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        }));
        var report = await ModelContextProbe.ChatCompletionsAsync(client, "http://127.0.0.1:8097/v1", "model.gguf", null, "the server", CancellationToken.None);
        Assert.Equal(131072, report.ContextTokens);
        Assert.True(report.Hears);
        Assert.False(report.Sees);
        Assert.Equal("the server's llama.cpp settings", report.AbilitySource);
        // Known from props: Ollama isn't asked.
        Assert.DoesNotContain("/api/show", asked);
    }

    [Fact]
    public async Task Ollama_says_it_through_its_capabilities()
    {
        using var client = new HttpClient(new Stub(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/show" => Json("{\"model_info\":{\"gemma4.context_length\":262144},\"capabilities\":[\"completion\",\"vision\",\"audio\",\"tools\",\"thinking\"]}"),
            "/api/ps" => Json("{\"models\":[]}"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        }));
        var report = await ModelContextProbe.OllamaAsync(client, new Uri("http://127.0.0.1:11434/"), "gemma4:12b", load: false, CancellationToken.None);
        Assert.True(report.Hears);
        Assert.True(report.Sees);
        Assert.Equal("Ollama on this PC", report.AbilitySource);
        Assert.Equal(262144, report.ModelMaximum);
    }

    [Theory]
    [InlineData(200, "{\"choices\":[{\"message\":{\"content\":\"Pineapple.\"}}]}", true)]
    [InlineData(200, "{\"choices\":[{\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"pine apple\"}]}}]}", true)]
    [InlineData(200, "{\"choices\":[{\"message\":{\"content\":\"I can't listen to audio.\"}}]}", false)]
    [InlineData(400, "{\"error\":{\"message\":\"This model does not support audio input.\"}}", false)]
    [InlineData(500, "{\"error\":{\"message\":\"audio input is not supported\"}}", false)]
    [InlineData(401, "{\"error\":{\"message\":\"Invalid API key.\"}}", null)]
    [InlineData(404, "{\"error\":{\"message\":\"model not found\"}}", null)]
    [InlineData(429, "{\"error\":{\"message\":\"slow down\"}}", null)]
    [InlineData(200, "{\"choices\":[]}", null)]
    public async Task Test_hearing_reads_the_answer(int status, string body, bool? hears)
    {
        byte[]? sent = null;
        using var client = new HttpClient(new Stub(request =>
        {
            sent = request.Content!.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }));
        var report = await ModelHearingTest.RunAsync(client, "http://127.0.0.1:9/v1", "model", null, Clip(), "pineapple", "the server",
            CancellationToken.None);
        Assert.Equal(hears, report.Hears);
        Assert.True(report.Reached);
        using var request = JsonDocument.Parse(sent!);
        var content = request.RootElement.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal(new string?[] { "text", "input_audio" }, content.EnumerateArray().Select(p => p.GetProperty("type").GetString()).ToArray());
        Assert.Equal("wav", content[1].GetProperty("input_audio").GetProperty("format").GetString());
        Assert.False(request.RootElement.GetProperty("stream").GetBoolean());
        Assert.False(request.RootElement.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
        Assert.DoesNotContain("pineapple", Encoding.UTF8.GetString(sent!), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Test_hearing_asks_Ollama_without_thinking_or_a_reply_budget()
    {
        using var request = JsonDocument.Parse(ModelHearingTest.Body(Ollama, "gemma4:e2b", Clip()));
        Assert.Equal("none", request.RootElement.GetProperty("reasoning_effort").GetString());
        Assert.False(request.RootElement.TryGetProperty("max_tokens", out _));
        using var cloud = JsonDocument.Parse(ModelHearingTest.Body(ChatCompletionsEndpointCatalog.OpenRouterBaseUrl, "google/gemini-2.5-flash", Clip()));
        Assert.Equal("none", cloud.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.Equal(32, cloud.RootElement.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public async Task An_unreachable_server_tells_nothing()
    {
        using var client = new HttpClient(new Stub(_ => throw new HttpRequestException(HttpRequestError.ConnectionError, "refused")));
        var report = await ModelHearingTest.RunAsync(client, "http://127.0.0.1:9/v1", "model", null, Clip(), "pineapple", "the server",
            CancellationToken.None);
        Assert.Null(report.Hears);
        Assert.False(report.Reached);
    }

    private static BoundedWaveAudio Clip() =>
        BoundedWaveAudio.FromPcm(new PcmFormat { SampleRate = 16_000, Channels = 1, Encoding = PcmEncoding.Signed16LittleEndian }, new byte[3200]);

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(answer(request));
    }
}
