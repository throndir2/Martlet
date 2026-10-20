using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Core.Nodes;

namespace Martlet.NodeLinkCheck;

/// <summary>Stands in for Martlet's command runner: prints fixed lines, records the secrets and choices it was handed and
/// waits to be canceled for describe-role (slow-role). Adding a role named hold-* waits until <see cref="Release"/> lets
/// that role finish. It counts the commands it runs at once and records when each starts and ends. An update to
/// <see cref="BusyVersion"/> continues later (as Martlet's does while someone uses Martlet on the host) until
/// <see cref="UpdateMayInstall"/>.</summary>
sealed class FixtureRunner : INodeCommandRunner
{
    internal static readonly string[] UpdateLines = ["Checking the fixture release...", "Updating the fixture host service...", "Fixture host updated."];
    internal const string BusyVersion = "9.9.8";
    internal const string BusyLine = "FIXTURE: Martlet is in use here, so the update waits until it is idle.";
    private readonly object gate = new();
    private readonly Dictionary<string, TaskCompletionSource> holds = [];
    private readonly List<string> events = [];
    private readonly Dictionary<string, int> activeWhenStarted = [];
    private int active;
    internal List<string> SecretsSeen { get; } = [];
    internal string? LastChoice { get; private set; }
    /// <summary>The martlet-host command the last host.exposure command would run (FIXTURE: runs nothing).</summary>
    internal string? LastExposure { get; private set; }
    internal bool Canceled { get; private set; }
    internal bool UpdateMayInstall { get; set; }
    public IReadOnlyList<string> Kinds => NodeCommandKinds.All;
    /// <summary>How many commands run right now.</summary>
    internal int Active { get { lock (gate) return active; } }
    /// <summary>"start ID" and "end ID", in order.</summary>
    internal List<string> Events { get { lock (gate) return [.. events]; } }
    /// <summary>How many other commands ran when each command started.</summary>
    internal Dictionary<string, int> ActiveWhenStarted { get { lock (gate) return new(activeWhenStarted); } }

    internal void Release(string role) => Hold(role).TrySetResult();

    private TaskCompletionSource Hold(string role)
    {
        lock (gate)
        {
            if (!holds.TryGetValue(role, out var hold)) holds[role] = hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return hold;
        }
    }

    public async Task<NodeCommandOutcome?> RunAsync(NodeCommand command, IReadOnlyDictionary<string, string> secrets, bool resumed,
        IProgress<string> output, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            activeWhenStarted[command.Id] = active;
            active++;
            events.Add("start " + command.Id);
        }
        try { return await RunOneAsync(command, secrets, resumed, output, cancellationToken); }
        finally
        {
            lock (gate)
            {
                active--;
                events.Add("end " + command.Id);
            }
        }
    }

    private async Task<NodeCommandOutcome?> RunOneAsync(NodeCommand command, IReadOnlyDictionary<string, string> secrets, bool resumed,
        IProgress<string> output, CancellationToken cancellationToken)
    {
        switch (command.Kind)
        {
            case NodeCommandKinds.Update when command.Arguments.GetValueOrDefault("version") == BusyVersion:
                if (!UpdateMayInstall)
                {
                    if (!resumed) output.Report(BusyLine);
                    return null;
                }
                output.Report("FIXTURE: idle now; installing nothing.");
                return new(true, "FIXTURE - updated nothing.", 0);
            case NodeCommandKinds.Update:
                foreach (var line in UpdateLines) output.Report(line);
                return new(true, "FIXTURE - updated nothing.", 0);
            case NodeCommandKinds.AddRole:
                lock (gate)
                {
                    SecretsSeen.AddRange(secrets.Values);
                    LastChoice = command.Arguments.GetValueOrDefault("choice.MODEL");
                }
                output.Report("Installing the fixture role...");
                if (command.Arguments.GetValueOrDefault("role") is { } role && role.StartsWith("hold-", StringComparison.Ordinal))
                    await Hold(role).Task.WaitAsync(cancellationToken);
                return new(true, "FIXTURE - installed nothing.", 0);
            case NodeCommandKinds.Exposure:
                LastExposure = "exposure " + string.Join(' ', NodeCommandRules.ExposureOptions(command.Arguments));
                output.Report("FIXTURE: " + LastExposure);
                return new(true, "FIXTURE - changed nothing.", 0);
            case NodeCommandKinds.DescribeRole when command.Arguments.GetValueOrDefault("role") == "slow-role":
                output.Report("Waiting to be canceled...");
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                catch (OperationCanceledException) { Canceled = true; throw; }
                return new(false, "Not canceled.");
            default:
                return new(true, "FIXTURE - nothing to do.", 0);
        }
    }
}
