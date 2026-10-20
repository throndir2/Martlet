using Martlet.Core.Cluster;
using Martlet.Core.Installation;
using Martlet.HostClient;

namespace Martlet.Desktop.Tests;

/// <summary>The host dashboard's steps (Pages\HostDashboard), without a window.</summary>
public sealed class HostDashboardModelTests
{
    [Fact]
    public void DockerDesktopMissingOffersItsInstallAndHoldsTheHostServiceBack()
    {
        var services = new Services();
        var model = new HostDashboardModel(services);
        var state = new LocalHostServiceState { Stage = LocalHostServiceStage.DockerMissing };

        Assert.Equal("Needs Docker Desktop", model.Headline(state));
        var docker = model.DockerStep(state);
        Assert.False(docker.Done);
        Assert.Equal("Required for the host service.", docker.Detail);
        var install = Assert.Single(docker.Commands);
        Assert.Equal("Install Docker Desktop", install.Label);
        install.Run();
        Assert.Equal(["install docker"], services.Started);
        var service = model.ServiceStep(state);
        Assert.Equal("Runs in Docker Desktop; Martlet checks it again once Docker Desktop is running.", service.Detail);
        Assert.Empty(service.Commands);
        Assert.StartsWith("Docker Desktop isn't installed", model.ServiceSentence(state), StringComparison.Ordinal);
    }

    [Fact]
    public void ARunningHostPairedWithAnotherComputerIsReadyAndShowsItsName()
    {
        var services = new Services
        {
            Paired = [new HostPairedDevice("host-1", "laptop", "Laptop", DateTimeOffset.UtcNow, null)]
        };
        var model = new HostDashboardModel(services);
        var state = new LocalHostServiceState
        {
            Stage = LocalHostServiceStage.Running, Answering = true, AddressOnThisPc = true, HostId = "host-1", Version = AppVersions.Current,
            Roles = ["llm"]
        };

        Assert.Equal("Host is running", model.Headline(state));
        Assert.True(model.DockerStep(state).Done);
        Assert.True(model.ServiceStep(state).Done);
        var pair = model.PairStep(state);
        Assert.True(pair.Done);
        Assert.StartsWith("Paired with Laptop", pair.Detail, StringComparison.Ordinal);
        Assert.True(model.UpdateStep(state, setUp: true).Done);
        Assert.Equal("A, B and C", HostDashboardModel.JoinNames(["A", "B", "C"]));
    }

    private sealed class Services : IHostDashboardServices
    {
        internal List<string> Started { get; } = [];
        internal IReadOnlyList<HostPairedDevice> Paired { get; init; } = [];

        public MachineInfo Machine { get; } = MachineInfo.Unknown;
        public WindowsVirtualization? Virtualization => null;
        public bool HostMoving => false;
        public bool HasThisPcHost => false;
        public string? ThisPcHostVersion => null;
        public IReadOnlyList<HostPairedDevice> PairedComputers() => Paired;
        public string SelfDeviceId => "this-pc";
        public bool IsThisDevice(string deviceId) => deviceId == "this-pc";
        public bool UsesOwnHost => false;
        public DateTime? HostPairedAt => null;
        public bool NearbyBlocked => false;
        public bool ClusterEnabled => false;
        public ClusterPlan ClusterPlan => ClusterPlan.Empty;
        public bool UpdatingThisPcHost => false;
        public bool RunningTitled(string title) => false;
        public void InstallDocker() => Started.Add("install docker");
        public void StartDocker() => Started.Add("start docker");
        public void PrepareWindows() => Started.Add("prepare windows");
        public void SetUpHostService() => Started.Add("set up");
        public void LaunchHost(HostAction action) => Started.Add("launch " + action.Verb);
        public void LetComputersFind() => Started.Add("firewall");
    }
}
