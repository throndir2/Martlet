using Martlet.Core.Cluster;
using Martlet.Core.Planning;

namespace Martlet.Core.Tests.Planning;

public sealed class ServedModelsTests
{
    private static MachineGpu Nvidia(double gb) => new($"RTX {gb} GB", GpuVendor.Nvidia, gb);

    private static NetworkMachine Pc(params MachineGpu[] gpus) =>
        new(new MachineSpecs("pc", "pc") { Gpus = gpus, RamGb = 64, CpuThreads = 16, IsPrimary = true }, NetworkMachineKind.Companion)
        {
            HasHostService = true,
            OnWindows = true
        };

    private static ServedModel LmStudio(string model, double? sizeGb = null, string machine = "pc") =>
        new(machine, "lmstudio", "LM Studio", "http://127.0.0.1:1234/v1", model) { SizeGb = sizeGb };

    private static NetworkSetupRequest Alone(MachineGpu gpu, string thinking, params ServedModel[] served) =>
        new([Pc(gpu)])
        {
            VoiceEngine = "chatterbox",
            CurrentJobs = [new JobPlan(ClusterJobs.Thinking, null, OptionId: thinking)],
            ServedModels = served
        };

    private static NetworkSetupRequest Apply(NetworkSetupRequest request, NetworkRecommendation recommendation) => request with
    {
        Machines = request.Machines.Select(m => m with { Roles = recommendation.Target.Machine(m.Specs.Id)?.Roles ?? m.Roles }).ToArray(),
        CurrentJobs = recommendation.Target.Jobs,
        CurrentThinkingPool = recommendation.Target.ThinkingPool
    };

    [Theory]
    [InlineData("qwen3:32b", 32d)]
    [InlineData("Llama-3.3-70B-Instruct", 70d)]
    [InlineData("hf.co/unsloth/Qwen3-30B-A3B-GGUF:Q4_K_M", 30d)]
    [InlineData("mistral-small3.2:24b", 24d)]
    [InlineData("phi4-mini", null)]
    public void TheSizeComesFromTheModelsName(string model, double? billions) => Assert.Equal(billions, ServedModels.Billions(model));

    [Fact]
    public void TheDownloadSizeWinsOverTheName()
    {
        Assert.Equal(11.5, ServedModels.Gb(LmStudio("qwen3-14b", 10)));
        Assert.Equal(46.3, ServedModels.Gb(LmStudio("llama3.3:70b")));
        Assert.Null(ServedModels.Gb(LmStudio("my-model")));
    }

    [Fact]
    public void OnlyChatModelsThatMartletDoesNotRunItselfAreUsed()
    {
        var usable = ServedModels.Usable(
            [LmStudio("nomic-embed-text"), LmStudio("bge-reranker-v2"), LmStudio("gemma4:e2b"), LmStudio("qwen3:14b"),
             LmStudio("QWEN3:14B"), LmStudio("phi4", machine: "")], FootprintCatalog.Default);

        Assert.Equal(["qwen3:14b"], usable.Select(m => m.ModelId));
    }

    [Fact]
    public void AddingTheServedModelsTwiceKeepsOneOptionEach()
    {
        ServedModel[] served = [LmStudio("qwen3-32b", 19.8), LmStudio("llama3.3:70b", 42.5)];
        var once = FootprintCatalog.Default.WithServed(served);
        var twice = once.WithServed(served);

        Assert.Same(once, twice);
        Assert.Equal(2, once.Options.Count(o => ServedModels.IsServed(o)));
        // The review asks for today's choice with a catalog that already has the served models.
        var request = Alone(Nvidia(32), ServedModels.OptionId("qwen3-32b"), served);
        Assert.Equal(ServedModels.OptionId("qwen3-32b"), NetworkRecommender.TodayChoice(request, ClusterJobs.Thinking, once));
    }

    [Fact]
    public void AModelTheOwnerAlreadyRunsBecomesThinkingOnAPcAlone()
    {
        var request = Alone(Nvidia(24), "gemma4:e2b", LmStudio("qwen3-14b", 9), LmStudio("nomic-embed-text-v1.5", 0.3));

        var recommendation = NetworkRecommender.Recommend(request);

        var thinking = recommendation.Target.Job(ClusterJobs.Thinking)!;
        Assert.Equal("served:qwen3-14b", thinking.OptionId);
        var change = recommendation.Changes.Single(c => c.Kind == SetupChangeKind.AssignJob && c.Job == ClusterJobs.Thinking);
        Assert.Equal("served:qwen3-14b", change.OptionId);
        Assert.Contains("LM Studio", change.Why);
        var card = recommendation.Target.Machine("pc")!.Usage!.Gpus[0];
        Assert.True(card.Vram.Used <= card.Vram.Capacity, $"{card.Vram.Used} > {card.Vram.Capacity}");
        Assert.True(NetworkRecommender.Recommend(Apply(request, recommendation)).AlreadyOptimal);
    }

    [Fact]
    public void TheBiggestServedModelThatFitsWins()
    {
        var request = Alone(Nvidia(32), "gemma4:e2b", LmStudio("qwen3:8b"), LmStudio("qwen3:32b"), LmStudio("llama3.3:70b"));

        var recommendation = NetworkRecommender.Recommend(request);

        Assert.Equal("served:qwen3:32b", recommendation.Target.Job(ClusterJobs.Thinking)!.OptionId);
    }

    [Fact]
    public void AServedModelTooBigForTheCardLeavesMartletsModelAndANote()
    {
        var request = Alone(Nvidia(8), "gemma4:e2b", LmStudio("llama3.3:70b"));

        var recommendation = NetworkRecommender.Recommend(request);

        Assert.Equal("gemma4:e2b", recommendation.Target.Job(ClusterJobs.Thinking)!.OptionId);
        Assert.Contains(recommendation.Notes, n => n.Contains("llama3.3:70b", StringComparison.Ordinal) && n.Contains("no graphics card", StringComparison.Ordinal));
    }

    [Fact]
    public void AHostedProviderTheOwnerChoseStays()
    {
        var request = Alone(Nvidia(24), "hosted:openai", LmStudio("qwen3:14b"));

        var recommendation = NetworkRecommender.Recommend(request);

        Assert.Equal("hosted:openai", recommendation.Target.Job(ClusterJobs.Thinking)!.OptionId);
        Assert.Contains(recommendation.Notes, n => n.Contains("Companion › Thinking", StringComparison.Ordinal));
    }

    [Fact]
    public void TodaysServedModelStaysWithoutTheAppsList()
    {
        var request = Alone(Nvidia(24), "served:qwen3:14b");

        var recommendation = NetworkRecommender.Recommend(request);

        Assert.Equal("served:qwen3:14b", recommendation.Target.Job(ClusterJobs.Thinking)!.OptionId);
        Assert.DoesNotContain(recommendation.Changes, c => c.Job == ClusterJobs.Thinking);
    }

    [Fact]
    public void NoServedModelsChangeNothing()
    {
        var request = Alone(Nvidia(24), "gemma4:e2b");

        Assert.Equal(
            NetworkRecommender.Recommend(request).Target.Jobs.Select(j => j.OptionId),
            NetworkRecommender.Recommend(request with { ServedModels = [LmStudio("text-embedding-3")] }).Target.Jobs.Select(j => j.OptionId));
    }
    [Fact]
    public void A_smarter_model_an_app_on_a_host_serves_takes_that_hosts_thinking_pool_place()
    {
        NetworkSetupRequest Request(string? deep, params ServedModel[] served) => new(
        [
            new NetworkMachine(new MachineSpecs("pc", "pc") { RamGb = 32, CpuThreads = 16, IsPrimary = true }, NetworkMachineKind.Companion),
            new NetworkMachine(new MachineSpecs("lab", "lab") { Gpus = [Nvidia(8)], RamGb = 128, CpuThreads = 32, Platform = "linux" },
                NetworkMachineKind.Host)
            {
                HasHostService = true, Roles = deep is null ? [] : [new HostedRolePlacement("deep-thinking", deep, null)]
            }
        ]) { VoiceEngine = "chatterbox", Wanted = [PlanComponent.DeepThinking], ServedModels = served };
        // gpt-oss-120b (in main memory) is smarter than any Thinking pool model an 8 GB card holds.
        var big = LmStudio("openai/gpt-oss-120b", 63.4, machine: "lab") with { LoadedGb = 0 };
        var models = ModelCatalog.Build(ModelCatalogStore.Snapshot());
        var catalog = models.Models.Count == 0 ? FootprintCatalog.Default : FootprintCatalog.FromModels(models);

        var recommendation = NetworkRecommender.Recommend(Request("gemma4:e4b", big), catalog);
        var change = Assert.Single(recommendation.Changes, c => c.RoleKind == NetworkRecommender.AppDeepThinkingRole);
        Assert.Equal((SetupChangeKind.AddRole, "lab", "openai/gpt-oss-120b", "gemma4:e4b"), (change.Kind, change.MachineId, change.Model, change.FromModel));
        Assert.Equal(new Dictionary<string, string> { ["APP_PORT"] = "1234", ["APP_PATH"] = "/v1" }, change.Choices);
        Assert.DoesNotContain(recommendation.Changes, c => c.RoleKind == "deep-thinking");
        Assert.Contains("lab", recommendation.Target.ThinkingPool);
        Assert.Contains("openai/gpt-oss-120b in LM Studio on lab", recommendation.Components.Single(c => c.Component == PlanComponent.DeepThinking).Where);

        // Once the host relays to it (its Deep thinking route runs the app's model), it stays: no change, also with a smarter model.
        var kept = NetworkRecommender.Recommend(Request("openai/gpt-oss-120b", big), catalog);
        Assert.DoesNotContain(kept.Changes, c => c.RoleKind is "deep-thinking" or NetworkRecommender.AppDeepThinkingRole);
        // A model that isn't smarter than what Martlet runs there, a model on another computer, or an embedding model: no relay.
        Assert.DoesNotContain(NetworkRecommender.Recommend(Request("gemma4:e4b", LmStudio("llama3.2:1b", 1.3, machine: "lab")), catalog).Changes,
            c => c.RoleKind == NetworkRecommender.AppDeepThinkingRole);
        Assert.DoesNotContain(NetworkRecommender.Recommend(Request("gemma4:e4b", big with { MachineId = "pc" }), catalog).Changes,
            c => c.RoleKind == NetworkRecommender.AppDeepThinkingRole);
        Assert.DoesNotContain(NetworkRecommender.Recommend(Request("gemma4:e4b", LmStudio("text-embedding-3-large", 1, machine: "lab")), catalog).Changes,
            c => c.RoleKind == NetworkRecommender.AppDeepThinkingRole);

        // The executor installs it with the port and path the change names, and the model through the open model choice.
        var needs = new SetupRoleNeeds("deep-thinking-app", "")
        {
            Choices =
            [
                new("APP_PORT", ["1234", "8080"], "1234"), new("APP_PATH", ["/v1", "/api/v1"], "/v1"),
                new("APP_MODEL", ["model"], "model") { Open = true }
            ]
        };
        var (arguments, problem) = SetupExecutor.Arguments(change, needs, null);
        Assert.Null(problem);
        Assert.Equal(("1234", "/v1", "openai/gpt-oss-120b"),
            (arguments["choice.APP_PORT"], arguments["choice.APP_PATH"], arguments["choice.APP_MODEL"]));
        Assert.Contains("doesn't offer 9999", SetupExecutor.Arguments(change with { Choices = new Dictionary<string, string> { ["APP_PORT"] = "9999" } }, needs, null).Problem);
    }
}