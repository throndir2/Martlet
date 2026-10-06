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
    private readonly Dictionary<string, (HostSecurityAudit Audit, DateTimeOffset ReadAt)> networkSecurity = new(StringComparer.Ordinal);

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
        if (roster is null) return;
        var now = DateTimeOffset.UtcNow;
        foreach (var host in homeHosts)
        {
            if (roster.Host(host.HostId) is not { Removed: false, Addresses.Count: > 0 }) continue;
            var known = networkSecurity.TryGetValue(host.HostId, out var seen) ? seen : default;
            if (known.Audit is not null && now - known.ReadAt < SecurityAuditInterval) continue;
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
                networkSecurity[host.HostId] = (known.Audit ?? new(host.HostId, true, false, false, 0, 0, 0, 0, []), now);
            }
        }
    }

    /// <summary>The guard's totals a host reported (only for hosts with outside addresses), for its row and MCP.</summary>
    internal string HostSecurityText(string hostId) =>
        networkSecurity.TryGetValue(hostId, out var seen) && seen.Audit.Events.Count + seen.Audit.Failures + seen.Audit.Successes > 0
            ? $" Guard: {seen.Audit.Failures} failed and {seen.Audit.Throttled} throttled request(s) since it started, " +
              $"{seen.Audit.LockedOut} address(es) locked out now" +
              (seen.Audit.Events.Count(e => e.Outcome == "refused") is var refused and > 0 ? $", {refused} pairing(s) from outside refused." : ".")
            : "";
}
