namespace Martlet.Core.Settings;

public sealed record ChatCompletionsEndpointOption(string Name, string BaseUrl);

public static class ChatCompletionsEndpointCatalog
{
    public const string OpenRouterBaseUrl = "https://openrouter.ai/api/v1";
    public const string NvidiaBuildBaseUrl = "https://integrate.api.nvidia.com/v1";

    public static IReadOnlyList<ChatCompletionsEndpointOption> NamedEndpoints { get; } =
        Array.AsReadOnly<ChatCompletionsEndpointOption>(
        [
            new("OpenRouter", OpenRouterBaseUrl),
            new("NVIDIA Build", NvidiaBuildBaseUrl)
        ]);
}
