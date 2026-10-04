using System.Net;
using System.Net.Http;
using System.Text;
using Martlet.Providers.Ollama;

namespace Martlet.Providers.Tests;

public sealed class OllamaSideBySideTests
{
    private const long Gib = 1L << 30;
    private const string Thinking = "gemma4:e4b", Large = "gemma4:12b", Small = "qwen3:1.7b";
    private static readonly OllamaLoadedModel[] ThinkingLoaded = [new(Thinking, 7 * Gib, 7 * Gib)];
    private static readonly Dictionary<string, long> Downloads = new(StringComparer.OrdinalIgnoreCase)
    {
        [Thinking] = 6_583_656_505, [Large] = 8_021_618_941, [Small] = 1_400_000_000
    };

    [Theory]
    [InlineData(Large, 24, 7.5, true)]
    [InlineData(Large, 12, 7.5, false)]
    [InlineData(Small, 12, 7.5, true)]
    [InlineData(Small, 12, 11.0, false)]
    public void A_second_model_runs_only_when_both_fit_beside_what_else_uses_the_card(string deep, int totalGb, double usedGb, bool fits)
    {
        var fit = OllamaSideBySide.Decide(Thinking, deep, ThinkingLoaded, Downloads, new(totalGb * Gib, (long)(usedGb * Gib)));
        Assert.Equal(fits, fit.Fits);
        Assert.Equal(7 * Gib + OllamaSideBySide.Estimate(Downloads[deep]), fit.NeedBytes);
        Assert.Contains(deep, fit.Why, StringComparison.Ordinal);
        if (!fits) Assert.Contains("Choose a smaller model", fit.Why, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_the_driver_it_counts_the_cards_total_less_Ollamas_other_models()
    {
        OllamaLoadedModel[] loaded = [.. ThinkingLoaded, new("llava:7b", 5 * Gib, 5 * Gib)];
        Assert.True(OllamaSideBySide.Decide(Thinking, Small, ThinkingLoaded, Downloads, new(12 * Gib, null)).Fits);
        Assert.False(OllamaSideBySide.Decide(Thinking, Small, loaded, Downloads, new(12 * Gib, null)).Fits);
    }

    [Fact]
    public void Ollamas_own_figure_replaces_the_estimate_once_seen()
    {
        var estimated = OllamaSideBySide.Decide(Thinking, Small, ThinkingLoaded, Downloads, new(12 * Gib, 7 * Gib));
        Assert.True(estimated.Fits);
        var learned = new Dictionary<string, long> { [Small] = 6 * Gib };
        var seen = OllamaSideBySide.Decide(Thinking, Small, ThinkingLoaded, Downloads, new(12 * Gib, 7 * Gib), learned);
        Assert.False(seen.Fits);
        Assert.Equal(13 * Gib, seen.NeedBytes);
    }

    [Fact]
    public void It_never_runs_beside_itself_unknown_models_or_an_unknown_card()
    {
        Assert.False(OllamaSideBySide.Decide(Thinking, Thinking, ThinkingLoaded, Downloads, new(48 * Gib, 0)).Fits);
        Assert.False(OllamaSideBySide.Decide(Thinking, "GEMMA4:E4B", ThinkingLoaded, Downloads, new(48 * Gib, 0)).Fits);
        Assert.Contains("isn't downloaded", OllamaSideBySide.Decide(Thinking, "llama3.3:70b", ThinkingLoaded, Downloads, new(48 * Gib, 0)).Why,
            StringComparison.Ordinal);
        Assert.Contains("couldn't read how much graphics memory",
            OllamaSideBySide.Decide(Thinking, Small, ThinkingLoaded, Downloads, null).Why, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_models_already_loaded_on_the_card_fit()
    {
        OllamaLoadedModel[] both = [.. ThinkingLoaded, new(Large, 9 * Gib, 9 * Gib)];
        Assert.True(OllamaSideBySide.Decide(Thinking, Large, both, Downloads, new(12 * Gib, 11 * Gib)).Fits);
        OllamaLoadedModel[] spilled = [new(Thinking, 7 * Gib, 4 * Gib), new(Large, 9 * Gib, 9 * Gib)];
        Assert.False(OllamaSideBySide.Decide(Thinking, Large, spilled, Downloads, new(12 * Gib, 11 * Gib)).Fits);
    }

    [Fact]
    public void Thinking_already_partly_on_the_processor_leaves_no_room()
    {
        OllamaLoadedModel[] partial = [new(Thinking, 7 * Gib, 3 * Gib)];
        var fit = OllamaSideBySide.Decide(Thinking, Small, partial, Downloads, new(48 * Gib, 3 * Gib));
        Assert.False(fit.Fits);
        Assert.Contains("runs on the processor", fit.Why, StringComparison.Ordinal);
    }

    [Fact]
    public void A_think_stops_when_loading_its_model_pushed_Thinkings_off_the_card()
    {
        Assert.Null(OllamaSideBySide.PushedOut(Thinking, Large, ThinkingLoaded));
        Assert.Null(OllamaSideBySide.PushedOut(Thinking, Large, [.. ThinkingLoaded, new(Large, 9 * Gib, 9 * Gib)]));
        Assert.Contains("unloaded", OllamaSideBySide.PushedOut(Thinking, Large, [new(Large, 9 * Gib, 9 * Gib)]), StringComparison.Ordinal);
        Assert.Contains("pushed part", OllamaSideBySide.PushedOut(Thinking, Large,
            [new(Thinking, 7 * Gib, 4 * Gib), new(Large, 9 * Gib, 9 * Gib)]), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("gemma4", "gemma4:latest", true)]
    [InlineData("Gemma4:12B", "gemma4:12b", true)]
    [InlineData("gemma4:12b", "gemma4:e4b", false)]
    [InlineData("hf.co/acme/model:Q4_K_M", "hf.co/acme/model:Q4_K_M", true)]
    public void Names_match_as_Ollama_tags_them(string name, string model, bool same) =>
        Assert.Equal(same, OllamaSideBySide.Same(name, model));

    [Fact]
    public async Task It_reads_what_Ollama_has_loaded_and_downloaded_and_asks_it_to_load_and_unload()
    {
        var sent = new List<(string Path, string? Body)>();
        using var client = new HttpClient(new Stub(request =>
        {
            sent.Add((request.RequestUri!.AbsolutePath, request.Content?.ReadAsStringAsync().GetAwaiter().GetResult()));
            return request.RequestUri!.AbsolutePath switch
            {
                "/api/ps" => Json("{\"models\":[{\"name\":\"gemma4:e4b\",\"model\":\"gemma4:e4b\",\"size\":7516192768,\"size_vram\":7516192768}," +
                    "{\"name\":\"\",\"size\":1},{\"model\":\"llava:7b\",\"size\":5368709120}]}"),
                "/api/tags" => Json("{\"models\":[{\"name\":\"gemma4:12b\",\"size\":8021618941},{\"name\":\"mistral\",\"size\":4100000000}," +
                    "{\"name\":\"broken\"}]}"),
                "/api/generate" => Json("{\"done\":true}"),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        }));
        var origin = new Uri("http://127.0.0.1:11434/");
        var loaded = await OllamaSideBySide.LoadedAsync(client, origin, CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.Collection(loaded,
            model => Assert.Equal(new OllamaLoadedModel("gemma4:e4b", 7_516_192_768, 7_516_192_768), model),
            model => Assert.Equal(new OllamaLoadedModel("llava:7b", 5_368_709_120, 0), model));
        Assert.False(loaded[1].OnGraphicsCard);
        var downloads = await OllamaSideBySide.DownloadsAsync(client, origin, CancellationToken.None);
        Assert.Equal(8_021_618_941, downloads!["gemma4:12b"]);
        Assert.Equal(4_100_000_000, downloads["mistral:latest"]);
        Assert.Equal(2, downloads.Count);
        Assert.True(await OllamaSideBySide.LoadAsync(client, origin, Thinking, unload: false, TimeSpan.FromSeconds(5), CancellationToken.None));
        Assert.True(await OllamaSideBySide.LoadAsync(client, origin, Large, unload: true, TimeSpan.FromSeconds(5), CancellationToken.None));
        Assert.Equal("{\"model\":\"gemma4:e4b\"}", sent[2].Body);
        Assert.Equal("{\"model\":\"gemma4:12b\",\"keep_alive\":0}", sent[3].Body);
    }

    [Fact]
    public async Task An_Ollama_that_doesnt_answer_reads_as_unknown()
    {
        using var client = new HttpClient(new Stub(_ => throw new HttpRequestException("refused")));
        var origin = new Uri("http://127.0.0.1:11434/");
        Assert.Null(await OllamaSideBySide.LoadedAsync(client, origin, CancellationToken.None));
        Assert.Null(await OllamaSideBySide.DownloadsAsync(client, origin, CancellationToken.None));
        Assert.False(await OllamaSideBySide.LoadAsync(client, origin, Thinking, unload: false, TimeSpan.FromSeconds(5), CancellationToken.None));
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(answer(request));
    }
}
