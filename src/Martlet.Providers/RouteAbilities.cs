using Martlet.Core.Settings;

namespace Martlet.Providers;

/// <summary>Whether a route's model takes video itself (a video part, apart from seeing frames as pictures). Unknown means
/// Martlet found nothing either way.</summary>
public enum VideoSupport { Supported, Unsupported, Unknown }

/// <summary>Whether a route's model calls tools. Unknown means Martlet found nothing either way, so tools are offered and a
/// refusal is handled as before.</summary>
public enum ToolSupport { Supported, Unsupported, Unknown }

/// <summary>What a route takes that Martlet has no name guesses for, like <see cref="VisionModelCatalog.ForRoute"/> and
/// <see cref="HearingModelCatalog.ForRoute"/> but from what Martlet found out only (model-abilities.json: a test, then the server's
/// metadata; docs/MODEL_CATALOG.md). A model found retired on the route takes nothing.</summary>
public static class RouteAbilities
{
    /// <summary>Whether the route's model takes video itself. A model that only sees can still get frames as pictures.</summary>
    public static VideoSupport Video(string? origin, string? modelId, ModelAbilities? abilities, bool retired = false)
    {
        var found = abilities?.Find(origin, modelId);
        if (retired || found?.Retired is not null) return VideoSupport.Unsupported;
        return found?.Video switch { true => VideoSupport.Supported, false => VideoSupport.Unsupported, _ => VideoSupport.Unknown };
    }

    /// <summary>Whether a route type carries tool calls: OpenAI's Responses route and a Chat Completions endpoint do (function
    /// calling); a paired computer's gateway doesn't.</summary>
    public static bool CarriesTools(SetupRouteType? routeType) => routeType is SetupRouteType.ChatCompletions or SetupRouteType.OpenAi;

    /// <summary>Whether the route's model calls tools: only a route that carries them (<see cref="CarriesTools"/>); OpenAI's own
    /// models all do; on a Chat Completions endpoint, what Martlet found out.</summary>
    public static ToolSupport Tools(SetupRouteType? routeType, string? origin, string? modelId, ModelAbilities? abilities, bool retired = false)
    {
        var found = abilities?.Find(origin, modelId);
        if (!CarriesTools(routeType) || retired || found?.Retired is not null) return ToolSupport.Unsupported;
        if (routeType == SetupRouteType.OpenAi) return ToolSupport.Supported;
        return found?.Tools switch { true => ToolSupport.Supported, false => ToolSupport.Unsupported, _ => ToolSupport.Unknown };
    }
}
