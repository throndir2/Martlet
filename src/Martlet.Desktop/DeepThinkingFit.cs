using Martlet.Core.Installation;

namespace Martlet.Desktop;

/// <summary>Martlet's recommendation for a host's Deep thinking role's thinks at once (its <c>OLLAMA_NUM_PARALLEL</c> choice),
/// for the role's settings window (Add Deep thinking, Change model): for each model it offers, the most slots that fit on the
/// host's graphics card beside the host's other roles (<see cref="DeepThinkingSlots.Fit"/>), so a Thinking or voice model there
/// is never pushed off the card. The window shows the one for the model chosen there.</summary>
internal static class DeepThinkingFit
{
    internal const string SlotsVariable = "OLLAMA_NUM_PARALLEL";
    internal const string ModelVariable = "OLLAMA_MODEL";

    /// <summary>The models the deep-thinking role offers and its suggestion by graphics memory (MiB, ascending), as its
    /// role.conf lists them (HostRolesTests checks they match).</summary>
    internal static readonly IReadOnlyList<string> Models =
    [
        "gemma4:e2b", "gemma4:e4b", "qwen3-vl:8b", "gemma4:12b", "gemma4:26b", "gemma3:4b", "qwen2.5vl:7b", "gemma3:12b", "gemma3:27b",
        "llama3.2:3b", "qwen2.5:7b", "llama3.1:8b", "qwen2.5:14b"
    ];
    internal static readonly IReadOnlyList<(int MiB, string Model)> SuggestedByVram =
        [(0, "gemma4:e2b"), (7000, "gemma4:e4b"), (11000, "gemma4:12b"), (22000, "gemma4:26b")];

    /// <summary>What the host's other roles take on its graphics card (<paramref name="offers"/>: role kind to model), from the
    /// footprint catalog, with Thinking's model counted with its usual context.</summary>
    internal static IReadOnlyList<(string Name, double Gb)> Others(IReadOnlyDictionary<string, string>? offers)
    {
        var others = new List<(string, double)>();
        if (offers is null) return others;
        if (offers.GetValueOrDefault(HostRoles.Ollama) is { } alias)
        {
            var thinking = Tag(alias);
            others.Add(($"Thinking's {thinking}", Math.Round(DeepThinkingSlots.ThinkingGb(thinking, ListeningAdvisor.OllamaModelGb(thinking)), 1)));
        }
        foreach (var (kind, name) in new[]
        {
            (HostRoles.Xtts, "the XTTS voice"), (HostRoles.GptSovits, "the GPT-SoVITS voice"), (HostRoles.Dia, "the Dia voice"),
            (HostRoles.F5, "the F5 voice"), (HostRoles.Chatterbox, "the Chatterbox voice"), (HostRoles.Audio2Face, "Audio2Face lip-sync"),
            (HostRoles.Stt, "listening"), (HostRoles.Singing, "singing"), (HostRoles.Pictures, "pictures")
        })
            if (offers.ContainsKey(kind)) others.Add((name, Math.Round(DeepThinkingSlots.RoleGb(kind) ?? 4, 1)));
        return others;
    }

    /// <summary>The recommendations for the role's settings window: <c>choice.OLLAMA_NUM_PARALLEL</c> for the model it runs now
    /// (else the one the host suggests for its card), and <c>choice.OLLAMA_NUM_PARALLEL@OLLAMA_MODEL=model</c> for each model it
    /// offers (and Automatic), which the window shows under the model chosen.</summary>
    internal static Dictionary<string, (string Value, string Why)> Recommend(HostHardware? hardware, IReadOnlyDictionary<string, string>? offers)
    {
        var gpu = hardware?.BestGpu is { IsNvidia: true } best && hardware.NvidiaContainers != "no" ? best : null;
        var cardGb = gpu?.MemoryGb;
        var others = Others(offers);
        (string Value, string Why) For(string model)
        {
            var (slots, why) = DeepThinkingSlots.Fit(model, ListeningAdvisor.OllamaModelGb(model), cardGb, others);
            return (slots.ToString(System.Globalization.CultureInfo.InvariantCulture), why);
        }
        var suggested = SuggestedByVram.LastOrDefault(s => (gpu?.MemoryMb ?? 0) >= s.MiB).Model ?? Models[0];
        var now = offers?.GetValueOrDefault(HostRoles.DeepThinking) is { } alias ? Tag(alias) : null;
        var result = new Dictionary<string, (string, string)>(StringComparer.Ordinal) { ["choice." + SlotsVariable] = For(now ?? suggested) };
        foreach (var model in Models) result[$"choice.{SlotsVariable}@{ModelVariable}={model}"] = For(model);
        result[$"choice.{SlotsVariable}@{ModelVariable}={HostInputDialog.Automatic}"] = For(suggested);
        return result;
    }

    /// <summary>The Ollama tag of a model a host route names by its alias ("gemma4-e4b" is gemma4:e4b), when it is one the role
    /// offers; otherwise the alias itself.</summary>
    internal static string Tag(string alias) =>
        Models.Concat(MainWindow.LocalChatModels.Select(m => m.Id)).FirstOrDefault(m => m.Replace(':', '-') == alias) ?? alias;
}
