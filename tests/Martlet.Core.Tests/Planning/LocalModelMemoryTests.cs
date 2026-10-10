using Martlet.Core.Planning;

namespace Martlet.Core.Tests.Planning;

public sealed class LocalModelMemoryTests
{
    // Gemma 4's config.json fields Martlet reads (from huggingface.co, 2026-10-10): E2B and E4B share the cache of their last
    // layers and have per-layer embeddings and audio; 26B A4B has 128 experts, 8 a token, two global KV heads and no audio.
    private static string Gemma(int layers, int heads, int kvHeads, int? globalKvHeads, int hidden, int window, int shared, int pattern,
        bool ple, bool audio, int? experts = null, int? topK = null, int? moe = null) => System.Text.Json.JsonSerializer.Serialize(new
    {
        model_type = "gemma4", video_token_id = 258_884,
        audio_config = audio ? new { model_type = "gemma4_audio" } : null,
        vision_config = new { model_type = "gemma4_vision" },
        text_config = new
        {
            num_hidden_layers = layers, num_attention_heads = heads, num_key_value_heads = kvHeads, num_global_key_value_heads = globalKvHeads,
            head_dim = 256, global_head_dim = 512, hidden_size = hidden, sliding_window = window, num_kv_shared_layers = shared,
            layer_types = Enumerable.Range(0, layers).Select(i => (i + 1) % pattern == 0 ? "full_attention" : "sliding_attention").ToArray(),
            hidden_size_per_layer_input = ple ? 256 : 0, vocab_size_per_layer_input = 262_144,
            num_experts = experts, top_k_experts = topK, moe_intermediate_size = moe, intermediate_size = 2112, max_position_embeddings = 131_072
        }
    });

    private static LocalModelArchitecture E2B => LocalModelArchitecture.FromConfig(Gemma(35, 8, 1, null, 1536, 512, 20, 5, ple: true, audio: true))!;
    private static LocalModelArchitecture E4B => LocalModelArchitecture.FromConfig(Gemma(42, 8, 2, null, 2560, 512, 18, 6, ple: true, audio: true))!;
    private static LocalModelArchitecture A4B => LocalModelArchitecture.FromConfig(Gemma(30, 16, 8, 2, 2816, 1024, 0, 6, ple: false, audio: false, 128, 8, 704))!;

    private const string Qwen35 = """
        {"model_type":"qwen3_5","video_token_id":248057,"vision_config":{"model_type":"qwen3_5"},
         "text_config":{"num_hidden_layers":8,"num_attention_heads":16,"num_key_value_heads":4,"head_dim":256,"hidden_size":2560,
          "layer_types":["linear_attention","linear_attention","linear_attention","full_attention","linear_attention","linear_attention","linear_attention","full_attention"],
          "linear_num_value_heads":32,"linear_num_key_heads":16,"linear_key_head_dim":128,"linear_value_head_dim":128,"linear_conv_kernel_dim":4}}
        """;

    [Fact]
    public void Gemma_4_cache_matches_resource_footprints()
    {
        // Resource footprints: own-cache layers 3/12, 4/20 and 5/25; 6, 16 and 20 KiB a token in the full-attention layers.
        Assert.Equal((3, 12, 20), (E2B.FullLayers, E2B.SlidingLayers, E2B.SharedCacheLayers));
        Assert.Equal((4, 20), (E4B.FullLayers, E4B.SlidingLayers));
        Assert.Equal((5, 25), (A4B.FullLayers, A4B.SlidingLayers));
        Assert.Equal(6 * 1024, E2B.FullBytesPerToken());
        Assert.Equal(16 * 1024, E4B.FullBytesPerToken());
        Assert.Equal(20 * 1024, A4B.FullBytesPerToken());
        Assert.InRange(E2B.KvCacheBytes(8_192) / 1e9, 0.05, 0.06);
        Assert.InRange(A4B.KvCacheBytes(8_192) / 1e9, 0.37, 0.39);
        // Sliding layers keep only their window: a longer context grows only the full-attention part.
        Assert.Equal(A4B.FullBytesPerToken() * (32_768 - 8_192), A4B.KvCacheBytes(32_768) - A4B.KvCacheBytes(8_192));
    }

    [Fact]
    public void Config_says_which_inputs_the_weights_take()
    {
        Assert.True(E2B is { Sees: true, Hears: true, VideoTokens: true });
        Assert.True(A4B is { Sees: true, Hears: false });
        var text = LocalModelArchitecture.FromConfig("""{"model_type":"llama","num_hidden_layers":32,"num_attention_heads":32,"num_key_value_heads":8,"hidden_size":4096}""")!;
        Assert.True(text is { Sees: false, Hears: false, VideoTokens: false, FullLayers: 32, HeadSize: 128 });
        Assert.Null(LocalModelArchitecture.FromConfig("""{"model_type":"whatever"}"""));
        Assert.Null(LocalModelArchitecture.FromConfig("not json"));
    }

    [Fact]
    public void Turned_off_sliding_windows_linear_layers_and_experts_are_read()
    {
        var vl = LocalModelArchitecture.FromConfig("""
            {"model_type":"qwen2_5_vl","vision_config":{},"num_hidden_layers":28,"num_attention_heads":28,"num_key_value_heads":4,
             "hidden_size":3584,"sliding_window":32768,"use_sliding_window":false}
            """)!;
        Assert.Equal((28, 0, (int?)null), (vl.FullLayers, vl.SlidingLayers, vl.SlidingWindow));
        Assert.Equal(56 * 1024, vl.FullBytesPerToken());
        var qwen = LocalModelArchitecture.FromConfig(Qwen35)!;
        Assert.Equal((2, 6), (qwen.FullLayers, qwen.LinearLayers));
        Assert.Equal(4L * 32 * 128 * 128 + 4L * 3 * (2 * 16 * 128 + 32 * 128), qwen.LinearStateBytes);
        Assert.True(A4B.MixtureOfExperts);
        Assert.InRange(A4B.ActiveParameters(25_200_000_000) / 1e9, 3.7, 3.9);
        Assert.Equal(4_600_000_000, E2B.ActiveParameters(4_600_000_000));
    }

    [Theory]
    [InlineData("gemma4:e2b", 3_502_493_472, 986_833_664, 97_817_696, 4_600_000_000, 3.3)]
    [InlineData("gemma4:e4b", 5_493_439_296, 991_552_256, 98_653_280, 7_500_000_000, 4.9)]
    public void Estimate_is_within_ten_percent_of_what_gemma_measured(string tag, long weights, long encoder, long draft, long parameters, double measuredGb)
    {
        var architecture = tag == "gemma4:e2b" ? E2B : E4B;
        var estimate = LocalModelMemory.Estimate(new("Q4_K_M", weights, tag, "Ollama registry") { EncoderBytes = encoder, DraftBytes = draft },
            architecture, parameters, parameters);
        Assert.InRange(estimate.GraphicsBytes / 1e9, measuredGb * 0.9, measuredGb * 1.1);
        Assert.True(estimate.SystemMemoryBytes > 1_000_000_000, "Gemma's per-layer embeddings stay in system memory.");
        Assert.Equal(estimate.TotalBytes, estimate.GraphicsBytes + estimate.SystemMemoryBytes);
    }

    [Fact]
    public void A_mixture_of_experts_keeps_every_expert_in_memory_but_reads_only_the_active_ones()
    {
        var quantization = new LocalModelQuantization("Q4_K_M", 17_074_419_072, "gemma4:26b", "Ollama registry");
        var estimate = LocalModelMemory.Estimate(quantization, A4B, 25_200_000_000, A4B.ActiveParameters(25_200_000_000));
        Assert.Equal(17_074_419_072 + A4B.KvCacheBytes(8_192) + LocalModelMemory.BuffersBytes, estimate.TotalBytes);
        Assert.InRange(estimate.BytesPerToken / 1e9, 2.5, 3.2);
        Assert.InRange(estimate.TokensPerSecond(504)!.Value, 150, 200);
        var unknown = LocalModelMemory.Estimate(quantization, null, null, null);
        Assert.False(unknown.KvCacheKnown);
        Assert.Equal(quantization.WeightsBytes / 10, unknown.KvCacheBytes);
    }

    [Theory]
    [InlineData("gemma-4-E2B-it-UD-Q4_K_XL.gguf", "UD-Q4_K_XL")]
    [InlineData("google_gemma-4-E2B-it-bf16.gguf", "BF16")]
    [InlineData("Q8_0/Llama-3.3-70B-Instruct-Q8_0-00001-of-00002.gguf", "Q8_0")]
    [InlineData("gemma-4-E2B-it-IQ4_XS.gguf", "IQ4_XS")]
    [InlineData("mmproj-model-f16.gguf", "F16")]
    [InlineData("gpt-oss-20b-MXFP4.gguf", "MXFP4")]
    [InlineData("README.md", null)]
    public void Quantization_names_come_from_gguf_file_names(string file, string? expected) =>
        Assert.Equal(expected, LocalModelQuantizations.FromFileName(file));

    [Fact]
    public void Parameter_counts_install_names_and_card_bandwidths_are_read()
    {
        Assert.Equal(4_600_000_000, LocalModelQuantizations.ParseParameters("4.6B"));
        Assert.Equal(137_000_000, LocalModelQuantizations.ParseParameters("137M"));
        Assert.Null(LocalModelQuantizations.ParseParameters("big"));
        Assert.Equal(3_000_000_000, LocalModelQuantizations.ActiveFromName("Qwen/Qwen3-30B-A3B"));
        Assert.Null(LocalModelQuantizations.ActiveFromName("google/gemma-4-E2B-it"));
        Assert.Equal("hf.co/unsloth/gemma-4-E2B-it-GGUF:Q4_K_M", LocalModelQuantizations.HuggingFaceInstallName("unsloth/gemma-4-E2B-it-GGUF", "Q4_K_M"));
        Assert.Equal(672, GraphicsCardBandwidth.Find("NVIDIA GeForce RTX 4070 Ti SUPER")?.Gbps);
        Assert.Equal(504, GraphicsCardBandwidth.Find("NVIDIA GeForce RTX 4070")?.Gbps);
        Assert.Equal(273, GraphicsCardBandwidth.Find("Apple M4 Pro")?.Gbps);
        Assert.Null(GraphicsCardBandwidth.Find("Intel(R) UHD Graphics 770"));
    }

    [Fact]
    public void Facts_pick_the_ollama_tag_then_a_4_bit_quantization()
    {
        var facts = new LocalModelFacts
        {
            Query = "google/gemma-4-E2B-it", OllamaTag = "gemma4:e2b",
            Quantizations =
            [
                new("Q8_0", 4_967_497_152, "hf.co/ggml-org/gemma-4-E2B-it-GGUF:Q8_0", "Hugging Face"),
                new("Q4_0", 2_841_481_184, "hf.co/ggml-org/gemma-4-E2B-it-GGUF:Q4_0", "Hugging Face"),
                new("Q4_K_M", 3_502_493_472, "gemma4:e2b", "Ollama registry")
            ]
        };
        Assert.Equal("gemma4:e2b", facts.Quantization()!.InstallName);
        Assert.Equal("Q4_0", (facts with { OllamaTag = null, Quantizations = facts.Quantizations.Take(2).ToArray() }).Quantization()!.Name);
        Assert.Equal("Q8_0", facts.Quantization("q8_0")!.Name);
        Assert.True(facts.LocallyHostable);
        Assert.False(new LocalModelFacts { Query = "x/y" }.LocallyHostable);
    }

    [Fact]
    public void Measured_memory_is_kept_for_each_server_and_model()
    {
        var directory = Path.Combine(Path.GetTempPath(), "martlet-model-memory-" + Guid.NewGuid().ToString("N"));
        try
        {
            var use = new MeasuredModelUse
            {
                Host = "http://127.0.0.1:11434", Model = "gemma4:e2b", Bytes = 5_100_000_000, GraphicsBytes = 3_300_000_000,
                ContextTokens = 8_192, Digest = "abc", MeasuredAt = DateTimeOffset.UnixEpoch
            };
            Assert.True(MeasuredModelMemory.Record(directory, use));
            Assert.False(MeasuredModelMemory.Record(directory, use with { MeasuredAt = DateTimeOffset.UnixEpoch.AddDays(1) }));
            Assert.True(MeasuredModelMemory.Record(directory, use with { Host = "http://127.0.0.1:11435" }));
            Assert.True(MeasuredModelMemory.Record(directory, use with { Model = "qwen3.5", Bytes = 4_100_000_000, GraphicsBytes = 4_100_000_000 }));
            var kept = MeasuredModelMemory.Load(directory);
            Assert.Equal(3, kept.Models.Count);
            Assert.Equal(3_300_000_000, kept.Find("http://127.0.0.1:11434", "gemma4:e2b")!.GraphicsBytes);
            Assert.True(kept.Find("http://127.0.0.1:11434", "qwen3.5:latest")!.OnGraphicsCard);
            Assert.Equal(2, kept.FindAll("gemma4:e2b").Count());
            Assert.False(MeasuredModelMemory.Record(directory, use with { Bytes = 0 }));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
