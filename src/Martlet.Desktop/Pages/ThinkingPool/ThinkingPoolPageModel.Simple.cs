using Martlet.Conversation;
using Martlet.Core.Settings;
using Martlet.Providers.Ollama;

namespace Martlet.Desktop;

/// <summary>Companion › Background thinking's simple list (Where it runs): the machines Martlet knows that aren't in the pool yet,
/// each with a box and a model, and what the What it does switches save.</summary>
internal sealed partial class ThinkingPoolPageModel
{
    /// <summary>What a machine that isn't in the pool yet is.</summary>
    internal enum JoinKind
    {
        /// <summary>A paired computer that isn't a member.</summary>
        Computer,
        /// <summary>A graphics card of a member computer without a Thinking pool model yet.</summary>
        Card,
        /// <summary>Ollama on this PC, while no member uses it.</summary>
        ThisPc
    }

    /// <summary>A machine that isn't in the pool yet, as its row in Where it runs shows it: ticking it adds it with the chosen model.</summary>
    /// <param name="Id">The end of its automation IDs: the host ID, host ID-gpu2, or this-pc.</param>
    /// <param name="Models">The models to choose from; <paramref name="Selected"/> first chosen.</param>
    /// <param name="Cannot">Why it can't be ticked now, or null.</param>
    /// <param name="Own">The model its Thinking pool role runs now (a paired computer), or null.</param>
    /// <param name="Ollama">The model its own Ollama runs (a paired computer whose Ollama may join), or null.</param>
    internal sealed record JoinLine(string Id, JoinKind Kind, string Title, string Detail, IReadOnlyList<string> Models, string Selected,
        string? Cannot, PairedHost? Host = null, int Card = 1, string? Own = null, string? Ollama = null, string? SharedCard = null);

    /// <summary>Every machine Martlet knows that isn't in the pool: each spare graphics card of a member computer, each paired
    /// computer that isn't a member, and Ollama on this PC when no member uses it.</summary>
    internal IReadOnlyList<JoinLine> Candidates()
    {
        var lines = new List<JoinLine>();
        foreach (var spare in SpareCards())
            lines.Add(new($"{spare.HostId}-gpu{spare.Card}", JoinKind.Card, spare.Title, spare.Detail, DeepThinkingFit.Models,
                DeepThinkingFit.Suggested(services.HardwareOf(spare.HostId)), spare.Cannot, spare.Host, spare.Card));
        foreach (var host in Others)
        {
            var computer = Computer(host);
            var check = computer.Check;
            var ollama = check?.Offers?.GetValueOrDefault(HostRoles.Ollama) is { } alias && ThinkingHost != host.HostId ? alias : null;
            var own = computer.Own is { } role ? DeepThinkingFit.Tag(role) : null;
            var joinsAsIs = own is not null || ollama is not null;
            var cannot = check?.Reachable != true ? (check?.Reachable == false ? $"{host.HostId} isn't reachable right now." : $"{host.HostId} isn't checked yet.")
                : joinsAsIs ? null
                : !host.CanLaunch ? $"Martlet can't set up roles on {host.HostId} from here."
                : services.CannotHand(host.HostId, HostRoles.DeepThinking, "deep thinking");
            var first = own ?? ollama ?? DeepThinkingFit.Suggested(services.HardwareOf(host.HostId));
            string[] models = [.. new[] { first }.Concat(DeepThinkingFit.Models).Distinct(StringComparer.Ordinal)];
            lines.Add(new(host.HostId, JoinKind.Computer, host.HostId, computer.Detail, models, first, cannot, host, 1, own, ollama, computer.SharedCard));
        }
        if (!Pool.All.Any(m => m.Place == DeepThinkingPlace.Endpoint && m.Origin == MainWindow.LocalOllamaBaseUrl))
        {
            var choices = LocalChoices.Where(id => !OllamaSideBySide.Same(id, LocalBeside)).ToArray();
            var start = LocalStart(choices);
            var detail = LocalBeside is { } beside ? $"A second model in Ollama on this PC, beside Thinking's {beside}. {LocalState}" : LocalState;
            lines.Add(new("this-pc", JoinKind.ThisPc, "This PC (Ollama)", detail, choices.Length > 0 ? choices : [start],
                start, null, SharedCard: LocalShare.Length > 0 ? LocalShare : null));
        }
        return lines;
    }

    /// <summary>Adds <paramref name="line"/> to the pool with <paramref name="model"/>: a graphics card or a paired computer
    /// without a usable model gets the Thinking pool role with that model (a run window); a paired computer whose role or Ollama
    /// already runs it joins at once (another model for its role is set up there first); Ollama on this PC joins with it.</summary>
    internal async Task JoinAsync(JoinLine line, string model)
    {
        if (services.Closing) return;
        switch (line)
        {
            case { Kind: JoinKind.ThisPc }:
                await AddLocalAsync(model);
                return;
            case { Kind: JoinKind.Card, Host: { } host }:
                await AddDeepRoleAsync(host, line.Card, model);
                return;
            case { Kind: JoinKind.Computer, Host: { } host, Own: { } own }:
                if (!string.Equals(own, model, StringComparison.Ordinal)) await ChangeDeepRoleAsync(host, 1, model, null);
                await UseDeepHostAsync(host, alsoHere: true);
                return;
            case { Kind: JoinKind.Computer, Host: { } host, Ollama: { } ollama } when string.Equals(DeepThinkingFit.Tag(ollama), DeepThinkingFit.Tag(model), StringComparison.Ordinal):
                await UseDeepHostAsync(host, alsoHere: true);
                return;
            case { Kind: JoinKind.Computer, Host: { } host }:
                await AddDeepRoleAsync(host, 1, model);
                return;
        }
    }

    /// <summary>The models for a cloud provider or server member's list: its model now, then the catalog's chat models on
    /// <paramref name="origin"/> that call tools (free first, then the smartest).</summary>
    internal static IReadOnlyList<string> CloudModels(string origin, string? current) =>
        [.. new[] { current ?? "" }.Concat(Martlet.Core.Planning.PlanningCatalog.ChatModels(origin)).Where(id => id.Length > 0).Distinct(StringComparer.Ordinal)];

    /// <summary>Backup for slow replies turned on or off. Turned on with no machine ticked to answer, every free machine that is on
    /// (not a paid cloud provider) may answer: a paid one stays your choice under Advanced.</summary>
    internal static ThinkingPoolSettings WithBackup(ThinkingPoolSettings pool, bool on)
    {
        var next = pool with { BackupThinking = on };
        if (!on || pool.Members.Any(m => pool.Answers(m.Key))) return next;
        return pool.Members.Where(m => !ThinkingBackupMembers.Paid(m)).Aggregate(next, (p, m) => p.WithAnswers(m.Key, true));
    }

    /// <summary>The status line after Backup for slow replies is turned on or off in <paramref name="next"/>.</summary>
    internal static string BackupDone(ThinkingPoolSettings next)
    {
        if (!next.BackupThinking) return "Help when a reply is slow is off.";
        var answering = next.Members.Where(m => next.Answers(m.Key)).Select(m => m.Describe()).ToArray();
        return answering.Length == 0
            ? "Help when a reply is slow is on, but no machine may answer yet: tick a free machine under Where it runs, or choose one under Advanced."
            : $"Help when a reply is slow is on: a slow reply also asks {string.Join(", ", answering)}.";
    }
}
