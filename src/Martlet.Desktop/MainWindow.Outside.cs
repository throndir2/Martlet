using System.IO;
using System.Net.Http;
using System.Windows;
using Martlet.Core.Nodes;
using Martlet.HostClient;
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
}
