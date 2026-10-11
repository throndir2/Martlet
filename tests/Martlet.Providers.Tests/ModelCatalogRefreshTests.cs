using System.Net;
using System.Net.Http;
using System.Text;
using Martlet.Core.Planning;
using Martlet.Providers.LocalModels;
using Martlet.Providers.ModelCatalogs;

namespace Martlet.Providers.Tests;

public sealed class ModelCatalogRefreshTests
{
    private const string OpenRouter = """
        {"data":[{"id":"google/gemma-4-26b-a4b-it","hugging_face_id":"google/gemma-4-26B-A4B-it","context_length":262144,"architecture":{"input_modalities":["text","image","video"],"output_modalities":["text"]},"pricing":{"prompt":"0","completion":"0"},"supported_parameters":["tools"]}]}
        """;

    private static readonly ModelCatalogEndpoints Endpoints = new(new("https://fixture/api/v1/models"), new("https://fixture/models.json"),
        new("https://fixture/api.json"), new("https://fixture/models.md"), new("https://fixture/vllm.md"), new("https://fixture/rows?dataset=x"));

    private sealed class Sources(Func<string, (HttpStatusCode, string)> answer) : HttpMessageHandler
    {
        public List<(DateTimeOffset At, string Path)> Asked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.PathAndQuery;
            lock (Asked) Asked.Add((DateTimeOffset.UtcNow, path));
            var (status, body) = answer(path);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "text/plain") });
        }
    }

    private static (HttpStatusCode, string) Answer(string path, bool failOpenRouter = false) => path switch
    {
        _ when path.StartsWith("/api/v1/models", StringComparison.Ordinal) => failOpenRouter ? (HttpStatusCode.BadGateway, "") : (HttpStatusCode.OK, OpenRouter),
        "/models.json" => (HttpStatusCode.OK, """{"google/gemma-4-26b-a4b-it":{"id":"google/gemma-4-26b-a4b-it","name":"Gemma 4 26B A4B IT","modalities":{"input":["text","image"]},"weights":[{"url":"https://huggingface.co/google/gemma-4-26B-A4B-it"}]}}"""),
        "/api.json" => (HttpStatusCode.OK, """{"groq":{"models":{"g":{"id":"g","canonical_model_id":"google/gemma-4-26b-a4b-it","modalities":{"input":["text"]}}}}}"""),
        "/models.md" => (HttpStatusCode.OK, "- [g](/x/g.md)"),
        "/x/g.md" => (HttpStatusCode.OK, "---\ntype: \"endpoint\"\ncanonical: \"https://build.nvidia.com/google/gemma-4-31b-it\"\n---\n## Specifications\n\n- **Input:** Text, Image\n"),
        "/vllm.md" => (HttpStatusCode.OK, "## List of Multimodal Language Models\n| Architecture | Models | Inputs | Example HF Models | LoRA |\n| - | - | - | - | - |\n| `Gemma4ForConditionalGeneration` | Gemma 4 | T + I + V + A<sup>*</sup> | `google/gemma-4-E2B-it` | |\n"),
        _ when path.StartsWith("/rows", StringComparison.Ordinal) => (HttpStatusCode.OK, """{"rows":[{"row":{"model_name":"gemma-4-26b-a4b","rating":1440,"category":"overall"}}]}"""),
        _ => (HttpStatusCode.NotFound, "")
    };

    [Fact]
    public async Task A_failing_source_keeps_its_last_good_part_and_the_rest_refresh()
    {
        var folder = Path.Combine(Path.GetTempPath(), "Martlet.Tests", "catalog-refresh-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ModelCatalogStore(folder);
            using var good = new HttpClient(new Sources(path => Answer(path)));
            var first = await new ModelCatalogRefresh(good, Endpoints, local: LocalModelFactsFixture.Reader()).RunAsync(store, CancellationToken.None);
            Assert.True(first.Saved);
            Assert.Null(first.Status.Problem);
            Assert.Equal(1, first.Status.Sources[CatalogSources.OpenRouter].Items);
            var model = store.Load().Find("gemma4:26b")!.Model;
            Assert.Equal((true, false), (model.TakesVideo, model.Hears));
            Assert.NotNull(model.Memory());

            using var failing = new HttpClient(new Sources(path => Answer(path, failOpenRouter: true)));
            var second = await new ModelCatalogRefresh(failing, Endpoints, local: LocalModelFactsFixture.Reader()).RunAsync(store, CancellationToken.None);
            Assert.True(second.Saved);
            Assert.Contains("502", second.Status.Sources[CatalogSources.OpenRouter].Problem, StringComparison.Ordinal);
            Assert.Contains("kept what it said", second.Status.Sources[CatalogSources.OpenRouter].Problem, StringComparison.Ordinal);
            Assert.Equal(first.Data.Block(CatalogSources.OpenRouter)!.Read, store.Cached()!.Block(CatalogSources.OpenRouter)!.Read);
            Assert.Equal(first.Status.LastSuccess, second.Status.LastSuccess);
            Assert.NotNull(store.Load().Route("openrouter", "google/gemma-4-26b-a4b-it"));
            Assert.Null(await new ModelCatalogRefresh(good, Endpoints).RunIfDueAsync(store, force: false, CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Nothing_is_asked_while_Martlet_is_replying()
    {
        var folder = Path.Combine(Path.GetTempPath(), "Martlet.Tests", "catalog-busy-" + Guid.NewGuid().ToString("N"));
        try
        {
            var sources = new Sources(path => Answer(path));
            using var client = new HttpClient(sources);
            var until = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1.5);
            var result = await new ModelCatalogRefresh(client, Endpoints, busy: () => DateTimeOffset.UtcNow < until, local: LocalModelFactsFixture.Reader())
                .RunAsync(new ModelCatalogStore(folder), CancellationToken.None);
            Assert.True(result.Saved);
            Assert.NotEmpty(sources.Asked);
            Assert.All(sources.Asked, a => Assert.True(a.At >= until));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task An_answer_larger_than_the_limit_is_refused()
    {
        var folder = Path.Combine(Path.GetTempPath(), "Martlet.Tests", "catalog-large-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var client = new HttpClient(new Sources(path => path == "/vllm.md" ? (HttpStatusCode.OK, new string('x', 5 * 1024 * 1024)) : Answer(path)));
            var result = await new ModelCatalogRefresh(client, Endpoints, local: LocalModelFactsFixture.Reader()).RunAsync(new ModelCatalogStore(folder), CancellationToken.None);
            Assert.Contains("larger than 4 MB", result.Status.Sources[CatalogSources.Vllm].Problem, StringComparison.Ordinal);
            Assert.True(result.Saved);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }
}
