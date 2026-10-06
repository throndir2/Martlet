using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Core.Installation;
using Martlet.Core.Network;

namespace Martlet.Desktop.Tests;

public sealed class HostingWakeTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 5, 17, 0, 0, TimeSpan.Zero);
    private static bool IsMiku(string id) => id == "desktop-miku";

    private static NetworkMember Desktop(string id, string name, bool removed = false) => new()
    {
        Kind = NetworkKinds.Desktop, Id = id, Name = name, Removed = removed, Revision = 1, UpdatedBy = id, Signature = "s"
    };

    private static readonly NetworkRoster Roster = new()
    {
        SchemaVersion = 1, NetworkId = "net",
        Members = [Desktop("desktop-diva", "DIVA"), Desktop("desktop-imouto", "IMOUTO"), Desktop("desktop-miku", "MIKU"),
            Desktop("desktop-old", "OLD", removed: true)]
    };

    private static HostPairedDevice Paired(string id, string name) => new("miku-host", id, name, Start, null);

    [Fact]
    public void ServesTheOtherComputersPairedWithTheHostByTheirNetworkNames()
    {
        var view = new HostNetworkView("miku-host", "bound", Roster, [],
            Devices: [Paired("desktop-miku", "MIKU"), Paired("desktop-imouto", "Imouto"), Paired("desktop-diva", "DIVA")]);

        Assert.Equal(["DIVA", "IMOUTO"], HostingWake.ServedBy(view, Roster, null, IsMiku));
    }

    [Fact]
    public void AHostOnlyThisPcUsesServesNobody()
    {
        var view = new HostNetworkView("miku-host", "bound", Roster, [], Devices: [Paired("desktop-miku", "MIKU")]);

        Assert.Empty(HostingWake.ServedBy(view, Roster, null, IsMiku));
    }

    [Fact]
    public void HostsOlderThanTheirPairedListServeTheNetworksOtherDesktops()
    {
        var view = new HostNetworkView("miku-host", "bound", Roster, []);

        Assert.Equal(["DIVA", "IMOUTO"], HostingWake.ServedBy(view, null, null, IsMiku));
    }

    [Fact]
    public void WithoutTheHostsReportOnlyARunningGatewayServesTheNetwork()
    {
        var running = new LocalHostServiceState
        {
            Stage = LocalHostServiceStage.Running, HostId = "miku-host",
            Desktops = [new("desktop-miku", "MIKU"), new("desktop-diva", "DIVA")]
        };

        Assert.Equal(["DIVA"], HostingWake.ServedBy(null, Roster, running, IsMiku));
        Assert.Empty(HostingWake.ServedBy(null, Roster, running with { Stage = LocalHostServiceStage.Stopped }, IsMiku));
        Assert.Empty(HostingWake.ServedBy(null, Roster, null, IsMiku));
    }

    [Fact]
    public void StaysAwakeWhileServingAndNamesWhy()
    {
        var wake = new HostingWake();

        Assert.True(wake.Observe(false, "miku-host", ["DIVA", "IMOUTO"], Start));
        Assert.Equal("Martlet: this PC's host service miku-host serves DIVA and IMOUTO", wake.Reason);
        Assert.Equal("miku-host", wake.HostId);
        Assert.False(wake.Observe(false, "miku-host", ["DIVA", "IMOUTO"], Start.AddSeconds(20)));
        Assert.True(wake.Observe(false, "miku-host", ["DIVA"], Start.AddSeconds(40)));
        Assert.Equal("Martlet: this PC's host service miku-host serves DIVA", wake.Reason);
    }

    [Fact]
    public void AMissedCheckKeepsItAwakeForTheGraceThenLetsItSleep()
    {
        var wake = new HostingWake();
        wake.Observe(false, "miku-host", ["DIVA"], Start);

        Assert.False(wake.Observe(false, "miku-host", [], Start + HostingWake.Grace - TimeSpan.FromSeconds(1)));
        Assert.NotNull(wake.Reason);
        Assert.True(wake.Observe(false, "miku-host", [], Start + HostingWake.Grace));
        Assert.Null(wake.Reason);
        Assert.Null(wake.HostId);
        Assert.Empty(wake.Served);
    }

    [Fact]
    public void NoHostServiceLetsItSleepAtOnce()
    {
        var wake = new HostingWake();

        Assert.False(wake.Observe(false, null, ["DIVA"], Start));
        Assert.Null(wake.Reason);
        wake.Observe(false, "miku-host", ["DIVA"], Start);
        Assert.True(wake.Observe(false, null, [], Start.AddSeconds(20)));
        Assert.Null(wake.Reason);
    }

    [Fact]
    public void AHostPcStaysAwakeWhetherItServesOrNot()
    {
        var wake = new HostingWake();

        Assert.True(wake.Observe(true, null, [], Start));
        Assert.Equal("Martlet: this PC is a Martlet host", wake.Reason);
        Assert.True(wake.HostPcOnly);
        Assert.True(wake.Observe(true, "miku-host", [], Start.AddSeconds(20)));
        Assert.Equal("Martlet: this PC is a Martlet host (miku-host)", wake.Reason);
        Assert.True(wake.Observe(true, "miku-host", ["DIVA"], Start.AddSeconds(40)));
        Assert.Equal("Martlet: this PC's host service miku-host serves DIVA", wake.Reason);
        Assert.False(wake.HostPcOnly);
        wake.Observe(true, "miku-host", [], Start.AddSeconds(60) + HostingWake.Grace);
        Assert.Equal("Martlet: this PC is a Martlet host (miku-host)", wake.Reason);
        Assert.True(wake.Observe(false, "miku-host", [], Start.AddSeconds(80) + HostingWake.Grace));
        Assert.Null(wake.Reason);
    }

    [Fact]
    public void NamesManyComputersBriefly()
    {
        Assert.Equal("A, B, C and 2 more", HostingWake.Names(["A", "B", "C", "D", "E"]));
        Assert.Equal("A, B and C", HostingWake.Names(["A", "B", "C"]));
    }

    [Fact]
    public void HoldsAndReleasesTheWindowsPowerRequest()
    {
        using var request = new PowerRequest();

        request.Hold("Martlet test: host service serves DIVA");
        Assert.Equal("Martlet test: host service serves DIVA", request.Reason);
        request.Hold("Martlet test: host service serves DIVA and IMOUTO");
        Assert.Equal("Martlet test: host service serves DIVA and IMOUTO", request.Reason);
        request.Hold(null);
        Assert.Null(request.Reason);
    }
}
