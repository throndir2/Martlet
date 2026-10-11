using Martlet.Core.Cluster;
using Martlet.Core.Planning;

namespace Martlet.Core.Tests.Planning;

public sealed class RecommendationPreferencesTests
{
    private static MachineGpu Nvidia(double gb) => new($"RTX {gb} GB", GpuVendor.Nvidia, gb);

    private static NetworkMachine Host(string id, params MachineGpu[] gpus) =>
        new(new MachineSpecs(id, id) { Gpus = gpus, RamGb = 64, CpuThreads = 16, Platform = "linux" }, NetworkMachineKind.Host) { HasHostService = true };

    private static NetworkMachine Companion(string id, bool hostService = false, params MachineGpu[] gpus) =>
        new(new MachineSpecs(id, id) { Gpus = gpus, RamGb = 32, CpuThreads = 16, IsPrimary = true }, NetworkMachineKind.Companion)
        {
            HasHostService = hostService
        };

    private static NetworkSetupRequest Network(params NetworkMachine[] machines) => new(machines) { VoiceEngine = "chatterbox" };

    private static PlacementPlan Pc(RecommendationPreferences preferences, double vramGb, params string[] keys) => PlacementEngine.Plan(
        new PlanRequest([MachineSpecs.ThisPc(vramGb > 0 ? [Nvidia(vramGb)] : [], 64, 24)])
        {
            Preference = preferences.Hosting, Quality = preferences.Quality, PreferHearing = preferences.PreferHearing, ConfiguredProviders = keys
        });

    [Fact]
    public void TheDefaultsAreTheDesignsAndTheyTravelAsCanonicalJson()
    {
        var defaults = new RecommendationPreferences();
        Assert.Equal(ReplyQuality.Balanced, defaults.Quality);
        Assert.Equal(OnlineServices.Backup, defaults.Online);
        Assert.True(defaults.PreferHearing);
        Assert.Equal(90, defaults.HostGpuShare);
        Assert.True(defaults.UseServedModels);
        Assert.False(defaults.PreferHostModels);
        Assert.True(defaults.IsDefault);
        Assert.Equal(400, defaults.FirstWordTargetMs);
        Assert.Equal(HostingPreference.Backup, defaults.Hosting);
        Assert.Equal(HostingPreference.PreferLocal, (defaults with { Online = OnlineServices.Never }).Hosting);
        Assert.Equal(HostingPreference.Balanced, (defaults with { Online = OnlineServices.Yes }).Hosting);
        Assert.Equal(250, (defaults with { Quality = ReplyQuality.Quick }).FirstWordTargetMs);
        Assert.Equal(1000, (defaults with { Quality = ReplyQuality.Smarter }).FirstWordTargetMs);

        var chosen = defaults with { Quality = ReplyQuality.Smarter, Online = OnlineServices.Yes, HostGpuShare = 75, PreferHearing = false };
        chosen = chosen.WithGames("pc-b", true).WithGames("pc-a", false).WithGames("pc-b", false);
        Assert.Equal(false, chosen.PlaysGames("pc-b"));
        Assert.Null(chosen.PlaysGames("pc-c"));
        Assert.Equal(["pc-a", "pc-b"], chosen.Games.Select(g => g.Device));
        Assert.Contains("\"quality\":\"smarter\"", chosen.Share(), StringComparison.Ordinal);
        Assert.Equal(chosen.Share(), RecommendationPreferences.Parse(chosen.Share())!.Share());
        Assert.Equal(0.75, chosen.HostGpuFraction);
        // A newer Martlet's choice isn't read.
        Assert.Null(RecommendationPreferences.Parse(chosen.Share().Replace("smarter", "fastest", StringComparison.Ordinal)));
        Assert.Null(RecommendationPreferences.Parse(chosen.Share().Replace("75", "60", StringComparison.Ordinal)));
    }

    [Fact]
    public void TheyAreSavedAndTakeTheOldModelChoicesUntilThen()
    {
        var directory = Path.Combine(Path.GetTempPath(), "martlet-preferences-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.False(RecommendationPreferences.Saved(directory));
            Assert.True(RecommendationPreferences.Load(directory).IsDefault);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "recommended-setup.json"), """{ "UseServedModels": false, "PreferHostModels": true }""");
            var legacy = RecommendationPreferences.Load(directory);
            Assert.False(legacy.UseServedModels);
            Assert.True(legacy.PreferHostModels);

            Assert.True((legacy with { Quality = ReplyQuality.Quick }).WithGames("desk", true).Save(directory));
            Assert.True(RecommendationPreferences.Saved(directory));
            var saved = RecommendationPreferences.Load(directory);
            Assert.Equal(ReplyQuality.Quick, saved.Quality);
            Assert.True(saved.PlaysGames("desk"));
            Assert.False(saved.UseServedModels);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void LiveThinkingTakesTheSmartestModelWithinTheTargetAndAModelThatHearsWithinOneStep()
    {
        var local = FootprintCatalog.Default.For(PlanComponent.Thinking).Where(o => o.IsLocal).ToList();
        string First(ReplyQuality? quality, bool hearing = true) => LiveThinking.Order(local, quality, hearing).First().Id;

        Assert.Equal("gemma4:e2b", First(null));
        Assert.Equal("gemma4:e2b", First(ReplyQuality.Quick));
        Assert.Equal("gemma4:12b", First(ReplyQuality.Balanced));
        // Smarter: Gemma 4 12B hears, so it wins over Gemma 4 26B one step smarter; without the hearing preference 26B wins.
        Assert.Equal("gemma4:12b", First(ReplyQuality.Smarter));
        Assert.Equal("gemma4:26b", First(ReplyQuality.Smarter, hearing: false));
        // The processor comes last, and a model that misses the target comes after the ones that meet it, fastest first.
        Assert.Equal("gemma4:e2b-cpu", LiveThinking.Order(local, ReplyQuality.Quick).Last().Id);
        Assert.Equal(["gemma4:e2b", "gemma4:e4b", "qwen3.5:4b", "gemma4:12b", "gemma4:26b"],
            LiveThinking.Order(local, ReplyQuality.Quick).Where(o => o.UsesGpu).Select(o => o.Id));
    }

    [Fact]
    public void APcAloneThinksWithTheSmartestModelThatLeavesRoomForTheVoice()
    {
        var balanced = Pc(new RecommendationPreferences(), 12);
        Assert.Equal("gemma4:e4b", balanced.Primary(PlanComponent.Thinking)!.Option.Id);
        Assert.Equal("chatterbox-turbo", balanced.Primary(PlanComponent.Voice)!.Option.Id);
        Assert.Contains("within 0.4 s", balanced.Primary(PlanComponent.Thinking)!.Why, StringComparison.Ordinal);
        Assert.Equal("gemma4:e2b", Pc(new RecommendationPreferences { Quality = ReplyQuality.Quick }, 12).Primary(PlanComponent.Thinking)!.Option.Id);
        var smarter = new RecommendationPreferences { Quality = ReplyQuality.Smarter };
        Assert.Equal("gemma4:12b", Pc(smarter, 32).Primary(PlanComponent.Thinking)!.Option.Id);
        Assert.Equal("gemma4:26b", Pc(smarter with { PreferHearing = false }, 32).Primary(PlanComponent.Thinking)!.Option.Id);
        Assert.Equal("chatterbox-turbo", Pc(smarter with { PreferHearing = false }, 32).Primary(PlanComponent.Voice)!.Option.Id);
    }

    [Fact]
    public void OnlineServicesDecideWhatMayGoOnline()
    {
        var backup = Pc(new RecommendationPreferences(), 0, "nvidia-build");
        Assert.Equal("gemma4:e2b-cpu", backup.Primary(PlanComponent.Thinking)!.Option.Id);
        Assert.True(backup.Primary(PlanComponent.DeepThinking)!.IsExternal);

        var never = Pc(new RecommendationPreferences { Online = OnlineServices.Never }, 0, "nvidia-build");
        Assert.Equal("gemma4:e2b-cpu", never.Primary(PlanComponent.Thinking)!.Option.Id);
        Assert.Contains(never.Dropped, d => d.Component == PlanComponent.DeepThinking && d.Reason == DropReason.KeptLocal);

        var yes = Pc(new RecommendationPreferences { Online = OnlineServices.Yes }, 0, "nvidia-build");
        Assert.Equal("hosted:nvidia-build", yes.Primary(PlanComponent.Thinking)!.Option.Id);

        // The same in the network recommender: a companion PC alone with no graphics card and a saved free key.
        var request = Network(Companion("c1", true)) with { ConfiguredProviders = ["nvidia-build"] };
        Assert.Equal("gemma4:e2b-cpu", NetworkRecommender.Recommend(request.With(new RecommendationPreferences())).Target.Job(ClusterJobs.Thinking)!.OptionId);
        Assert.Equal("hosted:nvidia-build",
            NetworkRecommender.Recommend(request.With(new RecommendationPreferences { Online = OnlineServices.Yes })).Target.Job(ClusterJobs.Thinking)!.OptionId);
    }

    [Fact]
    public void ACompanionPcAloneThinksWithTheReplyQualitysModelBesideTheVoice()
    {
        var request = Network(Companion("c1", true, Nvidia(12)));
        var balanced = NetworkRecommender.Recommend(request.With(new RecommendationPreferences()));
        Assert.Equal("gemma4:e4b", balanced.Target.Job(ClusterJobs.Thinking)!.OptionId);
        Assert.Contains(balanced.Target.Machine("c1")!.Roles, r => r.Kind == "chatterbox");
        var quick = NetworkRecommender.Recommend(request.With(new RecommendationPreferences { Quality = ReplyQuality.Quick }));
        Assert.Equal("gemma4:e2b", quick.Target.Job(ClusterJobs.Thinking)!.OptionId);
    }

    [Fact]
    public void TheHostCardSharePlansWithLessOfEachHostsCard()
    {
        var card = Nvidia(24);
        Assert.Equal(21.6, PlacementEngine.GpuCapacityGb(card));
        Assert.Equal(12, PlacementEngine.GpuCapacityGb(card, share: 0.5));
        Assert.Equal(0, PlacementEngine.GpuCapacityGb(card, keepForGames: true));

        var request = Network(Companion("c1"), Host("h1", card));
        Assert.Equal(21.6, NetworkRecommender.Recommend(request).Target.Machine("h1")!.Usage!.Gpus[0].Vram.Capacity);
        var half = NetworkRecommender.Recommend(request.With(new RecommendationPreferences { HostGpuShare = 50 }));
        var usage = half.Target.Machine("h1")!.Usage!.Gpus[0].Vram;
        Assert.Equal(12, usage.Capacity);
        Assert.True(usage.Used <= usage.Capacity);
    }

    [Fact]
    public void APcThatGamesKeepsItsCardForTheLiveJobs()
    {
        var spec = MachineSpecs.ThisPc([Nvidia(24)], 64, 24);
        var plain = PlacementEngine.Plan(new PlanRequest([spec]) { Preference = HostingPreference.PreferLocal });
        Assert.Equal("audio2face-3d", plain.Primary(PlanComponent.LipSync)!.Option.Id);
        var gaming = PlacementEngine.Plan(new PlanRequest([spec with { KeepGpuForGames = true }]) { Preference = HostingPreference.PreferLocal });
        // The normal plan still thinks and speaks on the card (no added latency), but nothing else uses it.
        Assert.Equal(0, gaming.Primary(PlanComponent.Thinking)!.GpuIndex);
        Assert.Equal(0, gaming.Primary(PlanComponent.Voice)!.GpuIndex);
        Assert.NotEqual("audio2face-3d", gaming.Primary(PlanComponent.LipSync)?.Option.Id);
        Assert.Contains(gaming.Notes, n => n.Contains("You play games on This PC", StringComparison.Ordinal));

        // With two companion PCs and no host, the one that doesn't game thinks for both.
        var request = Network(Companion("c1", true, Nvidia(12)), Companion("c2", true, Nvidia(12))) with { Preference = HostingPreference.PreferLocal };
        Assert.Equal("c1", NetworkRecommender.Recommend(request).Target.Job(ClusterJobs.Thinking)!.HostId);
        var c1Games = request with { Machines = [request.Machines[0] with { Specs = request.Machines[0].Specs with { KeepGpuForGames = true } }, request.Machines[1]] };
        Assert.Equal("c2", NetworkRecommender.Recommend(c1Games).Target.Job(ClusterJobs.Thinking)!.HostId);
    }

    [Fact]
    public void WithAHostThatAnswersACompanionPcRunsNoModels()
    {
        // Its Deep thinking goes although the host can't think in the background (no graphics card).
        var deep = NetworkRecommender.Recommend(Network(Companion("c1", true, Nvidia(12)) with { Roles = [new("deep-thinking", "gemma4:e4b", 0)] }, Host("h1"))
            with { Preference = HostingPreference.PreferLocal });
        Assert.Contains(deep.Changes, c => c.Kind == SetupChangeKind.RemoveRole && c.MachineId == "c1" && c.RoleKind == "deep-thinking");

        // Its own Thinking moves to a host that can't run the same model but one as fast (rule 7 still holds).
        var request = Network(Companion("c1", false, Nvidia(12)), Host("h1", Nvidia(5))) with
        {
            Preference = HostingPreference.PreferLocal, CurrentJobs = [new JobPlan(ClusterJobs.Thinking, null, OptionId: "gemma4:e4b")]
        };
        var thinking = NetworkRecommender.Recommend(request).Target.Job(ClusterJobs.Thinking)!;
        Assert.Equal("h1", thinking.HostId);
        Assert.Equal("gemma4:e2b", thinking.OptionId);
    }
}
