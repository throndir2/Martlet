using Martlet.Core.Cluster;
using Martlet.Core.Installation;
using Martlet.HostClient;

namespace Martlet.Desktop;

/// <summary>Everything the host dashboard's steps (<see cref="HostDashboardModel"/>) read and start in the app. MainWindow gives it
/// to the model (MainWindow.HostDashboardServices.cs). It has no WPF types. The runs the steps start (Docker Desktop, the host
/// service's setup, pairing, roles and updates) stay in the window (MainWindow.HostDashboard.cs).</summary>
internal interface IHostDashboardServices
{
    // ---------- what the steps read ----------

    /// <summary>This PC: its Docker Desktop, graphics cards and processor, as last read.</summary>
    MachineInfo Machine { get; }

    /// <summary>What Windows needs for Docker Desktop, read while Docker Desktop doesn't run; otherwise null.</summary>
    WindowsVirtualization? Virtualization { get; }

    /// <summary>Whether Martlet is moving the host service to this PC's new address now.</summary>
    bool HostMoving { get; }

    /// <summary>Whether this PC is paired with its own host service.</summary>
    bool HasThisPcHost { get; }

    /// <summary>The Martlet version this PC's host service runs, as last read, or null.</summary>
    string? ThisPcHostVersion { get; }

    /// <summary>The other computers paired with this PC's host service, as it said on the last network sync.</summary>
    IReadOnlyList<HostPairedDevice> PairedComputers();

    /// <summary>This computer's device ID on your Martlet network.</summary>
    string SelfDeviceId { get; }

    /// <summary>Whether <paramref name="deviceId"/> is this computer.</summary>
    bool IsThisDevice(string deviceId);

    /// <summary>Whether this PC uses its own host service (a pairing made on this PC's Docker Desktop).</summary>
    bool UsesOwnHost { get; }

    /// <summary>When Show a pairing code last paired a computer, until the host's network names it.</summary>
    DateTime? HostPairedAt { get; }

    /// <summary>Whether Windows Firewall keeps your other computers from finding this PC.</summary>
    bool NearbyBlocked { get; }

    /// <summary>Whether your Martlet network shares out the jobs, and its plan of who does which job.</summary>
    bool ClusterEnabled { get; }

    ClusterPlan ClusterPlan { get; }

    /// <summary>Whether Martlet updates this PC's host service now.</summary>
    bool UpdatingThisPcHost { get; }

    /// <summary>Whether a run window with <paramref name="title"/> works now.</summary>
    bool RunningTitled(string title);

    // ---------- what the steps start ----------

    void InstallDocker();

    void StartDocker();

    /// <summary>Turns on what Docker Desktop needs from Windows, in a run window.</summary>
    void PrepareWindows();

    void SetUpHostService();

    /// <summary>Runs <paramref name="action"/> on this PC's host service in a run window.</summary>
    void LaunchHost(HostAction action);

    /// <summary>Lets your other computers find this PC through Windows Firewall (administrator approval once).</summary>
    void LetComputersFind();
}
