using Martlet.Core.Settings;
using Martlet.Desktop;

namespace Martlet.Desktop.Tests;

public sealed class DeviceOverviewTests
{
    private static NetworkNode Node(string id, NodeKind kind, string title, NodeHealth health = NodeHealth.Ready, bool job = false) =>
        new(id, kind, title, "subtitle", "", health, health == NodeHealth.Attention ? "Not reachable" : "Connected",
            job ? [new HostedRole("Thinks", "Thinking", "", DeviceComponent.Job(SetupRole.Llm))] : [], [], [], []);

    private static List<NetworkNode> Network(int hosts, int clouds = 1)
    {
        List<NetworkNode> nodes = [Node("this-pc", NodeKind.ThisPc, "This PC")];
        for (var i = 1; i <= clouds; i++) nodes.Add(Node($"cloud:{i}", NodeKind.Cloud, $"Cloud {i}"));
        for (var i = 1; i <= hosts; i++) nodes.Add(Node($"host:h{i:00}", NodeKind.Host, $"h{i:00}"));
        nodes.Add(Node("add", NodeKind.Add, "Add a computer", NodeHealth.Unknown));
        return nodes;
    }

    [Fact]
    public void A_small_network_shows_every_device_on_the_map()
    {
        var nodes = Network(hosts: 6);
        Assert.True(DeviceOverview.FitsMap(nodes));
        Assert.Equal(nodes.Select(n => n.Id), DeviceOverview.MapNodes(nodes, "this-pc").Select(n => n.Id));
        Assert.True(DeviceOverview.FitsMap(Network(hosts: 32)));
        Assert.False(DeviceOverview.FitsMap(Network(hosts: 33)));
    }

    [Fact]
    public void Fifty_hosts_fold_into_a_more_card_keeping_attention_jobs_and_the_selection()
    {
        var nodes = Network(hosts: 50);
        nodes[30] = Node(nodes[30].Id, NodeKind.Host, nodes[30].Title, NodeHealth.Attention);
        nodes[40] = Node(nodes[40].Id, NodeKind.Host, nodes[40].Title, job: true);
        nodes[45] = Node(nodes[45].Id, NodeKind.Host, nodes[45].Title, NodeHealth.Attention);
        Assert.False(DeviceOverview.FitsMap(nodes));

        var map = DeviceOverview.MapNodes(nodes, "host:h48");
        Assert.Equal(1 + DeviceOverview.MapLimit + 1, map.Count);
        Assert.Equal("this-pc", map[0].Id);
        Assert.Equal("add", map[^1].Id);
        Assert.Equal(["host:h27", "host:h29", "host:h39", "host:h44", "host:h48"], map.Skip(28).Take(5).Select(n => n.Id));
        var more = map[^2];
        Assert.Equal("more:computers", more.Id);
        Assert.Equal("19 more computers", more.Title);
        Assert.Equal(NodeHealth.Ready, more.Health);
        Assert.Equal(MapSlot.More, DeviceOverview.Slot(more));
        Assert.Equal(MapSlot.Cloud, DeviceOverview.Slot(map[1]));
        Assert.Equal(MapSlot.Add, DeviceOverview.Slot(map[^1]));
        Assert.Equal("52 devices, 2 need attention; select one to see details.", DeviceOverview.Summary(nodes));
    }

    [Fact]
    public void A_more_card_says_how_many_hidden_devices_need_attention()
    {
        var hidden = new[] { Node("a", NodeKind.Host, "a", NodeHealth.Attention), Node("b", NodeKind.Host, "b") };
        var more = DeviceOverview.More(hidden, "computers", "computer", NodeKind.Computer);
        Assert.Equal(NodeHealth.Attention, more.Health);
        Assert.Equal("1 needs attention", more.HealthText);
        Assert.True(DeviceOverview.IsMore(more.Id));
    }

    [Fact]
    public void The_list_orders_filters_and_searches_every_device()
    {
        var nodes = Network(hosts: 50, clouds: 2);
        nodes[20] = Node(nodes[20].Id, NodeKind.Host, nodes[20].Title, NodeHealth.Attention);
        var all = DeviceOverview.ListNodes(nodes, "all", "");
        Assert.Equal(nodes.Count, all.Count);
        Assert.Equal(nodes[20].Id, all[0].Id);
        Assert.Equal("this-pc", all[1].Id);
        Assert.Equal("add", all[^1].Id);
        Assert.Equal("Showing all 53 devices.", DeviceOverview.ListStatus(nodes, all, ""));

        Assert.Equal([nodes[20].Id], DeviceOverview.ListNodes(nodes, "attention", "").Select(n => n.Id));
        Assert.Equal(2, DeviceOverview.ListNodes(nodes, "cloud", "").Count);
        var found = DeviceOverview.ListNodes(nodes, "all", " H1");
        Assert.Equal(10, found.Count);
        Assert.DoesNotContain(found, n => n.Kind == NodeKind.Add);
        Assert.Equal("Showing 10 of 53 devices.", DeviceOverview.ListStatus(nodes, found, " H1"));
        Assert.Equal("No device matches \"zzz\".", DeviceOverview.ListStatus(nodes, DeviceOverview.ListNodes(nodes, "all", "zzz"), "zzz"));
    }

    [Fact]
    public void The_list_groups_devices_under_headings_with_their_counts()
    {
        var nodes = Network(hosts: 120, clouds: 3);
        nodes[7] = Node(nodes[7].Id, NodeKind.Host, nodes[7].Title, NodeHealth.Attention);
        var groups = DeviceOverview.ListGroups(nodes, "all", "");
        Assert.Equal(["attention", "computers", "cloud", "add"], groups.Select(g => g.Key));
        Assert.Equal([1, 120, 3, 1], groups.Select(g => g.Nodes.Count));
        Assert.Equal("this-pc", groups[1].Nodes[0].Id);
        Assert.Equal(["h01", "h02", "h03"], groups[1].Nodes.Skip(1).Take(3).Select(n => n.Title));
        Assert.Equal("Your computers: 120 devices", DeviceOverview.GroupName(groups[1]));
        Assert.Equal("Needs attention: 1 device", DeviceOverview.GroupName(groups[0]));
        Assert.Equal("", groups[3].Label);
        // A filter or a search leaves out Add a computer and the empty groups.
        Assert.Equal(["computers"], DeviceOverview.ListGroups(nodes, "all", "h11").Select(g => g.Key));
    }

    [Fact]
    public void A_row_shows_up_to_three_jobs_and_reads_as_its_card()
    {
        var roles = new[] { "Listens", "Thinks", "Speaks", "Lip-sync", "Sings" }.Select(c => new HostedRole(c, c + " job", "")).ToList();
        var node = Node("pc:a", NodeKind.Computer, "IMOUTO") with { Subtitle = "desktop-imouto · imouto-host", Roles = roles };
        Assert.Equal(["Listens", "Thinks", "Speaks", "+2"], DeviceOverview.Chips(node));
        Assert.Equal("IMOUTO, desktop-imouto · imouto-host. Connected. Runs: Listens job, Thinks job, Speaks job, Lip-sync job, Sings job",
            DeviceOverview.Describe(node));
    }
}
