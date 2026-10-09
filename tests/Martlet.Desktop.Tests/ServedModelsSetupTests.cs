using System.Globalization;
using System.IO;
using Martlet.Core.Cluster;
using Martlet.Core.Planning;
using Martlet.Core.Settings;
using Martlet.Desktop;
using Martlet.Providers;

namespace Martlet.Desktop.Tests;

/// <summary>Use models your apps already run: the models found on this PC reach the recommender, the review, the apply step and
/// Set it all up for me.</summary>
public sealed class ServedModelsSetupTests
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private const string LmStudio = "http://127.0.0.1:1234/v1";

    private static ServedModel Lm(string model, double? gb = null) => new("", "lmstudio", "LM Studio", LmStudio, model) { SizeGb = gb };

    // MainWindow's statics include pack:// resources: register the scheme as a WPF app would before using them.
    public ServedModelsSetupTests() => _ = System.IO.Packaging.PackUriHelper.UriSchemePack;

    [Fact]
    public void The_models_found_skip_apps_that_need_a_key_keep_ollama_sizes_and_go_on_this_PC()
    {
        LocalModelServer[] servers =
        [
            new("ollama", "Ollama", MainWindow.LocalOllamaBaseUrl, ["llama3.3:70b", "gemma4:e2b"])
            {
                SizesGb = new Dictionary<string, double> { ["llama3.3:70b"] = 42.5 }
            },
            new("lmstudio", "LM Studio", LmStudio, ["qwen3-32b"]),
            new("vllm", "vLLM", "http://127.0.0.1:8000/v1", ["secret-model"]) { NeedsKey = true }
        ];

        var served = RecommendedSetupInputs.Served(servers);

        Assert.Equal(["llama3.3:70b", "gemma4:e2b", "qwen3-32b"], served.Select(m => m.ModelId));
        Assert.Equal(42.5, served[0].SizeGb);
        Assert.Null(served[1].SizeGb);
        Assert.Equal(("lmstudio", "LM Studio", LmStudio), (served[2].AppId, served[2].AppName, served[2].BaseUrl));
        Assert.Empty(RecommendedSetupInputs.Served(null));

        var build = RecommendedSetupInputs.Request(RecommendedSetupInputs.ServedFixture(DateTimeOffset.UtcNow));
        Assert.All(build.Request.ServedModels, m => Assert.Equal("desk-host", m.MachineId));
    }

    [Fact]
    public void Use_models_your_apps_already_run_is_on_unless_the_owner_turns_it_off()
    {
        Assert.True(new RecommendedSetupMemory().UseServedModels);
        var directory = Path.Combine(Path.GetTempPath(), "martlet-served-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.True(RecommendedSetupMemory.Load(directory).UseServedModels);
            // A file from before the choice existed keeps it on.
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, RecommendedSetupMemory.FileName), """{ "Off": ["Pictures"] }""");
            Assert.True(RecommendedSetupMemory.Load(directory).UseServedModels);
            Assert.True(RecommendedSetupMemory.Load(directory).WithServed(false).Save(directory));
            var off = RecommendedSetupMemory.Load(directory);
            Assert.False(off.UseServedModels);
            Assert.Equal([PlanComponent.Pictures], off.OffParts);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void The_served_fixture_thinks_with_the_biggest_served_model_that_fits_and_the_review_names_the_app()
    {
        var build = RecommendedSetupInputs.Request(RecommendedSetupInputs.ServedFixture(DateTimeOffset.UtcNow));
        var recommendation = NetworkRecommender.Recommend(build.Request, FootprintCatalog.Default);

        Assert.Equal(ServedModels.OptionId("qwen3-32b"), recommendation.Target.Job(ClusterJobs.Thinking)?.OptionId);
        var change = Assert.Single(recommendation.Changes, c => c.Job == ClusterJobs.Thinking);
        Assert.Equal(SetupChangeBenefit.Improvement, change.Benefit);
        Assert.Contains("LM Studio", change.Why, StringComparison.Ordinal);
        Assert.DoesNotContain(recommendation.Changes, c => c.OptionId == ServedModels.OptionId("llama3.3:70b"));

        var review = RecommendedSetupReview.From(recommendation, build);
        Assert.True(review.UseServed);
        Assert.Equal(["llama3.3:70b in Ollama (about 46 GB)", "qwen3-32b in LM Studio (about 22 GB)"], review.Served);
    }

    [Fact]
    public void A_served_model_is_used_where_its_app_was_found_and_is_otherwise_left_to_the_owner()
    {
        var served = Lm("qwen3-32b", 19.8);
        var facts = new SetupRouteFacts(false, null, OllamaMissing: true, ParakeetInstalled: _ => false, Served: served);
        var option = ServedModels.OptionId("qwen3-32b");

        var ready = SetupRoutes.Read(ClusterJobs.Thinking, option, facts);
        Assert.Equal(SetupStepVerdict.Ready, ready.Verdict);
        Assert.False(ready.InUse);
        Assert.Null(ready.Terms);
        Assert.Contains("qwen3-32b in LM Studio", ready.Text, StringComparison.Ordinal);

        var route = new SetupRoute
        {
            Role = SetupRole.Llm, RouteType = SetupRouteType.ChatCompletions, ProviderAlias = "test", Origin = LmStudio, ModelId = "qwen3-32b",
            ConfigurationRevision = Guid.NewGuid()
        };
        Assert.True(SetupRoutes.Read(ClusterJobs.Thinking, option, facts with { Route = route }).InUse);
        var missing = SetupRoutes.Read(ClusterJobs.Thinking, option, facts with { Served = null });
        Assert.Equal(SetupStepVerdict.NeedsOwner, missing.Verdict);
        Assert.Contains("Companion › Thinking", missing.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Set_it_all_up_for_me_thinks_with_a_served_model_only_when_it_fits_beside_the_voice()
    {
        static (IReadOnlyList<GpuNow> Gpus, GpuInfo Windows, DefaultSetupPlan Plan) Pc(double gb)
        {
            IReadOnlyList<GpuNow> gpus = [new("NVIDIA GeForce RTX", gb, 1, "581.42")];
            var windows = new GpuInfo("NVIDIA GeForce RTX", gb);
            return (gpus, windows, DefaultSetup.Plan(gpus, windows, 16, English, ramGb: 64));
        }
        ServedModel[] served = [Lm("qwen3-32b"), Lm("text-embedding-nomic-embed-text-v1.5", 0.1), new("", "ollama", "Ollama", MainWindow.LocalOllamaBaseUrl, "gemma4:e2b")];

        var (gpus, windows, plan) = Pc(32);
        var big = DefaultSetup.WithServed(plan, served, gpus, windows, 16, English, 64);
        Assert.Equal("qwen3-32b", big.Served?.ModelId);
        Assert.True(big.ThinkingOnGpu);
        Assert.NotNull(big.Voice);
        Assert.Equal(plan.VoiceOnGpu, big.VoiceOnGpu);
        Assert.Contains("Thinking: qwen3-32b in LM Studio on this PC", big.Describe(), StringComparison.Ordinal);
        Assert.Contains("nothing downloads", big.Describe(), StringComparison.Ordinal);

        // An 8 GB card has no room for it beside the voice; Martlet's own Ollama model is never taken as "served".
        var (smallGpus, smallWindows, smallPlan) = Pc(8);
        Assert.Null(DefaultSetup.WithServed(smallPlan, served, smallGpus, smallWindows, 16, English, 64).Served);
        Assert.Null(DefaultSetup.WithServed(plan, [served[2]], gpus, windows, 16, English, 64).Served);
        Assert.Same(plan, DefaultSetup.WithServed(plan, [], gpus, windows, 16, English, 64));
    }
}
