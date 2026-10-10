using System.IO;
using System.Windows.Threading;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Core.Contracts;
using Martlet.Core.Sync;
using Martlet.Memory;

namespace Martlet.Desktop;

/// <summary>One memory on every computer, by memory space: what each account remembers is the same on every computer where it
/// signs in, through the paired hosts (docs/MEMORY.md). Every 30 seconds while "Keep Martlet the same on all my computers" and
/// memory are on, this PC syncs each space the account signed in reads (its own space, the household and any space shared with
/// it): it reads each host's copy of the space when it changed, records facts saved, edited or forgotten here, makes the space's
/// store hold the newest version of every fact and gives hosts with an older copy the merged one. The owner's space also keeps
/// the old single memory document the same, for desktops on an older Martlet. A store is touched only while nobody else uses
/// it, so a conversation never waits for the sync.</summary>
public partial class MainWindow
{
    /// <summary>The cache key of a host's copy of the old single memory document (the owner's memories before accounts).</summary>
    private const string OldMemoryDocument = "memories.json";

    private readonly DispatcherTimer memorySyncTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly Dictionary<string, MemorySyncNode> memoryNodes = new(StringComparer.Ordinal);
    private bool memorySyncBusy, memorySyncQueued, memoryWindowOpen;
    /// <summary>Each host's copy of each space as last read or merged, with its digest, so a copy is read again only when it
    /// changed. The key is the host ID and the space (or <see cref="OldMemoryDocument"/>).</summary>
    private readonly Dictionary<(string Host, string Space), (string Digest, SharedMemories Copy)> memoryCopies = [];
    private string memorySyncStatus = "Memories: not synced yet.";

    private void InitializeMemorySync()
    {
        memorySyncTimer.Tick += (_, _) => SyncMemoriesAsync().Forget();
        ShowMemorySyncStatus();
    }

    private void StartMemorySync()
    {
        if (memory is null || closing) return;
        memorySyncTimer.Start();
        LoadMemorySpacesAsync().Forget();
        SyncMemoriesAsync().Forget();
    }

    /// <summary>Uses <paramref name="next"/>'s memories from now on (sign-in or a switch, between replies): the spaces of the
    /// account signed in before get one more sync in the background (it stays signed in on this device), then the new account's
    /// other spaces are loaded for recall and synced.</summary>
    private async Task UseMemoryAccountAsync(MemoryAccount? next)
    {
        if (memory is null || closing || Equals(memory.Account, next)) return;
        var before = memory.Account;
        var leaving = before is null ? null : await memory.SpacesAsync(lifetime.Token);
        memory.UseAccount(next);
        memoryCopies.Clear();
        memoryNodes.Clear();
        if (leaving is not null)
            await SyncMemoriesAsync([.. leaving.Readable.Where(s => s.Id != MemorySpaceId.Household)], before!.Owner ? before.Space : null);
        await LoadMemorySpacesAsync();
        QueueMemorySync();
    }

    /// <summary>Loads the read-only copies of the spaces recall reads besides the active one, off the window's thread.</summary>
    private async Task LoadMemorySpacesAsync()
    {
        if (memory is null || closing) return;
        var service = memory;
        try { await Task.Run(() => service.LoadSpacesAsync(lifetime.Token)); }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ContractException or DesktopMemoryException)
        {
            ErrorLog.Warn("Memory: couldn't load the memory spaces recall reads besides yours.", error);
        }
    }

    /// <summary>Syncs soon (debounced), so a fact saved or forgotten here reaches the other computers quickly.</summary>
    private void QueueMemorySync()
    {
        if (memorySyncQueued || memory is null || closing) return;
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

    /// <summary>What one space's sync did, for the status line (counts only).</summary>
    private sealed record SpaceSync(string Space, int Remembered, int Same, int Taken, int Forgot, int Down, int Old, int Denied,
        int Unreadable, string? Problem, string? Waiting);

    /// <param name="only">The spaces to sync (an account's spaces after a switch); null: every space of the account signed in.</param>
    /// <param name="oldDocument">The space that also keeps the old single memory document the same: the owner's space.</param>
    private async Task SyncMemoriesAsync(IReadOnlyList<MemorySpace>? only = null, string? oldDocument = null)
    {
        if (memory is null || memorySyncBusy || closing) return;
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
        var service = memory;
        memorySyncBusy = true;
        try
        {
            IReadOnlyList<MemorySpace> spaces;
            if (only is not null) spaces = only;
            else if (await service.SpacesAsync(lifetime.Token) is { } set)
            {
                spaces = set.Readable;
                oldDocument = service.Account is { Owner: true } owner ? owner.Space : null;
            }
            else
            {
                memorySyncStatus = "Memories: memory is off, so nothing is remembered or shared.";
                return;
            }
            var synced = new List<SpaceSync>();
            foreach (var space in spaces.Where(s => s.SyncDirectory is not null && MemorySpaceId.IsValid(s.Id)))
            {
                if (closing) return;
                synced.Add(await SyncSpaceAsync(service, space, hosts, space.Id == oldDocument));
            }
            if (only is null) memorySyncStatus = MemorySyncLine(synced, hosts.Count, service.Account);
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

    private async Task<SpaceSync> SyncSpaceAsync(DesktopMemoryService service, MemorySpace space, IReadOnlyList<PairedHost> hosts,
        bool oldDocument)
    {
        var reads = await Task.WhenAll(hosts.Select(host => ReadMemoryCopiesAsync(host, space.Id, oldDocument)));
        var copies = reads.SelectMany(r => new[] { r.Copy, r.Old }).OfType<SharedMemories>().ToArray();
        if (!memoryNodes.TryGetValue(space.Id, out var node))
            memoryNodes[space.Id] = node = new MemorySyncNode(space.SyncDirectory!, ClusterDevice);
        // Off the window's thread: opening the store reads it from disk.
        var (ran, why, result) = await Task.Run(() => service.TryWithFreeStoreAsync(space, (owned, token) => node.SyncAsync(copies,
            read => ReadStoreAsync(owned, read), (changes, apply) => owned.MergeAsync(new() { Facts = changes.Facts, Forget = changes.Forget }, apply),
            DescribeFact, DateTimeOffset.UtcNow, token), lifetime.Token));
        if (!ran || result is null)
            return new(space.Id, 0, 0, 0, 0, 0, 0, 0, 0, null, why == "off" ? "off" : "busy");
        var digest = result.Document.Digest();
        foreach (var read in reads)
        {
            if (closing) break;
            if (read.Ok && memoryCopies.GetValueOrDefault((read.HostId, space.Id)).Digest != digest)
                await GiveMemoriesAsync(read.Host, space.Id, connection => connection.MergeMemorySpaceAsync(space.Id, result.Document, lifetime.Token));
            if (read.Old is not null && memoryCopies.GetValueOrDefault((read.HostId, OldMemoryDocument)).Digest != digest)
                await GiveMemoriesAsync(read.Host, OldMemoryDocument, connection => connection.MergeMemoriesAsync(result.Document, lifetime.Token));
        }
        if (result.Taken > 0 || result.Forgot > 0)
            ErrorLog.Info($"Shared memories ({SpaceName(space.Id, service.Account)}): took {result.Taken} and forgot {result.Forgot} from your other computers.");
        if (result.Problem is not null) ErrorLog.Warn($"Shared memories couldn't change this PC's memory: {result.Problem}");
        var same = reads.Count(r => (r.Ok && memoryCopies.GetValueOrDefault((r.HostId, space.Id)).Digest == digest) ||
            (!r.Ok && r.Old is not null && memoryCopies.GetValueOrDefault((r.HostId, OldMemoryDocument)).Digest == digest));
        return new(space.Id, result.Document.Live.Count(), same, result.Taken, result.Forgot,
            reads.Count(r => !r.Ok && !r.NoSpaces && !r.Denied), reads.Count(r => r.NoSpaces && r.Old is null), reads.Count(r => r.Denied),
            result.Unreadable, result.Problem, null);
    }

    private async Task GiveMemoriesAsync(PairedHost host, string key, Func<Audio2FaceHostConnection, Task<SharedMemories>> merge)
    {
        try
        {
            var copy = await ClusterSync.WithConnectionAsync(host.Pairing, merge);
            memoryCopies[(host.HostId, key)] = (copy.Digest(), copy);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is OperationCanceledException or ArgumentException || ClusterSync.IsHostFailure(error))
        {
            ErrorLog.Warn($"Couldn't give {host.HostId} the shared memories; trying again on the next check.", error);
        }
    }

    /// <summary>The status line: how many facts each space remembers, on how many hosts all of them are the same, and what came
    /// from or was forgotten because of other computers. Counts only, never a fact.</summary>
    private static string MemorySyncLine(IReadOnlyList<SpaceSync> synced, int hosts, MemoryAccount? account)
    {
        if (synced.Count == 0) return "Memories: nothing to share yet.";
        if (synced.All(s => s.Waiting == "off")) return "Memories: memory is off, so nothing is remembered or shared.";
        if (synced.Any(s => s.Waiting == "busy")) return "Memories: waiting for the memory store to be free.";
        var taken = synced.Sum(s => s.Taken);
        var forgot = synced.Sum(s => s.Forgot);
        var down = synced.Max(s => s.Down);
        var old = synced.Max(s => s.Old);
        var denied = synced.Max(s => s.Denied);
        var unreadable = synced.Sum(s => s.Unreadable);
        var problem = synced.Select(s => s.Problem).FirstOrDefault(p => p is not null);
        var counts = synced.Count == 1 ? $"{synced[0].Remembered} remembered"
            : string.Join(", ", synced.Select(s => $"{s.Remembered} {SpaceName(s.Space, account)}"));
        return $"Memories: {counts}, the same on {synced.Min(s => s.Same)} of {hosts} host{(hosts == 1 ? "" : "s")}; checked {DateTime.Now:t}." +
            (taken + forgot > 0 ? $" Took {taken} and forgot {forgot} from your other computers." : "") +
            (down > 0 ? $" {down} not responding." : "") +
            (old > 0 ? $" Update {(old == 1 ? "one host" : old + " hosts")} to share memories." : "") +
            (denied > 0 ? $" {(denied == 1 ? "One host doesn't" : denied + " hosts don't")} know you on this PC yet." : "") +
            (unreadable > 0 ? $" {unreadable} need a newer Martlet on this PC." : "") +
            (problem is not null ? $" This PC couldn't take them yet: {problem}" : "");
    }

    /// <summary>A space's short name in the status line.</summary>
    private static string SpaceName(string space, MemoryAccount? account) => MemorySpaces.Label(space, account) switch
    {
        "Your memories" => "yours",
        "Household" => "household",
        "This character's memories" => "this character's",
        _ => "shared with you"
    };

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

    /// <summary>A host's copies of the memory space <paramref name="space"/> and, for the owner's space
    /// (<paramref name="oldDocument"/>), of the old single memory document: each read again only when its digest changed. Ok: the
    /// space was read; NoSpaces: the host is older than memory spaces; Denied: the host doesn't let this PC use the space yet (it
    /// hasn't learned that the account signed in here).</summary>
    private async Task<(string HostId, PairedHost Host, SharedMemories? Copy, SharedMemories? Old, bool Ok, bool NoSpaces, bool Denied)>
        ReadMemoryCopiesAsync(PairedHost host, string space, bool oldDocument)
    {
        SharedMemories? copy = null, old = null;
        bool ok = false, noSpaces = false, denied = false;
        try
        {
            await ClusterSync.WithConnectionAsync(host.Pairing, async connection =>
            {
                try
                {
                    copy = await ReadCachedAsync(host.HostId, space, () => connection.ReadMemorySpaceDigestAsync(space, lifetime.Token),
                        () => connection.ReadMemorySpaceAsync(space, lifetime.Token));
                    ok = true;
                }
                catch (Audio2FaceHostException error) when (IsOlderHost(error)) { noSpaces = true; }
                catch (Audio2FaceHostException error) when (error.Code == "memories.space_denied") { denied = true; }
                if (oldDocument)
                {
                    try
                    {
                        old = await ReadCachedAsync(host.HostId, OldMemoryDocument, () => connection.ReadMemoriesDigestAsync(lifetime.Token),
                            () => connection.ReadMemoriesAsync(lifetime.Token));
                    }
                    catch (Audio2FaceHostException error) when (IsOlderHost(error)) { }
                }
                return true;
            });
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is OperationCanceledException or ArgumentException || ClusterSync.IsHostFailure(error)) { }
        return (host.HostId, host, copy, old, ok, noSpaces, denied);

        async Task<SharedMemories> ReadCachedAsync(string hostId, string key, Func<Task<string>> digest, Func<Task<SharedMemories>> read)
        {
            var now = await digest();
            if (memoryCopies.TryGetValue((hostId, key), out var known) && known.Digest == now) return known.Copy;
            var fresh = await read();
            memoryCopies[(hostId, key)] = (fresh.Digest(), fresh);
            return fresh;
        }
    }

    private static bool IsOlderHost(Audio2FaceHostException error) => error.Code is "request.invalid" or "request.not_found" or "route.not_found";

    private void ShowMemorySyncStatus() => MemorySyncStatusText.Text = memorySyncStatus;
}