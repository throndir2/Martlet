namespace Martlet.Core.Settings;

/// <summary>A named Chat Completions provider. DefaultModelId is only a prefilled suggestion; the owner still applies and consents.
/// Each default both talks and sees images (and calls tools), so screen watching works without changing models.
/// RetiredModelIds are models the provider has switched off for good (it answers HTTP 410 Gone), so a route on one cannot work.
/// Id is the stable preset id the setup planner and wizard use ("openrouter", "nvidia-build", "google-gemini").</summary>
public sealed record ChatCompletionsEndpointOption(string Id, string Name, string BaseUrl, string DefaultModelId, IReadOnlyList<string> RetiredModelIds)
{
    /// <summary>The page where the owner gets a key.</summary>
    public string? KeyUrl { get; init; }
    /// <summary>What setup shows under the provider: the recommendation and how to get a key; null for the built-in hints.</summary>
    public string? Guidance { get; init; }
    /// <summary>The default model hears, but the provider's free terms ask not to send personal data, so its recording goes
    /// only after the owner ticks Companion › Listening › Let Thinking hear my voice (as for every cloud model); until then
    /// Thinking gets the transcript.</summary>
    public bool HearingOptIn { get; init; }

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
    /// <summary>Google's OpenAI-compatible Gemini API, which takes input_audio (docs/HOSTED_THINKING.md).</summary>
    public const string GeminiBaseUrl = "https://generativelanguage.googleapis.com/v1beta/openai";
    /// <summary>Free on the Gemini API's free tier; hears, sees and calls tools (checked 2026-10-06).</summary>
    public const string GeminiDefaultModelId = "gemini-3.5-flash-lite";
    public const string GeminiKeyUrl = "https://aistudio.google.com/apikey";
    public const string OpenRouterId = "openrouter", NvidiaBuildId = "nvidia-build", GeminiId = "google-gemini";

    public static IReadOnlyList<ChatCompletionsEndpointOption> NamedEndpoints { get; } =
        Array.AsReadOnly<ChatCompletionsEndpointOption>(
        [
            new(OpenRouterId, "OpenRouter", OpenRouterBaseUrl, "google/gemma-4-26b-a4b-it", []) { KeyUrl = "https://openrouter.ai/settings/keys" },
            // Retired IDs checked on 2026-10-01 (HTTP 410 Gone), including Martlet's earlier default; the Gemma 3n models were
            // deprecated on 2026-07-27 and are gone from /v1/models (checked 2026-10-06, docs/HOSTED_THINKING.md).
            new(NvidiaBuildId, "NVIDIA Build", NvidiaBuildBaseUrl, NvidiaBuildDefaultModelId,
                ["meta/llama-3.3-70b-instruct", "meta/llama-4-maverick-17b-128e-instruct", "microsoft/phi-4-multimodal-instruct",
                 "nvidia/nemotron-nano-12b-v2-vl", "google/gemma-3n-e4b-it", "google/gemma-3n-e2b-it"])
            { KeyUrl = "https://build.nvidia.com/settings/api-keys" },
            new(GeminiId, "Google Gemini", GeminiBaseUrl, GeminiDefaultModelId, [])
            {
                KeyUrl = GeminiKeyUrl,
                HearingOptIn = true,
                Guidance = $"Recommended: {GeminiDefaultModelId}, free on Google's free tier. It sees your screen, and hears your voice " +
                    "only if you tick Companion › Listening › Let Thinking hear my voice; on the free tier Google's reviewers may read " +
                    $"what you send, so don't send anything private. Get a free key: open {GeminiKeyUrl}, sign in with a Google " +
                    "account, accept the Gemini API terms, then copy the key AI Studio made for you (or choose Create API key)."
            }
        ]);

    public static ChatCompletionsEndpointOption? Named(string? baseUrl) =>
        NamedEndpoints.SingleOrDefault(option => string.Equals(option.BaseUrl, baseUrl, StringComparison.Ordinal));

    /// <summary>The preset with this <see cref="ChatCompletionsEndpointOption.Id"/>, such as "google-gemini".</summary>
    public static ChatCompletionsEndpointOption? ById(string? id) =>
        NamedEndpoints.SingleOrDefault(option => string.Equals(option.Id, id, StringComparison.Ordinal));

    /// <summary>The named endpoint when it has retired this model for good; null otherwise. Martlet's built-in list only: callers
    /// ask <see cref="RetiredOn(string?, string?, ModelAbilities?)"/>, which puts what Martlet found first.</summary>
    public static ChatCompletionsEndpointOption? RetiredOn(string? baseUrl, string? modelId) =>
        Named(baseUrl) is { } named && named.Retired(modelId) ? named : null;

    /// <summary>Whether the server at <paramref name="baseUrl"/> retired <paramref name="modelId"/>: first what Martlet found on
    /// that route (<paramref name="abilities"/>: it answered HTTP 410 Gone; a later reply or test clears it), else Martlet's
    /// built-in list of retired models (<see cref="ChatCompletionsEndpointOption.RetiredModelIds"/>). Null when neither says so.</summary>
    public static RetiredModel? RetiredOn(string? baseUrl, string? modelId, ModelAbilities? abilities)
    {
        var named = Named(baseUrl);
        // The provider's own suggestion, unless that is the retired model.
        var suggestion = named is not null && !string.Equals(named.DefaultModelId, modelId?.Trim(), StringComparison.Ordinal) &&
            abilities?.Find(baseUrl, named.DefaultModelId)?.Retired is null ? named.DefaultModelId : null;
        if (abilities?.Find(baseUrl, modelId) is { Retired: { } since } found)
            return new(named?.Name ?? ServerName(baseUrl), suggestion, since, found.SourceOf(ModelFact.Retired)?.Source ?? found.Source);
        return RetiredOn(baseUrl, modelId) is { } listed ? new(listed.Name, suggestion, null, "Martlet's list of retired models") : null;
    }

    private static string ServerName(string? baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ? uri.Host : "The server";
}

/// <summary>A model its server retired for good: <see cref="Server"/> names the server ("NVIDIA Build"), <see cref="Suggestion"/>
/// is the provider's current default (null when there is none), <see cref="Since"/> the date Martlet found it (null for the
/// built-in list) and <see cref="Source"/> where that came from in words.</summary>
public sealed record RetiredModel(string Server, string? Suggestion, DateTimeOffset? Since, string Source)
{
    /// <summary>What to do, in one sentence: "Choose google/diffusiongemma-26b-a4b-it in Companion › Thinking."</summary>
    public string Remedy => $"Choose {Suggestion ?? "another model"} in Companion › Thinking.";
}