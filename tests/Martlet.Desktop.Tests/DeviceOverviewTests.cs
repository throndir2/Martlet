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
        var right = map.Where(n => n.Kind is NodeKind.Host or NodeKind.Computer).ToList();
        Assert.Equal(DeviceOverview.MapSideLimit, right.Count);
        Assert.Equal(["host:h01", "host:h29", "host:h39", "host:h44", "host:h48"], right.Take(5).Select(n => n.Id));
        var more = right[^1];
        Assert.Equal("more:computers", more.Id);
        Assert.Equal("45 more computers", more.Title);
        Assert.Equal(NodeHealth.Ready, more.Health);
        // A side with room keeps Add a computer.
        Assert.Contains(map, n => n.Kind == NodeKind.Add);
        Assert.Equal("52 devices, 2 need attention. Select one to see details.", DeviceOverview.Summary(nodes));
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
        Assert.Equal("this-pc", all[0].Id);
        Assert.Equal(nodes[20].Id, all[1].Id);
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
}
