using System.Net;
using Martlet.Core.Network;

namespace Martlet.Avatar.Audio2Face.Remote;

/// <summary>A host that answered a find on the home network, with an origin to try ("https://192.168.1.20:9443").</summary>
public sealed record NetworkHostSighting(string HostId, string Origin);

/// <summary>What the last look on the home network found: when, the host IDs that answered, the hosts this PC didn't know that
/// it brought into its network (<see cref="Found"/>), per other host it didn't know why it wasn't used, and why the look itself
/// failed (<see cref="Problem"/>, null when it ran).</summary>
public sealed record NetworkHostLook(DateTimeOffset At, IReadOnlyList<string> Answered, IReadOnlyList<string> Found,
    IReadOnlyDictionary<string, string> Left, string? Problem = null)
{
    /// <summary>What the owner should know from this look, one sentence each: Martlet hosts on the home network this PC doesn't
    /// use, with why and how to bring them in, and a look that failed.</summary>
    public IReadOnlyList<string> Sentences() =>
        Left.OrderBy(l => l.Key, StringComparer.Ordinal).Select(l => $"Martlet found {l.Key} on your home network, but {l.Value.TrimEnd('.')}.")
            .Concat(Problem is { } problem ? [$"Martlet couldn't look for new hosts on your home network ({problem.TrimEnd('.')})."] : [])
            .ToArray();
}

/// <summary>
/// Finds hosts of this PC's Martlet network that it hasn't heard of yet on the home network (docs/NETWORK.md, "When a host is
/// added where your other computers can't reach"). A host paired on another of your computers reaches this PC through a host
/// both use; when that computer couldn't reach any of them, nothing told this PC. So, at most every <see cref="Interval"/>, a
/// member desktop asks the home network which hosts are there (<see cref="HostFind"/>, UDP 9444, never a subnet scan) and pairs
/// by itself with each one its roster doesn't list (<see cref="HostNetworkPairing.PairFoundAsync"/>). It keeps the host, and
/// the network copy the host brought, only when that copy lists the host with the key it showed, signed by a member desktop
/// this PC already trusts (<see cref="NetworkRoster.Accept"/>). A host of another network refuses the pairing and is left
/// alone for <see cref="LeaveAlone"/>. The desktop keeps one finder across syncs.
/// </summary>
public sealed class NetworkHostFinder(Func<NetworkRoster, CancellationToken, Task<IReadOnlyList<NetworkHostSighting>>> look,
    TimeProvider? clock = null)
{
    /// <summary>How often a member looks (the first sync after Martlet starts looks at once).</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);
    /// <summary>How long a host that isn't in this PC's network (or didn't let it pair) is left alone.</summary>
    public static readonly TimeSpan LeaveAlone = TimeSpan.FromMinutes(30);
    /// <summary>How long a look listens for answers.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(1.5);
    /// <summary>The gateway's default port, tried for every host that answers, with the ports of your network's hosts.</summary>
    public const int DefaultPort = 9443;
    internal const int MaximumHosts = 4;
    internal const int MaximumOrigins = 3;

    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly object gate = new();
    private readonly Dictionary<string, DateTimeOffset> leftAlone = new(StringComparer.Ordinal);
    private DateTimeOffset? next;

    /// <summary>The last look, or null before the first.</summary>
    public NetworkHostLook? Last { get; private set; }

    /// <summary>Looks on the next sync (Check now).</summary>
    public void LookSoon()
    {
        lock (gate) next = null;
    }

    /// <summary>Whether a look is due now; when it is, the next one is due an <see cref="Interval"/> later.</summary>
    internal bool Due()
    {
        var now = time.GetUtcNow();
        lock (gate)
        {
            if (next is { } at && now < at) return false;
            next = now + Interval;
            return true;
        }
    }

    internal Task<IReadOnlyList<NetworkHostSighting>> LookAsync(NetworkRoster roster, CancellationToken token) => look(roster, token);

    internal bool IsLeftAlone(string hostId)
    {
        lock (gate) return leftAlone.TryGetValue(hostId, out var until) && time.GetUtcNow() < until;
    }

    internal void Leave(string hostId)
    {
        lock (gate) leftAlone[hostId] = time.GetUtcNow() + LeaveAlone;
    }

    internal void Done(IReadOnlyList<string> answered, IReadOnlyList<string> found, IReadOnlyDictionary<string, string> left,
        string? problem = null) => Last = new(time.GetUtcNow(), answered, found, left, problem);

    /// <summary>The home network look: one <see cref="HostFind"/> for every host that answers within <see cref="Window"/>,
    /// each tried at the gateway's default port and the ports of the network's hosts.</summary>
    public static NetworkHostFinder HomeNetwork(TimeProvider? clock = null) => new(async (roster, token) =>
        Sightings(await HostFind.FindAsync(null, Window, token).ConfigureAwait(false), roster), clock);

    /// <summary>The origins to try for each host that answered: its private IPv4 address at the gateway's default port and the
    /// ports of <paramref name="roster"/>'s hosts (at most <see cref="MaximumOrigins"/>). An answer over loopback (a host on this
    /// PC, which Martlet pairs itself) is left out.</summary>
    public static IReadOnlyList<NetworkHostSighting> Sightings(IEnumerable<HostSighting> seen, NetworkRoster roster)
    {
        var ports = new[] { DefaultPort }
            .Concat(roster.ActiveHosts.Select(h => Uri.TryCreate(h.Origin, UriKind.Absolute, out var origin) ? origin.Port : DefaultPort))
            .Distinct().Take(MaximumOrigins).ToArray();
        return seen.Where(s => !IPAddress.IsLoopback(s.Address) && HostFind.IsPrivate(s.Address))
            .SelectMany(s => ports.Select(port => new NetworkHostSighting(s.HostId, $"https://{s.Address}:{port}"))).ToArray();
    }
}

/// <summary>One host of your Martlet network as this PC saw it on its last sync: whether this PC is paired with it and reached
/// it, and which of your other computers it says aren't paired with it (null when it didn't say: it didn't answer, or runs an
/// older Martlet). <paramref name="Note"/> is what the sync said about it.</summary>
public sealed record NetworkHostReach(NetworkMember Host, bool PairedHere, bool Reached, IReadOnlyList<NetworkMember>? Missing, string? Note);

/// <summary>
/// Where your computers don't see the same Martlet network yet, as this PC can tell after a sync, in plain words for the Share
/// page and the Devices map (docs/NETWORK.md, "When a host is added where your other computers can't reach"). Every computer
/// learns the network from the hosts it reaches, so a host this PC couldn't reach may not know the newest network, and a
/// computer that isn't paired with a host may not have heard of it.
/// </summary>
public sealed record NetworkReach(IReadOnlyList<NetworkHostReach> Hosts)
{
    /// <summary>How long a member may not be seen on any host this PC reads before its row says why that matters.</summary>
    public static readonly TimeSpan AwayAfter = TimeSpan.FromHours(12);

    public static NetworkReach None { get; } = new([]);

    /// <summary>Every active host of <paramref name="roster"/> except those forgotten here (<paramref name="ignored"/>).</summary>
    public static NetworkReach Of(NetworkRoster roster, string me, IReadOnlyCollection<string> pairedHere, IReadOnlyCollection<string> ignored,
        IReadOnlyDictionary<string, HostNetworkView> views, IReadOnlyDictionary<string, string> notes)
    {
        ArgumentNullException.ThrowIfNull(roster);
        var others = roster.ActiveDesktops.Where(d => d.Id != me).ToArray();
        var hosts = new List<NetworkHostReach>();
        foreach (var host in roster.ActiveHosts.Where(h => !ignored.Contains(h.Id)).OrderBy(h => h.Id, StringComparer.Ordinal))
        {
            var view = views.GetValueOrDefault(host.Id);
            var reached = view?.Roster?.NetworkId == roster.NetworkId;
            // A host lists at most 16 computers; with that many it can't say who is missing.
            IReadOnlyList<NetworkMember>? missing = reached && view!.Devices is { Count: < 16 } devices
                ? others.Where(d => devices.All(p => p.DeviceId != d.Id)).ToArray()
                : null;
            hosts.Add(new(host, pairedHere.Contains(host.Id), reached, missing, notes.GetValueOrDefault(host.Id)));
        }
        return new(hosts);
    }

    /// <summary>Hosts this PC couldn't reach on the last sync (not paired here, or no answer).</summary>
    public IReadOnlyList<NetworkHostReach> Unreached => Hosts.Where(h => !h.Reached).ToArray();

    /// <summary>The host's row: who isn't paired with it yet and why, or why this PC can't tell it the network; null when it
    /// sees the same network as far as this PC can tell. <paramref name="release"/> is the Martlet release that finds hosts on
    /// the home network by itself (this PC's).</summary>
    public string? HostText(string hostId, string release)
    {
        if (Hosts.FirstOrDefault(h => h.Host.Id == hostId) is not { } host) return null;
        if (!host.Reached)
            return host.PairedHere
                ? $"This PC couldn't reach it on its last check{Why(host.Note)}, so it may not have the newest network. It gets it when this PC " +
                  "or another of your computers reaches it."
                : $"Not paired with this PC yet{Why(host.Note)}. This PC pairs with it by itself as soon as it reaches it.";
        if (host.Missing is not { Count: > 0 } missing) return null;
        var silent = Unreached.Where(h => h.Host.Id != hostId).Select(h => h.Host.Id).ToArray();
        return $"{Names(missing)} {(missing.Count == 1 ? "isn't" : "aren't")} paired with it yet. " + (silent.Length > 0
            ? $"{(missing.Count == 1 ? "It hears" : "They hear")} about a new host from a host {(missing.Count == 1 ? "it uses" : "they use")}, and " +
              $"this PC couldn't reach {Either(silent)} to tell {(missing.Count == 1 ? "it" : "them")}. Martlet {release} or newer there finds " +
              $"{host.Host.Id} on your home network by itself."
            : $"Your other hosts know it, so {(missing.Count == 1 ? "it pairs" : "they pair")} with it by {(missing.Count == 1 ? "itself" : "themselves")} " +
              "the next time Martlet runs there and reaches it.");
    }

    /// <summary>One sentence for the network's status line: null when every computer sees the same network as far as this PC
    /// can tell.</summary>
    public string? Summary(string release)
    {
        var behind = Hosts.Where(h => h.Reached && h.Missing is { Count: > 0 }).ToArray();
        var silent = Unreached.Select(h => h.Host.Id).ToArray();
        if (behind.Length > 0)
        {
            var missing = behind.SelectMany(h => h.Missing!).DistinctBy(m => m.Id).ToArray();
            var hosts = behind.Select(h => h.Host.Id).ToArray();
            var others = silent.Except(hosts).ToArray();
            return $"Your computers don't all see the same network yet: {Names(missing)} {(missing.Length == 1 ? "isn't" : "aren't")} paired with " +
                $"{Both(hosts)} yet" + (others.Length > 0
                    ? $", and this PC couldn't reach {Either(others)} to tell {(missing.Length == 1 ? "it" : "them")}. Martlet {release} or newer " +
                      $"finds new hosts on your home network by itself; otherwise check that {Both(others)} {(others.Length == 1 ? "is" : "are")} on " +
                      "and that this PC reaches it."
                    : ". They pair by themselves the next time Martlet runs there.");
        }
        return silent.Length > 0
            ? $"This PC couldn't reach {Both(silent)} on its last check, so {(silent.Length == 1 ? "it" : "they")} may not have the newest network. " +
              $"Changes made here reach your other computers through the hosts this PC reaches; Martlet {release} or newer on them also " +
              "finds new hosts on your home network by itself."
            : null;
    }

    /// <summary>For a member's row: why a member that no host this PC reads has seen for <see cref="AwayAfter"/> may see another
    /// network than this PC; null otherwise.</summary>
    public static string? AwayText(string name, DateTimeOffset? lastSeen, DateTimeOffset now) =>
        lastSeen is { } at && now - at >= AwayAfter
            ? $"It hasn't used the hosts this PC reaches since then, so a host added on {name} shows here once it reaches one of them again, " +
              "or once this PC finds that host on your home network."
            : null;

    private static string Why(string? note) => note is { Length: > 0 } ? $" ({note.TrimEnd('.')})" : "";

    private static string Names(IReadOnlyList<NetworkMember> members) => Both(members.Select(m => m.Name).ToArray());

    private static string Both(IReadOnlyList<string> items) => Join(items, "and");

    private static string Either(IReadOnlyList<string> items) => Join(items, "or");

    private static string Join(IReadOnlyList<string> items, string word) => items.Count switch
    {
        0 => "",
        1 => items[0],
        2 => $"{items[0]} {word} {items[1]}",
        _ => string.Join(", ", items.Take(items.Count - 1)) + $" {word} {items[^1]}"
    };
}
