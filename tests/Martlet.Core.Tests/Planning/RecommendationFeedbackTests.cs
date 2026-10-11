using Martlet.Core.Cluster;
using Martlet.Core.Planning;
using Martlet.Core.Settings;

namespace Martlet.Core.Tests.Planning;

/// <summary>Stage 3b of the recommendation design (docs/RECOMMENDATION_DESIGN.md): measured numbers replace estimates, job locks,
/// suggestions and A better setup is available.</summary>
public sealed class RecommendationFeedbackTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    private static MachineGpu Nvidia(double gb) => new($"RTX {gb} GB", GpuVendor.Nvidia, gb);

    private static NetworkMachine Host(string id, params MachineGpu[] gpus) =>
        new(new MachineSpecs(id, id) { Gpus = gpus, RamGb = 64, CpuThreads = 16, Platform = "linux" }, NetworkMachineKind.Host) { HasHostService = true };

    private static NetworkMachine Companion(string id) =>
        new(new MachineSpecs(id, id) { RamGb = 32, CpuThreads = 16, IsPrimary = true }, NetworkMachineKind.Companion);

    /// <summary>A companion PC and gpu-box (an RTX 4090) that thinks with <paramref name="model"/> today.</summary>
    private static NetworkSetupRequest ThinksWith(string model) =>
        new([Companion("c1"), Host("gpu-box", Nvidia(24)) with { Roles = [new("ollama", model, 0)] }])
        {
            VoiceEngine = "chatterbox", CurrentJobs = [new JobPlan(ClusterJobs.Thinking, "gpu-box", OptionId: model)]
        };

    private static string Temp() => Path.Combine(Path.GetTempPath(), "martlet-feedback-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void MeasuredFirstWordsKeepTheLastRepliesAndTheirMiddle()
    {
        var speed = new MeasuredFirstWords();
        foreach (var ms in new[] { 9000, 210, 190, 230, 200 }) speed = speed.With("http://127.0.0.1:11434/v1", "gemma4:e4b", ms, Now);
        var found = speed.Find("http://127.0.0.1:11434", "gemma4:e4b")!;
        Assert.Equal("http://127.0.0.1:11434", found.Host);
        // The first reply after the model loaded is seconds late; the middle of the replies isn't.
        Assert.Equal(210, found.Ms);
        for (var i = 0; i < 20; i++) speed = speed.With("http://127.0.0.1:11434", "gemma4:e4b", 300, Now);
        Assert.Equal(MeasuredFirstWords.MaximumSamples, speed.Find("http://127.0.0.1:11434", "gemma4:e4b")!.Samples.Count);
        // A time that is no first word, or a server that is no address, changes nothing.
        Assert.Same(speed, speed.With("http://127.0.0.1:11434", "gemma4:e4b", 0, Now));
        Assert.Same(speed, speed.With("http://127.0.0.1:11434", "gemma4:e4b", MeasuredFirstWords.MaximumMs + 1, Now));
        Assert.Same(speed, speed.With("not a url", "gemma4:e4b", 300, Now));

        var directory = Temp();
        try
        {
            Assert.True(MeasuredFirstWords.Record(directory, "https://gpu-box:8443", "gemma4:12b", 380, Now));
            Assert.True(MeasuredFirstWords.Record(directory, "https://gpu-box:8443", "gemma4:12b", 420, Now));
            Assert.Equal(400, MeasuredFirstWords.Load(directory).Find("https://gpu-box:8443", "gemma4:12b")!.Ms);
            File.WriteAllText(Path.Combine(directory, MeasuredFirstWords.FileName), "{ not json");
            Assert.Empty(MeasuredFirstWords.Load(directory).Models);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void MeasuredNumbersReplaceTheEstimates()
    {
        var speed = new MeasuredFirstWords()
            .With("http://127.0.0.1:11434", "gemma4:e4b", 260, Now)
            .With("https://gpu-box:8443", "gemma4:e2b", 2900, Now)
            .With(ChatCompletionsEndpointCatalog.NvidiaBuildBaseUrl, ChatCompletionsEndpointCatalog.NvidiaBuildDefaultModelId, 700, Now);
        var memory = new MeasuredModelMemory()
            .With(new MeasuredModelUse { Host = "http://127.0.0.1:11434", Model = "gemma4:e4b", Bytes = 6_100_000_000, GraphicsBytes = 6_100_000_000, MeasuredAt = Now })
            // Ollama on gpu-box held none of Gemma 4 E2B on the graphics card: its time is the processor option's.
            .With(new MeasuredModelUse { Host = "https://gpu-box:8443", Model = "gemma4:e2b", Bytes = 7_000_000_000, GraphicsBytes = 0, MeasuredAt = Now });
        var catalog = FootprintCatalog.Default.WithMeasured(memory, speed);

        var e4b = catalog.Find("gemma4:e4b")!;
        Assert.Equal(260, e4b.FirstWordMs);
        Assert.Equal(6.1, e4b.GpuGb, 2);
        Assert.Equal(FootprintEvidence.Measured, e4b.Evidence);
        Assert.Contains("measured on http://127.0.0.1:11434", e4b.Source, StringComparison.Ordinal);
        Assert.Equal(2900, catalog.Find("gemma4:e2b-cpu")!.FirstWordMs);
        Assert.Equal(154, catalog.Find("gemma4:e2b")!.FirstWordMs);
        Assert.Equal(700, catalog.Find("hosted:nvidia-build")!.FirstWordMs);
        // Nothing measured: the same catalog.
        Assert.Same(FootprintCatalog.Default, FootprintCatalog.Default.WithMeasured(null, new MeasuredFirstWords()));

        var measured = Assert.Single(catalog.Measurements, m => m.OptionId == "gemma4:e4b");
        Assert.Equal(208, measured.EstimatedFirstWordMs);
        Assert.Contains("first word 0.26 s (estimate 0.21 s) from 1 reply", measured.Describe(), StringComparison.Ordinal);
        Assert.Equal(3, catalog.WithServed([new ServedModel("c1", "lmstudio", "LM Studio", "http://127.0.0.1:1234/v1", "qwen3-32b")]).Measurements.Count);
    }

    [Fact]
    public void JobsStayLockedUntilTheOwnerUnlocksThemAndLockAgainWhenChangedByHand()
    {
        var preferences = new RecommendationPreferences();
        Assert.Equal(JobLockState.Kept, preferences.LockState(ClusterJobs.Thinking, "gemma4:e2b"));
        Assert.True(preferences.IsLocked(ClusterJobs.Thinking, "gemma4:e2b"));

        var unlocked = preferences.WithLock(ClusterJobs.Thinking, locked: false, "gemma4:e2b");
        Assert.Equal(JobLockState.Unlocked, unlocked.LockState(ClusterJobs.Thinking, "gemma4:e2b"));
        // The owner chose another model on Companion › Thinking: their choice is locked again.
        Assert.Equal(JobLockState.ChangedByHand, unlocked.LockState(ClusterJobs.Thinking, "qwen3.5:4b"));
        // Reconfigure set Gemma 4 12B up: that is Martlet's choice, so the job stays unlocked with it.
        var setUp = unlocked.SetUp([new JobPlan(ClusterJobs.Thinking, "gpu-box", OptionId: "gemma4:12b"), new JobPlan(ClusterJobs.Listening, null, OptionId: "parakeet")]);
        Assert.Equal(JobLockState.Unlocked, setUp.LockState(ClusterJobs.Thinking, "gemma4:12b"));
        Assert.Equal(JobLockState.Kept, setUp.LockState(ClusterJobs.Listening, "parakeet"));
        Assert.Equal(JobLockState.Locked, setUp.WithLock(ClusterJobs.Thinking, locked: true, "gemma4:12b").LockState(ClusterJobs.Thinking, "gemma4:12b"));

        // The locks travel with the preferences; an older Martlet's preferences (no locks) still read.
        Assert.Equal(setUp.Share(), RecommendationPreferences.Parse(setUp.Share())!.Share());
        Assert.Contains("\"locks\":[{\"job\":\"thinking\",\"locked\":false,\"chose\":\"gemma4:12b\"}]", setUp.Share(), StringComparison.Ordinal);
        Assert.Empty(RecommendationPreferences.Parse(new RecommendationPreferences().Share().Replace(",\"locks\":[]", "", StringComparison.Ordinal))!.Locks);
        Assert.Null(RecommendationPreferences.Parse(setUp.Share().Replace("\"thinking\"", "\"singing\"", StringComparison.Ordinal)));

        Assert.Equal([ClusterJobs.Thinking], ThinksWith("gemma4:e2b").With(unlocked).Unlocked);
        Assert.Empty(ThinksWith("qwen3.5:4b").With(unlocked).Unlocked);
    }

    [Fact]
    public void ALockedJobKeepsItsModelAndShowsTheSuggestion()
    {
        var recommendation = NetworkRecommender.Recommend(ThinksWith("gemma4:e2b").With(new RecommendationPreferences()));
        Assert.Equal("gemma4:e2b", recommendation.Target.Job(ClusterJobs.Thinking)!.OptionId);
        Assert.DoesNotContain(recommendation.Changes, c => c.RoleKind == "ollama");
        var suggestion = Assert.Single(recommendation.Suggestions, s => s.Job == ClusterJobs.Thinking);
        Assert.Equal("gemma4:12b", suggestion.OptionId);
        Assert.Equal(BetterChoice.Smarter, suggestion.Kind);
        Assert.True(suggestion.Locked);
        Assert.False(suggestion.Applied);
        Assert.Equal("Thinking: Gemma 4 12B on gpu-box instead of Gemma 4 E2B on gpu-box: smarter, and its first word comes in about 0.4 s (today about 0.15 s).",
            suggestion.Text);
        Assert.True(recommendation.BetterSetupAvailable);
        Assert.Equal(16, recommendation.SuggestionsFingerprint.Length);
    }

    [Fact]
    public void AnUnlockedJobGetsTheBetterChoiceInTheRecommendation()
    {
        var request = ThinksWith("gemma4:e2b").With(new RecommendationPreferences().WithLock(ClusterJobs.Thinking, locked: false, "gemma4:e2b"));
        var recommendation = NetworkRecommender.Recommend(request);
        Assert.Equal("gemma4:12b", recommendation.Target.Job(ClusterJobs.Thinking)!.OptionId);
        var change = Assert.Single(recommendation.Changes, c => c.RoleKind == "ollama");
        Assert.Equal(SetupChangeKind.ChangeModel, change.Kind);
        Assert.Equal("gemma4:12b", change.Model);
        Assert.Equal(SetupChangeBenefit.Improvement, change.Benefit);
        Assert.True(Assert.Single(recommendation.Suggestions, s => s.Job == ClusterJobs.Thinking).Applied);
        Assert.True(recommendation.WorthAsking);
    }

    [Fact]
    public void TheSmartestModelIsNoSuggestionAndMeasuredTimesChooseTheSuggestion()
    {
        Assert.DoesNotContain(NetworkRecommender.Recommend(ThinksWith("gemma4:12b").With(new RecommendationPreferences())).Suggestions,
            s => s.Job == ClusterJobs.Thinking);

        // Gemma 4 12B's first word was measured at 0.9 s, later than Balanced's 0.4 s: Gemma 4 E4B is the suggestion instead.
        var speed = new MeasuredFirstWords();
        for (var i = 0; i < 3; i++) speed = speed.With("https://other:8443", "gemma4:12b", 900, Now);
        var catalog = FootprintCatalog.Default.WithMeasured(null, speed);
        var recommendation = NetworkRecommender.Recommend(ThinksWith("gemma4:e2b").With(new RecommendationPreferences()), catalog);
        Assert.Equal("gemma4:e4b", Assert.Single(recommendation.Suggestions, s => s.Job == ClusterJobs.Thinking).OptionId);
    }

    [Fact]
    public void AsSmartWithClearlyLessMemoryIsALighterSuggestion()
    {
        ComponentOption Model(string id, double gb) => FootprintCatalog.Default.Find("gemma4:12b")! with
        {
            Id = id, ModelId = id, DisplayName = id, Steady = new(gb, 1, 1, 8), Peak = new(gb, 1.5, 2, 8), FirstWordMs = 300, HearsAudio = false
        };
        var catalog = new FootprintCatalog(FootprintCatalog.Default.Options.Where(o => o.Component != PlanComponent.Thinking || !o.IsLocal)
            .Append(Model("heavy:12b", 12)).Append(Model("light:12b", 6)));
        var recommendation = NetworkRecommender.Recommend(ThinksWith("heavy:12b").With(new RecommendationPreferences()), catalog);
        var suggestion = Assert.Single(recommendation.Suggestions, s => s.Job == ClusterJobs.Thinking);
        Assert.Equal("light:12b", suggestion.OptionId);
        Assert.Equal(BetterChoice.Lighter, suggestion.Kind);
        Assert.Contains("as smart with about 6 GB less graphics memory", suggestion.Why, StringComparison.Ordinal);
    }
}
