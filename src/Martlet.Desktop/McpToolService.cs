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

/// <summary>A tool call waiting for the user's answer in the talk window. Unanswered requests are declined. <paramref name="title"/>
/// and <paramref name="question"/> replace the default "Allow this tool?" heading and "Allow tool from server?" line (the
/// terminal asks to run its command), and <paramref name="label"/> is what the talk window's status line calls the call.</summary>
internal sealed class ToolApprovalRequest(string server, string tool, string arguments, DateTimeOffset expires, bool allowAlways = true,
    string? title = null, string? question = null, string? label = null)
{
    private readonly TaskCompletionSource<ToolApprovalChoice> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal string Server { get; } = server;
    internal string Tool { get; } = tool;
    /// <summary>The arguments as indented JSON, cut to a readable length.</summary>
    internal string Arguments { get; } = arguments;
    internal DateTimeOffset Expires { get; } = expires;
    /// <summary>Whether Always allow is offered (never for managed servers or calls their owner wants asked every time).</summary>
    internal bool AllowAlways { get; } = allowAlways;
    internal string Title { get; } = title ?? "Allow this tool?";
    internal string Question { get; } = question ?? $"Allow {tool} from {server}? It can use this PC with your permissions.";
    internal string Label { get; } = label ?? tool;
    internal Task<ToolApprovalChoice> Answer => answer.Task;
    internal void Resolve(ToolApprovalChoice choice) => answer.TrySetResult(choice);
    public override string ToString() => $"{nameof(ToolApprovalRequest)} {Server}/{Tool}";
}

/// <summary>One entry of the in-memory tool log on the Tools page (never written to disk).</summary>
internal sealed record ToolActivity(DateTimeOffset At, string Server, string Tool, string Outcome, string Arguments, bool Problem);

/// <summary>Tools Martlet itself gives a reply (think_longer and cancel_thinking while Thinking longer is on): each definition
/// and how to run a call, and the prompt added to the reply's instructions after the tools prompt. They come first, always in
/// the same order, so the start of every request stays the same.</summary>
internal sealed record BuiltInTools(IReadOnlyList<(TextToolDefinition Definition,
    Func<TextToolCall, CancellationToken, ValueTask<ConversationToolResult>> Call)> Tools, string? Guidance);

/// <summary>The MCP servers Martlet may call while you talk: the user's mcp.json in the data directory plus servers other
/// Martlet features manage (<see cref="SetManagedServer"/>), one session per enabled server (started only when a
/// conversation or the Tools page needs them), Martlet's own terminal when it is turned on (<see cref="Terminal"/>, off by
/// default), per-call confirmations and a short in-memory log. Tools are offered only to user-started replies, never to screen
/// glances or memory requests.</summary>
internal sealed class McpToolService : IAsyncDisposable
{
    internal const string FileName = "mcp.json";
    /// <summary>How the terminal appears in confirmations and the tool log.</summary>
    internal const string TerminalName = "Terminal";
    internal const int LogLength = 40;
    internal static TimeSpan ApprovalTimeout => TimeSpan.FromSeconds(60);
    internal static TimeSpan CallTimeout => TimeSpan.FromSeconds(60);
    internal static TimeSpan StartupWait => TimeSpan.FromSeconds(10);
    private readonly object gate = new();
    private readonly LinkedList<ToolActivity> log = new();
    private readonly Dictionary<string, DateTimeOffset> unsupportedModels = new(StringComparer.Ordinal);
    private readonly TimeProvider clock;
    private readonly string? dataDirectory;
    private McpConfiguration configuration = McpConfiguration.Empty;
    private TerminalSettings terminal;
    private readonly Dictionary<string, (McpServerDefinition Definition, Func<string, JsonObject, ToolApprovalPolicy>? Policy)> managed =
        new(StringComparer.Ordinal);
    private ToolApprovalRequest? pending;
    private bool loaded, started, disposed;

    internal McpToolService(string? dataDirectory, TimeProvider? clock = null, IMcpSecretStore? secrets = null)
    {
        this.clock = clock ?? TimeProvider.System;
        this.dataDirectory = dataDirectory;
        FilePath = dataDirectory is null ? null : Path.Combine(dataDirectory, FileName);
        Secrets = secrets ?? (FilePath is null || !OperatingSystem.IsWindows() ? null : new WindowsMcpSecrets(FilePath));
        terminal = TerminalSettings.Load(dataDirectory);
        var now = this.clock.GetUtcNow();
        foreach (var (key, at) in LoadUnsupported(dataDirectory))
            if (now - at < UnsupportedMemory) unsupportedModels[key] = at;
        Hub = new("martlet", AppVersions.Current, this.clock);
        Hub.Changed += () => Changed?.Invoke();
    }

    /// <summary>The Thinking models on this PC that rejected tools (<see cref="MarkUnsupported"/>) and when, kept for
    /// <see cref="UnsupportedMemory"/> across restarts, so a model without function calling isn't asked with tools (and then
    /// again without) on the first reply of every session, now that think_longer offers tools by default.</summary>
    internal const string UnsupportedFileName = "tools-unsupported.json";
    internal static TimeSpan UnsupportedMemory => TimeSpan.FromDays(7);

    private static Dictionary<string, DateTimeOffset> LoadUnsupported(string? directory)
    {
        if (directory is null) return [];
        try
        {
            var path = Path.Combine(directory, UnsupportedFileName);
            if (!File.Exists(path) || new FileInfo(path).Length > 65_536) return [];
            return (JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(File.ReadAllText(path)) ?? [])
                .Where(entry => entry.Key is { Length: > 0 and <= 512 }).Take(64).ToDictionary(StringComparer.Ordinal);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return []; }
    }

    private void SaveUnsupported()
    {
        if (dataDirectory is null) return;
        Dictionary<string, DateTimeOffset> kept;
        lock (gate) kept = unsupportedModels.OrderByDescending(entry => entry.Value).Take(64).ToDictionary(StringComparer.Ordinal);
        try { File.WriteAllText(Path.Combine(dataDirectory, UnsupportedFileName), JsonSerializer.Serialize(kept)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Warn("Couldn't save which Thinking models reject tools: " + error.Message);
        }
    }

    internal string? FilePath { get; }
    /// <summary>Where ${secret:NAME} values for this mcp.json are kept (Windows Credential Manager), or null.</summary>
    internal IMcpSecretStore? Secrets { get; }
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

    /// <summary>Whether replies may get tools: an enabled server or the terminal.</summary>
    internal bool HasTools => Terminal.Enabled || HasEnabledServers;

    /// <summary>Companion › Tools › Terminal on this PC (terminal.json; off by default).</summary>
    internal TerminalSettings Terminal { get { lock (gate) return terminal; } }

    /// <summary>Saves the terminal settings; false (and nothing changes) when the data folder can't be written. A reply that
    /// is already running uses the new settings for its next command.</summary>
    internal bool SetTerminal(TerminalSettings next)
    {
        ArgumentNullException.ThrowIfNull(next);
        next = next.Normalized();
        lock (gate)
        {
            if (disposed) return false;
            if (terminal == next) return true;
        }
        if (!next.Save(dataDirectory)) return false;
        lock (gate) terminal = next;
        ErrorLog.Info($"Terminal settings: {(next.Enabled ? "on" : "off")}, {TerminalRunner.Name(next.Shell)}, " +
            $"{(next.AskFirst ? "asks before every command" : "runs commands without asking")}, {next.TimeLimitSeconds} s limit, " +
            $"{(next.CustomFolder ? "a chosen start folder" : "starts in the home folder")}.");
        Changed?.Invoke();
        return true;
    }

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
        if (info.Length > McpConfiguration.MaxFileBytes) throw new McpConfigurationException("MCP server settings are larger than 1 MB.");
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
        try { next = McpConfiguration.Parse(ReadText(), secrets: Secrets is { } store ? store.Read : null); }
        catch (Exception problem) when (problem is McpConfigurationException or IOException or UnauthorizedAccessException)
        {
            next = McpConfiguration.Empty;
            error = problem is McpConfigurationException ? problem.Message : $"MCP server settings couldn't be read: {problem.Message}";
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
        if (FilePath is null) throw new McpConfigurationException("Martlet has no data folder to save MCP server settings.");
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

    /// <summary>Adds a server from the MCP directory as <paramref name="name"/> (replacing one of that name only when
    /// <paramref name="replace"/> is set): its secrets go to Windows Credential Manager first, then mcp.json is saved and the
    /// servers start. Secrets a replaced entry used that nothing uses any more are forgotten.</summary>
    internal void Install(string name, McpInstallPlan plan, bool replace)
    {
        var before = ReadText();
        var replaced = replace ? McpConfiguration.FindServer(before, name) : null;
        var json = McpConfiguration.AddServer(before, name, plan.Entry, replace);
        _ = McpConfiguration.Parse(json);
        if (plan.Secrets.Count > 0 && Secrets is null)
            throw new McpConfigurationException("Martlet can't keep secrets on this PC; type ${env:NAME} in secret fields instead.");
        var kept = McpConfiguration.SecretNames(before).ToHashSet(StringComparer.Ordinal);
        var written = new List<string>();
        try
        {
            foreach (var (secret, value) in plan.Secrets)
            {
                if (Secrets!.Write(secret, value) is { } problem)
                    throw new McpConfigurationException($"Windows Credential Manager couldn't save {secret} ({problem}). Nothing was installed.");
                written.Add(secret);
            }
            Save(json);
        }
        catch
        {
            foreach (var secret in written.Where(s => !kept.Contains(s))) Secrets?.Delete(secret);
            throw;
        }
        Forget(McpConfiguration.SecretNames(replaced), json);
        EnsureStarted(retryNow: true);
    }

    /// <summary>Removes a server from mcp.json and forgets the secrets only it used.</summary>
    internal void Remove(string name)
    {
        var (json, removed) = McpConfiguration.RemoveServer(ReadText(), name);
        Save(json);
        Forget(McpConfiguration.SecretNames(removed), json);
    }

    private void Forget(IEnumerable<string> secrets, string json)
    {
        var used = McpConfiguration.SecretNames(json).ToHashSet(StringComparer.Ordinal);
        foreach (var secret in secrets.Where(s => !used.Contains(s))) Secrets?.Delete(secret);
    }

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
    internal bool IsUnsupported(string modelKey) { lock (gate) return unsupportedModels.ContainsKey(modelKey); }
    internal void MarkUnsupported(string modelKey)
    {
        bool added;
        lock (gate) added = unsupportedModels.TryAdd(modelKey, clock.GetUtcNow());
        if (added) SaveUnsupported();
        Changed?.Invoke();
    }

    /// <summary>The tools of every running server, the terminal when it is on and Martlet's own <paramref name="builtIns"/>, for
    /// one reply. Waits briefly for servers that are still starting; the terminal and built-in tools alone need no wait.</summary>
    internal async Task<DesktopToolset?> PrepareAsync(CancellationToken token, BuiltInTools? builtIns = null)
    {
        var shell = Terminal is { Enabled: true } on ? on : null;
        IReadOnlyList<McpServerStatus> ready = [];
        if (HasEnabledServers)
        {
            EnsureStarted();
            var statuses = await Hub.WaitForStartupAsync(StartupWait, token).ConfigureAwait(false);
            ready = [.. statuses.Where(s => s.State == McpServerState.Ready && !s.Definition.Disabled)];
        }
        else if (shell is null && builtIns is null) return null;
        return DesktopToolset.Build(this, ready, shell, builtIns);
    }

    internal bool AutoApproves(McpServerDefinition server, string tool)
    {
        var current = Servers.FirstOrDefault(s => s.Name == server.Name) ?? server;
        return current.AutoApproves(tool);
    }

    internal async Task<ToolApprovalChoice> RequestApprovalAsync(string server, string tool, string arguments, CancellationToken token,
        bool allowAlways = true, string? title = null, string? question = null, string? label = null)
    {
        var request = new ToolApprovalRequest(server, tool, arguments, clock.GetUtcNow() + ApprovalTimeout, allowAlways, title, question, label);
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

/// <summary>Where the ${secret:NAME} values of one mcp.json are kept.</summary>
internal interface IMcpSecretStore
{
    string? Read(string name);
    /// <summary>Saves a value; returns why it couldn't, or null.</summary>
    string? Write(string name, string value);
    void Delete(string name);
}

/// <summary>Windows Credential Manager, scoped to one mcp.json file so another data folder never sees its secrets.</summary>
internal sealed class WindowsMcpSecrets(string filePath) : IMcpSecretStore
{
    private readonly Martlet.Credentials.Windows.WindowsCredentialStore vault = new();
    private readonly string scope = Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(filePath).ToUpperInvariant())))[..16];

    public string? Read(string name) =>
        vault.ReadMcpSecret(scope, name, out var value) == Martlet.Core.Settings.CredentialError.None ? value : null;

    public string? Write(string name, string value) =>
        vault.WriteMcpSecret(scope, name, value) is var error && error == Martlet.Core.Settings.CredentialError.None ? null : error.ToString();

    public void Delete(string name) => vault.DeleteMcpSecret(scope, name);
}

/// <summary>The tools offered to one reply and how to run them. Tool names are made safe for function calling and unique
/// across servers (prefixed with the server's name when two servers share one). Martlet's own tools come first, then the
/// terminal when it is on; both keep their names (<see cref="ThinkLonger.Name"/>, <see cref="TerminalTool.Name"/>).</summary>
internal sealed class DesktopToolset : IConversationToolHost
{
    private readonly McpToolService service;
    private readonly IReadOnlyDictionary<string, (McpServerDefinition Server, McpTool Tool)> map;
    private readonly bool terminal;
    private readonly IReadOnlyDictionary<string, Func<TextToolCall, CancellationToken, ValueTask<ConversationToolResult>>> builtIn;

    private DesktopToolset(McpToolService service, IReadOnlyList<TextToolDefinition> definitions,
        IReadOnlyDictionary<string, (McpServerDefinition, McpTool)> map, int skipped, IReadOnlyList<string> servers, bool terminal,
        IReadOnlyDictionary<string, Func<TextToolCall, CancellationToken, ValueTask<ConversationToolResult>>> builtIn, string? guidance)
    {
        this.service = service;
        this.map = map;
        this.terminal = terminal;
        this.builtIn = builtIn;
        Definitions = definitions;
        Skipped = skipped;
        Servers = servers;
        Guidance = guidance;
    }

    internal IReadOnlyList<TextToolDefinition> Definitions { get; }
    /// <summary>Tools left out because their schema was unusable or the tool budget was full.</summary>
    internal int Skipped { get; }
    internal IReadOnlyList<string> Servers { get; }
    /// <summary>What Martlet's own tools add to the reply's instructions (Companion › Prompts › Thinking longer), or null.</summary>
    internal string? Guidance { get; }
    /// <summary>Whether Martlet's own tool <paramref name="name"/> is offered.</summary>
    internal bool Offers(string name) => builtIn.ContainsKey(name);

    internal static DesktopToolset? Build(McpToolService service, IEnumerable<McpServerStatus> ready, TerminalSettings? terminal = null,
        BuiltInTools? builtIns = null)
    {
        var servers = ready.ToArray();
        var counts = servers.SelectMany(s => s.Tools.Select(t => SafeName(t.Name)))
            .GroupBy(n => n, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var definitions = new List<TextToolDefinition>();
        var map = new Dictionary<string, (McpServerDefinition, McpTool)>(StringComparer.Ordinal);
        var own = new Dictionary<string, Func<TextToolCall, CancellationToken, ValueTask<ConversationToolResult>>>(StringComparer.Ordinal);
        int bytes = 0, skipped = 0;
        foreach (var (definition, call) in builtIns?.Tools ?? [])
        {
            bytes += definition.Utf8Bytes;
            definitions.Add(definition);
            own[definition.Name] = call;
        }
        if (terminal is not null)
        {
            var shell = new TextToolDefinition(TerminalTool.Name, TerminalTool.Description(terminal), TerminalTool.ParametersJson);
            bytes += shell.Utf8Bytes;
            definitions.Add(shell);
        }
        bool Taken(string name) => map.ContainsKey(name) || own.ContainsKey(name) || terminal is not null && name == TerminalTool.Name;
        foreach (var status in servers)
            foreach (var tool in status.Tools)
            {
                var name = SafeName(tool.Name);
                if (counts[name] > 1 || Taken(name)) name = SafeName(status.Name + "_" + tool.Name);
                if (Taken(name)) name = SafeName(name + "_" + Hash(status.Name + "/" + tool.Name));
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
            map.Values.Select(v => v.Item1.Name).Distinct().ToArray(), terminal is not null, own, own.Count > 0 ? builtIns?.Guidance : null);
    }

    public async ValueTask<ConversationToolResult> CallAsync(TextToolCall call, CancellationToken token)
    {
        if (builtIn.TryGetValue(call.Name, out var own)) return await own(call, token).ConfigureAwait(false);
        if (terminal && call.Name == TerminalTool.Name) return await RunCommandAsync(call, token).ConfigureAwait(false);
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

    /// <summary>One terminal command: with the terminal's settings as they are now (turning it off or asking first applies to a
    /// reply already running), asked first unless the user turned that off (Allow once or Deny; never Always allow), then run
    /// hidden. The desktop log notes each run without the command or its output.</summary>
    private async ValueTask<ConversationToolResult> RunCommandAsync(TextToolCall call, CancellationToken token)
    {
        const string server = McpToolService.TerminalName, tool = "command";
        string? command = null;
        try
        {
            if (JsonNode.Parse(call.ArgumentsJson) is JsonObject arguments && arguments["command"] is JsonValue value &&
                value.TryGetValue<string>(out var text))
                command = text;
        }
        catch (JsonException) { }
        if (command is null)
        {
            service.Record(server, tool, "invalid arguments", Preview(call.ArgumentsJson), true);
            return new("Pass one JSON object with the command as a string, like {\"command\": \"Get-Date\"}.", true);
        }
        var settings = service.Terminal;
        if (!settings.Enabled)
        {
            service.Record(server, tool, "blocked: the terminal is off", Preview(command), false);
            return new("The user turned the terminal off in Companion › Tools. Tell them it's off; don't retry.", true);
        }
        if (TerminalRunner.Check(settings.Shell, command) is { } invalid)
        {
            service.Record(server, tool, "not run: " + invalid, Preview(command), true);
            return new(invalid, true);
        }
        var shell = TerminalRunner.Name(settings.Shell);
        if (settings.AskFirst)
        {
            var shown = (command.Length <= 1200 ? command : command[..1200] + "\n...") + "\n\nStarts in " + settings.StartFolder;
            var choice = await service.RequestApprovalAsync(server, tool, shown, token, allowAlways: false, title: "Run this command?",
                question: $"Martlet wants to run this in {shell}, on this PC as you.", label: "the command").ConfigureAwait(false);
            if (choice == ToolApprovalChoice.Deny)
            {
                service.Record(server, tool, "declined", Preview(command), false);
                ErrorLog.Info($"Terminal: a command for {shell} was declined (or not answered in time).");
                return new("The user declined this command (or didn't answer in time). Don't run it again unless they ask; answer without it.", true);
            }
        }
        TerminalRun run;
        try { run = await TerminalRunner.RunAsync(settings, command, token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            service.Record(server, tool, "stopped with the reply", Preview(command), true);
            ErrorLog.Info($"Terminal: a {shell} command was stopped because the reply stopped.");
            throw;
        }
        var outcome = run.Problem is { } problem ? "couldn't run: " + problem
            : run.TimedOut ? $"stopped at the {settings.TimeLimitSeconds} s limit" : $"ran, exit code {run.ExitCode}";
        service.Record(server, tool, outcome, Preview(command), !run.Succeeded);
        ErrorLog.Info(run.Problem is not null ? $"Terminal: a {shell} command couldn't run: {run.Problem}"
            : $"Terminal: ran a {shell} command{(settings.AskFirst ? " you allowed" : "")} " +
              $"({(run.TimedOut ? $"stopped at the {settings.TimeLimitSeconds} s limit" : $"exit code {run.ExitCode}")}, " +
              $"{run.Elapsed.TotalSeconds:0.0} s, {run.Output.Length} characters of output{(run.Cut ? ", cut" : "")}).");
        return new(TerminalRunner.Report(run, settings), !run.Succeeded);
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
