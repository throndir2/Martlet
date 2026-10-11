using Martlet.Core.Planning;
using Martlet.Core.Settings;

namespace Martlet.Core.Tests.Planning;

/// <summary>The planner on the model catalog (docs/RECOMMENDATION_DESIGN.md, "Planner on the catalog"), on a small made-up catalog
/// so the tests don't change when the shipped snapshot is refreshed.</summary>
public sealed class CatalogOptionsTests
{
    private static readonly DateTimeOffset Read = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    private const string LocalInstall = "hf.co/acme/Local-8B-GGUF:Q4_K_M";

    private static Dictionary<string, string> Facts(bool sees, bool tools, bool hears = false) => new(StringComparer.Ordinal)
    {
        [CatalogFacts.InputText] = CatalogValues.Yes, [CatalogFacts.InputImage] = CatalogValues.Of(sees),
        [CatalogFacts.InputAudio] = CatalogValues.Of(hears), [CatalogFacts.Tools] = CatalogValues.Of(tools),
        [CatalogFacts.OutputText] = CatalogValues.Yes
    };

    private static CatalogSourceBlock Block(params CatalogObservation[] items) => new() { Read = Read, Items = items };

    private static LocalModelFacts Local(string repo, long parameters, long weights, string install) => new()
    {
        Query = repo, HuggingFaceRepo = repo, Parameters = parameters, WeightsParameters = parameters,
        Inputs = new(true, false, false, "config.json"), Quantizations = [new("Q4_K_M", weights, install, "Hugging Face")]
    };

    /// <summary>NVIDIA Build's free routes (a fast one, a slow one, a giant, and ones the rules leave out), an OpenRouter model with
    /// scores, and local facts: a good 8B model, a broken record, one too big for any card, and two of Martlet's own models.</summary>
    internal static ModelCatalog Models() => ModelCatalog.Build(new ModelCatalogData { Built = Read }
        .With(CatalogSources.NvidiaBuild, Block(
            new() { Id = "acme/fast-30b-a3b-it", Name = "Fast", Facts = Facts(sees: true, tools: true) },
            new() { Id = "acme/slow-60b-a12b-it", Name = "Slow", Facts = Facts(sees: true, tools: true) },
            new() { Id = "acme/guard-4b-a1b", Name = "Guard", Facts = Facts(sees: true, tools: true) },
            new() { Id = "acme/think-30b-a2b-reasoning", Name = "Think", Facts = Facts(sees: true, tools: true) },
            new() { Id = "acme/blind-20b-a2b", Name = "Blind", Facts = Facts(sees: false, tools: true) },
            new() { Id = "acme/giant-900b-a90b-it", Name = "Giant", Facts = Facts(sees: true, tools: true) },
            // As big as Giant and first by name, but it calls no tools: never the Thinking pool's model.
            new() { Id = "acme/aaa-2000b-it", Name = "No tools", Facts = Facts(sees: true, tools: false) }))
        .With(CatalogSources.OpenRouter, Block(
            new CatalogObservation { Id = "acme/small-3b", HuggingFace = "acme/small-3b", Name = "Small", Rank = 95, Facts = Facts(sees: true, tools: true), Free = false }))
        .With(CatalogSources.HuggingFace, Block(
            CatalogReaders.Local(Local("acme/Local-8B", 8_000_000_000, 4_900_000_000, LocalInstall))!,
            CatalogReaders.Local(Local("acme/Huge-1000B", 1_000_000_000_000, 8_000_000_000, "hf.co/acme/Huge-1000B-GGUF:Q4_K_M"))!,
            CatalogReaders.Local(Local("acme/Wide-400B", 400_000_000_000, 240_000_000_000, "hf.co/acme/Wide-400B-GGUF:Q4_K_M"))!,
            CatalogReaders.Local(Local("google/gemma-4-12B-it", 12_000_000_000, 7_300_000_000, "gemma4:12b") with { OllamaTag = "gemma4:12b" })!,
            CatalogReaders.Local(Local("google/gemma-4-E2B-it", 5_100_000_000, 3_100_000_000, "gemma4:e2b") with { OllamaTag = "gemma4:e2b" })!)));

    [Fact]
    public void Martlets_own_options_keep_their_ids_and_measured_numbers()
    {
        var catalog = FootprintCatalog.FromModels(Models());
        Assert.All(FootprintCatalog.Default.Options, seed => Assert.NotNull(catalog.Find(seed.Id)));
        var e2b = catalog.Find("gemma4:e2b")!;
        Assert.Equal((154, 3.3, 1), (e2b.FirstWordMs!.Value, e2b.GpuGb, e2b.QualityTier));
        Assert.Equal("google/gemma-4-E2B-it", e2b.CatalogKey);
        Assert.Equal(OptionOrigin.Seed, e2b.Origin);
        // An estimate in the seed (Gemma 4 12B's first word) is estimated again from the local facts; its tier stays Martlet's.
        var twelve = catalog.Find("gemma4:12b")!;
        Assert.InRange(twelve.FirstWordMs!.Value, 300, 380);
        Assert.Equal(3, twelve.QualityTier);
        Assert.Contains("local facts", twelve.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void The_first_word_estimate_matches_martlets_measured_small_models()
    {
        static LocalMemoryEstimate Reads(long bytes) => new("Q4_K_M", 8192, 0, 0, 0, 0, true, 0, 0, bytes);
        // Gemma 4 E2B and E4B read about 1.79 and 3.59 GB a token; measured 154 ms and 208 ms on an RTX 4070 (504 GB/s).
        Assert.InRange(CatalogOptions.FirstWordMs(Reads(1_790_000_000), CatalogOptions.ReferenceBandwidthGbps)!.Value, 144, 164);
        Assert.InRange(CatalogOptions.FirstWordMs(Reads(3_590_000_000), CatalogOptions.ReferenceBandwidthGbps)!.Value, 198, 218);
        // A faster card's memory makes the same model sooner.
        Assert.True(CatalogOptions.FirstWordMs(Reads(3_590_000_000), 1008) < CatalogOptions.FirstWordMs(Reads(3_590_000_000), 504));
    }

    [Fact]
    public void A_local_model_with_good_facts_becomes_an_option_for_each_job_it_can_do()
    {
        var catalog = FootprintCatalog.FromModels(Models());
        var thinking = catalog.Find(LocalInstall)!;
        Assert.Equal((PlanComponent.Thinking, OptionOrigin.LocalFacts, "ollama", LocalInstall), (thinking.Component, thinking.Origin, thinking.HostRoleKind, thinking.ModelId));
        Assert.InRange(thinking.GpuGb, 5.5, 6.5);
        Assert.NotNull(thinking.FirstWordMs);
        Assert.NotNull(thinking.WordsPerSecond);
        Assert.True(thinking.SeesImages);
        // Unknown is not Yes: nothing says it hears.
        Assert.False(thinking.HearsAudio);
        // Martlet's host roles don't offer it, so only this PC's own Ollama runs it.
        Assert.True(thinking.NativeOnly);
        Assert.Equal(PlanComponent.DeepThinking, catalog.Find("deep-thinking:" + LocalInstall)!.Component);
        Assert.Equal(PlanComponent.Vision, catalog.Find("vision:" + LocalInstall)!.Component);
        Assert.Null(catalog.Find("hearing:" + LocalInstall));
    }

    [Fact]
    public void A_broken_record_or_a_model_too_big_for_any_card_never_becomes_an_option()
    {
        var catalog = FootprintCatalog.FromModels(Models());
        Assert.DoesNotContain(catalog.Options, o => o.ModelId is "hf.co/acme/Huge-1000B-GGUF:Q4_K_M" or "hf.co/acme/Wide-400B-GGUF:Q4_K_M");
        // Martlet's own models stay its own options: none is added again from the catalog.
        Assert.Single(catalog.For(PlanComponent.Thinking), o => o.Id == "gemma4:12b");
        Assert.DoesNotContain(catalog.Options, o => o.Origin == OptionOrigin.LocalFacts && o.ModelId is "gemma4:12b" or "gemma4:e2b");
    }

    [Fact]
    public void A_provider_suggests_its_fastest_known_free_model_that_sees_and_calls_tools()
    {
        var models = Models();
        Assert.Equal("acme/fast-30b-a3b-it", CatalogOptions.SuggestedChat(models, ChatCompletionsEndpointCatalog.NvidiaBuildId)!.ModelId);
        var hosted = FootprintCatalog.FromModels(models).Find("hosted:nvidia-build")!;
        Assert.Equal(("acme/fast-30b-a3b-it", OptionOrigin.Catalog), (hosted.ModelId, hosted.Origin));
        // A provider the catalog names no free model for keeps today's default.
        Assert.Equal(ChatCompletionsEndpointCatalog.GeminiDefaultModelId, CatalogOptions.SuggestedModel(models, ChatCompletionsEndpointCatalog.GeminiId));
        Assert.Equal(ChatCompletionsEndpointCatalog.NvidiaBuildDefaultModelId, CatalogOptions.SuggestedModel(null, ChatCompletionsEndpointCatalog.NvidiaBuildId));
    }

    [Fact]
    public void What_martlet_found_out_on_a_route_comes_before_the_catalog()
    {
        var models = Models();
        ModelAbility Found(bool? tools = null, DateTimeOffset? retired = null) => new()
        {
            Origin = ChatCompletionsEndpointCatalog.NvidiaBuildBaseUrl, ModelId = "acme/fast-30b-a3b-it", Tools = tools, Retired = retired,
            Source = retired is null ? ModelAbility.TestRequest : ModelAbility.GoneAnswer, CheckedAt = Read
        };
        Assert.Equal("acme/slow-60b-a12b-it", CatalogOptions.SuggestedChat(models, ChatCompletionsEndpointCatalog.NvidiaBuildId, [Found(retired: Read)])!.ModelId);
        Assert.Equal("acme/slow-60b-a12b-it", CatalogOptions.SuggestedChat(models, ChatCompletionsEndpointCatalog.NvidiaBuildId, [Found(tools: false)])!.ModelId);
        Assert.Equal("acme/slow-60b-a12b-it", FootprintCatalog.FromModels(models, tested: [Found(retired: Read)]).Find("hosted:nvidia-build")!.ModelId);
    }

    [Fact]
    public void Deep_thinking_online_uses_the_smartest_free_model_and_names_it()
    {
        var deep = FootprintCatalog.FromModels(Models()).Find("hosted:nvidia-build-deep")!;
        Assert.Equal(("acme/giant-900b-a90b-it", 5, OptionOrigin.Catalog), (deep.ModelId, deep.QualityTier, deep.Origin));
        Assert.Contains("Giant", deep.DisplayName, StringComparison.Ordinal);
        // Martlet's own list names no model for it.
        Assert.Null(FootprintCatalog.Default.Find("hosted:nvidia-build-deep")!.ModelId);
    }

    [Fact]
    public void A_model_the_catalog_scores_takes_its_smartness_instead_of_the_size_in_its_name()
    {
        var models = Models();
        var served = ServedModels.Option(new ServedModel("pc", "lm-studio", "LM Studio", "http://127.0.0.1:1234/v1", "acme/small-3b"), models);
        Assert.Equal((5, "among the smartest"), (served.QualityTier, served.Smartness));
        Assert.Equal(1, ServedModels.Option(new ServedModel("pc", "lm-studio", "LM Studio", "http://127.0.0.1:1234/v1", "acme/small-3b")).QualityTier);
        var catalog = FootprintCatalog.FromModels(models);
        Assert.Equal(5, catalog.Tier("acme/small-3b"));
        Assert.Equal(2, catalog.Tier("someone/unknown-7b"));
    }

    [Fact]
    public void Option_facts_show_the_catalogs_facts_and_martlets_own_options_stay_as_before()
    {
        var catalog = FootprintCatalog.FromModels(Models());
        var facts = OptionFacts.Of(catalog.Find(LocalInstall)!).ToDictionary(f => f.Key, f => f.Value);
        Assert.Equal("text and pictures", facts["inputs"]);
        Assert.StartsWith("Martlet's model catalog", facts["source"], StringComparison.Ordinal);
        Assert.Contains("this PC's own Ollama", facts["hosts"], StringComparison.Ordinal);
        Assert.Contains("words a second", facts["words"], StringComparison.Ordinal);
        Assert.Contains("smartness", facts.Keys);
        var seed = OptionFacts.Of(FootprintCatalog.Default.Find("gemma4:e2b")!).Select(f => f.Key).ToList();
        Assert.DoesNotContain("source", seed);
        Assert.DoesNotContain("smartness", seed);
        Assert.DoesNotContain("inputs", seed);
        Assert.StartsWith("Takes text and pictures", OptionFacts.Brief(catalog.Find(LocalInstall)!), StringComparison.Ordinal);
        Assert.Null(OptionFacts.Brief(FootprintCatalog.Default.Find("gemma4:e2b")!));
    }

    [Fact]
    public void Host_role_models_match_the_host_roles_choices()
    {
        foreach (var role in new[] { "ollama", "deep-thinking" })
        {
            var line = File.ReadAllLines(Path.Combine(RepositoryRoot(), "deploy", "host", "roles", role, "role.conf"))
                .Single(l => l.StartsWith("choice=OLLAMA_MODEL|", StringComparison.Ordinal));
            Assert.Equal(line.Split('|')[2].Split(' ', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal),
                FootprintCatalog.HostRoleModels.Order(StringComparer.Ordinal));
        }
    }

    [Fact]
    public void The_planning_catalog_plans_with_the_model_catalog_once_it_is_loaded()
    {
        try
        {
            var models = Models();
            Assert.Same(FootprintCatalog.Default, PlanningCatalog.Current);
            var used = PlanningCatalog.Use(models, "a test copy");
            Assert.Same(used, PlanningCatalog.Current);
            Assert.Same(models, PlanningCatalog.Models);
            Assert.Contains("a test copy", used.From, StringComparison.Ordinal);
            Assert.Equal("acme/fast-30b-a3b-it", PlanningCatalog.SuggestedModel(ChatCompletionsEndpointCatalog.NvidiaBuildId));
            Assert.Equal("acme/giant-900b-a90b-it", PlanningCatalog.SmartestModel(ChatCompletionsEndpointCatalog.NvidiaBuildId));
        }
        finally
        {
            PlanningCatalog.Reset();
        }
    }

    // ---------- the planners ----------

    private static NetworkSetupRequest AlonePc(HostingPreference preference) =>
        new([new NetworkMachine(new MachineSpecs("pc", "pc") { Gpus = [new("RTX 4070", GpuVendor.Nvidia, 12)], RamGb = 32, CpuThreads = 16, IsPrimary = true },
            NetworkMachineKind.Companion)])
        {
            VoiceEngine = "chatterbox", Preference = preference, Quality = ReplyQuality.Balanced
        };

    [Fact]
    public void Deep_thinking_online_is_suggested_when_no_host_card_fits_a_smarter_model()
    {
        var catalog = FootprintCatalog.FromModels(Models());
        var suggested = NetworkRecommender.Recommend(AlonePc(HostingPreference.Backup), catalog);
        Assert.Equal("acme/giant-900b-a90b-it", suggested.OnlineDeepThinking?.ModelId);
        Assert.Contains(suggested.Notes, n => n.StartsWith("Deep thinking: NVIDIA Build: Giant", StringComparison.Ordinal) && n.Contains("Thinking pool"));
        Assert.Contains("Companion › Thinking pool", suggested.Components.Single(c => c.Component == PlanComponent.DeepThinking).Why, StringComparison.Ordinal);
        // A suggestion changes no setup, so Martlet doesn't ask about a declined setup again because of it.
        Assert.Equal(NetworkRecommender.Recommend(AlonePc(HostingPreference.Backup)).Fingerprint, suggested.Fingerprint);
        // Never online, Martlet's own list (it names no model), or an online member already in the pool: no suggestion.
        Assert.Null(NetworkRecommender.Recommend(AlonePc(HostingPreference.PreferLocal), catalog).OnlineDeepThinking);
        Assert.Null(NetworkRecommender.Recommend(AlonePc(HostingPreference.Backup)).OnlineDeepThinking);
        Assert.Null(NetworkRecommender.Recommend(AlonePc(HostingPreference.Backup) with { OnlineThinkingPool = ["x on NVIDIA Build"] }, catalog).OnlineDeepThinking);
    }

    [Fact]
    public void A_model_the_host_roles_dont_offer_never_goes_to_a_host_role()
    {
        ComponentOption Smartest(bool nativeOnly) => new()
        {
            Id = "hf.co/acme/x-GGUF:Q4_K_M", Component = PlanComponent.Thinking, DisplayName = "X", ModelId = "hf.co/acme/x-GGUF:Q4_K_M",
            HostRoleKind = "ollama", Gpu = GpuRequirement.AnyGpu, Steady = new(2, 1, 1, 2), Peak = new(2, 1, 2, 2), QualityTier = 5,
            FirstWordMs = 100, HearsAudio = true, SeesImages = true, NativeOnly = nativeOnly, Source = "test"
        };
        var request = new NetworkSetupRequest(
        [
            new NetworkMachine(new MachineSpecs("h1", "h1") { Gpus = [new("RTX 4090", GpuVendor.Nvidia, 24)], RamGb = 64, CpuThreads = 16, Platform = "linux" },
                NetworkMachineKind.Host) { HasHostService = true },
            new NetworkMachine(new MachineSpecs("pc", "pc") { RamGb = 32, CpuThreads = 16, IsPrimary = true }, NetworkMachineKind.Companion)
        ]) { VoiceEngine = "chatterbox", Quality = ReplyQuality.Balanced };
        string? Model(bool nativeOnly) => NetworkRecommender.Recommend(request, new FootprintCatalog(FootprintCatalog.Default.Options.Append(Smartest(nativeOnly))))
            .Target.Machine("h1")!.Roles.FirstOrDefault(r => r.Kind == "ollama")?.Model;
        Assert.Equal("hf.co/acme/x-GGUF:Q4_K_M", Model(nativeOnly: false));
        Assert.NotEqual("hf.co/acme/x-GGUF:Q4_K_M", Model(nativeOnly: true));
        Assert.NotNull(Model(nativeOnly: true));
    }

    [Fact]
    public void The_placement_engine_puts_deep_thinking_online_when_no_card_fits_a_smarter_model()
    {
        var plan = PlacementEngine.Plan(new PlanRequest([MachineSpecs.ThisPc([new("RTX 4070", GpuVendor.Nvidia, 12)], 32, 16)])
        {
            Preference = HostingPreference.Backup, Quality = ReplyQuality.Balanced,
            Wanted = [PlanComponent.Thinking, PlanComponent.Voice, PlanComponent.Listening, PlanComponent.DeepThinking]
        }, FootprintCatalog.FromModels(Models()));
        Assert.Equal(("hosted:nvidia-build-deep", "acme/giant-900b-a90b-it"), (plan.Primary(PlanComponent.DeepThinking)!.Option.Id, plan.Primary(PlanComponent.DeepThinking)!.Option.ModelId));
        Assert.Contains(plan.Suggestions, s => s.Kind == SuggestionKind.SignUp && s.Why.Contains("Thinking pool", StringComparison.Ordinal));
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, "deploy", "host", "roles"))) return directory.FullName;
        throw new DirectoryNotFoundException("The repository root (deploy/host/roles) was not found above the test's folder.");
    }
}
