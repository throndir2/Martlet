using System.IO;
using System.Net.Http;
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
        HostRoutes.Moved += move =>
        {
            if (!closing) Dispatcher.InvokeAsync(() => KeepMovedHost(move));
        };
    }

    /// <summary>A host was found at another address on the home network (its computer got a new one), or back at its paired
    /// address: keep that with its pairing (and move an SSH target that named the old address), so Martlet dials it there first
    /// next time too.</summary>
    private void KeepMovedHost(HostMove move)
    {
        if (closing || store is null || setupService is null) return;
        var name = move.HostId ?? move.Origin;
        var paired = move.Origin["https://".Length..];
        ErrorLog.Info(move.To is { } to
            ? $"Martlet network: found {name} at {to} on your home network (its computer got another address; it was paired at {paired}). " +
              "Martlet reaches it there now, with the same key and pairing."
            : $"Martlet network: {name} answers at its paired address {paired} again.");
        try
        {
            if (Pairings().KeepMoved(move) is { } host)
            {
                if (host.Shared) sharedHosts = HostRegistry.Upsert(sharedHosts, host);
                else homeHosts = HostRegistry.Upsert(homeHosts, host);
                if (host.SshTarget is { } ssh) ErrorLog.Info($"Martlet network: {name} is managed over SSH at {ssh} now.");
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            ErrorLog.Warn($"Martlet network: couldn't keep {name}'s new address with its pairing: {error.Message}");
        }
        RenderNetwork();
        RenderDevicePages();
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
                    (audit.InternetReachable ? "; limits apply to every address" : "") +
                    (audit.OutsideListeners.Count > 0 ? $"; also listening on {string.Join(", ", audit.OutsideListeners)} (overlay network)" : "");
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
        var router = local ? RouterPort.Load(store.DataDirectory) is { } kept && kept.HostId == host.HostId ? kept : null : null;
        var (dialog, blocked) = OutsideAccessDialog(name, docker, current, audit, pausedFromHere, () => OpenSignInSettings(host),
            routerPort: local, router: router, homeAddress: new Uri(host.Pairing.Origin).Authority);
        if (dialog.Ask(this) is not { } values) return;
        var addresses = values["addresses"].Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var normalized = addresses.Select(NetworkRoster.NormalizeAddress).ToArray();
        if (normalized.Any(a => a is null) || normalized.Length > NetworkRoster.MaximumAddresses)
        {
            ActionText.Text = $"Type up to {NetworkRoster.MaximumAddresses} addresses as name:port, IPv4:port or [IPv6]:port, for example home.example.net:9443.";
            return;
        }
        // Opening (or closing) a port on the router for this PC's host service.
        var wantRouter = local && values.GetValueOrDefault("router") == "yes";
        RouterPortState? opened = null;
        if (wantRouter && router is null)
        {
            if (blocked is not null)
            {
                ActionText.Text = $"{host.HostId} needs sign-in set up before it can be reached from outside: make a share link (Share › Friends) " +
                    "or set up sign-in first.";
                return;
            }
            ActionText.Text = "Asking your router to open a port for this PC's host service...";
            try { opened = await RouterPort.OpenAsync(host.HostId, host.Pairing.Origin, lifetime.Token); }
            catch (Exception error) when (error is InvalidOperationException or RouterPortException or HttpRequestException or System.Net.Sockets.SocketException
                or System.Xml.XmlException)
            {
                ActionText.Text = "Couldn't open a port on your router: " + error.Message;
                ErrorLog.Warn("Router port: " + error.Message);
                return;
            }
            normalized = RouterPort.WithAddress(normalized.OfType<string>().ToArray(), null, opened.Address).ToArray();
        }
        else if (!wantRouter && router is not null)
        {
            try { await RouterPort.CloseAsync(router, lifetime.Token); }
            catch (Exception error) when (error is RouterPortException or HttpRequestException or System.Net.Sockets.SocketException or TaskCanceledException)
            {
                ErrorLog.Warn("Router port: couldn't close the port on the router: " + error.Message);
            }
            normalized = normalized.Where(a => a != router.Address).ToArray();
            SaveRouterPort(null);
            ErrorLog.Info($"Router port: closed {router.Address} for {host.HostId}.");
            await WindowsFirewall.ApplyOutsideAsync(false, router.InternalPort, lifetime.Token);
        }
        if (blocked is not null && normalized.Any(a => !current.Contains(a!, StringComparer.Ordinal)))
        {
            ActionText.Text = $"{host.HostId} needs sign-in set up before it gets new outside addresses, so set up sign-in first, then add them.";
            return;
        }
        // Docker hides where a connection comes from: with a router port open, every connection must count as outside home, so the
        // guard's limits and pauses apply to the internet too.
        var treatAll = values["treatAll"] == "yes" || wantRouter;
        var arguments = NodeCommandRules.ExposureArguments(normalized.OfType<string>(), values["allowCodes"] == "yes", treatAll);
        var done = await RunHostActionAsync(host, HostAction.Exposure(arguments));
        if (done is null)
        {
            // The host didn't take the settings: nothing may forward to it from the internet.
            if (opened is not null)
            {
                try { await RouterPort.CloseAsync(opened, lifetime.Token); }
                catch (Exception error) when (error is RouterPortException or HttpRequestException or System.Net.Sockets.SocketException or TaskCanceledException)
                {
                    ErrorLog.Warn("Router port: couldn't close the port on the router again: " + error.Message);
                }
                ActionText.Text = "The host didn't take the new outside access, so Martlet closed the router port again (the progress window has details).";
            }
            return;
        }
        if (opened is not null)
        {
            SaveRouterPort(opened);
            ErrorLog.Info($"Router port: {opened.RouterName ?? "the router"} forwards port {opened.ExternalPort} to {opened.InternalClient}:{opened.InternalPort} " +
                $"for {host.HostId}" + (opened.AnsweredFromInside == true ? "; it answers there from this PC." : "; this PC can't test it from inside your home."));
            if (!await WindowsFirewall.OutsideRuleExistsAsync(lifetime.Token) &&
                await WindowsFirewall.ApplyOutsideAsync(true, opened.InternalPort, lifetime.Token) != WindowsFirewall.Outcome.Applied)
                ActionText.Text = "The router forwards the port, but Windows Firewall still lets only your home network in: open Outside access " +
                    "again and agree to Windows' prompt.";
        }
        networkSecurity.Remove(host.HostId);
        ErrorLog.Info($"Martlet network: {host.HostId} outside access set: {normalized.Length} outside address(es), typed pairing codes from " +
            $"outside home {(values["allowCodes"] == "yes" ? "allowed" : "refused")}, every connection treated as outside: {treatAll}.");
        QueueNetworkSync();
    }

    /// <summary>The Outside access dialog for a host (<paramref name="name"/>): its outside addresses, typed codes from outside
    /// and how connections are treated. While sign-in isn't usable on the host (<paramref name="audit"/>'s blocked reason, or a
    /// paused answer from outside) it says why, offers "Set up sign-in first" (<paramref name="setUpSignIn"/>), and only lets
    /// addresses be removed and typed codes be turned off. Returns the dialog and the blocked reason (null: none).</summary>
    internal static (HostInputDialog Dialog, string? Blocked) OutsideAccessDialog(string name, bool docker, IReadOnlyList<string> current,
        HostSecurityAudit? audit, bool pausedFromHere, Action setUpSignIn, bool routerPort = false, RouterPortState? router = null,
        string? homeAddress = null)
    {
        var dialog = new HostInputDialog("Outside access", $"Reach {name} from outside home",
            $"Addresses that reach {name}'s gateway when your laptop isn't at home, or for a friend you share it with: a Tailscale, " +
            "ZeroTier or WireGuard address (recommended) or your router's public address with a forwarded port.", "Save and restart gateway");
        dialog.AddExplanation("OutsideAccessAbout", "outside addresses", "Separate up to four addresses with commas, or leave the box empty " +
            "to remove them. Desktops try the home address first and these only when it doesn't answer, always checking the same host " +
            "key, so a proxy that ends TLS won't work. Saving restarts the host's gateway, and your network learns the addresses on its next sync.",
            wiki: "Sharing-with-Friends#outside-addresses");
        if (docker)
            dialog.AddExplanation("OutsideAccessDocker", "Docker and outside connections", "Behind Docker the host can't tell a connection " +
                "from home apart from one from outside, so keep Treat every connection as coming from outside home on when the port is " +
                "reachable from outside. Typed pairing codes then need Allow typed pairing codes from outside home, even at home; Martlet " +
                "pairing with its own host service doesn't.", wiki: "Sharing-with-Friends#outside-addresses");
        if (!routerPort && homeAddress is not null)
            dialog.AddExplanation("OutsideAccessByHand", "forwarding a port", $"To forward a port by hand, forward a TCP port to {homeAddress} " +
                "in your router's settings and add your public address with that port here.", wiki: "Sharing-with-Friends#outside-addresses");
        // A host becomes reachable from outside only once sign-in is set up on it (the host enforces it too).
        var blocked = audit?.OutsideAccessBlockedReason ?? (pausedFromHere ? "signin.not_set_up" : null);
        if (blocked is not null)
        {
            var why = blocked == "signin.no_allowed_identity" ? "sign-in on this host has no allowed identity" : "sign-in isn't set up on this host";
            var paused = (audit?.OutsideAccessPaused ?? pausedFromHere) && current.Count > 0;
            dialog.AddNote("OutsideAccessBlockedReason", (paused ? "Outside access is paused because " + why : char.ToUpperInvariant(why[0]) + why[1..]) +
                ", so set up an owner account with an authenticator, or a provider with an allowed identity, before you add addresses " +
                $"or allow typed codes from outside home ({blocked}).",
                "Set up sign-in first", "OutsideAccessSetUpSignIn", setUpSignIn);
        }
        dialog.AddText("addresses", "Outside addresses", string.Join(", ", current),
            "For example gpu-box.tailnet.ts.net:9443, 100.101.102.103:9443 or home.example.net:9443.", optional: true, maxLength: 600);
        if (routerPort)
        {
            dialog.AddCheck("router", "Let Martlet open a port on my router for it (UPnP), add the router's internet address here and keep it " +
                "open while Martlet runs", router is not null, enabled: blocked is null || router is not null);
            dialog.AddNote("OutsideAccessRouterPort", router is { } open
                ? $"Your router{(open.RouterName is { } routerName ? $" ({routerName})" : "")} forwards port {open.ExternalPort} to this PC " +
                  $"(since {open.OpenedAt.ToLocalTime():g}, renewed {open.RenewedAt.ToLocalTime():g}" +
                  (open.Problem is { } problem ? "; last problem: " + problem.TrimEnd('.') : "") + "): clear the box to close it."
                : "Most home routers open a port when Martlet asks (UPnP must be on in the router), and every request still needs a " +
                  "Martlet sign-in or share link.");
        }
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
