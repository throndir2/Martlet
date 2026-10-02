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
    /// <summary>NVIDIA retires Build models often (meta/llama-3.3-70b-instruct answers 410 Gone since 2026-08-26), so the
    /// suggestion is a current model; a retired one fails with ProviderFailureCode.ModelRetired and a clear remedy.
    /// A Free Endpoint checked on 2026-10-01: about 0.5 s per reply, reads screenshots and calls tools (google/gemma-4-31b-it
    /// also sees but took 10-30 s and sometimes timed out).</summary>
    public const string NvidiaBuildDefaultModelId = "google/diffusiongemma-26b-a4b-it";
    public const string OpenRouterAppUrl = "https://github.com/throndir2/Martlet";
    public const string OpenRouterAppTitle = "Martlet";

    public static IReadOnlyList<ChatCompletionsEndpointOption> NamedEndpoints { get; } =
        Array.AsReadOnly<ChatCompletionsEndpointOption>(
        [
            new("OpenRouter", OpenRouterBaseUrl, "google/gemma-4-26b-a4b-it", []),
            // Retired IDs checked on 2026-10-01 (HTTP 410 Gone), including Martlet's earlier default.
            new("NVIDIA Build", NvidiaBuildBaseUrl, NvidiaBuildDefaultModelId,
                ["meta/llama-3.3-70b-instruct", "meta/llama-4-maverick-17b-128e-instruct", "microsoft/phi-4-multimodal-instruct",
                 "nvidia/nemotron-nano-12b-v2-vl"])
        ]);

    public static ChatCompletionsEndpointOption? Named(string? baseUrl) =>
        NamedEndpoints.SingleOrDefault(option => string.Equals(option.BaseUrl, baseUrl, StringComparison.Ordinal));

    /// <summary>The named endpoint when it has retired this model for good; null otherwise.</summary>
    public static ChatCompletionsEndpointOption? RetiredOn(string? baseUrl, string? modelId) =>
        Named(baseUrl) is { } named && named.Retired(modelId) ? named : null;
}