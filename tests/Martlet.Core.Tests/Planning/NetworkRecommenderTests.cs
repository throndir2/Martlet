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
        // Thinking runs on h1's 8 GB card. The voice engine (planned first) has no room beside it, so Thinking stays and
        // Chatterbox Nano stands in, instead of Thinking moving to a hosted provider the owner never chose. Speaking names the
        // retired Windows voices option, as an older plan can.
        var request = Network(Companion("c1"), Host("h1", Nvidia(8)) with { Roles = [Role("ollama", "gemma4:e2b")] }) with
        {
            CurrentJobs =
            [
                new JobPlan(ClusterJobs.Thinking, "h1", OptionId: "gemma4:e2b"),
                new JobPlan(ClusterJobs.Speaking, null, OptionId: "windows-voices")
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
                new JobPlan(ClusterJobs.Speaking, null, OptionId: "windows-voices")
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
    public void KeepingEverythingLocalBesideAWindowsHostIsStable()
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
                new JobPlan(ClusterJobs.Speaking, null, OptionId: "windows-voices")
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

    private static NetworkSetupRequest LoneVoice(bool hostService, params MachineGpu[] gpus) =>
        Network(Companion("desk-1", hostService, gpus)) with { Wanted = [PlanComponent.Voice] };

    [Fact]
    public void ChatterboxNanoSpeaksOnACardWithNoRoomForTheOwnersEngineAndStaysWhenApplied()
    {
        var request = LoneVoice(true, Nvidia(4, "GTX 1650"));
        var plan = NetworkRecommender.Recommend(request);

        var speaking = plan.Target.Job(ClusterJobs.Speaking)!;
        Assert.Equal("desk-1", speaking.HostId);
        Assert.Equal(FootprintCatalog.FallbackVoiceKind, speaking.OptionId);
        Assert.Contains(plan.Notes, n => n.StartsWith("No computer has room for Chatterbox Turbo: Martlet speaks with Chatterbox Nano",
            StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Changes, c => c.Summary.Contains("Windows", StringComparison.Ordinal));
        Assert.True(NetworkRecommender.Recommend(Apply(request, plan)).AlreadyOptimal);
    }

    [Fact]
    public void ChatterboxNanoSpeaksOnTheProcessorWhenNoComputerHasACard()
    {
        var plan = NetworkRecommender.Recommend(LoneVoice(true));

        var speaking = plan.Target.Job(ClusterJobs.Speaking)!;
        Assert.Equal("desk-1", speaking.HostId);
        Assert.Equal("chatterbox-nano-cpu", speaking.OptionId);
        Assert.Contains(plan.Changes, c => c.Kind == SetupChangeKind.AddRole && c.Summary.Contains("desk-1's processor", StringComparison.Ordinal));
    }

    [Fact]
    public void AHostedVoiceSpeaksOnlyWithASavedKeyAndOtherwiseANoteSaysHowToGiveMartletAVoice()
    {
        var keyed = NetworkRecommender.Recommend(LoneVoice(false) with { ConfiguredProviders = ["openai"] });
        Assert.Equal(FootprintCatalog.OpenAiVoiceId, keyed.Target.Job(ClusterJobs.Speaking)!.OptionId);
        Assert.False(keyed.CannotSpeak);

        var mute = NetworkRecommender.Recommend(LoneVoice(false));
        Assert.Null(mute.Target.Job(ClusterJobs.Speaking)?.OptionId);
        Assert.Contains(mute.Notes, n => n.StartsWith("Martlet can't speak yet", StringComparison.Ordinal) &&
            n.Contains("host service", StringComparison.Ordinal) && n.Contains("Chatterbox Nano", StringComparison.Ordinal));
        Assert.True(mute.CannotSpeak);
        Assert.Contains(mute.CannotSpeakNote!, mute.Notes);
        Assert.False(NetworkRecommender.Recommend(LoneVoice(true)).CannotSpeak);
    }

    [Fact]
    public void TheOwnersVoiceEngineComesBackWhenAComputerHasRoomForItAgain()
    {
        var small = LoneVoice(true, Nvidia(4, "GTX 1650"));
        var applied = Apply(small, NetworkRecommender.Recommend(small));
        var desk = applied.Machines.Single();
        var upgraded = applied with { Machines = [desk with { Specs = desk.Specs with { Gpus = [Nvidia(12)] } }] };

        var plan = NetworkRecommender.Recommend(upgraded);

        Assert.Equal("chatterbox-turbo", plan.Target.Job(ClusterJobs.Speaking)!.OptionId);
        Assert.DoesNotContain(plan.Target.Machine("desk-1")!.Roles, r => r.Kind == FootprintCatalog.FallbackVoiceKind);
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

    /// <summary>The owner's report: one companion PC (a 12 GB card by default) runs Singing and a Listening pool place, and
    /// both hosts (Thinking, the voice and lip-sync on miku; Listening on imouto) haven't answered for 155 minutes.</summary>
    private static NetworkSetupRequest HostsGoneForHours(double cardGb = 12) => Network(
        Companion("this-pc", true, Nvidia(cardGb)) with { Roles = [Role("singing", "ace-step-v15-soulx-svc"), Role("stt", "large-v3-turbo")] },
        Host("miku", Nvidia(16)) with
        {
            Online = false, OfflineFor = TimeSpan.FromMinutes(155),
            Roles = [Role("ollama", "gemma4:e2b"), Role("chatterbox", "chatterbox-turbo"), Role("audio2face")]
        },
        Host("imouto", Nvidia(12)) with { Online = false, OfflineFor = TimeSpan.FromMinutes(155), Roles = [Role("stt", "large-v3-turbo")] }) with
    {
        CurrentJobs =
        [
            new JobPlan(ClusterJobs.Thinking, "miku", OptionId: "gemma4:e2b"),
            new JobPlan(ClusterJobs.Speaking, "miku", OptionId: "chatterbox-turbo"),
            new JobPlan(ClusterJobs.Listening, "imouto", OptionId: "whisper-large-v3-turbo-cuda") { Pool = ["this-pc"] },
            new JobPlan(ClusterJobs.LipSync, "miku", OptionId: "audio2face-3d")
        ],
        Preference = HostingPreference.PreferLocal
    };

    private static void AssertWithinCapacity(NetworkRecommendation recommendation)
    {
        foreach (var machine in recommendation.Target.Machines.Where(m => m.Usage is not null))
            Assert.All(machine.Usage!.Gpus, g => Assert.True(g.Vram.Used <= g.Vram.Capacity + 1e-6, $"{machine.MachineId} card {g.Index}: {g.Vram.Used} > {g.Vram.Capacity}"));
    }

    [Fact]
    public void ACompanionPcWhoseHostsAreGoneThinksItselfAndSingingYields()
    {
        // Needed jobs come before optional extras: Thinking first (in the PC's own Ollama, on its card), then the voice and
        // lip-sync on the card, listening in the app; Singing (optional) has no room left and goes.
        var request = HostsGoneForHours();

        var recommendation = NetworkRecommender.Recommend(request);

        var thinking = recommendation.Target.Job(ClusterJobs.Thinking)!;
        Assert.Null(thinking.HostId);
        Assert.Equal("gemma4:e2b", thinking.OptionId);
        Assert.DoesNotContain(recommendation.Notes, n => n.Contains("No computer can run a Thinking model", StringComparison.Ordinal));
        Assert.Equal("this-pc", recommendation.Target.Job(ClusterJobs.Speaking)!.HostId);
        Assert.Equal("this-pc", recommendation.Target.Job(ClusterJobs.LipSync)!.HostId);
        Assert.False(recommendation.Target.Job(ClusterJobs.LipSync)!.Off);
        var listening = recommendation.Target.Job(ClusterJobs.Listening)!;
        Assert.Null(listening.HostId);
        Assert.StartsWith("parakeet", listening.OptionId, StringComparison.Ordinal);
        var roles = RolesOf(recommendation, "this-pc");
        Assert.Contains(roles, r => r.Kind == "chatterbox");
        Assert.Contains(roles, r => r.Kind == "audio2face");
        Assert.DoesNotContain(roles, r => r.Kind is "singing" or "stt");
        var singing = recommendation.Changes.Single(c => c.Kind == SetupChangeKind.RemoveRole && c.RoleKind == "singing");
        Assert.Equal("this-pc", singing.MachineId);
        Assert.Equal(SetupChangeBenefit.Required, singing.Benefit);
        Assert.Contains("optional", singing.Why, StringComparison.Ordinal);
        // One companion PC alone: no "each companion PC" wording.
        Assert.DoesNotContain(recommendation.Changes, c => c.Summary.Contains("ach companion PC", StringComparison.Ordinal) ||
            c.Why.Contains("ach companion PC", StringComparison.Ordinal));
        AssertWithinCapacity(recommendation);
        var again = NetworkRecommender.Recommend(Apply(request, recommendation));
        Assert.True(again.AlreadyOptimal, string.Join("\n", again.Changes.Select(c => c.Summary)));
    }

    [Fact]
    public void ACompanionPcKeepsSingingWhenTheNeededJobsLeaveRoom()
    {
        var request = HostsGoneForHours(cardGb: 24);

        var recommendation = NetworkRecommender.Recommend(request);

        Assert.Equal("gemma4:e2b", recommendation.Target.Job(ClusterJobs.Thinking)!.OptionId);
        Assert.Equal("this-pc", recommendation.Target.Job(ClusterJobs.Speaking)!.HostId);
        Assert.Contains(RolesOf(recommendation, "this-pc"), r => r.Kind == "singing");
        Assert.DoesNotContain(recommendation.Changes, c => c.RoleKind == "singing");
        AssertWithinCapacity(recommendation);
        Assert.True(NetworkRecommender.Recommend(Apply(request, recommendation)).AlreadyOptimal);
    }

    [Fact]
    public void ASavedFreeProviderKeyThinksHostedOnlyWhenTheCardHasNoRoomForALocalModel()
    {
        // Desktop plans Balanced with a saved provider key and PreferLocal without one (RecommendedSetupInputs.PreferenceFor).
        NetworkSetupRequest WithKey(double cardGb) => HostsGoneForHours(cardGb) with
        {
            Preference = HostingPreference.Balanced, ConfiguredProviders = ["nvidia-build"]
        };

        // 12 GB: the voice, lip-sync and a local model all fit; the local model stays, because its first word comes sooner.
        var roomy = NetworkRecommender.Recommend(WithKey(12));
        Assert.Equal("gemma4:e2b", roomy.Target.Job(ClusterJobs.Thinking)!.OptionId);
        Assert.Null(roomy.Target.Job(ClusterJobs.Thinking)!.HostId);

        // 8 GB: the voice and lip-sync take the card, and Thinking uses the free hosted model the owner saved a key for.
        var tight = NetworkRecommender.Recommend(WithKey(8));
        var thinking = tight.Target.Job(ClusterJobs.Thinking)!;
        Assert.Null(thinking.HostId);
        Assert.Equal("hosted:nvidia-build", thinking.OptionId);
        Assert.Equal("this-pc", tight.Target.Job(ClusterJobs.Speaking)!.HostId);
        Assert.Equal("this-pc", tight.Target.Job(ClusterJobs.LipSync)!.HostId);
        Assert.Contains(RolesOf(tight, "this-pc"), r => r.Kind == "audio2face");
        Assert.DoesNotContain(tight.Notes, n => n.StartsWith("Sign up", StringComparison.Ordinal));
        AssertWithinCapacity(tight);

        // Without a key the same 8 GB PC thinks locally first: Thinking comes before every other job.
        var local = NetworkRecommender.Recommend(HostsGoneForHours(8));
        Assert.Equal("gemma4:e2b", local.Target.Job(ClusterJobs.Thinking)!.OptionId);
        Assert.Null(local.Target.Job(ClusterJobs.Thinking)!.HostId);
    }

    /// <summary>The offline fixture: a small companion PC with no graphics card (4 GB memory, 4 threads) that listens with
    /// Whisper, and both hosts gone for 155 minutes, so no computer that answers has room for a Thinking model.</summary>
    private static NetworkSetupRequest NoRoomToThink(HostingPreference preference = HostingPreference.PreferLocal, params string[] keys) => Network(
        new NetworkMachine(new MachineSpecs("this-pc", "This PC") { RamGb = 4, CpuThreads = 4, IsPrimary = true }, NetworkMachineKind.Companion)
        {
            HasHostService = true, Roles = [Role("stt", "whisper-large-v3-turbo")]
        },
        Host("miku", Nvidia(24)) with
        {
            Online = false, OfflineFor = TimeSpan.FromMinutes(155),
            Roles = [Role("ollama", "gemma4:12b"), Role("chatterbox", "chatterbox-turbo"), Role("audio2face")]
        },
        Host("imouto", Nvidia(12)) with { Online = false, OfflineFor = TimeSpan.FromMinutes(155), Roles = [Role("stt", "whisper-large-v3-turbo")] }) with
    {
        CurrentJobs =
        [
            new JobPlan(ClusterJobs.Thinking, "miku"), new JobPlan(ClusterJobs.Speaking, "miku"),
            new JobPlan(ClusterJobs.Listening, "imouto"), new JobPlan(ClusterJobs.LipSync, "miku")
        ],
        Preference = preference, ConfiguredProviders = keys
    };

    [Fact]
    public void NobodyToThinkSaysNoFreeKeyIsSavedAndMarksItForTheReview()
    {
        var recommendation = NetworkRecommender.Recommend(NoRoomToThink());

        var thinking = recommendation.Target.Job(ClusterJobs.Thinking)!;
        Assert.Null(thinking.HostId);
        Assert.Null(thinking.OptionId);
        Assert.True(recommendation.CannotReply);
        Assert.Equal("No computer has room for a Thinking model, and no free API key is saved. Martlet can't reply until one is set up.",
            recommendation.CannotReplyNote);
        Assert.Contains(recommendation.CannotReplyNote!, recommendation.Notes);
        Assert.DoesNotContain(recommendation.Notes, n => n.Contains("hosted endpoint is allowed", StringComparison.Ordinal));

        // Each change names the computer that stays away in Away, and Detail is the rest of the reason.
        var nobody = recommendation.Changes.Single(c => c.Kind == SetupChangeKind.AssignJob && c.Job == ClusterJobs.Thinking);
        Assert.Equal("miku hasn't answered for 155 minutes, so thinking moves.", nobody.Away);
        Assert.Equal("No computer has room for a Thinking model, and no free API key is saved.", nobody.Detail);
        Assert.All(recommendation.Changes, c => Assert.DoesNotContain("hasn't answered", c.Detail, StringComparison.Ordinal));
        Assert.All(recommendation.Changes.Where(c => c.Why.Contains("hasn't answered", StringComparison.Ordinal)), c => Assert.NotNull(c.Away));

        // The computers it plans without, with the note that says so.
        Assert.Equal(["imouto", "miku"], recommendation.Offline.Select(o => o.Id));
        Assert.All(recommendation.Offline, o =>
        {
            Assert.Equal(TimeSpan.FromMinutes(155), o.For);
            Assert.Contains(o.Note, recommendation.Notes);
            Assert.StartsWith($"{o.Id} hasn't answered for 155 minutes, so Martlet plans without it", o.Note, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void NobodyToThinkWithASavedKeySaysWhyNoHostedProviderThinks()
    {
        // A saved key, but the owner keeps everything on their computers: no hosted provider, and the reason says so.
        var local = NetworkRecommender.Recommend(NoRoomToThink(HostingPreference.PreferLocal, "nvidia-build"));
        Assert.Equal("No computer has room for a Thinking model, and you keep everything on your computers. Martlet can't reply until one is set up.",
            local.CannotReplyNote);

        // The desktop plans Balanced with a saved key: the free hosted model thinks, and Martlet can reply.
        var hosted = NetworkRecommender.Recommend(NoRoomToThink(HostingPreference.Balanced, "nvidia-build"));
        Assert.Equal("hosted:nvidia-build", hosted.Target.Job(ClusterJobs.Thinking)!.OptionId);
        Assert.False(hosted.CannotReply);
        Assert.Null(hosted.CannotReplyNote);
        Assert.DoesNotContain(hosted.Notes, n => n.Contains("can't reply until", StringComparison.Ordinal));
    }

    [Fact]
    public void AWhisperModelNamedWithItsEngineIsLabelledAsItsOwnCatalogModel()
    {
        // "whisper-large-v3-turbo" is the catalog's "large-v3-turbo"; it was read as an unknown model, and on a computer with no
        // graphics card that took the processor variant, so the change said "Remove Whisper small (Listening)".
        var recommendation = NetworkRecommender.Recommend(NoRoomToThink());

        var remove = recommendation.Changes.Single(c => c.Kind == SetupChangeKind.RemoveRole && c.MachineId == "this-pc" && c.RoleKind == "stt");
        Assert.Contains("Whisper large-v3 turbo", remove.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain(recommendation.Changes, c => c.Summary.Contains("Whisper small", StringComparison.Ordinal));
        Assert.Equal("whisper-large-v3-turbo", remove.Model);

        // On a host with a card the same name runs the large model on the card, as its catalog name does.
        var gpu = NetworkRecommender.Recommend(Network(Companion("c1"), Host("h1", Nvidia(24)) with { Roles = [Role("stt", "whisper-large-v3-turbo")] }) with
        {
            CurrentJobs = [new JobPlan(ClusterJobs.Listening, "h1")]
        });
        Assert.DoesNotContain(gpu.Changes, c => c.Kind is SetupChangeKind.ChangeModel or SetupChangeKind.RemoveRole && c.RoleKind == "stt");
        Assert.Contains(gpu.Target.Machine("h1")!.Usage!.Items, i => i.OptionId == "whisper-large-v3-turbo-cuda");
    }
}
