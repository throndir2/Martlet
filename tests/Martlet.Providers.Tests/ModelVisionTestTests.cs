using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Providers.Tests;

public sealed class ModelVisionTestTests
{
    // A PNG is only checked for its signature here; the desktop draws the real picture (VisionTestPicture).
    private static BoundedImage Picture() =>
        new([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. new byte[56]], ImageMediaType.Png, 640, 240);

    [Theory]
    [InlineData(200, "{\"choices\":[{\"message\":{\"content\":\"LIGHTHOUSE\"}}]}", true)]
    [InlineData(200, "{\"choices\":[{\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"The word is lighthouse.\"}]}}]}", true)]
    [InlineData(200, "{\"choices\":[{\"message\":{\"content\":\"I can't see images.\"}}]}", false)]
    [InlineData(400, "{\"error\":{\"message\":\"This model does not support image input.\"}}", false)]
    [InlineData(422, "{\"error\":{\"message\":\"vision is not enabled for this model\"}}", false)]
    [InlineData(500, "{\"error\":{\"message\":\"model has no multimodal projector\"}}", false)]
    [InlineData(401, "{\"error\":{\"message\":\"Invalid API key.\"}}", null)]
    [InlineData(404, "{\"error\":{\"message\":\"model not found\"}}", null)]
    [InlineData(429, "{\"error\":{\"message\":\"slow down\"}}", null)]
    [InlineData(200, "{\"choices\":[]}", null)]
    public async Task Test_vision_reads_the_answer(int status, string body, bool? sees)
    {
        byte[]? sent = null;
        using var client = new HttpClient(new Stub(request =>
        {
            sent = request.Content!.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }));
        var report = await ModelVisionTest.RunAsync(client, "http://127.0.0.1:9/v1", "model", null, Picture(), "lighthouse", "the server",
            CancellationToken.None);
        Assert.Equal(sees, report.Sees);
        Assert.True(report.Reached);
        using var request = JsonDocument.Parse(sent!);
        var content = request.RootElement.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal(new string?[] { "text", "image_url" }, content.EnumerateArray().Select(p => p.GetProperty("type").GetString()).ToArray());
        Assert.Equal(ModelVisionTest.Question, content[0].GetProperty("text").GetString());
        Assert.StartsWith("data:image/png;base64,", content[1].GetProperty("image_url").GetProperty("url").GetString());
        Assert.False(request.RootElement.GetProperty("stream").GetBoolean());
        Assert.False(request.RootElement.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
        // The word is only in the picture.
        Assert.DoesNotContain("lighthouse", Encoding.UTF8.GetString(sent!), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_key_goes_to_its_own_server_as_a_bearer_token()
    {
        string? authorization = null;
        Uri? asked = null;
        using var client = new HttpClient(new Stub(request =>
        {
            authorization = request.Headers.Authorization?.ToString();
            asked = request.RequestUri;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"penguin\"}}]}", Encoding.UTF8, "application/json")
            };
        }));
        var report = await ModelVisionTest.RunAsync(client, ChatCompletionsEndpointCatalog.OpenRouterBaseUrl + "/", "acme/sight", "sk-test",
            Picture(), "penguin", "OpenRouter", CancellationToken.None);
        Assert.True(report.Sees);
        Assert.Equal("Bearer sk-test", authorization);
        Assert.Equal(ChatCompletionsEndpointCatalog.OpenRouterBaseUrl + "/chat/completions", asked!.AbsoluteUri);
        Assert.DoesNotContain("sk-test", report.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Test_vision_asks_Ollama_without_thinking_or_a_reply_budget_and_a_cloud_model_with_both()
    {
        using var ollama = JsonDocument.Parse(ModelVisionTest.Body(GenerationSupport.LocalOllamaChatBaseUrl, "qwen2.5vl:7b", Picture()));
        Assert.Equal("none", ollama.RootElement.GetProperty("reasoning_effort").GetString());
        Assert.False(ollama.RootElement.TryGetProperty("max_tokens", out _));
        using var cloud = JsonDocument.Parse(ModelVisionTest.Body(ChatCompletionsEndpointCatalog.OpenRouterBaseUrl, "google/gemini-2.5-flash", Picture()));
        Assert.Equal("none", cloud.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.Equal(32, cloud.RootElement.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public void A_paired_computers_answer_is_read_the_same_way()
    {
        Assert.True(ModelVisionTest.Read("Volcano.", "volcano", "qwen2.5vl:7b", "diva", 420).Sees);
        var wrong = ModelVisionTest.Read("I see a picture of a mountain.", "volcano", "qwen2.5vl:7b", "diva");
        Assert.False(wrong.Sees);
        Assert.Contains("doesn't seem to see", wrong.Summary, StringComparison.Ordinal);
        Assert.Null(ModelVisionTest.Read("  ", "volcano", "qwen2.5vl:7b", "diva").Sees);
        Assert.Equal(12, ModelVisionTest.Words.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData("llama3.2-vision", 404, "{\"error\":{\"message\":\"model 'llama3.2-vision' not found\"}}")]
    [InlineData("meta-llama/llama-3.2-11b-vision-instruct", 400, "{\"error\":{\"message\":\"llama-3.2-11b-vision-instruct is not a valid model ID\"}}")]
    [InlineData("llava:13b", 404, "{\"error\":{\"message\":\"model \\\"llava\\\" not found, try pulling it first\"}}")]
    public async Task A_missing_model_named_for_vision_is_not_read_as_one_that_cannot_see(string model, int status, string body)
    {
        using var client = new HttpClient(new Stub(_ =>
            new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") }));
        var report = await ModelVisionTest.RunAsync(client, "http://127.0.0.1:9/v1", model, null, Picture(), "penguin", "the server",
            CancellationToken.None);
        Assert.Null(report.Sees);
    }

    [Fact]
    public async Task A_missing_model_named_for_audio_is_not_read_as_one_that_cannot_hear()
    {
        using var client = new HttpClient(new Stub(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("{\"error\":{\"message\":\"model 'gpt-4o-audio-preview' not found\"}}", Encoding.UTF8, "application/json")
        }));
        var clip = BoundedWaveAudio.FromPcm(new Martlet.Core.Audio.PcmFormat
        {
            SampleRate = 16_000, Channels = 1, Encoding = Martlet.Core.Audio.PcmEncoding.Signed16LittleEndian
        }, new byte[3200]);
        var report = await ModelHearingTest.RunAsync(client, "http://127.0.0.1:9/v1", "gpt-4o-audio-preview", null, clip, "pineapple", "the server",
            CancellationToken.None);
        Assert.Null(report.Hears);
        Assert.Contains("doesn't have gpt-4o-audio-preview", report.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unreachable_server_tells_nothing()
    {
        using var client = new HttpClient(new Stub(_ => throw new HttpRequestException(HttpRequestError.ConnectionError, "refused")));
        var report = await ModelVisionTest.RunAsync(client, "http://127.0.0.1:9/v1", "model", null, Picture(), "penguin", "the server",
            CancellationToken.None);
        Assert.Null(report.Sees);
        Assert.False(report.Reached);
        Assert.Contains("nothing answered", report.Summary, StringComparison.Ordinal);
    }

    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(answer(request));
    }
}
