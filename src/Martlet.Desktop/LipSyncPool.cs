using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Avatar.Hosting;
using Martlet.Core.Cluster;
using Martlet.Core.Contracts;

namespace Martlet.Desktop;

/// <summary>One place lip-sync's pool tries: a paired computer's Audio2Face relay (<see cref="Host"/>), or this PC's own
/// Audio2Face service (<see cref="Host"/> null) at <see cref="Endpoint"/> (null: the character's own endpoint).
/// <see cref="Key"/> is the pool member's key (<see cref="PoolMember.Key"/>), its "computer" in <see cref="WorkQueue"/>.</summary>
internal sealed record LipSyncPlace(string Key, AvatarRemoteHost? Host, string? Endpoint = null);

/// <summary>A computer of lip-sync's pool that is ready for a sentence: its member key, host ID and link.</summary>
internal sealed record LipSyncMember(string Key, string HostId, IAvatarHostLink Link);

/// <summary>Lip-sync's pool on this PC (docs/AVATARS.md#the-lip-sync-pool, <see cref="PoolAreas.LipSync"/>). The members are
/// lip-sync's list in pools.json (<see cref="PoolSettings"/>), read again only when the file changed; until the owner saves
/// one, the list made from the older choices (<see cref="Migrate"/>): the computer assigned to lip-sync, the other paired
/// computers the shared plan says run Audio2Face, then this PC's own Audio2Face service. <see cref="PoolRouting.Order"/> keeps
/// the members that are on, kept for this PC and paired here; a card member uses its computer's Audio2Face relay when the relay
/// runs on that card (or the computer didn't say which). Each chunk of a sentence goes through <see cref="WorkQueue.Shared"/>
/// at live priority (<see cref="LipSyncSharing"/>): a busy computer is passed over for the next, one reply stays on one
/// computer while it is free, and a chunk that finds every computer busy too long is skipped, so the voice's loudness moves the
/// mouth for it. An empty list means the voice's loudness on this PC. The assigned computer's link belongs to
/// <see cref="AvatarController"/>; this pool opens and closes the others. Thread-safe.</summary>
internal sealed class LipSyncPool(Func<AvatarRemoteHost, IAvatarHostLink?> open,
    Func<AvatarProfile?, IReadOnlyList<LipSyncPlace>>? places = null) : IDisposable
{
    private readonly object gate = new();
    private readonly Func<AvatarProfile?, IReadOnlyList<LipSyncPlace>> places = places ?? FromFiles;
    // Links this pool opened, by host ID; null when the pairing couldn't be opened (no saved secret).
    private readonly Dictionary<string, IAvatarHostLink?> links = new(StringComparer.Ordinal);

    /// <summary>Where the chunks went: how many, how many moved off the first computer, how many were skipped, and the last.</summary>
    internal LipSyncSharing Sharing { get; } = new();

    /// <summary>The places to try for the next sentence, in order (never the network). Empty: the voice's loudness.</summary>
    internal IReadOnlyList<LipSyncPlace> Places(AvatarProfile? profile)
    {
        try { return places(profile); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or
            InvalidDataException or ContractException)
        {
            return Assigned(profile) is { } assigned ? [new("host:" + assigned.HostId, assigned), new(PoolMember.ThisPcKey, null)]
                : [new(PoolMember.ThisPcKey, null)];
        }
    }

    /// <summary>The paired computer the character's profile assigns to lip-sync (automatic lip-sync only), or null.</summary>
    internal static AvatarRemoteHost? Assigned(AvatarProfile? profile) =>
        profile is { LipSync: AvatarLipSync.Auto, RemoteHost: { } remote } ? remote : null;

    // ---------- the list ----------

    /// <summary>The places from this PC's files: lip-sync's saved list, or the one made from the older choices.</summary>
    internal static IReadOnlyList<LipSyncPlace> FromFiles(AvatarProfile? profile)
    {
        var directory = WorkSharingRoster.DataDirectory;
        var paired = directory is null ? new Dictionary<string, AvatarRemoteHost>(StringComparer.Ordinal)
            : WorkSharingRoster.Hosts(directory).GroupBy(h => h.HostId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().Pairing, StringComparer.Ordinal);
        if (Assigned(profile) is { } assigned) paired.TryAdd(assigned.HostId, assigned);
        var list = Saved(directory) ?? Migrate(profile, directory);
        return Resolve(PoolRouting.Order(PoolAreas.LipSync, list, WorkSharingRoster.Device, m => Usable(m, paired)), paired);
    }

    /// <summary>The places for <paramref name="order"/>: each computer once (at its first member: a computer runs one Audio2Face
    /// relay, whichever of its cards a member names), this PC's own service with its endpoint setting.</summary>
    internal static IReadOnlyList<LipSyncPlace> Resolve(PoolOrder order, IReadOnlyDictionary<string, AvatarRemoteHost> paired)
    {
        List<LipSyncPlace> list = [];
        foreach (var member in order.Members)
        {
            if (member.Kind == PoolMemberKind.ThisPc)
                list.Add(new(member.Key, null, member.Setting(PoolSettingKeys.Endpoint)));
            else if (member.HostId is { } id && paired.TryGetValue(id, out var host) && list.All(p => p.Host?.HostId != id))
                list.Add(new(member.Key, host));
        }
        return list;
    }

    /// <summary>Whether lip-sync can use <paramref name="member"/> on this PC now: this PC's own service, or a computer paired
    /// here whose Audio2Face relay runs on the card the member names (<see cref="Serves"/>).</summary>
    internal static bool Usable(PoolMember member, IReadOnlyDictionary<string, AvatarRemoteHost> paired) =>
        member.Kind == PoolMemberKind.ThisPc || member.OnHost && member.HostId is { } id && paired.ContainsKey(id) && Serves(member);

    /// <summary>A card member's computer runs its Audio2Face relay on that card: the relay's cards (as the computer last said)
    /// include CUDA index card − 1, or the computer named none by index (unknown, or only by UUID).</summary>
    internal static bool Serves(PoolMember member)
    {
        if (member.Kind != PoolMemberKind.Gpu || member.HostId is not { } id || member.Card is not { } card) return true;
        var indexes = HostRouteGpus.For(id, Audio2FaceHostClient.RouteId).Where(g => g.Length > 0 && g.All(char.IsAsciiDigit)).ToArray();
        return indexes.Length == 0 || indexes.Contains((card - 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Lip-sync's list made from the older choices, used until lip-sync's page saves one: empty (the voice's loudness)
    /// when lip-sync was off; otherwise the computer assigned to it with the other paired computers the shared plan says run
    /// Audio2Face (in Devices › Sharing work's order for lip-sync), then this PC's own Audio2Face service.</summary>
    internal static PoolList Migrate(AvatarProfile? profile, string? directory) =>
        LipSyncSharing.FromOlderChoices(profile?.LipSync == AvatarLipSync.Loudness, Assigned(profile) is { } assigned
            ? [.. WorkSharingRoster.Order(directory, WorkSharingJobs.LipSync, HostRoles.Audio2Face, null, assigned.HostId)
                .Select(place => place.Host?.HostId ?? assigned.HostId)]
            : []);

    private static readonly object FileGate = new();
    private static (string? Path, DateTime Written, long Checked, PoolList? List) saved;

    // Lip-sync's saved list (null: none yet). The file is looked at again at most once a second and read again only when it
    // changed, so a sentence pays no file system call.
    private static PoolList? Saved(string? directory)
    {
        if (directory is null) return null;
        var path = Path.Combine(directory, PoolSettings.File(PoolAreas.LipSync.Shared));
        var now = Environment.TickCount64;
        lock (FileGate)
            if (saved.Path == path && now - saved.Checked < 1_000) return saved.List;
        DateTime written;
        try { written = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { written = DateTime.MinValue; }
        lock (FileGate)
            if (saved.Path == path && saved.Written == written)
            {
                saved.Checked = now;
                return saved.List;
            }
        var list = written == DateTime.MinValue ? null : PoolSettings.LoadFor(directory, PoolAreas.LipSync);
        lock (FileGate) saved = (path, written, now, list);
        return list;
    }

    // ---------- the computers ----------

    /// <summary>The computers of <paramref name="hosts"/> that are ready for the next sentence, in order; empty when none is.
    /// Every one is checked at once; only the first one's check is awaited (as the assigned computer's always was), and the
    /// others count once their check is known, so the pool never adds a wait to a sentence. Each link remembers its answer (a
    /// ready one until it fails, a silent one for 30 seconds).</summary>
    internal async Task<IReadOnlyList<LipSyncMember>> ReadyAsync(IReadOnlyList<LipSyncPlace> hosts, string? assignedId,
        IAvatarHostLink? assignedLink, CancellationToken token)
    {
        var list = Members(hosts, assignedId, assignedLink);
        if (list.Count == 0) return [];
        var checks = list.Select((member, index) => member.Link.ReadyAsync(index == 0 ? token : CancellationToken.None)).ToArray();
        foreach (var check in checks.Skip(1)) Observe(check);
        await checks[0].ConfigureAwait(false);
        return [.. list.Where((_, index) => checks[index].IsCompletedSuccessfully && checks[index].Result)];
    }

    /// <summary>Checks the pool's other computers in the background (the controller checks the assigned one itself), so the
    /// first sentence finds them known.</summary>
    internal void Warm(AvatarProfile? profile, IAvatarHostLink? assignedLink)
    {
        var hosts = Places(profile).Where(p => p.Host is not null).ToArray();
        var assigned = Assigned(profile)?.HostId;
        foreach (var member in Members(hosts, assigned, assignedLink).Where(m => m.HostId != assigned))
            Observe(member.Link.ReadyAsync(CancellationToken.None));
    }

    // A background check's failure (a link closed under it) is nobody's to report.
    private static void Observe(Task check) =>
        check.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private List<LipSyncMember> Members(IReadOnlyList<LipSyncPlace> hosts, string? assignedId, IAvatarHostLink? assignedLink)
    {
        List<LipSyncMember> list = [];
        List<IAvatarHostLink> closing = [];
        lock (gate)
        {
            foreach (var place in hosts)
            {
                if (place.Host is not { } host) continue;
                if (host.HostId == assignedId && assignedLink is not null)
                {
                    list.Add(new(place.Key, host.HostId, assignedLink));
                    continue;
                }
                if (!links.TryGetValue(host.HostId, out var link))
                {
                    try { link = open(host); }
                    catch (Exception error) when (error is Audio2FaceHostException or ContractException) { link = null; }
                    links[host.HostId] = link;
                }
                if (link is not null) list.Add(new(place.Key, host.HostId, link));
            }
            // A computer that left the pool (taken out, turned off or kept for another companion PC) closes its link.
            foreach (var gone in links.Keys.Where(id => hosts.All(p => p.Host?.HostId != id) || id == assignedId && assignedLink is not null).ToArray())
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
        Sharing.ChunkAsync(WorkQueue.Shared, ids.TurnId, pool, m => m.Key,
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
