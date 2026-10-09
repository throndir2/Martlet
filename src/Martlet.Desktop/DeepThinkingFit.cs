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
    internal static Dictionary<string, (string Value, string Why)> Recommend(HostHardware? hardware, IReadOnlyDictionary<string, string>? offers,
        int card = 1)
    {
        var gpu = hardware?.BestGpu is { IsNvidia: true } best && hardware.NvidiaContainers != "no" ? best : null;
        var cardGb = gpu?.MemoryGb;
        // A Thinking pool model on an extra card (deep-thinking-2...) is pinned to a card no other role uses.
        IReadOnlyList<(string Name, double Gb)> others = card > 1 ? [] : Others(offers);
        (string Value, string Why) For(string model)
        {
            var (slots, why) = DeepThinkingSlots.Fit(model, ListeningAdvisor.OllamaModelGb(model), cardGb, others);
            return (slots.ToString(System.Globalization.CultureInfo.InvariantCulture), why);
        }
        var suggested = SuggestedByVram.LastOrDefault(s => (gpu?.MemoryMb ?? 0) >= s.MiB).Model ?? Models[0];
        var now = offers?.GetValueOrDefault(Martlet.Core.Settings.SelfHostSetup.DeepThinkingRoleKind(card)) is { } alias ? Tag(alias) : null;
        var result = new Dictionary<string, (string, string)>(StringComparer.Ordinal) { ["choice." + SlotsVariable] = For(now ?? suggested) };
        foreach (var model in Models) result[$"choice.{SlotsVariable}@{ModelVariable}={model}"] = For(model);
        result[$"choice.{SlotsVariable}@{ModelVariable}={HostInputDialog.Automatic}"] = For(suggested);
        return result;
    }

    /// <summary>The warning for a Deep thinking model that would share a graphics card with a Thinking model: on
    /// <paramref name="where"/>, which runs Thinking's <paramref name="thinking"/> (null: no Thinking model there, so no warning)
    /// and has <paramref name="cards"/> dedicated graphics cards. Two models that run at the same time share one card's memory
    /// speed and compute, so each runs at about half speed; Martlet recommends one graphics card for each Thinking model.</summary>
    internal static string? SharedCard(string where, string? thinking, int cards, bool sameCard = false)
    {
        if (thinking is null || cards >= 2 && !sameCard) return null;
        var on = sameCard ? "the same graphics card" : cards == 0 ? "its processor (no graphics card)" : "its only graphics card";
        return $"{where} already runs a Thinking model ({thinking}) on {on}. A Thinking pool model there shares it: while both run, " +
               "each runs at about half speed, so replies can start later. We recommend one graphics card for each Thinking model: " +
               "add another computer with its own graphics card, or a cloud provider, to the Thinking pool instead.";
    }

    /// <summary><see cref="SharedCard(string, string?, int)"/> for a paired host: its Thinking model from its Ollama role in
    /// <paramref name="offers"/>, and its dedicated graphics cards from <paramref name="hardware"/> (one when it hasn't reported).</summary>
    internal static string? SharedCard(string hostId, HostHardware? hardware, IReadOnlyDictionary<string, string>? offers) =>
        SharedCard(hostId, offers?.GetValueOrDefault(HostRoles.Ollama) is { } alias ? Tag(alias) : null,
            hardware is null ? 1 : DedicatedCards(hardware.Gpus.Select(g => (g.Vendor, g.MemoryGb))));

    /// <summary><see cref="SharedCard(string, string?, int, bool)"/> for a Thinking pool member on a paired host, one per graphics
    /// card: when the host names the cards of the member's route and of its Thinking route, it warns only when they share one.
    /// Otherwise a member on an extra card (deep-thinking-2...) is on a card of its own, and the first one warns as
    /// <see cref="SharedCard(string, HostHardware?, IReadOnlyDictionary{string, string}?)"/> does.</summary>
    internal static string? SharedCard(Martlet.Core.Settings.DeepThinkingSettings member, HostHardware? hardware, HostCheck? check)
    {
        if (member is not { Place: Martlet.Core.Settings.DeepThinkingPlace.Host, HostId: { } host } || check?.Offers is not { } offers) return null;
        if (offers.GetValueOrDefault(HostRoles.Ollama) is not { } alias) return null;
        var routes = check.Routes ?? [];
        var mine = routes.FirstOrDefault(r => r.RouteId == member.HostRoute)?.Gpus ?? [];
        var theirs = routes.FirstOrDefault(r => r.RouteId == Martlet.Avatar.Audio2Face.Remote.HostRoute.OllamaChatRouteId)?.Gpus ?? [];
        var cards = hardware is null ? 1 : DedicatedCards(hardware.Gpus.Select(g => (g.Vendor, g.MemoryGb)));
        if (mine.Count > 0 && theirs.Count > 0 && !mine.Contains("cpu") && !theirs.Contains("cpu"))
            return mine.Any(gpu => theirs.Contains(gpu, StringComparer.Ordinal)) ? SharedCard(host, Tag(alias), cards, sameCard: true) : null;
        return member.Card > 1 ? null : SharedCard(host, Tag(alias), cards);
    }

    /// <summary>How many of <paramref name="gpus"/> (vendor or name, memory) are dedicated graphics cards a model can run on.</summary>
    internal static int DedicatedCards(IEnumerable<(string VendorOrName, double? MemoryGb)> gpus) =>
        gpus.Count(g => SetupAdvisor.Classify(g.VendorOrName, g.MemoryGb) != AdvisorGpu.None);

    /// <summary>The Ollama tag of a model a host route names by its alias ("gemma4-e4b" is gemma4:e4b), when it is one the role
    /// offers; otherwise the alias itself.</summary>
    internal static string Tag(string alias) =>
        Models.Concat(MainWindow.LocalChatModels.Select(m => m.Id)).FirstOrDefault(m => m.Replace(':', '-') == alias) ?? alias;
}
