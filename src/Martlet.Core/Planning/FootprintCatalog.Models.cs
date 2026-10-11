using System.Globalization;
using System.Text.RegularExpressions;

namespace Martlet.Core.Planning;

// The planner on the model catalog (docs/RECOMMENDATION_DESIGN.md, "Planner on the catalog"): the options for Thinking, Deep
// thinking, Vision and Hearing come from the model catalog and its local facts, so a new model needs no Martlet release. The
// seed (FootprintCatalog.Seed.cs) stays: it is the offline fallback, and its measured numbers win.
public sealed partial class FootprintCatalog
{
    /// <summary>The Ollama models Martlet's host roles offer: the OLLAMA_MODEL choice of deploy/host/roles/ollama/role.conf and
    /// deep-thinking/role.conf (FootprintCatalogTests checks they match). A host role installs only these, so a catalog model
    /// outside the list runs only in a companion PC's own Ollama (<see cref="ComponentOption.NativeOnly"/>).</summary>
    public static IReadOnlyList<string> HostRoleModels { get; } = Array.AsReadOnly<string>(
    [
        "gemma4:e2b", "gemma4:e4b", "qwen3-vl:8b", "gemma4:12b", "gemma4:26b", "gemma3:4b", "qwen2.5vl:7b", "gemma3:12b", "gemma3:27b",
        "llama3.2:3b", "qwen2.5:7b", "llama3.1:8b", "qwen2.5:14b"
    ]);

    /// <summary>Martlet's own options with what the model catalog knows, plus the catalog's own options:
    /// <list type="bullet">
    /// <item>Each seed option for Thinking, Deep thinking, Vision or Hearing whose model the catalog knows gets its smartness words,
    /// LMArena credit, tool calls and video. Its quality tier stays (Martlet chose it), and its measured numbers stay; an estimated
    /// first word is estimated again from the local facts and <paramref name="bandwidthGbps"/>.</item>
    /// <item>Each open-weight model with local facts that passes the gates (<see cref="CatalogOptions.Local"/>) becomes a Thinking,
    /// Deep thinking, Vision (it sees) and Hearing (it hears) option, sized and timed from its local facts.</item>
    /// <item>The hosted options use the catalog's models (<see cref="CatalogOptions.SuggestedChat"/>,
    /// <see cref="CatalogOptions.SmartestChat"/>); today's models stay when the catalog has none.</item>
    /// </list>
    /// Option IDs of the seed stay the same (settings save them). <paramref name="bandwidthGbps"/> is the memory bandwidth of the
    /// card the speed estimates are for (null: an RTX 4070's, where Martlet measured).</summary>
    public static FootprintCatalog FromModels(ModelCatalog models, double? bandwidthGbps = null, string? from = null)
    {
        ArgumentNullException.ThrowIfNull(models);
        var bandwidth = bandwidthGbps is > 0 ? bandwidthGbps.Value : CatalogOptions.ReferenceBandwidthGbps;
        var options = Default.Options.Select(o => CatalogOptions.Enrich(models, o, bandwidth)).ToList();
        var seedModels = Default.Options.Where(o => o.IsLocal && o.ModelId is not null && CatalogOptions.TakesModels(o.Component))
            .Select(o => models.Find(o.ModelId)?.Model.Key).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ids = options.Select(o => o.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var model in models.Models.Where(m => !seedModels.Contains(m.Key)).OrderBy(m => m.Key, StringComparer.Ordinal))
            foreach (var option in CatalogOptions.Local(models, model, bandwidth))
                if (ids.Add(option.Id)) options.Add(option);
        return new(options) { Models = models, From = from ?? "the model catalog and Martlet's own list" };
    }
}

/// <summary>The rules that turn the model catalog into planner options (docs/RECOMMENDATION_DESIGN.md): the gates a local model
/// passes, the hosted model each provider suggests, and the speed estimate from the local facts.</summary>
public static partial class CatalogOptions
{
    /// <summary>An RTX 4070's memory bandwidth (GB/s): Martlet's first-word measurements were made on it.</summary>
    public const double ReferenceBandwidthGbps = 504;
    /// <summary>A local model bigger than this (graphics memory at Martlet's context) fits no card the planner meets, so it
    /// isn't an option.</summary>
    public const double LargestGb = 80;
    /// <summary>A Deep thinking think's context (docs/RESOURCE_FOOTPRINTS.md): the option's ContextGb is the cache beyond
    /// Martlet's 8,192 tokens.</summary>
    public const int DeepContextTokens = 32_768;
    // The first-word estimate: a fixed part (the prompt's new tokens, the runtime) plus the tokens of a first sentence at the
    // real speed, which is about 60% of the bandwidth bound. Fitted to Martlet's measured Gemma 4 E2B (154 ms) and E4B (208 ms)
    // on an RTX 4070 (docs/VOICE_LATENCY.md): it gives 153 ms and 207 ms.
    private const double FirstWordBaseMs = 100;
    private const double TokensToFirstWord = 9;
    private const double RealShare = 0.6;
    private const double WordsPerToken = 0.75;

    /// <summary>The parts whose options are language models the catalog knows.</summary>
    public static bool TakesModels(PlanComponent component) =>
        component is PlanComponent.Thinking or PlanComponent.DeepThinking or PlanComponent.Vision or PlanComponent.Hearing;

    /// <summary>About how long until the first sentence on a card with <paramref name="bandwidthGbps"/> GB/s, from the bytes
    /// read for each token (<see cref="LocalMemoryEstimate.BytesPerToken"/>); null when it can't be worked out.</summary>
    public static int? FirstWordMs(LocalMemoryEstimate estimate, double bandwidthGbps)
    {
        ArgumentNullException.ThrowIfNull(estimate);
        return estimate.TokensPerSecond(bandwidthGbps) is { } most && most > 0
            ? (int)Math.Round(FirstWordBaseMs + TokensToFirstWord * 1000 / (most * RealShare))
            : null;
    }

    /// <summary>About how many words a second it writes on a card with <paramref name="bandwidthGbps"/> GB/s (60% of the
    /// bandwidth bound, three words for each four tokens); null when it can't be worked out.</summary>
    public static double? WordsPerSecond(LocalMemoryEstimate estimate, double bandwidthGbps)
    {
        ArgumentNullException.ThrowIfNull(estimate);
        return estimate.TokensPerSecond(bandwidthGbps) is { } most && most > 0 ? Math.Round(most * RealShare * WordsPerToken) : null;
    }

    /// <summary>LMArena's rating with its credit ("LMArena (CC-BY-4.0) text rating 1318"), or null. Never the rank.</summary>
    public static string? Credit(CatalogSmartness? smartness) => smartness?.Credit is not { } credit ? null
        : smartness.Rating is { } text ? $"{credit} text rating {text.ToString("0", CultureInfo.InvariantCulture)}"
        : smartness.VisionRating is { } vision ? $"{credit} vision rating {vision.ToString("0", CultureInfo.InvariantCulture)}"
        : null;

    /// <summary>Whether scores back <paramref name="smartness"/>: the model's own, or a scored model of the same family and size.</summary>
    public static bool Scored(CatalogSmartness smartness)
    {
        ArgumentNullException.ThrowIfNull(smartness);
        return smartness.Rank is not null && (smartness.From == "its own scores" ||
            smartness.From.EndsWith("same family and size", StringComparison.Ordinal));
    }

    /// <summary>How smart a model is in words for its option: the catalog's words when scores back them; else, with
    /// <paramref name="estimates"/>, the words marked as an estimate, and null without (Martlet's own options keep the quality
    /// tier Martlet chose, and an estimate would only contradict it).</summary>
    public static string? Words(CatalogSmartness? smartness, bool estimates)
    {
        if (smartness is null) return null;
        if (Scored(smartness)) return smartness.Words;
        if (!estimates) return null;
        return smartness.Rank is not null ? $"{smartness.Words} (an estimate from scored models of a similar size)" : $"{smartness.Words} (from its size)";
    }

    /// <summary>A model that holds a conversation: not an embedding, speech, picture, safety or guard model, and not a variant
    /// that always reasons first (its first word comes late).</summary>
    public static bool Chat(string name) => ServedModels.Chats(name) && !NotConversation().IsMatch(name);

    /// <summary>The gates for a local option (docs/RECOMMENDATION_DESIGN.md, "How the planner decides"): it can run locally and
    /// has an install name (an Ollama tag or hf.co/{repo}:{quant}) and a memory estimate; it writes text; its weights' size
    /// agrees with its parameters (a broken record never wins a card); it is at most <see cref="LargestGb"/>. Unknown is not
    /// Yes: it hears or sees only when the catalog says so. Whether it fits a card and the owner's online answer are the
    /// planners' gates.</summary>
    public static IEnumerable<ComponentOption> Local(ModelCatalog models, CatalogModel model, double bandwidthGbps = ReferenceBandwidthGbps)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(model);
        if (model.LocallyHostable != true || model.Local is not { } local || local.Quantization() is not { InstallName.Length: > 0 } quantization ||
            local.Estimate() is not { } estimate || model.Fact(CatalogFacts.OutputText).Yes == false || !Chat(model.Key) ||
            !Consistent(model, local, quantization))
            yield break;
        var gb = Gb(estimate.GraphicsBytes);
        if (gb <= 0 || gb > LargestGb) yield break;
        var smartness = models.Smartness(model);
        var install = quantization.InstallName;
        var ram = Gb(estimate.SystemMemoryBytes);
        var disk = Gb(quantization.DownloadBytes);
        var words = WordsPerSecond(estimate, bandwidthGbps);
        var source = $"Estimate from the model catalog's local facts: {quantization.Name} ({quantization.Source}) at " +
            $"{LocalModelMemory.DefaultContextTokens:N0} tokens; speed from {bandwidthGbps.ToString("0", CultureInfo.InvariantCulture)} GB/s " +
            "of memory bandwidth (docs/RESOURCE_FOOTPRINTS.md)";
        ComponentOption Option(string id, PlanComponent component, string? role) => new()
        {
            Id = id, Component = component, DisplayName = model.Name, ModelId = install, HostRoleKind = role, Gpu = GpuRequirement.AnyGpu,
            Steady = new(gb, ram + 0.5, 1, disk), Peak = new(gb, ram + 1, 2, disk), QualityTier = smartness.Tier,
            HearsAudio = component != PlanComponent.Vision && model.Hears == true, SeesImages = component != PlanComponent.Hearing && model.Sees == true,
            TakesVideo = model.TakesVideo == true, CallsTools = model.CallsTools, Evidence = FootprintEvidence.Estimate, Origin = OptionOrigin.LocalFacts,
            CatalogKey = model.Key, Smartness = Words(smartness, estimates: true), SmartnessCredit = Credit(smartness),
            NativeOnly = !FootprintCatalog.HostRoleModels.Contains(install, StringComparer.OrdinalIgnoreCase), Source = source
        };
        yield return Option(install, PlanComponent.Thinking, "ollama") with { FirstWordMs = FirstWordMs(estimate, bandwidthGbps), WordsPerSecond = words };
        var deep = local.Estimate(null, DeepContextTokens);
        yield return Option("deep-thinking:" + install, PlanComponent.DeepThinking, "deep-thinking") with
        {
            DisplayName = model.Name + " (deep thinking)", WordsPerSecond = words,
            ContextGb = deep is null ? 0.5 : Math.Max(0, Gb(deep.KvCacheBytes - estimate.KvCacheBytes))
        };
        if (model.Sees == true) yield return Option("vision:" + install, PlanComponent.Vision, null);
        if (model.Hears == true) yield return Option("hearing:" + install, PlanComponent.Hearing, null);
    }

    /// <summary>Whether the weights' size agrees with the model's parameters at the quantization's bits (between half and three
    /// times): a record whose GGUF is one part of a split file, or a draft only, fails.</summary>
    public static bool Consistent(CatalogModel model, LocalModelFacts local, LocalModelQuantization quantization)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(quantization);
        var total = model.Fact(CatalogFacts.ParametersTotal).Number is { } billions ? (long)(billions * 1e9) : 0;
        var parameters = Math.Max(Math.Max(local.WeightsParameters ?? 0, local.Parameters ?? 0), total);
        if (parameters <= 0 || quantization.WeightsBytes <= 0) return false;
        var expected = parameters * (double)(LocalModelQuantizations.Bits(quantization.Name) ?? 4) / 8;
        return quantization.WeightsBytes >= expected * 0.5 && quantization.WeightsBytes <= expected * 3;
    }

    /// <summary>The model a hosted provider suggests for live Thinking and Vision (docs/RECOMMENDATION_DESIGN.md, "Provider
    /// defaults"): a free chat route on <paramref name="provider"/> that sees and calls tools and is not retired or going
    /// away, the fastest known first (the fewest active parameters, since nothing measures hosted speed), then the smartest.
    /// Null when the catalog has none: today's default stays.</summary>
    public static CatalogRoute? SuggestedChat(ModelCatalog models, string provider)
    {
        ArgumentNullException.ThrowIfNull(models);
        return models.Routes.Where(r => Usable(models, r, provider) && r.Fact(CatalogFacts.InputImage).Yes == true && r.Fact(CatalogFacts.Tools).Yes == true)
            .OrderBy(r => Active(models, r) ?? double.MaxValue).ThenByDescending(r => Smartness(models, r).Tier)
            .ThenBy(r => r.ModelId, StringComparer.Ordinal).FirstOrDefault();
    }

    /// <summary>The smartest free chat route on <paramref name="provider"/> (Deep thinking: a few seconds don't matter there),
    /// one that sees and calls tools first within a quality step (screen summaries and research use them). Null when none.</summary>
    public static CatalogRoute? SmartestChat(ModelCatalog models, string provider)
    {
        ArgumentNullException.ThrowIfNull(models);
        return models.Routes.Where(r => Usable(models, r, provider))
            .OrderByDescending(r => Smartness(models, r).Tier)
            .ThenByDescending(r => (r.Fact(CatalogFacts.InputImage).Yes == true ? 1 : 0) + (r.Fact(CatalogFacts.Tools).Yes == true ? 1 : 0))
            .ThenByDescending(r => Smartness(models, r).Rank ?? -1).ThenBy(r => r.ModelId, StringComparer.Ordinal).FirstOrDefault();
    }

    /// <summary>A free route on <paramref name="provider"/> that hears (Hearing's audio model), the fastest known first.</summary>
    public static CatalogRoute? SuggestedHearing(ModelCatalog models, string provider)
    {
        ArgumentNullException.ThrowIfNull(models);
        return models.Routes.Where(r => string.Equals(r.Provider, provider, StringComparison.OrdinalIgnoreCase) && r.Free == true && !r.Retired &&
                !r.Deprecated && r.Fact(CatalogFacts.InputAudio).Yes == true && models.Model(r.ModelKey)?.Fact(CatalogFacts.OutputText).Yes == true)
            .OrderBy(r => Active(models, r) ?? double.MaxValue).ThenByDescending(r => Smartness(models, r).Tier)
            .ThenBy(r => r.ModelId, StringComparer.Ordinal).FirstOrDefault();
    }

    /// <summary>The model <paramref name="providerId"/> suggests (<see cref="SuggestedChat"/>), else today's preset default
    /// (<see cref="Settings.ChatCompletionsEndpointCatalog"/>); null for a provider Martlet doesn't name.</summary>
    public static string? SuggestedModel(ModelCatalog? models, string providerId) =>
        (models is null ? null : SuggestedChat(models, providerId)?.ModelId) ?? Settings.ChatCompletionsEndpointCatalog.ById(providerId)?.DefaultModelId;

    /// <summary>A seed option with what the catalog knows (see <see cref="FootprintCatalog.FromModels"/>).</summary>
    internal static ComponentOption Enrich(ModelCatalog models, ComponentOption option, double bandwidthGbps)
    {
        if (!option.IsLocal) return Hosted(models, option);
        if (!TakesModels(option.Component) || option.UsesThinking || option.ModelId is not { } id || models.Find(id)?.Model is not { } model)
            return option;
        var smartness = models.Smartness(model);
        var enriched = option with
        {
            CatalogKey = model.Key, Smartness = Words(smartness, estimates: false), SmartnessCredit = Credit(smartness), CallsTools = model.CallsTools,
            TakesVideo = model.TakesVideo == true
        };
        if (!option.UsesGpu || option.Component is not (PlanComponent.Thinking or PlanComponent.DeepThinking) || model.Memory() is not { } estimate)
            return enriched;
        enriched = enriched with { WordsPerSecond = WordsPerSecond(estimate, bandwidthGbps) };
        // Measured numbers win; an estimated first word is estimated again from the local facts.
        return option.Component == PlanComponent.Thinking && option.Evidence != FootprintEvidence.Measured && FirstWordMs(estimate, bandwidthGbps) is { } ms
            ? enriched with
            {
                FirstWordMs = ms,
                Source = option.Source + $"; first word estimated from the model catalog's local facts at " +
                    $"{bandwidthGbps.ToString("0", CultureInfo.InvariantCulture)} GB/s"
            }
            : enriched;
    }

    private static ComponentOption Hosted(ModelCatalog models, ComponentOption option)
    {
        if (option.ProviderId is not { } provider || !TakesModels(option.Component)) return option;
        var route = option.Component switch
        {
            PlanComponent.Thinking or PlanComponent.Vision when option.ModelId is not null => SuggestedChat(models, provider),
            PlanComponent.DeepThinking => SmartestChat(models, provider),
            PlanComponent.Hearing when option.ModelId is { } id && models.Route(provider, id) is { Retired: false, Deprecated: false } today => today,
            PlanComponent.Hearing => SuggestedHearing(models, provider),
            _ => null
        };
        route ??= option.ModelId is { } current ? models.Route(provider, current) : null;
        if (route is null || models.Model(route.ModelKey) is not { } model) return option;
        var smartness = models.Smartness(model);
        var chosen = !string.Equals(route.ModelId, option.ModelId, StringComparison.Ordinal);
        return option with
        {
            ModelId = route.ModelId, Origin = chosen ? OptionOrigin.Catalog : option.Origin, CatalogKey = model.Key,
            Smartness = Words(smartness, estimates: chosen || option.Component == PlanComponent.DeepThinking),
            SmartnessCredit = Credit(smartness), CallsTools = route.Fact(CatalogFacts.Tools).Yes, TakesVideo = route.Fact(CatalogFacts.InputVideo).Yes == true,
            SeesImages = option.SeesImages || route.Fact(CatalogFacts.InputImage).Yes == true,
            // Deep thinking names its model, and its tier is the model's: the planner compares it with live Thinking's.
            QualityTier = option.Component == PlanComponent.DeepThinking ? smartness.Tier : option.QualityTier,
            DisplayName = option.Component == PlanComponent.DeepThinking ? $"{ProviderName(provider)}: {model.Name} (free endpoint)" : option.DisplayName,
            FreeTier = option.FreeTier || route.Free == true,
            Source = chosen
                ? $"The model catalog's {ProviderName(provider)} route: " + (option.Component == PlanComponent.DeepThinking
                    ? "the smartest free chat model, not retired"
                    : option.Component == PlanComponent.Hearing ? "free, hears, not retired, the fewest active parameters"
                    : "free, sees, calls tools, not retired, the fewest active parameters (the fastest known)") + "; uses no local resources"
                : option.Source
        };
    }

    private static string ProviderName(string provider) =>
        Settings.ChatCompletionsEndpointCatalog.ById(provider)?.Name ?? provider;

    private static bool Usable(ModelCatalog models, CatalogRoute route, string provider) =>
        string.Equals(route.Provider, provider, StringComparison.OrdinalIgnoreCase) && route.Free == true && !route.Retired && !route.Deprecated &&
        models.Model(route.ModelKey)?.Fact(CatalogFacts.OutputText).Yes == true && Chat(route.ModelId);

    private static double? Active(ModelCatalog models, CatalogRoute route) => models.Model(route.ModelKey) is { } model
        ? model.Fact(CatalogFacts.ParametersActive).Number ?? model.Fact(CatalogFacts.ParametersTotal).Number
        : null;

    private static CatalogSmartness Smartness(ModelCatalog models, CatalogRoute route) =>
        models.Model(route.ModelKey) is { } model ? models.Smartness(model) : models.Smartness(route.ModelId);

    private static double Gb(long bytes) => Math.Round(bytes / 1e9, 1);

    [GeneratedRegex(@"guard|safety|jailbreak|(?<![a-z])reasoning(?![a-z])|paligemma|calibration|(?<![a-z])ocr(?![a-z])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NotConversation();
}
