using System.Net;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Avatar.Hosting;
using Martlet.Core.Network;
using Martlet.Core.Settings;

namespace Martlet.Desktop.Tests;

/// <summary>
/// A host added where your other computers can't reach (docs/NETWORK.md): MIKO pairs a NAS while it can't reach the host DIVA
/// and LUMINE use. Three simulated computers check what each one says (<see cref="NetworkReach"/>), that the Devices map draws
/// every host of the network on every computer with why it isn't the same yet (<see cref="NetworkMap.Build"/>), and which
/// addresses a find on the home network tries (<see cref="NetworkHostFinder.Sightings"/>).
/// </summary>
public sealed class NetworkReachTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 1, 50, 0, TimeSpan.Zero);

    private sealed record Network(NetworkRoster Roster, NetworkKey Miko, NetworkKey Diva, NetworkKey Lumine) : IDisposable
    {
        public void Dispose()
        {
            Miko.Dispose();
            Diva.Dispose();
            Lumine.Dispose();
        }
    }

    /// <summary>MIKO's copy: MIKO, DIVA and LUMINE, diva-host and the NAS MIKO just added.</summary>
    private static Network Owners()
    {
        var miko = NetworkKey.Create("desktop-miko");
        var diva = NetworkKey.Create("desktop-diva");
        var lumine = NetworkKey.Create("desktop-lumine");
        var roster = NetworkRoster.Found(miko, "MIKO", Now)
            .AddDesktop(miko, diva.DeviceId, "DIVA", diva.PublicKey, Now)
            .AddDesktop(miko, lumine.DeviceId, "LUMINE", lumine.PublicKey, Now)
            .AddHost(miko, "diva-host", "diva-host", "https://192.168.50.45:9443", "sha256:" + new string('d', 64), Now)
            .AddHost(miko, "Nahida-host", "Nahida-host", "https://192.168.50.9:9443", "sha256:" + new string('e', 64), Now);
        return new(roster, miko, diva, lumine);
    }

    private static HostNetworkView View(NetworkRoster roster, string hostId, params string[] paired) =>
        new(hostId, "bound", roster, [], Devices: paired.Select(id => new HostPairedDevice(hostId, id, id, Now, Now)).ToArray());

    [Fact]
    public void The_computer_that_added_a_host_says_who_doesnt_have_it_and_why()
    {
        using var network = Owners();
        // MIKO reached the NAS (only MIKO is paired with it) but not diva-host.
        var views = new Dictionary<string, HostNetworkView> { ["Nahida-host"] = View(network.Roster, "Nahida-host", "desktop-miko") };
        var notes = new Dictionary<string, string> { ["diva-host"] = "Didn't answer in time." };
        var reach = NetworkReach.Of(network.Roster, "desktop-miko", ["diva-host", "Nahida-host"], [], views, notes);

        Assert.Equal(["diva-host"], reach.Unreached.Select(h => h.Host.Id));
        Assert.Equal(["DIVA", "LUMINE"], reach.Hosts.Single(h => h.Host.Id == "Nahida-host").Missing!.Select(m => m.Name));
        Assert.Equal("Your computers don't all see the same network yet: DIVA and LUMINE aren't paired with Nahida-host yet, and this PC " +
            "couldn't reach diva-host to tell them. Martlet 0.89.0 or newer finds new hosts on your home network by itself; otherwise check " +
            "that diva-host is on and that this PC reaches it.", reach.Summary("0.89.0"));
        Assert.Equal("DIVA and LUMINE aren't paired with it yet. They hear about a new host from a host they use, and this PC couldn't reach " +
            "diva-host to tell them. Martlet 0.89.0 or newer there finds Nahida-host on your home network by itself.",
            reach.HostText("Nahida-host", "0.89.0"));
        Assert.Equal("This PC couldn't reach it on its last check (Didn't answer in time), so it may not have the newest network. It gets " +
            "it when this PC or another of your computers reaches it.", reach.HostText("diva-host", "0.89.0"));
    }

    [Fact]
    public void Every_computer_sees_the_same_network_once_all_are_paired_with_every_host()
    {
        using var network = Owners();
        var all = new[] { "desktop-miko", "desktop-diva", "desktop-lumine" };
        var views = new Dictionary<string, HostNetworkView>
        {
            ["Nahida-host"] = View(network.Roster, "Nahida-host", all), ["diva-host"] = View(network.Roster, "diva-host", all)
        };
        // As LUMINE sees it after it found the NAS: the same network everywhere, nothing to say.
        var reach = NetworkReach.Of(network.Roster, "desktop-lumine", ["diva-host", "Nahida-host"], [], views, new Dictionary<string, string>());
        Assert.Null(reach.Summary("0.89.0"));
        Assert.Null(reach.HostText("Nahida-host", "0.89.0"));
        // A host forgotten on this PC isn't counted, and a host that lists too many computers to tell can't say who is missing.
        Assert.DoesNotContain(NetworkReach.Of(network.Roster, "desktop-lumine", ["diva-host"], ["Nahida-host"], views,
            new Dictionary<string, string>()).Hosts, h => h.Host.Id == "Nahida-host");
    }

    [Fact]
    public void A_member_no_host_here_has_seen_for_long_says_why_it_may_see_another_network()
    {
        Assert.Null(NetworkReach.AwayText("MIKO", Now.AddHours(-2), Now));
        Assert.Null(NetworkReach.AwayText("MIKO", null, Now));
        Assert.Equal("It hasn't used the hosts this PC reaches since then, so a host added on MIKO shows here once it reaches one of them " +
            "again, or once this PC finds that host on your home network.", NetworkReach.AwayText("MIKO", Now.AddDays(-10), Now));
    }

    [Fact]
    public void The_devices_map_draws_every_host_of_the_network_and_why_it_isnt_the_same_yet()
    {
        // On DIVA after it learned of the NAS but before it could pair with it, and with the NAS missing LUMINE on MIKO's map.
        var hosts = new[]
        {
            new PairedHost
            {
                Pairing = new AvatarRemoteHost
                {
                    Origin = "https://192.168.50.45:9443", HostId = "diva-host", SpkiFingerprint = "sha256:" + new string('d', 64),
                    DeviceId = "desktop-diva", CredentialId = new string('B', 22)
                }
            }
        };
        var nodes = NetworkMap.Build(new(MachineInfo.Unknown, DeviceRole.Companion, null, null, false, new Dictionary<string, HostCheck>(),
            Hosts: hosts, NetworkHosts:
            [
                new("diva-host", "https://192.168.50.45:9443", true, "LUMINE isn't paired with it yet."),
                new("Nahida-host", "https://192.168.50.9:9443", false,
                    "In your Martlet network (added on MIKO) but not paired with this PC yet: it didn't answer in time at 192.168.50.9:9443.")
            ]));

        var nas = nodes.Single(n => n.Id == "host:Nahida-host");
        Assert.Equal(NodeHealth.Attention, nas.Health);
        Assert.Equal("Not paired with this PC yet", nas.HealthText);
        Assert.Contains(nas.Notes, n => n.Contains("added on MIKO", StringComparison.Ordinal));
        Assert.DoesNotContain(nas.Notes, n => n.Contains("once it's in your Martlet network", StringComparison.Ordinal));
        Assert.Contains(nas.Facts, f => f.Label == "Address" && f.Value == "https://192.168.50.9:9443");
        var diva = nodes.Single(n => n.Id == "host:diva-host");
        Assert.Equal("Not on all your computers yet", diva.HealthText);
        Assert.Equal("LUMINE isn't paired with it yet.", diva.Notes[0]);
    }

    [Fact]
    public void A_find_on_the_home_network_tries_the_default_port_and_the_ports_of_your_hosts_never_loopback()
    {
        using var network = Owners();
        var roster = network.Roster.AddHost(network.Miko, "gpu-box", "gpu-box", "https://192.168.50.60:9500", "sha256:" + new string('f', 64), Now);
        var seen = new[]
        {
            new HostSighting("Nahida-host", IPAddress.Parse("192.168.50.9")), new HostSighting("own-host", IPAddress.Loopback)
        };
        Assert.Equal(["https://192.168.50.9:9443", "https://192.168.50.9:9500"],
            NetworkHostFinder.Sightings(seen, roster).Select(s => s.Origin));
    }
}
