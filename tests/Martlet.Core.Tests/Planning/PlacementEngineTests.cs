using Martlet.Core.Installation;
using Martlet.Core.Planning;

namespace Martlet.Core.Tests.Planning;

public sealed class PlacementEngineTests
{
    private static MachineGpu Nvidia(double gb) => new($"NVIDIA {gb} GB", GpuVendor.Nvidia, gb);

    private static MachineSpecs Pc(double ramGb = 32, int threads = 16, params MachineGpu[] gpus) =>
        MachineSpecs.ThisPc(gpus, ramGb, threads);

    private static MachineSpecs Host(string id, double ramGb = 32, int threads = 16, params MachineGpu[] gpus) =>
        new(id, id) { Gpus = gpus, RamGb = ramGb, CpuThreads = threads, Platform = "linux" };

    private static PlacementPlan Plan(HostingPreference preference, params MachineSpecs[] machines) =>
        PlacementEngine.Plan(new PlanRequest(machines) { Preference = preference });

    [Fact]
    public void WeakNvidiaPcKeepsTheVoiceLocalAndThinksOnNvidiaBuildWithLoudnessLipSync()
    {
        var plan = Plan(HostingPreference.Balanced, Pc(16, 8, Nvidia(6)));

        Assert.Equal("chatterbox-turbo", plan.Primary(PlanComponent.Voice)!.Option.Id);
        Assert.Equal("hosted:nvidia-build", plan.Primary(PlanComponent.Thinking)!.Option.Id);
        Assert.Equal("loudness-lipsync", plan.Primary(PlanComponent.LipSync)!.Option.Id);
        Assert.Equal("parakeet-tdt-0.6b-v3-cpu", plan.Primary(PlanComponent.Listening)!.Option.Id);
        Assert.NotNull(plan.Fallback(PlanComponent.Thinking));
        Assert.Contains(plan.Suggestions, s => s.Kind == SuggestionKind.SignUp && s.ToOptionId == "hosted:nvidia-build");
    }

    [Fact]
    public void PreferLocalOnAnEightGigabyteCardGivesThinkingTheCardAndTheVoiceASmallEngine()
    {
        var plan = Plan(HostingPreference.PreferLocal, Pc(32, 16, Nvidia(8)));

        Assert.Equal("gemma4:e2b", plan.Primary(PlanComponent.Thinking)!.Option.Id);
        // Chatterbox (4.2 GB) does not fit beside Gemma 4 E2B (3.3 GB) on the card; XTTS-v2 (3 GB) does and starts speaking
        // sooner than F5 (about 0.3 s against 1.4 s).
        Assert.Equal("xtts-v2", plan.Primary(PlanComponent.Voice)!.Option.Id);
        Assert.DoesNotContain(plan.Assignments, a => a.IsExternal);
    }

    [Fact]
    public void TwentyFourGigabyteCardRunsVoiceLipSyncAndThinkingLocally()
    {
        var plan = Plan(HostingPreference.Balanced, Pc(64, 24, Nvidia(24)));

        Assert.Equal("chatterbox-turbo", plan.Primary(PlanComponent.Voice)!.Option.Id);
        Assert.Equal("audio2face-3d", plan.Primary(PlanComponent.LipSync)!.Option.Id);
        Assert.Equal("gemma4:e2b", plan.Primary(PlanComponent.Thinking)!.Option.Id);
        Assert.Null(plan.Fallback(PlanComponent.Thinking));
        var usage = plan.Usage("this-pc")!;
        Assert.InRange(usage.Gpus[0].Vram.Percent, 1, 100);
    }

    [Fact]
    public void CpuOnlyPcUsesWindowsVoicesAndHostedThinkingWithAFallbackChain()
    {
        var plan = Plan(HostingPreference.Balanced, Pc(32, 16));

        Assert.Equal("windows-speech", plan.Primary(PlanComponent.Voice)!.Option.Id);
        Assert.Equal("hosted:nvidia-build", plan.Primary(PlanComponent.Thinking)!.Option.Id);
        // Another company's endpoint first (one outage never takes both down), the processor last.
        Assert.Equal(["hosted:gemini", "gemma4:e2b-cpu"], plan.Fallbacks(PlanComponent.Thinking).Select(a => a.Option.Id));
        Assert.Contains(plan.Dropped, d => d.Component == PlanComponent.Singing && d.Reason == DropReason.NeedsNvidia);
    }

    [Fact]
    public void PreferLocalCpuOnlyThinksOnTheProcessorAndNeverGoesOnline()
    {
        var plan = Plan(HostingPreference.PreferLocal, Pc(32, 16));

        Assert.Equal("gemma4:e2b-cpu", plan.Primary(PlanComponent.Thinking)!.Option.Id);
        Assert.Empty(plan.External);
        Assert.Contains(plan.Dropped, d => d.Component == PlanComponent.DeepThinking);
    }

    [Fact]
    public void PreferenceChangesThePlan()
    {
        var local = Plan(HostingPreference.PreferLocal, Pc(32, 16, Nvidia(12)));
        var balanced8 = Plan(HostingPreference.Balanced, Pc(32, 16, Nvidia(8)));
        var balanced12 = Plan(HostingPreference.Balanced, Pc(32, 16, Nvidia(12)));
        var hosted = Plan(HostingPreference.PreferHosted, Pc(32, 16, Nvidia(12)));

        // Keep everything local: Thinking claims the card first, the voice still fits beside it.
        Assert.Equal("gemma4:e2b", local.Primary(PlanComponent.Thinking)!.Option.Id);
        Assert.Equal("chatterbox-turbo", local.Primary(PlanComponent.Voice)!.Option.Id);
        Assert.Empty(local.External);
        Assert.Equal(DropReason.KeptLocal, local.Dropped.Single(d => d.Component == PlanComponent.DeepThinking).Reason);
        // Balanced on 8 GB: the voice and advanced lip-sync come before a local model a free endpoint can replace.
        Assert.Equal("chatterbox-turbo", balanced8.Primary(PlanComponent.Voice)!.Option.Id);
        Assert.Equal("audio2face-3d", balanced8.Primary(PlanComponent.LipSync)!.Option.Id);
        Assert.False(balanced8.Primary(PlanComponent.Thinking)!.Option.IsLocal);
        // Balanced on 12 GB: all three fit, so nothing of the conversation leaves the PC.
        Assert.Equal("gemma4:e2b", balanced12.Primary(PlanComponent.Thinking)!.Option.Id);
        Assert.Equal("audio2face-3d", balanced12.Primary(PlanComponent.LipSync)!.Option.Id);
        // Happy with hosted: Thinking goes online with a local model on the card behind it; Deep thinking stays off the
        // voice's card, so it goes online too.
        Assert.False(hosted.Primary(PlanComponent.Thinking)!.Option.IsLocal);
        Assert.Equal("gemma4:e2b", hosted.Fallback(PlanComponent.Thinking)!.Option.Id);
        Assert.False(hosted.Primary(PlanComponent.DeepThinking)!.Option.IsLocal);
    }

    [Fact]
    public void ThreeMachinesCoverEveryComponentAndAFourthAffordsTwoDeepThinkingModels()
    {
        var machines = new[] { Pc(32, 16, Nvidia(12)), Host("a", 32, 16, Nvidia(24)), Host("b", 32, 16, Nvidia(24)) };
        var plan = PlacementEngine.Plan(new PlanRequest(machines));

        Assert.All(Enum.GetValues<PlanComponent>(), c => Assert.True(plan.Primary(c)?.Option.IsLocal, $"{c} is not local"));
        Assert.DoesNotContain(plan.Dropped, d => d.Reason != DropReason.NotWanted);
        var deep = FootprintCatalog.Default.Find("deep-thinking:gemma4:12b")!;
        Assert.Equal(0, PlacementEngine.Afford(plan, deep));

        // A dual-card machine joins: the conversation stays where it runs, Deep thinking moves up to 26B on one new card,
        // and the other card still fits two more Deep thinking models.
        var fourth = Host("dual", 64, 32, Nvidia(24), Nvidia(24));
        var request = new PlanRequest(machines);
        var joined = PlacementEngine.Plan(new PlanRequest([.. machines, fourth]) { Current = plan.AsCurrent() });
        foreach (var component in new[] { PlanComponent.Thinking, PlanComponent.Voice, PlanComponent.LipSync })
            Assert.Equal(plan.Primary(component)!.MachineId, joined.Primary(component)!.MachineId);
        Assert.Equal("deep-thinking:gemma4:26b", joined.Primary(PlanComponent.DeepThinking)!.Option.Id);
        Assert.True(PlacementEngine.Afford(joined, deep) >= 2);
        Assert.Equal(2, PlacementEngine.Afford(joined, deep, "dual"));
        Assert.Contains(PlacementEngine.SuggestForJoiningMachine(request, fourth), s =>
            s.Kind == SuggestionKind.Upgrade && s.Component == PlanComponent.DeepThinking && s.MachineId == "dual");
    }

    [Fact]
    public void JoiningMachineGetsAdvancedLipSyncBeforeALocalModel()
    {
        var network = new PlanRequest([Pc(16, 8, Nvidia(6))]);
        // A laptop whose card has 4.5 GB free: enough for Gemma 4 E2B (3.3 GB) or advanced lip-sync (1.5 GB), not both.
        var laptop = Host("laptop", 16, 8, new MachineGpu("RTX 3050 Laptop", GpuVendor.Nvidia, 6) { UsedGb = 1.5 });
        var suggestions = PlacementEngine.SuggestForJoiningMachine(network, laptop);

        Assert.Contains(suggestions, s => s.Kind == SuggestionKind.Upgrade && s.Component == PlanComponent.LipSync &&
            s.ToOptionId == "audio2face-3d" && s.MachineId == "laptop");
        Assert.DoesNotContain(suggestions, s => s.Component == PlanComponent.Thinking && s.Kind == SuggestionKind.RunLocally);
        // What already runs is not suggested again.
        Assert.DoesNotContain(suggestions, s => s.ToOptionId is "hosted:nvidia-build" or "hosted:gemini");
    }

    [Fact]
    public void JoiningBigMachineRecommendsASmarterHearingModel()
    {
        var network = new PlanRequest([Pc(32, 16, Nvidia(8))]) { Preference = HostingPreference.PreferLocal };
        var suggestions = PlacementEngine.SuggestForJoiningMachine(network, Host("gpu-box", 64, 24, Nvidia(24)));

        Assert.Contains(suggestions, s => s.Kind == SuggestionKind.Upgrade && s.Component == PlanComponent.Voice &&
            s.ToOptionId == "chatterbox-turbo" && s.MachineId == "gpu-box");
        var smarter = Assert.Single(suggestions, s => s.Kind == SuggestionKind.Upgrade && s.Component == PlanComponent.Thinking);
        Assert.True(FootprintCatalog.Default.Find(smarter.ToOptionId!)!.HearsAudio);
        Assert.Equal("gpu-box", smarter.MachineId);
    }

    [Fact]
    public void LeavingMachineDowngradesTheVoice()
    {
        var network = new PlanRequest([Pc(32, 16), Host("voice-box", 16, 8, Nvidia(8))]);
        var suggestions = PlacementEngine.SuggestForLeavingMachine(network, "voice-box");

        Assert.Contains(suggestions, s => s.Kind == SuggestionKind.Downgrade && s.Component == PlanComponent.Voice &&
            s.FromOptionId == "chatterbox-turbo" && s.ToOptionId == "windows-speech");
    }

    [Fact]
    public void AmdCardTakesThinkingSoNvidiaStaysForTheVoice()
    {
        var amd = new MachineGpu("Radeon RX 7800 XT", GpuVendor.Amd, 16);
        var plan = PlacementEngine.Plan(new PlanRequest([Pc(32, 16, Nvidia(8)), Host("amd", 32, 16, amd)]) { ThinkingFirst = true });

        Assert.Equal("amd", plan.Primary(PlanComponent.Thinking)!.MachineId);
        Assert.Equal("chatterbox-turbo", plan.Primary(PlanComponent.Voice)!.Option.Id);
        Assert.Contains(plan.Notes, n => n.Contains("AMD", StringComparison.Ordinal));
    }

    [Fact]
    public void GamingPcLeavesItsCardAlone()
    {
        var plan = PlacementEngine.Plan(new PlanRequest([Pc(32, 16, Nvidia(16)) with { KeepGpuForGames = true }]));

        Assert.DoesNotContain(plan.Usage("this-pc")!.Items, i => i.GpuIndex is not null);
        Assert.False(plan.Primary(PlanComponent.Thinking)!.Option.IsLocal);
    }

    [Fact]
    public void AppleSiliconCountsGraphicsMemoryAgainstMainMemory()
    {
        var mac = MachineSpecs.ThisPc([new("Apple M3", GpuVendor.Apple, 22) { UnifiedMemory = true }], 32, 12, "macos", "arm64");
        var plan = PlacementEngine.Plan(new PlanRequest([mac]) { Preference = HostingPreference.PreferLocal });

        var thinking = plan.Primary(PlanComponent.Thinking)!;
        Assert.Equal("gemma4:e2b", thinking.Option.Id);
        Assert.Equal("macos-speech", plan.Primary(PlanComponent.Voice)!.Option.Id);
        Assert.True(plan.Usage("this-pc")!.Ram.Used >= thinking.Option.GpuGb);
    }

    [Fact]
    public void JoiningMachineThatRunsGemmaLocallyReplacesTheHostedEndpoint()
    {
        var network = new PlanRequest([Pc(16, 8, Nvidia(6))]);
        var suggestions = PlacementEngine.SuggestForJoiningMachine(network, Host("gpu-box", 32, 16, Nvidia(12)));

        Assert.Contains(suggestions, s => s.Kind == SuggestionKind.RunLocally && s.Component == PlanComponent.Thinking &&
            s.FromOptionId == "hosted:nvidia-build" && s.MachineId == "gpu-box");
    }

    [Fact]
    public void AffordNeverCountsTheVoicesCardForOptionalJobs()
    {
        var plan = PlacementEngine.Plan(new PlanRequest([Pc(32, 16, Nvidia(24)), Host("big", 64, 32, Nvidia(32))])
        {
            Wanted = [PlanComponent.Thinking, PlanComponent.Voice, PlanComponent.Listening, PlanComponent.Character, PlanComponent.LipSync,
                PlanComponent.DeepThinking]
        });
        var deep = FootprintCatalog.Default.Find("deep-thinking:gemma4:e4b")!;
        var thinking = FootprintCatalog.Default.Find("gemma4:e2b")!;

        // The host's card has room, but it runs the voice: optional jobs never go there; another Thinking model may.
        Assert.Equal("big", plan.Primary(PlanComponent.Voice)!.MachineId);
        Assert.Equal(0, PlacementEngine.Afford(plan, deep, "big"));
        Assert.True(PlacementEngine.Afford(plan, thinking, "big") >= 1);
        Assert.Equal(0, PlacementEngine.Afford(plan, deep, "missing"));
    }

    [Fact]
    public void MeasureReportsTodaysSetupWithoutReplacingIt()
    {
        var request = new PlanRequest([Pc(32, 16, Nvidia(8))])
        {
            Current = [new(PlanComponent.Thinking, "gemma4:e2b", "this-pc"), new(PlanComponent.Voice, "chatterbox-turbo", "this-pc")]
        };
        var plan = PlacementEngine.Measure(request);

        Assert.Equal(2, plan.Assignments.Count);
        Assert.True(plan.Usage("this-pc")!.Gpus[0].Vram.Percent > 100);
    }

    [Fact]
    public void MachineSpecsFromHostHardwareKeepsDedicatedCards()
    {
        var report = new HostHardware("h1", "lan", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "test", "Ubuntu", null, "cpu", 12, 32,
            "docker", "yes", [new HostGpu("RTX 4070", "nvidia", 12282, "580"), new HostGpu("Intel UHD", "intel", 128, null)])
        { Platform = "linux" };
        var specs = MachineSpecs.FromHostHardware(report);

        Assert.Single(specs.Gpus);
        Assert.Equal(GpuVendor.Nvidia, specs.Gpus[0].Vendor);
        Assert.Equal(12, specs.CpuThreads);
        Assert.Equal("linux", specs.Platform);
    }

    [Fact]
    public void RankingPutsThinkingFirstAndVoiceFirstOnLocalHardware()
    {
        Assert.Equal(PlanComponent.Thinking, ComponentRanking.All[0].Component);
        Assert.Equal(PlanStep.Voice, ComponentRanking.ClaimOrder(HostingPreference.Balanced)[1]);
        Assert.Equal(PlanStep.ThinkingPrimary, ComponentRanking.ClaimOrder(HostingPreference.PreferLocal)[1]);
    }
}
