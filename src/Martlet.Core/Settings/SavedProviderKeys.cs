using Martlet.Core.Cluster;

namespace Martlet.Core.Settings;

/// <summary>A key the owner saved for a hosted provider somewhere in Martlet: <see cref="Provider"/> is the provider
/// (<see cref="SavedProviderKeys.ProviderOf"/>), <see cref="Where"/> names the place in words ("the Thinking pool") and
/// <see cref="Binding"/> is where Windows Credential Manager keeps it.</summary>
public sealed record SavedProviderKey(string Provider, string Where, CredentialBinding Binding);

/// <summary>Every API key the owner saved for a hosted provider, wherever it was saved: the Thinking, Listening and Voice routes,
/// If Thinking fails, the Thinking pool and the Vision, Hearing and Picture making lists. All of them travel to every computer
/// (docs/CLUSTER.md), so a key added in one place on one computer is a key for that provider everywhere: no page asks for it
/// again, and a new place for the same provider takes a copy of it.</summary>
public static class SavedProviderKeys
{
    /// <summary>The planner's id for "OpenAI".</summary>
    public const string OpenAi = "openai";

    // The lists whose keys are Chat Completions keys bound to the member's base URL (pool-keys.json). Speaking's and Listening's
    // entries point at route keys, which the routes already list.
    private static readonly PoolArea[] KeyedAreas = [PoolAreas.Vision, PoolAreas.Hearing, PoolAreas.PictureMaking];

    /// <summary>The provider of a base URL: "openai" for OpenAI, a named endpoint's id ("nvidia-build", "openrouter"), else the
    /// base URL itself without its last slash; null for none.</summary>
    public static string? ProviderOf(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin)) return null;
        var trimmed = origin.Trim().TrimEnd('/');
        if (string.Equals(trimmed, OpenAiSetup.Origin, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, SenseLists.OpenAiBaseUrl, StringComparison.OrdinalIgnoreCase))
            return OpenAi;
        return ChatCompletionsEndpointCatalog.NamedEndpoints
            .FirstOrDefault(e => string.Equals(e.BaseUrl.TrimEnd('/'), trimmed, StringComparison.OrdinalIgnoreCase))?.Id ?? trimmed;
    }

    /// <summary>Every saved key, in the order a page uses them again: the routes, If Thinking fails, the Thinking pool, then the
    /// Vision, Hearing and Picture making lists. Only what the settings say: a key is read only when it is used.</summary>
    public static IReadOnlyList<SavedProviderKey> All(AppSettings? settings, string? dataDirectory)
    {
        var keys = new List<SavedProviderKey>();
        if (settings is null) return keys;
        var profile = settings.Profile.Id;
        foreach (var route in settings.Setup?.Routes ?? [])
        {
            if (route.CredentialId is not { } id) continue;
            var origin = route.RouteType switch
            {
                null or SetupRouteType.OpenAi => OpenAiSetup.Origin,
                SetupRouteType.ChatCompletions => route.Origin,
                _ => null
            };
            if (ProviderOf(origin) is { } provider) keys.Add(new(provider, RouteWhere(route.Role), CredentialBinding.For(profile, route, id)));
        }
        if (settings.ThinkingFallback is { CredentialId: { } fallbackId } fallback && ProviderOf(fallback.Origin) is { } fallbackProvider)
            keys.Add(new(fallbackProvider, "If Thinking fails", fallback.Binding(profile, fallbackId)));
        if (dataDirectory is null) return keys;
        foreach (var member in ThinkingPoolSettings.Read(dataDirectory, save: false).Settings.All)
            if (member is { Place: DeepThinkingPlace.Endpoint, CredentialId: { } memberId, Origin: { } memberOrigin } &&
                ProviderOf(memberOrigin) is { } memberProvider)
                keys.Add(new(memberProvider, "the Thinking pool", member.Binding(profile, memberId)));
        var poolKeys = PoolKeys.Load(dataDirectory);
        if (poolKeys.Keys.Count == 0) return [.. keys.Distinct()];
        foreach (var area in KeyedAreas)
            foreach (var member in PoolSettings.LoadFor(dataDirectory, area)?.Members ?? [])
            {
                if (poolKeys.For(area.Id, member.Key) is not { } listId) continue;
                var origin = member.Kind switch
                {
                    PoolMemberKind.Cloud => SenseLists.BaseUrl(member),
                    PoolMemberKind.Address => member.Address?.TrimEnd('/'),
                    _ => null
                };
                if (origin is null || ProviderOf(origin) is not { } listProvider) continue;
                keys.Add(new(listProvider, $"the {area.Title} list",
                    new DeepThinkingSettings { Place = DeepThinkingPlace.Endpoint, Origin = origin, ModelId = "key" }.Binding(profile, listId)));
            }
        return [.. keys.Distinct()];
    }

    /// <summary>The saved keys for the provider at <paramref name="origin"/> (<see cref="All"/>'s order).</summary>
    public static IReadOnlyList<SavedProviderKey> For(AppSettings? settings, string? dataDirectory, string? origin) =>
        ProviderOf(origin) is { } provider
            ? [.. All(settings, dataDirectory).Where(k => string.Equals(k.Provider, provider, StringComparison.OrdinalIgnoreCase))]
            : [];

    /// <summary>The planner's hosted providers with a saved key anywhere: "openai" and the named endpoints' ids.</summary>
    public static IReadOnlyCollection<string> Providers(AppSettings? settings, string? dataDirectory) =>
        [.. All(settings, dataDirectory).Select(k => k.Provider)
            .Where(p => p == OpenAi || ChatCompletionsEndpointCatalog.ById(p) is not null)
            .Distinct(StringComparer.Ordinal)];

    /// <summary>A copy of the first of <paramref name="keys"/> that <paramref name="vault"/> can read; null when none can.
    /// The caller disposes it.</summary>
    public static SecretLease? Read(ICredentialStore vault, IEnumerable<SavedProviderKey> keys)
    {
        ArgumentNullException.ThrowIfNull(vault);
        ArgumentNullException.ThrowIfNull(keys);
        foreach (var key in keys)
        {
            using var read = vault.Read(key.Binding);
            if (read.Error != CredentialError.None || read.Secret is null) continue;
            SecretLease? copy = null;
            try { read.Secret.Use(secret => copy = new SecretLease(secret)); }
            catch (Contracts.ContractException) { continue; }
            return copy;
        }
        return null;
    }

    private static string RouteWhere(SetupRole role) => role switch
    {
        SetupRole.Llm => "Thinking",
        SetupRole.Stt => "Listening",
        _ => "Voice"
    };
}
