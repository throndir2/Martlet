using System.Globalization;
using Martlet.Core.Planning;
using Martlet.Core.Settings;
using Martlet.Desktop;

namespace Martlet.Desktop.Tests;

public sealed class DefaultSetupTests
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    private static DefaultSetupPlan Plan(double? totalGb, double usedGb = 1, string driver = "581.42", double? thinkingGb = null)
    {
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
        IReadOnlyList<GpuNow> gpus = totalGb is { } total ? [new("NVIDIA GeForce RTX", total, usedGb, driver)] : [];
        var windows = totalGb is { } gb ? new GpuInfo("NVIDIA GeForce RTX", gb) : null;
        return DefaultSetup.Plan(gpus, windows, 16, English, thinkingGb, ramGb: 32);
    }

    [Fact]
    public void ThinkingIsTheFastestModelThatHears()
    {
        Assert.Equal("gemma4:e2b", Plan(null).Thinking.Id);
        Assert.True(Plan(null).Thinking.Hears);
        Assert.Equal("gemma4:e2b", Plan(24).Thinking.Id);
    }

    [Fact]
    public void WithoutAGraphicsCardEverythingRunsOnTheProcessor()
    {
        var plan = Plan(null);
        Assert.False(plan.ThinkingOnGpu);
        Assert.Null(plan.Voice);
        Assert.False(plan.ListenOnGpu);
        Assert.False(plan.LipSyncOnGpu);
        Assert.Equal(LocalSpeechSetup.RecommendedParakeetModel(English), plan.ParakeetModel);
        Assert.Contains("Windows voice", plan.Describe());
        Assert.Contains("Parakeet on the processor", plan.Describe());
    }

    [Fact]
    public void ASmallCardKeepsThinkingAndFallsBackToTheProcessorForVoiceAndListening()
    {
        var plan = Plan(8);
        Assert.True(plan.ThinkingOnGpu);
        Assert.Null(plan.Voice);
        Assert.False(plan.ListenOnGpu);
        Assert.False(plan.LipSyncOnGpu);
    }

    [Fact]
    public void TheVoiceGetsTheCardBeforeListeningAndLipSync()
    {
        var twelve = Plan(12);
        Assert.Equal(SpeechEngines.Default, twelve.Voice);
        Assert.False(twelve.ListenOnGpu);

        var twentyFour = Plan(24);
        Assert.Equal(SpeechEngines.Default, twentyFour.Voice);
        Assert.True(twentyFour.ListenOnGpu);
        Assert.Equal("large-v3-turbo", twentyFour.Listening.GpuModel);
        Assert.True(twentyFour.LipSyncOnGpu);
    }

    [Fact]
    public void ABusyCardOrAnOldDriverLeavesWorkOnTheProcessor()
    {
        Assert.Null(Plan(12, usedGb: 9).Voice);
        var oldDriver = Plan(24, driver: "570.10");
        Assert.Equal(SpeechEngines.Default, oldDriver.Voice);
        Assert.False(oldDriver.ListenOnGpu);
    }

    [Fact]
    public void ThinkingElsewhereLeavesTheCardToTheVoice()
    {
        Assert.Null(Plan(8).Voice);
        Assert.Equal(SpeechEngines.Default, Plan(8, thinkingGb: 0).Voice);
        Assert.Null(Plan(8, thinkingGb: 5.1).Voice);
    }

    private static WelcomePlan Recommend(double? totalGb, HostingPreference preference, double ramGb = 32, int threads = 16,
        PlanRequest? network = null)
    {
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
        IReadOnlyList<GpuNow> gpus = totalGb is { } total ? [new("NVIDIA GeForce RTX", total, 1, "581.42")] : [];
        var windows = totalGb is { } gb ? new GpuInfo("NVIDIA GeForce RTX", gb) : null;
        var specs = DefaultSetup.Specs(gpus, windows, ramGb, threads);
        return DefaultSetup.Recommend(specs, gpus, windows, English, preference, network: network);
    }

    [Fact]
    public void TheWizardReadsTheCardsVendorAndMemory()
    {
        var specs = Recommend(12, HostingPreference.PreferLocal).Specs;
        Assert.Equal(GpuVendor.Nvidia, Assert.Single(specs.Gpus).Vendor);
        Assert.Equal(12, specs.Gpus[0].VramGb);
        Assert.Equal(1, specs.Gpus[0].UsedGb);
        Assert.Equal(32, specs.RamGb);
        Assert.Equal(16, specs.CpuThreads);
        Assert.Empty(Recommend(null, HostingPreference.PreferLocal).Specs.Gpus);
        // Without nvidia-smi, the card Windows reports still counts.
        var windowsOnly = DefaultSetup.Specs([], new GpuInfo("AMD Radeon RX 7800 XT", 16), 32, 16);
        Assert.Equal(GpuVendor.Amd, windowsOnly.Gpus[0].Vendor);
    }

    [Fact]
    public void KeepingEverythingLocalNeverGoesOnline()
    {
        foreach (var gb in new double?[] { null, 6, 8, 12, 24 })
        {
            var plan = Recommend(gb, HostingPreference.PreferLocal);
            Assert.False(plan.ThinkingOnline);
            Assert.Empty(plan.Placement.External);
        }
    }

    [Fact]
    public void FreeOnlineSendsThinkingToNvidiaBuildAndGivesTheCardToTheVoice()
    {
        var eight = Recommend(8, HostingPreference.PreferHosted);
        Assert.True(eight.ThinkingOnline);
        Assert.Equal("nvidia-build", eight.ThinkingHosted!.Option.ProviderId);
        Assert.Equal(SpeechEngines.Default, eight.Setup.Voice);
        Assert.Contains(eight.Placement.Suggestions, s => s.Kind == SuggestionKind.SignUp);
        Assert.Contains("online (free)", eight.Describe(PlanComponent.Thinking));
    }

    [Fact]
    public void LipSyncFollowsLoudnessWithoutRoomForAudio2Face()
    {
        Assert.Equal("loudness-lipsync", Recommend(null, HostingPreference.PreferLocal).Placement.Primary(PlanComponent.LipSync)!.Option.Id);
        Assert.Equal("loudness-lipsync", Recommend(8, HostingPreference.PreferLocal).Placement.Primary(PlanComponent.LipSync)!.Option.Id);
        Assert.True(Recommend(24, HostingPreference.PreferLocal).Setup.LipSyncOnGpu);
    }

    [Fact]
    public void EachPartShowsItsShareOfThisPc()
    {
        var plan = Recommend(12, HostingPreference.PreferLocal);
        var (vram, _, _) = plan.Share(PlanComponent.Voice);
        Assert.Equal(33, vram);
        Assert.Contains("% graphics memory", plan.Describe(PlanComponent.Thinking));
        var total = plan.Total();
        Assert.InRange(total.Vram, 1, 100);
        Assert.InRange(total.Ram, 1, 100);
    }

    [Fact]
    public void JoiningANetworkSuggestsWhatThisPcTakesOn()
    {
        // The network: a PC without a graphics card, so Thinking runs online and the voice on the processor.
        var laptop = new MachineSpecs("laptop", "Laptop") { RamGb = 16, CpuThreads = 8 };
        var network = new PlanRequest([laptop]) { Preference = HostingPreference.Balanced };
        var plan = Recommend(24, HostingPreference.PreferLocal, network: network);
        Assert.NotEmpty(plan.Joining);
        Assert.Contains(plan.Joining, s => s.MachineId == "this-pc");
    }
}
