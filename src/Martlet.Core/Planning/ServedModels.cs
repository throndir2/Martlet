using System.Globalization;
using System.Text.RegularExpressions;

namespace Martlet.Core.Planning;

/// <summary>Thinking options for the chat models the owner's own model apps already serve (<see cref="ServedModel"/>): Ollama,
/// LM Studio, llama.cpp, vLLM and the others Martlet finds on this PC. The owner runs them, so Recommended setup assumes they
/// want to think with them (docs/RECOMMENDED_SETUPS.md#models-your-apps-already-run). Their option id is "served:" plus the
/// model id. Sizes are estimates: the app's download size plus the context, else the size in the model's name.</summary>
public static partial class ServedModels
{
    public const string Prefix = "served:";

    public static string OptionId(string model) => Prefix + model;

    /// <summary>The model of a served option id ("served:llama3.3:70b" is "llama3.3:70b"), or null for another id.</summary>
    public static string? ModelOf(string? optionId) =>
        optionId is { Length: > 7 } id && id.StartsWith(Prefix, StringComparison.Ordinal) ? id[Prefix.Length..] : null;

    public static bool IsServed(ComponentOption? option) => ModelOf(option?.Id) is not null;

    /// <summary>Whether <paramref name="model"/> is a chat model (not an embedding, reranking, speech or picture model).</summary>
    public static bool Chats(string model) => !NotChat().IsMatch(model);

    /// <summary>The models in <paramref name="served"/> worth planning with: chat models with a computer, that none of
    /// <paramref name="catalog"/>'s own options run (Martlet's own Ollama models stay Martlet's options), one per model (the
    /// first app wins).</summary>
    public static IReadOnlyList<ServedModel> Usable(IEnumerable<ServedModel>? served, FootprintCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return (served ?? []).Where(m => m is { MachineId.Length: > 0, ModelId.Length: > 0 } && Chats(m.ModelId) &&
                !catalog.Options.Any(o => o.ServedBy is null && o.IsLocal && string.Equals(o.ModelId, m.ModelId, StringComparison.OrdinalIgnoreCase)))
            .GroupBy(m => m.ModelId, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
    }

    /// <summary>The Thinking option for <paramref name="served"/>, on any graphics card, sized as <see cref="Gb"/> says (on the
    /// processor with no size when its size isn't known).</summary>
    public static ComponentOption Option(ServedModel served) => Option(served, null);

    /// <summary>The Thinking option for <paramref name="served"/>, as <see cref="Option(ServedModel)"/>; how smart it is comes
    /// from <paramref name="models"/> when the model catalog knows it, else from the size in its name.</summary>
    public static ComponentOption Option(ServedModel served, ModelCatalog? models)
    {
        ArgumentNullException.ThrowIfNull(served);
        var gb = Gb(served);
        var known = models?.Find(served.ModelId)?.Model;
        var smartness = known is null ? null : models!.Smartness(known);
        return new()
        {
            Id = OptionId(served.ModelId), Component = PlanComponent.Thinking, DisplayName = served.ModelId, ModelId = served.ModelId,
            Gpu = gb is null ? GpuRequirement.None : GpuRequirement.AnyGpu, Steady = new(gb ?? 0, 1, 1, 0), Peak = new(gb ?? 0, 1.5, 2, 0),
            QualityTier = smartness?.Tier ?? Tier(served), Evidence = FootprintEvidence.Estimate,
            ServedBy = served.AppName.Length > 0 ? served.AppName : "your model app",
            ServedOn = served.MachineId.Length > 0 ? served.MachineId : null, ServedAt = served.BaseUrl.Length > 0 ? served.BaseUrl : null,
            Origin = OptionOrigin.Served, CatalogKey = known?.Key, Smartness = CatalogOptions.Words(smartness, estimates: true),
            SmartnessCredit = CatalogOptions.Credit(smartness),
            CallsTools = known?.CallsTools, TakesVideo = known?.TakesVideo == true,
            Source = gb is null ? "Served by your own model app; its size isn't known"
                : served.SizeGb is not null ? "Estimate: the app's download size plus the context and buffers"
                : "Estimate from the model's size in its name"
        };
    }

    /// <summary>The option for a served model Martlet knows only by its id (today's Thinking route): no app or computer known.</summary>
    public static ComponentOption Option(string model) => Option(new ServedModel("", "", "", "", model), null);

    /// <summary>As <see cref="Option(string)"/>, with how smart it is from <paramref name="models"/>.</summary>
    public static ComponentOption Option(string model, ModelCatalog? models) => Option(new ServedModel("", "", "", "", model), models);

    /// <summary>About how much graphics memory the model takes at Martlet's context: the app's download size plus 5% and
    /// 1 GB for the context and buffers, else about 0.65 GB per billion parameters in its name ("70b") plus 0.8 GB; null
    /// when neither is known.</summary>
    public static double? Gb(ServedModel served)
    {
        ArgumentNullException.ThrowIfNull(served);
        if (served.SizeGb is { } size && size > 0) return Math.Round(size * 1.05 + 1, 1);
        return Billions(served.ModelId) is { } billions ? Math.Round(billions * 0.65 + 0.8, 1) : null;
    }

    /// <summary>The parameter count in billions from the model's name ("qwen3:32b", "Llama-3.3-70B-Instruct"), or null.</summary>
    public static double? Billions(string model)
    {
        var match = Size().Match(model);
        return match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var billions)
            ? billions : null;
    }

    /// <summary>1 to 5 from the parameter count (from the name, else from the download size at about 4-bit weights): bigger
    /// models think better.</summary>
    private static int Tier(ServedModel served) => Tier(served.ModelId, served.SizeGb);

    /// <summary>1 to 5 for <paramref name="model"/> from the parameter count in its name, else from
    /// <paramref name="sizeGb"/> (its download size at about 4-bit weights).</summary>
    public static int Tier(string model, double? sizeGb = null)
    {
        var billions = Billions(model) ?? (sizeGb is { } size && size > 0 ? size / 0.6 : null);
        return billions switch
        {
            null or < 4 => 1,
            < 10 => 2,
            < 20 => 3,
            < 40 => 4,
            _ => 5
        };
    }

    [GeneratedRegex(@"(?<![a-z])(\d+(?:\.\d+)?)b(?![a-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Size();

    [GeneratedRegex(@"embed|rerank|whisper|(?<![a-z])tts|bge-|minilm|(?<![a-z])clip|stable-diffusion|sdxl|flux|kokoro|parakeet",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NotChat();
}
