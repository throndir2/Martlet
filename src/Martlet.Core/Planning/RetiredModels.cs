using Martlet.Core.Settings;

namespace Martlet.Core.Planning;

/// <summary>A model its server retired, and what to use instead (docs/RECOMMENDATION_DESIGN.md, "Staying current": a retired
/// model is the exception, so Martlet proposes its replacement at once instead of waiting for the daily check).</summary>
public static class RetiredModels
{
    /// <summary>Whether the model catalog says <paramref name="modelId"/> on the server at <paramref name="baseUrl"/> is retired by
    /// <paramref name="today"/>: past its expiration date (OpenRouter's <c>expiration_date</c>).</summary>
    public static bool Expired(ModelCatalog? catalog, string? baseUrl, string? modelId, DateOnly today) =>
        catalog is not null && baseUrl is { Length: > 0 } && modelId is { Length: > 0 } &&
        catalog.Route(baseUrl, modelId) is { } route && (route.Retired || route.Expires is { } expires && expires <= today);

    /// <summary>What to use instead of <paramref name="retired"/> on the server at <paramref name="baseUrl"/>: the provider's own
    /// suggestion (<paramref name="suggestion"/>) when it isn't retired too, else the smartest model the catalog lists on that
    /// server that isn't retired or going away, takes text and gives text, sees when the retired one saw, and is free when the
    /// retired one was. Null when there is none.</summary>
    public static string? Replacement(ModelCatalog? catalog, string baseUrl, string retired, string? suggestion, Func<string, bool> isRetired,
        DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(isRetired);
        bool Gone(string model) => isRetired(model) || Expired(catalog, baseUrl, model, today);
        if (suggestion is { Length: > 0 } offered && !string.Equals(offered, retired, StringComparison.Ordinal) && !Gone(offered)) return offered;
        if (catalog is null || ModelCatalog.ProviderOf(baseUrl) is not { } provider) return null;
        var before = catalog.Route(baseUrl, retired);
        var saw = before?.Fact(CatalogFacts.InputImage).Yes == true;
        return catalog.Routes
            .Where(r => string.Equals(r.Provider, provider, StringComparison.OrdinalIgnoreCase) && !r.Deprecated &&
                !string.Equals(r.ModelId, retired, StringComparison.Ordinal) && !Gone(r.ModelId) &&
                r.Fact(CatalogFacts.InputText).Yes != false && r.Fact(CatalogFacts.OutputText).Yes != false &&
                (!saw || r.Fact(CatalogFacts.InputImage).Yes == true) && (before?.Free != true || r.Free == true))
            .Select(r => (Route: r, Model: catalog.Model(r.ModelKey)))
            .Where(x => x.Model is not null)
            .OrderByDescending(x => catalog.Smartness(x.Model!).Rank ?? -1).ThenBy(x => x.Route.ModelId, StringComparer.Ordinal)
            .Select(x => x.Route.ModelId).FirstOrDefault();
    }

    /// <summary><see cref="Replacement(ModelCatalog?, string, string, string?, Func{string, bool}, DateOnly)"/> for a model on a
    /// Chat Completions server, with what Martlet found out on its routes (<paramref name="abilities"/>: an HTTP 410 Gone) and its
    /// list of retired models.</summary>
    public static string? Replacement(ModelCatalog? catalog, string baseUrl, string retired, ModelAbilities? abilities, DateOnly today) =>
        Replacement(catalog, baseUrl, retired, ChatCompletionsEndpointCatalog.RetiredOn(baseUrl, retired, abilities)?.Suggestion,
            model => ChatCompletionsEndpointCatalog.RetiredOn(baseUrl, model, abilities) is not null, today);
}
