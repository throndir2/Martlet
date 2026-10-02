using System.Text.Json.Nodes;

namespace Martlet.Mcp.Client;

public enum McpServerState { Disabled, Starting, Ready, Failed }

/// <summary>A server's current state. <see cref="Diagnostics"/> is its recent error output, for troubleshooting.</summary>
public sealed record McpServerStatus(McpServerDefinition Definition, McpServerState State, IReadOnlyList<McpTool> Tools,
    string? Error, string? Diagnostics, string? ServerName, DateTimeOffset? Since)
{
    public string Name => Definition.Name;
}

/// <summary>Keeps one session per enabled MCP server: starts them in the background, restarts them when their launch
/// settings change, and stops them on dispose. Starting is never implicit at construction.</summary>
public sealed class McpHub(string clientName, string clientVersion, TimeProvider? clock = null) : IAsyncDisposable
{
    private sealed class Entry(McpServerDefinition definition)
    {
        public McpServerDefinition Definition = definition;
        public McpServerState State = definition.Disabled ? McpServerState.Disabled : McpServerState.Starting;
        public McpClient? Client;
        public IReadOnlyList<McpTool> Tools = [];
        public string? Error, Diagnostics;
        public Task Starting = Task.CompletedTask;
        public DateTimeOffset? Since;
        public readonly CancellationTokenSource Life = new();
    }

    private readonly object gate = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private bool disposed;

    /// <summary>How long a server may take to start; npx's first run downloads the package.</summary>
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(90);
    public TimeSpan RetryAfter { get; init; } = TimeSpan.FromSeconds(20);
    /// <summary>Raised (on any thread) whenever a server's state or tools change.</summary>
    public event Action? Changed;

    public IReadOnlyList<McpServerStatus> Status
    {
        get
        {
            lock (gate)
                return entries.Values.Select(e => new McpServerStatus(e.Definition, e.State, e.Tools, e.Error,
                    e.Diagnostics ?? e.Client?.Diagnostics, e.Client?.ServerName, e.Since)).ToArray();
        }
    }

    /// <summary>Makes the running set match <paramref name="servers"/>: new or changed enabled servers start, removed,
    /// disabled or changed ones stop. Changing only approvals keeps a server running.</summary>
    public void Apply(IEnumerable<McpServerDefinition> servers)
    {
        var stop = new List<Entry>();
        var start = new List<Entry>();
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var wanted = servers.ToDictionary(s => s.Name, StringComparer.Ordinal);
            foreach (var (name, entry) in entries.ToArray())
            {
                if (wanted.TryGetValue(name, out var next) && next.Disabled == entry.Definition.Disabled &&
                    next.LaunchFingerprint == entry.Definition.LaunchFingerprint)
                {
                    entry.Definition = next;
                    continue;
                }
                entries.Remove(name);
                stop.Add(entry);
            }
            foreach (var definition in wanted.Values.Where(d => !entries.ContainsKey(d.Name)))
            {
                var entry = new Entry(definition);
                entries[definition.Name] = entry;
                if (!definition.Disabled) start.Add(entry);
            }
            foreach (var entry in start) Launch(entry);
        }
        foreach (var entry in stop) _ = StopAsync(entry);
        if (stop.Count + start.Count > 0) Changed?.Invoke();
    }

    /// <summary>Starts servers that failed a while ago again (a server can need a moment, a network or a fix).</summary>
    public void Retry(bool now = false)
    {
        bool any = false;
        lock (gate)
        {
            if (disposed) return;
            foreach (var entry in entries.Values.Where(e => e.State == McpServerState.Failed &&
                (now || e.Since is null || clock.GetUtcNow() - e.Since >= RetryAfter)).ToArray())
            {
                var fresh = new Entry(entry.Definition);
                entries[entry.Definition.Name] = fresh;
                _ = StopAsync(entry);
                Launch(fresh);
                any = true;
            }
        }
        if (any) Changed?.Invoke();
    }

    /// <summary>Stops and starts one server again.</summary>
    public void Restart(string name)
    {
        lock (gate)
        {
            if (disposed || !entries.TryGetValue(name, out var entry) || entry.Definition.Disabled) return;
            var fresh = new Entry(entry.Definition);
            entries[name] = fresh;
            _ = StopAsync(entry);
            Launch(fresh);
        }
        Changed?.Invoke();
    }

    /// <summary>Waits until no server is still starting, or until <paramref name="maximum"/> passes.</summary>
    public async Task<IReadOnlyList<McpServerStatus>> WaitForStartupAsync(TimeSpan maximum, CancellationToken token)
    {
        Task[] starting;
        lock (gate) starting = entries.Values.Where(e => e.State == McpServerState.Starting).Select(e => e.Starting).ToArray();
        if (starting.Length > 0)
            await Task.WhenAny(Task.WhenAll(starting), Task.Delay(maximum, clock, token)).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return Status;
    }

    public async Task<McpToolResult> CallAsync(string server, string tool, JsonObject? arguments, TimeSpan timeout, CancellationToken token)
    {
        Entry? entry;
        McpClient? client;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            entry = entries.GetValueOrDefault(server);
            client = entry?.State == McpServerState.Ready ? entry.Client : null;
        }
        if (entry is null || client is null)
            throw new McpException(entry?.Error is { } error ? $"The {server} server isn't running: {error}" : $"The {server} server isn't running.");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token, entry.Life.Token);
        limit.CancelAfter(timeout);
        try { return await client.CallToolAsync(tool, arguments, limit.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new McpException(entry.Life.IsCancellationRequested
                ? $"The {server} server was stopped."
                : $"{tool} didn't finish within {timeout.TotalSeconds:0} seconds.");
        }
    }

    // Caller holds gate.
    private void Launch(Entry entry)
    {
        entry.State = McpServerState.Starting;
        entry.Since = clock.GetUtcNow();
        entry.Starting = Task.Run(() => StartAsync(entry));
    }

    private async Task StartAsync(Entry entry)
    {
        McpClient? client = null;
        try
        {
            client = await McpClient.ConnectAsync(entry.Definition, clientName, clientVersion, StartTimeout, entry.Life.Token)
                .ConfigureAwait(false);
            using var listing = CancellationTokenSource.CreateLinkedTokenSource(entry.Life.Token);
            listing.CancelAfter(TimeSpan.FromSeconds(30));
            var tools = await client.ListToolsAsync(listing.Token).ConfigureAwait(false);
            lock (gate)
            {
                if (entry.Life.IsCancellationRequested) throw new OperationCanceledException(entry.Life.Token);
                entry.Client = client;
                entry.Tools = tools;
                entry.State = McpServerState.Ready;
                entry.Error = entry.Diagnostics = null;
                entry.Since = clock.GetUtcNow();
            }
            client.ToolsChanged += () => _ = RefreshToolsAsync(entry, client);
            _ = WatchAsync(entry, client);
            client = null;
        }
        catch (OperationCanceledException) when (entry.Life.IsCancellationRequested) { }
        catch (Exception error)
        {
            lock (gate)
            {
                entry.State = McpServerState.Failed;
                entry.Error = error switch
                {
                    McpException mcp => mcp.Message,
                    OperationCanceledException => "The server didn't list its tools in time.",
                    _ => $"{error.GetType().Name}: {error.Message}"
                };
                entry.Diagnostics = (error as McpException)?.Diagnostics ?? client?.Diagnostics;
                entry.Since = clock.GetUtcNow();
            }
        }
        finally
        {
            if (client is not null) await client.DisposeAsync().ConfigureAwait(false);
        }
        Changed?.Invoke();
    }

    private async Task RefreshToolsAsync(Entry entry, McpClient client)
    {
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(entry.Life.Token);
            limit.CancelAfter(TimeSpan.FromSeconds(30));
            var tools = await client.ListToolsAsync(limit.Token).ConfigureAwait(false);
            lock (gate) if (ReferenceEquals(entry.Client, client)) entry.Tools = tools;
            Changed?.Invoke();
        }
        catch (Exception error) when (error is McpException or OperationCanceledException or ObjectDisposedException) { }
    }

    private async Task WatchAsync(Entry entry, McpClient client)
    {
        await client.Completion.ConfigureAwait(false);
        lock (gate)
        {
            if (entry.Life.IsCancellationRequested || !ReferenceEquals(entry.Client, client)) return;
            entry.State = McpServerState.Failed;
            entry.Error = client.StoppedReason;
            entry.Diagnostics = client.Diagnostics;
            entry.Tools = [];
            entry.Since = clock.GetUtcNow();
        }
        Changed?.Invoke();
    }

    private static async Task StopAsync(Entry entry)
    {
        await entry.Life.CancelAsync().ConfigureAwait(false);
        try { await entry.Starting.ConfigureAwait(false); }
        catch (Exception) { }
        var client = Interlocked.Exchange(ref entry.Client, null);
        if (client is not null) await client.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        Entry[] all;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            all = [.. entries.Values];
            entries.Clear();
        }
        await Task.WhenAny(Task.WhenAll(all.Select(StopAsync)), Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
    }
}
