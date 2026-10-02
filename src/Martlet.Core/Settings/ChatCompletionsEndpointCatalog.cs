namespace Martlet.Core.Settings;

/// <summary>A named Chat Completions provider. DefaultModelId is only a prefilled suggestion; the owner still applies and consents.</summary>
public sealed record ChatCompletionsEndpointOption(string Name, string BaseUrl, string DefaultModelId);

public static class ChatCompletionsEndpointCatalog
{
    public const string OpenRouterBaseUrl = "https://openrouter.ai/api/v1";
    public const string NvidiaBuildBaseUrl = "https://integrate.api.nvidia.com/v1";
    /// <summary>NVIDIA retires Build models often (meta/llama-3.3-70b-instruct answers 410 Gone since 2026-08-26), so the
    /// suggestion is a current model; a retired one fails with ProviderFailureCode.ModelRetired and a clear remedy.</summary>
    public const string NvidiaBuildDefaultModelId = "google/gemma-4-31b-it";
    public const string OpenRouterAppUrl = "https://github.com/throndir2/Martlet";
    public const string OpenRouterAppTitle = "Martlet";

    public static IReadOnlyList<ChatCompletionsEndpointOption> NamedEndpoints { get; } =
        Array.AsReadOnly<ChatCompletionsEndpointOption>(
        [
            new("OpenRouter", OpenRouterBaseUrl, "meta-llama/llama-3.3-70b-instruct"),
            new("NVIDIA Build", NvidiaBuildBaseUrl, NvidiaBuildDefaultModelId)
        ]);

    public static ChatCompletionsEndpointOption? Named(string? baseUrl) =>
        NamedEndpoints.SingleOrDefault(option => string.Equals(option.BaseUrl, baseUrl, StringComparison.Ordinal));
}
