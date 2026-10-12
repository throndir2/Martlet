using System.Net;
using Martlet.Gateway;

namespace Martlet.Gateway.Tests;

public sealed class OverlayListenerTests
{
    private static readonly GatewayOrigin Home = new("https://192.168.1.20:9443");

    private static string[] Select(string[] local, params string[] outside) =>
        GatewayOverlayListeners.Select(Home, local.Select(IPAddress.Parse), outside).Select(e => e.ToString()).ToArray();

    [Theory]
    [InlineData("100.64.0.1", true)]
    [InlineData("100.101.102.103", true)]
    [InlineData("100.127.255.254", true)]
    [InlineData("100.63.255.255", false)]
    [InlineData("100.128.0.1", false)]
    [InlineData("fd7a:115c:a1e0::1", true)]
    [InlineData("fd7a:115c:a1e1::1", false)]
    [InlineData("192.168.1.20", false)]
    [InlineData("::ffff:100.101.102.103", true)]
    public void Tailscale_addresses_are_recognized(string address, bool expected) =>
        Assert.Equal(expected, GatewayOverlayListeners.IsTailscale(IPAddress.Parse(address)));

    [Fact]
    public void A_host_listens_on_its_Tailscale_addresses_on_the_home_port()
    {
        var listen = Select(["192.168.1.20", "127.0.0.1", "100.101.102.103", "fd7a:115c:a1e0::5", "172.18.16.1", "fe80::1"],
            "gpu-box.tailnet.ts.net:9443");
        Assert.Equal(["100.101.102.103:9443", "[fd7a:115c:a1e0::5]:9443"], listen);
    }

    [Fact]
    public void An_outside_address_that_names_one_of_the_hosts_own_addresses_is_listened_on_at_its_port()
    {
        // A ZeroTier address (private range) and a Tailscale address on another port, both named by the owner.
        var listen = Select(["192.168.1.20", "10.147.17.5", "100.101.102.103", "203.0.113.7"],
            "10.147.17.5:9443", "100.101.102.103:8443", "home.example.net:9443");
        Assert.Equal(["10.147.17.5:9443", "100.101.102.103:8443"], listen);
    }

    [Fact]
    public void The_home_address_loopback_and_unnamed_addresses_are_never_added()
    {
        Assert.Empty(Select(["192.168.1.20", "127.0.0.1", "::1", "172.18.16.1", "203.0.113.7"], "192.168.1.20:9444", "127.0.0.1:9443"));
        Assert.Empty(Select(["192.168.1.20"], "100.101.102.103:9443"));
    }
}
