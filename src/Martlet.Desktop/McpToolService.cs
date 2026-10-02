using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Martlet.Conversation;
using Martlet.Mcp.Client;
using Martlet.Providers;

namespace Martlet.Desktop;

internal enum ToolApprovalChoice { Deny, AllowOnce, AlwaysAllow }

/// <summary>What the feature that manages a server decides about one call. Default follows the normal rules (mcp.json's
/// autoApprove, then asking with Always allow offered); AskEveryTime asks without offering Always allow; Deny blocks.</summary>
internal enum ToolApprovalPolicy { Default, AutoApprove, AskEveryTime, Deny }

/// <summary>A tool call waiting for the user's answer in the talk window. Unanswered requests are declined.</summary>
internal sealed class ToolApprovalRequest(string server, string tool, string arguments, DateTimeOffset expires, bool allowAlways = true)
{
    private readonly TaskCompletionSource<ToolApprovalChoice> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal string Server { get; } = server;
    internal string Tool { get; } = tool;
    /// <summary>The arguments as indented JSON, cut to a readable length.</summary>
    internal string Arguments { get; } = arguments;
    internal DateTimeOffset Expires { get; } = expires;
    /// <summary>Whether Always allow is offered (never for managed servers or calls their owner wants asked every time).</summary>
    internal bool AllowAlways { get; } = allowAlways;
    internal Task<ToolApprovalChoice> Answer => answer.Task;
    internal void Resolve(ToolApprovalChoice choice) => answer.TrySetResult(choice);
    public override string ToString() => $"{nameof(ToolApprovalRequest)} {Server}/{Tool}";
}

/// <summary>One entry of the in-memory tool log on the Tools page (never written to disk).</summary>
internal sealed record ToolActivity(DateTimeOffset At, string Server, string Tool, string Outcome, string Arguments, bool Problem);

/// <summary>The MCP servers Martlet may call while you talk: the user's mcp.json in the data directory plus servers other
/// Martlet features manage (<see cref="SetManagedServer"/>), one session per enabled server (started only when a
/// conversation or the Tools page needs them), per-call confirmations and a short in-memory log. Tools are offered only to
/// user-started replies, never to screen glances or memory requests.</summary>
internal sealed class McpToolService : IAsyncDisposable
{
    internal const string FileName = "mcp.json";
    internal const int LogLength = 40;
    internal static TimeSpan ApprovalTimeout => TimeSpan.FromSeconds(60);
    internal static TimeSpan CallTimeout => TimeSpan.FromSeconds(60);
    internal static TimeSpan StartupWait => TimeSpan.FromSeconds(10);
    private readonly object gate = new();
    private readonly LinkedList<ToolActivity> log = new();
    private readonly HashSet<string> unsupportedModels = new(StringComparer.Ordinal);
    private readonly TimeProvider clock;
    private McpConfiguration configuration = McpConfiguration.Empty;
    private readonly Dictionary<string, (McpServerDefinition Definition, Func<string, JsonObject, ToolApprovalPolicy>? Policy)> managed =
        new(StringComparer.Ordinal);
    private ToolApprovalRequest? pending;
    private bool loaded, started, disposed;

    internal McpToolService(string? dataDirectory, TimeProvider? clock = null)
    {
        this.clock = clock ?? TimeProvider.System;
        FilePath = dataDirectory is null ? null : Path.Combine(dataDirectory, FileName);
        Hub = new("martlet", AppVersions.Current, this.clock);
        Hub.Changed += () => Changed?.Invoke();
    }

    internal string? FilePath { get; }
    internal McpHub Hub { get; }
    /// <summary>Why mcp.json could not be used (the servers it listed are not started until it is fixed).</summary>
    internal string? ConfigurationError { get; private set; }
    internal bool Started { get { lock (gate) return started; } }
    internal ToolApprovalRequest? PendingApproval { get { lock (gate) return pending; } }
    internal IReadOnlyList<ToolActivity> Log { get { lock (gate) return [.. log]; } }
    /// <summary>Raised on any thread when servers, approvals or the log change.</summary>
    internal event Action? Changed;

    internal McpConfiguration Configuration
    {
        get
        {
            EnsureLoaded();
            lock (gate) return configuration;
        }
    }

    internal bool HasEnabledServers => Servers.Any(s => !s.Disabled);

    /// <summary>Every server Martlet runs: mcp.json's, then managed ones whose name mcp.json doesn't already use.</summary>
    internal IReadOnlyList<McpServerDefinition> Servers
    {
        get
        {
            var user = Configuration.Servers;
            lock (gate)
                return [.. user, .. managed.Values.Select(m => m.Definition)
                    .Where(d => !user.Any(u => string.Equals(u.Name, d.Name, StringComparison.OrdinalIgnoreCase)))];
        }
    }

    /// <summary>Servers another feature manages that can't run because mcp.json uses the same name.</summary>
    internal IReadOnlyList<McpServerDefinition> ManagedConflicts
    {
        get
        {
            var user = Configuration.Servers;
            lock (gate)
                return managed.Values.Select(m => m.Definition)
                    .Where(d => user.Any(u => string.Equals(u.Name, d.Name, StringComparison.OrdinalIgnoreCase))).ToArray();
        }
    }

    /// <summary>Adds, replaces or (with null) removes a server another Martlet feature manages, such as Smart home's Home
    /// Assistant MCP endpoint. It is never written to mcp.json. <paramref name="policy"/> decides each call before the normal
    /// rules (an exception counts as AskEveryTime); managed servers never offer Always allow. A running set follows at once.</summary>
    internal void SetManagedServer(string name, McpServerDefinition? definition,
        Func<string, JsonObject, ToolApprovalPolicy>? policy = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (definition is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(definition.ManagedBy, nameof(definition));
            if (definition.Name != name) throw new ArgumentException("The definition's name must match.", nameof(definition));
        }
        EnsureLoaded();
        bool apply;
        lock (gate)
        {
            if (disposed) return;
            if (definition is null) managed.Remove(name);
            else managed[name] = (definition, policy);
            apply = started;
        }
        if (apply) Hub.Apply(Servers);
        Changed?.Invoke();
    }

    /// <summary>The managing feature's decision about one call; Default for mcp.json's servers.</summary>
    internal ToolApprovalPolicy PolicyFor(string server, string tool, JsonObject arguments)
    {
        Func<string, JsonObject, ToolApprovalPolicy>? policy;
        lock (gate) policy = managed.TryGetValue(server, out var entry) ? entry.Policy : null;
        if (policy is null) return ToolApprovalPolicy.Default;
        try
        {
            var decision = policy(tool, (JsonObject)arguments.DeepClone());
            return Enum.IsDefined(decision) ? decision : ToolApprovalPolicy.AskEveryTime;
        }
        catch (Exception)
        {
            return ToolApprovalPolicy.AskEveryTime;
        }
    }

    internal string ReadText()
    {
        if (FilePath is null || !File.Exists(FilePath)) return "";
        var info = new FileInfo(FilePath);
        if (info.Length > McpConfiguration.MaxFileBytes) throw new McpConfigurationException("mcp.json is larger than 1 MB.");
        return File.ReadAllText(FilePath);
    }

    internal void EnsureLoaded()
    {
        lock (gate) if (loaded) return;
        Reload();
    }

    /// <summary>Reads mcp.json again; running servers follow the new file only if they were started.</summary>
    internal void Reload()
    {
        McpConfiguration next;
        string? error = null;
        try { next = McpConfiguration.Parse(ReadText()); }
        catch (Exception problem) when (problem is McpConfigurationException or IOException or UnauthorizedAccessException)
        {
            next = McpConfiguration.Empty;
            error = problem is McpConfigurationException ? problem.Message : $"mcp.json couldn't be read: {problem.Message}";
        }
        bool apply;
        lock (gate)
        {
            if (disposed) return;
            configuration = next;
            ConfigurationError = error;
            loaded = true;
            apply = started;
        }
        if (apply) Hub.Apply(Servers);
        Changed?.Invoke();
    }

    /// <summary>Validates and saves mcp.json, then applies it. Throws <see cref="McpConfigurationException"/> with what to fix.</summary>
    internal void Save(string json)
    {
        _ = McpConfiguration.Parse(json);
        if (FilePath is null) throw new McpConfigurationException("Martlet has no data folder to save mcp.json in.");
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = FilePath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, json, new UTF8Encoding(false));
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        Reload();
    }

    /// <summary>Changes one server's entry in mcp.json (turning it on or off, or what runs without asking).</summary>
    internal void EditServer(string server, Action<JsonObject> edit) =>
        Save(McpConfiguration.EditServer(ReadText(), server, edit));

    /// <summary>Starts the enabled servers (once). <paramref name="retry"/> also restarts servers that stopped a while ago,
    /// <paramref name="retryNow"/> any stopped server. Replies never retry, so a broken server can't delay each one.</summary>
    internal void EnsureStarted(bool retry = false, bool retryNow = false)
    {
        EnsureLoaded();
        bool first;
        lock (gate)
        {
            if (disposed) return;
            first = !started;
            started = true;
        }
        if (first) Hub.Apply(Servers);
        else if (retry || retryNow) Hub.Retry(retryNow);
    }

    internal static string ModelKey(string routeType, string origin, string model) => $"{routeType}|{origin}|{model}";
    internal bool IsUnsupported(string modelKey) { lock (gate) return unsupportedModels.Contains(modelKey); }
    internal void MarkUnsupported(string modelKey)
    {
        lock (gate) unsupportedModels.Add(modelKey);
        Changed?.Invoke();
    }

    /// <summary>The tools of every running server, for one reply. Waits briefly for servers that are still starting.</summary>
    internal async Task<DesktopToolset?> PrepareAsync(CancellationToken token)
    {
        if (!HasEnabledServers) return null;
        EnsureStarted();
        var statuses = await Hub.WaitForStartupAsync(StartupWait, token).ConfigureAwait(false);
        return DesktopToolset.Build(this, statuses.Where(s => s.State == McpServerState.Ready && !s.Definition.Disabled));
    }

    internal bool AutoApproves(McpServerDefinition server, string tool)
    {
        var current = Servers.FirstOrDefault(s => s.Name == server.Name) ?? server;
        return current.AutoApproves(tool);
    }

    internal async Task<ToolApprovalChoice> RequestApprovalAsync(string server, string tool, string arguments, CancellationToken token,
        bool allowAlways = true)
    {
        var request = new ToolApprovalRequest(server, tool, arguments, clock.GetUtcNow() + ApprovalTimeout, allowAlways);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            // Calls run one at a time; a stale request from a stopped reply is declined.
            pending?.Resolve(ToolApprovalChoice.Deny);
            pending = request;
        }
        Changed?.Invoke();
        try
        {
            var expired = Task.Delay(ApprovalTimeout, clock, token);
            var first = await Task.WhenAny(request.Answer, expired).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var choice = first == request.Answer ? await request.Answer.ConfigureAwait(false) : ToolApprovalChoice.Deny;
            return choice == ToolApprovalChoice.AlwaysAllow && !allowAlways ? ToolApprovalChoice.AllowOnce : choice;
        }
        finally
        {
            request.Resolve(ToolApprovalChoice.Deny);
            lock (gate) if (ReferenceEquals(pending, request)) pending = null;
            Changed?.Invoke();
        }
    }

    internal void Answer(ToolApprovalRequest request, ToolApprovalChoice choice) => request.Resolve(choice);

    /// <summary>Saves "always allow" for one tool in mcp.json's autoApprove list.</summary>
    internal string? AlwaysAllow(string server, string tool)
    {
        try
        {
            EditServer(server, entry => McpConfiguration.AddAutoApproveEntry(entry, tool));
            return null;
        }
        catch (Exception error) when (error is McpConfigurationException or IOException or UnauthorizedAccessException)
        {
            return error.Message;
        }
    }

    internal void Record(string server, string tool, string outcome, string arguments, bool problem)
    {
        lock (gate)
        {
            log.AddFirst(new ToolActivity(clock.GetUtcNow(), server, tool, outcome, arguments, problem));
            while (log.Count > LogLength) log.RemoveLast();
        }
        Changed?.Invoke();
    }

    internal void ClearLog()
    {
        lock (gate) log.Clear();
        Changed?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            pending?.Resolve(ToolApprovalChoice.Deny);
            pending = null;
        }
        await Hub.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>The tools offered to one reply and how to run them. Tool names are made safe for function calling and unique
/// across servers (prefixed with the server's name when two servers share one).</summary>
internal sealed class DesktopToolset : IConversationToolHost
{
    private readonly McpToolService service;
    private readonly IReadOnlyDictionary<string, (McpServerDefinition Server, McpTool Tool)> map;

    private DesktopToolset(McpToolService service, IReadOnlyList<TextToolDefinition> definitions,
        IReadOnlyDictionary<string, (McpServerDefinition, McpTool)> map, int skipped, IReadOnlyList<string> servers)
    {
        this.service = service;
        this.map = map;
        Definitions = definitions;
        Skipped = skipped;
        Servers = servers;
    }

    internal IReadOnlyList<TextToolDefinition> Definitions { get; }
    /// <summary>Tools left out because their schema was unusable or the tool budget was full.</summary>
    internal int Skipped { get; }
    internal IReadOnlyList<string> Servers { get; }

    internal static DesktopToolset? Build(McpToolService service, IEnumerable<McpServerStatus> ready)
    {
        var servers = ready.ToArray();
        var counts = servers.SelectMany(s => s.Tools.Select(t => SafeName(t.Name)))
            .GroupBy(n => n, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var definitions = new List<TextToolDefinition>();
        var map = new Dictionary<string, (McpServerDefinition, McpTool)>(StringComparer.Ordinal);
        int bytes = 0, skipped = 0;
        foreach (var status in servers)
            foreach (var tool in status.Tools)
            {
                var name = SafeName(tool.Name);
                if (counts[name] > 1 || map.ContainsKey(name)) name = SafeName(status.Name + "_" + tool.Name);
                if (map.ContainsKey(name)) name = SafeName(name + "_" + Hash(status.Name + "/" + tool.Name));
                TextToolDefinition definition;
                try { definition = new(name, Describe(tool), Schema(tool.InputSchema)); }
                catch (Martlet.Core.Contracts.ContractException) { skipped++; continue; }
                if (definitions.Count >= BoundedTextInput.HardMaxTools || bytes + definition.Utf8Bytes > BoundedTextInput.HardMaxToolDefinitionBytes)
                {
                    skipped++;
                    continue;
                }
                bytes += definition.Utf8Bytes;
                definitions.Add(definition);
                map[name] = (status.Definition, tool);
            }
        return definitions.Count == 0 ? null : new(service, definitions, map, skipped,
            map.Values.Select(v => v.Item1.Name).Distinct().ToArray());
    }

    public async ValueTask<ConversationToolResult> CallAsync(TextToolCall call, CancellationToken token)
    {
        if (!map.TryGetValue(call.Name, out var target))
        {
            service.Record("?", call.Name, "unknown tool", "", true);
            return new($"There is no tool named {call.Name}. Use only the tools you were given.", true);
        }
        var (server, tool) = target;
        JsonObject? arguments;
        try { arguments = JsonNode.Parse(call.ArgumentsJson) as JsonObject; }
        catch (JsonException) { arguments = null; }
        if (arguments is null)
        {
            service.Record(server.Name, tool.Name, "invalid arguments", Preview(call.ArgumentsJson), true);
            return new("The arguments must be one JSON object that matches the tool's parameters.", true);
        }
        var shown = Readable(arguments);
        var policy = service.PolicyFor(server.Name, tool.Name, arguments);
        if (policy == ToolApprovalPolicy.Deny)
        {
            service.Record(server.Name, tool.Name, $"blocked by {server.ManagedBy ?? "your settings"}", Preview(shown), false);
            return new("This action is blocked by the user's settings. Tell the user it's blocked and where to allow it; don't retry.", true);
        }
        var ask = policy switch
        {
            ToolApprovalPolicy.AutoApprove => false,
            ToolApprovalPolicy.AskEveryTime => true,
            _ => !service.AutoApproves(server, tool.Name)
        };
        if (ask)
        {
            var allowAlways = policy == ToolApprovalPolicy.Default && server.ManagedBy is null;
            var choice = await service.RequestApprovalAsync(server.Name, tool.Name, shown, token, allowAlways).ConfigureAwait(false);
            if (choice == ToolApprovalChoice.Deny)
            {
                service.Record(server.Name, tool.Name, "declined", Preview(shown), false);
                return new("The user declined this tool call (or didn't answer in time). Don't call it again unless they ask; answer without it.", true);
            }
            if (choice == ToolApprovalChoice.AlwaysAllow && service.AlwaysAllow(server.Name, tool.Name) is { } problem)
                service.Record(server.Name, tool.Name, $"allowed once (couldn't save always allow: {problem})", Preview(shown), true);
        }
        try
        {
            var result = await service.Hub.CallAsync(server.Name, tool.Name, arguments, McpToolService.CallTimeout, token).ConfigureAwait(false);
            service.Record(server.Name, tool.Name, result.IsError ? "returned an error" : "ran", Preview(shown), result.IsError);
            return new(result.Text, result.IsError);
        }
        catch (McpException error)
        {
            service.Record(server.Name, tool.Name, "failed: " + error.Message, Preview(shown), true);
            return new($"The tool failed: {error.Message}", true);
        }
    }

    internal static string SafeName(string name)
    {
        var safe = new string(name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_').ToArray()).Trim('_');
        if (safe.Length == 0) safe = "tool";
        return safe.Length <= TextToolDefinition.MaxNameLength ? safe : safe[..(TextToolDefinition.MaxNameLength - 9)] + "_" + Hash(name);
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..8].ToLowerInvariant();

    private static string Describe(McpTool tool)
    {
        var text = tool.Description ?? tool.Title ?? tool.Name;
        text = new string(text.Select(c => char.IsControl(c) && c is not '\n' and not '\t' ? ' ' : c).ToArray()).Trim();
        return text.Length <= TextToolDefinition.MaxDescriptionCharacters ? text : text[..(TextToolDefinition.MaxDescriptionCharacters - 3)] + "...";
    }

    // Providers expect an object schema with properties; "$schema" only costs tokens.
    private static string Schema(JsonObject declared)
    {
        var schema = (JsonObject)declared.DeepClone();
        schema.Remove("$schema");
        if (schema["type"] is null) schema["type"] = "object";
        if (schema["properties"] is null) schema["properties"] = new JsonObject();
        return schema.ToJsonString();
    }

    private static string Readable(JsonObject arguments)
    {
        var text = arguments.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        return text.Length <= 1200 ? text : text[..1200] + "\n...";
    }

    private static string Preview(string text)
    {
        var flat = string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()));
        return flat.Length <= 160 ? flat : flat[..157] + "...";
    }

    public override string ToString() => $"{nameof(DesktopToolset)} ({Definitions.Count} tools)";
}
