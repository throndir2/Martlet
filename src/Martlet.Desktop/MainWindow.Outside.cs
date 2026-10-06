using System.Windows;
using Martlet.Core.Nodes;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Core.Network;

namespace Martlet.Desktop;

/// <summary>
/// Reaching the network from outside home (docs/NETWORK.md): logs which route each host is reached over (home or an
/// outside address) when it changes, and every few minutes reads the security audit of each host that has outside
/// addresses, so failed attempts, lockouts and refused pairings from outside show on the Devices card and in the log.
/// </summary>
public partial class MainWindow
{
    private static readonly TimeSpan SecurityAuditInterval = TimeSpan.FromMinutes(5);
    private readonly Dictionary<string, (HostSecurityAudit? Audit, DateTimeOffset ReadAt)> networkSecurity = new(StringComparer.Ordinal);

    private void InitializeOutsideRoutes()
    {
        HostRoutes.RouteChanged += status =>
        {
            var host = status.HostId ?? status.Origin;
            if (status.Route == "outside")
                ErrorLog.Info($"Martlet network: reaching {host} from outside home ({status.Address}); its home address didn't answer.");
            else if (status.Route == "home" && status.Outside.Count > 0)
                ErrorLog.Info($"Martlet network: reaching {host} at home again.");
            else if (status.Route == "none" && status.Error is { } why)
                ErrorLog.Warn($"Martlet network: {host}: {why}");
            if (!closing) Dispatcher.InvokeAsync(RenderNetwork);
        };
    }

    /// <summary>Reads the audit of each paired host listed with outside addresses (at most every five minutes) and logs what
    /// is new: failed attempts, lockouts and refused pairings, with their sources.</summary>
    private async Task ReadSecurityAuditsAsync(NetworkRoster? roster)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var host in homeHosts)
        {
            // Hosts reachable from outside, and this PC's own host service (its Outside access choices show on the Devices map).
            if (roster?.Host(host.HostId) is not { Removed: false, Addresses.Count: > 0 } && host.Method is not (HostSetupMethod.ThisPcDocker or HostSetupMethod.Agent)) continue;
            var tried = networkSecurity.TryGetValue(host.HostId, out var known);
            if (tried && now - known.ReadAt < SecurityAuditInterval) continue;
            try
            {
                using var connection = ConnectToHost(GatewayAvatarHostLink.Pairing(host.Pairing));
                var audit = await connection.ReadSecurityAuditAsync(lifetime.Token);
                var since = known.Audit?.Events.LastOrDefault()?.At ?? DateTimeOffset.MinValue;
                var fresh = audit.Events.Where(e => e.At > since && e.Outcome is "failure" or "throttled" or "refused").ToArray();
                if (fresh.Length > 0)
                    ErrorLog.Warn($"Martlet network: {host.HostId} turned away {fresh.Length} request(s) since the last check " +
                        $"({string.Join(", ", fresh.GroupBy(e => e.Outcome).Select(g => $"{g.Count()} {g.Key}"))}) from " +
                        string.Join(", ", fresh.Select(e => $"{e.Source} ({e.SourceKind})").Distinct().Take(5)) +
                        $"; {audit.LockedOut} address(es) locked out now.");
                networkSecurity[host.HostId] = (audit, now);
            }
            catch (OperationCanceledException) when (!lifetime.IsCancellationRequested) { }
            catch (Exception error) when (ClusterSync.IsHostFailure(error) || error is InvalidOperationException)
            {
                // A host older than the audit (request.invalid) or not reachable now: try again on a later sync.
                networkSecurity[host.HostId] = (known.Audit, now);
            }
        }
    }

    /// <summary>Each managed host's outside access in words, for the Devices map's "Outside home" fact (MCP
    /// SelectedDeviceOutside): how many outside addresses it has, and its pairing-code and source choices once read.</summary>
    private IReadOnlyDictionary<string, string> HostOutsideFacts()
    {
        var facts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var host in homeHosts.Where(h => h.Method is HostSetupMethod.ThisPcDocker or HostSetupMethod.SshDocker or HostSetupMethod.SshNative or HostSetupMethod.Agent))
        {
            var count = networkState.Roster?.Host(host.HostId)?.Addresses?.Count ??
                networkViews.GetValueOrDefault(host.HostId)?.AdvertisedAddresses?.Count ?? 0;
            var text = count == 0 ? "No outside addresses" : count == 1 ? "1 outside address" : $"{count} outside addresses";
            if (networkSecurity.GetValueOrDefault(host.HostId).Audit is { } audit)
                text += (audit.OutsideAccessPaused ? "; Outside access paused: sign-in is off" : "") +
                    (audit.OutsideAccessBlockedReason is { } reason ? $"; sign-in needed first ({reason})" : "") + $"; pairing codes from outside home {(audit.AllowPairingOutsideHome ? "allowed" : "refused")}" +
                    (audit.TreatAllAsOutside ? "; every connection treated as outside home" : "") +
                    (audit.InternetReachable ? "; limits apply to every address" : "");
            facts[host.HostId] = text + ".";
        }
        return facts;
    }

    /// <summary>Outside access for this PC's host service or a host Martlet manages over SSH: asks for its outside addresses
    /// and choices and runs martlet-host exposure there (the gateway restarts). The host advertises the addresses and the
    /// next network sync signs them into the network.</summary>
    private async Task EditHostExposureAsync(PairedHost host)
    {
        if (store is null || closing) return;
        var local = host.Method == HostSetupMethod.ThisPcDocker;
        // A host run by Martlet on that computer is its Docker Desktop host service.
        var docker = host.Method is HostSetupMethod.ThisPcDocker or HostSetupMethod.SshDocker or HostSetupMethod.Agent;
        HostSecurityAudit? audit = null;
        var pausedFromHere = false;
        try
        {
            using var connection = ConnectToHost(GatewayAvatarHostLink.Pairing(host.Pairing));
            using var quick = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            quick.CancelAfter(TimeSpan.FromSeconds(4));
            audit = await connection.ReadSecurityAuditAsync(quick.Token);
            networkSecurity[host.HostId] = (audit, DateTimeOffset.UtcNow);
        }
        catch (Audio2FaceHostException error) when (error.Code == "outside.paused") { pausedFromHere = true; }
        catch (OperationCanceledException) when (!lifetime.IsCancellationRequested) { }
        catch (Exception error) when (ClusterSync.IsHostFailure(error) || error is InvalidOperationException) { }
        if (closing) return;
        var current = networkViews.GetValueOrDefault(host.HostId)?.AdvertisedAddresses ?? networkState.Roster?.Host(host.HostId)?.Addresses ?? [];
        var name = local ? "this PC's host service" : host.HostId;
        var (dialog, blocked) = OutsideAccessDialog(name, docker, current, audit, pausedFromHere, () => OpenSignInSettings(host));
        if (dialog.Ask(this) is not { } values) return;
        var addresses = values["addresses"].Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var normalized = addresses.Select(NetworkRoster.NormalizeAddress).ToArray();
        if (normalized.Any(a => a is null) || normalized.Length > NetworkRoster.MaximumAddresses)
        {
            ActionText.Text = $"Type up to {NetworkRoster.MaximumAddresses} addresses as name:port, IPv4:port or [IPv6]:port, for example home.example.net:9443.";
            return;
        }
        if (blocked is not null && normalized.Any(a => !current.Contains(a!, StringComparer.Ordinal)))
        {
            ActionText.Text = $"{host.HostId} needs sign-in set up before it gets new outside addresses. Set up sign-in first, then add them.";
            return;
        }
        var arguments = NodeCommandRules.ExposureArguments(normalized.OfType<string>(), values["allowCodes"] == "yes", values["treatAll"] == "yes");
        var done = await RunHostActionAsync(host, HostAction.Exposure(arguments));
        if (done is null) return;
        networkSecurity.Remove(host.HostId);
        ErrorLog.Info($"Martlet network: {host.HostId} outside access set: {normalized.Length} outside address(es), typed pairing codes from " +
            $"outside home {(values["allowCodes"] == "yes" ? "allowed" : "refused")}, every connection treated as outside: {values["treatAll"] == "yes"}.");
        QueueNetworkSync();
    }

    /// <summary>The Outside access dialog for a host (<paramref name="name"/>): its outside addresses, typed codes from outside
    /// and how connections are treated. While sign-in isn't usable on the host (<paramref name="audit"/>'s blocked reason, or a
    /// paused answer from outside) it says why, offers "Set up sign-in first" (<paramref name="setUpSignIn"/>), and only lets
    /// addresses be removed and typed codes be turned off. Returns the dialog and the blocked reason (null: none).</summary>
    internal static (HostInputDialog Dialog, string? Blocked) OutsideAccessDialog(string name, bool docker, IReadOnlyList<string> current,
        HostSecurityAudit? audit, bool pausedFromHere, Action setUpSignIn)
    {
        var dialog = new HostInputDialog("Outside access", $"Reach {name} from outside home",
            $"Addresses that reach {name}'s gateway when your laptop isn't at home: an overlay network address (Tailscale, ZeroTier, " +
            "WireGuard; recommended) or your router's public name with a forwarded port. Desktops try the home address first and " +
            "these only when it doesn't answer, always checking the same host key, so a proxy that ends TLS won't work. Separate " +
            "up to four with commas; leave empty to remove them. Saving restarts the host's gateway; your network learns the " +
            "addresses on its next sync." + (docker ? " Docker publishes the gateway's port, so the host can't tell a connection " +
            "from home apart from one from outside: keep \"Treat every connection as coming from outside home\" on when the " +
            "port is reachable from outside. Typed pairing codes then need \"Allow typed pairing codes from outside home\", even " +
            "at home; Martlet pairing with its own host service doesn't." : ""), "Save and restart gateway");
        // A host becomes reachable from outside only once sign-in is set up on it (the host enforces it too).
        var blocked = audit?.OutsideAccessBlockedReason ?? (pausedFromHere ? "signin.not_set_up" : null);
        if (blocked is not null)
            dialog.AddNote("OutsideAccessBlockedReason", ((audit?.OutsideAccessPaused ?? pausedFromHere) && current.Count > 0
                    ? "Outside access paused: sign-in is off. " : "") +
                (blocked == "signin.no_allowed_identity"
                    ? "Sign-in on this host has no allowed identity, "
                    : "Sign-in isn't set up on this host, ") +
                "so it can't get new outside addresses or accept typed codes from outside home. Set up an owner account with an " +
                "authenticator, or a provider with an allowed identity, first. Until then you can only remove addresses (they are " +
                $"kept otherwise) or change how connections are treated. ({blocked})",
                "Set up sign-in first", "OutsideAccessSetUpSignIn", setUpSignIn);
        dialog.AddText("addresses", "Outside addresses", string.Join(", ", current),
            "For example gpu-box.tailnet.ts.net:9443, 100.101.102.103:9443 or home.example.net:9443.", optional: true, maxLength: 600);
        var codesOn = audit?.AllowPairingOutsideHome ?? false;
        dialog.AddCheck("allowCodes", "Allow typed pairing codes from outside home (each code still closes after five wrong tries)",
            codesOn, enabled: blocked is null || codesOn);
        dialog.AddCheck("treatAll", "Treat every connection as coming from outside home (for a gateway behind Docker or a TCP relay)",
            audit?.TreatAllAsOutside ?? docker);
        return (dialog, blocked);
    }

    /// <summary>Settings › Sign-in from outside for <paramref name="host"/> (the sign-in window).</summary>
    private void OpenSignInSettings(PairedHost host)
    {
        if (store is null) return;
        var owner = Application.Current.Windows.OfType<Window>().LastOrDefault(w => w.IsActive) ?? this;
        new SignInSettingsWindow(host.Pairing, store.DataDirectory) { Owner = owner }.ShowDialog();
        networkSecurity.Remove(host.HostId);
    }

    /// <summary>The guard's totals a host reported (only for hosts with outside addresses), for its row and MCP.</summary>
    internal string HostSecurityText(string hostId) =>
        networkSecurity.GetValueOrDefault(hostId).Audit is { } seen && seen.Events.Count + seen.Failures + seen.Successes > 0
            ? $" Guard: {seen.Failures} failed and {seen.Throttled} throttled request(s) since it started, " +
              $"{seen.LockedOut} address(es) locked out now" +
              (seen.Events.Count(e => e.Outcome == "refused") is var refused and > 0 ? $", {refused} request(s) from outside refused." : ".") +
              (seen.OutsideAccessPaused ? " Outside access paused: sign-in is off." : "")
            : networkSecurity.GetValueOrDefault(hostId).Audit is { OutsideAccessPaused: true } ? " Outside access paused: sign-in is off." : "";
}
