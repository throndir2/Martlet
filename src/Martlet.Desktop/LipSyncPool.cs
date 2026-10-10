using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Avatar.Hosting;
using Martlet.Core.Cluster;
using Martlet.Core.Contracts;

namespace Martlet.Desktop;

/// <summary>A computer in lip-sync's pool: its host ID and the link to its Audio2Face relay.</summary>
internal sealed record LipSyncMember(string HostId, IAvatarHostLink Link);

/// <summary>Lip-sync's pool on this PC (docs/AVATARS.md#lip-sync-pool). The members are the paired computers whose shared-plan
/// record runs the Audio2Face host role, the computer assigned to lip-sync first, in the order <see cref="WorkSharingRoster"/>
/// gives (read from this PC's files, never the network). Each chunk of a sentence goes through <see cref="WorkQueue.Shared"/>
/// at live priority (<see cref="LipSyncSharing"/>): a busy computer is passed over for the next, one reply stays on one
/// computer while it is free, and a chunk that finds every computer busy too long is skipped, so the voice's loudness moves
/// the mouth for it. The assigned computer's link belongs to <see cref="AvatarController"/>; this pool opens and closes the
/// others. Thread-safe.</summary>
internal sealed class LipSyncPool(Func<AvatarRemoteHost, IAvatarHostLink?> open,
    Func<AvatarRemoteHost, IReadOnlyList<AvatarRemoteHost>>? members = null) : IDisposable
{
    private readonly object gate = new();
    private readonly Func<AvatarRemoteHost, IReadOnlyList<AvatarRemoteHost>> members = members ?? FromPlan;
    // Links this pool opened, by host ID; null when the pairing couldn't be opened (no saved secret).
    private readonly Dictionary<string, IAvatarHostLink?> links = new(StringComparer.Ordinal);

    /// <summary>Where the chunks went: how many, how many moved off the first computer, how many were skipped, and the last.</summary>
    internal LipSyncSharing Sharing { get; } = new();

    /// <summary>The pool's computers when <paramref name="assigned"/> does lip-sync: the paired computers the shared plan says
    /// run Audio2Face, in Sharing work's order for lip-sync (the assigned computer first unless chosen otherwise).</summary>
    internal static IReadOnlyList<AvatarRemoteHost> FromPlan(AvatarRemoteHost assigned) =>
        [.. WorkSharingRoster.Order(WorkSharingRoster.DataDirectory, WorkSharingJobs.LipSync, HostRoles.Audio2Face, null, assigned.HostId)
            .Select(place => place.Host is { } host && host.HostId != assigned.HostId ? host.Pairing : assigned)];

    /// <summary>The members ready for the next sentence, in order; empty when none is. Every member is checked at once; only
    /// the first one's check is awaited (as the assigned computer's always was), and the others count once their check is
    /// known, so the pool never adds a wait to a sentence. Each link remembers its answer (a ready one until it fails, a
    /// silent one for 30 seconds).</summary>
    internal async Task<IReadOnlyList<LipSyncMember>> ReadyAsync(AvatarRemoteHost assigned, IAvatarHostLink assignedLink,
        CancellationToken token)
    {
        var list = Members(assigned, assignedLink);
        if (list.Count == 0) return [];
        var checks = list.Select((member, index) => member.Link.ReadyAsync(index == 0 ? token : CancellationToken.None)).ToArray();
        await checks[0].ConfigureAwait(false);
        return [.. list.Where((_, index) => checks[index].IsCompletedSuccessfully && checks[index].Result)];
    }

    /// <summary>Checks the pool's members in the background, so the first sentence finds them known.</summary>
    internal void Warm(AvatarRemoteHost assigned, IAvatarHostLink assignedLink)
    {
        foreach (var member in Members(assigned, assignedLink))
            _ = member.Link.ReadyAsync(CancellationToken.None);
    }

    private List<LipSyncMember> Members(AvatarRemoteHost assigned, IAvatarHostLink assignedLink)
    {
        IReadOnlyList<AvatarRemoteHost> wanted;
        try { wanted = members(assigned); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or
            InvalidDataException or ContractException)
        {
            wanted = [assigned];
        }
        List<LipSyncMember> list = [];
        List<IAvatarHostLink> closing = [];
        lock (gate)
        {
            foreach (var remote in wanted)
            {
                if (remote.HostId == assigned.HostId)
                {
                    list.Add(new(assigned.HostId, assignedLink));
                    continue;
                }
                if (!links.TryGetValue(remote.HostId, out var link))
                {
                    try { link = open(remote); }
                    catch (Exception error) when (error is Audio2FaceHostException or ContractException) { link = null; }
                    links[remote.HostId] = link;
                }
                if (link is not null) list.Add(new(remote.HostId, link));
            }
            // A computer that left the pool (its role removed, unticked or kept for another companion PC) closes its link.
            foreach (var gone in links.Keys.Where(id => wanted.All(w => w.HostId != id)).ToArray())
            {
                if (links[gone] is { } link) closing.Add(link);
                links.Remove(gone);
            }
        }
        foreach (var link in closing) link.Dispose();
        return list;
    }

    /// <summary>Animates one chunk of reply <paramref name="ids"/>.TurnId on the pool: <paramref name="pcm"/> is the chunk with
    /// its context before it, <paramref name="samples"/> the new part's length (how long the chunk may wait for a busy
    /// computer, <see cref="LipSyncSharing.Wait"/>). No frames: every computer stayed busy, so the voice's loudness moves the
    /// mouth for this chunk.</summary>
    internal IAsyncEnumerable<RemoteFaceFrame> AnimateAsync(IReadOnlyList<LipSyncMember> pool, CorrelationIds ids, long epoch,
        int sampleRate, ReadOnlyMemory<byte> pcm, long samples, CancellationToken token) =>
        Sharing.ChunkAsync(WorkQueue.Shared, ids.TurnId, pool, m => m.HostId,
            (member, t) => AnimateOnAsync(member, ids, epoch, sampleRate, pcm, t), WorkSharingRoster.Classify,
            LipSyncSharing.Wait(samples, sampleRate), null, token);

    private static async IAsyncEnumerable<RemoteFaceFrame> AnimateOnAsync(LipSyncMember member, CorrelationIds ids, long epoch,
        int sampleRate, ReadOnlyMemory<byte> pcm, [EnumeratorCancellation] CancellationToken token)
    {
        if (!await member.Link.ReadyAsync(token).ConfigureAwait(false))
            throw new Audio2FaceHostException("worker.unavailable", $"{member.HostId} isn't ready for lip-sync.");
        await using var frames = WorkSharingRoster.Watched(member.HostId, "lip-sync",
            member.Link.AnimateAsync(ids, epoch, sampleRate, pcm, token), token).GetAsyncEnumerator(token);
        while (true)
        {
            bool next;
            try { next = await frames.MoveNextAsync().ConfigureAwait(false); }
            catch (Exception error) when (!token.IsCancellationRequested && error is Audio2FaceHostException or HttpRequestException or
                IOException && WorkSharingRoster.Classify(error) is WorkRefusal.Unavailable or WorkRefusal.None)
            {
                // A computer that doesn't answer or fails is left out for a while (its link checks again after 30 s); a busy
                // one stays in the pool.
                member.Link.Invalidate();
                throw;
            }
            if (!next) yield break;
            yield return frames.Current;
        }
    }

    /// <summary>Closes every link this pool opened (the character hid, or lip-sync moved to another computer).</summary>
    internal void Clear()
    {
        IAvatarHostLink[] closing;
        lock (gate)
        {
            closing = [.. links.Values.OfType<IAvatarHostLink>()];
            links.Clear();
        }
        foreach (var link in closing) link.Dispose();
    }

    public void Dispose() => Clear();
}
