using System.Net;
using System.Text;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Core.Network;
using Martlet.Desktop;

namespace Martlet.Desktop.Tests;

/// <summary>Share links on the desktop (docs/NETWORK.md, "Sharing with a link"): which hosts get which link, how link friends show on
/// the Friends card, clicked links in the start arguments, and opening a port on the router for this PC's host service.</summary>
public sealed class ShareLinksTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    private static HostSignInSettings Settings(string hostId, IReadOnlyList<HostSignInLink>? links, HostSignInEnrolled[]? enrolled = null) =>
        new(hostId, "owner", 10, [], [], enrolled ?? [], null) { Links = links };

    // A valid link ID (16 bytes): the last character carries only padding bits.
    private static string Id(char c) => new string(c, 21) + "A";

    [Fact]
    public void Each_host_gets_exactly_the_links_made_for_it_and_older_hosts_get_none()
    {
        var all = new MadeShareLink { Id = Id('a'), Label = "Ana" };
        var some = new MadeShareLink { Id = Id('b'), Label = "Bo", Hosts = ["home-pc"] };
        var read = new Dictionary<string, HostSignInSettings>
        {
            // Has Ana's link already; misses Bo's.
            ["home-pc"] = Settings("home-pc", [new(all.Id, "Ana", Now, null)]),
            // A host added later: misses Ana's; still has Bo's from before Bo's link stopped covering it.
            ["gpu-box"] = Settings("gpu-box", [new(some.Id, "Bo", Now, ["home-pc", "gpu-box"])]),
            // Older than share links: nothing is sent.
            ["old-box"] = Settings("old-box", null)
        };
        var plan = ShareLinks.Plan([all, some], read);
        Assert.Equal([("gpu-box", all.Id, true), ("gpu-box", some.Id, false), ("home-pc", some.Id, true)],
            plan.Select(c => (c.HostId, c.Link.Id, c.Add)));
        // Nothing to do once every host keeps what it should, also after a rename reached them.
        read["home-pc"] = Settings("home-pc", [new(all.Id, "Ana", Now, null), new(some.Id, "Bo", Now, ["home-pc"])]);
        read["gpu-box"] = Settings("gpu-box", [new(all.Id, "Ana", Now, null)]);
        Assert.Empty(ShareLinks.Plan([all, some], read));
        Assert.Single(ShareLinks.Plan([all with { Label = "Ana B" }, some], read), c => c.HostId == "gpu-box");
        // A link made on another of your computers is left alone.
        read["gpu-box"] = Settings("gpu-box", [new(all.Id, "Ana", Now, null), new(Id('z'), "Zoe", Now, ["home-pc"])]);
        Assert.Empty(ShareLinks.Plan([all, some], read));
        // A stopped link is removed wherever a host still keeps it and never added again.
        Assert.Equal([("gpu-box", all.Id, false), ("home-pc", all.Id, false)],
            ShareLinks.Plan([all, some], read, [all.Id]).Select(c => (c.HostId, c.Link.Id, c.Add)));
        // A link a host says was stopped (on another of your computers) is stopped here too.
        read["old-box"] = Settings("old-box", []) with { RemovedLinks = [some.Id] };
        Assert.Equal([some.Id], ShareLinks.StoppedElsewhere([all, some], read).Select(l => l.Id));

        var change = ShareLinks.AddChange(some, ShareLink.NewSecret());
        Assert.Equal(("share-link", some.Id, "Bo", "home-pc"), ((string?)change["action"], (string?)change["id"], (string?)change["label"], (string?)change["hosts"]![0]));
        Assert.Equal(43, ((string?)change["verifier"])!.Length);
        Assert.Null(ShareLinks.AddChange(all, ShareLink.NewSecret())["hosts"]);
    }

    [Fact]
    public void A_link_friend_shows_with_the_hosts_that_keep_the_link_and_their_computers()
    {
        var made = new MadeShareLink { Id = Id('a'), Label = "Ana" };
        var read = new Dictionary<string, HostSignInSettings>
        {
            ["home-pc"] = Settings("home-pc", [new(made.Id, "Ana", Now, null)],
                [new("ana-pc", ShareLinks.Provider, made.Id, "Ana", Now) { Access = HostSignInAccess.Friend }]),
            ["gpu-box"] = Settings("gpu-box", []),
            ["old-box"] = Settings("old-box", null)
        };
        var friend = Assert.Single(FriendsOverview.Build(read, [made]));
        Assert.True(friend.IsLink);
        Assert.Equal(("Ana", "share link"), (friend.Name, friend.Way));
        Assert.Equal(["home-pc"], friend.SharedOn);
        Assert.Equal(["ana-pc"], friend.Hosts.SelectMany(h => h.Computers).Select(c => c.DeviceId));
        Assert.True(friend.Hosts.Single(h => h.HostId == "gpu-box").ProviderReady);
        Assert.False(friend.Hosts.Single(h => h.HostId == "old-box").ProviderReady);
        // A link this PC made shows before any host keeps it.
        Assert.Equal("Bo", Assert.Single(FriendsOverview.Build(new Dictionary<string, HostSignInSettings>(), [new MadeShareLink { Id = Id('b'), Label = "Bo" }])).Name);
        Assert.StartsWith("You share 1 host with 1 friend.", FriendsOverview.Status([friend], 3, 3), StringComparison.Ordinal);
    }

    [Fact]
    public void A_clicked_link_comes_out_of_the_start_arguments_and_is_handed_over_once()
    {
        Assert.Equal((string[])[], ShareLinkProtocol.Split(["martlet://share/abc"]).Others);
        Assert.Equal("martlet://share/abc", ShareLinkProtocol.Split(["martlet://share/abc"]).Link);
        Assert.Equal(["--data-directory", "x"], ShareLinkProtocol.Split(["--data-directory", "x", "MARTLET://share/abc"]).Others);
        Assert.Null(ShareLinkProtocol.Split(["--tray"]).Link);
        Assert.Equal("\"C:\\Program Files\\Martlet\\Martlet.Desktop.exe\" \"%1\"", ShareLinkProtocol.Command(@"C:\Program Files\Martlet\Martlet.Desktop.exe"));

        using var scope = new AvatarHostingTests.Scope();
        Assert.Empty(ShareLinkProtocol.TakeAllHandedOver(scope.DirectoryPath));
        ShareLinkProtocol.HandOver(scope.DirectoryPath, "martlet://share/abc");
        Assert.Equal(["martlet://share/abc"], ShareLinkProtocol.TakeAllHandedOver(scope.DirectoryPath));
        Assert.Empty(ShareLinkProtocol.TakeAllHandedOver(scope.DirectoryPath));

        // Two quick starts before the running Martlet looks: both links wait, oldest first, and no temporary file counts.
        File.WriteAllText(Path.Combine(scope.DirectoryPath, ShareLinkProtocol.HandOverFile), "martlet://share/old");
        ShareLinkProtocol.HandOver(scope.DirectoryPath, "martlet://share/one");
        ShareLinkProtocol.HandOver(scope.DirectoryPath, "martlet://share/two");
        File.WriteAllText(Path.Combine(scope.DirectoryPath, "pending-share-link-half.tmp"), "martlet://sh");
        Assert.Equal(["martlet://share/old", "martlet://share/one", "martlet://share/two"], ShareLinkProtocol.TakeAllHandedOver(scope.DirectoryPath));
        Assert.Empty(ShareLinkProtocol.TakeAllHandedOver(scope.DirectoryPath));

        var document = new ShareLinksDocument
        {
            Made = [new MadeShareLink { Id = Id('a'), Label = "Ana", Hosts = ["home-pc"], CreatedAt = Now }],
            Accepted = [new AcceptedShareLink { Id = Id('c'), From = "Sam", HostIds = ["sams-pc"], Skipped = ["lab"], AcceptedAt = Now }]
        };
        ShareLinks.Save(scope.DirectoryPath, document);
        var loaded = ShareLinks.Load(scope.DirectoryPath);
        Assert.Equal(["home-pc"], loaded.Made.Single().Hosts!);
        Assert.Equal((Id('c'), "Sam", "sams-pc", "lab"), (loaded.Accepted.Single().Id, loaded.Accepted.Single().From, loaded.Accepted.Single().HostIds.Single(), loaded.Accepted.Single().Skipped.Single()));
        Assert.DoesNotContain("secret", File.ReadAllText(Path.Combine(scope.DirectoryPath, ShareLinks.FileName)), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A router with one port mapping in use by another device, answering like miniupnpd.</summary>
    private sealed class FakeRouter(string external) : HttpMessageHandler
    {
        internal readonly Dictionary<int, string> Mappings = new() { [9443] = "192.168.1.99" };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var action = request.Headers.GetValues("SOAPAction").Single().Trim('"').Split('#')[1];
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            string Arg(string name) => body.Split("<" + name + ">")[1].Split("</" + name + ">")[0];
            string values;
            switch (action)
            {
                case "GetExternalIPAddress": values = $"<NewExternalIPAddress>{external}</NewExternalIPAddress>"; break;
                case "GetSpecificPortMappingEntry" when Mappings.TryGetValue(int.Parse(Arg("NewExternalPort")), out var client):
                    values = $"<NewInternalPort>9443</NewInternalPort><NewInternalClient>{client}</NewInternalClient>"; break;
                case "GetSpecificPortMappingEntry":
                    return new(HttpStatusCode.InternalServerError) { Content = new StringContent(
                        "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><s:Fault><detail><UPnPError><errorCode>714</errorCode></UPnPError></detail></s:Fault></s:Body></s:Envelope>") };
                default: Mappings[int.Parse(Arg("NewExternalPort"))] = Arg("NewInternalClient"); values = ""; break;
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(
                $"<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><u:{action}Response xmlns:u=\"x\">{values}</u:{action}Response></s:Body></s:Envelope>", Encoding.UTF8) };
        }
    }

    [Fact]
    public async Task Opening_a_router_port_skips_ports_other_devices_use_and_refuses_an_address_shared_by_the_provider()
    {
        // 192.0.2.0/24 answers nothing, so the check from inside says it couldn't reach it, quickly.
        var gateway = new RouterGateway(new Uri("http://192.168.1.1:5000/ctl/IPConn"), "urn:schemas-upnp-org:service:WANIPConnection:1", "192.168.1.10", "Home router");
        var router = new FakeRouter("192.0.2.1");
        using (var http = new HttpClient(router))
        {
            var opened = await RouterPort.OpenAsync(http, gateway, "home-pc", "https://192.168.1.10:9443", CancellationToken.None);
            Assert.Equal(("192.0.2.1:9444", "192.168.1.10", 9443, "home-pc"), (opened.Address, opened.InternalClient, opened.InternalPort, opened.HostId));
            Assert.Equal("192.168.1.10", router.Mappings[9444]);
            // Renewing keeps the same port.
            var again = await RouterPort.OpenAsync(http, gateway, "home-pc", "https://192.168.1.10:9443", CancellationToken.None, opened.ExternalPort);
            Assert.Equal(9444, again.ExternalPort);
        }
        using (var shared = new HttpClient(new FakeRouter("100.72.3.4")))
        {
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                RouterPort.OpenAsync(shared, gateway, "home-pc", "https://192.168.1.10:9443", CancellationToken.None));
            Assert.Contains("shared by your internet provider", refused.Message, StringComparison.Ordinal);
            Assert.Contains("Tailscale", refused.Message, StringComparison.Ordinal);
        }
        Assert.Equal(["198.51.100.2:9443", "gpu.tailnet.ts.net:9443"], RouterPort.WithAddress(["203.0.113.7:9443", "gpu.tailnet.ts.net:9443"], "203.0.113.7:9443", "198.51.100.2:9443"));
        Assert.Null(RouterPort.Unreachable("203.0.113.7"));
        Assert.Contains("another router", RouterPort.Unreachable("192.168.0.2"), StringComparison.Ordinal);
    }
}
