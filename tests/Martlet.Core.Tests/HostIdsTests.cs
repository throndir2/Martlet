using Martlet.Core.Network;

namespace Martlet.Core.Tests;

/// <summary>One rule names every new host service after its computer, and every computer shows by the name it gives itself.</summary>
public sealed class HostIdsTests
{
    [Theory]
    [InlineData("DIVA", "diva-host")]
    [InlineData("Nahida", "nahida-host")]
    [InlineData("DESKTOP-IMOUTO", "desktop-imouto-host")]
    [InlineData("Ada's MacBook Pro", "adas-macbook-pro-host")]
    [InlineData(".hidden_Name.x", "hidden_name.x-host")]
    [InlineData("---", "martlet-host")]
    [InlineData("", "martlet-host")]
    [InlineData(null, "martlet-host")]
    public void A_new_host_id_follows_one_rule_on_every_system(string? computer, string hostId) =>
        // deploy/host/martlet-host's host_id_for gives the same IDs (checked with bash for these names).
        Assert.Equal(hostId, HostIds.ForComputer(computer));

    [Fact]
    public void A_host_id_is_short_enough_for_a_gateway()
    {
        var id = HostIds.ForComputer(new string('a', 80));
        Assert.Equal(58 + HostIds.Suffix.Length, id.Length);
        Assert.Matches(@"\A[A-Za-z0-9][A-Za-z0-9._-]{0,63}\z", id);
    }

    [Fact]
    public void A_computer_shows_by_its_reported_name_else_the_name_its_host_id_came_from()
    {
        Assert.Equal("Nahida", HostIds.ComputerName("Nahida-host", "Nahida"));
        Assert.Equal("Nahida", HostIds.ComputerName("Nahida-host"));
        Assert.Equal("diva", HostIds.ComputerName("diva-host"));
        Assert.Equal("Studio", HostIds.ComputerName("render-box", "Studio"));
        Assert.Equal("gpu-box", HostIds.ComputerName("gpu-box"));
        // Names that never were a computer's keep the host ID.
        Assert.Equal("martlet-host", HostIds.ComputerName("martlet-host"));
        Assert.Equal("docker-desktop-host", HostIds.ComputerName("docker-desktop-host"));
        Assert.Equal("docker-desktop-host", HostIds.ComputerName("docker-desktop-host", "docker-desktop"));
        Assert.Equal("-host", HostIds.ComputerName("-host"));
    }

    [Fact]
    public void Only_a_name_people_can_read_is_reported()
    {
        Assert.Equal("Miko", HostIds.CleanComputerName(" Miko "));
        Assert.Null(HostIds.CleanComputerName(null));
        Assert.Null(HostIds.CleanComputerName(""));
        Assert.Null(HostIds.CleanComputerName("Docker-Desktop"));
        Assert.Null(HostIds.CleanComputerName(new string('a', 64)));
        Assert.Null(HostIds.CleanComputerName("bad\u0007name"));
    }

    [Fact]
    public void This_computer_is_named_by_its_host_name_in_the_case_its_owner_set()
    {
        Assert.Equal("Miko", ThisComputer.FromHostName("Miko"));
        Assert.Equal("Nahida", ThisComputer.FromHostName("Nahida.local"));
        Assert.Null(ThisComputer.FromHostName(""));
        Assert.Null(ThisComputer.FromHostName(null));
        Assert.False(string.IsNullOrWhiteSpace(ThisComputer.Name));
        // Not the NetBIOS name in capitals, when the host name differs only in case.
        if (System.Net.Dns.GetHostName() is var host && host.Split('.')[0] is { Length: > 0 } shortName &&
            HostIds.CleanComputerName(shortName) is not null)
            Assert.Equal(shortName, ThisComputer.Name);
    }
}
