using Martlet.Core.Planning;

namespace Martlet.Core.Tests.Planning;

public sealed class ModelCatalogTests
{
    private static readonly DateTimeOffset Read = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    private static CatalogAnswer A(string source, string value) => new() { Source = source, Value = value };

    private static Dictionary<string, string> Inputs(string text) =>
        new(StringComparer.Ordinal)
        {
            [CatalogFacts.InputText] = CatalogValues.Yes,
            [CatalogFacts.InputImage] = CatalogValues.Of(text.Contains('I')),
            [CatalogFacts.InputVideo] = CatalogValues.Of(text.Contains('V')),
            [CatalogFacts.InputAudio] = CatalogValues.Of(text.Contains('A'))
        };

    private static CatalogSourceBlock Block(params CatalogObservation[] items) => new() { Read = Read, Items = items };

    /// <summary>The conflicts measured on 2026-10-10 (docs/MODEL_CATALOG.md, Conflicts found), as each source said them.</summary>
    private static ModelCatalogData Measured(bool withConfig = false)
    {
        var data = new ModelCatalogData { Built = Read }
            .With(CatalogSources.ModelsDev, Block(
                new() { Id = "google/gemma-4-26b-a4b-it", HuggingFace = "google/gemma-4-26B-A4B-it", Name = "Gemma 4 26B A4B IT", Facts = Inputs("TI") },
                new() { Id = "alibaba/qwen3.5-122b-a10b", HuggingFace = "Qwen/Qwen3.5-122B-A10B", Name = "Qwen3.5 122B-A10B", Facts = Inputs("TIVA") },
                new() { Id = "google/gemma-4-31b-it", HuggingFace = "google/gemma-4-31B-it", Name = "Gemma 4 31B IT", Facts = Inputs("TI") }))
            .With(CatalogSources.OpenRouter, Block(
                new() { Id = "google/gemma-4-26b-a4b-it", HuggingFace = "google/gemma-4-26B-A4B-it", Facts = Inputs("TIV"), Rank = 60, Free = false },
                new() { Id = "google/gemma-4-26b-a4b-it:free", HuggingFace = "google/gemma-4-26B-A4B-it", Facts = Inputs("TIV"), Rank = 60, Free = true },
                new() { Id = "qwen/qwen3.5-122b-a10b", HuggingFace = "Qwen/Qwen3.5-122B-A10B", Facts = Inputs("TIV"), Rank = 70 }))
            .With(CatalogSources.ModelsDevRows, Block(
                new() { Id = "google/gemma-4-31b-it", Provider = "groq", Canonical = "google/gemma-4-31b-it", Facts = Inputs("T"), Free = true },
                new() { Id = "google/gemma-4-31B-it", Provider = "deepinfra", Canonical = "google/gemma-4-31b-it", Facts = Inputs("TIV") },
                new() { Id = "gemma-4-31b", Provider = "togetherai", Canonical = "google/gemma-4-31b-it", Facts = Inputs("TIA"), Deprecated = true },
                new() { Id = "Qwen/Qwen3.5-122B-A10B", Provider = "deepinfra", Canonical = "alibaba/qwen3.5-122b-a10b", Facts = Inputs("TIVA") }))
            .With(CatalogSources.Vllm, Block(
                new() { Id = "Gemma4ForCausalLM", Examples = ["google/gemma-4-E2B-it"], TextOnly = true, Facts = Inputs("T") },
                new()
                {
                    Id = "Gemma4ForConditionalGeneration", Examples = ["google/gemma-4-E2B-it"],
                    Facts = new(Inputs("TIV")) { [CatalogFacts.InputAudio] = CatalogValues.Some }
                },
                new() { Id = "Gemma4UnifiedForConditionalGeneration", Examples = ["google/gemma-4-12B-it"], Facts = Inputs("TIVA") },
                new() { Id = "Qwen3_5MoeForConditionalGeneration", Examples = ["Qwen/Qwen3.5-35B-A3B-Instruct"], Facts = Inputs("TIV") }));
        if (!withConfig) return data;
        return data.With(CatalogSources.HuggingFace, Block(
            CatalogReaders.Local(new LocalModelFacts
            {
                Query = "google/gemma-4-E2B-it", HuggingFaceRepo = "google/gemma-4-E2B-it", Parameters = 5_123_178_051, ActiveParameters = 2_300_000_000,
                Inputs = new(true, true, true, "config.json"), MaxContext = 131_072, License = "apache-2.0"
            })!,
            CatalogReaders.Local(new LocalModelFacts
            {
                Query = "google/gemma-4-26B-A4B-it", HuggingFaceRepo = "google/gemma-4-26B-A4B-it", Inputs = new(true, false, true, "config.json")
            })!));
    }

    [Fact]
    public void Sources_at_the_same_level_that_disagree_give_unknown_and_keep_every_answer()
    {
        var fact = CatalogResolver.ResolveModel(CatalogFacts.InputVideo,
            [A(CatalogSources.ModelsDev, "no"), A(CatalogSources.OpenRouter, "yes"), A(CatalogSources.Name, "no")]);
        Assert.True(fact.Disagree);
        Assert.Null(fact.Value);
        Assert.Equal(3, fact.Answers.Count);
    }

    [Fact]
    public void The_first_level_that_answers_decides_and_only_some_variants_is_no_answer()
    {
        var fact = CatalogResolver.ResolveModel(CatalogFacts.InputAudio,
            [A(CatalogSources.Vllm, CatalogValues.Some), A(CatalogSources.OpenRouter, "no"), A(CatalogSources.ModelsDev, "no"), A(CatalogSources.VllmFamily, CatalogValues.Some)]);
        Assert.Equal(("no", CatalogSources.OpenRouter, false), (fact.Value, fact.From, fact.Disagree));
        var maker = CatalogResolver.ResolveModel(CatalogFacts.InputAudio, [A(CatalogSources.ModelsDev, "yes"), A(CatalogSources.ConfigJson, "no")]);
        Assert.Equal(("no", CatalogSources.ConfigJson), (maker.Value, maker.From));
    }

    [Fact]
    public void Numbers_within_five_percent_and_dates_in_the_same_month_agree()
    {
        Assert.Equal("262144", CatalogResolver.ResolveModel(CatalogFacts.Context,
            [A(CatalogSources.OpenRouter, "262144"), A(CatalogSources.ModelsDev, "256000")]).Value);
        Assert.True(CatalogResolver.ResolveModel(CatalogFacts.Context,
            [A(CatalogSources.OpenRouter, "131072"), A(CatalogSources.ModelsDev, "262144")]).Disagree);
        Assert.Equal("2024-09-30", CatalogResolver.ResolveModel(CatalogFacts.KnowledgeCutoff,
            [A(CatalogSources.ModelsDev, "2024-09"), A(CatalogSources.OpenRouter, "2024-09-30")]).Value);
        Assert.Equal("Apache 2.0", CatalogResolver.ResolveModel(CatalogFacts.License,
            [A(CatalogSources.ModelsDev, "Apache 2.0"), A(CatalogSources.NvidiaBuild, "apache-2.0")]).Value);
    }

    [Fact]
    public void A_route_uses_a_test_then_the_server_then_the_model_then_its_own_providers_row()
    {
        var model = new CatalogFact { Value = "yes", From = CatalogSources.Vllm };
        Assert.Equal("yes", CatalogResolver.ResolveRoute(CatalogFacts.InputImage, [A(CatalogSources.ModelsDevRows, "no")], null, model).Value);
        Assert.Equal("no", CatalogResolver.ResolveRoute(CatalogFacts.InputImage,
            [A(CatalogSources.OpenRouter, "no"), A(CatalogSources.ModelsDevRows, "yes")], CatalogSources.OpenRouter, model).Value);
        Assert.Equal("yes", CatalogResolver.ResolveRoute(CatalogFacts.InputImage,
            [A(CatalogSources.OpenRouter, "no"), A(CatalogSources.MartletTest, "yes")], CatalogSources.OpenRouter, model).Value);
        Assert.Equal("no", CatalogResolver.ResolveRoute(CatalogFacts.InputAudio, [A(CatalogSources.ModelsDevRows, "no")], null, CatalogFact.Unknown).Value);
    }

    [Fact]
    public void Gemma_4_26B_takes_video_although_models_dev_leaves_it_out()
    {
        var model = ModelCatalog.Build(Measured()).Find("google/gemma-4-26b-a4b-it")!.Model;
        var video = model.Fact(CatalogFacts.InputVideo);
        Assert.Equal(("yes", CatalogSources.VllmFamily), (video.Value, video.From));
        Assert.Contains(video.Answers, a => a.Source == CatalogSources.ModelsDev && a.Value == "no");
        Assert.True(model.VideoAsFrames);
        Assert.Equal("google/gemma-4-26B-A4B-it", model.Key);
    }

    [Fact]
    public void Qwen3_5_122B_does_not_hear_although_models_dev_and_a_provider_say_it_does()
    {
        var catalog = ModelCatalog.Build(Measured());
        var model = catalog.Find("Qwen/Qwen3.5-122B-A10B")!.Model;
        Assert.Equal(("no", CatalogSources.VllmFamily), (model.Fact(CatalogFacts.InputAudio).Value, model.Fact(CatalogFacts.InputAudio).From));
        Assert.Equal("no", catalog.Route("deepinfra", "Qwen/Qwen3.5-122B-A10B")!.Fact(CatalogFacts.InputAudio).Value);
    }

    [Fact]
    public void Each_gemma_4_31b_route_keeps_only_its_own_providers_row()
    {
        var catalog = ModelCatalog.Build(Measured());
        var model = catalog.Find("google/gemma-4-31b-it")!.Model;
        Assert.Equal(3, catalog.RoutesOf(model).Count);
        var groq = catalog.Route("https://api.groq.com/openai/v1", "google/gemma-4-31b-it")!;
        Assert.Equal([("no")], groq.Fact(CatalogFacts.InputImage).Answers.Where(a => a.Source == CatalogSources.ModelsDevRows).Select(a => a.Value));
        Assert.Equal((true, "no price for input or output"), (groq.Free, groq.FreeNote));
        var together = catalog.Route("togetherai", "gemma-4-31b")!;
        Assert.True(together.Deprecated);
        Assert.Single(together.Fact(CatalogFacts.InputAudio).Answers, a => a.Source == CatalogSources.ModelsDevRows);
        // One provider listing audio doesn't make the model hear; the model's own answer comes from its record.
        Assert.NotEqual(true, model.Hears);
    }

    [Fact]
    public void Config_json_settles_audio_for_each_size()
    {
        var catalog = ModelCatalog.Build(Measured(withConfig: true));
        var e2b = catalog.Find("gemma4:e2b")!.Model;
        Assert.Equal(("yes", CatalogSources.ConfigJson), (e2b.Fact(CatalogFacts.InputAudio).Value, e2b.Fact(CatalogFacts.InputAudio).From));
        Assert.Equal(("5.12", CatalogSources.HuggingFace), (e2b.Fact(CatalogFacts.ParametersTotal).Value, e2b.Fact(CatalogFacts.ParametersTotal).From));
        Assert.True(e2b.LocallyHostable);
        var big = catalog.Find("gemma4:26b")!.Model;
        Assert.Equal(("no", CatalogSources.ConfigJson), (big.Fact(CatalogFacts.InputAudio).Value, big.Fact(CatalogFacts.InputAudio).From));
    }

    [Fact]
    public void A_model_is_found_by_any_of_its_names()
    {
        var catalog = ModelCatalog.Build(Measured());
        Assert.Equal("Ollama tag", catalog.Find("gemma4:26b")!.How);
        Assert.Equal("google/gemma-4-26B-A4B-it", catalog.Find("google/gemma-4-26b-a4b-it:free")!.Model.Key);
        Assert.Equal("google/gemma-4-26B-A4B-it", catalog.Find("hf.co/unsloth/gemma-4-26B-A4B-it-GGUF:Q4_K_M")!.Model.Key);
        Assert.Equal("alibaba/qwen3.5-122b-a10b", catalog.Find("Qwen/Qwen3.5-122B-A10B")!.Model.Names.ModelsDev);
        Assert.Equal("Qwen/Qwen3.5-122B-A10B", catalog.Find("qwen3.5:122b")!.Model.Key);
        Assert.Null(catalog.Find("nobody/never-heard-of-it"));
    }

    [Fact]
    public void Smartness_falls_back_to_the_same_family_and_size_then_similar_models_then_the_tier()
    {
        var catalog = ModelCatalog.Build(Measured());
        var own = catalog.Smartness(catalog.Find("google/gemma-4-26b-a4b-it")!.Model);
        Assert.Equal(("its own scores", 3), (own.From, own.Tier));
        Assert.Null(own.Credit);
        var tier = catalog.Smartness("someone/unknown-70b");
        Assert.Equal((5, "the quality tier from its name"), (tier.Tier, tier.From));
        var twin = new CatalogModel { Key = "unsloth/gemma-4-26B-A4B-it-qat", Name = "QAT", Family = "gemma4", Size = "26b-a4b" };
        var withTwin = ModelCatalog.Build(Measured()).Smartness(twin);
        Assert.Contains("same family and size", withTwin.From, StringComparison.Ordinal);
    }

    [Fact]
    public void LmArena_ratings_join_by_name_without_effort_words_and_carry_their_credit()
    {
        var data = Measured().With(CatalogSources.LmArenaText, Block(
            new() { Id = "gemma-4-26b-a4b", Rating = 1440, Votes = 7000 }, new() { Id = "qwen3.5-122b-a10b-high", Rating = 1450 },
            new() { Id = "qwen3.5-122b-a10b-low", Rating = 1430 }));
        var catalog = ModelCatalog.Build(data);
        var qwen = catalog.Find("qwen3.5-122b-a10b-low")!.Model;
        Assert.Equal((1450.0, "qwen3.5-122b-a10b-high"), (qwen.Rating!.Value, qwen.Names.LmArena));
        Assert.Equal(CatalogSources.LmArenaCredit, catalog.Smartness(qwen).Credit);
    }

    [Fact]
    public void Names_give_family_size_parameters_and_a_comparison_key()
    {
        Assert.Equal(("gemma-4", "26b-a4b"), (CatalogNaming.Family("google/gemma-4-26B-A4B-it"), CatalogNaming.Size("google/gemma-4-26B-A4B-it")));
        Assert.Equal("gemma4", CatalogNaming.CompactFamily("google/gemma-4-E2B-it"));
        Assert.Equal((26.0, 4.0), CatalogNaming.Parameters("gemma-4-26B-A4B-it"));
        Assert.Equal((31.0, 31.0), CatalogNaming.Parameters("gemma-4-31B-it"));
        Assert.Equal(((double?)null, 2.0), CatalogNaming.Parameters("gemma-4-E2B-it"));
        Assert.Equal(((double?)null, 17.0), CatalogNaming.Parameters("Llama-4-Scout-17B-16E-Instruct"));
        Assert.Equal("claude-opus-4-6", CatalogNaming.Key("claude-opus-4-6-high"));
        Assert.Equal("claude-opus-4-6", CatalogNaming.Key("anthropic/claude-opus-4.6"));
        Assert.Equal("claude-opus-4-5", CatalogNaming.Key("claude-opus-4-5-20251101-high-32k"));
        Assert.Equal("gemini-3-flash", CatalogNaming.Key("gemini-3-flash (thinking-minimal)"));
        Assert.Equal(("gemma4", (string?)"26b"), CatalogNaming.Ollama("gemma4:26b")!.Value);
        Assert.Equal("unsloth/gemma-4-26B-A4B-it", CatalogNaming.HuggingFaceOf("hf.co/unsloth/gemma-4-26B-A4B-it-GGUF:Q4_K_M"));
    }

    [Fact]
    public void OpenRouter_keeps_where_the_analysis_index_ranks_never_the_index()
    {
        var items = CatalogReaders.OpenRouter("""
            {"data":[
            {"id":"a/one","hugging_face_id":"A/One-7B","context_length":8192,"architecture":{"input_modalities":["text","image","video"],"output_modalities":["text"]},"pricing":{"prompt":"0","completion":"0"},"supported_parameters":["tools","include_reasoning"],"benchmarks":{"artificial_analysis":{"intelligence_index":10}}},
            {"id":"b/two","hugging_face_id":"","context_length":400000,"architecture":{"input_modalities":["text","audio"],"output_modalities":["text","audio"]},"pricing":{"prompt":"0.000001","completion":"0.000002"},"supported_parameters":[],"knowledge_cutoff":"2024-09-30","expiration_date":"2026-11-01","benchmarks":{"artificial_analysis":{"intelligence_index":30}}}
            ]}
            """);
        Assert.Equal([25.0, 75.0], items.Select(i => i.Rank!.Value));
        Assert.Equal(("yes", "yes", "no", "yes", "yes", true), (items[0].Fact(CatalogFacts.InputImage), items[0].Fact(CatalogFacts.InputVideo),
            items[0].Fact(CatalogFacts.InputAudio), items[0].Fact(CatalogFacts.Tools), items[0].Fact(CatalogFacts.Reasoning), items[0].Free));
        Assert.Equal(("A/One-7B", "yes"), (items[0].HuggingFace, items[0].Fact(CatalogFacts.OpenWeights)));
        Assert.Equal(((string?)null, "2026-11-01", "yes", false), (items[1].HuggingFace, items[1].Expires, items[1].Fact(CatalogFacts.OutputAudio), items[1].Free));
        Assert.DoesNotContain("intelligence", new ModelCatalogData().With(CatalogSources.OpenRouter, Block([.. items])).Write(), StringComparison.Ordinal);
    }

    [Fact]
    public void NVIDIA_pages_give_inputs_context_tools_and_the_repository_and_skip_other_services()
    {
        var page = CatalogReaders.NvidiaPage("""
            ---
            title: "gemma-4-31b-it"
            type: "endpoint"
            canonical: "https://build.nvidia.com/google/gemma-4-31b-it"
            ---
            **Huggingface:** 04/02/2026 via [link](https://huggingface.co/google/gemma-4-31B-it)
            **Data Modality:** Text, Image, Audio
            **Active Parameters:** 4B

            ## Specifications

            - **Context Length:** 262,144 tokens
            - **Parameters:** 2.8T
            - **Input:** Text, Image (optional), Video
            - **Output:** Text

            ## Capabilities

            - **Function Calling:** Supported
            - **Reasoning:** Not supported
            """, "/x/gemma.md")!;
        Assert.Equal(("google/gemma-4-31b-it", "google/gemma-4-31B-it", true), (page.Id, page.HuggingFace, page.Free));
        Assert.Equal(("yes", "yes", "no", "262144", "2800", "4"), (page.Fact(CatalogFacts.InputImage), page.Fact(CatalogFacts.InputVideo),
            page.Fact(CatalogFacts.InputAudio), page.Fact(CatalogFacts.Context), page.Fact(CatalogFacts.ParametersTotal), page.Fact(CatalogFacts.ParametersActive)));
        Assert.Equal(("yes", "no"), (page.Fact(CatalogFacts.Tools), page.Fact(CatalogFacts.Reasoning)));
        Assert.Null(CatalogReaders.NvidiaPage("---\ntitle: \"Body Pose\"\ncanonical: \"https://build.nvidia.com/nvidia/body-pose\"\n---\n# Pose", "/x/pose.md"));
        var old = CatalogReaders.NvidiaPage("---\ntype: \"endpoint\"\ncanonical: \"https://build.nvidia.com/a/b\"\n---\n**Input Type(s):** Video, Audio, Image, Text <br>\n" +
            "**Input Context Length (ISL):** 256K", "/x/b.md")!;
        Assert.Equal(("yes", "262144"), (old.Fact(CatalogFacts.InputAudio), old.Fact(CatalogFacts.Context)));
        Assert.Equal(["/a.md", "/b.md"], CatalogReaders.NvidiaLinks("- [A](/a.md) x\n- [B](/b.md)\n- [A](/a.md)"));
    }

    [Fact]
    public void VLLM_footnotes_and_brackets_mean_only_some_variants()
    {
        var rows = CatalogReaders.Vllm("""
            ## List of Text-only Language Models
            | Architecture | Models | Example HF Models | LoRA | PP |
            | --- | --- | --- | --- | --- |
            | `Gemma4ForCausalLM` | Gemma 4 | `google/gemma-4-E2B-it`, etc. | ✅︎ | ✅︎ |
            ## List of Multimodal Language Models
            | Architecture | Models | Inputs | Example HF Models | LoRA | PP |
            | --- | --- | --- | --- | --- | --- |
            | `Gemma4ForConditionalGeneration` | Gemma 4 | T + I<sup>+</sup> + V + A<sup>*</sup> | `google/gemma-4-E2B-it`, etc. | ✅︎ | ✅︎ |
            | `InternVLChatModel` | InternVL | T + I<sup>E+</sup> + (V<sup>E+</sup>) | `OpenGVLab/InternVL3-9B` | ✅︎ | ✅︎ |
            | `A`, `B` | Both | T + I<sup>+</sup> / T + A<sup>+</sup> | `x/y` | | |
            """);
        Assert.Equal(["Gemma4ForCausalLM", "Gemma4ForConditionalGeneration", "InternVLChatModel", "A", "B"], rows.Select(r => r.Id));
        Assert.True(rows[0].TextOnly);
        Assert.Equal(("yes", "yes", "some"), (rows[1].Fact(CatalogFacts.InputImage), rows[1].Fact(CatalogFacts.InputVideo), rows[1].Fact(CatalogFacts.InputAudio)));
        Assert.Equal("some", rows[2].Fact(CatalogFacts.InputVideo));
        Assert.Equal(("yes", "yes", "no"), (rows[3].Fact(CatalogFacts.InputImage), rows[3].Fact(CatalogFacts.InputAudio), rows[3].Fact(CatalogFacts.InputVideo)));
    }

    [Fact]
    public void LmArena_pages_stop_at_the_next_category_and_models_dev_rows_keep_only_known_providers()
    {
        var arena = CatalogReaders.LmArena("""{"rows":[{"row":{"model_name":"m1","rating":1500.04,"vote_count":10,"category":"overall","license":"MIT"}},{"row":{"model_name":"m2","rating":1400,"category":"chinese"}}]}""", out var more);
        Assert.False(more);
        Assert.Equal(("m1", 1500.0, 10, "MIT"), (arena.Single().Id, arena[0].Rating!.Value, arena[0].Votes!.Value, arena[0].Fact(CatalogFacts.License)));
        var rows = CatalogReaders.ModelsDevRows("""
            {"groq":{"models":{"m":{"id":"m","canonical_model_id":"o/m","status":"deprecated","modalities":{"input":["text","image"]},"cost":{"input":0,"output":0},"limit":{"context":8192}}}},
             "somewhere-else":{"models":{"m":{"id":"m"}}}}
            """);
        Assert.Equal(("groq", "https://api.groq.com/openai/v1", "o/m", true, true, "8192"),
            (rows.Single().Provider, rows[0].BaseUrl, rows[0].Canonical, rows[0].Free, rows[0].Deprecated, rows[0].Fact(CatalogFacts.Context)));
    }

    [Fact]
    public void The_store_uses_the_newer_copy_keeps_the_last_good_one_and_refreshes_once_a_day()
    {
        var folder = Path.Combine(Path.GetTempPath(), "Martlet.Tests", "catalog-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ModelCatalogStore(folder);
            Assert.True(store.Due(Read));
            Assert.Equal("snapshot", store.Current().From);
            var data = Measured() with { Built = DateTimeOffset.UtcNow.AddYears(5) };
            Assert.Null(store.Save(data));
            Assert.Equal(("daily copy", data.Built), (store.Current().From, store.Current().Data.Built));
            Assert.Equal(data.Write(), ModelCatalogData.Parse(File.ReadAllBytes(store.FilePath))!.Write());
            Assert.NotNull(store.Load().Find("gemma4:26b"));
            File.WriteAllText(store.FilePath, "{ broken");
            Assert.Null(store.Cached());
            Assert.Equal("snapshot", store.Current().From);
            Assert.Null(store.SaveStatus(new ModelCatalogStatus { LastAttempt = Read }));
            Assert.False(store.Due(Read.AddHours(23)));
            Assert.True(store.Due(Read.AddHours(24)));
            Assert.True(store.Due(Read.AddHours(-1)));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }
}
