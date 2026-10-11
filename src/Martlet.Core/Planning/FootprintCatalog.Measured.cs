using System.Globalization;
using Martlet.Core.Settings;

namespace Martlet.Core.Planning;

/// <summary>One option whose numbers Martlet measured (<see cref="FootprintCatalog.WithMeasured"/>): the measured first word and
/// graphics memory beside the estimates they replace, and the server that measured them.</summary>
public sealed record OptionMeasurement(string OptionId, string Model, string Host)
{
    public int? FirstWordMs { get; init; }
    public int? EstimatedFirstWordMs { get; init; }
    /// <summary>How many replies the first word is the middle of.</summary>
    public int Replies { get; init; }
    public double? GpuGb { get; init; }
    public double? EstimatedGpuGb { get; init; }
    public DateTimeOffset MeasuredAt { get; init; }

    /// <summary>"gemma4:e4b: first word 0.22 s (estimate 0.21 s, 5 replies), 7.4 GB of graphics memory (estimate 7.9 GB), measured
    /// on http://127.0.0.1:11434".</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (FirstWordMs is { } ms)
            parts.Add($"first word {PlacementEngine.Seconds(ms)}" +
                (EstimatedFirstWordMs is { } guess ? $" (estimate {PlacementEngine.Seconds(guess)})" : "") +
                $" from {Replies} repl{(Replies == 1 ? "y" : "ies")}");
        if (GpuGb is { } gb)
            parts.Add($"{PlacementEngine.Gb(gb)} GB of graphics memory" + (EstimatedGpuGb is { } before ? $" (estimate {PlacementEngine.Gb(before)} GB)" : ""));
        return $"{Model}: {string.Join(", ", parts)}, measured on {Host}";
    }
}

public sealed partial class FootprintCatalog
{
    /// <summary>The options whose numbers <see cref="WithMeasured"/> replaced with measured ones.</summary>
    public IReadOnlyList<OptionMeasurement> Measurements { get; private init; } = [];

    /// <summary>This catalog with measured numbers in place of the estimates (docs/RECOMMENDATION_DESIGN.md, "Staying current"):
    /// a Thinking model's first word from the replies Martlet timed (<paramref name="speed"/>, model-speed.json) and its graphics
    /// memory as Ollama reported it (<paramref name="memory"/>, model-memory.json, from this PC and from each paired host). The
    /// newest measurement of a model counts, whichever computer made it. A first word measured with the model on the graphics
    /// card replaces its graphics card option's estimate, one measured on the processor its processor option's; a hosted option
    /// takes the time measured on its provider. Memory counts only when Ollama held the model on a graphics card. Pure.</summary>
    public FootprintCatalog WithMeasured(MeasuredModelMemory? memory, MeasuredFirstWords? speed)
    {
        if ((memory?.Models.Count ?? 0) == 0 && (speed?.Models.Count ?? 0) == 0) return this;
        var measurements = new List<OptionMeasurement>();
        var next = options.Select(option =>
        {
            var (measured, found) = Measure(option, memory, speed);
            if (found is not null) measurements.Add(found);
            return measured;
        }).ToArray();
        return measurements.Count == 0 ? this : new(next) { Measurements = [.. Measurements, .. measurements], Models = Models, From = From };
    }

    private static (ComponentOption Option, OptionMeasurement? Found) Measure(ComponentOption option, MeasuredModelMemory? memory, MeasuredFirstWords? speed)
    {
        if (option.Component != PlanComponent.Thinking || option.ModelId is not { Length: > 0 } model)
            return (option, null);
        MeasuredFirstWord? first = null;
        MeasuredModelUse? use = null;
        if (option.IsLocal)
        {
            // A measurement on the processor (Ollama held none of it on the graphics card) is the processor option's.
            bool OnCard(MeasuredFirstWord m) => memory?.Find(m.Host, m.Model) is not { GraphicsBytes: 0 };
            if (option.Component == PlanComponent.Thinking)
            {
                first = speed?.FindAll(model).FirstOrDefault(m => !Hosted(m.Host) && OnCard(m) == option.UsesGpu);
                // Ollama's Thinking context is the planner's (8,192 tokens); a Deep thinking role's is bigger, so it keeps its estimate.
                if (option.UsesGpu) use = memory?.FindAll(model).FirstOrDefault(m => m.Bytes > 0 && m.GraphicsBytes > 0);
            }
        }
        else if (option.Component == PlanComponent.Thinking && ChatCompletionsEndpointCatalog.ById(option.ProviderId) is { } provider)
            first = speed?.Find(provider.BaseUrl, model);
        if (first is null && use is null) return (option, null);
        var next = option;
        var notes = new List<string>();
        if (first is not null)
        {
            next = next with { FirstWordMs = first.Ms };
            notes.Add($"first word {PlacementEngine.Seconds(first.Ms)} measured on {first.Host} ({first.Samples.Count} replies)");
        }
        double? gb = null;
        if (use is not null)
        {
            gb = Math.Round(use.Bytes / 1e9, 2);
            var weights = Math.Max(0, gb.Value - option.ContextGb);
            next = next with { Peak = next.Peak with { VramGb = weights }, Steady = next.Steady with { VramGb = weights } };
            notes.Add($"{PlacementEngine.Gb(gb.Value)} GB measured in Ollama on {use.Host}" +
                (use.ContextTokens is { } tokens ? $" at {tokens.ToString("N0", CultureInfo.InvariantCulture)} tokens" : ""));
        }
        next = next with
        {
            Evidence = FootprintEvidence.Measured,
            Source = $"Measured: {string.Join("; ", notes)}." + (option.Source.Length > 0 ? $" Estimate before: {option.Source}" : "")
        };
        var at = new[] { first?.MeasuredAt, use?.MeasuredAt }.Max() ?? default;
        return (next, new OptionMeasurement(option.Id, model, first?.Host ?? use!.Host)
        {
            FirstWordMs = first?.Ms, EstimatedFirstWordMs = first is null ? null : option.FirstWordMs, Replies = first?.Samples.Count ?? 0,
            GpuGb = gb, EstimatedGpuGb = gb is null ? null : Math.Round(option.GpuGb, 2), MeasuredAt = at
        });
    }

    /// <summary>A named cloud provider's server (OpenRouter, NVIDIA Build...), not one of the owner's computers.</summary>
    private static bool Hosted(string host) => ChatCompletionsEndpointCatalog.NamedEndpoints
        .Any(e => string.Equals(MeasuredFirstWords.HostKey(e.BaseUrl), host, StringComparison.OrdinalIgnoreCase));
}
