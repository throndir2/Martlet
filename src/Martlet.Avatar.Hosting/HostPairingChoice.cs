namespace Martlet.Avatar.Hosting;

/// <summary>Which pairing a connection to a paired host uses. A saved job (a Setup route, a Thinking pool member, a list entry)
/// keeps its host's pairing as it was when it was saved. Pairing that host again, or removing it from the Martlet network and
/// adding it back, gives this PC a new pairing (a new credential ID) and deletes the old secret, so the saved copy no longer
/// opens. A connection therefore uses this PC's current pairing with the same host ID (hosts.json) when it has one, and the
/// saved pairing only for a host this PC isn't paired with now. The host, its pinned key and this PC's secret still decide
/// trust: only the host ID ties a saved job to its host.</summary>
public static class HostPairingChoice
{
    /// <summary>The pairing to connect with for <paramref name="saved"/>: this PC's pairing with the same host in
    /// <paramref name="paired"/>, else <paramref name="saved"/>.</summary>
    public static AvatarRemoteHost For(AvatarRemoteHost saved, IEnumerable<AvatarRemoteHost> paired)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(paired);
        return paired.FirstOrDefault(p => string.Equals(p.HostId, saved.HostId, StringComparison.Ordinal)) ?? saved;
    }
}
