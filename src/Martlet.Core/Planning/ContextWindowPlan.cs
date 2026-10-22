using System.Globalization;
using Martlet.Core.Installation;
using Martlet.Core.Settings;

namespace Martlet.Core.Planning;

/// <summary>A planned context window: the tokens the model loads with, the graphics memory that window takes beyond Martlet's
/// 8,192-token footprint (<see cref="ExtraGb"/>), and why, in plain words.</summary>
public sealed record PlannedContext(int Tokens, double ExtraGb, string Why);

/// <summary>How large a context window the conversation model on a card loads: the largest of
/// <see cref="GenerationSettings.PlannedContextTokens"/>, 16,384 and 8,192 tokens that fits the card with the model, the other
/// models on that card, <see cref="DeepThinkingSlots.ReserveGb"/> and <see cref="HeadroomGb"/>; never above the model's own limit
/// and never below <see cref="GenerationSettings.DefaultHostContextTokens"/>. A bigger window is room, not more text: each reply
/// still sends the same input (<see cref="ContextBudget.PlannedInputTokens"/>), so it reads the same tokens and starts as soon.
/// 32,768 is the Thinking pool's window too, so a pool job on the same model never makes Ollama load it again.</summary>
public static class ContextWindowPlan
{
    /// <summary>Kept free on the card beyond the reserve, so an estimate that is a little low never pushes a model off it.</summary>
    public const double HeadroomGb = 1.0;

    /// <summary>The windows tried, largest first.</summary>
    public static readonly IReadOnlyList<int> Steps = [GenerationSettings.PlannedContextTokens, 16_384, GenerationSettings.DefaultHostContextTokens];

    /// <summary>The graphics memory <paramref name="tokens"/> of context takes for <paramref name="model"/> beyond its 8,192-token
    /// footprint: the footprint catalog's Deep thinking context (from the model's own config) scaled, else a large guess from
    /// <paramref name="modelGb"/> (<see cref="DeepThinkingSlots.ContextGb(double, int)"/>).</summary>
    public static double ExtraGb(string model, double modelGb, int tokens)
    {
        var floor = GenerationSettings.DefaultHostContextTokens;
        if (tokens <= floor) return 0;
        var known = FootprintCatalog.Default.For(PlanComponent.DeepThinking)
            .FirstOrDefault(o => o.UsesGpu && string.Equals(o.ModelId, model, StringComparison.OrdinalIgnoreCase));
        return known is { ContextGb: > 0 } deep
            ? deep.ContextGb * (tokens - floor) / (GenerationSettings.DeepThinkingContextTokens - floor)
            : DeepThinkingSlots.ContextGb(modelGb, tokens) - DeepThinkingSlots.ContextGb(modelGb, floor);
    }

    /// <summary>The window for <paramref name="model"/> (about <paramref name="modelGb"/> at 8,192 tokens) on a card of
    /// <paramref name="cardGb"/> GB (null: not known, then 8,192) beside <paramref name="others"/> on that card, at most
    /// <paramref name="modelMaximum"/> tokens when known.</summary>
    public static PlannedContext Fit(string model, double modelGb, double? cardGb, IReadOnlyList<(string Name, double Gb)> others,
        int? modelMaximum = null, string? cardName = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(others);
        var floor = GenerationSettings.DefaultHostContextTokens;
        var card = cardName ?? "the card";
        if (cardGb is not > 0)
            return new(floor, 0, $"{floor:N0} tokens: the graphics card's memory isn't known, so Martlet keeps its usual window");
        var taken = others.Sum(o => o.Gb);
        var free = cardGb.Value - modelGb - taken - DeepThinkingSlots.ReserveGb - HeadroomGb;
        foreach (var tokens in Steps)
        {
            if (modelMaximum is { } most && tokens > most && tokens > floor) continue;
            var extra = ExtraGb(model, modelGb, tokens);
            if (tokens > floor && extra > free) continue;
            var beside = others.Count == 0 ? "" : " beside " + string.Join(", ", others.Select(o => $"{o.Name} (about {Gb(o.Gb)} GB)"));
            var why = tokens == floor
                ? $"{tokens:N0} tokens: {model} (about {Gb(modelGb)} GB){beside} leaves too little of {card}'s {Gb(cardGb.Value)} GB for more"
                : $"{tokens:N0} tokens: {model} (about {Gb(modelGb)} GB) with this window (about {Gb(extra)} GB more){beside} fits " +
                  $"{card}'s {Gb(cardGb.Value)} GB with {Gb(DeepThinkingSlots.ReserveGb + HeadroomGb)} GB to spare";
            return new(tokens, extra, why);
        }
        return new(floor, 0, $"{floor:N0} tokens");
    }

    private static string Gb(double gb) => gb.ToString("0.#", CultureInfo.InvariantCulture);
}
