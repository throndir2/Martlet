using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Core.Installation;
using Martlet.Core.Network;

namespace Martlet.Desktop;

/// <summary>Whether this PC stays awake for its own host service (docs/PLATFORMS.md): while the host service answers and
/// serves another of the owner's computers, Windows must not put this PC to sleep when it is left idle, or those computers
/// lose its jobs (speaking, lip-sync, listening...) until someone wakes it. Requests from other computers don't count as
/// activity to Windows, so a host PC left alone otherwise sleeps mid-conversation. A host service that misses a check
/// (restarting after an update, for example) keeps this PC awake for <see cref="Grace"/> before it may sleep again.</summary>
internal sealed class HostingWake
{
    internal static readonly TimeSpan Grace = TimeSpan.FromMinutes(2);
    private DateTimeOffset? servedAt;

    /// <summary>Why this PC stays awake ("Martlet: this PC's host service miku-host serves DIVA and IMOUTO"), or null when it
    /// may sleep.</summary>
    internal string? Reason { get; private set; }

    /// <summary>The host service this PC stays awake for and the computers it last served (null and empty while it may sleep).</summary>
    internal string? HostId { get; private set; }

    internal IReadOnlyList<string> Served { get; private set; } = [];

    /// <summary>Takes in what the latest check found: this PC's own host service (null when it runs none) and the other
    /// computers it serves (empty when it serves none, or didn't answer). Returns true when <see cref="Reason"/> changed.</summary>
    internal bool Observe(string? hostId, IReadOnlyList<string> served, DateTimeOffset now)
    {
        string? next;
        if (hostId is not null && served.Count > 0)
        {
            servedAt = now;
            HostId = hostId;
            Served = served;
            next = $"Martlet: this PC's host service {hostId} serves {Names(served)}";
        }
        else if (hostId is not null && Reason is not null && servedAt is { } at && now - at < Grace) next = Reason;
        else
        {
            servedAt = null;
            HostId = null;
            Served = [];
            next = null;
        }
        if (next == Reason) return false;
        Reason = next;
        return true;
    }

    /// <summary>The other computers this PC's own host service serves, by name: the computers paired with it as the host
    /// reported them on this PC's last network sync (for hosts older than that list, the network's other member desktops),
    /// none when it didn't answer that sync. Without a report from the host (a host PC not paired with its own host service),
    /// what this PC's Docker engine last read of it: the network's desktops while its gateway runs.</summary>
    internal static IReadOnlyList<string> ServedBy(HostNetworkView? view, NetworkRoster? roster, LocalHostServiceState? local,
        Func<string, bool> isThisDevice)
    {
        IEnumerable<string> names;
        if (view is not null)
            names = view.Devices is { } devices
                ? devices.Where(d => !isThisDevice(d.DeviceId)).Select(d => roster?.Desktop(d.DeviceId) is { Removed: false } member ? member.Name : d.DisplayName)
                : (roster ?? view.Roster)?.ActiveDesktops.Where(d => !isThisDevice(d.Id)).Select(d => d.Name) ?? [];
        else if (local is { Stage: LocalHostServiceStage.Running })
            names = local.Desktops.Where(d => !isThisDevice(d.Id)).Select(d => d.Name);
        else names = [];
        return names.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>"DIVA", "DIVA and IMOUTO", "A, B and C", "A, B, C and 2 more".</summary>
    internal static string Names(IReadOnlyList<string> names) => names.Count switch
    {
        0 => "",
        1 => names[0],
        <= 3 => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1],
        _ => string.Join(", ", names.Take(3)) + $" and {names.Count - 3} more"
    };
}
