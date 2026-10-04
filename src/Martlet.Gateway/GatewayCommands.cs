using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Core.Contracts;
using Martlet.Core.Nodes;
using Microsoft.AspNetCore.Http;

namespace Martlet.Gateway;

/// <summary>Where a gateway keeps the commands paired computers sent it between restarts (commands.json beside host.json on
/// Linux hosts; never their secrets). <see cref="Load"/> returns null when there is none; either call may throw.</summary>
public interface IGatewayCommandStorage
{
    byte[]? Load();
    void Save(byte[] bytes);
}

/// <summary>The host's mailbox for commands between the owner's Martlet computers. Any paired device may send one of the
/// closed set of <see cref="NodeCommandKinds"/> and follow it; only the host's agent (the Martlet app on the host computer,
/// which proves it runs there by presenting the token the gateway wrote to its local configuration) may take and report
/// them. The gateway itself never runs anything. Secrets stay in memory until the agent takes the command.</summary>
internal sealed class GatewayCommandStore
{
    internal const int MaximumRetained = 24;
    internal const int MaximumActive = NodeCommandRules.MaximumActive;
    internal const int MaximumStorageBytes = 1_048_576;
    internal const int SavedOutputLines = 20;
    internal static readonly TimeSpan SecretLifetime = TimeSpan.FromMinutes(15);
    internal static readonly TimeSpan QueueLifetime = TimeSpan.FromHours(24);
    internal static readonly TimeSpan SilentRunLimit = TimeSpan.FromHours(2);

    private sealed class Entry(NodeCommand command, Dictionary<string, string>? secrets)
    {
        internal NodeCommand Command = command;
        internal Dictionary<string, string>? Secrets = secrets;
    }

    private sealed record Document
    {
        public required int SchemaVersion { get; init; }
        public required IReadOnlyList<NodeCommand> Commands { get; init; }
    }

    private readonly object gate = new();
    private readonly List<Entry> entries = [];
    private byte[]? agentToken;
    private NodeAgentInfo? agent;
    private IGatewayCommandStorage? storage;

    internal void Attach(IGatewayCommandStorage value, string token, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(value);
        GatewayRules.Require(Base64Url.TryDecode(token, 32, out var raw), "request.invalid");
        List<NodeCommand> saved = [];
        try
        {
            if (value.Load() is { Length: > 0 and <= MaximumStorageBytes } bytes)
            {
                using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
                if (document.RootElement.GetProperty("schema_version").GetInt32() == 1)
                    foreach (var item in document.RootElement.GetProperty("commands").EnumerateArray().Take(MaximumRetained))
                        saved.Add(NodeCommandRules.Read(item));
            }
        }
        // An unreadable copy is dropped; senders see their command is gone and can send it again.
        catch (Exception error) when (error is JsonException or ContractException or KeyNotFoundException or InvalidOperationException or
            FormatException or IOException or UnauthorizedAccessException or GatewayProtocolException) { saved.Clear(); }
        lock (gate)
        {
            storage = value;
            agentToken = raw;
            entries.Clear();
            foreach (var command in saved.DistinctBy(c => c.Id))
                entries.Add(new(command.State == NodeCommandState.Queued && command.HasSecrets
                    ? Finish(command, NodeCommandState.Failed, "This host restarted before its Martlet took the command, and secrets are " +
                        "kept only in memory. Send it again.", now)
                    : command, null));
            SweepLocked(now);
            SaveLocked();
        }
    }

    internal (NodeAgentInfo? Agent, IReadOnlyList<NodeCommand> Commands) List(DateTimeOffset now)
    {
        lock (gate)
        {
            if (SweepLocked(now)) SaveLocked();
            return (agent, entries.Select(e => e.Command with { Output = [] }).Reverse().ToArray());
        }
    }

    internal NodeCommand Get(string id, DateTimeOffset now)
    {
        lock (gate)
        {
            if (SweepLocked(now)) SaveLocked();
            return Find(id).Command;
        }
    }

    internal NodeCommand Submit(GatewayPrincipal principal, string kind, IReadOnlyDictionary<string, string> arguments,
        IReadOnlyDictionary<string, string> secrets, DateTimeOffset now, string id)
    {
        try { NodeCommandRules.Validate(kind, arguments, secrets); }
        catch (ContractException) { throw new GatewayProtocolException("request.invalid"); }
        lock (gate)
        {
            SweepLocked(now);
            // The same request while one is still waiting or running is that one (a double click, or two computers asking
            // for the same update).
            if (entries.FirstOrDefault(e => !e.Command.Finished && e.Command.Kind == kind && SameArguments(e.Command.Arguments, arguments))
                is { } existing)
                return existing.Command;
            if (entries.Count(e => !e.Command.Finished) >= MaximumActive) throw new GatewayProtocolException("command.busy");
            var command = new NodeCommand
            {
                Id = id, Kind = kind, Arguments = new SortedDictionary<string, string>(arguments.ToDictionary(), StringComparer.Ordinal),
                RequestedBy = principal.DeviceId, RequestedAt = now, State = NodeCommandState.Queued, UpdatedAt = now,
                HasSecrets = secrets.Count > 0
            };
            entries.Add(new(command, secrets.Count > 0 ? secrets.ToDictionary() : null));
            while (entries.Count > MaximumRetained && entries.FindIndex(e => e.Command.Finished) is >= 0 and var oldest)
                entries.RemoveAt(oldest);
            SaveLocked();
            return command;
        }
    }

    internal NodeCommand Cancel(GatewayPrincipal principal, string id, DateTimeOffset now)
    {
        lock (gate)
        {
            SweepLocked(now);
            var entry = Find(id);
            if (entry.Command.State == NodeCommandState.Queued)
            {
                entry.Command = Finish(entry.Command, NodeCommandState.Canceled, $"Canceled by {principal.DeviceId} before it ran.", now);
                entry.Secrets = null;
            }
            else if (entry.Command.State == NodeCommandState.Running && !entry.Command.CancelRequested)
                entry.Command = entry.Command with { CancelRequested = true, UpdatedAt = now };
            else return entry.Command;
            SaveLocked();
            return entry.Command;
        }
    }

    /// <summary>The agent asks for work: first a command it took before and isn't running now (after it restarted, for
    /// example to install an update), otherwise the next waiting command of a kind it runs. An agent that runs commands
    /// side by side lists the ones it runs (<paramref name="running"/>, kept alive by asking) and gets the oldest waiting
    /// command that <see cref="NodeCommandSchedule"/> lets start beside them; an older agent (null) runs one at a time and
    /// asks only between commands. It is handed that command's secrets once; the gateway then forgets them.</summary>
    internal (NodeCommand? Command, IReadOnlyDictionary<string, string>? Secrets, bool Resumed) Poll(GatewayPrincipal principal, string token,
        string? version, IReadOnlyList<string> kinds, IReadOnlyList<string>? running, DateTimeOffset now)
    {
        GatewayRules.Require(running is null || running.Count <= MaximumActive && running.All(NodeCommandRules.IsId), "request.invalid");
        lock (gate)
        {
            RequireAgentLocked(token);
            var changed = SweepLocked(now);
            agent = new()
            {
                DeviceId = principal.DeviceId, SeenAt = now, Version = NodeCommandRules.IsVersion(version) ? version : null,
                Kinds = NodeCommandKinds.All.Where(kinds.Contains).ToArray(), Parallel = running is not null
            };
            var mine = entries.Where(e => e.Command.State == NodeCommandState.Running && e.Command.ClaimedBy == principal.DeviceId).ToArray();
            // Asking again keeps the commands the agent runs, or continues later (for example after restarting into an update),
            // alive.
            foreach (var entry in mine.Where(e => now - e.Command.UpdatedAt > TimeSpan.FromMinutes(1)))
            {
                entry.Command = entry.Command with { UpdatedAt = now };
                changed = true;
            }
            if (mine.FirstOrDefault(e => running?.Contains(e.Command.Id) != true) is { } resume)
            {
                if (changed) SaveLocked();
                return (resume.Command, null, true);
            }
            var commands = entries.Select(e => e.Command).Where(c => c.State == NodeCommandState.Running || agent.Kinds.Contains(c.Kind)).ToArray();
            if (entries.FirstOrDefault(e => e.Command.State == NodeCommandState.Queued && agent.Kinds.Contains(e.Command.Kind) &&
                    (running is null || NodeCommandSchedule.Blocker(e.Command, commands, parallel: true) is null)) is not { } next)
            {
                if (changed) SaveLocked();
                return (null, null, false);
            }
            next.Command = next.Command with { State = NodeCommandState.Running, ClaimedBy = principal.DeviceId, UpdatedAt = now };
            var secrets = next.Secrets;
            next.Secrets = null;
            SaveLocked();
            return (next.Command, secrets, false);
        }
    }

    /// <summary>The agent adds output to a command it took and, at the end, its outcome. Returns whether a paired computer
    /// asked to cancel it.</summary>
    internal bool Report(GatewayPrincipal principal, string token, string id, NodeCommandState? state, string? summary,
        IReadOnlyList<string> output, int? exitCode, DateTimeOffset now)
    {
        GatewayRules.Require(state is null or NodeCommandState.Succeeded or NodeCommandState.Failed or NodeCommandState.Canceled &&
            output.Count <= NodeCommandRules.MaximumOutputLines, "request.invalid");
        lock (gate)
        {
            RequireAgentLocked(token);
            SweepLocked(now);
            var entry = Find(id);
            if (entry.Command.State != NodeCommandState.Running) throw new GatewayProtocolException("command.not_found");
            if (entry.Command.ClaimedBy != principal.DeviceId) throw new GatewayProtocolException("command.agent");
            var lines = entry.Command.Output.Concat(output.Select(NodeCommandRules.Line)).TakeLast(NodeCommandRules.MaximumOutputLines).ToArray();
            entry.Command = entry.Command with
            {
                Output = lines, OutputTotal = entry.Command.OutputTotal + output.Count,
                Summary = NodeCommandRules.Summary(summary) ?? entry.Command.Summary, UpdatedAt = now
            };
            if (state is { } final) entry.Command = Finish(entry.Command, final, entry.Command.Summary, now) with { ExitCode = exitCode };
            if (agent?.DeviceId == principal.DeviceId) agent = agent with { SeenAt = now };
            SaveLocked();
            return entry.Command.CancelRequested;
        }
    }

    private void RequireAgentLocked(string token)
    {
        if (agentToken is null || !Base64Url.TryDecode(token, 32, out var presented) ||
            !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(presented, agentToken))
            throw new GatewayProtocolException("command.agent");
    }

    private Entry Find(string id) =>
        (NodeCommandRules.IsId(id) ? entries.FirstOrDefault(e => e.Command.Id == id) : null) ?? throw new GatewayProtocolException("command.not_found");

    private bool SweepLocked(DateTimeOffset now)
    {
        var changed = false;
        foreach (var entry in entries)
        {
            var command = entry.Command;
            string? why = command.State switch
            {
                NodeCommandState.Queued when command.HasSecrets && now - command.RequestedAt > SecretLifetime =>
                    "Martlet on the host did not take it within 15 minutes, so its secrets were dropped. Open Martlet there and send it again.",
                NodeCommandState.Queued when now - command.RequestedAt > QueueLifetime =>
                    "Martlet on the host did not take it within a day. Open Martlet there and send it again.",
                NodeCommandState.Running when now - command.UpdatedAt > SilentRunLimit =>
                    "Martlet on the host stopped reporting on it.",
                _ => null
            };
            if (why is null) continue;
            entry.Command = Finish(command, NodeCommandState.Failed, why, now);
            entry.Secrets = null;
            changed = true;
        }
        return changed;
    }

    private void SaveLocked()
    {
        if (storage is null) return;
        try
        {
            // The saved copy keeps each command's state and only its latest output; the full tail lives in memory.
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new Document
            {
                SchemaVersion = 1, Commands = entries.Select(e => e.Command with { Output = e.Command.Output.TakeLast(SavedOutputLines).ToArray() }).ToArray()
            }, NodeCommandRules.Json);
            if (bytes.Length <= MaximumStorageBytes) storage.Save(bytes);
        }
        // Commands that cannot be saved still work from memory until the gateway restarts.
        catch (Exception) { }
    }

    private static NodeCommand Finish(NodeCommand command, NodeCommandState state, string? summary, DateTimeOffset now) =>
        command with { State = state, Summary = NodeCommandRules.Summary(summary), UpdatedAt = now, CancelRequested = false };

    private static bool SameArguments(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b) =>
        a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var value) && value == pair.Value);
}

internal sealed partial class GatewayHttpApplication
{
    internal const string CommandsPath = "/martlet/v1/commands";

    internal GatewayCommandStore Commands { get; } = new();

    /// <summary>The command endpoints, all over the paired device's signed, pinned connection:
    /// GET  /martlet/v1/commands             the host's agent and recent commands (without output)
    /// POST /martlet/v1/commands             send a command
    /// GET  /martlet/v1/commands/{id}        one command with its output
    /// POST /martlet/v1/commands/{id}/cancel cancel it (waiting: at once; running: the agent stops it)
    /// POST /martlet/v1/commands/agent       the agent takes work (token required)
    /// POST /martlet/v1/commands/{id}/report the agent reports output and outcome (token required)</summary>
    private async ValueTask InvokeCommandsAsync(HttpContext context, string rawTarget)
    {
        var rest = rawTarget.Length == CommandsPath.Length ? "" : rawTarget[(CommandsPath.Length + 1)..];
        var parts = rest.Length == 0 ? [] : rest.Split('/');
        var now = clock.GetUtcNow();
        var get = context.Request.Method == HttpMethods.Get;
        GatewayRules.Require(get || context.Request.Method == HttpMethods.Post, "request.invalid");
        if (get)
        {
            EnsureEmptyRequest(context.Request);
            _ = Authorize(context.Request, GatewayApiAccess.ReadOrManage);
            if (parts.Length == 0)
            {
                var (agent, commands) = Commands.List(now);
                await WriteJsonAsync(context, StatusCodes.Status200OK, new CommandListDocument
                {
                    ProtocolVersion = GatewayProtocolVersion.Current, HostId = identity.HostId, GeneratedAt = now,
                    Agent = agent, Commands = commands
                }, NodeCommandRules.MaximumResponseBytes).ConfigureAwait(false);
                return;
            }
            GatewayRules.Require(parts.Length == 1 && NodeCommandRules.IsId(parts[0]), "request.invalid");
            await WriteCommandAsync(context, StatusCodes.Status200OK, Commands.Get(parts[0], now)).ConfigureAwait(false);
            return;
        }

        var bytes = await ReadInferenceBodyAsync(context.Request, NodeCommandRules.MaximumRequestBytes, context.RequestAborted)
            .ConfigureAwait(false);
        // An API key with manage access may send and cancel commands; taking and reporting them is the host's own agent's.
        var principal = parts.Length == 0 || parts is [_, "cancel"]
            ? Authorize(context.Request, crypto.Sha256(bytes), GatewayApiAccess.Manage)
            : authenticator.Authenticate(context.Request, crypto.Sha256(bytes));
        if (parts.Length == 0)
        {
            var request = ReadCommandBody<SubmitRequest>(bytes);
            var command = Commands.Submit(principal, request.Kind, request.Arguments ?? new Dictionary<string, string>(),
                request.Secrets ?? new Dictionary<string, string>(), now, Base64Url.Encode(crypto.RandomBytes(16)));
            await WriteCommandAsync(context, StatusCodes.Status202Accepted, command).ConfigureAwait(false);
        }
        else if (parts is ["agent"])
        {
            var request = ReadCommandBody<AgentRequest>(bytes);
            var (command, secrets, resumed) = Commands.Poll(principal, request.Token, request.Version, request.Kinds ?? [], request.Running, now);
            await WriteJsonAsync(context, StatusCodes.Status200OK, new AgentDocument
            {
                ProtocolVersion = GatewayProtocolVersion.Current, HostId = identity.HostId, Command = command,
                Secrets = secrets, Resumed = resumed
            }, NodeCommandRules.MaximumResponseBytes).ConfigureAwait(false);
        }
        else if (parts.Length == 2 && NodeCommandRules.IsId(parts[0]) && parts[1] == "cancel")
        {
            _ = ReadCommandBody<CancelRequest>(bytes);
            await WriteCommandAsync(context, StatusCodes.Status200OK, Commands.Cancel(principal, parts[0], now)).ConfigureAwait(false);
        }
        else if (parts.Length == 2 && NodeCommandRules.IsId(parts[0]) && parts[1] == "report")
        {
            var request = ReadCommandBody<ReportRequest>(bytes);
            var cancel = Commands.Report(principal, request.Token, parts[0], request.State, request.Summary, request.Output ?? [],
                request.ExitCode, now);
            await WriteJsonAsync(context, StatusCodes.Status200OK, new ReportDocument
            {
                ProtocolVersion = GatewayProtocolVersion.Current, HostId = identity.HostId, CancelRequested = cancel
            }).ConfigureAwait(false);
        }
        else throw new GatewayProtocolException("request.invalid");
    }

    private ValueTask WriteCommandAsync(HttpContext context, int status, NodeCommand command) =>
        WriteJsonAsync(context, status, new CommandDocument
        {
            ProtocolVersion = GatewayProtocolVersion.Current, HostId = identity.HostId, Command = command
        }, NodeCommandRules.MaximumResponseBytes);

    private static T ReadCommandBody<T>(byte[] bytes) where T : class
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
            InspectJson(document.RootElement);
            return document.Deserialize<T>(Json) ?? throw new GatewayProtocolException("request.invalid");
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or NotSupportedException)
        {
            throw new GatewayProtocolException("request.invalid");
        }
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record SubmitRequest
    {
        public required string Kind { get; init; }
        public Dictionary<string, string>? Arguments { get; init; }
        public Dictionary<string, string>? Secrets { get; init; }
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record AgentRequest
    {
        public required string Token { get; init; }
        public string? Version { get; init; }
        public string[]? Kinds { get; init; }
        /// <summary>The commands an agent that runs them side by side runs right now; absent for agents that run one at a time.</summary>
        public string[]? Running { get; init; }
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record ReportRequest
    {
        public required string Token { get; init; }
        public NodeCommandState? State { get; init; }
        public string? Summary { get; init; }
        public string[]? Output { get; init; }
        public int? ExitCode { get; init; }
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record CancelRequest;

    private sealed record CommandListDocument
    {
        public required GatewayProtocolVersion ProtocolVersion { get; init; }
        public required string HostId { get; init; }
        public required DateTimeOffset GeneratedAt { get; init; }
        public NodeAgentInfo? Agent { get; init; }
        public required IReadOnlyList<NodeCommand> Commands { get; init; }
    }

    private sealed record CommandDocument
    {
        public required GatewayProtocolVersion ProtocolVersion { get; init; }
        public required string HostId { get; init; }
        public required NodeCommand Command { get; init; }
    }

    private sealed record AgentDocument
    {
        public required GatewayProtocolVersion ProtocolVersion { get; init; }
        public required string HostId { get; init; }
        public NodeCommand? Command { get; init; }
        public IReadOnlyDictionary<string, string>? Secrets { get; init; }
        public required bool Resumed { get; init; }
    }

    private sealed record ReportDocument
    {
        public required GatewayProtocolVersion ProtocolVersion { get; init; }
        public required string HostId { get; init; }
        public required bool CancelRequested { get; init; }
    }
}
