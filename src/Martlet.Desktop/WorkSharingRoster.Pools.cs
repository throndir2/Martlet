using System.IO;
using Martlet.Core.Cluster;

namespace Martlet.Desktop;

/// <summary>The pool lists on the request path (docs/CLUSTER.md#pools-one-ordered-list-of-members-per-area): each area's
/// ordered members from pools.json or pools-local.json, read again only when the file changed (at most one file time a second),
/// never the network.</summary>
internal static partial class WorkSharingRoster
{
    /// <summary><paramref name="area"/>'s list in <paramref name="directory"/>; null when the area has none yet (it still uses its
    /// older choices).</summary>
    internal static PoolList? Pool(string? directory, PoolArea area)
    {
        if (directory is null) return null;
        var settings = Cached(Path.Combine(directory, PoolSettings.File(area.Shared)), () => PoolSettings.Load(directory, area.Shared));
        return settings.Has(area.Id) ? settings.Pool(area.Id) : null;
    }

    /// <summary>The members this PC tries for <paramref name="area"/>, in the owner's order (<see cref="PoolRouting.Order"/>), from
    /// <see cref="DataDirectory"/>. <paramref name="usable"/> is the area's own check (paired, runs the engine, has a key).</summary>
    internal static PoolOrder PoolMembers(PoolArea area, Func<PoolMember, bool>? usable = null) =>
        PoolRouting.Order(area, Pool(DataDirectory, area), Device, usable);

    /// <summary>The paired computers a host request of <paramref name="area"/> tries (<see cref="PoolRouting.Hosts"/>).</summary>
    internal static IReadOnlyList<string> PoolHosts(PoolArea area, PoolList list, string planned, string? own, IReadOnlySet<string> runs) =>
        PoolRouting.Hosts(area, list, Device, planned, own, runs);
}
