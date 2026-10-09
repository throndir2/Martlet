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
        // Chatterbox (4.2 GB) does not fit beside Gemma 4 E2B (3.3 GB) on the card; F5 (2 GB at its peak) does.
        Assert.Equal("f5-tts", plan.Primary(PlanComponent.Voice)!.Option.Id);
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
    public void CpuOnlyPcUsesChatterboxNanoOnTheProcessorAndHostedThinkingWithAProcessorFallback()
    {
        var plan = Plan(HostingPreference.Balanced, Pc(32, 16));

        Assert.Equal("chatterbox-nano-cpu", plan.Primary(PlanComponent.Voice)!.Option.Id);
        Assert.False(plan.Primary(PlanComponent.Thinking)!.Option.IsLocal);
        Assert.Equal("gemma4:e2b-cpu", plan.Fallback(PlanComponent.Thinking)!.Option.Id);
        Assert.Contains(plan.Dropped, d => d.Component == PlanComponent.Singing && d.Reason == DropReason.NeedsNvidia);
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
    public void AffordCountsSpareDeepThinkingModels()
    {
        var plan = PlacementEngine.Plan(new PlanRequest([Pc(32, 16, Nvidia(24)), Host("big", 64, 32, Nvidia(32))])
        {
            Wanted = [PlanComponent.Thinking, PlanComponent.Voice, PlanComponent.Listening, PlanComponent.Character, PlanComponent.LipSync,
                PlanComponent.DeepThinking]
        });
        var deep = FootprintCatalog.Default.Find("deep-thinking:gemma4:e4b")!;

        Assert.True(PlacementEngine.Afford(plan, deep) >= 1);
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
    public void MeasureKeepsWhatEachPartUsuallyHoldsBesideTheMostItTakes()
    {
        var request = new PlanRequest([Pc(32, 16, Nvidia(8)), Host("cpu-box", 32, 16)])
        {
            Current = [new(PlanComponent.Thinking, "gemma4:e2b", "this-pc"), new(PlanComponent.Voice, "chatterbox-turbo", "this-pc"),
                new(PlanComponent.Voice, "dia", "cpu-box")]
        };
        var plan = PlacementEngine.Measure(request);
        var items = plan.Usage("this-pc")!.Items;

        // Chatterbox usually holds 3.7 GB of the card and grows to 4.2 GB while it speaks; Gemma 4 E2B holds 3.3 GB throughout.
        // Together they usually fit the 7.2 GB the engine may use, but not at their busiest.
        var voice = items.Single(i => i.OptionId == "chatterbox-turbo");
        Assert.Equal(3.7, voice.Usual.VramGb, 3);
        Assert.Equal(4.2, voice.Use.VramGb, 3);
        var thinking = items.Single(i => i.OptionId == "gemma4:e2b");
        Assert.Equal(thinking.Use.VramGb, thinking.Usual.VramGb, 3);
        Assert.True(items.Sum(i => i.Usual.VramGb) <= plan.Usage("this-pc")!.Gpus[0].Vram.Capacity);

        // A graphics card part on a computer without one counts in its memory, both what it usually holds and the most.
        var dia = plan.Usage("cpu-box")!.Items.Single();
        Assert.Equal(0, dia.Usual.VramGb);
        Assert.Equal(4.4 + 3, dia.Usual.RamGb, 3);
        Assert.Equal(9.8 + 4, dia.Use.RamGb, 3);
    }

    [Fact]
    public void UsualIsSteadyMemoryPlusContextAndNeverMoreThanTheReserve()
    {
        var deep = FootprintCatalog.Default.Find("deep-thinking:gemma4:e4b")!;
        Assert.Equal(deep.Steady.VramGb + deep.ContextGb, deep.Usual.VramGb, 3);
        Assert.True(deep.Usual.RamGb < deep.Reserve.RamGb);
        var peakOnly = new ComponentOption
        {
            Id = "peak-only", Component = PlanComponent.Voice, DisplayName = "Peak only", Gpu = GpuRequirement.Nvidia, Peak = new(5, 2, 1, 3)
        };
        Assert.Equal(peakOnly.Reserve, peakOnly.Usual);
        Assert.Equal(ResourceUse.Zero, FootprintCatalog.Default.Find("hosted:openai")!.Usual);
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

    [Fact]
    public void TheRankingListsEveryPartOnceNeededFirstAndEveryOptionalPartCanBeOff()
    {
        var all = ComponentRanking.All;
        Assert.Equal(Enum.GetValues<PlanComponent>(), all.Select(i => i.Component));
        Assert.Equal(Enumerable.Range(1, all.Count), all.Select(i => i.Rank));
        Assert.Equal(all.OrderBy(i => i.Necessity == ComponentNecessity.Optional ? 1 : 0).Select(i => i.Component), all.Select(i => i.Component));
        Assert.Equal([PlanComponent.Vision, PlanComponent.Reading, PlanComponent.Hearing, PlanComponent.DeepThinking, PlanComponent.SmartHome,
            PlanComponent.Singing, PlanComponent.Pictures], all.Where(i => i.Necessity == ComponentNecessity.Optional).Select(i => i.Component));
        foreach (var info in all)
        {
            Assert.Equal(info.Necessity == ComponentNecessity.Optional || info.Component == PlanComponent.LipSync, ComponentRanking.CanBeOff(info.Component));
            if (ComponentRanking.CanBeOff(info.Component)) Assert.NotEqual("it doesn't run", ComponentRanking.OffMeans(info.Component));
            Assert.StartsWith("Companion \u203a ", ComponentRanking.Page(info.Component), StringComparison.Ordinal);
        }
        Assert.Equal("Companion \u203a Hearing", ComponentRanking.Page(PlanComponent.Hearing));
        Assert.Equal("Companion \u203a Thinking pool", ComponentRanking.Page(PlanComponent.DeepThinking));
        // Only Thinking and the voice may use a companion PC's card; Vision and Hearing only as Thinking's own model.
        Assert.Equal([PlanComponent.Thinking, PlanComponent.Voice], all.Select(i => i.Component).Where(ComponentRanking.UsesCompanionCard));
        Assert.Equal([PlanComponent.Vision, PlanComponent.Reading, PlanComponent.Hearing, PlanComponent.SmartHome],
            all.Select(i => i.Component).Where(ComponentRanking.SetOnPage));
        foreach (var preference in Enum.GetValues<HostingPreference>())
            Assert.All(new[] { PlanStep.Vision, PlanStep.Reading, PlanStep.Hearing, PlanStep.SmartHome },
                step => Assert.Contains(step, ComponentRanking.ClaimOrder(preference)));
    }

    [Fact]
    public void VisionAndHearingUseThinkingsOwnModelAndReadingRunsInMartlet()
    {
        var plan = Plan(HostingPreference.PreferLocal, Pc(32, 16, Nvidia(12)));

        var thinking = plan.Primary(PlanComponent.Thinking)!;
        var vision = plan.Primary(PlanComponent.Vision)!;
        Assert.Equal("vision:thinking", vision.Option.Id);
        Assert.Equal(thinking.MachineId, vision.MachineId);
        Assert.Equal("hearing:thinking", plan.Primary(PlanComponent.Hearing)!.Option.Id);
        Assert.Equal("reading:windows-ocr", plan.Primary(PlanComponent.Reading)!.Option.Id);
        // Thinking's own model takes no room of its own; Home Assistant needs a Linux computer.
        Assert.DoesNotContain(plan.Usage("this-pc")!.Items, i => i.Component is PlanComponent.Vision or PlanComponent.Hearing);
        Assert.Contains(plan.Dropped, d => d.Component == PlanComponent.SmartHome);
        Assert.Equal(0, PlacementEngine.Afford(plan, vision.Option));

        var withHost = Plan(HostingPreference.PreferLocal, Pc(32, 16, Nvidia(12)), Host("home-box", 8, 4));
        Assert.Equal("home-box", withHost.Primary(PlanComponent.SmartHome)!.MachineId);
    }

    [Fact]
    public void AThinkingModelThatDoesntSeeGetsAnImageModelOfItsOwnWhereItFits()
    {
        var blind = new FootprintCatalog(FootprintCatalog.Default.Options.Select(o => o.Component == PlanComponent.Thinking ? o with { SeesImages = false } : o));
        var plan = PlacementEngine.Plan(new PlanRequest([Pc(64, 24, Nvidia(24))]) { Preference = HostingPreference.PreferLocal }, blind);

        var vision = plan.Primary(PlanComponent.Vision)!;
        Assert.False(vision.Option.UsesThinking);
        Assert.True(vision.Option.UsesGpu);
        Assert.Contains(plan.Usage("this-pc")!.Items, i => i.Component == PlanComponent.Vision && i.Use.VramGb > 0);
        Assert.InRange(plan.Usage("this-pc")!.Gpus[0].Vram.Percent, 1, 100);
    }

    [Fact]
    public void MeasureCountsThinkingsOwnModelAsNothingMore()
    {
        var plan = PlacementEngine.Measure(new PlanRequest([Pc(32, 16, Nvidia(8))])
        {
            Current = [new(PlanComponent.Thinking, "gemma4:e2b", "this-pc"), new(PlanComponent.Vision, "vision:thinking", "this-pc")]
        });

        Assert.Equal("vision:thinking", plan.Primary(PlanComponent.Vision)!.Option.Id);
        Assert.Single(plan.Usage("this-pc")!.Items);
        Assert.Empty(plan.Notes);
    }
}
