using System.Globalization;
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
        return DefaultSetup.Plan(gpus, windows, 16, English, thinkingGb);
    }

    [Fact]
    public void ThinkingIsTheSmallestModelThatHears()
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
    }

    [Fact]
    public void TheVoiceGetsTheCardBeforeListening()
    {
        var twelve = Plan(12);
        Assert.Equal(SpeechEngines.Default, twelve.Voice);
        Assert.True(twelve.ListenOnGpu);
        Assert.Equal("small", twelve.Listening.GpuModel);

        var sixteen = Plan(16);
        Assert.Equal(SpeechEngines.Default, sixteen.Voice);
        Assert.True(sixteen.ListenOnGpu);
        Assert.Equal("large-v3-turbo", sixteen.Listening.GpuModel);
    }

    [Fact]
    public void ABusyCardOrAnOldDriverLeavesWorkOnTheProcessor()
    {
        Assert.Null(Plan(12, usedGb: 9).Voice);
        var oldDriver = Plan(16, driver: "570.10");
        Assert.Equal(SpeechEngines.Default, oldDriver.Voice);
        Assert.False(oldDriver.ListenOnGpu);
    }

    [Fact]
    public void ThinkingElsewhereLeavesTheCardToTheVoice()
    {
        Assert.Null(Plan(8).Voice);
        Assert.Equal(SpeechEngines.Default, Plan(8, thinkingGb: 0).Voice);
    }

    private static WelcomePlan Recommend(double? totalGb, WelcomePreference preference, double ramGb = 32, int threads = 16)
    {
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
        IReadOnlyList<GpuNow> gpus = totalGb is { } total ? [new("NVIDIA GeForce RTX", total, 1, "581.42")] : [];
        var windows = totalGb is { } gb ? new GpuInfo("NVIDIA GeForce RTX", gb) : null;
        var machine = new MachineInfo("PC", "Windows 11", "CPU", threads, ramGb, windows is null ? [] : [windows], null, false, false);
        return DefaultSetup.Recommend(DefaultSetup.Specs(machine, gpus), gpus, windows, English, preference);
    }

    private static WelcomePart Part(WelcomePlan plan, WelcomeJob job) => plan.Parts.Single(p => p.Job == job);

    [Fact]
    public void TheWizardReadsTheCardsVendorAndMemory()
    {
        var specs = Recommend(12, WelcomePreference.LocalOnly).Specs;
        Assert.Equal("NVIDIA", specs.GpuVendor);
        Assert.Equal(12, specs.VramGb);
        Assert.Equal(32, specs.RamGb);
        Assert.Equal(16, specs.Threads);
        Assert.Null(Recommend(null, WelcomePreference.LocalOnly).Specs.GpuName);
    }

    [Fact]
    public void KeepingEverythingLocalNeverGoesOnline()
    {
        foreach (var gb in new double?[] { null, 6, 8, 12, 24 })
        {
            var plan = Recommend(gb, WelcomePreference.LocalOnly);
            Assert.False(plan.ThinkingOnline);
            Assert.All(plan.Parts, p => Assert.Equal(WelcomePlace.ThisPc, p.Place));
        }
    }

    [Fact]
    public void FreeOnlineMovesThinkingOffACrowdedPc()
    {
        // No graphics card: Thinking would run on the processor.
        var none = Recommend(null, WelcomePreference.FreeOnline);
        Assert.True(none.ThinkingOnline);
        Assert.Equal(0, Part(none, WelcomeJob.Thinking).VramGb);
        // 8 GB: Thinking fits but leaves no room for Chatterbox; online, the card takes the voice.
        var eight = Recommend(8, WelcomePreference.FreeOnline);
        Assert.True(eight.ThinkingOnline);
        Assert.Equal(SpeechEngines.Default.Name, Part(eight, WelcomeJob.Voice).What);
        // 24 GB holds both, so Thinking stays here.
        Assert.False(Recommend(24, WelcomePreference.FreeOnline).ThinkingOnline);
    }

    [Fact]
    public void LipSyncFollowsLoudnessWithoutRoomForAudio2Face()
    {
        Assert.Equal("Voice loudness", Part(Recommend(null, WelcomePreference.LocalOnly), WelcomeJob.LipSync).What);
        Assert.Equal("Voice loudness", Part(Recommend(8, WelcomePreference.LocalOnly), WelcomeJob.LipSync).What);
        Assert.Equal("Audio2Face", Part(Recommend(24, WelcomePreference.LocalOnly), WelcomeJob.LipSync).What);
    }

    [Fact]
    public void EachPartShowsItsShareOfThisPc()
    {
        var plan = Recommend(12, WelcomePreference.LocalOnly);
        var (vram, _, _) = plan.Share(Part(plan, WelcomeJob.Voice));
        Assert.Equal(33, vram);
        Assert.Contains("% graphics memory", plan.Describe(Part(plan, WelcomeJob.Thinking)));
        var total = plan.Total();
        Assert.InRange(total.Vram, 1, 100);
        Assert.Equal(0, WelcomePlan.Percent(4, null));
    }
}
