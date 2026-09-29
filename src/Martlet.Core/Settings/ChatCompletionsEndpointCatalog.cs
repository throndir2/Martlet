namespace Martlet.Core.Settings;

public sealed record ChatCompletionsEndpointOption(string Name, string BaseUrl);

public static class ChatCompletionsEndpointCatalog
{
    public const string OpenRouterBaseUrl = "https://openrouter.ai/api/v1";
    public const string NvidiaBuildBaseUrl = "https://integrate.api.nvidia.com/v1";
    public const string OpenRouterAppUrl = "https://github.com/throndir2/Martlet";
    public const string OpenRouterAppTitle = "Martlet";

    public static IReadOnlyList<ChatCompletionsEndpointOption> NamedEndpoints { get; } =
        Array.AsReadOnly<ChatCompletionsEndpointOption>(
        [
            new("OpenRouter", OpenRouterBaseUrl),
            new("NVIDIA Build", NvidiaBuildBaseUrl)
        ]);

    public static ChatCompletionsEndpointOption? Named(string? baseUrl) =>
        NamedEndpoints.SingleOrDefault(option => string.Equals(option.BaseUrl, baseUrl, StringComparison.Ordinal));
}
