using Martlet.Core.Cluster;
using Martlet.Core.Planning;

namespace Martlet.Core.Tests.Planning;

public sealed class NetworkRecommenderTests
{
    private static MachineGpu Nvidia(double gb, string? name = null) => new(name ?? $"RTX {gb} GB", GpuVendor.Nvidia, gb);

    private static NetworkMachine Host(string id, params MachineGpu[] gpus) =>
        new(new MachineSpecs(id, id) { Gpus = gpus, RamGb = 64, CpuThreads = 16, Platform = "linux" }, NetworkMachineKind.Host)
        {
            HasHostService = true
        };

    private static NetworkMachine WindowsHost(string id, params MachineGpu[] gpus) =>
        Host(id, gpus) with { Specs = Host(id, gpus).Specs with { Platform = "windows" }, OnWindows = true };

    private static NetworkMachine Companion(string id, bool hostService = false, params MachineGpu[] gpus) =>
        new(new MachineSpecs(id, id) { Gpus = gpus, RamGb = 32, CpuThreads = 16, IsPrimary = true }, NetworkMachineKind.Companion)
        {
            HasHostService = hostService
        };

    private static HostedRolePlacement Role(string kind, string? model = null, int? gpu = null) => new(kind, model, gpu);

    private static NetworkSetupRequest Network(params NetworkMachine[] machines) => new(machines) { VoiceEngine = "chatterbox" };

    /// <summary>Today's setup after applying <paramref name="recommendation"/> to <paramref name="request"/>.</summary>
    private static NetworkSetupRequest Apply(NetworkSetupRequest request, NetworkRecommendation recommendation) => request with
    {
        Machines = request.Machines.Select(m => m with { Roles = recommendation.Target.Machine(m.Specs.Id)?.Roles ?? m.Roles }).ToArray(),
        CurrentJobs = recommendation.Target.Jobs,
        CurrentThinkingPool = recommendation.Target.ThinkingPool
    };

    private static IReadOnlyList<HostedRolePlacement> RolesOf(NetworkRecommendation recommendation, string id) =>
        recommendation.Target.Machine(id)!.Roles;

    private static void AssertOneModelPerCard(NetworkRecommendation recommendation)
    {
        foreach (var machine in recommendation.Target.Machines)
        {
            var models = machine.Roles.Where(r => r.Kind is NetworkRecommender.ThinkingRole or NetworkRecommender.DeepThinkingRole).ToList();
            if (models.Count < 2) continue;
            Assert.All(models, r => Assert.NotNull(r.GpuIndex));
            Assert.Equal(models.Count, models.Select(r => r.GpuIndex).Distinct().Count());
        }
    }

    [Fact]
    public void TwoCompanionsAndTwoHostsKeepTheCompanionsLightAndFillThePools()
    {
        var request = Network(Companion("c1", true, Nvidia(12)), Companion("c2", true, Nvidia(12)), Host("h1", Nvidia(24)), Host("h2", Nvidia(16)));

        var recommendation = NetworkRecommender.Recommend(request);

        Assert.Empty(RolesOf(recommendation, "c1"));
        Assert.Empty(RolesOf(recommendation, "c2"));
        var speaking = recommendation.Target.Job(ClusterJobs.Speaking)!;
        var thinking = recommendation.Target.Job(ClusterJobs.Thinking)!;
        Assert.Contains(speaking.HostId, new[] { "h1", "h2" });
        Assert.Equal("gemma4:e2b", thinking.OptionId);
        Assert.NotEqual(speaking.HostId, thinking.HostId);
        // Thinking's card stays as idle as it can: lip-sync goes beside the voice instead.
        Assert.DoesNotContain(RolesOf(recommendation, thinking.HostId!), r => r.Kind == "audio2face");
        Assert.Equal(speaking.HostId, recommendation.Target.Job(ClusterJobs.LipSync)!.HostId);
        Assert.Single(speaking.Pool);
        Assert.Empty(thinking.Pool);
        Assert.NotEmpty(recommendation.Target.ThinkingPool);
        AssertOneModelPerCard(recommendation);
        Assert.DoesNotContain(recommendation.Changes, c => c.Kind is SetupChangeKind.JoinPool or SetupChangeKind.LeavePool && c.Job is null);
        Assert.True(recommendation.WorthAsking);
        Assert.Contains(recommendation.Changes, c => c.Kind == SetupChangeKind.AddRole && c.RoleKind == NetworkRecommender.DeepThinkingRole &&
            c.Summary.Contains("joins the Thinking pool by itself", StringComparison.Ordinal));
    }

    [Fact]
    public void ApplyingTheRecommendationLeavesNothingToChange()
    {
        var request = Network(Companion("c1", true, Nvidia(12)), Companion("c2", false), Host("h1", Nvidia(24)), Host("h2", Nvidia(12), Nvidia(12)));
        var first = NetworkRecommender.Recommend(request);

        var second = NetworkRecommender.Recommend(Apply(request, first));

        Assert.True(second.AlreadyOptimal, string.Join("\n", second.Changes.Select(c => c.Summary)));
        Assert.False(second.WorthAsking);
        Assert.Equal(first.Fingerprint, second.Fingerprint);
    }

    [Fact]
    public void TheSameNetworkInAnotherOrderGivesTheSameRecommendation()
    {
        NetworkMachine[] machines = [Companion("c1", true, Nvidia(8)), Host("h2", Nvidia(16)), Host("h1", Nvidia(16)), Companion("c2")];
        var forward = NetworkRecommender.Recommend(Network(machines));
        var backward = NetworkRecommender.Recommend(Network([.. machines.Reverse()]));

        Assert.Equal(forward.Fingerprint, backward.Fingerprint);
        Assert.Equal(forward.Changes.Select(c => c.Summary), backward.Changes.Select(c => c.Summary));
        Assert.Equal(16, forward.Fingerprint.Length);
        // h1 and h2 are the same: the lower id wins every tie.
        Assert.Equal("h1", forward.Target.Job(ClusterJobs.Speaking)!.HostId);
    }

    [Fact]
    public void AHostWithTwoNvidiaCardsPinsThinkingAndDeepThinkingToCardsOfTheirOwn()
    {
        var request = Network(Companion("c1"), Host("h1", Nvidia(24, "RTX 4090"), Nvidia(24, "RTX 3090")) with
        {
            Roles = [Role("ollama", "gemma4:e2b"), Role("deep-thinking", "gemma4:12b")]
        }) with
        {
            CurrentJobs = [new JobPlan(ClusterJobs.Thinking, "h1", OptionId: "gemma4:e2b")]
        };

        var recommendation = NetworkRecommender.Recommend(request);

        var roles = RolesOf(recommendation, "h1");
        var thinking = roles.Single(r => r.Kind == "ollama");
        var deep = roles.Single(r => r.Kind == "deep-thinking");
        Assert.NotNull(thinking.GpuIndex);
        Assert.NotNull(deep.GpuIndex);
        Assert.NotEqual(thinking.GpuIndex, deep.GpuIndex);
        Assert.Equal("gemma4:12b", deep.Model);
        Assert.Contains(recommendation.Changes, c => c.Kind == SetupChangeKind.MoveToGpu && c.RoleKind == "deep-thinking" &&
            c.Benefit == SetupChangeBenefit.Improvement);
        AssertOneModelPerCard(recommendation);
    }

    [Fact]
    public void ThinkingAndDeepThinkingOnOneCardAreSeparated()
    {
        var request = Network(Companion("c1"), Host("h1", Nvidia(24)) with
        {
            Roles = [Role("ollama", "gemma4:e2b"), Role("deep-thinking", "gemma4:e2b")]
        }, Host("h2", Nvidia(12))) with
        {
            CurrentJobs = [new JobPlan(ClusterJobs.Thinking, "h1", OptionId: "gemma4:e2b")],
            Wanted = [PlanComponent.Thinking, PlanComponent.DeepThinking]
        };

        var recommendation = NetworkRecommender.Recommend(request);

        Assert.DoesNotContain(RolesOf(recommendation, "h1"), r => r.Kind == "deep-thinking");
        Assert.Contains(RolesOf(recommendation, "h2"), r => r.Kind == "deep-thinking");
        Assert.Equal("h1", recommendation.Target.Job(ClusterJobs.Thinking)!.HostId);
        Assert.Contains(recommendation.Changes, c => c.Kind == SetupChangeKind.RemoveRole && c.MachineId == "h1" && c.RoleKind == "deep-thinking");
    }

    [Fact]
    public void AVoiceOnAWindowsHostGetsACardOfItsOwn()
    {
        var request = Network(Companion("c1"), WindowsHost("w1", Nvidia(12)) with { Roles = [Role("chatterbox", "chatterbox-turbo"), Role("stt", "large-v3-turbo")] },
            Host("l1", Nvidia(12))) with
        {
            CurrentJobs =
            [
                new JobPlan(ClusterJobs.Speaking, "w1", OptionId: "chatterbox-turbo"),
                new JobPlan(ClusterJobs.Listening, "w1", OptionId: "whisper-large-v3-turbo-cuda")
            ],
            Wanted = [PlanComponent.Voice, PlanComponent.Listening]
        };

        var recommendation = NetworkRecommender.Recommend(request);

        Assert.Equal("l1", recommendation.Target.Job(ClusterJobs.Speaking)!.HostId);
        Assert.Equal("w1", recommendation.Target.Job(ClusterJobs.Listening)!.HostId);
        Assert.DoesNotContain(RolesOf(recommendation, "w1"), r => r.Kind == "chatterbox");
        var move = recommendation.Changes.Single(c => c.Kind == SetupChangeKind.AssignJob && c.Job == ClusterJobs.Speaking);
        Assert.Equal(SetupChangeBenefit.Improvement, move.Benefit);
        Assert.Contains("Windows", move.Why, StringComparison.Ordinal);
    }

    [Fact]
    public void AVoiceOnALinuxHostSharingItsCardStaysWhereItIs()
    {
        var request = Network(Companion("c1"), Host("l2", Nvidia(12)) with { Roles = [Role("chatterbox", "chatterbox-turbo"), Role("stt", "large-v3-turbo")] },
            Host("l1", Nvidia(12))) with
        {
            CurrentJobs =
            [
                new JobPlan(ClusterJobs.Speaking, "l2", OptionId: "chatterbox-turbo"),
                new JobPlan(ClusterJobs.Listening, "l2", OptionId: "whisper-large-v3-turbo-cuda")
            ],
            Wanted = [PlanComponent.Voice, PlanComponent.Listening]
        };

        var recommendation = NetworkRecommender.Recommend(request);

        Assert.True(recommendation.AlreadyOptimal, string.Join("\n", recommendation.Changes.Select(c => c.Summary)));
    }

    [Fact]
    public void EveryComputerSpeaksWithTheOwnersVoiceEngine()
    {
        var request = Network(Companion("c1"), Host("h1", Nvidia(12)) with { Roles = [Role("f5", "f5tts-v1-base")] }, Host("h2", Nvidia(12))) with
        {
            CurrentJobs = [new JobPlan(ClusterJobs.Speaking, "h1", OptionId: "f5-tts")],
            Wanted = [PlanComponent.Voice]
        };

        var recommendation = NetworkRecommender.Recommend(request);

        var speaking = recommendation.Target.Job(ClusterJobs.Speaking)!;
        Assert.Equal("h1", speaking.HostId);
        Assert.Equal("chatterbox-turbo", speaking.OptionId);
        Assert.Equal(["chatterbox"], RolesOf(recommendation, "h1").Select(r => r.Kind));
        Assert.Contains(recommendation.Changes, c => c.Kind == SetupChangeKind.AddRole && c.RoleKind == "chatterbox" && c.MachineId == "h1");
        Assert.Contains(recommendation.Changes, c => c.Kind == SetupChangeKind.RemoveRole && c.RoleKind == "f5" && c.Why.Contains("one voice engine", StringComparison.Ordinal));
        Assert.DoesNotContain(recommendation.Changes, c => c.Kind == SetupChangeKind.AssignJob);
    }

    [Fact]
    public void NewRolesNeverPushTodaysThinkingOffItsCard()
    {
        // Thinking runs on h1's 8 GB card. The voice engine (planned first) has no room beside it, so Thinking stays and the
        // Windows voice stands in, instead of Thinking moving to a hosted provider the owner never chose.
        var request = Network(Companion("c1"), Host("h1", Nvidia(8)) with { Roles = [Role("ollama", "gemma4:e2b")] }) with
        {
            CurrentJobs =
            [
                new JobPlan(ClusterJobs.Thinking, "h1", OptionId: "gemma4:e2b"),
                new JobPlan(ClusterJobs.Speaking, null, OptionId: FootprintCatalog.WindowsVoiceId)
            ],
            Wanted = [PlanComponent.Thinking, PlanComponent.Voice]
        };

        var recommendation = NetworkRecommender.Recommend(request);

        Assert.Equal("h1", recommendation.Target.Job(ClusterJobs.Thinking)!.HostId);
        Assert.DoesNotContain(recommendation.Changes, c => c.Job == ClusterJobs.Thinking || c.RoleKind == "ollama");
        Assert.Contains(recommendation.Notes, n => n.Contains("No computer has room for Chatterbox Turbo", StringComparison.Ordinal));
    }

    [Fact]
    public void ThinkingInsideTheCompanionPcKeepsItsRoomOnTheCard()
    {
        var request = Network(Companion("this-pc", true, Nvidia(8))) with
        {
            CurrentJobs =
            [
                new JobPlan(ClusterJobs.Thinking, null, OptionId: "gemma4:e2b"),
                new JobPlan(ClusterJobs.Speaking, null, OptionId: FootprintCatalog.WindowsVoiceId)
            ],
            Wanted = [PlanComponent.Thinking, PlanComponent.Voice]
        };

        var recommendation = NetworkRecommender.Recommend(request);

        var card = recommendation.Target.Machine("this-pc")!.Usage!.Gpus[0];
        Assert.True(card.Vram.Used <= card.Vram.Capacity, $"{card.Vram.Used} > {card.Vram.Capacity}");
        Assert.Equal("gemma4:e2b", recommendation.Target.Job(ClusterJobs.Thinking)!.OptionId);
    }

    [Fact]
    public void ANewOptionWithoutAHostIsAChange()
    {
        var request = Network(Companion("this-pc", true, Nvidia(12))) with
        {
            Preference = HostingPreference.PreferLocal,
            CurrentJobs = [new JobPlan(ClusterJobs.Thinking, null, OptionId: "hosted:openai")],
            Wanted = [PlanComponent.Thinking]
        };

        var recommendation = NetworkRecommender.Recommend(request);

        var change = recommendation.Changes.Single(c => c.Kind == SetupChangeKind.AssignJob);
        Assert.Equal("", change.MachineId);
        Assert.Equal("gemma4:e2b", change.OptionId);
        Assert.Null(change.FromMachineId);
        Assert.True(recommendation.WorthAsking);
        Assert.True(NetworkRecommender.Recommend(Apply(request, recommendation)).AlreadyOptimal);
    }

    [Fact]
    public void KeepingEverythingLocalBesideAWindowsVoiceIsStable()
    {
        var request = Network(Companion("c1"), WindowsHost("win-box", Nvidia(12)), Host("amd-box", new MachineGpu("RX 7800 XT", GpuVendor.Amd, 16))) with
        {
            Preference = HostingPreference.PreferLocal,
            Wanted = [PlanComponent.Thinking, PlanComponent.Voice]
        };

        var first = NetworkRecommender.Recommend(request);
        var second = NetworkRecommender.Recommend(Apply(request, first));

        Assert.Equal("amd-box", first.Target.Job(ClusterJobs.Thinking)!.HostId);
        Assert.Equal("win-box", first.Target.Job(ClusterJobs.Speaking)!.HostId);
        Assert.True(second.AlreadyOptimal, string.Join("\n", second.Changes.Select(c => c.Summary)));
        Assert.Equal(first.Fingerprint, second.Fingerprint);
    }

    [Fact]
    public void AThinkingPoolModelThatIsntPlannedStaysInThePool()
    {
        var request = Network(Companion("c1"), Host("h1", Nvidia(16)) with { Roles = [Role("deep-thinking", "gemma4:e4b")] }) with
        {
            CurrentThinkingPool = ["h1"],
            Wanted = [PlanComponent.Voice]
        };

        var recommendation = NetworkRecommender.Recommend(request);

        Assert.Equal(["h1"], recommendation.Target.ThinkingPool);
        Assert.Contains(RolesOf(recommendation, "h1"), r => r.Kind == "deep-thinking");
    }

    [Fact]
    public void TheVoiceGetsTheRoomThinkingFreesWhenItMoves()
    {
        // Thinking in c1's own Ollama fills its 8 GB card with the voice; once Thinking moves to the AMD host, the voice
        // (which needs NVIDIA) takes c1's card in the same recommendation, not in a second one.
        var request = Network(Companion("c1", true, Nvidia(8, "RTX 4060")), Host("amd-box", new MachineGpu("RX 7600", GpuVendor.Amd, 8))) with
        {
            CurrentJobs =
            [
                new JobPlan(ClusterJobs.Thinking, null, OptionId: "gemma4:e2b"),
                new JobPlan(ClusterJobs.Speaking, null, OptionId: FootprintCatalog.WindowsVoiceId)
            ],
            Wanted = [PlanComponent.Thinking, PlanComponent.Voice]
        };

        var first = NetworkRecommender.Recommend(request);

        Assert.Equal("amd-box", first.Target.Job(ClusterJobs.Thinking)!.HostId);
        Assert.Equal("c1", first.Target.Job(ClusterJobs.Speaking)!.HostId);
        Assert.DoesNotContain(first.Notes, n => n.Contains("No computer has room", StringComparison.Ordinal));
        Assert.True(NetworkRecommender.Recommend(Apply(request, first)).AlreadyOptimal);
    }

    [Fact]
    public void LocalThinkingStaysOffAWindowsCardWithAPoolVoice()
    {
        var request = Network(Companion("c0"), Companion("c2"),
            Host("h2", Nvidia(8)) with { Roles = [Role("chatterbox", "chatterbox-turbo")] },
            WindowsHost("h4", Nvidia(24, "RTX 4090"), Nvidia(12, "RTX 3060")) with
            {
                Roles = [Role("chatterbox", "chatterbox-turbo", 0), Role("deep-thinking", "gemma4:e4b", 1)]
            }) with
        {
            Preference = HostingPreference.PreferLocal,
            ConfiguredProviders = ["openai"],
            CurrentJobs =
            [
                new JobPlan(ClusterJobs.Thinking, null, OptionId: "hosted:openai"),
                new JobPlan(ClusterJobs.Speaking, "h2", OptionId: "chatterbox-turbo") { Pool = ["h4"] }
            ],
            Wanted = [PlanComponent.Thinking, PlanComponent.Voice, PlanComponent.DeepThinking]
        };

        var first = NetworkRecommender.Recommend(request);
        var second = NetworkRecommender.Recommend(Apply(request, first));

        var h4 = RolesOf(first, "h4");
        Assert.NotEqual(h4.Single(r => r.Kind == "chatterbox").GpuIndex, h4.FirstOrDefault(r => r.Kind == "ollama")?.GpuIndex);
        Assert.True(second.AlreadyOptimal, string.Join("\n", second.Changes.Select(c => c.Summary)));
    }

    [Fact]
    public void AComputerAwayWithinTheGracePeriodIsPlannedAsIfItWereBack()
    {
        var request = Network(Companion("c1"), Host("h1", Nvidia(12)) with
        {
            Online = false, OfflineFor = TimeSpan.FromMinutes(4), Roles = [Role("chatterbox", "chatterbox-turbo")]
        }, Host("h2", Nvidia(12))) with
        {
            CurrentJobs = [new JobPlan(ClusterJobs.Speaking, "h1", OptionId: "chatterbox-turbo")],
            Wanted = [PlanComponent.Voice]
        };

        var recommendation = NetworkRecommender.Recommend(request);

        Assert.Equal("h1", recommendation.Target.Job(ClusterJobs.Speaking)!.HostId);
        Assert.DoesNotContain(recommendation.Changes, c => c.MachineId == "h1" || c.FromMachineId == "h1");
        Assert.True(recommendation.AlreadyOptimal);
        Assert.Contains(recommendation.Notes, n => n.Contains("h1 hasn't answered for 4 minutes", StringComparison.Ordinal));
    }

    [Fact]
    public void AComputerAwayLongerThanTheGracePeriodLosesItsJobs()
    {
        var request = Network(Companion("c1"), Companion("c2"), Host("h1", Nvidia(12)) with
        {
            Online = false, OfflineFor = TimeSpan.FromMinutes(25), Roles = [Role("chatterbox", "chatterbox-turbo")]
        }, Host("h2", Nvidia(12)) with { Roles = [Role("chatterbox", "chatterbox-turbo")] }) with
        {
            CurrentJobs = [new JobPlan(ClusterJobs.Speaking, "h1", OptionId: "chatterbox-turbo") { Pool = ["h2"] }],
            Wanted = [PlanComponent.Voice]
        };

        var recommendation = NetworkRecommender.Recommend(request);

        Assert.Equal("h2", recommendation.Target.Job(ClusterJobs.Speaking)!.HostId);
        var move = recommendation.Changes.Single(c => c.Kind == SetupChangeKind.AssignJob);
        Assert.Equal(SetupChangeBenefit.Required, move.Benefit);
        Assert.Equal("h1", move.FromMachineId);
        Assert.DoesNotContain(recommendation.Changes, c => c.MachineId == "h1" && c.Kind is SetupChangeKind.AddRole or SetupChangeKind.RemoveRole);
        Assert.Contains(recommendation.Notes, n => n.Contains("h1 hasn't answered for 25 minutes", StringComparison.Ordinal) && n.Contains("jobs move", StringComparison.Ordinal));
        Assert.True(recommendation.WorthAsking);
    }

    [Fact]
    public void AJobOnAComputerWithoutAReportStaysAsItIs()
    {
        var request = Network(Companion("c1"), Host("h1", Nvidia(12))) with
        {
            CurrentJobs = [new JobPlan(ClusterJobs.Speaking, "ghost", OptionId: "chatterbox-turbo") { Pool = ["ghost-2"] }],
            Wanted = [PlanComponent.Voice]
        };

        var recommendation = NetworkRecommender.Recommend(request);

        Assert.Equal("ghost", recommendation.Target.Job(ClusterJobs.Speaking)!.HostId);
        Assert.Equal(["ghost-2"], recommendation.Target.Job(ClusterJobs.Speaking)!.Pool);
        Assert.DoesNotContain(recommendation.Changes, c => c.Job == ClusterJobs.Speaking);
        Assert.Contains(recommendation.Notes, n => n.Contains("ghost does speaking today", StringComparison.Ordinal));
    }

    [Fact]
    public void ALiveJobNeverMovesToAPlaceWithALaterFirstWord()
    {
        // The companion PC runs Gemma 4 E2B on its own card (0.15 s); the only host has no graphics card, where the same model
        // would take about 2.5 s. Moving it would relieve the companion PC but add conversation latency, so it stays.
        var request = Network(Companion("c1", false, Nvidia(12)), Host("cpu-box")) with
        {
            CurrentJobs = [new JobPlan(ClusterJobs.Thinking, null, OptionId: "gemma4:e2b")],
            Wanted = [PlanComponent.Thinking]
        };

        var recommendation = NetworkRecommender.Recommend(request);

        var thinking = recommendation.Target.Job(ClusterJobs.Thinking)!;
        Assert.Null(thinking.HostId);
        Assert.Equal("gemma4:e2b", thinking.OptionId);
        Assert.True(recommendation.AlreadyOptimal, string.Join("\n", recommendation.Changes.Select(c => c.Summary)));
    }

    [Fact]
    public void AHostedThinkingProviderTheOwnerChoseStays()
    {
        var request = Network(Companion("c1"), Host("h1", Nvidia(24))) with
        {
            CurrentJobs = [new JobPlan(ClusterJobs.Thinking, null, OptionId: "hosted:openai")],
            ConfiguredProviders = ["openai"],
            Wanted = [PlanComponent.Thinking]
        };

        Assert.True(NetworkRecommender.Recommend(request).AlreadyOptimal);
        // Keeping everything local moves it to the host, where the first word comes sooner.
        var local = NetworkRecommender.Recommend(request with { Preference = HostingPreference.PreferLocal });
        Assert.Equal("h1", local.Target.Job(ClusterJobs.Thinking)!.HostId);
        Assert.Contains(local.Changes, c => c.Kind == SetupChangeKind.AssignJob && c.Job == ClusterJobs.Thinking && c.FromMachineId is null);
    }

    [Fact]
    public void OneCompanionPcAloneUsesItsOwnCardLikeThePlacementEngine()
    {
        var request = Network(Companion("this-pc", true, Nvidia(12))) with
        {
            Preference = HostingPreference.PreferLocal,
            Wanted = [PlanComponent.Thinking, PlanComponent.Voice, PlanComponent.Listening, PlanComponent.LipSync, PlanComponent.DeepThinking]
        };

        var recommendation = NetworkRecommender.Recommend(request);

        var thinking = recommendation.Target.Job(ClusterJobs.Thinking)!;
        Assert.Null(thinking.HostId);
        Assert.Equal("gemma4:e2b", thinking.OptionId);
        Assert.Equal("this-pc", recommendation.Target.Job(ClusterJobs.Speaking)!.HostId);
        Assert.Contains(RolesOf(recommendation, "this-pc"), r => r.Kind == "chatterbox");
        // Its one card runs Thinking's model already: no Deep thinking model beside it (one language model per card).
        Assert.DoesNotContain(RolesOf(recommendation, "this-pc"), r => r.Kind == "deep-thinking");
        var usage = recommendation.Target.Machine("this-pc")!.Usage!;
        Assert.InRange(usage.Gpus[0].Vram.Used, 0, usage.Gpus[0].Vram.Capacity);
    }

    [Fact]
    public void HeavyRolesLeaveACompanionPcWhenAHostCanTakeThem()
    {
        var request = Network(Companion("c1", true, Nvidia(12)) with
        {
            Roles = [Role("chatterbox", "chatterbox-turbo"), Role("ollama", "gemma4:e2b")]
        }, Host("h1", Nvidia(24))) with
        {
            CurrentJobs =
            [
                new JobPlan(ClusterJobs.Thinking, "c1", OptionId: "gemma4:e2b"),
                new JobPlan(ClusterJobs.Speaking, "c1", OptionId: "chatterbox-turbo")
            ],
            Wanted = [PlanComponent.Thinking, PlanComponent.Voice]
        };

        var recommendation = NetworkRecommender.Recommend(request);

        Assert.Empty(RolesOf(recommendation, "c1"));
        Assert.Equal("h1", recommendation.Target.Job(ClusterJobs.Thinking)!.HostId);
        Assert.Equal("h1", recommendation.Target.Job(ClusterJobs.Speaking)!.HostId);
        var changes = recommendation.Changes.ToList();
        Assert.All(changes.Where(c => c.MachineId == "c1"), c => Assert.Equal(SetupChangeKind.RemoveRole, c.Kind));
        Assert.All(changes, c => Assert.NotEqual(SetupChangeBenefit.Minor, c.Benefit));
        // Make before break: the host's roles first, then the jobs, then the companion's roles go.
        var lastAdd = changes.FindLastIndex(c => c.Kind == SetupChangeKind.AddRole);
        var firstAssign = changes.FindIndex(c => c.Kind == SetupChangeKind.AssignJob);
        var lastAssign = changes.FindLastIndex(c => c.Kind == SetupChangeKind.AssignJob);
        var firstRemove = changes.FindIndex(c => c.Kind == SetupChangeKind.RemoveRole);
        Assert.True(lastAdd < firstAssign && lastAssign < firstRemove);
        Assert.Contains(changes, c => c.Kind == SetupChangeKind.AddRole && c.DownloadGb > 0);
    }

    [Fact]
    public void ACompanionPcKeepsItsVoiceWhenNoHostCanRunIt()
    {
        var request = Network(Companion("c1", true, Nvidia(12)) with { Roles = [Role("chatterbox", "chatterbox-turbo")] }, Host("cpu-box")) with
        {
            CurrentJobs = [new JobPlan(ClusterJobs.Speaking, "c1", OptionId: "chatterbox-turbo")],
            Wanted = [PlanComponent.Voice]
        };

        Assert.True(NetworkRecommender.Recommend(request).AlreadyOptimal);
    }

    [Fact]
    public void DeepThinkingMovesToACardNoLiveJobUses()
    {
        var request = Network(Companion("c1"), Host("h1", Nvidia(16), Nvidia(16)) with
        {
            Roles = [Role("chatterbox", "chatterbox-turbo", 0), Role("deep-thinking", "gemma4:e4b", 0)]
        }) with
        {
            CurrentJobs = [new JobPlan(ClusterJobs.Speaking, "h1", OptionId: "chatterbox-turbo")],
            Wanted = [PlanComponent.Voice, PlanComponent.DeepThinking]
        };

        var recommendation = NetworkRecommender.Recommend(request);

        var roles = RolesOf(recommendation, "h1");
        Assert.Equal(0, roles.Single(r => r.Kind == "chatterbox").GpuIndex);
        Assert.Equal(1, roles.Single(r => r.Kind == "deep-thinking").GpuIndex);
        var move = recommendation.Changes.Single();
        Assert.Equal(SetupChangeKind.MoveToGpu, move.Kind);
        Assert.Equal(SetupChangeBenefit.Improvement, move.Benefit);
        Assert.Contains("no live job uses", move.Why, StringComparison.Ordinal);
    }

    [Fact]
    public void TheThinkingPoolSkipsHostsTheOwnerLeftOut()
    {
        var request = Network(Companion("c1"), Host("h1", Nvidia(16)), Host("h2", Nvidia(16))) with
        {
            Wanted = [PlanComponent.DeepThinking],
            ThinkingPoolOptOut = ["h1"]
        };

        var recommendation = NetworkRecommender.Recommend(request);

        Assert.Equal(["h2"], recommendation.Target.ThinkingPool);
        Assert.DoesNotContain(RolesOf(recommendation, "h1"), r => r.Kind == "deep-thinking");
        Assert.Equal("gemma4:12b", RolesOf(recommendation, "h2").Single(r => r.Kind == "deep-thinking").Model);
    }

    [Fact]
    public void ChangesOnAComputerMartletCantReachNeedSomeoneThere()
    {
        var request = Network(Companion("c1"), Host("h1", Nvidia(24)) with { Manageable = false }) with { Wanted = [PlanComponent.Voice] };

        var recommendation = NetworkRecommender.Recommend(request);

        var add = recommendation.Changes.Single(c => c.Kind == SetupChangeKind.AddRole);
        Assert.Equal("h1", add.MachineId);
        Assert.True(add.NeedsSomeoneThere);
        Assert.False(recommendation.Changes.Single(c => c.Kind == SetupChangeKind.AssignJob).NeedsSomeoneThere);
        Assert.Contains(recommendation.Notes, n => n.Contains("someone has to make its changes", StringComparison.Ordinal));
    }

    [Fact]
    public void TodayDescribesTheNetworkWithoutChangingIt()
    {
        var request = Network(Companion("c1"), Host("h1", Nvidia(12)) with { Roles = [Role("chatterbox", "chatterbox-turbo")] }) with
        {
            CurrentJobs = [new JobPlan(ClusterJobs.Speaking, "h1", OptionId: "chatterbox-turbo")]
        };

        var today = NetworkRecommender.Today(request);

        Assert.Equal(["chatterbox"], today.Machine("h1")!.Roles.Select(r => r.Kind));
        Assert.Equal("h1", today.Job(ClusterJobs.Speaking)!.HostId);
        Assert.Equal(4.2, today.Machine("h1")!.Usage!.Gpus[0].Vram.Used, 1);
    }

    [Fact]
    public void NothingIsEverPlannedOverACardsCapacity()
    {
        var request = Network(Companion("c1"), Companion("c2"), Companion("c3"), Host("h1", Nvidia(8)), Host("h2", Nvidia(12), Nvidia(6)),
            WindowsHost("w1", Nvidia(16))) with { Preference = HostingPreference.PreferLocal };

        var recommendation = NetworkRecommender.Recommend(request);

        foreach (var machine in recommendation.Target.Machines.Where(m => m.Usage is not null))
            Assert.All(machine.Usage!.Gpus, g => Assert.True(g.Vram.Used <= g.Vram.Capacity + 1e-6, $"{machine.MachineId} card {g.Index}: {g.Vram.Used} > {g.Vram.Capacity}"));
        AssertOneModelPerCard(recommendation);
        // One voice engine per computer.
        Assert.All(recommendation.Target.Machines, m => Assert.True(m.Roles.Count(r => r.Kind.StartsWith("chatterbox", StringComparison.Ordinal)) <= 1));
        // The Speaking pool has one place for each other companion PC at most among new places.
        Assert.InRange(recommendation.Target.Job(ClusterJobs.Speaking)!.Pool.Count, 0, 2);
    }
}
