using System.Globalization;
using Martlet.Core.Settings;

namespace Martlet.Core.Planning;

/// <summary>The options the planners use in this process (docs/RECOMMENDATION_DESIGN.md, "Planner on the catalog"): Martlet's
/// own list (<see cref="FootprintCatalog.Default"/>) until the model catalog is loaded, then <see cref="FootprintCatalog.FromModels"/>.
/// <see cref="Load"/> reads the model catalog (the daily copy, else the snapshot) and what Martlet found out on routes
/// (model-abilities.json), so call it off the reply path: a setup page, a background task. Reading <see cref="Current"/> is free
/// and never waits.</summary>
public static class PlanningCatalog
{
    private sealed record State(FootprintCatalog Options, double? Bandwidth, IReadOnlyList<ModelAbility> Tested, string TestedKey);

    private static volatile State current = new(FootprintCatalog.Default, null, [], "");
    private static readonly object Gate = new();

    /// <summary>The options to plan with now.</summary>
    public static FootprintCatalog Current => current.Options;

    /// <summary>The model catalog <see cref="Current"/> was built from, or null before it was loaded.</summary>
    public static ModelCatalog? Models => current.Options.Models;

    /// <summary>Plans with <paramref name="models"/> from now on (<paramref name="from"/> says which copy, for MCP and the
    /// Doctor), with speed estimates for a card with <paramref name="bandwidthGbps"/> GB/s and the route facts Martlet found
    /// out (<paramref name="tested"/>) before the catalog's.</summary>
    public static FootprintCatalog Use(ModelCatalog models, string from, double? bandwidthGbps = null, IReadOnlyList<ModelAbility>? tested = null)
    {
        ArgumentNullException.ThrowIfNull(models);
        var options = FootprintCatalog.FromModels(models, bandwidthGbps, $"the model catalog ({from}) and Martlet's own list", tested);
        current = new(options, bandwidthGbps, tested ?? [], Key(tested));
        return options;
    }

    /// <summary>Loads the model catalog for <paramref name="dataDirectory"/> (<see cref="ModelCatalogStore.Load"/>) and its
    /// model-abilities.json, and plans with them; nothing is built again while they and the bandwidth stay the same. Off the
    /// reply path only.</summary>
    public static FootprintCatalog Load(string dataDirectory, double? bandwidthGbps = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        var models = ModelCatalogStore.For(dataDirectory).Load();
        var tested = ModelAbilities.Load(dataDirectory).Models;
        var key = Key(tested);
        lock (Gate)
        {
            var held = current;
            if (ReferenceEquals(held.Options.Models, models) && held.Bandwidth == bandwidthGbps && held.TestedKey == key) return held.Options;
            return Use(models, Copy(models), bandwidthGbps, tested);
        }
    }

    /// <summary>Which copy of the catalog <paramref name="models"/> is: "the snapshot of 2026-10-10" or "the daily copy of
    /// 2026-10-11".</summary>
    public static string Copy(ModelCatalog models)
    {
        ArgumentNullException.ThrowIfNull(models);
        var date = models.Built.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return models.Built == ModelCatalogStore.Snapshot().Built ? $"the snapshot of {date}" : $"the daily copy of {date}";
    }

    /// <summary>The model <paramref name="providerId"/> suggests now: the model catalog's pick when it is loaded
    /// (<see cref="CatalogOptions.SuggestedChat"/>), else today's preset default.</summary>
    public static string? SuggestedModel(string providerId) => CatalogOptions.SuggestedModel(Models, providerId, current.Tested);

    /// <summary>The smartest free model on <paramref name="providerId"/> for the Thinking pool (<see cref="CatalogOptions.SmartestChat"/>),
    /// else today's preset default.</summary>
    public static string? SmartestModel(string providerId)
    {
        var held = current;
        return (held.Options.Models is { } models ? CatalogOptions.SmartestChat(models, providerId, held.Tested)?.ModelId : null) ??
            ChatCompletionsEndpointCatalog.ById(providerId)?.DefaultModelId;
    }

    /// <summary>Plans with Martlet's own list again (tests).</summary>
    public static void Reset() => current = new(FootprintCatalog.Default, null, [], "");

    private static string Key(IReadOnlyList<ModelAbility>? tested) => tested is null ? "" : string.Join('\n', tested.Where(a => a is not null)
        .Select(a => $"{a.Origin}|{a.ModelId}|{a.Sees}|{a.Hears}|{a.Video}|{a.Tools}|{a.Retired:O}").Order(StringComparer.Ordinal));
}
