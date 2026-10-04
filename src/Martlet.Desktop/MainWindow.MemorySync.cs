using System.IO;
using System.Windows.Threading;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Core.Contracts;
using Martlet.Core.Sync;
using Martlet.Memory;

namespace Martlet.Desktop;

/// <summary>One memory on every computer: what Martlet remembers is the same on all the owner's computers, through the paired
/// hosts (docs/MEMORY.md). Every 30 seconds while "Keep Martlet the same on all my computers" and memory are on, this PC reads
/// each host's copy when it changed, records facts saved, edited or forgotten here, makes its store hold the newest version of
/// every fact and gives hosts with an older copy the merged one. The store is touched only while nobody else uses it, so a
/// conversation never waits for the sync.</summary>
public partial class MainWindow
{
    private readonly DispatcherTimer memorySyncTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private MemorySyncNode? memoryNode;
    private bool memorySyncBusy, memorySyncQueued, memoryWindowOpen;
    /// <summary>Each host's copy as last read or merged, with its digest, so a copy is read again only when it changed.</summary>
    private readonly Dictionary<string, (string Digest, SharedMemories Copy)> memoryCopies = new(StringComparer.Ordinal);
    private string memorySyncStatus = "Memories: not synced yet.";

    private void InitializeMemorySync()
    {
        memorySyncTimer.Tick += (_, _) => SyncMemoriesAsync().Forget();
        if (store is not null) memoryNode = new MemorySyncNode(store.DataDirectory, ClusterDevice);
        ShowMemorySyncStatus();
    }

    private void StartMemorySync()
    {
        if (memoryNode is null || closing) return;
        memorySyncTimer.Start();
        SyncMemoriesAsync().Forget();
    }

    /// <summary>Syncs soon (debounced), so a fact saved or forgotten here reaches the other computers quickly.</summary>
    private void QueueMemorySync()
    {
        if (memorySyncQueued || memoryNode is null || closing) return;
        memorySyncQueued = true;
        SyncSoonAsync().Forget();

        async Task SyncSoonAsync()
        {
            try { await Task.Delay(TimeSpan.FromSeconds(3), lifetime.Token); }
            catch (OperationCanceledException) { return; }
            finally { memorySyncQueued = false; }
            await SyncMemoriesAsync();
        }
    }

    private async Task SyncMemoriesAsync()
    {
        if (memoryNode is null || memory is null || memorySyncBusy || closing) return;
        if (!clusterEnabled)
        {
            memorySyncStatus = "Memories stay on this PC while this is off; what changed here is shared when you turn it on.";
            ShowMemorySyncStatus();
            return;
        }
        // Never in a conversation's way: the Memory window, a reply or the owner talking hold the sync for the next check.
        if (memoryWindowOpen || conversation?.Replying == true || openConversation?.HearingYou == true) return;
        var hosts = NetworkMap.Hosts(Inputs());
        if (hosts.Count == 0)
        {
            memorySyncStatus = "Memories: pair a Martlet host to share them with your other computers.";
            ShowMemorySyncStatus();
            return;
        }
        memorySyncBusy = true;
        try
        {
            var reads = await Task.WhenAll(hosts.Select(ReadMemoryCopyAsync));
            if (closing) return;
            var copies = reads.Select(r => r.Copy).OfType<SharedMemories>().ToArray();
            var node = memoryNode;
            var service = memory;
            // Off the window's thread: opening the store reads it from disk.
            var (ran, why, result) = await Task.Run(() => service.TryWithFreeStoreAsync((owned, token) => node.SyncAsync(copies,
                read => ReadStoreAsync(owned, read), (changes, apply) => owned.MergeAsync(new() { Facts = changes.Facts, Forget = changes.Forget }, apply),
                DescribeFact, DateTimeOffset.UtcNow, token), lifetime.Token));
            if (!ran || result is null)
            {
                memorySyncStatus = why == "off" ? "Memories: memory is off, so nothing is remembered or shared."
                    : "Memories: waiting for the memory store to be free.";
                return;
            }
            var digest = result.Document.Digest();
            foreach (var read in reads.Where(r => r.Ok))
            {
                if (closing) return;
                if (memoryCopies.GetValueOrDefault(read.HostId).Digest == digest) continue;
                try
                {
                    var copy = await ClusterSync.WithConnectionAsync(read.Host.Pairing, connection => connection.MergeMemoriesAsync(result.Document, lifetime.Token));
                    memoryCopies[read.HostId] = (copy.Digest(), copy);
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { throw; }
                catch (Exception error) when (error is OperationCanceledException or ArgumentException || ClusterSync.IsHostFailure(error))
                {
                    ErrorLog.Warn($"Couldn't give {read.HostId} the shared memories; trying again on the next check.", error);
                }
            }
            if (result.Taken > 0 || result.Forgot > 0)
                ErrorLog.Info($"Shared memories: took {result.Taken} and forgot {result.Forgot} from your other computers.");
            if (result.Problem is not null) ErrorLog.Warn($"Shared memories couldn't change this PC's memory: {result.Problem}");
            var same = reads.Count(r => r.Ok && memoryCopies.GetValueOrDefault(r.HostId).Digest == digest);
            var old = reads.Count(r => r.Old);
            var down = reads.Count(r => !r.Ok && !r.Old);
            memorySyncStatus = $"Memories: {result.Document.Live.Count()} remembered, the same on {same} of {hosts.Count} " +
                $"host{(hosts.Count == 1 ? "" : "s")}; checked {DateTime.Now:t}." +
                (result.Taken + result.Forgot > 0 ? $" Took {result.Taken} and forgot {result.Forgot} from your other computers." : "") +
                (down > 0 ? $" {down} not responding." : "") +
                (old > 0 ? $" Update {(old == 1 ? "one host" : old + " hosts")} to share memories." : "") +
                (result.Unreadable > 0 ? $" {result.Unreadable} need a newer Martlet on this PC." : "") +
                (result.Problem is { } problem ? $" This PC couldn't take them yet: {problem}" : "");
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ContractException or MemoryException or
            DesktopMemoryException or InvalidOperationException)
        {
            memorySyncStatus = "Memories: couldn't sync with your other computers: " + error.Message;
            ErrorLog.Warn("Shared memories sync failed.", error);
        }
        finally
        {
            memorySyncBusy = false;
            if (!closing) ShowMemorySyncStatus();
        }
    }

    private static async Task<LocalMemories> ReadStoreAsync(MemoryStore owned, CancellationToken token)
    {
        var inspection = await owned.InspectAsync(token);
        return new(inspection.StoreId, [.. inspection.Facts.Select(f => new LocalMemory(f.Id, f.Revision, f.UpdatedAtUtc, MemoryFactJson.Write(f)))]);
    }

    /// <summary>Whether a shared fact expired and whether the owner typed it, or null when this Martlet can't read it.</summary>
    internal static MemoryFactInfo? DescribeFact(string json)
    {
        try
        {
            var fact = MemoryFactJson.Read(json);
            return new(fact.Retention.HasExpired(DateTimeOffset.UtcNow), fact.LastModifiedBy.SourceKind != MemorySourceKind.Conversation);
        }
        catch (MemoryException) { return null; }
    }

    /// <summary>A host's copy of the shared memories: read again only when its digest changed. Hosts older than shared memories
    /// answer request.invalid.</summary>
    private async Task<(string HostId, PairedHost Host, SharedMemories? Copy, bool Ok, bool Old)> ReadMemoryCopyAsync(PairedHost host)
    {
        try
        {
            var copy = await ClusterSync.WithConnectionAsync(host.Pairing, async connection =>
            {
                var digest = await connection.ReadMemoriesDigestAsync(lifetime.Token);
                if (memoryCopies.TryGetValue(host.HostId, out var known) && known.Digest == digest) return known.Copy;
                var read = await connection.ReadMemoriesAsync(lifetime.Token);
                memoryCopies[host.HostId] = (read.Digest(), read);
                return read;
            });
            return (host.HostId, host, copy, true, false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { throw; }
        catch (Audio2FaceHostException error) when (error.Code is "request.invalid" or "request.not_found" or "route.not_found")
        {
            return (host.HostId, host, null, false, true);
        }
        catch (Exception error) when (error is OperationCanceledException or ArgumentException || ClusterSync.IsHostFailure(error))
        {
            return (host.HostId, host, null, false, false);
        }
    }

    private void ShowMemorySyncStatus() => MemorySyncStatusText.Text = memorySyncStatus;
}
