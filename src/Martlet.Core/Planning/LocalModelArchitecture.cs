using System.Text.Json;

namespace Martlet.Core.Planning;

/// <summary>A model's shape as its <c>config.json</c> on Hugging Face gives it (the text model's part from <c>text_config</c> or
/// <c>llm_config</c> when present): the layers that keep a KV cache (the memory for the context), its key/value heads and head
/// size, sliding-window and linear-attention layers, experts, and which inputs the weights take besides text. Enough to work out
/// the KV cache at a context (<see cref="KvCacheBytes"/>) and a mixture-of-experts model's active parameters.</summary>
public sealed record LocalModelArchitecture
{
    public string? ModelType { get; init; }
    public int Layers { get; init; }
    /// <summary>Full-attention layers with a cache of their own: they keep every token of the context.</summary>
    public int FullLayers { get; init; }
    /// <summary>Sliding-window layers with a cache of their own: they keep at most <see cref="SlidingWindow"/> tokens.</summary>
    public int SlidingLayers { get; init; }
    /// <summary>Linear-attention (recurrent) layers: a fixed state of <see cref="LinearStateBytes"/> each, whatever the context.</summary>
    public int LinearLayers { get; init; }
    /// <summary>Last layers that use an earlier layer's cache (Gemma 4's <c>num_kv_shared_layers</c>), so keep none.</summary>
    public int SharedCacheLayers { get; init; }
    public int KvHeads { get; init; }
    public int HeadSize { get; init; }
    /// <summary>Key/value heads and head size of the full-attention layers when they differ (Gemma 4's global layers).</summary>
    public int FullKvHeads { get; init; }
    public int FullHeadSize { get; init; }
    public int? SlidingWindow { get; init; }
    /// <summary>Values a token keeps in each layer with a compressed (latent) cache, DeepSeek's <c>kv_lora_rank</c> plus
    /// <c>qk_rope_head_dim</c>; null for an ordinary key/value cache.</summary>
    public int? LatentCacheSize { get; init; }
    public long LinearStateBytes { get; init; }
    public int HiddenSize { get; init; }
    public int? Experts { get; init; }
    public int? ActiveExperts { get; init; }
    /// <summary>All experts' parameters together (a mixture-of-experts model keeps them all in memory), null for a dense model.</summary>
    public long? ExpertParameters { get; init; }
    /// <summary>Gemma's per-layer embeddings: llama.cpp and Ollama keep them in system memory, and a token reads only one row.</summary>
    public long PerLayerEmbeddingParameters { get; init; }
    public int? MaxContext { get; init; }
    /// <summary>The weights take pictures (<c>vision_config</c> or another vision tower is set).</summary>
    public bool Sees { get; init; }
    /// <summary>The weights take sound (<c>audio_config</c> or <c>sound_config</c> is set, not null).</summary>
    public bool Hears { get; init; }
    /// <summary>The config has a video token: the model takes video, at least as a series of frames.</summary>
    public bool VideoTokens { get; init; }

    public bool MixtureOfExperts => Experts is > 1 && ActiveExperts is > 0 && ActiveExperts < Experts;

    /// <summary>The KV cache of one context of <paramref name="contextTokens"/> tokens, with <paramref name="bytesPerValue"/> for
    /// each key or value (2 for f16, Ollama's default): full-attention layers keep every token, sliding-window layers keep only
    /// their window, and linear-attention layers a fixed state.</summary>
    public long KvCacheBytes(int contextTokens, int bytesPerValue = 2)
    {
        if (contextTokens <= 0) return 0;
        var sliding = Math.Min(contextTokens, SlidingWindow ?? contextTokens);
        return FullBytesPerToken(bytesPerValue) * contextTokens + SlidingBytesPerToken(bytesPerValue) * sliding + LinearLayers * LinearStateBytes;
    }

    /// <summary>What each token of context adds in the full-attention layers.</summary>
    public long FullBytesPerToken(int bytesPerValue = 2) => LatentCacheSize is { } latent
        ? (long)FullLayers * latent * bytesPerValue
        : 2L * FullLayers * FullKvHeads * FullHeadSize * bytesPerValue;

    public long SlidingBytesPerToken(int bytesPerValue = 2) => 2L * SlidingLayers * KvHeads * HeadSize * bytesPerValue;

    /// <summary>A mixture-of-experts model's parameters used for each token, from <paramref name="totalParameters"/> (the text
    /// model's): every expert's share less the experts a token doesn't use. <paramref name="totalParameters"/> for a dense model.</summary>
    public long ActiveParameters(long totalParameters) =>
        MixtureOfExperts && ExpertParameters is { } experts && experts < totalParameters
            ? totalParameters - (long)(experts * (1 - (double)ActiveExperts!.Value / Experts!.Value))
            : totalParameters;

    private static readonly string[] TextParts = ["text_config", "llm_config", "language_config", "text_model"];
    private static readonly string[] VisionParts = ["vision_config", "visual", "vision_tower", "mm_vision_tower", "vision_tower_config", "image_encoder"];
    private static readonly string[] SoundParts = ["audio_config", "sound_config", "audio_encoder_config", "speech_config", "audio_tower"];

    /// <summary>Reads a <c>config.json</c>; null when it has no layers or heads (not a language model's config).</summary>
    public static LocalModelArchitecture? FromConfig(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return FromConfig(document.RootElement);
        }
        catch (JsonException) { return null; }
    }

    public static LocalModelArchitecture? FromConfig(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        var text = root;
        foreach (var part in TextParts)
            if (root.TryGetProperty(part, out var inner) && inner.ValueKind == JsonValueKind.Object) { text = inner; break; }
        var layers = Int(text, "num_hidden_layers", "n_layer", "num_layers", "n_layers") ?? 0;
        var heads = Int(text, "num_attention_heads", "n_head", "num_heads") ?? 0;
        var hidden = Int(text, "hidden_size", "n_embd", "d_model", "dim") ?? 0;
        if (layers is <= 0 or > 10_000 || heads is <= 0 or > 10_000) return null;
        var kvHeads = Int(text, "num_key_value_heads", "multi_query_group_num", "num_kv_heads", "n_head_kv") ?? heads;
        var headSize = Int(text, "head_dim") ?? (hidden > 0 ? hidden / heads : 0);
        var shared = Math.Clamp(Int(text, "num_kv_shared_layers") ?? 0, 0, layers);
        var own = layers - shared;
        int? window = Bool(text, "use_sliding_window") == false ? null : Int(text, "sliding_window", "attention_window_size");
        var (full, slidingCount, linear) = LayerKinds(text, own, window is not null, Text(text, "model_type") ?? Text(root, "model_type"));
        if (window is null) (full, slidingCount) = (full + slidingCount, 0);
        int? latent = Int(text, "kv_lora_rank") is { } rank ? rank + (Int(text, "qk_rope_head_dim") ?? 0) : null;
        var experts = Int(text, "num_experts", "num_local_experts", "n_routed_experts", "moe_num_experts");
        var active = Int(text, "top_k_experts", "num_experts_per_tok", "experts_per_token", "num_experts_per_token", "moe_top_k");
        long? expertParameters = null;
        if (experts is > 1 && active is > 0 && hidden > 0 &&
            Int(text, "moe_intermediate_size", "expert_intermediate_size", "intermediate_size") is { } ffn and > 0)
        {
            var moeLayers = layers - Math.Clamp(Int(text, "first_k_dense_replace") ?? 0, 0, layers);
            expertParameters = (long)moeLayers * experts.Value * 3L * hidden * ffn;
        }
        var perLayerInput = Int(text, "hidden_size_per_layer_input") ?? 0;
        var perLayerVocabulary = Int(text, "vocab_size_per_layer_input") ?? 0;
        return new()
        {
            ModelType = Text(root, "model_type") ?? Text(text, "model_type"),
            Layers = layers,
            FullLayers = full,
            SlidingLayers = slidingCount,
            LinearLayers = linear,
            SharedCacheLayers = shared,
            KvHeads = kvHeads,
            HeadSize = headSize,
            FullKvHeads = Int(text, "num_global_key_value_heads") ?? kvHeads,
            FullHeadSize = Int(text, "global_head_dim") ?? headSize,
            SlidingWindow = window,
            LatentCacheSize = latent,
            LinearStateBytes = linear > 0 ? LinearState(text) : 0,
            HiddenSize = hidden,
            Experts = experts,
            ActiveExperts = active,
            ExpertParameters = expertParameters,
            PerLayerEmbeddingParameters = perLayerInput > 0 && perLayerVocabulary > 0 ? (long)perLayerInput * perLayerVocabulary * layers : 0,
            MaxContext = Int(text, "max_position_embeddings", "n_positions", "seq_length", "max_sequence_length"),
            Sees = VisionParts.Any(part => Set(root, part)),
            Hears = SoundParts.Any(part => Set(root, part)),
            VideoTokens = Int(root, "video_token_id", "video_token_index") is not null || Int(text, "video_token_id") is not null
        };
    }

    // How many of the first `own` layers attend fully, to a sliding window, or linearly: layer_types when the config lists them,
    // else Gemma 3's sliding_window_pattern (every Nth layer full), Gemma 2's alternating layers, or every layer sliding.
    private static (int Full, int Sliding, int Linear) LayerKinds(JsonElement text, int own, bool windowed, string? modelType)
    {
        if (text.TryGetProperty("layer_types", out var types) && types.ValueKind == JsonValueKind.Array && types.GetArrayLength() > 0)
        {
            int full = 0, sliding = 0, linear = 0;
            foreach (var type in types.EnumerateArray().Take(own))
                switch (type.ValueKind == JsonValueKind.String ? type.GetString() : null)
                {
                    case "sliding_attention" or "chunked_attention" or "local_attention": sliding++; break;
                    case "linear_attention" or "mamba" or "recurrent" or "conv": linear++; break;
                    default: full++; break;
                }
            return (full + Math.Max(0, own - types.GetArrayLength()), sliding, linear);
        }
        if (!windowed) return (own, 0, 0);
        if (Int(text, "sliding_window_pattern") is { } pattern and > 1)
        {
            var full = Enumerable.Range(0, own).Count(i => (i + 1) % pattern == 0);
            return (full, own - full, 0);
        }
        if (modelType?.StartsWith("gemma2", StringComparison.OrdinalIgnoreCase) == true) return (own / 2, own - own / 2, 0);
        return (0, own, 0);
    }

    // Qwen3.5's and Qwen3-Next's gated delta-net layers keep, in f32, a short convolution state and one key x value matrix for
    // each value head; Mamba layers keep heads x head size x state size.
    private static long LinearState(JsonElement text)
    {
        if (Int(text, "linear_num_value_heads") is { } valueHeads && Int(text, "linear_key_head_dim") is { } keySize &&
            Int(text, "linear_value_head_dim") is { } valueSize)
        {
            var keyHeads = Int(text, "linear_num_key_heads") ?? valueHeads;
            var kernel = Int(text, "linear_conv_kernel_dim") ?? 4;
            return 4L * valueHeads * keySize * valueSize + 4L * Math.Max(0, kernel - 1) * (2L * keyHeads * keySize + (long)valueHeads * valueSize);
        }
        if (Int(text, "mamba_d_state", "state_size") is { } state && Int(text, "mamba_num_heads", "mamba_n_heads") is { } mambaHeads &&
            Int(text, "mamba_head_dim") is { } mambaHeadSize)
            return 4L * state * mambaHeads * mambaHeadSize;
        return 0;
    }

    private static bool Set(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.Object or JsonValueKind.String &&
        (value.ValueKind != JsonValueKind.String || value.GetString() is { Length: > 0 });

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static bool? Bool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    private static int? Int(JsonElement element, params string[] names)
    {
        foreach (var name in names)
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) &&
                number is > 0 and <= int.MaxValue)
                return (int)number;
        return null;
    }
}
