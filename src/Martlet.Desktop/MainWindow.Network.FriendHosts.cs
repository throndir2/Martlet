using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Martlet.HostClient;
using Martlet.Avatar.Hosting;
using Martlet.Core.Contracts;
using Martlet.Core.Network;
using Martlet.Core.Settings;
using Martlet.Credentials.Windows;

namespace Martlet.Desktop;

/// <summary>More of MainWindow.Network.cs, in a file of its own so that each file stays under 20 KB.</summary>
public partial class MainWindow
{
    /// <summary>The hosts a friend's computer signed in to, as they reported it on the last network sync.</summary>
    private IEnumerable<string> FriendHosts(string deviceId) => networkViews.Values
        .Where(v => v.Devices?.Any(d => d.DeviceId == deviceId && d.Friend) == true).Select(v => v.HostId).Order(StringComparer.Ordinal);

    /// <summary>Changes when a computer pairs with or leaves a host, joins or leaves the network, asks to join, or becomes
    /// active or idle: then the Devices map is drawn again (not on every sync, so the map doesn't animate every 20 seconds).</summary>
    private string NetworkDevicesSignature()
    {
        var now = DateTimeOffset.UtcNow;
        return string.Join(";", networkViews.OrderBy(v => v.Key, StringComparer.Ordinal).Select(v => v.Key + ":" +
            string.Join(",", (v.Value.Devices ?? []).Select(d => d.DeviceId + (Active(d, now) ? "+" : "-"))))) + "|" +
            string.Join(",", networkState.Roster?.ActiveDesktops.Select(d => d.Id + "=" +
                (settingsNode?.Document.Find(SharedPc.Key(d.Id))?.Value ?? "") + "/" +
                (settingsNode?.Document.Find(SharedPc.RoleKey(d.Id)) is { } role ? role.Value + role.UpdatedBy : "")) ?? []) + "|" +
            string.Join(",", networkJoins.Select(j => j.DeviceId)) + "|" +
            string.Join(",", networkState.Roster?.ActiveHosts.Select(h => h.Id) ?? []) + "|" + NetworkGap();
    }

    /// <summary>What the host dashboard shows about the network: requests to join and the computers paired with this PC's
    /// host service.</summary>
    private string HostDashboardNetworkSignature() =>
        string.Join(",", networkJoins.Select(j => j.DeviceId + j.CheckNumber)) + "|" + NetworkDevicesSignature() + "|" +
        string.Join(",", networkState.Roster?.ActiveDesktops.Select(d => d.Id) ?? []);

    /// <summary>Writes the network as this PC sees it to the desktop log when it changes (membership, requests to join, the
    /// computers paired with each host and each host's note), so the Diagnostics page shows why computers do or don't see
    /// each other.</summary>
    private void LogNetworkPicture(NetworkLocalState state)
    {
        var me = NetworkIdentity.DeviceId(homeHosts);
        var parts = new List<string>();
        if (state.Roster is { } roster)
            parts.Add($"{(Role == DeviceRole.Host ? "this host PC" : "this PC")} ({me}) is in network {roster.NetworkId} with " +
                string.Join(", ", roster.ActiveDesktops.Select(d => d.Id == me ? $"{d.Name} (this PC)" : $"{d.Name} ({d.Id})")) +
                "; hosts " + (roster.ActiveHosts.Any() ? string.Join(", ", roster.ActiveHosts.Select(h => h.Id)) : "none"));
        else if (state.Waiting is { } waiting)
            parts.Add($"this PC ({me}) waits to join network {waiting.NetworkId} through {waiting.HostId} (check number {waiting.CheckNumber}); " +
                "a member computer must allow it");
        else parts.Add(networkWatchOnly
            ? $"this host PC ({me}) is in no network and only watches its hosts; the main PC's network takes them"
            : $"this PC ({me}) is in no network");
        if (networkJoins.Count > 0)
            parts.Add("waiting to be allowed: " + string.Join(", ", networkJoins.Select(j => $"{j.DisplayName} ({j.DeviceId}, check number {j.CheckNumber}, through {j.HostId})")));
        foreach (var (hostId, view) in networkViews.OrderBy(v => v.Key, StringComparer.Ordinal))
            parts.Add($"{hostId} ({view.State}{(view.Bound ? " in " + view.Roster!.NetworkId : "")}) is paired with " + (view.Devices is not { } devices
                ? "(not reported; it runs an older Martlet)"
                : devices.Count == 0 ? "nobody"
                : string.Join(", ", devices.Select(d => IsThisDevice(d.DeviceId) ? $"{d.DeviceId} (this PC)"
                    : d.Friend ? $"{d.DeviceId} ({d.DisplayName}, a friend's computer)" : $"{d.DeviceId} ({d.DisplayName})"))));
        var picture = string.Join("; ", parts) + ".";
        if (picture != networkLogged)
        {
            networkLogged = picture;
            ErrorLog.Info("Martlet network: " + picture);
        }
        foreach (var hostId in networkNotesLogged.Keys.Where(id => !networkNotes.ContainsKey(id)).ToArray()) networkNotesLogged.Remove(hostId);
        foreach (var (hostId, note) in networkNotes)
            if (networkNotesLogged.GetValueOrDefault(hostId) != note)
            {
                networkNotesLogged[hostId] = note;
                ErrorLog.Info($"Martlet network: {hostId}: {note}");
            }
    }

    // ---------- presentation ----------

    private void RenderNetwork()
    {
        NetworkStatusText.Text = NetworkStatusLine();
        var gap = NetworkGap();
        NetworkGapText.Text = gap ?? "";
        NetworkGapCard.Visibility = gap is null ? Visibility.Collapsed : Visibility.Visible;
        NetworkCheckButton.IsEnabled = store is not null;
        NetworkJoinsPanel.Children.Clear();
        foreach (var join in networkJoins) NetworkJoinsPanel.Children.Add(JoinRow(join));
        NetworkMembersPanel.Children.Clear();
        var roster = networkState.Roster;
        if (roster is not null)
        {
            var me = roster.Desktop(NetworkIdentity.DeviceId(homeHosts));
            foreach (var member in roster.Members.Where(m => !m.Removed).OrderBy(m => m.IsHost).ThenBy(m => m.Id != me?.Id).ThenBy(m => m.Id, StringComparer.Ordinal))
                NetworkMembersPanel.Children.Add(MemberRow(roster, member, member.IsDesktop && member.Id == me?.Id));
        }
        // Computers that use one of these hosts but aren't (yet) in the network still show, so every PC sees who is connected.
        foreach (var device in OutsideComputers(roster)) NetworkMembersPanel.Children.Add(PairedRow(device));
        RenderWorkSharing();
        // Settings lists your other computers with the switch between companion and host PC; members come and go here.
        if (SettingsPage.IsVisible) RenderOtherRoles();
    }

    /// <summary>Each computer paired with one of this PC's hosts that is not a member of the network (or of any network, on a
    /// host PC that only watches), not this PC and not already asking to join: its most recently active pairing. A friend's
    /// computer (<see cref="HostPairedDevice.Friend"/>) is listed as one, never as a computer to let in.</summary>
    private IEnumerable<HostPairedDevice> OutsideComputers(NetworkRoster? roster) => networkViews.Values
        .SelectMany(v => v.Devices ?? [])
        .Where(d => !IsThisDevice(d.DeviceId) && roster?.Desktop(d.DeviceId) is not { Removed: false } && networkJoins.All(j => j.DeviceId != d.DeviceId))
        .GroupBy(d => d.DeviceId, StringComparer.Ordinal)
        .Select(g => g.OrderByDescending(d => d.Friend).ThenByDescending(d => d.LastSeen ?? DateTimeOffset.MinValue).First())
        .OrderBy(d => d.DisplayName, StringComparer.OrdinalIgnoreCase);

    /// <summary>Where a member computer was last active, from its hosts' reports ("active now on diva-host"), or null. A member no
    /// host here has seen for a long time also says why it may see another network than this PC.</summary>
    private string? MemberActivity(string deviceId)
    {
        var seen = networkViews.Values.SelectMany(v => v.Devices ?? []).Where(d => d.DeviceId == deviceId)
            .OrderByDescending(d => d.LastSeen ?? DateTimeOffset.MinValue).FirstOrDefault();
        if (seen is null) return null;
        var text = Seen(seen.LastSeen, DateTimeOffset.UtcNow);
        var line = $"{char.ToUpperInvariant(text[0])}{text[1..]} on {seen.HostId}.";
        return networkState.Roster?.Desktop(deviceId) is { Removed: false } member &&
            NetworkReach.AwayText(member.Name, seen.LastSeen, DateTimeOffset.UtcNow) is { } away ? line + " " + away : line;
    }

    /// <summary>What isn't the same on all your computers yet, in plain words (docs/NETWORK.md): which of your computers don't
    /// know a host and why, hosts this PC couldn't reach, and Martlet hosts on your home network outside your network with how to
    /// bring them in. Null when nothing; the Share page and the Devices page show it.</summary>
    private string? NetworkGap()
    {
        if (networkState.Roster is null) return null;
        var parts = new List<string>();
        if (networkReach.Summary(AppVersions.Current) is { } summary) parts.Add(summary);
        if (networkFinder.Last is { } look) parts.AddRange(look.Sentences());
        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    /// <summary>Writes <see cref="NetworkGap"/> to the desktop log when it changes.</summary>
    private void LogNetworkReach()
    {
        var text = NetworkGap();
        if (text == networkReachLogged) return;
        networkReachLogged = text;
        ErrorLog.Info("Martlet network: " + (text ?? "every computer sees the same network, as far as this PC can tell."));
    }

    /// <summary>Every active host of your network (except those forgotten here) for the Devices map: whether this PC is paired
    /// with it and, in plain words, what isn't the same on all your computers yet.</summary>
    private IReadOnlyList<NetworkHostState>? NetworkHostStates() => networkState.Roster is not { } roster ? null
        : roster.ActiveHosts.Where(h => !networkState.Ignored.Contains(h.Id)).Select(h =>
        {
            // homeHosts, not FindHost: FindHost reads the map's inputs, which include this list.
            var paired = homeHosts.Any(p => p.HostId == h.Id);
            var reach = networkReach.Hosts.FirstOrDefault(r => r.Host.Id == h.Id);
            // A paired host that didn't answer shows that on its own status; here only who doesn't know it, or why it isn't paired.
            var gap = paired && reach is { Reached: false } ? null
                : paired ? networkReach.HostText(h.Id, AppVersions.Current)
                : $"In your Martlet network (added on {roster.Desktop(h.UpdatedBy)?.Name ?? h.UpdatedBy}) but not paired with this PC yet" +
                  (networkNotes.GetValueOrDefault(h.Id) is { } note ? $": {note.Replace("Not paired yet: ", "", StringComparison.Ordinal).TrimEnd('.')}" : "") +
                  "; this PC pairs with it by itself as soon as it reaches it.";
            return new NetworkHostState(h.Id, h.Origin ?? "", paired, gap);
        }).ToArray();

    private string NetworkStatusLine()
    {
        if (store is null) return "Unavailable without a local data folder.";
        if (networkProblem is not null) return "Couldn't check your network this time: " + networkProblem;
        if (networkState.Roster is { } roster)
        {
            var others = roster.ActiveDesktops.Count() - 1;
            var hosts = roster.ActiveHosts.Count();
            var away = roster.ActiveHosts.Count(h => HostRoutes.For(h.Origin, h.Spki) is { Route: "outside" });
            return $"This PC is in your Martlet network with {Count(others, "other computer")} and {Count(hosts, "host")}" +
                (away > 0 ? $", {away} reached from outside home right now" : "") +
                (networkCheckedAt is { } at ? $"; checked {at:t}." : ".");
        }
        if (networkState.Waiting is { } wait)
            return $"Waiting to join: on another of your computers, open Share and allow {NetworkIdentity.DeviceId(homeHosts)} under Your Martlet " +
                $"network if it shows {wait.CheckNumber} (asked through {wait.HostId}).";
        if (homeHosts.Count == 0)
            return "Not in a network yet: add a computer, and the first host you pair starts your Martlet network.";
        if (networkState.RemovedFrom is not null)
            return "This PC was removed from its Martlet network; pair a host to ask to join again or to start a new network.";
        if (networkWatchOnly)
            return "This host PC has no Martlet network of its own; it joins the network of the main PC that pairs with its host service" +
                (networkCheckedAt is { } seen ? $" (checked {seen:t})." : ".");
        return networkCheckedAt is null ? "Checking your hosts..."
            : "Not in a network: your hosts run an older Martlet or didn't answer, so update them with Update host.";

        static string Count(int count, string what) => count == 1 ? $"1 {what}" : $"{count} {what}s";
    }

    private FrameworkElement JoinRow(HostJoinRequest join)
    {
        var row = NetworkRowFrame();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var allow = new Button { Content = "Allow", Margin = new Thickness(8, 0, 0, 0) };
        allow.SetResourceReference(StyleProperty, "PrimaryButton");
        AutomationProperties.SetAutomationId(allow, "NetworkAllow-" + join.DeviceId);
        AutomationProperties.SetName(allow, $"Allow {join.DisplayName} into your Martlet network");
        allow.Click += (_, _) => AllowJoin(join);
        var deny = new Button { Content = "Turn down", Margin = new Thickness(8, 0, 0, 0) };
        AutomationProperties.SetAutomationId(deny, "NetworkDeny-" + join.DeviceId);
        AutomationProperties.SetName(deny, $"Turn down {join.DisplayName}'s request to join");
        deny.Click += (_, _) => DenyJoinAsync(join).Forget();
        buttons.Children.Add(allow);
        buttons.Children.Add(deny);
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);
        row.Children.Add(NetworkRowText("NetworkJoin-" + join.DeviceId, $"{join.DisplayName} asks to join",
            $"{join.DeviceId}, through {join.HostId}, check number {join.CheckNumber}: allow it only if that computer shows the same number."));
        return NetworkRowCard(row, warning: true);
    }

    private FrameworkElement MemberRow(NetworkRoster roster, NetworkMember member, bool thisPc)
    {
        var row = NetworkRowFrame();
        if (!thisPc)
        {
            var remove = new Button { Content = "Remove from network", VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetAutomationId(remove, $"NetworkRemove-{member.Kind}-{member.Id}");
            AutomationProperties.SetName(remove, $"Remove {member.Name} from your Martlet network");
            remove.Click += (_, _) => RemoveFromNetwork(member);
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);
        }
        if (member.IsHost)
        {
            var outside = new Button { Content = "Outside addresses", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            AutomationProperties.SetAutomationId(outside, "NetworkOutside-" + member.Id);
            AutomationProperties.SetName(outside, $"Set how {member.Name} is reached from outside home");
            outside.Click += (_, _) => EditOutsideAddresses(member);
            DockPanel.SetDock(outside, Dock.Right);
            row.Children.Add(outside);
        }
        string detail;
        if (member.IsDesktop)
            detail = (thisPc ? "This PC" : "Computer") + (roster.Founder?.Id == member.Id ? ", started the network" : $", allowed on {member.UpdatedBy}") + "." +
                (!thisPc && MemberActivity(member.Id) is { } activity ? " " + activity : "");
        else
        {
            var paired = FindHost(member.Id) is not null;
            detail = "Host, " + (paired ? "paired with this PC" : networkState.Ignored.Contains(member.Id) ? "forgotten on this PC (it stays in the network)" : "not paired with this PC yet") +
                $"; added on {member.UpdatedBy}." + " " + HostRouteText(member) + HostSecurityText(member.Id) + (networkNotes.GetValueOrDefault(member.Id) is { } note ? " " + note : "") +
                (paired && networkReach.HostText(member.Id, AppVersions.Current) is { } reach ? " " + reach : "");
        }
        row.Children.Add(NetworkRowText($"NetworkMember-{member.Kind}-{member.Id}",
            member.IsDesktop && member.Name != member.Id ? $"{member.Name} ({member.Id})" : member.Name, detail));
        return NetworkRowCard(row, warning: false);
    }

    /// <summary>A computer that uses one of this PC's hosts but is not in the network: it can use the hosts it paired with, but
    /// isn't paired with the network's other hosts by itself. A friend's computer says so: it uses those hosts' engines only and
    /// never joins.</summary>
    private FrameworkElement PairedRow(HostPairedDevice device)
    {
        var row = NetworkRowFrame();
        var hosts = networkViews.Values.Where(v => v.Devices?.Any(d => d.DeviceId == device.DeviceId) == true).Select(v => v.HostId).Order(StringComparer.Ordinal);
        var detail = device.Friend
            ? $"A friend's computer that signed in to {string.Join(", ", FriendHosts(device.DeviceId))} as a friend and uses only " +
              $"{(FriendHosts(device.DeviceId).Count() == 1 ? "that host's" : "those hosts'")} engines; {Seen(device.LastSeen, DateTimeOffset.UtcNow)}."
            : $"Uses {string.Join(", ", hosts)}; {Seen(device.LastSeen, DateTimeOffset.UtcNow)}; " + (networkState.Roster is null
                ? "it is paired with this PC's host service, and this PC is in no Martlet network itself."
                : "it isn't in your Martlet network, so update Martlet on it to make it ask to join.");
        row.Children.Add(NetworkRowText((device.Friend ? "NetworkFriend-" : "NetworkPaired-") + device.DeviceId,
            device.DisplayName != device.DeviceId ? $"{device.DisplayName} ({device.DeviceId})" : device.DeviceId, detail));
        return NetworkRowCard(row, warning: false);
    }
}
