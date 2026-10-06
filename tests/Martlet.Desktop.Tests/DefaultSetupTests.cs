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
}
