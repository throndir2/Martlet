using System.Windows;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Avatar.Hosting;
using Martlet.Core.Nodes;

namespace Martlet.Desktop;

/// <summary>Runs martlet-host actions on a paired host through Martlet on that computer: the command goes to the host's
/// gateway over this PC's pinned, signed pairing, Martlet there (the host's agent) runs it, and its output streams into a run
/// window here. Cancel withdraws a command still waiting, or asks Martlet there to stop it.</summary>
internal static class HostAgentRun
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1.5);
    /// <summary>How long the host may stay unreachable while a command runs (its gateway restarts during an update).</summary>
    private static readonly TimeSpan UnreachablePatience = TimeSpan.FromMinutes(15);
    /// <summary>How long sending keeps trying while the host's gateway doesn't answer (for example while another computer
    /// updates it, which restarts it).</summary>
    private static readonly TimeSpan SendPatience = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SendRetryDelay = TimeSpan.FromSeconds(5);
    /// <summary>How often a waiting command checks what it waits behind.</summary>
    private const int QueueReadEvery = 4;

    internal static Task<string?> RunAsync(Window owner, AvatarRemoteHost pairing, string version, HostAction action,
        IReadOnlyDictionary<string, string>? answers, IReadOnlyDictionary<string, (string Value, string Why)>? recommended,
        bool confirmed = false)
    {
        var name = pairing.HostId;
        var role = action.Role;
        if (action.Verb is HostVerb.Setup or HostVerb.Pair)
            throw new InvalidOperationException($"Set {name} up and pair it from Martlet on that computer (Use as a Martlet host).");
        var title = action.Verb switch
        {
            HostVerb.Add => $"{action.AddVerb} {role} on {name}",
            HostVerb.Remove => $"Remove {role} from {name}",
            HostVerb.Update => $"Update {name}",
            _ => $"{name}: status"
        };
        return HostRunWindow.RunAsync(owner, title, run => ClusterSync.WithConnectionAsync(pairing, async connection =>
        {
            run.Status($"Asking Martlet on {name}...");
            HostCommandList list;
            try { list = await WhileRestartingAsync(run, name, () => connection.ReadCommandsAsync(run.Token)); }
            catch (Audio2FaceHostException error) when (error.Code == "request.invalid")
            {
                throw new InvalidOperationException($"{name}'s host service is older than commands between computers. Open Martlet on {name} " +
                    "once: it brings its host service up to date by itself, and from then on this PC updates and manages it from here.");
            }
            run.Output.Report(AgentText(name, list.Agent));
            if (role is not null && action.Verb == HostVerb.Remove && !confirmed && !ConfirmationDialog.Confirm(run,
                    $"Stop {role} on {name} and remove it from its gateway? Its data volumes are kept, so adding it again is quick.", "Remove role"))
                throw new OperationCanceledException();

            var input = answers;
            if (role is not null && action.Verb == HostVerb.Add)
            {
                run.Status(input is null ? $"Reading what {role} needs on {name}..." : $"Checking {name}'s graphics cards for {role}...");
                var described = await RunCommandAsync(connection, name, NodeCommandKinds.DescribeRole,
                    new Dictionary<string, string> { ["role"] = role }, null, run, line => !line.StartsWith("role.", StringComparison.Ordinal));
                if (described.State != NodeCommandState.Succeeded)
                    throw new InvalidOperationException(described.Summary ?? $"Could not read the {role} role on {name}.");
                var inputs = HostRemote.ParseRole(described.Output.Where(line => line.StartsWith("role.", StringComparison.Ordinal)));
                input = (input is null
                    ? HostInputDialog.ForRole(run, name, role, inputs, recommended, agent: true)
                    : HostInputDialog.WithGpu(run, name, role, inputs, input, recommended)) ?? throw new OperationCanceledException();
            }

            (string Kind, Dictionary<string, string> Arguments, Dictionary<string, string>? Secrets) request = action.Verb switch
            {
                HostVerb.Update => (NodeCommandKinds.Update, new Dictionary<string, string> { ["version"] = version }, null),
                HostVerb.Add => (NodeCommandKinds.AddRole, Choices(role!, input), Secrets(input)),
                HostVerb.Remove => (NodeCommandKinds.RemoveRole, new Dictionary<string, string> { ["role"] = role! }, null),
                _ => (NodeCommandKinds.Status, new Dictionary<string, string>(), null)
            };
            run.Status(action.Verb switch
            {
                HostVerb.Add when action.Changing => $"Changing {role} on {name}. A new model can take a while to download; this PC follows it when it's ready.",
                HostVerb.Add => $"Installing {role} on {name}. Large downloads can take a while; this PC picks the role up when it runs.",
                HostVerb.Remove => $"Removing {role} from {name}...",
                HostVerb.Update => $"Updating {name} to Martlet {version}...",
                _ => $"Reading {name}'s status..."
            });
            var command = await RunCommandAsync(connection, name, request.Kind, request.Arguments, request.Secrets, run);
            if (command.State != NodeCommandState.Succeeded)
                throw new InvalidOperationException(command.Summary ?? $"Martlet on {name} could not finish it.");
            return command.Summary ?? $"Finished on {name}.";
        }), join: true);
    }

    /// <summary>Who runs this host's commands, in words.</summary>
    internal static string AgentText(string name, NodeAgentInfo? agent)
    {
        if (agent is null)
            return $"Martlet on {name} hasn't checked in since its host service started. Commands wait until Martlet runs there " +
                "(open it there, or let it start with Windows). A Linux computer without Martlet is reached over SSH instead.";
        var ago = DateTimeOffset.UtcNow - agent.SeenAt;
        var version = agent.Version is null ? "" : $" (Martlet {agent.Version})";
        return ago < TimeSpan.FromMinutes(1)
            ? $"Martlet on {name} is running{version} and takes commands from this PC."
            : $"Martlet on {name}{version} last checked in {Ago(ago)} ago. The command waits until it runs there again.";
    }

    private static string Ago(TimeSpan span) =>
        span < TimeSpan.FromHours(1) ? $"{Math.Max(1, (int)span.TotalMinutes)} min" : span < TimeSpan.FromDays(2) ? $"{(int)span.TotalHours} h" : $"{(int)span.TotalDays} days";

    private static Dictionary<string, string> Choices(string role, IReadOnlyDictionary<string, string>? input)
    {
        var arguments = new Dictionary<string, string>(StringComparer.Ordinal) { ["role"] = role };
        foreach (var (key, value) in input ?? new Dictionary<string, string>())
            if (key.StartsWith("choice.", StringComparison.Ordinal)) arguments[key] = value;
        return arguments;
    }

    private static Dictionary<string, string>? Secrets(IReadOnlyDictionary<string, string>? input)
    {
        var secrets = (input ?? new Dictionary<string, string>()).Where(pair => pair.Key.StartsWith("secret.", StringComparison.Ordinal) && pair.Value.Length > 0)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        return secrets.Count > 0 ? secrets : null;
    }

    /// <summary>Sends one command and follows it to its end, showing new output lines (those <paramref name="show"/> accepts)
    /// and its state, including what it waits behind. Canceling withdraws it (or asks Martlet there to stop it).</summary>
    private static async Task<Martlet.Core.Nodes.NodeCommand> RunCommandAsync(Audio2FaceHostConnection connection, string name, string kind,
        IReadOnlyDictionary<string, string> arguments, IReadOnlyDictionary<string, string>? secrets, HostRunWindow run,
        Func<string, bool>? show = null)
    {
        // Sending the same command again while it waits or runs returns that one, so a retry never sends it twice.
        var command = await WhileRestartingAsync(run, name, () => connection.SendCommandAsync(kind, arguments, secrets, run.Token));
        run.Output.Report($"Sent to {name}: {NodeCommandAgent.Describe(command)}.");
        var shown = 0;
        var polls = 0;
        string? waitingBehind = null;
        DateTimeOffset? unreachableSince = null;
        try
        {
            while (true)
            {
                var fresh = Math.Min(command.OutputTotal - shown, command.Output.Count);
                foreach (var line in command.Output.Skip(command.Output.Count - fresh))
                    if (show?.Invoke(line) != false) run.Output.Report(line);
                shown = command.OutputTotal;
                if (command.Finished) return command;
                if (command.State == NodeCommandState.Queued && polls++ % QueueReadEvery == 0)
                {
                    try
                    {
                        var behind = (await connection.ReadCommandsAsync(run.Token)).WaitingText(command, name);
                        if (behind is not null && behind != waitingBehind) run.Output.Report(behind);
                        waitingBehind = behind;
                    }
                    catch (Audio2FaceHostException) { }
                    catch (OperationCanceledException) when (!run.Token.IsCancellationRequested) { }
                }
                run.Status(command.State == NodeCommandState.Queued
                    ? waitingBehind ?? $"Waiting for Martlet on {name} to take it. It runs as soon as Martlet is open there; Cancel withdraws it."
                    : command.CancelRequested ? $"Asked Martlet on {name} to stop..."
                    : $"Running on {name}" + (command.Summary is { } summary ? $": {summary}" : "..."));
                await Task.Delay(PollInterval, run.Token);
                try
                {
                    command = await connection.ReadCommandAsync(command.Id, run.Token);
                    unreachableSince = null;
                }
                catch (Audio2FaceHostException error) when (error.Code is "host.unreachable" or "auth.clock" or "gateway.internal")
                {
                    unreachableSince ??= DateTimeOffset.UtcNow;
                    if (DateTimeOffset.UtcNow - unreachableSince > UnreachablePatience)
                        throw new InvalidOperationException($"{name}'s host service stopped answering for {UnreachablePatience.TotalMinutes:0} minutes.");
                    run.Status($"{name}'s host service isn't answering right now (it restarts during an update); still following the command...");
                }
                catch (OperationCanceledException) when (!run.Token.IsCancellationRequested)
                {
                    unreachableSince ??= DateTimeOffset.UtcNow;
                }
                catch (Audio2FaceHostException error) when (error.Code == "command.not_found")
                {
                    throw new InvalidOperationException($"{name} no longer has this command (its host service restarted). Send it again.");
                }
            }
        }
        catch (OperationCanceledException) when (run.Token.IsCancellationRequested)
        {
            try
            {
                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await connection.CancelCommandAsync(command.Id, limit.Token);
            }
            catch (Exception error) when (error is Audio2FaceHostException or OperationCanceledException or System.Net.Http.HttpRequestException) { }
            throw;
        }
    }

    /// <summary>Runs <paramref name="call"/>, trying again for up to <see cref="SendPatience"/> while the host's gateway doesn't
    /// answer: it restarts while it updates (which another computer may be doing right now), and comes back on its own.</summary>
    private static async Task<T> WhileRestartingAsync<T>(HostRunWindow run, string name, Func<Task<T>> call)
    {
        var since = DateTimeOffset.UtcNow;
        while (true)
        {
            try { return await call(); }
            catch (Exception error) when (Restarting(error, run.Token) && DateTimeOffset.UtcNow - since < SendPatience)
            {
                run.Status($"{name}'s host service isn't answering right now (it restarts while it updates). Trying again...");
                await Task.Delay(SendRetryDelay, run.Token);
            }
        }
    }

    private static bool Restarting(Exception error, CancellationToken token) =>
        error is Audio2FaceHostException { Code: "host.unreachable" or "gateway.internal" } ||
        error is OperationCanceledException && !token.IsCancellationRequested;
}
