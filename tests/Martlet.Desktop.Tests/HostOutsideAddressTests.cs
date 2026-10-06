using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Avatar.Hosting;
using Martlet.Core.Network;
using Martlet.Desktop;

namespace Martlet.Desktop.Tests;

/// <summary>Outside addresses kept with each pairing (hosts.json), so a PC that has never been home reconnects whatever its
/// network state, and kept in step with the network roster.</summary>
public sealed class HostOutsideAddressTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 8, 0, 0, TimeSpan.Zero);
    private const string Spki = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static PairedHost Host(string id, string ip, IReadOnlyList<string>? outside = null) => new()
    {
        Pairing = new AvatarRemoteHost
        {
            Origin = $"https://{ip}:9443", HostId = id, SpkiFingerprint = Spki, DeviceId = "laptop-away", CredentialId = new string('C', 22)
        },
        OutsideAddresses = outside
    };

    [Fact]
    public void Saved_outside_addresses_reach_the_host_after_a_restart_until_the_roster_says_otherwise()
    {
        using var scope = new AvatarHostingTests.Scope();
        HostRegistry.Save(scope.DirectoryPath, [Host("away-host", "192.168.77.20", ["home.example.net:9443"]), Host("lan-host", "192.168.77.21")]);
        var loaded = HostRegistry.Load(scope.DirectoryPath);
        Assert.Equal(["home.example.net:9443"], loaded[0].OutsideAddresses!);
        Assert.Null(loaded[1].OutsideAddresses);
        Assert.Equal(["home.example.net:9443"], HostRoutes.For("https://192.168.77.20:9443")!.Outside);

        // The roster wins once it lists the host's addresses; a later load doesn't put the saved ones back.
        HostRoutes.Set("https://192.168.77.20:9443", "away-host", ["vpn.example.net:9443"]);
        HostRegistry.Load(scope.DirectoryPath);
        Assert.Equal(["vpn.example.net:9443"], HostRoutes.For("https://192.168.77.20:9443")!.Outside);
    }

    [Fact]
    public void Roster_sync_updates_the_saved_addresses_only_for_the_same_host_and_origin()
    {
        using var founder = NetworkKey.Create("home-pc");
        var roster = NetworkRoster.Found(founder, "HOME", Now)
            .AddHost(founder, "away-host", "away-host", "https://192.168.78.20:9443", Spki, Now)
            .AddHost(founder, "moved-host", "moved-host", "https://192.168.78.99:9443", Spki, Now);
        roster = roster.SetHostAddresses(founder, "away-host", ["new.example.net:9443", "100.101.102.103:9443"], Now.AddSeconds(1));
        roster = roster.SetHostAddresses(founder, "moved-host", ["moved.example.net:9443"], Now.AddSeconds(1));
        var hosts = new[]
        {
            Host("away-host", "192.168.78.20", ["old.example.net:9443"]), Host("moved-host", "192.168.78.21"), Host("other-host", "192.168.78.22")
        };
        var updated = HostRegistry.WithRosterAddresses(hosts, roster);
        Assert.Equal(["new.example.net:9443", "100.101.102.103:9443"], updated[0].OutsideAddresses!);
        // Listed under another home address: not the same pairing, left alone. Not listed: left alone.
        Assert.Same(hosts[1], updated[1]);
        Assert.Same(hosts[2], updated[2]);
        Assert.Same(updated[0], HostRegistry.WithRosterAddresses(updated, roster)[0]);
        Assert.Same(hosts, HostRegistry.WithRosterAddresses(hosts, null));

        var cleared = roster.SetHostAddresses(founder, "away-host", [], Now.AddSeconds(2));
        Assert.Null(HostRegistry.WithRosterAddresses(updated, cleared)[0].OutsideAddresses);
    }
}
