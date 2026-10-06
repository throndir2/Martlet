using System.Globalization;
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
    private const double ContextGbPerModelGb = 0.75;
    private const double MinimumContextGb = 0.25;

    /// <summary>About how much memory one think's context takes for a model of <paramref name="modelGb"/> loaded with
    /// <paramref name="contextTokens"/> of context (Deep thinking loads up to <see cref="GenerationSettings.MaximumHostContextTokens"/>).</summary>
    public static double ContextGb(double modelGb, int contextTokens = GenerationSettings.MaximumHostContextTokens) =>
        Math.Max(MinimumContextGb, modelGb * ContextGbPerModelGb * contextTokens / GenerationSettings.MaximumHostContextTokens);

    /// <summary>The most thinks at once that fit (at least one, at most <see cref="SelfHostSetup.DeepThinkingMaximumSlots"/>)
    /// and why: <paramref name="model"/> (about <paramref name="modelGb"/>) with one context per slot, beside
    /// <paramref name="others"/> (the host's other models on the card, each with what it takes), on a card of
    /// <paramref name="cardGb"/> (null when the host hasn't reported one: then only one, which runs on the processor).</summary>
    public static (int Slots, string Why) Fit(string model, double modelGb, double? cardGb, IReadOnlyList<(string Name, double Gb)> others,
        int contextTokens = GenerationSettings.MaximumHostContextTokens)
    {
        ArgumentNullException.ThrowIfNull(others);
        if (cardGb is not > 0) return (1, "the host reported no graphics card, so it thinks one at a time on the processor");
        var context = ContextGb(modelGb, contextTokens);
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
