using Martlet.Core.Logs;

namespace Martlet.Gateway;

/// <summary>The health of one of the gateway's shared stores (API keys, accounts, memories, ...). <see cref="Failing"/>
/// names the action (load, save, remove) that failed last and has not worked since; null when the store is healthy.</summary>
public sealed record GatewayStoreStatus(string Store, string? Failing, long Failures, DateTimeOffset? LastFailureAt, string? LastFailure);

/// <summary>The one place where the gateway's shared stores load, save and remove their files. A failure never stops the
/// store (it keeps its copy in memory and desktops send it again), but it is written to the host log (once a minute for each
/// store and action) and kept for the security audit, so a lost save is visible.</summary>
internal sealed class GatewayStoreHealth(TimeProvider? clock = null, Action<string, string, string>? log = null)
{
    private const int MaximumStores = 256;
    private readonly object gate = new();
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private readonly Dictionary<string, Entry> stores = new(StringComparer.Ordinal);

    /// <summary>Runs one storage call of <paramref name="store"/>. Tries it <paramref name="attempts"/> times; returns false
    /// (and records and logs the failure) when every try failed.</summary>
    internal bool Try(string store, string action, Action operation, int attempts = 1)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                operation();
                Succeeded(store, action);
                return true;
            }
            catch (Exception error) when (attempt >= attempts)
            {
                Failed(store, action, error);
                return false;
            }
            catch (Exception)
            {
                // Try again; only the last failure is reported.
            }
        }
    }

    internal void Succeeded(string store, string action)
    {
        lock (gate)
            if (stores.TryGetValue(store, out var entry) && entry.Failing == action)
                stores[store] = entry with { Failing = null };
    }

    /// <summary>Records a failed storage call. <paramref name="logged"/> is true when the caller already wrote its own log line.</summary>
    internal void Failed(string store, string action, Exception error, bool logged = false)
    {
        var now = clock.GetUtcNow();
        var reason = $"{error.GetType().Name}: {error.Message}";
        if (reason.Length > 300) reason = reason[..300];
        lock (gate)
        {
            stores.TryGetValue(store, out var entry);
            if (entry is null && stores.Count >= MaximumStores)
                stores.Remove(stores.OrderBy(item => item.Value.LastFailureAt).First().Key);
            stores[store] = new(action, (entry?.Failures ?? 0) + 1, now, reason);
        }
        if (logged) return;
        var level = action == "load" ? LogLevels.Warn : LogLevels.Error;
        var what = action == "load"
            ? "it starts from what it has in memory, and the next desktop that syncs sends it again"
            : "it keeps the change in memory only until the gateway restarts, and desktops send it again";
        try { log?.Invoke(level, $"Could not {action} the {store} store on this host ({reason}); {what}.", $"store:{store}:{action}"); }
        catch (Exception) { } // A log that cannot be written must not hide the store's own failure from the caller.
    }

    /// <summary>The stores that failed since the gateway started, by name; empty when every load and save worked.</summary>
    internal IReadOnlyList<GatewayStoreStatus> Snapshot()
    {
        lock (gate)
            return stores.OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => new GatewayStoreStatus(item.Key, item.Value.Failing, item.Value.Failures, item.Value.LastFailureAt, item.Value.LastFailure))
                .ToArray();
    }

    /// <summary>True when no store has a failure that has not worked since.</summary>
    internal bool Healthy
    {
        get
        {
            lock (gate)
                return stores.Values.All(entry => entry.Failing is null);
        }
    }

    private sealed record Entry(string? Failing, long Failures, DateTimeOffset? LastFailureAt, string? LastFailure);
}
