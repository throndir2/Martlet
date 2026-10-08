using Martlet.Core.Cluster;
using Martlet.Core.Nodes;

namespace Martlet.Core.Planning;

// Applying a NetworkRecommendation to every computer: what the owner must see or accept first (the preflight) and the run
// that makes the changes in order, reporting each computer's progress in a SetupRun. The side effects (host commands, the
// cluster plan, Sharing work, publishing the run) go through ISetupTargets: the desktop's real paths, or fakes in tests and
// the MCP self-test.

/// <summary>A choice that applies only to one variant of a role: choice <see cref="Variable"/> is <see cref="Value"/>.</summary>
public sealed record SetupRoleCondition(string Variable, string Value);

/// <summary>A role's choice (role.choice / role.choice_when): its variable, options and default.</summary>
public sealed record SetupRoleChoice(string Variable, IReadOnlyList<string> Options, string Default, SetupRoleCondition? When = null);

/// <summary>A secret a role asks for (role.secret), whether the host already stores it, and the variant it belongs to.</summary>
public sealed record SetupRoleSecret(string Name, string Prompt, bool Stored, SetupRoleCondition? When = null);

/// <summary>Terms that apply only to one variant of a role (role.terms_when).</summary>
public sealed record SetupRoleTerms(SetupRoleCondition When, string Text);

/// <summary>One NVIDIA card of a host with several (role.gpu): its UUID, name and memory.</summary>
public sealed record SetupRoleCard(string Id, string Name, int MemoryMb);

/// <summary>What a role needs on one computer, as its host service's martlet-host describe says: its terms, choices,
/// secrets and graphics cards, and what it runs with now when installed.</summary>
public sealed record SetupRoleNeeds(string Title, string Terms)
{
    public bool Installed { get; init; }
    public IReadOnlyList<SetupRoleChoice> Choices { get; init; } = [];
    public IReadOnlyList<SetupRoleSecret> Secrets { get; init; } = [];
    public IReadOnlyList<SetupRoleTerms> TermsWhen { get; init; } = [];
    public IReadOnlyList<SetupRoleCard> Gpus { get; init; } = [];
    /// <summary>It runs on the graphics card or the processor by choice (choice.accelerator), for <see cref="GpuWhen"/>'s
    /// variant only when that is set.</summary>
    public bool GpuOrCpu { get; init; }
    public SetupRoleCondition? GpuWhen { get; init; }
    /// <summary>What the installed role runs with now, by choice variable.</summary>
    public IReadOnlyDictionary<string, string> Current { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
    /// <summary>The installed roles that adding it stops first (another voice engine: one runs on a computer).</summary>
    public IReadOnlyList<string> Stops { get; init; } = [];
}

/// <summary>What the review shows for one change. Ready: it can be made. NeedsOwner: it needs a secret the owner enters in
/// the review (<see cref="SetupRunPreflight.WithSecret"/>). NeedsSomeoneThere: someone at that computer makes it. CannotApply:
/// Martlet can't make it, <see cref="SetupPreflightItem.Text"/> says why. Automatic: nothing to do, it happens by itself (a
/// computer joins or leaves the Thinking pool on its next check by the roles it runs).</summary>
public enum SetupStepVerdict { Ready, NeedsOwner, NeedsSomeoneThere, CannotApply, Automatic }

/// <summary>A secret one change needs (an NGC API key, say), with the text the host asks it with. The value is never part of it.</summary>
public sealed record SetupSecretNeed(int Index, string MachineId, string RoleKind, string Name, string Prompt);

/// <summary>One change as the review shows it before Reconfigure: whether it can be made and, for a role install, the terms
/// the owner accepts with Reconfigure, the secrets it needs and the non-secret <c>choice.VAR</c> arguments it is installed with.</summary>
public sealed record SetupPreflightItem(int Index, SetupChange Change, SetupStepVerdict Verdict, string Text)
{
    /// <summary>The role's terms and licenses, as its host describes them (its own and its variant's), or null.</summary>
    public string? Terms { get; init; }
    public IReadOnlyList<SetupSecretNeed> Secrets { get; init; } = [];
    /// <summary>The <c>choice.VAR</c> arguments the role is installed or changed with (<c>choice.gpu</c> is the card's UUID).</summary>
    public IReadOnlyDictionary<string, string> Arguments { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
    public double? DownloadGb => Change.DownloadGb;
    public bool Applies => Verdict is SetupStepVerdict.Ready or SetupStepVerdict.NeedsOwner;
}

/// <summary>Everything the owner must see or accept before Reconfigure: one item per change, in the recommendation's order.
/// Secrets the owner enters in the review are kept only in memory, in this object (<see cref="WithSecret"/>); they are never
/// written, logged or shown.</summary>
public sealed record SetupRunPreflight(string Fingerprint, IReadOnlyList<SetupPreflightItem> Items)
{
    private IReadOnlyDictionary<string, string> answers = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The changes that install a role under terms the owner accepts with Reconfigure.</summary>
    public IReadOnlyList<SetupPreflightItem> WithTerms => [.. Items.Where(i => i.Applies && i.Terms is { Length: > 0 })];

    public IReadOnlyList<SetupSecretNeed> Secrets => [.. Items.Where(i => i.Applies).SelectMany(i => i.Secrets)];

    public IReadOnlyList<SetupSecretNeed> Unanswered => [.. Secrets.Where(s => !Answered(s))];

    /// <summary>About how much the changes that can be made download, in GB.</summary>
    public double DownloadGb => Items.Where(i => i.Applies).Sum(i => i.DownloadGb ?? 0);

    public IReadOnlyList<SetupPreflightItem> NeedSomeoneThere => [.. Items.Where(i => i.Verdict == SetupStepVerdict.NeedsSomeoneThere)];

    public IReadOnlyList<SetupPreflightItem> CannotApply => [.. Items.Where(i => i.Verdict == SetupStepVerdict.CannotApply)];

    /// <summary>Reconfigure would change something.</summary>
    public bool CanApply => Items.Any(i => i.Applies);

    /// <summary>This preflight with the owner's <paramref name="value"/> for <paramref name="need"/>.</summary>
    public SetupRunPreflight WithSecret(SetupSecretNeed need, string value)
    {
        ArgumentNullException.ThrowIfNull(need);
        if (string.IsNullOrEmpty(value) || value.Length > NodeCommandRules.MaximumSecretCharacters || value.Any(char.IsControl))
            throw new ArgumentException("A secret is one line of at most 4096 characters.", nameof(value));
        if (!Secrets.Contains(need)) throw new ArgumentException("This preflight doesn't ask for that secret.", nameof(need));
        var copy = this with { };
        copy.answers = new Dictionary<string, string>(answers, StringComparer.Ordinal) { [AnswerKey(need)] = value };
        return copy;
    }

    public bool Answered(SetupSecretNeed need) => answers.ContainsKey(AnswerKey(need));

    /// <summary>The <c>secret.name</c> values the owner entered for change <paramref name="index"/>.</summary>
    internal IReadOnlyDictionary<string, string> SecretsFor(int index) =>
        Items.Where(i => i.Index == index).SelectMany(i => i.Secrets).Where(Answered)
            .ToDictionary(s => "secret." + s.Name, s => answers[AnswerKey(s)], StringComparer.Ordinal);

    private static string AnswerKey(SetupSecretNeed need) => $"{need.Index}/{need.MachineId}/{need.RoleKind}/{need.Name}";

    public override string ToString() => $"Setup preflight ({Items.Count} changes, {answers.Count} secrets entered, redacted)";
}

/// <summary>How one change ended: <see cref="SetupMachineState.Done"/>, <see cref="SetupMachineState.Failed"/> or
/// <see cref="SetupMachineState.NeedsAttention"/> (skipped: it needs the owner or someone at that computer), with why.</summary>
public sealed record SetupStepOutcome(int Index, SetupChange Change, SetupMachineState State, string Text);

/// <summary>How a whole run ended: its record (one entry per computer) and each change's outcome.</summary>
public sealed record SetupRunOutcome(SetupRun Run, IReadOnlyList<SetupStepOutcome> Steps)
{
    public bool Succeeded => Steps.All(s => s.State == SetupMachineState.Done);

    public string Summary
    {
        get
        {
            var done = Steps.Count(s => s.State == SetupMachineState.Done);
            var failed = Steps.Count(s => s.State == SetupMachineState.Failed);
            var attention = Steps.Count(s => s.State == SetupMachineState.NeedsAttention);
            return Steps.Count == 0 ? "Nothing to change: your computers already use the recommended setup."
                : $"Reconfigured your computers: {done} of {Steps.Count} changes made" +
                  (failed > 0 ? $", {failed} failed" : "") + (attention > 0 ? $", {attention} {(attention == 1 ? "needs" : "need")} you" : "") + ".";
        }
    }
}

/// <summary>How Martlet reaches one computer's host service: it can change it from here, or why not (and whether someone at
/// that computer can).</summary>
public sealed record SetupReach(bool Can, string? Why = null, bool SomeoneThere = false)
{
    public static SetupReach Yes { get; } = new(true);
}

/// <summary>Add (or change: the same add with other choices) or remove one host role on one computer. Arguments are the
/// non-secret <c>choice.VAR</c> answers; Secrets the <c>secret.name</c> ones, which never leave memory except to that host.</summary>
public sealed record SetupRoleCommand(string MachineId, string RoleKind, bool Add, IReadOnlyDictionary<string, string> Arguments,
    IReadOnlyDictionary<string, string> Secrets)
{
    public override string ToString()
    {
        var parts = Arguments.Select(a => $"{a.Key}={a.Value}").ToList();
        if (Secrets.Count > 0) parts.Add($"{Secrets.Count} {(Secrets.Count == 1 ? "secret" : "secrets")} redacted");
        return $"{(Add ? "Add" : "Remove")} {RoleKind} on {MachineId}" + (parts.Count > 0 ? $" ({string.Join(", ", parts)})" : "");
    }
}

/// <summary>How one step went, in words.</summary>
public sealed record SetupStepResult(SetupMachineState State, string Text)
{
    public static SetupStepResult Done(string text) => new(SetupMachineState.Done, text);
    public static SetupStepResult Failed(string text) => new(SetupMachineState.Failed, text);
    public static SetupStepResult Attention(string text) => new(SetupMachineState.NeedsAttention, text);
}

/// <summary>The computers and choices a run changes, through Martlet's existing paths: host commands (through Martlet on that
/// computer, this PC's own host service, or SSH), the shared cluster plan (who does each job, failover on), Devices › Sharing
/// work, the cluster check and the published run. The desktop implements it; tests and the MCP self-test use fakes.</summary>
public interface ISetupTargets
{
    /// <summary>The Martlet device ID of the computer that runs the change (it publishes the run).</summary>
    string Device { get; }

    /// <summary>Whether Martlet can change <paramref name="machineId"/>'s host service from here.</summary>
    SetupReach Reach(string machineId);

    /// <summary>What Martlet knows of <paramref name="machineId"/>'s hardware (to find the graphics card a change names).</summary>
    MachineSpecs? Specs(string machineId);

    /// <summary>A host role in words ("Chatterbox Turbo").</summary>
    string RoleName(string kind);

    /// <summary>What <paramref name="roleKind"/> needs on <paramref name="machineId"/> (martlet-host describe there).</summary>
    Task<SetupRoleNeeds> DescribeAsync(string machineId, string roleKind, CancellationToken cancel);

    /// <summary>Installs, changes or removes a role and waits until it is done there.</summary>
    Task<SetupStepResult> ChangeRoleAsync(SetupRoleCommand command, IProgress<string> progress, CancellationToken cancel);

    /// <summary>Records in the shared plan who does <paramref name="job"/> (a ClusterJobs name) with failover on; null:
    /// each companion PC's own choice; <paramref name="off"/>: lip-sync by nobody.</summary>
    Task<SetupStepResult> AssignJobAsync(string job, string? hostId, bool off, CancellationToken cancel);

    /// <summary>Lets <paramref name="machineId"/> take <paramref name="job"/>'s requests when the computer doing it is busy
    /// (Devices › Sharing work), or stops it.</summary>
    Task<SetupStepResult> ShareAsync(string job, string machineId, bool join, CancellationToken cancel);

    /// <summary>Checks every host once (Check hosts), so this PC and every companion PC follow the plan within one check.
    /// Returns, by job, why this PC can't follow it yet.</summary>
    Task<IReadOnlyDictionary<string, string>> CheckAsync(CancellationToken cancel);

    /// <summary>Publishes the run to every computer (this computer's setup-run entry in the shared settings).</summary>
    Task PublishAsync(SetupRun run, CancellationToken cancel);

    /// <summary>The owner accepted <paramref name="item"/>'s terms with Reconfigure: record it as the install dialogs do.</summary>
    void Accepted(SetupPreflightItem item);
}

/// <summary>Turns a recommendation into the review's preflight and applies it, one change at a time in the recommendation's
/// order (make before break), continuing past a failed step and reporting each computer.</summary>
public static class SetupExecutor
{
    /// <summary>How often a long step (a large download) refreshes the published run, so other computers see it is alive.</summary>
    public static readonly TimeSpan Heartbeat = TimeSpan.FromMinutes(5);

    private static bool RoleChange(SetupChangeKind kind) =>
        kind is SetupChangeKind.AddRole or SetupChangeKind.RemoveRole or SetupChangeKind.ChangeModel or SetupChangeKind.MoveToGpu;

    /// <summary>A Thinking pool change (Job null, or the deep-thinking work): it happens by itself from the roles a computer runs.</summary>
    private static bool ThinkingPool(SetupChange change) =>
        change.Kind is SetupChangeKind.JoinPool or SetupChangeKind.LeavePool && change.Job is null or WorkSharingJobs.DeepThinking;

    public static async Task<SetupRunPreflight> PrepareAsync(NetworkRecommendation recommendation, ISetupTargets targets, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(recommendation);
        ArgumentNullException.ThrowIfNull(targets);
        var described = new Dictionary<(string, string), Task<SetupRoleNeeds>>();
        var items = new List<SetupPreflightItem>();
        for (var index = 0; index < recommendation.Changes.Count; index++)
        {
            cancel.ThrowIfCancellationRequested();
            items.Add(await PrepareOneAsync(index, recommendation.Changes[index], targets, described, cancel));
        }
        return new(recommendation.Fingerprint, items);
    }

    private static async Task<SetupPreflightItem> PrepareOneAsync(int index, SetupChange change, ISetupTargets targets,
        Dictionary<(string, string), Task<SetupRoleNeeds>> described, CancellationToken cancel)
    {
        SetupPreflightItem Item(SetupStepVerdict verdict, string text) => new(index, change, verdict, text);
        var machine = change.MachineId;
        if (ThinkingPool(change))
            return Item(SetupStepVerdict.Automatic, change.Kind == SetupChangeKind.JoinPool
                ? $"{machine} joins the Thinking pool by itself on its next check, once it runs the Thinking pool role."
                : $"{machine} leaves the Thinking pool by itself once it no longer runs the Thinking pool role.");
        if (change.Kind is SetupChangeKind.JoinPool or SetupChangeKind.LeavePool)
            return change.Job is WorkSharingJobs.Thinking or WorkSharingJobs.Listening or WorkSharingJobs.Speaking
                ? Item(SetupStepVerdict.Ready, change.Kind == SetupChangeKind.JoinPool
                    ? $"Devices › Sharing work: {machine} takes {Job(change.Job)} when the computer doing it is busy."
                    : $"Devices › Sharing work: {machine} no longer takes {Job(change.Job)}.")
                : Item(SetupStepVerdict.CannotApply, $"Martlet doesn't share \"{change.Job}\" between computers.");
        if (change.Kind == SetupChangeKind.AssignJob)
            return change.Job is { } job && ClusterJobs.All.Contains(job)
                ? Item(SetupStepVerdict.Ready, change.Summary)
                : Item(SetupStepVerdict.CannotApply, $"\"{change.Job}\" isn't a job your computers share.");
        if (!RoleChange(change.Kind) || !NodeCommandRules.IsRole(change.RoleKind))
            return Item(SetupStepVerdict.CannotApply, "This change names no host role Martlet knows.");
        if (change.NeedsSomeoneThere)
            return Item(SetupStepVerdict.NeedsSomeoneThere, $"Someone at {machine} makes this change there: Martlet can't change its host service from here.");
        var reach = targets.Reach(machine);
        if (!reach.Can)
            return Item(reach.SomeoneThere ? SetupStepVerdict.NeedsSomeoneThere : SetupStepVerdict.CannotApply,
                reach.Why ?? $"Martlet can't change {machine}'s host service from here.");
        var role = change.RoleKind!;
        if (change.Kind == SetupChangeKind.RemoveRole) return Item(SetupStepVerdict.Ready, change.Summary);
        SetupRoleNeeds needs;
        try
        {
            if (!described.TryGetValue((machine, role), out var reading))
                described[(machine, role)] = reading = targets.DescribeAsync(machine, role, cancel);
            needs = await reading;
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            return Item(SetupStepVerdict.CannotApply, $"Couldn't read what {targets.RoleName(role)} needs on {machine}: {error.Message}");
        }
        var (arguments, problem) = Arguments(change, needs, targets.Specs(machine));
        if (problem is not null) return Item(SetupStepVerdict.CannotApply, problem);
        var secrets = needs.Secrets.Where(s => !s.Stored && Holds(s.When, arguments))
            .Select(s => new SetupSecretNeed(index, machine, role, s.Name, s.Prompt)).ToArray();
        var terms = string.Join("\n\n", new[] { needs.Terms }.Concat(needs.TermsWhen.Where(t => Holds(t.When, arguments)).Select(t => t.Text))
            .Where(t => t.Length > 0));
        var text = change.Summary + (needs.Stops.Count > 0
            ? $" Only one voice engine runs on a computer, so {string.Join(" and ", needs.Stops.Select(targets.RoleName))} stops there first; its download is kept."
            : "") + (secrets.Length > 0 ? " It needs " + string.Join(" and ", secrets.Select(s => s.Prompt)) + "." : "");
        return Item(secrets.Length > 0 ? SetupStepVerdict.NeedsOwner : SetupStepVerdict.Ready, text) with
        {
            Terms = terms.Length > 0 ? terms : null, Secrets = secrets, Arguments = arguments
        };
    }

    /// <summary>Whether a variant condition holds for the chosen arguments (no condition always holds).</summary>
    private static bool Holds(SetupRoleCondition? when, IReadOnlyDictionary<string, string> arguments) =>
        when is null || arguments.GetValueOrDefault("choice." + when.Variable) == when.Value;

    /// <summary>The <c>choice.VAR</c> arguments that make <paramref name="change"/> on a role that needs <paramref name="needs"/>:
    /// what an installed role runs with now, the model's choice (and the variant it belongs to) and the card's UUID; or why
    /// the host can't make it.</summary>
    public static (IReadOnlyDictionary<string, string> Arguments, string? Problem) Arguments(SetupChange change, SetupRoleNeeds needs,
        MachineSpecs? specs)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(needs);
        var arguments = new Dictionary<string, string>(StringComparer.Ordinal);
        var role = change.RoleKind ?? "";
        // A change keeps everything the role runs with now except what it changes.
        if (needs.Installed && change.Kind is SetupChangeKind.ChangeModel or SetupChangeKind.MoveToGpu or SetupChangeKind.AddRole)
            foreach (var (variable, value) in needs.Current) arguments["choice." + variable] = value;
        if (change.Kind == SetupChangeKind.ChangeModel && string.IsNullOrEmpty(change.Model))
            return (arguments, $"The change names no model for {role} on {change.MachineId}.");
        if (change.Kind is SetupChangeKind.AddRole or SetupChangeKind.ChangeModel && change.Model is { Length: > 0 } model)
        {
            var choice = ModelChoice(needs, model, arguments);
            if (choice is null && needs.Choices.Count > 0)
                return (arguments, $"{change.MachineId}'s {role} role doesn't offer {model}. Update Martlet there, then check again.");
            if (choice is not null)
            {
                arguments["choice." + choice.Variable] = choice.Options.First(o => string.Equals(o, model, StringComparison.OrdinalIgnoreCase));
                if (choice.When is { } when) arguments["choice." + when.Variable] = when.Value;
            }
        }
        if (change.Kind == SetupChangeKind.MoveToGpu && change.GpuIndex is null)
            return (arguments, $"The change names no graphics card for {role} on {change.MachineId}.");
        if (change.Kind is SetupChangeKind.AddRole or SetupChangeKind.MoveToGpu or SetupChangeKind.ChangeModel && change.GpuIndex is { } index)
        {
            var (card, problem) = Card(specs, index, needs.Gpus);
            if (problem is not null) return (arguments, $"{change.MachineId}: {problem}");
            if (card is not null) arguments["choice.gpu"] = card.Id;
            if (needs.GpuOrCpu && Holds(needs.GpuWhen, arguments)) arguments["choice.accelerator"] = "gpu";
        }
        return (arguments, null);
    }

    /// <summary>The choice whose options hold <paramref name="model"/>: one of the variant already chosen first, then any.</summary>
    private static SetupRoleChoice? ModelChoice(SetupRoleNeeds needs, string model, IReadOnlyDictionary<string, string> arguments)
    {
        var offering = needs.Choices.Where(c => c.Options.Contains(model, StringComparer.OrdinalIgnoreCase)).ToArray();
        return offering.FirstOrDefault(c => c.When is not null && Holds(c.When, arguments)) ?? offering.FirstOrDefault(c => c.When is null) ??
            offering.FirstOrDefault();
    }

    /// <summary>The host's card for <paramref name="index"/> (an index into <paramref name="specs"/>' graphics cards): by its
    /// name (the nth of several alike), else by its place among the NVIDIA cards. Null without a problem when the host has one
    /// NVIDIA card or none (<paramref name="cards"/> empty): there is nothing to choose.</summary>
    public static (SetupRoleCard? Card, string? Problem) Card(MachineSpecs? specs, int index, IReadOnlyList<SetupRoleCard> cards)
    {
        if (cards.Count == 0) return (null, null);
        var gpus = specs?.Gpus;
        if (gpus is null || gpus.Count == 0)
            return index >= 0 && index < cards.Count ? (cards[index], null) : (null, $"it has no graphics card {index + 1}.");
        if (index < 0 || index >= gpus.Count) return (null, $"it has no graphics card {index + 1}.");
        var gpu = gpus[index];
        if (!gpu.IsNvidia) return (null, $"{gpu.Name} isn't an NVIDIA card, so a host role can't be pinned to it.");
        static bool Same(string a, string b) => a.Contains(b, StringComparison.OrdinalIgnoreCase) || b.Contains(a, StringComparison.OrdinalIgnoreCase);
        var alike = cards.Where(c => Same(c.Name, gpu.Name)).ToArray();
        if (alike.Length > 0) return (alike[Math.Min(gpus.Take(index).Count(g => Same(g.Name, gpu.Name)), alike.Length - 1)], null);
        var ordinal = gpus.Take(index).Count(g => g.IsNvidia);
        return ordinal < cards.Count ? (cards[ordinal], null) : (null, $"its host service doesn't see {gpu.Name}.");
    }

    /// <summary>A change as a step in words, without the count ("Installing Chatterbox Turbo").</summary>
    public static string Doing(SetupChange change, ISetupTargets targets)
    {
        var role = change.RoleKind is { } kind ? targets.RoleName(kind) : "a role";
        var gpu = change.GpuIndex is { } index && targets.Specs(change.MachineId)?.Gpus is { } gpus && index >= 0 && index < gpus.Count
            ? gpus[index].Name : null;
        return change.Kind switch
        {
            SetupChangeKind.AddRole => $"Installing {role}" + (gpu is null ? "" : $" on its {gpu}"),
            SetupChangeKind.RemoveRole => $"Removing {role}",
            SetupChangeKind.ChangeModel => $"Switching {role} to {change.Model}",
            SetupChangeKind.MoveToGpu => $"Moving {role} to " + (gpu is null ? "another graphics card" : $"its {gpu}"),
            SetupChangeKind.AssignJob when change.MachineId.Length == 0 => $"Handing {Job(change.Job)} back to each companion PC's own choice",
            SetupChangeKind.AssignJob => $"Handing {Job(change.Job)} to {change.MachineId}",
            SetupChangeKind.JoinPool when ThinkingPool(change) => "Joining the Thinking pool",
            SetupChangeKind.LeavePool when ThinkingPool(change) => "Leaving the Thinking pool",
            SetupChangeKind.JoinPool => $"Sharing {Job(change.Job)}",
            _ => $"Stopping {Job(change.Job)} sharing"
        };
    }

    private static string Job(string? job) => job is null ? "the Thinking pool" : WorkSharingJobs.Title(job).ToLowerInvariant();

    /// <summary>Applies <paramref name="recommendation"/>'s changes in order through <paramref name="targets"/>. Only changes the
    /// owner saw in <paramref name="preflight"/> (the review) are made; one without its review item, or one the review said
    /// Martlet can't make, is skipped and reported. A failed step doesn't stop the others. The run is published at the start,
    /// at every step and at the end (and refreshed during long steps), and reported to <paramref name="progress"/>.</summary>
    public static async Task<SetupRunOutcome> ApplyAsync(NetworkRecommendation recommendation, SetupRunPreflight preflight, ISetupTargets targets,
        IProgress<SetupRun>? progress, CancellationToken cancel, TimeProvider? clock = null, string? runId = null)
    {
        ArgumentNullException.ThrowIfNull(recommendation);
        ArgumentNullException.ThrowIfNull(preflight);
        ArgumentNullException.ThrowIfNull(targets);
        clock ??= TimeProvider.System;
        var changes = recommendation.Changes;
        var totals = changes.GroupBy(c => RunMachine(c, targets.Device), StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var position = new Dictionary<string, int>(StringComparer.Ordinal);
        var done = new Dictionary<string, int>(StringComparer.Ordinal);
        var worst = new Dictionary<string, SetupStepOutcome>(StringComparer.Ordinal);
        var outcomes = new List<SetupStepOutcome>();
        var run = SetupRun.Start(runId ?? "run-" + Guid.NewGuid().ToString("N")[..16], targets.Device, clock.GetUtcNow(),
            totals.Select(t => (t.Key, t.Value)));

        async Task Publish(SetupRun next)
        {
            run = next;
            progress?.Report(next);
            try { await targets.PublishAsync(next, CancellationToken.None); }
            catch (Exception error) when (error is not OperationCanceledException) { }
        }

        await Publish(run);
        for (var index = 0; index < changes.Count; index++)
        {
            var change = changes[index];
            var machine = RunMachine(change, targets.Device);
            var step = position[machine] = position.GetValueOrDefault(machine) + 1;
            var total = totals[machine];
            SetupStepResult result;
            if (cancel.IsCancellationRequested) result = SetupStepResult.Attention("Stopped before it ran.");
            else
            {
                await Publish(run.With(machine, SetupMachineState.Configuring, $"{Doing(change, targets)} ({step} of {total})",
                    done.GetValueOrDefault(machine), clock.GetUtcNow()));
                result = await WithHeartbeatAsync(() => ApplyOneAsync(index, change, recommendation, preflight, targets, cancel),
                    () => Publish(run with { UpdatedAt = clock.GetUtcNow() }), clock);
            }
            var outcome = new SetupStepOutcome(index, change, result.State, result.Text);
            outcomes.Add(outcome);
            if (result.State == SetupMachineState.Done) done[machine] = done.GetValueOrDefault(machine) + 1;
            else if (!worst.TryGetValue(machine, out var bad) || bad.State != SetupMachineState.Failed) worst[machine] = outcome;
            await Publish(Settle(run, machine, step, total, done.GetValueOrDefault(machine), worst.GetValueOrDefault(machine), clock.GetUtcNow()));
        }

        // Every companion PC follows the new plan within one check; this PC says now why it can't follow a job yet.
        if (!cancel.IsCancellationRequested && outcomes.Count > 0)
        {
            IReadOnlyDictionary<string, string> problems;
            try { problems = await targets.CheckAsync(cancel); }
            catch (Exception error) when (error is not OperationCanceledException || cancel.IsCancellationRequested) { problems = new Dictionary<string, string>(); }
            for (var i = 0; i < outcomes.Count; i++)
            {
                var outcome = outcomes[i];
                if (outcome is not { State: SetupMachineState.Done, Change: { Kind: SetupChangeKind.AssignJob, Job: { } job } } ||
                    problems.GetValueOrDefault(job) is not { } why) continue;
                var machine = RunMachine(outcome.Change, targets.Device);
                outcomes[i] = outcome with
                {
                    State = SetupMachineState.NeedsAttention,
                    Text = $"{outcome.Text} This PC follows it as soon as it can: {why}"
                };
                done[machine] = done.GetValueOrDefault(machine) - 1;
                if (!worst.TryGetValue(machine, out var bad) || bad.State != SetupMachineState.Failed) worst[machine] = outcomes[i];
                run = Settle(run, machine, totals[machine], totals[machine], done[machine], worst[machine], clock.GetUtcNow());
            }
        }
        await Publish(run.Finish(clock.GetUtcNow()));
        return new(run, outcomes);
    }

    /// <summary>The computer a step shows on in the run: its own, or for a job no host does next, the host it leaves (else the
    /// computer that runs the change).</summary>
    public static string RunMachine(SetupChange change, string device) =>
        change.MachineId.Length > 0 ? change.MachineId : change.FromMachineId is { Length: > 0 } from ? from : device;

    /// <summary>A computer after one of its steps: still waiting for its next step, or finished (Done when every step was,
    /// else how its worst step ended).</summary>
    private static SetupRun Settle(SetupRun run, string machine, int step, int total, int done, SetupStepOutcome? worst, DateTimeOffset now) =>
        step < total
            ? run.With(machine, SetupMachineState.Pending, $"{done} of {total} done; waiting for its next step", done, now)
            : worst is null
                ? run.With(machine, SetupMachineState.Done, $"All {total} {(total == 1 ? "change" : "changes")} made", done, now)
                : run.With(machine, worst.State, worst.Text, done, now);

    private static async Task<SetupStepResult> WithHeartbeatAsync(Func<Task<SetupStepResult>> work, Func<Task> beat, TimeProvider clock)
    {
        var task = work();
        using var stop = new CancellationTokenSource();
        try
        {
            while (!task.IsCompleted)
            {
                var tick = Task.Delay(Heartbeat, clock, stop.Token);
                if (await Task.WhenAny(task, tick) == tick && !task.IsCompleted) await beat();
            }
        }
        finally { stop.Cancel(); }
        return await task;
    }

    private static async Task<SetupStepResult> ApplyOneAsync(int index, SetupChange change, NetworkRecommendation recommendation,
        SetupRunPreflight preflight, ISetupTargets targets, CancellationToken cancel)
    {
        var item = preflight.Items.FirstOrDefault(i => i.Index == index);
        if (item is null || item.Change != change)
            return SetupStepResult.Attention("This change wasn't in the review, so Martlet didn't make it. Check the recommended setup again.");
        switch (item.Verdict)
        {
            case SetupStepVerdict.Automatic: return SetupStepResult.Done(item.Text);
            case SetupStepVerdict.NeedsSomeoneThere or SetupStepVerdict.CannotApply: return SetupStepResult.Attention(item.Text);
            case SetupStepVerdict.NeedsOwner when item.Secrets.Any(s => !preflight.Answered(s)):
                return SetupStepResult.Attention($"It needs {string.Join(" and ", item.Secrets.Where(s => !preflight.Answered(s)).Select(s => s.Prompt))}. " +
                    $"Enter it in the review and Reconfigure again, or install {targets.RoleName(change.RoleKind!)} on {change.MachineId} from the Devices map.");
        }
        try
        {
            switch (change.Kind)
            {
                case SetupChangeKind.AddRole or SetupChangeKind.ChangeModel or SetupChangeKind.MoveToGpu or SetupChangeKind.RemoveRole:
                    var add = change.Kind != SetupChangeKind.RemoveRole;
                    if (add && item.Terms is not null) targets.Accepted(item);
                    return await targets.ChangeRoleAsync(new(change.MachineId, change.RoleKind!, add, add ? item.Arguments : new Dictionary<string, string>(),
                        add ? preflight.SecretsFor(index) : new Dictionary<string, string>()), new Progress<string>(), cancel);
                case SetupChangeKind.AssignJob:
                    var plan = recommendation.Target.Job(change.Job!);
                    var host = plan is null ? change.MachineId.Length > 0 ? change.MachineId : null : plan.HostId;
                    return await targets.AssignJobAsync(change.Job!, host, plan?.Off ?? false, cancel);
                default:
                    return await targets.ShareAsync(change.Job!, change.MachineId, change.Kind == SetupChangeKind.JoinPool, cancel);
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            return SetupStepResult.Attention("Stopped before it finished.");
        }
        catch (Exception error)
        {
            return SetupStepResult.Failed(error.Message);
        }
    }
}
