using System.Net;
using System.Net.Http;
using System.Text;
using Martlet.Core.Planning;
using Martlet.Providers.LocalModels;

namespace Martlet.Providers.Tests;

public sealed class LocalModelFactsReaderTests
{
    [Fact]
    public async Task A_model_reads_its_config_and_the_preferred_gguf_repository_in_a_few_cached_requests()
    {
        var seen = new List<string>();
        var reader = LocalModelFactsFixture.Reader(seen);
        var facts = await reader.LookupAsync("google/gemma-4-E2B-it", CancellationToken.None);
        Assert.Empty(facts.Problems);
        Assert.Equal(("google/gemma-4-E2B-it", "ggml-org/gemma-4-E2B-it-GGUF"), (facts.HuggingFaceRepo, facts.GgufRepo));
        Assert.Equal(["Q4_0", "Q8_0", "BF16"], facts.Quantizations.Select(q => q.Name));
        Assert.Equal("hf.co/ggml-org/gemma-4-E2B-it-GGUF:Q4_0", facts.Quantization()!.InstallName);
        Assert.Equal(986_833_664, facts.Quantization()!.EncoderBytes);
        Assert.Equal(["mtp-gemma-4-E2B-it-Q8_0.gguf"], facts.Drafts.Select(d => d.Name));
        Assert.Equal(new LocalModelInputs(true, true, true, "config.json"), facts.Inputs);
        Assert.Equal((5_123_178_051L, 4_647_450_147L), (facts.Parameters!.Value, facts.WeightsParameters!.Value));
        Assert.Equal(("apache-2.0", 131_072), (facts.License, facts.MaxContext!.Value));
        Assert.Equal(4, seen.Count);
        Assert.Equal(4, facts.Sources.Count);
        await reader.LookupAsync("hf.co/ggml-org/gemma-4-E2B-it-GGUF:Q8_0", CancellationToken.None);
        Assert.Equal(4, seen.Count);
    }

    [Fact]
    public async Task An_ollama_tag_gives_its_exact_layers_and_a_repository_its_shape()
    {
        var reader = LocalModelFactsFixture.Reader();
        var tagOnly = await reader.LookupAsync("gemma4:26b", CancellationToken.None);
        Assert.Equal(("Q4_K_M", 17_074_419_072L, 1_194_828_128L, 461_766_752L, "gemma4:26b"),
            (tagOnly.Quantizations[0].Name, tagOnly.Quantizations[0].WeightsBytes, tagOnly.Quantizations[0].EncoderBytes,
                tagOnly.Quantizations[0].DraftBytes, tagOnly.Quantizations[0].InstallName));
        Assert.Equal(new LocalModelInputs(true, null, null, "Ollama registry (projector layer)"), tagOnly.Inputs);
        Assert.False(tagOnly.Estimate()!.KvCacheKnown);
        var both = await reader.LookupAsync("google/gemma-4-26B-A4B-it", "gemma4:26b", CancellationToken.None);
        Assert.Equal(false, both.Inputs!.Audio);
        Assert.InRange(both.ActiveParameters!.Value / 1e9, 3.7, 3.9);
        Assert.True(both.Estimate()!.KvCacheKnown);
        Assert.Contains(both.Problems, p => p.Contains("no GGUF repository", StringComparison.Ordinal));
        var missing = await reader.LookupAsync("gemma4:nope", CancellationToken.None);
        Assert.False(missing.LocallyHostable);
        Assert.Contains(missing.Problems, p => p.Contains("no library/gemma4:nope", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("google/gemma-4-E2B-it", "google/gemma-4-E2B-it", null, null)]
    [InlineData("hf.co/unsloth/gemma-4-E2B-it-GGUF:UD-Q4_K_XL", "unsloth/gemma-4-E2B-it-GGUF", "UD-Q4_K_XL", null)]
    [InlineData("https://huggingface.co/Qwen/Qwen3.5-4B", "Qwen/Qwen3.5-4B", null, null)]
    [InlineData("gemma4:e2b", null, null, "gemma4:e2b")]
    [InlineData("llama3.2", null, null, "llama3.2")]
    [InlineData("hf.co/not a repo", null, null, null)]
    public void Queries_name_a_repository_or_a_tag(string query, string? repo, string? quantization, string? tag) =>
        Assert.Equal((repo, quantization, tag), LocalModelFactsReader.Parse(query));

    [Fact]
    public async Task A_gated_model_uses_its_gguf_repositorys_config()
    {
        var reader = Reader(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/models/meta-llama/Llama-3.1-8B-Instruct" => Json("""{"id":"meta-llama/Llama-3.1-8B-Instruct","gated":"manual","cardData":{"license":"llama3.1"},"safetensors":{"total":8030261248},"siblings":[{"rfilename":"config.json","size":855}]}"""),
            "/api/models" => Json("""[{"id":"someone/Llama-3.1-8B-Instruct-GGUF"},{"id":"unsloth/Llama-3.1-8B-Instruct-GGUF"}]"""),
            "/api/models/unsloth/Llama-3.1-8B-Instruct-GGUF" => Json("""{"id":"unsloth/Llama-3.1-8B-Instruct-GGUF","siblings":[{"rfilename":"config.json","size":900},{"rfilename":"Llama-3.1-8B-Instruct-Q4_K_M.gguf","size":4920734176},{"rfilename":"Q8_0/Llama-3.1-8B-Instruct-Q8_0-00001-of-00002.gguf","size":4000000000},{"rfilename":"Q8_0/Llama-3.1-8B-Instruct-Q8_0-00002-of-00002.gguf","size":4500000000},{"rfilename":"Llama-3.1-8B-Instruct.imatrix.gguf","size":5000000}]}"""),
            "/meta-llama/Llama-3.1-8B-Instruct/raw/main/config.json" => new HttpResponseMessage(HttpStatusCode.Unauthorized),
            "/unsloth/Llama-3.1-8B-Instruct-GGUF/raw/main/config.json" => Json("""{"model_type":"llama","num_hidden_layers":32,"num_attention_heads":32,"num_key_value_heads":8,"hidden_size":4096,"max_position_embeddings":131072}"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });
        var facts = await reader.LookupAsync("meta-llama/Llama-3.1-8B-Instruct", CancellationToken.None);
        Assert.True(facts.Gated);
        Assert.Equal("unsloth/Llama-3.1-8B-Instruct-GGUF", facts.GgufRepo);
        Assert.Equal(32, facts.Architecture!.FullLayers);
        Assert.Empty(facts.Problems);
        Assert.Equal([("Q4_K_M", 4_920_734_176L), ("Q8_0", 8_500_000_000L)], facts.Quantizations.Select(q => (q.Name, q.WeightsBytes)));
        Assert.InRange(facts.Estimate()!.KvCacheBytes / 1e9, 1.07, 1.08);
    }

    [Fact]
    public async Task It_stays_within_its_request_budget_and_pauses_when_asked_to_slow_down()
    {
        var sent = 0;
        var clock = new ManualClock();
        var reader = Reader(_ =>
        {
            sent++;
            var slow = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            slow.Headers.RetryAfter = new(TimeSpan.FromMinutes(2));
            return slow;
        }, clock, budget: 3);
        var first = await reader.LookupAsync("a/b", CancellationToken.None);
        Assert.Contains(first.Problems, p => p.Contains("slow down", StringComparison.Ordinal));
        await reader.LookupAsync("c/d", CancellationToken.None);
        Assert.Equal(1, sent);
        clock.Advance(TimeSpan.FromMinutes(3));
        var budgeted = Reader(_ => { sent++; return new HttpResponseMessage(HttpStatusCode.InternalServerError); }, clock, budget: 3);
        for (var i = 0; i < 5; i++) await budgeted.LookupAsync($"a/b{i}", CancellationToken.None);
        Assert.Equal(4, sent);
        var last = await budgeted.LookupAsync("a/z", CancellationToken.None);
        Assert.Contains(last.Problems, p => p.Contains("already asked Hugging Face 3 times", StringComparison.Ordinal));
    }

    [Fact]
    public async Task What_ollama_reports_for_a_loaded_model_is_kept_when_it_changes()
    {
        var directory = Path.Combine(Path.GetTempPath(), "martlet-measured-" + Guid.NewGuid().ToString("N"));
        using var client = new HttpClient(new Stub(request => request.RequestUri!.AbsolutePath == "/api/ps"
            ? Json("""{"models":[{"name":"gemma4:e2b","model":"gemma4:e2b","size":5100000000,"size_vram":3300000000,"context_length":8192,"digest":"abc123"},{"name":"","size":1}]}""")
            : new HttpResponseMessage(HttpStatusCode.NotFound)));
        try
        {
            var origin = new Uri("http://127.0.0.1:11434/");
            var (use, saved) = await OllamaMeasuredMemory.RecordAsync(client, origin, "gemma4:e2b", directory, CancellationToken.None);
            Assert.True(saved);
            Assert.Equal(("http://127.0.0.1:11434", 5_100_000_000L, 3_300_000_000L, (int?)8_192, "abc123"),
                (use!.Host, use.Bytes, use.GraphicsBytes, use.ContextTokens, use.Digest));
            Assert.False((await OllamaMeasuredMemory.RecordAsync(client, origin, "gemma4:e2b", directory, CancellationToken.None)).Saved);
            Assert.Null((await OllamaMeasuredMemory.RecordAsync(client, origin, "qwen3.5:4b", directory, CancellationToken.None)).Use);
            Assert.Single(MeasuredModelMemory.Load(directory).Models);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static LocalModelFactsReader Reader(Func<HttpRequestMessage, HttpResponseMessage> answer, TimeProvider? clock = null, int budget = 100) =>
        new(new HttpClient(new Stub(answer)), new Uri("https://hf.test/"), new Uri("https://registry.test/"), clock, budget);

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(answer(request));
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan by) => now += by;
    }
}
