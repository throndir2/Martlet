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
}
