using Martlet.Avatar.Hosting;
using Martlet.Core.Cluster;

namespace Martlet.Desktop;

/// <summary>Lip-sync's pool (docs/AVATARS.md#the-lip-sync-pool): the list and the older "who does lip-sync" choice (the
/// character's profile and the shared plan's lip-sync job, which Home, Devices and the coverage checks show) kept in step both
/// ways. Companion › Lip sync (<see cref="LipSyncPage"/> in Pages\LipSync) shows the list and this PC's own Audio2Face set-up;
/// MainWindow.LipSyncPage.cs gives it what it needs.</summary>
public partial class MainWindow
{
    // ---------- the list and the older choice, in step ----------

    /// <summary>Whether a host answered its last check (the background sync check when sync is on, else Check connection);
    /// null before it was checked.</summary>
    private bool? LipSyncHostReachable(string hostId) =>
        (clusterEnabled ? clusterProbes.GetValueOrDefault(hostId)?.Reachable : null) ?? hostChecks.GetValueOrDefault(hostId)?.Reachable;

    /// <summary>This PC's own Audio2Face service address: the list's This PC setting, or the character's endpoint.</summary>
    private Uri LipSyncThisPcEndpoint(PoolMember? member = null)
    {
        member ??= store is null ? null : WorkSharingRoster.Shared.Pool(store.DataDirectory, PoolAreas.LipSync)?.Find(PoolMember.ThisPcKey);
        return member?.Setting(PoolSettingKeys.Endpoint) is { } text && Uri.TryCreate(text, UriKind.Absolute, out var uri) ? uri : OwnLipSyncEndpoint();
    }

    /// <summary>Whether this PC's own Audio2Face service is in use or on in the list, so its port is worth a look.</summary>
    private bool LipSyncLooksHere() => NetworkMap.LipSync(homeAvatar) == LipSyncHandler.ThisPc ||
        store is not null && WorkSharingRoster.Shared.Pool(store.DataDirectory, PoolAreas.LipSync)?.Find(PoolMember.ThisPcKey) is { Off: false };

    /// <summary>The older choice as a list key: "off" (voice loudness), "this-pc" (this PC's own service) or "host:&lt;id&gt;";
    /// null for a host a friend shares with this PC, which never goes in your shared list.</summary>
    private string? LipSyncChoiceKey() => NetworkMap.LipSync(homeAvatar) switch
    {
        LipSyncHandler.Loudness => "off",
        LipSyncHandler.Host when FindHost(homeAvatar!.RemoteHost!.HostId) is null => null,
        LipSyncHandler.Host => "host:" + homeAvatar!.RemoteHost!.HostId,
        _ => "this-pc"
    };

    /// <summary>Lip-sync's list; made once from the older choices when there is none yet (and saved and shared, unless it would
    /// be empty: a computer never shares an empty list over your other computers' choices). Null while there is none.</summary>
    private PoolList? EnsureLipSyncPool()
    {
        if (store is null) return null;
        if (WorkSharingRoster.Shared.Pool(store.DataDirectory, PoolAreas.LipSync) is { } list)
        {
            // A graphics card of a computer is one row for its computer here: a host runs one Audio2Face relay.
            var rows = LipSyncSharing.OneRowPerComputer(list);
            if (rows == list || !PoolSettings.SaveFor(store.DataDirectory, PoolAreas.LipSync, rows)) return list;
            QueueSettingsSync();
            return rows;
        }
        var made = LipSyncPool.Migrate(homeAvatar, store.DataDirectory);
        // A host a friend shares with this PC stays this PC's own choice.
        made = made with { Members = [.. made.Members.Where(m => m.HostId is null || FindHost(m.HostId) is not null)] };
        if (made.Members.Count == 0 || !PoolSettings.SaveFor(store.DataDirectory, PoolAreas.LipSync, made)) return null;
        QueueSettingsSync();
        ErrorLog.Info($"Pools: made the Lip-sync list from the older choice: {string.Join(", ", made.Members.Select(m => m.Key))}.");
        return made;
    }

    /// <summary>A lip-sync choice made outside the list (Home, Devices, a coverage fix, setup) puts that place first in the list
    /// and turns it on; voice loudness turns every member off (each keeps its settings). A host a friend shares stays out.</summary>
    private void FollowLipSyncChoiceIntoPool(string key)
    {
        if (store is null) return;
        if (WorkSharingRoster.Shared.Pool(store.DataDirectory, PoolAreas.LipSync) is not { } list)
        {
            EnsureLipSyncPool();
            return;
        }
        PoolList next;
        if (key == "off")
        {
            if (list.NoneOn) return;
            next = list with { Members = [.. list.Members.Select(m => m with { Off = true })] };
        }
        else
        {
            var member = key == "this-pc" ? PoolMember.ThisPc() : PoolMember.Computer(key[5..]);
            if (member.HostId is { } id && FindHost(id) is null) return;
            list = LipSyncSharing.OneRowPerComputer(list);
            var lead = list.Members.FirstOrDefault(m => !m.Off);
            if (lead is not null && lead.Key == member.Key) return;
            next = list.With((list.Find(member.Key) ?? member) with { Off = false }).Move(member.Key, -PoolSettings.MaximumMembers);
        }
        if (!PoolSettings.SaveFor(store.DataDirectory, PoolAreas.LipSync, next)) return;
        QueueSettingsSync();
        ErrorLog.Info($"Pools: lip-sync's list follows the choice {key}: {string.Join(", ", next.Members.Select(m => m.Key + (m.Off ? " (off)" : "")))}.");
    }

    /// <summary>After the list changed on this page: the older choice follows its first member that is on and paired here
    /// (so Home, Devices, the shared plan and a showing character agree), or voice loudness when none is on. A host a friend
    /// shares, chosen on this PC, stays while the list has a member on.</summary>
    private async Task FollowLipSyncListAsync(PoolList after)
    {
        if (store is null || setupService is null || closing) return;
        var lead = after.Members.FirstOrDefault(m => !m.Off && (m.Kind == PoolMemberKind.ThisPc || FindHost(m.HostId) is not null));
        var choice = lead is null ? "off" : lead.Kind == PoolMemberKind.ThisPc ? "this-pc" : "host:" + lead.HostId;
        var now = LipSyncChoiceKey();
        if (choice == now || now is null && lead is not null) return;
        using var turn = await ChangeTurnAsync();
        await ApplyLipSyncAsync(lead?.Kind == PoolMemberKind.ThisPc ? null : FindHost(lead?.HostId), lead is null);
        RecordClusterJob(ClusterJobs.LipSync, ClusterSync.Local(ClusterJobs.LipSync, homeSettings, homeAvatar, shared: SharedHostIds()));
        ErrorLog.Info($"Pools: lip-sync follows its list: {choice}.");
        if (closing) return;
        UpdateCharacterButton();
        RenderHome();
        RenderDevicePages();
        CheckOwnLipSyncAsync().Forget();
    }
}
