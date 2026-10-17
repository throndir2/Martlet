using Martlet.Avatar.Hosting;
using Martlet.Core.Installation;
using Martlet.Core.Network;
using Martlet.Core.Platforms;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Desktop.Tests;

/// <summary>Every computer is one device named after the computer, whatever system it runs and whether it runs the Martlet app,
/// a host service or both.</summary>
public sealed class ComputerNamesTests
{
    private static readonly MachineInfo Lumine = MachineInfo.Unknown with { Name = "Lumine", LanAddress = "192.168.50.20" };

    private static PairedHost Host(string id, string ip, HostSetupMethod method = HostSetupMethod.SshDocker) => new()
    {
        Pairing = new AvatarRemoteHost
        {
            Origin = $"https://{ip}:9443", HostId = id, SpkiFingerprint = "sha256:" + new string('a', 64),
            DeviceId = "desktop-lumine", CredentialId = new string('B', 22)
        },
        Method = method
    };

    private static HostHardware Report(string id, string? computer, string platform = "linux") =>
        new(id, "https://192.168.50.9:9443", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "docker", "Ubuntu 22.04.3 LTS", null, null, 4, 7.6,
            "Docker 20.10.23", "no", [])
        { ComputerName = computer, Platform = platform };

    private static IReadOnlyList<NetworkNode> Map(IReadOnlyList<PairedHost> hosts, IReadOnlyList<HostHardware>? hardware = null,
        IReadOnlyList<MartletComputer>? computers = null, AppSettings? settings = null) =>
        NetworkMap.Build(new(Lumine, DeviceRole.Companion, settings, null, false, new Dictionary<string, HostCheck>(), hardware, hosts,
            Computers: computers));

    private static string Fact(NetworkNode node, string label) => Assert.Single(node.Facts, f => f.Label == label).Value;

    [Fact]
    public void A_host_only_linux_computer_shows_by_the_name_it_gives_itself()
    {
        // Nahida, a NAS that runs only a host service in Docker: no Martlet app tells its name, so the host's report does.
        var nahida = Map([Host("Nahida-host", "192.168.50.9")], [Report("Nahida-host", "Nahida")]).Single(n => n.Id == "host:Nahida-host");
        Assert.Equal(NodeKind.Host, nahida.Kind);
        Assert.Equal("Nahida", nahida.Title);
        Assert.Equal("Nahida-host", nahida.Subtitle);
        Assert.Equal("Nahida", Fact(nahida, "Computer"));
        Assert.Equal("Nahida-host", Fact(nahida, "Host"));
        Assert.Equal("https://192.168.50.9:9443", Fact(nahida, "Address"));

        // Before it reports its name (an older host), the name its host ID was made from; a host ID chosen by hand stays as it is.
        Assert.Equal("Nahida", Map([Host("Nahida-host", "192.168.50.9")]).Single(n => n.Id == "host:Nahida-host").Title);
        Assert.Equal("gpu-box", Map([Host("gpu-box", "192.168.50.30")]).Single(n => n.Id == "host:gpu-box").Title);
        // Its name says its host ID already, so the line below says where it is.
        Assert.Equal("192.168.50.30", Map([Host("gpu-box", "192.168.50.30")]).Single(n => n.Id == "host:gpu-box").Subtitle);
        // Docker Desktop's engine name was never the computer's: an older host named after it keeps its ID as its name.
        Assert.Equal("docker-desktop-host", Map([Host("docker-desktop-host", "192.168.50.31")]).Single(n => n.Id == "host:docker-desktop-host").Title);
    }

    [Fact]
    public void Linux_mac_and_windows_hosts_are_drawn_the_same_way()
    {
        var nodes = Map([Host("Nahida-host", "192.168.50.9"), Host("adas-macbook-pro-host", "192.168.50.10"), Host("gpu-a-host", "192.168.50.11")],
            [Report("Nahida-host", "Nahida"), Report("adas-macbook-pro-host", "Adas-MacBook-Pro", "macos"), Report("gpu-a-host", "Gpu-A", "windows")]);
        var hosts = nodes.Where(n => n.Kind == NodeKind.Host).ToArray();
        Assert.Equal(["Nahida", "Adas-MacBook-Pro", "Gpu-A"], hosts.Select(n => n.Title));
        Assert.All(hosts, n =>
        {
            Assert.Equal(NetworkMap.ComputerGlyph, n.Glyph);
            Assert.Equal(n.Id["host:".Length..], n.Subtitle);
            Assert.Equal([n.Title], n.Facts.Where(f => f.Label == "Computer").Select(f => f.Value));
        });
    }

    [Fact]
    public void A_computer_with_the_martlet_app_and_a_host_service_is_one_device_named_by_the_app()
    {
        // Imouto's app says its name and host service; its host reports the same computer.
        var imouto = new MartletComputer("desktop-imouto", "Imouto", ComputerStanding.Member, null, true, Role: DeviceRole.Companion,
            HostId: "imouto-host");
        var nodes = Map([Host("imouto-host", "192.168.50.12")], [Report("imouto-host", "Imouto", "windows")], [imouto]);
        Assert.DoesNotContain(nodes, n => n.Id == "pc:desktop-imouto");
        var node = nodes.Single(n => n.Id == "host:imouto-host");
        Assert.Equal("Imouto", node.Title);
        // The host ID fits a narrow card; the device ID is in its details.
        Assert.Equal("imouto-host", node.Subtitle);
        Assert.Equal("Imouto (desktop-imouto)", Fact(node, "Computer"));
        Assert.Equal("Member", Fact(node, "Martlet network"));

        // An older Martlet that hasn't said its host service is still one device: the host that reports its computer's name.
        var older = imouto with { HostId = null, Name = "Studio" };
        var merged = Map([Host("render-box", "192.168.50.13")], [Report("render-box", "studio")], [older]);
        Assert.DoesNotContain(merged, n => n.Id == "pc:desktop-imouto");
        Assert.Equal("Studio", merged.Single(n => n.Id == "host:render-box").Title);
        // A friend's computer is never one of your hosts, whatever it is called.
        var friend = Map([Host("render-box", "192.168.50.13")], [Report("render-box", "studio")], [older with { Standing = ComputerStanding.Friend }]);
        Assert.Contains(friend, n => n.Id == "pc:desktop-imouto");
        Assert.Equal("studio", friend.Single(n => n.Id == "host:render-box").Title);
    }

    [Fact]
    public void A_model_server_on_one_of_your_computers_shows_on_that_computer()
    {
        AppSettings Thinking(string origin) => ChatCompletionsSetup.SelectRoute(SetupSettings.Begin(null), origin, "qwen3-8b");
        var nahida = Host("Nahida-host", "192.168.50.9");

        var onHost = Map([nahida], settings: Thinking("https://192.168.50.9:1234/v1"));
        Assert.DoesNotContain(onHost, n => n.Id.StartsWith("lan:", StringComparison.Ordinal));
        Assert.Contains(onHost.Single(n => n.Id == "host:Nahida-host").Roles, r => r.Detail.StartsWith("Local server:", StringComparison.Ordinal));

        var here = Map([nahida], settings: Thinking("https://192.168.50.20:1234/v1"));
        Assert.DoesNotContain(here, n => n.Id.StartsWith("lan:", StringComparison.Ordinal));
        Assert.Contains(here.Single(n => n.Kind == NodeKind.ThisPc).Roles, r => r.Detail.StartsWith("Local server:", StringComparison.Ordinal));

        // A server on a computer Martlet doesn't know stays a device of its own.
        Assert.Contains(Map([nahida], settings: Thinking("https://192.168.50.40:1234/v1")), n => n.Id == "lan:192.168.50.40");
    }

    [Fact]
    public void Status_texts_name_the_computer_not_its_host_id()
    {
        var job = new JobSituation
        {
            Job = Martlet.Core.Cluster.ClusterJobs.Thinking, Doer = JobDoer.Host, DoerName = "Miku",
            Host = new() { HostId = "miku-host", Name = "Miku", Reachable = false, SyncOn = true }
        };
        var coverage = JobCoverageRules.Evaluate(job);
        Assert.Equal("Miku isn't answering. Failover is off for this job.", coverage.Problem);
        Assert.Equal("Miku", coverage.HostName);
        Assert.Equal("miku-host", coverage.HostId);
        // Without a name, the host ID.
        Assert.StartsWith("miku-host isn't answering", JobCoverageRules.Evaluate(job with { Host = job.Host with { Name = null } }).Problem,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Each_computer_shares_the_name_it_gives_itself()
    {
        var written = new SharedPc(SharedPc.CompanionRole, "miko-host", "Miko").Write();
        Assert.Contains("\"computer_name\":\"Miko\"", written, StringComparison.Ordinal);
        Assert.Equal("Miko", SharedPc.Read(written)!.ComputerName);
        // An older Martlet's entry has no name; a name nobody can show is dropped.
        Assert.Null(SharedPc.Read("{\"role\":\"companion\"}")!.ComputerName);
        Assert.Null(SharedPc.Read("{\"role\":\"companion\",\"computer_name\":\"docker-desktop\"}")!.ComputerName);
        // This PC shows its own host name, as on Windows, Linux and macOS alike.
        Assert.Equal(ThisComputer.Name, MachineInfo.Unknown.Name);
    }
}
