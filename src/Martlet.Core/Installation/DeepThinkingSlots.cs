using System.Globalization;
using Martlet.Core.Planning;
using Martlet.Core.Settings;

namespace Martlet.Core.Installation;

/// <summary>How many thinks at once a host's Deep thinking role can run on its graphics card (its Ollama's
/// <c>OLLAMA_NUM_PARALLEL</c> slots) without pushing the host's other models off the card. Ollama loads the model once and
/// reserves one think's context (the KV cache) per slot when it loads, so each slot beyond the first costs another context's
/// memory and nothing more while thinks run; the fit is therefore decided once, when the slots are chosen. Estimates err
/// large: a Thinking or voice model pushed off the card would delay every reply, while one slot too few only queues a think.</summary>
public static class DeepThinkingSlots
{
    /// <summary>Kept free on the card besides the models: the driver, desktop and Ollama's own buffers.</summary>
    public const double ReserveGb = 0.8;
    // For models the footprint catalog doesn't know: a full-attention model's KV cache at 32,768 tokens is about three
    // quarters of its size (Llama 3.1 8B: 4 GB of f16 cache for a 4.9 GB download).
    private const double ContextGbPerModelGb = 0.75;
    private const double MinimumContextGb = 0.25;

    /// <summary>About how much memory one think's context takes for a model of <paramref name="modelGb"/> loaded with
    /// <paramref name="contextTokens"/> of context (Deep thinking loads up to <see cref="GenerationSettings.MaximumHostContextTokens"/>),
    /// for a model the footprint catalog doesn't know (it errs large).</summary>
    public static double ContextGb(double modelGb, int contextTokens = GenerationSettings.MaximumHostContextTokens) =>
        Math.Max(MinimumContextGb, modelGb * ContextGbPerModelGb * contextTokens / GenerationSettings.MaximumHostContextTokens);

    /// <summary>One think's context for <paramref name="model"/>: the footprint catalog's Deep thinking
    /// <see cref="ComponentOption.ContextGb"/> (from the model's own config; Gemma 4's sliding-window layers keep it under
    /// 1 GB at 32,768 tokens, docs/RESOURCE_FOOTPRINTS.md) scaled to <paramref name="contextTokens"/>, else
    /// <see cref="ContextGb(double, int)"/> of <paramref name="modelGb"/>.</summary>
    public static double ContextGb(string model, double modelGb, int contextTokens = GenerationSettings.MaximumHostContextTokens) =>
        Catalog(PlanComponent.DeepThinking, model) is { ContextGb: > 0 } deep
            ? deep.ContextGb * contextTokens / GenerationSettings.MaximumHostContextTokens
            : ContextGb(modelGb, contextTokens);

    /// <summary>What <paramref name="model"/> itself takes on the card: the catalog's Deep thinking or Thinking VRAM (measured
    /// in Ollama where known), else <paramref name="fallbackGb"/>.</summary>
    public static double ModelGb(string model, double fallbackGb) =>
        (Catalog(PlanComponent.DeepThinking, model) ?? Catalog(PlanComponent.Thinking, model))?.Peak.VramGb ?? fallbackGb;

    /// <summary>What Thinking's <paramref name="model"/> takes on the card with its usual 8,192-token context: the catalog's
    /// Thinking VRAM (which includes that context), else <paramref name="fallbackGb"/> plus <see cref="ContextGb(double, int)"/>.</summary>
    public static double ThinkingGb(string model, double fallbackGb) =>
        Catalog(PlanComponent.Thinking, model) is { } thinking
            ? thinking.GpuGb
            : fallbackGb + ContextGb(fallbackGb, GenerationSettings.DefaultHostContextTokens);

    /// <summary>The most a host role of <paramref name="roleKind"/> takes on the card: the largest of its graphics card options
    /// in the footprint catalog (estimates err large), or null when the catalog has none.</summary>
    public static double? RoleGb(string roleKind) =>
        FootprintCatalog.Default.Options.Where(o => o.UsesGpu && o.HostRoleKind == roleKind).Select(o => (double?)o.GpuGb).Max();

    private static ComponentOption? Catalog(PlanComponent component, string model) =>
        FootprintCatalog.Default.For(component).FirstOrDefault(o => o.UsesGpu && string.Equals(o.ModelId, model, StringComparison.OrdinalIgnoreCase));

    /// <summary>The most thinks at once that fit (at least one, at most <see cref="SelfHostSetup.DeepThinkingMaximumSlots"/>)
    /// and why: <paramref name="model"/> (its catalog size, else about <paramref name="modelGb"/>) with one context per slot
    /// (<see cref="ContextGb(string, double, int)"/>), beside
    /// <paramref name="others"/> (the host's other models on the card, each with what it takes), on a card of
    /// <paramref name="cardGb"/> (null when the host hasn't reported one: then only one, which runs on the processor).</summary>
    public static (int Slots, string Why) Fit(string model, double modelGb, double? cardGb, IReadOnlyList<(string Name, double Gb)> others,
        int contextTokens = GenerationSettings.MaximumHostContextTokens)
    {
        ArgumentNullException.ThrowIfNull(others);
        if (cardGb is not > 0) return (1, "the host reported no graphics card, so it thinks one at a time on the processor");
        modelGb = ModelGb(model, modelGb);
        var context = ContextGb(model, modelGb, contextTokens);
        var taken = others.Sum(o => o.Gb);
        var room = cardGb.Value - taken - ReserveGb - modelGb;
        var slots = Math.Clamp((int)Math.Floor(room / context), 1, SelfHostSetup.DeepThinkingMaximumSlots);
        var beside = others.Count == 0 ? "" : " beside " + string.Join(", ", others.Select(o => $"{o.Name} (about {Gb(o.Gb)} GB)"));
        var why = room < context
            ? $"{model} (about {Gb(modelGb)} GB) with one think's context (about {Gb(context)} GB) already fills its {Gb(cardGb.Value)} GB card{beside}"
            : $"{model} (about {Gb(modelGb)} GB) plus {slots} think{(slots == 1 ? "'s context" : "s' contexts")} (about {Gb(context)} GB each) " +
              $"fit{(slots == 1 ? "s" : "")} its {Gb(cardGb.Value)} GB card{beside}";
        return (slots, why);
    }

    private static string Gb(double gb) => gb.ToString("0.#", CultureInfo.InvariantCulture);
}
