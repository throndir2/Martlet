using Martlet.Core.Cluster;
using Martlet.Core.Installation;
using Martlet.HostClient;

namespace Martlet.Desktop;

/// <summary>The services MainWindow gives the host dashboard's steps (<see cref="HostDashboardModel"/> in Pages\HostDashboard).</summary>
public partial class MainWindow : IHostDashboardServices
{
    private HostDashboardModel? hostDashboard;

    /// <summary>The host dashboard's steps, made once.</summary>
    private HostDashboardModel HostDashboard => hostDashboard ??= new(this);

    MachineInfo IHostDashboardServices.Machine => machine;
    WindowsVirtualization? IHostDashboardServices.Virtualization => virtualization;
    bool IHostDashboardServices.HostMoving => HostMoving;
    bool IHostDashboardServices.HasThisPcHost => ThisPcHost() is not null;
    string? IHostDashboardServices.ThisPcHostVersion => thisPcHostVersion;
    IReadOnlyList<HostPairedDevice> IHostDashboardServices.PairedComputers() => PairedComputers();
    string IHostDashboardServices.SelfDeviceId => NetworkIdentity.DeviceId(homeHosts);
    bool IHostDashboardServices.IsThisDevice(string deviceId) => IsThisDevice(deviceId);
    bool IHostDashboardServices.UsesOwnHost => homeHosts.Any(h => h.Method == HostSetupMethod.ThisPcDocker);
    DateTime? IHostDashboardServices.HostPairedAt => hostPairedAt;
    bool IHostDashboardServices.NearbyBlocked => NearbyBlocked;
    bool IHostDashboardServices.ClusterEnabled => clusterEnabled;
    ClusterPlan IHostDashboardServices.ClusterPlan => clusterPlan;
    bool IHostDashboardServices.UpdatingThisPcHost => hostUpdates.IsUpdating(ThisPcHostId);
    bool IHostDashboardServices.RunningTitled(string title) => HostRunWindow.IsRunningTitled(title);

    void IHostDashboardServices.InstallDocker() => InstallDocker();
    void IHostDashboardServices.StartDocker() => StartDocker();
    void IHostDashboardServices.PrepareWindows() => PrepareWindows();
    void IHostDashboardServices.SetUpHostService() => SetUpHostServiceAsync().Forget();
    void IHostDashboardServices.LaunchHost(HostAction action) => LaunchHost(action);
    void IHostDashboardServices.LetComputersFind() => NearbyFirewall_Click(this, new System.Windows.RoutedEventArgs());
}
