using System.Security.Cryptography;
using System.Text;

namespace Martlet.Core.Creations;

/// <summary>One paired host as the creation sync sees it (the desktop's paired, signed, pinned connection; docs/CREATIONS.md).
/// Every call throws <see cref="CreationHostException"/> when the host can't be used.</summary>
public interface ICreationHost
{
    string HostId { get; }

    /// <summary>The digests of the host's copy of the list and of the pieces it holds.</summary>
    Task<(string Digest, string PresentDigest)> ReadDigestAsync(CancellationToken token);

    Task<(CreationLibrary Library, IReadOnlySet<string> Present)> ReadAsync(CancellationToken token);

    /// <summary>Merges <paramref name="library"/> into the host's copy; returns the merged copy and the pieces it holds.</summary>
    Task<(CreationLibrary Library, IReadOnlySet<string> Present)> MergeAsync(CreationLibrary library, CancellationToken token);

    /// <summary>A piece the host holds, checked against its SHA-256, or null when it has none.</summary>
    Task<byte[]?> ReadChunkAsync(string sha256, CancellationToken token);

    /// <summary>Gives the host a piece of a live creation; returns the pieces it now holds.</summary>
    Task<IReadOnlySet<string>> SendChunkAsync(string sha256, ReadOnlyMemory<byte> data, CancellationToken token);
}

/// <summary>A host that can't be used for creations now: unreachable, refusing, or (<see cref="Old"/>) a Martlet older than
/// creations.</summary>
public sealed class CreationHostException(string message, bool old = false, Exception? inner = null) : Exception(message, inner)
{
    public bool Old { get; } = old;
}

/// <summary>What one <see cref="CreationSync.RunAsync"/> did: the list, assets copied in and files deleted here, creations
/// still copying here, the live creations complete here, pieces sent to hosts, and each host's state.</summary>
public sealed record CreationSyncResult(CreationLibrary Library, int Added, int Removed, int Waiting, IReadOnlySet<string> Local, int Sent,
    IReadOnlyList<CreationHostState> Hosts)
{
    public int Shared => Hosts.Count(h => h.State == CreationSyncState.Shared);

    /// <summary>The sync in a sentence, such as "Creations shared with 2 of 2 computers."</summary>
    public string Describe()
    {
        var old = Hosts.Where(h => h.State == CreationSyncState.Old).Select(h => h.HostId).ToArray();
        return $"Creations shared with {Shared} of {Hosts.Count} computer{(Hosts.Count == 1 ? "" : "s")}." +
            (Waiting > 0 ? $" {Waiting} still copying to this PC." : "") +
            (old.Length > 0 ? $" Update {string.Join(", ", old)} to share creations there." : "");
    }
}

/// <summary>Keeps this computer's creations (<see cref="CreationStore"/>) the same as every paired host's: reads each host's
/// copy (only when its digests changed since the last pass, unless something here still waits), merges them here, copies the
/// assets this computer lacks from hosts that hold them, gives every host whose copy differs the merged list and sends each
/// host every piece it lacks, so a host keeps everything for computers that are off. Records each host's state in
/// <see cref="CreationSyncState"/>.</summary>
public sealed class CreationSync(string dataDirectory)
{
    private readonly Dictionary<string, string> seen = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlySet<string>> presentByHost = new(StringComparer.Ordinal);

    public string DataDirectory { get; } = dataDirectory;

    public async Task<CreationSyncResult> RunAsync(IReadOnlyList<ICreationHost> hosts, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(hosts);
        var before = CreationStore.View(DataDirectory);
        var waitingHere = before.Live.Any(c => !CreationStore.IsComplete(DataDirectory, c));
        var localKey = Key(before);
        var reads = await Task.WhenAll(hosts.Select(async host =>
        {
            try
            {
                var (digest, presentDigest) = await host.ReadDigestAsync(token);
                if (!waitingHere && seen.TryGetValue(host.HostId, out var last) && last == $"{digest}|{presentDigest}|{localKey}")
                    return (Host: host, Copy: ((CreationLibrary Library, IReadOnlySet<string> Present)?)null, State: "skip");
                var copy = await host.ReadAsync(token);
                return (host, copy, CreationSyncState.Shared);
            }
            catch (CreationHostException error) { return (host, null, error.Old ? CreationSyncState.Old : CreationSyncState.Unreachable); }
        }));

        CreationStore.Commit(DataDirectory, reads.Where(r => r.Copy is not null)
            .Aggregate(CreationLibrary.Empty, (merged, r) => CreationLibrary.Merge(merged, r.Copy!.Value.Library)));
        foreach (var (host, copy, _) in reads)
            if (copy is { } value) presentByHost[host.HostId] = value.Present;

        async Task<byte[]?> FetchAsync(string sha256, CancellationToken fetchToken)
        {
            foreach (var (host, _, state) in reads.Where(r => r.State is CreationSyncState.Shared or "skip"))
            {
                if (!presentByHost.TryGetValue(host.HostId, out var present) || !present.Contains(sha256)) continue;
                try { if (await host.ReadChunkAsync(sha256, fetchToken) is { } data) return data; }
                catch (CreationHostException) { }
            }
            return null;
        }
        var reconciled = await CreationStore.ReconcileAsync(DataDirectory, FetchAsync, token);
        var library = reconciled.Library;

        var states = new List<CreationHostState>();
        var sent = 0;
        var now = DateTimeOffset.UtcNow;
        foreach (var (host, copy, state) in reads)
        {
            if (state is CreationSyncState.Old or CreationSyncState.Unreachable)
            {
                seen.Remove(host.HostId);
                states.Add(new() { HostId = host.HostId, State = state, At = now });
                continue;
            }
            var present = presentByHost.GetValueOrDefault(host.HostId) ?? new HashSet<string>(StringComparer.Ordinal);
            try
            {
                if (state == "skip" && Key(library) == localKey)
                {
                    states.Add(new() { HostId = host.HostId, State = CreationSyncState.Shared, At = now, Complete = CreationSyncState.CompleteOn(library, present) });
                    continue;
                }
                if (copy is null || copy.Value.Library.Digest() != library.Digest())
                {
                    var merged = await host.MergeAsync(library, token);
                    library = CreationStore.Commit(DataDirectory, merged.Library);
                    present = merged.Present;
                }
                foreach (var creation in library.Live.Where(c => reconciled.Local.Contains(c.Id)))
                foreach (var sha256 in creation.Assets!.SelectMany(a => a.Chunks).Distinct(StringComparer.Ordinal).Where(s => !present.Contains(s)).ToArray())
                {
                    if (await CreationStore.ReadChunkAsync(DataDirectory, library, sha256, token) is not { } data) continue;
                    present = await host.SendChunkAsync(sha256, data, token);
                    sent++;
                }
                presentByHost[host.HostId] = present;
                seen[host.HostId] = $"{library.Digest()}|{CreationLibrary.PresentDigest(present)}|{Key(library)}";
                states.Add(new() { HostId = host.HostId, State = CreationSyncState.Shared, At = now, Complete = CreationSyncState.CompleteOn(library, present) });
            }
            catch (CreationHostException)
            {
                seen.Remove(host.HostId);
                states.Add(new() { HostId = host.HostId, State = CreationSyncState.Unreachable, At = now });
            }
        }
        var result = new CreationSyncResult(library, reconciled.Added, reconciled.Removed, reconciled.Waiting, reconciled.Local, sent, states);
        try { new CreationSyncState { CheckedAt = now, Summary = result.Describe(), Hosts = states }.Save(DataDirectory); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return result;
    }

    // What this computer has: its list and which live creations are complete here.
    private string Key(CreationLibrary library)
    {
        var complete = string.Join('\n', library.Live.Where(c => CreationStore.IsComplete(DataDirectory, c)).Select(c => c.Id).Order(StringComparer.Ordinal));
        return library.Digest() + ":" + Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(complete)));
    }
}
