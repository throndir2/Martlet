using System.Text.Json;

namespace Martlet.Providers.Ollama;

/// <summary>Gemma 4 tags on Ollama bundle a small speculative-decoding (MTP) draft model that llama.cpp loads after the
/// main weights. When those leave no graphics memory free (Windows often reports none at that point), the draft fails
/// and takes the whole load with it: "Gemma4Assistant requires ctx_other to be set ... error loading model: vector"
/// (ggml-org/llama.cpp#24795). Saving <c>draft_num_predict 0</c> on the tag makes Ollama skip the draft for every caller,
/// including its OpenAI-compatible endpoint, at the cost of the draft's speed-up.</summary>
public static class OllamaDraftHead
{
    public const string CreatePath = "/api/create";

    /// <summary>Whether Ollama's error is the draft model failing to load.</summary>
    public static bool FailedToLoad(string? error) => error is not null && (
        error.Contains("ctx_other", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("failed to load draft model", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("invalid vector subscript", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("error loading model: vector", StringComparison.OrdinalIgnoreCase));

    /// <summary>The /api/create body that saves <c>draft_num_predict 0</c> on <paramref name="model"/> itself, keeping its
    /// weights and other parameters (Ollama merges new parameters with the inherited ones).</summary>
    public static string DisableRequest(string model) => JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["model"] = model,
        ["from"] = model,
        ["parameters"] = new Dictionary<string, int> { ["draft_num_predict"] = 0 },
        ["stream"] = false
    });
}
