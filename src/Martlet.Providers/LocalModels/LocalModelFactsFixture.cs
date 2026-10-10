using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Martlet.Providers.LocalModels;

/// <summary>FIXTURE (NOT the real services): an in-process transport shaped like Hugging Face's model API, its raw
/// <c>config.json</c> files, its GGUF search and the Ollama registry, for rehearsing <see cref="LocalModelFactsReader"/> without the
/// network. The numbers are the real ones read on 2026-10-10 for Gemma 4 E2B, E4B and 26B A4B and Qwen3.5 4B (sizes, parameter
/// counts, and the config fields Martlet reads; the configs are cut down to those fields). <see cref="MeasuredGraphicsGb"/> is what
/// those Ollama tags measured on a graphics card at 8,192 tokens (Resource footprints: RTX 4070, Ollama 0.35.1, 2026-10-04).</summary>
public static class LocalModelFactsFixture
{
    public static IReadOnlyDictionary<string, double> MeasuredGraphicsGb { get; } = new Dictionary<string, double>(StringComparer.Ordinal)
    {
        ["gemma4:e2b"] = 3.3, ["gemma4:e4b"] = 4.9, ["qwen3.5:4b"] = 4.1
    };

    /// <summary>The Hugging Face repository of each fixture Ollama tag.</summary>
    public static IReadOnlyDictionary<string, string> Repos { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["gemma4:e2b"] = "google/gemma-4-E2B-it", ["gemma4:e4b"] = "google/gemma-4-E4B-it", ["gemma4:26b"] = "google/gemma-4-26B-A4B-it",
        ["qwen3.5:4b"] = "Qwen/Qwen3.5-4B"
    };

    /// <summary>A reader on the fixture transport. <paramref name="seen"/> collects each path asked.</summary>
    public static LocalModelFactsReader Reader(ICollection<string>? seen = null, TimeProvider? clock = null, int budget = LocalModelFactsReader.RequestBudget) =>
        new(new HttpClient(new Handler(seen)), new Uri("https://huggingface.fixture/"), new Uri("https://registry.fixture/"), clock, budget);

    private sealed class Handler(ICollection<string>? seen) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var query = Uri.UnescapeDataString(request.RequestUri.Query);
            if (seen is not null) lock (seen) seen.Add(path + (path == "/api/models" ? query : ""));
            var body = Answer(path, query);
            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"error\":\"not found\"}") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private static string? Answer(string path, string query) => path switch
    {
        "/api/models" when query.Contains("base_model:quantized:google/gemma-4-E2B-it", StringComparison.Ordinal) =>
            Json(new[] { new { id = "bartowski/google_gemma-4-E2B-it-GGUF" }, new { id = "unsloth/gemma-4-E2B-it-GGUF" }, new { id = "ggml-org/gemma-4-E2B-it-GGUF" } }),
        "/api/models" => "[]",
        "/api/models/google/gemma-4-E2B-it" => Info("google/gemma-4-E2B-it", "any-to-any", "google/gemma-4-E2B", 5_123_178_051),
        "/api/models/google/gemma-4-E4B-it" => Info("google/gemma-4-E4B-it", "any-to-any", "google/gemma-4-E4B", 7_996_156_490),
        "/api/models/google/gemma-4-26B-A4B-it" => Info("google/gemma-4-26B-A4B-it", "image-text-to-text", "google/gemma-4-26B-A4B", 25_805_936_206),
        "/api/models/Qwen/Qwen3.5-4B" => Info("Qwen/Qwen3.5-4B", "image-text-to-text", "Qwen/Qwen3.5-4B-Base", 4_659_865_088),
        "/api/models/ggml-org/gemma-4-E2B-it-GGUF" => Json(new
        {
            id = "ggml-org/gemma-4-E2B-it-GGUF", pipeline_tag = "any-to-any", gated = false,
            cardData = new { license = "apache-2.0", base_model = "google/gemma-4-E2B-it" },
            gguf = new { total = 4_647_450_147, architecture = "gemma4", context_length = 131_072 },
            siblings = new object[]
            {
                new { rfilename = "README.md", size = 621 },
                new { rfilename = "gemma-4-E2B-it-BF16.gguf", size = 9_311_305_152 },
                new { rfilename = "gemma-4-E2B-it-Q4_0.gguf", size = 2_841_481_184 },
                new { rfilename = "gemma-4-E2B-it-Q8_0.gguf", size = 4_967_497_152 },
                new { rfilename = "mmproj-gemma-4-E2B-it-BF16.gguf", size = 986_833_664 },
                new { rfilename = "mmproj-gemma-4-E2B-it-Q8_0.gguf", size = 557_368_064 },
                new { rfilename = "mtp-gemma-4-E2B-it-Q8_0.gguf", size = 97_817_696 }
            }
        }),
        "/google/gemma-4-E2B-it/raw/main/config.json" => Gemma(35, 8, 1, null, 1536, 512, 20, 5, ple: true, audio: true, 131_072),
        "/google/gemma-4-E4B-it/raw/main/config.json" => Gemma(42, 8, 2, null, 2560, 512, 18, 6, ple: true, audio: true, 131_072),
        "/google/gemma-4-26B-A4B-it/raw/main/config.json" => Gemma(30, 16, 8, 2, 2816, 1024, 0, 6, ple: false, audio: false, 262_144, 128, 8, 704),
        "/Qwen/Qwen3.5-4B/raw/main/config.json" => Json(new
        {
            model_type = "qwen3_5", video_token_id = 248_057, vision_config = new { model_type = "qwen3_5" },
            text_config = new
            {
                num_hidden_layers = 32, num_attention_heads = 16, num_key_value_heads = 4, head_dim = 256, hidden_size = 2560,
                layer_types = Enumerable.Range(0, 32).Select(i => (i + 1) % 4 == 0 ? "full_attention" : "linear_attention").ToArray(),
                linear_num_value_heads = 32, linear_num_key_heads = 16, linear_key_head_dim = 128, linear_value_head_dim = 128,
                linear_conv_kernel_dim = 4, max_position_embeddings = 262_144
            }
        }),
        _ when path.StartsWith("/v2/library/", StringComparison.Ordinal) => Registry(path),
        _ => null
    };

    private static readonly (string Model, string Tag, long Weights, long Projector, long Draft, string Config)[] Tags =
    [
        ("gemma4", "e2b", 3_502_493_472, 986_833_664, 97_817_696, Blob("gemma4", "4.6B")),
        ("gemma4", "e4b", 5_493_439_296, 991_552_256, 98_653_280, Blob("gemma4", "7.5B")),
        ("gemma4", "26b", 17_074_419_072, 1_194_828_128, 461_766_752, Blob("gemma4", "25.2B")),
        ("qwen3.5", "4b", 2_648_593_216, 675_569_120, 0, Blob("qwen35", "4.2B"))
    ];

    private static string? Registry(string path)
    {
        foreach (var tag in Tags)
        {
            var digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(tag.Config)));
            if (path == $"/v2/library/{tag.Model}/blobs/{digest}") return tag.Config;
            if (path != $"/v2/library/{tag.Model}/manifests/{tag.Tag}") continue;
            var layers = new List<object> { new { mediaType = "application/vnd.ollama.image.model", digest = "sha256:" + new string('a', 64), size = tag.Weights } };
            if (tag.Projector > 0) layers.Add(new { mediaType = "application/vnd.ollama.image.projector", digest = "sha256:" + new string('b', 64), size = tag.Projector });
            if (tag.Draft > 0) layers.Add(new { mediaType = "application/vnd.ollama.image.draft", digest = "sha256:" + new string('c', 64), size = tag.Draft });
            layers.Add(new { mediaType = "application/vnd.ollama.image.template", digest = "sha256:" + new string('d', 64), size = 13 });
            return Json(new
            {
                schemaVersion = 2, mediaType = "application/vnd.docker.distribution.manifest.v2+json",
                config = new { mediaType = "application/vnd.docker.container.image.v1+json", digest, size = tag.Config.Length },
                layers
            });
        }
        return null;
    }

    private static string Blob(string family, string parameters) =>
        Json(new { model_format = "gguf", model_family = family, model_families = new[] { family }, model_type = parameters, file_type = "Q4_K_M" });

    private static string Info(string id, string task, string made, long parameters) => Json(new
    {
        id, pipeline_tag = task, gated = false, cardData = new { license = "apache-2.0", pipeline_tag = task, base_model = new[] { made } },
        safetensors = new { total = parameters },
        siblings = new object[] { new { rfilename = "config.json", size = 5000 }, new { rfilename = "model.safetensors", size = parameters * 2 } }
    });

    // Gemma 4: every `pattern`th layer attends fully, the rest to a sliding window; the E models share the cache of their last
    // layers and have per-layer embeddings; 26B A4B has 128 experts, 8 a token.
    private static string Gemma(int layers, int heads, int kvHeads, int? globalKvHeads, int hidden, int window, int shared, int pattern,
        bool ple, bool audio, int maxContext, int? experts = null, int? topK = null, int? moe = null) => Json(new
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
            num_experts = experts, top_k_experts = topK, moe_intermediate_size = moe, max_position_embeddings = maxContext
        }
    });

    private static string Json(object value) => JsonSerializer.Serialize(value);
}
