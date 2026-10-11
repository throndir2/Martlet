using Martlet.Core.Cluster;

namespace Martlet.Core.Planning;

/// <summary>One of the three plans Recommended setup shows: <see cref="Situation"/>, its title ("While gaming") and one line
/// for each live job (where it goes in that situation).</summary>
public sealed record SituationPlan(Situation Situation, string Title, IReadOnlyList<string> Lines);

/// <summary>The three plans of a recommended setup (docs/RECOMMENDATION_DESIGN.md, stage 2): Normal, While gaming (each companion
/// PC whose owner said it is used for games, while a game runs) and Host away (each host that runs a live job, while it doesn't
/// answer), with the same order the desktop follows at run time (<see cref="LiveSituations"/>): another host, the hosted backup
/// when the online services preference allows it, then the companion PC's own model. Speaking and Listening follow their lists
/// (Companion › Voice and Listening). Pure.</summary>
public static class SituationPlans
{
    private static readonly string[] LiveJobs = [ClusterJobs.Thinking, ClusterJobs.Speaking, ClusterJobs.Listening];

    /// <param name="name">A computer's name from its ID (null: "your companion PCs").</param>
    /// <param name="backup">The hosted backup's name (If Thinking fails, "NVIDIA Build"), or null when none is set up.</param>
    /// <param name="backupAllowed">The online services preference allows the hosted backup (not Never).</param>
    public static IReadOnlyList<SituationPlan> For(NetworkSetupRequest request, NetworkRecommendation recommendation,
        Func<string?, string> name, FootprintCatalog? catalog = null, string? backup = null, bool backupAllowed = true)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(recommendation);
        ArgumentNullException.ThrowIfNull(name);
        catalog ??= FootprintCatalog.Default;
        var target = recommendation.Target;
        var online = request.Machines.Where(m => m.Online).ToList();
        var jobs = LiveJobs.Select(target.Job).OfType<JobPlan>().ToList();

        string Model(string machine) => target.Machine(machine)?.Roles.FirstOrDefault(r => r.Kind == "ollama")?.Model ?? "its model";
        string Where(JobPlan job) =>
            job.Off ? "nobody"
            : job.HostId is { } host ? job.Job == ClusterJobs.Thinking ? $"{name(host)} ({Model(host)})" : name(host)
            : job.OptionId is { } id && catalog.Find(id) is { } option
                ? option.ServedOn is { } on ? $"{name(on)} itself ({option.DisplayName} in {option.ServedBy})"
                : option.IsLocal ? $"each companion PC itself ({option.DisplayName})" : option.DisplayName
            : "each companion PC itself";
        // Hosts that run a Thinking model in the recommended setup, in plan order.
        IEnumerable<string> ThinkingHosts(string except) => online
            .Where(m => m.Specs.Id != except && target.Machine(m.Specs.Id)?.Roles.Any(r => r.Kind == "ollama" && r.Model is { Length: > 0 }) == true)
            .Select(m => $"{name(m.Specs.Id)} ({Model(m.Specs.Id)})");
        var hosted = backup is not null && backupAllowed ? backup : null;
        var never = backup is not null && !backupAllowed ? $" Online services are set to Never, so {backup} isn't used." : "";

        var normal = jobs.Select(job => $"{Title(job.Job)}: {Where(job)}.").ToList();

        var gaming = new List<string>();
        var gamers = online.Where(m => m.Kind == NetworkMachineKind.Companion && m.Specs.KeepGpuForGames).ToList();
        foreach (var pc in gamers)
        {
            var who = name(pc.Specs.Id);
            foreach (var job in jobs)
            {
                if (!OnCard(job, pc, catalog))
                {
                    gaming.Add($"On {who}: {Title(job.Job)} stays on {Where(job)}.");
                    continue;
                }
                if (job.Job == ClusterJobs.Thinking)
                {
                    var order = ThinkingHosts(pc.Specs.Id).ToList();
                    if (hosted is not null) order.Add(hosted);
                    var own = $"{who}'s own model (on the processor when the game needs the graphics card's memory)";
                    gaming.Add(order.Count == 0
                        ? $"On {who}: Thinking stays on {own}: no host runs a Thinking model{(hosted is null ? " and no hosted backup is set up" : "")}.{never}"
                        : $"On {who}: Thinking moves to {Chain([.. order, own])}. It comes back when the game ends.{never}");
                    continue;
                }
                var next = job.Pool.Where(p => p != pc.Specs.Id).Select(name).ToList();
                gaming.Add(next.Count == 0
                    ? $"On {who}: {Title(job.Job)} stays on its own graphics card: no other computer on its list runs its engine."
                    : $"On {who}: {Title(job.Job)} moves to {Chain(next)} (its list). It comes back when the game ends.");
            }
        }
        if (gamers.Count == 0) gaming.Add("No companion PC is used for games, so nothing moves.");

        var away = new List<string>();
        foreach (var host in jobs.Select(j => j.HostId).OfType<string>().Distinct(StringComparer.Ordinal)
                     .Where(id => online.Any(m => m.Specs.Id == id && m.Kind == NetworkMachineKind.Host)))
        {
            var who = name(host);
            foreach (var job in jobs.Where(j => j.HostId == host))
            {
                if (job.Job == ClusterJobs.Thinking)
                {
                    var order = ThinkingHosts(host).ToList();
                    if (hosted is not null) order.Add(hosted);
                    order.Add("each companion PC's own model, when Ollama there has one");
                    away.Add($"If {who} doesn't answer: Thinking uses {Chain(order)}, until {who} answers again.{never}");
                    continue;
                }
                var next = job.Pool.Where(p => p != host).Select(name).ToList();
                away.Add(next.Count > 0
                    ? $"If {who} doesn't answer: {Title(job.Job)} goes to {Chain(next)} (its list)."
                    : job.Job == ClusterJobs.Listening
                        ? $"If {who} doesn't answer: Listening uses Parakeet on the companion PC until {who} answers again."
                        : $"If {who} doesn't answer: {Title(job.Job)} waits for it; replies still show as text.");
            }
        }
        if (away.Count == 0) away.Add("No host does a live job, so a host that goes away changes nothing live.");

        return
        [
            new(Situation.Normal, LiveSituations.Title(Situation.Normal), normal),
            new(Situation.Gaming, LiveSituations.Title(Situation.Gaming), gaming),
            new(Situation.HostAway, LiveSituations.Title(Situation.HostAway), away)
        ];
    }

    /// <summary>The job uses <paramref name="pc"/>'s graphics card: its own host service does it on a card, or each companion PC
    /// does it itself with an option that uses a card.</summary>
    private static bool OnCard(JobPlan job, NetworkMachine pc, FootprintCatalog catalog)
    {
        if (job.Off || pc.Specs.Gpus.Count == 0) return false;
        var option = job.OptionId is { } id ? catalog.Find(id) : null;
        if (job.HostId == pc.Specs.Id) return option is null || option.UsesGpu;
        return job.HostId is null && option is { UsesGpu: true } && (option.ServedOn is null || option.ServedOn == pc.Specs.Id);
    }

    private static string Title(string job) => job switch
    {
        ClusterJobs.Thinking => "Thinking",
        ClusterJobs.Speaking => "Speaking",
        ClusterJobs.Listening => "Listening",
        _ => job
    };

    // "a", "a, then b", "a, then b, then c".
    private static string Chain(IReadOnlyList<string> items) => string.Join(", then ", items);
}
