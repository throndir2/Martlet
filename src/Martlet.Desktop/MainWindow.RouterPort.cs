using System.Net.Http;
using System.Windows.Threading;
using Martlet.Core.Network;
using Martlet.Core.Nodes;

namespace Martlet.Desktop;

/// <summary>
/// Keeps the port Martlet opened on the router for this PC's host service (docs/NETWORK.md, "Opening a port on your router")
/// open while Martlet runs: renews it a minute after start and every six hours. When the router's internet address changed, it
/// updates the host's outside addresses (the gateway restarts) so your computers and friends reach the new one.
/// </summary>
public partial class MainWindow
{
    private DispatcherTimer? routerPortTimer;
    private bool routerPortBusy;

    private void SaveRouterPort(RouterPortState? state)
    {
        if (store is null) return;
        try { RouterPort.Save(store.DataDirectory, state); }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException)
        {
            ErrorLog.Warn("Router port: couldn't save router-port.json: " + error.Message);
        }
    }

    private void StartRouterPort()
    {
        routerPortTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        routerPortTimer.Tick += (_, _) =>
        {
            routerPortTimer.Interval = RouterPort.RenewEvery;
            RenewRouterPortAsync().Forget();
        };
        routerPortTimer.Start();
    }

    private async Task RenewRouterPortAsync()
    {
        if (store is null || closing || routerPortBusy || RouterPort.Load(store.DataDirectory) is not { } state) return;
        if (homeHosts.FirstOrDefault(h => h.HostId == state.HostId && h.Method == HostSetupMethod.ThisPcDocker) is not { } host) return;
        routerPortBusy = true;
        try
        {
            var renewed = await RouterPort.RenewAsync(state, host.Pairing.Origin, lifetime.Token);
            SaveRouterPort(renewed);
            if (renewed.Address == state.Address) return;
            ErrorLog.Info($"Router port: the router's internet address changed from {state.Address} to {renewed.Address}; updating {host.HostId}'s outside addresses.");
            var current = networkViews.GetValueOrDefault(host.HostId)?.AdvertisedAddresses ?? networkState.Roster?.Host(host.HostId)?.Addresses ?? [];
            var audit = networkSecurity.GetValueOrDefault(host.HostId).Audit;
            var arguments = NodeCommandRules.ExposureArguments(RouterPort.WithAddress(current, state.Address, renewed.Address),
                audit?.AllowPairingOutsideHome ?? false, treatAllAsOutside: true);
            if (await RunHostActionAsync(host, HostAction.Exposure(arguments)) is not null) QueueNetworkSync();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is InvalidOperationException or RouterPortException or HttpRequestException or
            System.Net.Sockets.SocketException or System.Xml.XmlException or OperationCanceledException)
        {
            SaveRouterPort(state with { Problem = error.Message });
            ErrorLog.Warn("Router port: couldn't renew the port on the router: " + error.Message);
        }
        finally { routerPortBusy = false; }
    }
}
