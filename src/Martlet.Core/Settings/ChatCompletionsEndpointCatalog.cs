namespace Martlet.Core.Settings;

/// <summary>A named Chat Completions provider. DefaultModelId is only a prefilled suggestion; the owner still applies and consents.
/// Each default both talks and sees images (and calls tools), so screen watching works without changing models.
/// RetiredModelIds are models the provider has switched off for good (it answers HTTP 410 Gone), so a route on one cannot work.</summary>
public sealed record ChatCompletionsEndpointOption(string Name, string BaseUrl, string DefaultModelId, IReadOnlyList<string> RetiredModelIds)
{
    public bool Retired(string? modelId) => modelId is not null && RetiredModelIds.Contains(modelId.Trim(), StringComparer.Ordinal);
}

public static class ChatCompletionsEndpointCatalog
{
    public const string OpenRouterBaseUrl = "https://openrouter.ai/api/v1";
    public const string NvidiaBuildBaseUrl = "https://integrate.api.nvidia.com/v1";
    public const string OpenRouterAppUrl = "https://github.com/throndir2/Martlet";
    public const string OpenRouterAppTitle = "Martlet";

    public static IReadOnlyList<ChatCompletionsEndpointOption> NamedEndpoints { get; } =
        Array.AsReadOnly<ChatCompletionsEndpointOption>(
        [
            new("OpenRouter", OpenRouterBaseUrl, "google/gemma-4-26b-a4b-it", []),
            // A Free Endpoint on build.nvidia.com. Martlet's earlier default, meta/llama-3.3-70b-instruct, reached its end of
            // life there on 2026-08-26 (checked 2026-10-01, together with the other retired IDs below).
            new("NVIDIA Build", NvidiaBuildBaseUrl, "google/diffusiongemma-26b-a4b-it",
                ["meta/llama-3.3-70b-instruct", "meta/llama-4-maverick-17b-128e-instruct", "microsoft/phi-4-multimodal-instruct",
                 "nvidia/nemotron-nano-12b-v2-vl"])
        ]);

    public static ChatCompletionsEndpointOption? Named(string? baseUrl) =>
        NamedEndpoints.SingleOrDefault(option => string.Equals(option.BaseUrl, baseUrl, StringComparison.Ordinal));

    /// <summary>The named endpoint when it has retired this model for good; null otherwise.</summary>
    public static ChatCompletionsEndpointOption? RetiredOn(string? baseUrl, string? modelId) =>
        Named(baseUrl) is { } named && named.Retired(modelId) ? named : null;
}
