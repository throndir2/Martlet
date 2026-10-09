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
    public void AComputerThatJustStoppedAnsweringIsPlannedWithout()
    {
        foreach (var away in new TimeSpan?[] { null, TimeSpan.FromSeconds(40), TimeSpan.FromMinutes(4) })
        {
            var request = Network(Companion("c1"), Host("h1", Nvidia(12)) with
            {
                Online = false, OfflineFor = away, Roles = [Role("chatterbox", "chatterbox-turbo")]
            }, Host("h2", Nvidia(12))) with
            {
                CurrentJobs = [new JobPlan(ClusterJobs.Speaking, "h1", OptionId: "chatterbox-turbo")],
                Wanted = [PlanComponent.Voice]
            };

            var recommendation = NetworkRecommender.Recommend(request);

            Assert.Equal("h2", recommendation.Target.Job(ClusterJobs.Speaking)!.HostId);
            var move = recommendation.Changes.Single(c => c.Kind == SetupChangeKind.AssignJob);
            Assert.Equal(SetupChangeBenefit.Required, move.Benefit);
            Assert.Equal("h1", move.FromMachineId);
            Assert.DoesNotContain(recommendation.Changes, c => c.MachineId == "h1");
            Assert.True(recommendation.WorthAsking);
            Assert.Equal("h1", Assert.Single(recommendation.Offline).Id);
            var words = away is { TotalMinutes: >= 1 } ? "h1 hasn't answered for 4 minutes" : "h1 isn't answering";
            Assert.Contains(recommendation.Notes, n => n.StartsWith(words + ", so Martlet plans without it", StringComparison.Ordinal));
            Assert.Null(recommendation.Target.Machine("h1")!.Usage);
        }
    }

    /// <summary>The owner's report (2026-10-08): DIVA, a companion PC with an RTX 4070 whose host service runs Singing and
    /// Whisper, was a host for the others; MIKU (Thinking, the voice and lip-sync) and IMOUTO (a companion PC with Listening and
    /// a Thinking pool model) were turned off 7 minutes ago. DIVA alone takes every job at once.</summary>
    [Fact]
    public void OneCompanionPcLeftAloneMinutesAgoTakesEveryJob()
    {
        var request = Network(
            Companion("diva-host", true, Nvidia(12, "NVIDIA GeForce RTX 4070")) with
            {
                OnWindows = true, Roles = [Role("singing", "ace-step-v15-soulx-svc"), Role("stt", "large-v3-turbo")]
            },
            Companion("imouto-host", true, Nvidia(16)) with
            {
                Online = false, OfflineFor = TimeSpan.FromMinutes(7), Roles = [Role("deep-thinking", "gemma4-e2b"), Role("stt", "parakeet-tdt-110m-en")]
            },
            Host("miku-host", Nvidia(16)) with
            {
                Online = false, OfflineFor = TimeSpan.FromMinutes(7),
                Roles = [Role("audio2face", "claire"), Role("chatterbox", "chatterbox-turbo"), Role("ollama", "gemma4-e4b")]
            }) with
        {
            CurrentJobs =
            [
                new JobPlan(ClusterJobs.Thinking, "miku-host"),
                new JobPlan(ClusterJobs.Speaking, "miku-host"),
                new JobPlan(ClusterJobs.Listening, "imouto-host") { Pool = ["diva-host"] },
                new JobPlan(ClusterJobs.LipSync, "miku-host")
            ],
            CurrentThinkingPool = ["imouto-host"],
            Preference = HostingPreference.PreferLocal
        };

        var recommendation = NetworkRecommender.Recommend(request);

        Assert.False(recommendation.AlreadyOptimal);
        Assert.True(recommendation.WorthAsking);
        Assert.Equal(["imouto-host", "miku-host"], recommendation.Offline.Select(o => o.Id));
        foreach (var job in ClusterJobs.All)
        {
            var next = recommendation.Target.Job(job)!;
            Assert.True(next.HostId is null or "diva-host", $"{job} stays on {next.HostId}");
            Assert.False(next.Off && job != ClusterJobs.LipSync);
        }
        Assert.Contains(recommendation.Changes, c => c is { Kind: SetupChangeKind.AssignJob, Job: ClusterJobs.Thinking, Benefit: SetupChangeBenefit.Required });
        Assert.Contains(recommendation.Changes, c => c is { Kind: SetupChangeKind.AssignJob, Job: ClusterJobs.Speaking, Benefit: SetupChangeBenefit.Required });
        Assert.Contains(recommendation.Changes, c => c is { Kind: SetupChangeKind.AssignJob, Job: ClusterJobs.Listening, Benefit: SetupChangeBenefit.Required });
        Assert.Contains(RolesOf(recommendation, "diva-host"), r => r.Kind == "chatterbox");
        // The priority list on one companion PC: Thinking on its card, then Chatterbox Turbo (it fits), the rest on the
        // processor or off: no advanced lip-sync, no Thinking pool model and no singing on a companion PC's card.
        Assert.DoesNotContain(RolesOf(recommendation, "diva-host"), r => r.Kind is "audio2face" or "deep-thinking" or "singing" or "stt");
        Assert.True(recommendation.Target.Job(ClusterJobs.LipSync)!.Off);
        Assert.StartsWith("parakeet", recommendation.Target.Job(ClusterJobs.Listening)!.OptionId, StringComparison.Ordinal);
        // Setup order: Singing goes first (it frees the card), then Thinking, then listening (it frees Whisper's memory),
        // lip-sync, and the voice last (it takes memory).
        var order = recommendation.Changes.Select(c => c.Kind == SetupChangeKind.RemoveRole ? $"remove {c.RoleKind}" : c.Kind == SetupChangeKind.AddRole
            ? $"add {c.RoleKind}" : $"{c.Kind} {c.Job}").ToList();
        Assert.Equal(["remove singing", "AssignJob thinking", "AssignJob listening", "LeavePool listening", "remove stt", "AssignJob lip-sync",
            "add chatterbox", "AssignJob speaking"], order);
        var parts = recommendation.Components.ToDictionary(c => c.Component);
        Assert.Equal(ComponentRanking.All.Select(i => i.Component), recommendation.Components.Select(c => c.Component));
        Assert.True(parts[PlanComponent.Thinking].On);
        Assert.Equal("Gemma 4 E4B in Ollama on diva-host's NVIDIA GeForce RTX 4070", parts[PlanComponent.Thinking].Where);
        Assert.Equal("Chatterbox Turbo on diva-host's NVIDIA GeForce RTX 4070", parts[PlanComponent.Voice].Where);
        Assert.Contains("processor", parts[PlanComponent.Listening].Where, StringComparison.Ordinal);
        foreach (var off in new[] { PlanComponent.LipSync, PlanComponent.DeepThinking, PlanComponent.Singing, PlanComponent.Pictures })
        {
            Assert.False(parts[off].On, $"{off} is on");
            Assert.True(parts[off].CanBeOff);
            Assert.StartsWith("Off: ", parts[off].Where, StringComparison.Ordinal);
        }
        Assert.False(parts[PlanComponent.Thinking].CanBeOff);
        // Thinking moves to this PC with a model Martlet can set up (MIKU's route names it "gemma4-e4b", the tag gemma4:e4b).
        var thinking = recommendation.Target.Job(ClusterJobs.Thinking)!;
        Assert.Equal("gemma4:e4b", FootprintCatalog.Default.Find(thinking.OptionId!)?.ModelId);
        Assert.DoesNotContain(recommendation.Changes, c => c.Summary.StartsWith("Nobody does", StringComparison.Ordinal));
        Assert.Empty(recommendation.Target.ThinkingPool);
        Assert.DoesNotContain(recommendation.Changes, c => c.MachineId is "imouto-host" or "miku-host");
        Assert.DoesNotContain(recommendation.Notes, n => n.Contains("No computer can run a Thinking model", StringComparison.Ordinal));
        AssertOneModelPerCard(recommendation);
        AssertWithinCapacity(recommendation);
        var again = NetworkRecommender.Recommend(Apply(request, recommendation));
        Assert.True(again.AlreadyOptimal, string.Join("\n", again.Changes.Select(c => c.Summary)));
    }

    [Fact]
    public void AThinkingModelARouteNamesByItsAliasIsTheCatalogsModel()
    {
        // Host routes name an Ollama tag with '-' for ':' ("gemma4-e4b"); the recommender reads it as the catalog's gemma4:e4b.
        var request = Network(Companion("c1"), Host("h1", Nvidia(16)) with { Roles = [Role("ollama", "gemma4-e4b"), Role("deep-thinking", "gemma4-e2b")] }) with
        {
            CurrentJobs = [new JobPlan(ClusterJobs.Thinking, "h1")],
            Wanted = [PlanComponent.Thinking, PlanComponent.DeepThinking]
        };

        var today = NetworkRecommender.Today(request);

        var usage = today.Machine("h1")!.Usage!;
        Assert.Contains(usage.Items, i => i.OptionId == "gemma4:e4b");
        Assert.Contains(usage.Items, i => i.OptionId == "deep-thinking:gemma4:e2b");
    }

    [Fact]
    public void ACompanionPcAloneWithASmallCardSpeaksWithChatterboxNanoAfterThinking()
    {
        // 8 GB: Thinking (Gemma 4 E4B, the owner's model) takes the card first; Chatterbox Turbo has no room beside it, nor
        // Nano on the card, so Nano speaks on the processor (choice.accelerator=cpu) instead of nobody speaking.
        var request = Network(Companion("pc", true, Nvidia(8)) with { OnWindows = true }) with
        {
            CurrentJobs = [new JobPlan(ClusterJobs.Thinking, null, OptionId: "gemma4:e4b")],
            Preference = HostingPreference.PreferLocal
        };

        var recommendation = NetworkRecommender.Recommend(request);

        Assert.Equal("gemma4:e4b", recommendation.Target.Job(ClusterJobs.Thinking)!.OptionId);
        var speaking = recommendation.Target.Job(ClusterJobs.Speaking)!;
        Assert.Equal("pc", speaking.HostId);
        Assert.Equal("chatterbox-nano-cpu", speaking.OptionId);
        var install = recommendation.Changes.Single(c => c.Kind == SetupChangeKind.AddRole && c.RoleKind == "chatterbox-nano");
        Assert.True(install.OnProcessor);
        Assert.Null(recommendation.CannotSpeakNote);
        AssertWithinCapacity(recommendation);
    }

    [Fact]
    public void PartsTheOwnerTurnedOffAreRemovedAndShownOff()
    {
        var request = Network(Companion("c1"), Host("h1", Nvidia(24)) with
        {
            Roles = [Role("ollama", "gemma4:e2b"), Role("chatterbox", "chatterbox-turbo"), Role("audio2face"), Role("singing", "ace-step-v15-soulx-svc"),
                Role("deep-thinking", "gemma4:e2b", 0)]
        }) with
        {
            CurrentJobs =
            [
                new JobPlan(ClusterJobs.Thinking, "h1", OptionId: "gemma4:e2b"),
                new JobPlan(ClusterJobs.Speaking, "h1", OptionId: "chatterbox-turbo"),
                new JobPlan(ClusterJobs.LipSync, "h1", OptionId: "audio2face-3d")
            ],
            CurrentThinkingPool = ["h1"]
        };

        var on = NetworkRecommender.Recommend(request);
        Assert.Contains(RolesOf(on, "h1"), r => r.Kind == "singing");
        Assert.True(on.Components.Single(c => c.Component == PlanComponent.Singing).On);

        var off = NetworkRecommender.Recommend(request with { Off = [PlanComponent.LipSync, PlanComponent.DeepThinking, PlanComponent.Singing, PlanComponent.Thinking] });

        Assert.DoesNotContain(RolesOf(off, "h1"), r => r.Kind is "audio2face" or "deep-thinking" or "singing");
        Assert.Contains(RolesOf(off, "h1"), r => r.Kind == "ollama");
        Assert.True(off.Target.Job(ClusterJobs.LipSync)!.Off);
        Assert.Empty(off.Target.ThinkingPool);
        foreach (var kind in new[] { "audio2face", "deep-thinking", "singing" })
        {
            var remove = off.Changes.Single(c => c.Kind == SetupChangeKind.RemoveRole && c.RoleKind == kind);
            Assert.StartsWith("You turned", remove.Why, StringComparison.Ordinal);
            Assert.StartsWith("Turn off ", remove.Summary, StringComparison.Ordinal);
            Assert.Contains("Its downloads stay", remove.Summary, StringComparison.Ordinal);
        }
        var parts = off.Components.ToDictionary(c => c.Component);
        Assert.True(parts[PlanComponent.Singing].OwnerOff);
        Assert.False(parts[PlanComponent.Singing].On);
        Assert.Equal("Off: Martlet doesn't sing.", parts[PlanComponent.Singing].Where);
        Assert.Equal("Off: the character's face follows the voice's loudness.", parts[PlanComponent.LipSync].Where);
        // Thinking can't be turned off.
        Assert.False(parts[PlanComponent.Thinking].OwnerOff);
        Assert.True(parts[PlanComponent.Thinking].On);
        var offRequest = request with { Off = [PlanComponent.LipSync, PlanComponent.DeepThinking, PlanComponent.Singing] };
        var again = NetworkRecommender.Recommend(Apply(offRequest, off));
        Assert.True(again.AlreadyOptimal, string.Join("\n", again.Changes.Select(c => c.Summary)));
    }

    private static NetworkSetupRequest Extended(PartChoice reading, PartChoice home, params NetworkMachine[] companions) =>
        ExtendedReading("rapidocr-ppocrv4", reading, home, companions);

    private static NetworkSetupRequest ExtendedReading(string ocrModel, PartChoice reading, PartChoice home, params NetworkMachine[] companions) =>
        Network([.. companions, Host("cpu-box") with { Roles = [Role("ocr", ocrModel), Role("home-assistant")] }, Host("gpu-box", Nvidia(16))]) with
        {
            Choices =
            [
                new PartChoice(PlanComponent.Vision, true) { OptionId = "vision:thinking" },
                reading,
                new PartChoice(PlanComponent.Hearing, true) { OptionId = "hearing:gemma4:e4b" },
                home
            ]
        };

    [Fact]
    public void EveryWayToExtendMartletHasALineAndThePagePartsAreTurnedOffOnTheirPages()
    {
        var recommendation = NetworkRecommender.Recommend(Extended(new PartChoice(PlanComponent.Reading, true) { OptionId = "reading:rapidocr", HostId = "cpu-box" },
            new PartChoice(PlanComponent.SmartHome, true), Companion("c1")));
        var parts = recommendation.Components.ToDictionary(c => c.Component);

        Assert.Equal(ComponentRanking.All.Select(i => i.Component), recommendation.Components.Select(c => c.Component));
        Assert.Equal("Gemma 4 E2B, Thinking's own model", parts[PlanComponent.Vision].Where);
        Assert.True(parts[PlanComponent.Vision].On);
        Assert.Equal("Martlet's Reading role (RapidOCR) on cpu-box's processor", parts[PlanComponent.Reading].Where);
        Assert.Equal("Gemma 4 E4B in Ollama on this PC", parts[PlanComponent.Hearing].Where);
        Assert.Contains("companion PC's graphics card is for Thinking and the voice", parts[PlanComponent.Hearing].Why, StringComparison.Ordinal);
        Assert.Equal("Home Assistant on cpu-box's processor", parts[PlanComponent.SmartHome].Where);
        foreach (var part in new[] { PlanComponent.Vision, PlanComponent.Reading, PlanComponent.Hearing, PlanComponent.SmartHome })
        {
            Assert.True(parts[part].CanBeOff);
            Assert.False(parts[part].OffInReview);
        }
        Assert.True(parts[PlanComponent.Singing].OffInReview);
        // The roles stay: Reading is on, and Home Assistant runs the home.
        Assert.Contains(RolesOf(recommendation, "cpu-box"), r => r.Kind == "ocr");
        Assert.Contains(RolesOf(recommendation, "cpu-box"), r => r.Kind == "home-assistant");
        Assert.DoesNotContain(recommendation.Changes, c => c.RoleKind is "ocr" or "home-assistant");
    }

    [Fact]
    public void ReadingNamesPpOcrV5AndWhetherItReadsOnTheProcessorOrTheGraphicsCard()
    {
        var mobile = NetworkRecommender.Recommend(ExtendedReading("ppocrv5-mobile",
            new PartChoice(PlanComponent.Reading, true) { OptionId = "reading:ppocrv5", HostId = "cpu-box" },
            new PartChoice(PlanComponent.SmartHome, true), Companion("c1")));
        Assert.Equal("Martlet's Reading role (PP-OCRv5) on cpu-box's processor",
            mobile.Components.Single(c => c.Component == PlanComponent.Reading).Where);
        Assert.DoesNotContain(mobile.Changes, c => c.RoleKind == "ocr");

        var server = NetworkRecommender.Recommend(ExtendedReading("ppocrv5-server",
            new PartChoice(PlanComponent.Reading, true) { OptionId = "reading:ppocrv5-cuda", HostId = "cpu-box" },
            new PartChoice(PlanComponent.SmartHome, true), Companion("c1")));
        Assert.Equal("Martlet's Reading role (PP-OCRv5) on cpu-box's graphics card",
            server.Components.Single(c => c.Component == PlanComponent.Reading).Where);
    }

    [Fact]
    public void ReadingsRoleGoesWhenReadingIsOffAndNoOtherCompanionPcMayUseIt()
    {
        var off = new PartChoice(PlanComponent.Reading, false) { OptionId = "reading:rapidocr", HostId = "cpu-box" };
        var homeOff = new PartChoice(PlanComponent.SmartHome, false);
        var alone = NetworkRecommender.Recommend(Extended(off, homeOff, Companion("c1")));

        var remove = alone.Changes.Single(c => c.Kind == SetupChangeKind.RemoveRole && c.RoleKind == "ocr");
        Assert.Equal(SetupChangeBenefit.Improvement, remove.Benefit);
        Assert.Equal("Reading is off in Companion \u203a Reading, and no other companion PC uses it.", remove.Why);
        var parts = alone.Components.ToDictionary(c => c.Component);
        Assert.False(parts[PlanComponent.Reading].On);
        Assert.True(parts[PlanComponent.Reading].OwnerOff);
        Assert.Equal("Off: Martlet doesn't read the text on your screen.", parts[PlanComponent.Reading].Where);
        // Home Assistant runs the owner's home: it stays even with Smart home off on this PC.
        Assert.DoesNotContain(alone.Changes, c => c.RoleKind == "home-assistant");
        Assert.False(parts[PlanComponent.SmartHome].On);
        Assert.Contains("still runs your home", parts[PlanComponent.SmartHome].Why, StringComparison.Ordinal);

        // Another companion PC may read with the role, also one the request leaves out (no hardware report).
        Assert.DoesNotContain(NetworkRecommender.Recommend(Extended(off, homeOff, Companion("c1"), Companion("c2"))).Changes, c => c.RoleKind == "ocr");
        Assert.DoesNotContain(NetworkRecommender.Recommend(Extended(off, homeOff, Companion("c1")) with { CompanionPcs = 2 }).Changes, c => c.RoleKind == "ocr");
        // The review's Off doesn't turn off a part this PC sets on its page.
        Assert.DoesNotContain(NetworkRecommender.Recommend(Extended(off with { On = true }, homeOff, Companion("c1")) with
        {
            Off = [PlanComponent.Reading]
        }).Changes, c => c.RoleKind == "ocr");
    }

    [Fact]
    public void WithoutChoicesTheExtrasReadAsTheirDefaults()
    {
        var recommendation = NetworkRecommender.Recommend(Network(Companion("c1", true, Nvidia(12))) with { Preference = HostingPreference.PreferLocal });
        var parts = recommendation.Components.ToDictionary(c => c.Component);

        Assert.True(parts[PlanComponent.Vision].On);
        Assert.EndsWith("Thinking's own model", parts[PlanComponent.Vision].Where, StringComparison.Ordinal);
        Assert.Equal("Windows OCR inside Martlet on this PC's processor", parts[PlanComponent.Reading].Where);
        Assert.True(parts[PlanComponent.Hearing].On);
        Assert.False(parts[PlanComponent.SmartHome].On);
        Assert.Equal("Off: Martlet doesn't control your smart home.", parts[PlanComponent.SmartHome].Where);
        Assert.False(parts[PlanComponent.SmartHome].OwnerOff);
    }

    [Fact]
    public void AProcessorInstallAsksTheHostForItsProcessorVariant()
    {
        var change = new SetupChange(SetupChangeKind.AddRole, "pc", "Install Chatterbox Nano on pc's processor.", "") { RoleKind = "chatterbox-nano", OnProcessor = true };
        var needs = new SetupRoleNeeds("Chatterbox Nano", "MIT") { GpuOrCpu = true };
        var (arguments, problem) = SetupExecutor.Arguments(change, needs, null);
        Assert.Null(problem);
        Assert.Equal("cpu", arguments["choice.accelerator"]);
        Assert.False(SetupExecutor.Arguments(change with { OnProcessor = false }, needs, null).Arguments.ContainsKey("choice.accelerator"));
    }

    [Fact]
    public void AComputerThatStaysAwayLosesItsJobs()
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
        // Make before break, job by job in priority order: Thinking's new role, its move, then its old role; then the voice's.
        AssertMakeBeforeBreak(changes);
        Assert.True(changes[0] is { Kind: SetupChangeKind.AddRole, RoleKind: "ollama", MachineId: "h1" }, "Thinking is set up first");
        Assert.Contains(changes, c => c.Kind == SetupChangeKind.AddRole && c.DownloadGb > 0);
    }

    /// <summary>For every job, its new roles come before its move, and its move before the roles it leaves.</summary>
    private static void AssertMakeBeforeBreak(IReadOnlyList<SetupChange> changes)
    {
        string? JobOf(SetupChange c) => c.Kind is SetupChangeKind.AssignJob or SetupChangeKind.JoinPool or SetupChangeKind.LeavePool ? c.Job
            : c.RoleKind switch { "ollama" => ClusterJobs.Thinking, "stt" => ClusterJobs.Listening, "audio2face" => ClusterJobs.LipSync, null => null, var kind => kind.Contains("chatterbox") ? ClusterJobs.Speaking : null };
        foreach (var job in ClusterJobs.All)
        {
            var mine = changes.Select((c, i) => (c, i)).Where(x => JobOf(x.c) == job).ToList();
            var lastAdd = mine.Where(x => x.c.Kind == SetupChangeKind.AddRole).Select(x => x.i).DefaultIfEmpty(-1).Max();
            var assign = mine.Where(x => x.c.Kind == SetupChangeKind.AssignJob).Select(x => x.i).DefaultIfEmpty(-1).Max();
            var firstRemove = mine.Where(x => x.c.Kind == SetupChangeKind.RemoveRole).Select(x => x.i).DefaultIfEmpty(int.MaxValue).Min();
            if (assign >= 0) Assert.True(lastAdd < assign, $"{job}: a role is added after the job moves");
            Assert.True(Math.Max(lastAdd, assign) < firstRemove, $"{job}: a role is removed before the job moves");
        }
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
    public void ACompanionPcWhoseHostsAreGoneThinksOnItsCardAndRunsTheRestOnTheProcessor()
    {
        // The priority list: Thinking first (in the PC's own Ollama, on its card), then the voice on the card; listening in
        // the app and lip-sync by the voice's loudness (only Thinking and the voice take a companion PC's card). Singing
        // (optional) has no room left and goes first, so the card is free before Thinking loads.
        var request = HostsGoneForHours();

        var recommendation = NetworkRecommender.Recommend(request);

        var thinking = recommendation.Target.Job(ClusterJobs.Thinking)!;
        Assert.Null(thinking.HostId);
        Assert.Equal("gemma4:e2b", thinking.OptionId);
        Assert.DoesNotContain(recommendation.Notes, n => n.Contains("No computer can run a Thinking model", StringComparison.Ordinal));
        Assert.Equal("this-pc", recommendation.Target.Job(ClusterJobs.Speaking)!.HostId);
        Assert.True(recommendation.Target.Job(ClusterJobs.LipSync)!.Off);
        var listening = recommendation.Target.Job(ClusterJobs.Listening)!;
        Assert.Null(listening.HostId);
        Assert.StartsWith("parakeet", listening.OptionId, StringComparison.Ordinal);
        var roles = RolesOf(recommendation, "this-pc");
        Assert.Contains(roles, r => r.Kind == "chatterbox");
        Assert.DoesNotContain(roles, r => r.Kind is "singing" or "stt" or "audio2face");
        var singing = recommendation.Changes.Single(c => c.Kind == SetupChangeKind.RemoveRole && c.RoleKind == "singing");
        Assert.Equal("this-pc", singing.MachineId);
        Assert.Equal(SetupChangeBenefit.Required, singing.Benefit);
        Assert.Contains("optional", singing.Why, StringComparison.Ordinal);
        Assert.Same(singing, recommendation.Changes[0]);
        Assert.Equal(ClusterJobs.Thinking, recommendation.Changes[1].Job);
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

        // 12 GB: the voice and a local model fit; the local model stays, because its first word comes sooner. Lip-sync
        // follows the voice's loudness: a companion PC's card is only for Thinking and the voice.
        var roomy = NetworkRecommender.Recommend(WithKey(12));
        Assert.Equal("gemma4:e2b", roomy.Target.Job(ClusterJobs.Thinking)!.OptionId);
        Assert.Null(roomy.Target.Job(ClusterJobs.Thinking)!.HostId);
        Assert.True(roomy.Target.Job(ClusterJobs.LipSync)!.Off);

        // 8 GB: the voice takes the card first (a saved key: Balanced), and Thinking uses the free hosted model.
        var tight = NetworkRecommender.Recommend(WithKey(8));
        var thinking = tight.Target.Job(ClusterJobs.Thinking)!;
        Assert.Null(thinking.HostId);
        Assert.Equal("hosted:nvidia-build", thinking.OptionId);
        Assert.Equal("this-pc", tight.Target.Job(ClusterJobs.Speaking)!.HostId);
        Assert.True(tight.Target.Job(ClusterJobs.LipSync)!.Off);
        Assert.DoesNotContain(RolesOf(tight, "this-pc"), r => r.Kind == "audio2face");
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

    [Fact]
    public void ARoleTurnedOffBeforeComesBackOnWithoutDownloadingItAgain()
    {
        var request = Network(Companion("c1"), Host("h1", Nvidia(24))) with { Wanted = [PlanComponent.Thinking, PlanComponent.Voice] };
        var fresh = NetworkRecommender.Recommend(request);
        var installs = fresh.Changes.Where(c => c.Kind == SetupChangeKind.AddRole && c.MachineId == "h1" && c.Model is not null).ToList();
        Assert.Contains(installs, c => c.RoleKind == "ollama");
        Assert.All(installs, c => Assert.True(c.DownloadGb > 0, c.Summary));

        // The host service kept the models when Reconfigure turned these roles off; it reports them with the route's names.
        var kept = request with
        {
            Machines = [request.Machines[0], request.Machines[1] with { Downloaded = [.. installs.Select(c => Role(c.RoleKind!, c.Model!.Replace(':', '-')))] }]
        };
        var again = NetworkRecommender.Recommend(kept);

        Assert.Equal(RolesOf(fresh, "h1"), RolesOf(again, "h1"));
        foreach (var install in installs)
        {
            var on = again.Changes.Single(c => c.Kind == SetupChangeKind.AddRole && c.MachineId == "h1" && c.RoleKind == install.RoleKind);
            Assert.Null(on.DownloadGb);
            Assert.StartsWith("Start ", on.Summary, StringComparison.Ordinal);
            Assert.Contains("already downloaded", on.Summary, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AKeptDownloadDecidesBetweenEqualHostsAndNeedsNoDiskSpace()
    {
        var request = Network(Companion("c1"), Host("h1", Nvidia(24)), Host("h2", Nvidia(24))) with { Wanted = [PlanComponent.Thinking] };
        var fresh = NetworkRecommender.Recommend(request);
        var first = fresh.Target.Job(ClusterJobs.Thinking)!.HostId!;
        var model = fresh.Changes.Single(c => c.Kind == SetupChangeKind.AddRole && c.MachineId == first && c.RoleKind == "ollama").Model!;
        var other = first == "h1" ? "h2" : "h1";

        var kept = request with
        {
            Machines = [.. request.Machines.Select(m => m.Specs.Id == other ? m with { Downloaded = [Role("ollama", model)] } : m)]
        };
        var again = NetworkRecommender.Recommend(kept);
        Assert.Equal(other, again.Target.Job(ClusterJobs.Thinking)!.HostId);
        Assert.Null(again.Changes.Single(c => c.Kind == SetupChangeKind.AddRole && c.MachineId == other && c.RoleKind == "ollama").DownloadGb);

        // A disk too full for a new download still turns a kept model back on.
        var full = Host("h1", Nvidia(24)) with { Specs = Host("h1", Nvidia(24)).Specs with { DiskFreeGb = 10.5 } };
        var noRoom = NetworkRecommender.Recommend(Network(Companion("c1"), full) with { Wanted = [PlanComponent.Thinking] });
        Assert.DoesNotContain(RolesOf(noRoom, "h1"), r => r.Kind == "ollama");
        var room = NetworkRecommender.Recommend(Network(Companion("c1"), full with { Downloaded = [Role("ollama", model)] }) with { Wanted = [PlanComponent.Thinking] });
        Assert.Contains(RolesOf(room, "h1"), r => r.Kind == "ollama" && r.Model == model);
    }

    private static NetworkSetupRequest ThinksOnH1(string model, params HostedRolePlacement[] kept) =>
        Network(Companion("c1"), Host("h1", Nvidia(24)) with { Roles = [Role("ollama", model)], Downloaded = kept }) with
        {
            Wanted = [PlanComponent.Thinking],
            CurrentJobs = [new JobPlan(ClusterJobs.Thinking, "h1", OptionId: model)]
        };

    [Fact]
    public void PreferHostModelsThinksWithABetterModelTheHostKeeps()
    {
        var request = ThinksOnH1("gemma4:e4b", Role("ollama", "qwen2.5:14b"));
        var off = NetworkRecommender.Recommend(request);
        Assert.Contains(RolesOf(off, "h1"), r => r.Kind == "ollama" && r.Model == "gemma4:e4b");

        var on = NetworkRecommender.Recommend(request with { PreferHostModels = true });
        Assert.Equal("h1", on.Target.Job(ClusterJobs.Thinking)!.HostId);
        Assert.Contains(RolesOf(on, "h1"), r => r.Kind == "ollama" && r.Model == "qwen2.5:14b");
        var change = on.Changes.Single(c => c.MachineId == "h1" && c.RoleKind == "ollama" && c.Model == "qwen2.5:14b");
        Assert.Null(change.DownloadGb);
        Assert.Contains("already has qwen2.5:14b", change.Why, StringComparison.Ordinal);
        Assert.Contains("first word may come later", change.Why, StringComparison.Ordinal);
    }

    [Fact]
    public void PreferHostModelsKeepsTodaysModelOverASmallerOrNonChatOrDeepThinkingModel()
    {
        var request = ThinksOnH1("gemma4:12b", Role("ollama", "gemma4:e2b"), Role("ollama", "nomic-embed-text"),
            Role("deep-thinking", "qwen2.5:14b")) with { PreferHostModels = true };
        var recommendation = NetworkRecommender.Recommend(request);
        Assert.Equal("h1", recommendation.Target.Job(ClusterJobs.Thinking)!.HostId);
        Assert.Contains(RolesOf(recommendation, "h1"), r => r.Kind == "ollama" && r.Model == "gemma4:12b");
        Assert.DoesNotContain(recommendation.Changes, c => c.RoleKind == "ollama");
    }

    [Fact]
    public void PreferHostModelsMovesThinkingToTheHostThatHasABetterModel()
    {
        var request = Network(Companion("c1"), Host("h1", Nvidia(24)) with { Roles = [Role("ollama", "gemma4:e4b")] },
            Host("h2", Nvidia(24)) with { Downloaded = [Role("ollama", "qwen2.5:14b")] }) with
        {
            Wanted = [PlanComponent.Thinking],
            CurrentJobs = [new JobPlan(ClusterJobs.Thinking, "h1", OptionId: "gemma4:e4b")],
            PreferHostModels = true
        };
        var recommendation = NetworkRecommender.Recommend(request);
        Assert.Equal("h2", recommendation.Target.Job(ClusterJobs.Thinking)!.HostId);
        Assert.Contains(RolesOf(recommendation, "h2"), r => r.Kind == "ollama" && r.Model == "qwen2.5:14b");
        Assert.Null(recommendation.Changes.Single(c => c.MachineId == "h2" && c.RoleKind == "ollama").DownloadGb);
    }

    [Fact]
    public void PreferHostModelsSetsUpThinkingWithAKeptModelInsteadOfADownload()
    {
        var request = Network(Companion("c1"), Host("h1", Nvidia(24)) with { Downloaded = [Role("ollama", "llama3.1:8b")] }) with
        {
            Wanted = [PlanComponent.Thinking]
        };
        var off = NetworkRecommender.Recommend(request);
        Assert.DoesNotContain(RolesOf(off, "h1"), r => r.Kind == "ollama" && r.Model == "llama3.1:8b");

        var on = NetworkRecommender.Recommend(request with { PreferHostModels = true });
        Assert.Equal("h1", on.Target.Job(ClusterJobs.Thinking)!.HostId);
        var add = on.Changes.Single(c => c.MachineId == "h1" && c.RoleKind == "ollama");
        Assert.Equal("llama3.1:8b", add.Model);
        Assert.Null(add.DownloadGb);
        Assert.Equal(SetupChangeBenefit.Required, add.Benefit);
    }
}
