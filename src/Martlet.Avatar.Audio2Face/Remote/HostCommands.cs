using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Martlet.Core.Contracts;
using Martlet.Core.Nodes;

namespace Martlet.Avatar.Audio2Face.Remote;

/// <summary>A host's command mailbox as one paired computer sees it: the Martlet app that runs its commands (null when none
/// has asked for work since the gateway started) and its recent commands.</summary>
public sealed record HostCommandList(NodeAgentInfo? Agent, IReadOnlyList<NodeCommand> Commands)
{
    /// <summary>What <paramref name="waiting"/> waits behind, or null when it is not waiting or nothing is ahead of it. The
    /// host's agent runs one command at a time and finishes the one it took before it takes another (an update stays first
    /// while it waits until Martlet there is idle and while Martlet restarts into it); waiting commands go oldest first.</summary>
    public NodeCommand? Ahead(NodeCommand waiting) =>
        waiting.State != NodeCommandState.Queued ? null
        : Commands.Where(c => c.State == NodeCommandState.Running && c.Id != waiting.Id).OrderBy(c => c.RequestedAt).FirstOrDefault()
          ?? Commands.Where(c => c.State == NodeCommandState.Queued && c.Id != waiting.Id && c.RequestedAt < waiting.RequestedAt)
              .OrderBy(c => c.RequestedAt).FirstOrDefault();

    /// <summary>Why <paramref name="waiting"/> has not started yet, in words, or null when nothing is ahead of it.</summary>
    public string? WaitingText(NodeCommand waiting, string host) => Ahead(waiting) switch
    {
        null => null,
        { State: NodeCommandState.Running, Kind: NodeCommandKinds.Update } update =>
            $"Martlet on {host} is updating first: {NodeCommandAgent.Describe(update)}. This runs right after it.",
        { State: NodeCommandState.Running } running => $"Martlet on {host} is busy with: {NodeCommandAgent.Describe(running)}. This runs next.",
        var earlier => $"Waiting behind {NodeCommandAgent.Describe(earlier)}, sent earlier. Martlet on {host} runs them in order."
    };
}

/// <summary>Work the gateway hands the host's agent: a command (null when there is none), its secrets (handed over once)
/// and whether the agent had taken it before (after a restart).</summary>
public sealed record HostAgentWork(NodeCommand? Command, IReadOnlyDictionary<string, string> Secrets, bool Resumed);

/// <summary>Commands between the owner's Martlet computers, through a host's gateway (see <see cref="NodeCommandKinds"/>).</summary>
public sealed partial class Audio2FaceHostConnection
{
    private const string CommandsPath = "/martlet/v1/commands";

    /// <summary>Reads the host's agent and recent commands. Hosts older than commands refuse with code
    /// <c>request.invalid</c>.</summary>
    public async Task<HostCommandList> ReadCommandsAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, pairing.Origin + CommandsPath);
        Sign(request, []);
        return await CommandJsonAsync(request, root =>
        {
            NodeAgentInfo? agent = null;
            if (root.TryGetProperty("agent", out var found) && found.ValueKind == JsonValueKind.Object)
            {
                agent = found.Deserialize<NodeAgentInfo>(NodeCommandRules.Json);
                if (agent is null || !NodeCommandRules.IsDevice(agent.DeviceId)) throw new FormatException();
            }
            return new HostCommandList(agent, root.GetProperty("commands").EnumerateArray().Take(64).Select(NodeCommandRules.Read).ToArray());
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends a command for the host's Martlet to run. Secrets stay in the gateway's memory until its Martlet takes
    /// the command. Sending the same command while one is waiting or running returns that one.</summary>
    public Task<NodeCommand> SendCommandAsync(string kind, IReadOnlyDictionary<string, string> arguments,
        IReadOnlyDictionary<string, string>? secrets = null, CancellationToken cancellationToken = default)
    {
        NodeCommandRules.Validate(kind, arguments, secrets ?? new Dictionary<string, string>());
        return PostCommandAsync(CommandsPath, new Dictionary<string, object?>
        {
            ["kind"] = kind, ["arguments"] = arguments, ["secrets"] = secrets is { Count: > 0 } ? secrets : null
        }, root => NodeCommandRules.Read(root.GetProperty("command")), cancellationToken);
    }

    /// <summary>Reads one command with the last lines of its output.</summary>
    public async Task<NodeCommand> ReadCommandAsync(string id, CancellationToken cancellationToken = default)
    {
        RequireId(id);
        using var request = new HttpRequestMessage(HttpMethod.Get, pairing.Origin + CommandsPath + "/" + id);
        Sign(request, []);
        return await CommandJsonAsync(request, root => NodeCommandRules.Read(root.GetProperty("command")), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Cancels a command: one still waiting ends at once; a running one is stopped by the host's Martlet.</summary>
    public Task<NodeCommand> CancelCommandAsync(string id, CancellationToken cancellationToken = default)
    {
        RequireId(id);
        return PostCommandAsync(CommandsPath + "/" + id + "/cancel", new Dictionary<string, object?>(),
            root => NodeCommandRules.Read(root.GetProperty("command")), cancellationToken);
    }

    /// <summary>For the Martlet app on the host computer only: takes the next command of <paramref name="kinds"/>, presenting
    /// the agent token the gateway wrote to the host's local configuration. Also tells the gateway this agent is alive.</summary>
    public Task<HostAgentWork> TakeCommandAsync(string token, string? version, IReadOnlyList<string> kinds,
        CancellationToken cancellationToken = default) =>
        PostCommandAsync(CommandsPath + "/agent", new Dictionary<string, object?>
        {
            ["token"] = token, ["version"] = version, ["kinds"] = kinds
        }, root =>
        {
            var command = root.TryGetProperty("command", out var found) && found.ValueKind == JsonValueKind.Object
                ? NodeCommandRules.Read(found) : null;
            var secrets = root.TryGetProperty("secrets", out var held) && held.ValueKind == JsonValueKind.Object
                ? held.Deserialize<Dictionary<string, string>>() ?? [] : [];
            return new HostAgentWork(command, secrets, root.GetProperty("resumed").GetBoolean());
        }, cancellationToken);

    /// <summary>For the host's agent: adds output lines to a command it took and, with <paramref name="state"/>, ends it.
    /// Returns whether a paired computer asked to cancel it.</summary>
    public Task<bool> ReportCommandAsync(string token, string id, IReadOnlyList<string> output, string? summary = null,
        NodeCommandState? state = null, int? exitCode = null, CancellationToken cancellationToken = default)
    {
        RequireId(id);
        return PostCommandAsync(CommandsPath + "/" + id + "/report", new Dictionary<string, object?>
        {
            ["token"] = token, ["output"] = output.Select(NodeCommandRules.Line).ToArray(), ["summary"] = NodeCommandRules.Summary(summary),
            ["state"] = state, ["exit_code"] = exitCode
        }, root => root.GetProperty("cancel_requested").GetBoolean(), cancellationToken);
    }

    private async Task<T> PostCommandAsync<T>(string path, Dictionary<string, object?> body, Func<JsonElement, T> read,
        CancellationToken cancellationToken)
    {
        foreach (var key in body.Where(pair => pair.Value is null).Select(pair => pair.Key).ToArray()) body.Remove(key);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body, NodeCommandRules.Json);
        using var request = new HttpRequestMessage(HttpMethod.Post, pairing.Origin + path) { Content = Audio2FaceHostClient.JsonContent(bytes) };
        Sign(request, bytes);
        return await CommandJsonAsync(request, read, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> CommandJsonAsync<T>(HttpRequestMessage request, Func<JsonElement, T> read, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var response = await Audio2FaceHostClient.Send(http, request, timeout.Token).ConfigureAwait(false);
        using var document = await Audio2FaceHostClient.ReadJson(response, NodeCommandRules.MaximumResponseBytes, timeout.Token)
            .ConfigureAwait(false);
        if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Accepted)) throw Audio2FaceHostClient.Remote(document.RootElement);
        try
        {
            var root = document.RootElement;
            if (root.GetProperty("host_id").GetString() != pairing.HostId)
                throw new Audio2FaceHostException("response.invalid", "The host identity changed; pair again.");
            return read(root);
        }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or ContractException or FormatException or
            JsonException)
        {
            throw new Audio2FaceHostException("response.invalid", "The host's command list was invalid.");
        }
    }

    private static void RequireId(string id)
    {
        if (!NodeCommandRules.IsId(id)) throw new ArgumentException("Invalid command ID.", nameof(id));
    }
}

/// <summary>How a command the host's agent ran ended.</summary>
public sealed record NodeCommandOutcome(bool Succeeded, string Summary, int? ExitCode = null);

/// <summary>Runs the commands a host's agent takes. <see cref="RunAsync"/> returns null when the command is still going
/// on beyond this run (for example while Martlet restarts into an update); the agent takes it again afterwards.</summary>
public interface INodeCommandRunner
{
    IReadOnlyList<string> Kinds { get; }
    Task<NodeCommandOutcome?> RunAsync(NodeCommand command, IReadOnlyDictionary<string, string> secrets, bool resumed,
        IProgress<string> output, CancellationToken cancellationToken);
}

/// <summary>What one pass of <see cref="NodeCommandAgent"/> found or did.</summary>
public enum NodeAgentPassKind { Idle, Ran, Pending, NoToken, HostTooOld, Unreachable }

public sealed record NodeAgentPass(NodeAgentPassKind Kind, string Text, NodeCommand? Command = null, NodeCommandOutcome? Outcome = null);

/// <summary>The host side of commands between Martlet computers: asks this host's gateway for work with the local agent
/// token, runs it through an <see cref="INodeCommandRunner"/>, streams its output back every couple of seconds (holding it
/// while the gateway restarts, for example during its own update) and reports the outcome.</summary>
public sealed class NodeCommandAgent(Func<CancellationToken, Task<string?>> readToken, string version, INodeCommandRunner runner)
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan FinalReportPatience = TimeSpan.FromMinutes(10);
    private string? token;

    /// <summary>One pass: take at most one command and run it to its end (or until the runner says it continues later).</summary>
    public async Task<NodeAgentPass> RunOnceAsync(Audio2FaceHostConnection connection, CancellationToken cancellationToken)
    {
        HostAgentWork work;
        try { work = await TakeAsync(connection, cancellationToken).ConfigureAwait(false); }
        catch (Audio2FaceHostException error) when (error.Code == "request.invalid")
        {
            return new(NodeAgentPassKind.HostTooOld, "This host service predates commands between computers; update it.");
        }
        catch (Audio2FaceHostException error) when (error.Code == "command.agent")
        {
            token = null;
            return new(NodeAgentPassKind.NoToken, "The host service did not accept this PC's agent token.");
        }
        catch (Audio2FaceHostException error) { return new(NodeAgentPassKind.Unreachable, error.Message); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(NodeAgentPassKind.Unreachable, "The host service did not answer in time.");
        }
        catch (InvalidOperationException error) { return new(NodeAgentPassKind.NoToken, error.Message); }
        if (work.Command is not { } command) return new(NodeAgentPassKind.Idle, "Waiting for commands from your other computers.");

        var lines = new ConcurrentQueue<string>();
        var output = new LineQueue(lines);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var running = RunCommandAsync(command, work, output, stop.Token);
        while (!running.IsCompleted)
        {
            await Task.WhenAny(running, Task.Delay(FlushInterval, cancellationToken)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (await FlushAsync(connection, command.Id, lines, null, null, null, cancellationToken).ConfigureAwait(false) == true)
                await stop.CancelAsync().ConfigureAwait(false);
        }
        var outcome = await running.ConfigureAwait(false);
        if (outcome is null)
        {
            await FlushAsync(connection, command.Id, lines, null, null, null, cancellationToken).ConfigureAwait(false);
            return new(NodeAgentPassKind.Pending, $"{Describe(command)} continues after this PC's Martlet restarts.", command);
        }
        var state = outcome.Succeeded ? NodeCommandState.Succeeded
            : stop.IsCancellationRequested && !cancellationToken.IsCancellationRequested ? NodeCommandState.Canceled : NodeCommandState.Failed;
        var deadline = DateTimeOffset.UtcNow + FinalReportPatience;
        while (await FlushAsync(connection, command.Id, lines, outcome.Summary, state, outcome.ExitCode, cancellationToken).ConfigureAwait(false) is null)
        {
            if (DateTimeOffset.UtcNow > deadline) break;
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
        }
        return new(NodeAgentPassKind.Ran, $"{Describe(command)}: {outcome.Summary}", command, outcome);
    }

    /// <summary>A command in words ("Update to Martlet 0.17.0 (from desktop-main)").</summary>
    public static string Describe(NodeCommand command)
    {
        var what = command.Kind switch
        {
            NodeCommandKinds.Update => $"Update to Martlet {command.Arguments.GetValueOrDefault("version")}",
            NodeCommandKinds.Status => "Show the host service's status",
            NodeCommandKinds.DescribeRole => $"Read what {command.Arguments.GetValueOrDefault("role")} needs",
            NodeCommandKinds.AddRole => $"Install {command.Arguments.GetValueOrDefault("role")}",
            NodeCommandKinds.RemoveRole => $"Remove {command.Arguments.GetValueOrDefault("role")}",
            _ => command.Kind
        };
        return $"{what} (from {command.RequestedBy})";
    }

    private async Task<HostAgentWork> TakeAsync(Audio2FaceHostConnection connection, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            token ??= await readToken(cancellationToken).ConfigureAwait(false) ??
                throw new InvalidOperationException("This PC's host service has no agent token yet (it predates commands or is not running).");
            try { return await connection.TakeCommandAsync(token, version, runner.Kinds, cancellationToken).ConfigureAwait(false); }
            // The gateway writes a new token each time it starts.
            catch (Audio2FaceHostException error) when (error.Code == "command.agent" && attempt == 0) { token = null; }
        }
    }

    private async Task<NodeCommandOutcome?> RunCommandAsync(NodeCommand command, HostAgentWork work, IProgress<string> output,
        CancellationToken cancellationToken)
    {
        try { return await runner.RunAsync(command, work.Secrets, work.Resumed, output, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return new(false, "Canceled."); }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException or
            System.ComponentModel.Win32Exception or Audio2FaceHostException or ContractException or ArgumentException or HttpRequestException)
        {
            return new(false, error.Message);
        }
    }

    /// <summary>Sends the waiting lines (and an outcome). Returns whether cancel was requested, or null when the gateway
    /// could not be reached (the lines stay queued, keeping the newest).</summary>
    private async Task<bool?> FlushAsync(Audio2FaceHostConnection connection, string id, ConcurrentQueue<string> lines, string? summary,
        NodeCommandState? state, int? exitCode, CancellationToken cancellationToken)
    {
        while (lines.Count > NodeCommandRules.MaximumOutputLines) lines.TryDequeue(out _);
        if (lines.IsEmpty && state is null) return false;
        var batch = lines.ToArray().Take(NodeCommandRules.MaximumOutputLines).ToArray();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                token ??= await readToken(cancellationToken).ConfigureAwait(false);
                if (token is null) return null;
                var cancel = await connection.ReportCommandAsync(token, id, batch, summary, state, exitCode, cancellationToken).ConfigureAwait(false);
                for (var i = 0; i < batch.Length; i++) lines.TryDequeue(out _);
                return cancel;
            }
            catch (Audio2FaceHostException error) when (error.Code == "command.agent" && attempt == 0) { token = null; }
            catch (Audio2FaceHostException error) when (error.Code == "command.not_found") { return true; }
            catch (Audio2FaceHostException) { return null; }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
        }
        return null;
    }

    private sealed class LineQueue(ConcurrentQueue<string> lines) : IProgress<string>
    {
        public void Report(string value)
        {
            foreach (var line in (value ?? "").Replace("\r\n", "\n").Split('\n')) lines.Enqueue(NodeCommandRules.Line(line));
        }
    }
}
