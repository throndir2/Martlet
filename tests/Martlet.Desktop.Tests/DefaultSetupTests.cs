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
    public void A_catalog_model_is_installed_by_its_own_name_not_swapped_for_the_smallest()
    {
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
        var option = new ComponentOption
        {
            Id = "hf.co/acme/Local-8B-GGUF:Q4_K_M", Component = PlanComponent.Thinking, DisplayName = "Local 8B", ModelId = "hf.co/acme/Local-8B-GGUF:Q4_K_M",
            HostRoleKind = "ollama", Gpu = GpuRequirement.AnyGpu, Steady = new(5.9, 1, 1, 4.9), Peak = new(5.9, 1.5, 2, 4.9), Origin = OptionOrigin.LocalFacts,
            FirstWordMs = 260, Source = "test"
        };
        var model = DefaultSetup.ChatModel(option);
        Assert.Equal(("hf.co/acme/Local-8B-GGUF:Q4_K_M", "4.9 GB", 8.0, false), (model.Id, model.Size, model.MinimumVramGb, model.Hears));
        // Martlet's own suggestions keep their entry, and an option it can't install falls back to the smallest model that hears.
        Assert.Equal("gemma4:e4b", DefaultSetup.ChatModel(FootprintCatalog.Default.Find("gemma4:e4b")!).Id);
        Assert.Equal(DefaultSetup.SmallestHearingModel, DefaultSetup.ChatModel(option with { Origin = OptionOrigin.Seed }));
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

/// <summary>Tests that set the process-wide <see cref="PlanningCatalog"/> run alone, so no other test plans with their catalog.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PlanningCatalogCollection
{
    public const string Name = "Planning catalog";
}

[Collection(PlanningCatalogCollection.Name)]
public sealed class PlanningCatalogTests
{
    // MainWindow's static resources use pack:// addresses, so the scheme must be known before MainWindow.LocalChatModels.
    public PlanningCatalogTests() => _ = System.IO.Packaging.PackUriHelper.UriSchemePack;

    [Fact]
    public void The_thinking_picker_lists_catalog_models_that_fit_this_pcs_card()
    {
        var models = new ModelCatalogData { Built = DateTimeOffset.UtcNow }.With(CatalogSources.HuggingFace, new CatalogSourceBlock
        {
            Read = DateTimeOffset.UtcNow,
            Items = [CatalogReaders.Local(new LocalModelFacts
            {
                Query = "acme/Local-8B", HuggingFaceRepo = "acme/Local-8B", Parameters = 8_000_000_000, WeightsParameters = 8_000_000_000,
                Inputs = new(true, false, false, "config.json"), Quantizations = [new("Q4_K_M", 4_900_000_000, "hf.co/acme/Local-8B-GGUF:Q4_K_M", "Hugging Face")]
            })!]
        });
        try
        {
            PlanningCatalog.Use(ModelCatalog.Build(models), "a test copy");
            Assert.Equal(["hf.co/acme/Local-8B-GGUF:Q4_K_M"], JobOptions.CatalogModels(MainWindow.LocalChatModels, 12).Select(o => o.ModelId));
            Assert.Empty(JobOptions.CatalogModels(MainWindow.LocalChatModels, 4));
            Assert.Empty(JobOptions.CatalogModels(MainWindow.LocalChatModels, null));
            var picker = JobOptions.OllamaModels(MainWindow.LocalChatModels, [], null, 12);
            var listed = picker.Single(o => o.Key == "hf.co/acme/Local-8B-GGUF:Q4_K_M");
            Assert.Equal("from the catalog", listed.Badge);
            Assert.Contains(listed.Facts, f => f.Key == "source");
        }
        finally
        {
            PlanningCatalog.Reset();
        }
    }

    [Fact]
    public void The_review_shows_the_catalogs_facts_under_thinking_and_the_online_deep_thinking_suggestion()
    {
        static Dictionary<string, string> Chat() => new(StringComparer.Ordinal)
        {
            [CatalogFacts.InputText] = CatalogValues.Yes, [CatalogFacts.InputImage] = CatalogValues.Yes, [CatalogFacts.Tools] = CatalogValues.Yes,
            [CatalogFacts.OutputText] = CatalogValues.Yes
        };
        var models = ModelCatalog.Build(new ModelCatalogData { Built = DateTimeOffset.UtcNow }.With(CatalogSources.NvidiaBuild, new CatalogSourceBlock
        {
            Read = DateTimeOffset.UtcNow,
            Items = [new() { Id = "acme/giant-900b-a90b-it", Name = "Giant", Facts = Chat() }]
        }));
        var catalog = FootprintCatalog.FromModels(models);
        var build = RecommendedSetupInputs.Request(RecommendedSetupInputs.WithPreferences(RecommendedSetupInputs.Fixture(DateTimeOffset.UtcNow),
            new RecommendationPreferences()));
        var recommendation = NetworkRecommender.Recommend(build.Request, catalog);
        var parts = RecommendedSetupReview.From(recommendation, build, catalog).Parts;
        Assert.Equal("acme/giant-900b-a90b-it", recommendation.OnlineDeepThinking?.ModelId);
        Assert.Contains("from Martlet's model catalog", parts.Single(p => p.Component == PlanComponent.DeepThinking).Facts, StringComparison.Ordinal);
        Assert.StartsWith("Takes text", parts.Single(p => p.Component == PlanComponent.Thinking).Facts, StringComparison.Ordinal);
        Assert.All(parts.Where(p => p.Component is not (PlanComponent.Thinking or PlanComponent.DeepThinking)), p => Assert.Null(p.Facts));
        // Before the catalog is loaded, the review has no facts lines.
        Assert.All(RecommendedSetupReview.From(NetworkRecommender.Recommend(build.Request), build, FootprintCatalog.Default).Parts, p => Assert.Null(p.Facts));
    }
}
