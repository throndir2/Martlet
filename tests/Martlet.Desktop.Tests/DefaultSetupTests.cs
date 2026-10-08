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
        // Windows voices are gone: Chatterbox Nano speaks on the processor.
        Assert.Equal(SpeechEngines.ChatterboxNano, plan.Voice);
        Assert.False(plan.VoiceOnGpu);
        Assert.False(plan.ListenOnGpu);
        Assert.False(plan.LipSyncOnGpu);
        Assert.Equal(LocalSpeechSetup.RecommendedParakeetModel(English), plan.ParakeetModel);
        Assert.Contains("Voice: Chatterbox Nano on the processor", plan.Describe());
        Assert.DoesNotContain("Windows voice", plan.Describe());
        Assert.Contains("Parakeet on the processor", plan.Describe());
    }

    [Fact]
    public void ASmallCardKeepsThinkingAndSpeaksWithChatterboxNanoBesideIt()
    {
        var plan = Plan(8);
        Assert.True(plan.ThinkingOnGpu);
        // Chatterbox Turbo has no room beside Thinking; Nano (about 3 GB) does, and the voice comes before Audio2Face.
        Assert.Equal(SpeechEngines.ChatterboxNano, plan.Voice);
        Assert.True(plan.VoiceOnGpu);
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
        var busy = Plan(12, usedGb: 9);
        Assert.Equal(SpeechEngines.ChatterboxNano, busy.Voice);
        Assert.False(busy.VoiceOnGpu);
        var oldDriver = Plan(24, driver: "570.10");
        Assert.Equal(SpeechEngines.Default, oldDriver.Voice);
        Assert.False(oldDriver.ListenOnGpu);
    }

    [Fact]
    public void WithoutNvidiaSmiNoJobNeedsTheNvidiaDriver()
    {
        var plan = DefaultSetup.Plan([], new GpuInfo("NVIDIA GeForce RTX 4090", 24), 16, English, ramGb: 32);
        Assert.True(plan.ThinkingOnGpu);
        Assert.Equal(SpeechEngines.ChatterboxNano, plan.Voice);
        Assert.False(plan.VoiceOnGpu);
        Assert.False(plan.ListenOnGpu);
        Assert.False(plan.LipSyncOnGpu);
    }

    [Fact]
    public void ThinkingElsewhereLeavesTheCardToTheVoice()
    {
        Assert.Equal(SpeechEngines.ChatterboxNano, Plan(8).Voice);
        Assert.Equal(SpeechEngines.Default, Plan(8, thinkingGb: 0).Voice);
        var full = Plan(8, thinkingGb: 5.1);
        Assert.Equal(SpeechEngines.ChatterboxNano, full.Voice);
        Assert.False(full.VoiceOnGpu);
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
        // On 8 GB, Chatterbox Nano takes the room beside Thinking: the voice is needed, Audio2Face is not.
        Assert.False(Recommend(8, HostingPreference.PreferLocal).Setup.LipSyncOnGpu);
        Assert.True(Recommend(24, HostingPreference.PreferLocal).Setup.LipSyncOnGpu);
    }

    [Fact]
    public void EachPartShowsItsShareOfThisPc()
    {
        var plan = Recommend(12, HostingPreference.PreferLocal);
        var (vram, _, _) = plan.Share(PlanComponent.Voice);
        Assert.InRange(vram, 30, 40);
        // The voice usually holds less of the card than the most it takes while it speaks; the wizard shows both.
        var usual = plan.UsualVram(PlanComponent.Voice);
        Assert.InRange(usual, 1, vram - 1);
        Assert.Contains($"Uses {usual}-{vram}% graphics memory", plan.Describe(PlanComponent.Voice));
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

    private static SetupRoute Route(string origin, Guid? key, SetupRouteType type = SetupRouteType.ChatCompletions) => new()
    {
        RouteType = type, Role = SetupRole.Llm, ProviderAlias = "chat", Origin = origin, ModelId = "m", CredentialId = key,
        ConfigurationRevision = Guid.NewGuid()
    };

    private static ThinkingFallbackSettings Fallback(string origin, Guid? key) =>
        new() { Origin = origin, ModelId = "m", CredentialId = key, ConfigurationRevision = Guid.NewGuid() };

    [Fact]
    public void SavedKeysCountAsConfiguredProvidersByPresetId()
    {
        Assert.Empty(DefaultSetup.ConfiguredProviders(null, null));
        Assert.Equal(["google-gemini"], DefaultSetup.ConfiguredProviders(Route(ChatCompletionsEndpointCatalog.GeminiBaseUrl, Guid.NewGuid()), null));
        Assert.Equal(["nvidia-build", "google-gemini"], DefaultSetup.ConfiguredProviders(
            Route(ChatCompletionsEndpointCatalog.NvidiaBuildBaseUrl, Guid.NewGuid()), Fallback(ChatCompletionsEndpointCatalog.GeminiBaseUrl, Guid.NewGuid())));
        // No key: not configured. A fallback on the same endpoint borrows the Thinking key and isn't counted twice.
        Assert.Empty(DefaultSetup.ConfiguredProviders(Route(ChatCompletionsEndpointCatalog.NvidiaBuildBaseUrl, null), null));
        Assert.Equal(["nvidia-build"], DefaultSetup.ConfiguredProviders(
            Route(ChatCompletionsEndpointCatalog.NvidiaBuildBaseUrl, Guid.NewGuid()), Fallback(ChatCompletionsEndpointCatalog.NvidiaBuildBaseUrl, null)));
        Assert.Empty(DefaultSetup.ConfiguredProviders(null, Fallback(ChatCompletionsEndpointCatalog.GeminiBaseUrl, null)));
        Assert.Equal(["openai"], DefaultSetup.ConfiguredProviders(Route("https://api.openai.com/v1", Guid.NewGuid(), SetupRouteType.OpenAi), null));
    }
}