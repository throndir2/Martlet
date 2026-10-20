namespace Martlet.Desktop;

/// <summary>A heading of the Devices list and the devices under it. <paramref name="Label"/> is empty for Add a computer,
/// which shows last without a heading.</summary>
internal sealed record DeviceGroup(string Key, string Label, IReadOnlyList<NetworkNode> Nodes);

/// <summary>Keeps the Devices page readable with any number of devices. The map (<see cref="MapLayout"/>) shows up to
/// <see cref="MapLimit"/> devices around This PC and folds the rest into a "N more" card; the list shows every device as one
/// compact row, grouped, filtered and searched. Reads nothing itself.</summary>
internal static class DeviceOverview
{
    /// <summary>The most devices the map draws (nine rows of cards); the list shows by itself beyond.</summary>
    internal const int MapLimit = 33;
    internal const string MorePrefix = "more:";
    internal const string MoreGlyph = "\uE712";

    internal static readonly IReadOnlyList<(string Key, string Label)> Filters =
        [("all", "All"), ("attention", "Needs attention"), ("hosts", "Hosts"), ("computers", "Computers"), ("cloud", "Cloud")];

    internal static bool IsMore(string id) => id.StartsWith(MorePrefix, StringComparison.Ordinal);

    /// <summary>Cloud services (and a job nobody does yet) sit left of This PC on the map.</summary>
    private static bool OnLeft(NetworkNode node) => node.Kind is NodeKind.Cloud or NodeKind.Missing;

    /// <summary>A device the map places around This PC (not This PC itself or Add a computer).</summary>
    private static bool AroundThisPc(NetworkNode node) => node.Kind is not (NodeKind.ThisPc or NodeKind.Add);

    /// <summary>The computers and services shown (This PC included; not Add a computer or a job nobody does).</summary>
    internal static int Devices(IEnumerable<NetworkNode> nodes) => nodes.Count(n => n.Kind is not (NodeKind.Add or NodeKind.Missing));

    /// <summary>Whether every device fits on the map without folding any into a "more" card.</summary>
    internal static bool FitsMap(IReadOnlyList<NetworkNode> nodes) => nodes.Count(AroundThisPc) <= MapLimit;

    /// <summary>What the map draws: This PC; up to <see cref="MapLimit"/> devices (beyond that the selected one, those needing
    /// attention and those doing this PC's jobs first, in their usual order, and a "more" card for the rest); and Add a
    /// computer.</summary>
    internal static IReadOnlyList<NetworkNode> MapNodes(IReadOnlyList<NetworkNode> nodes, string? selected)
    {
        var devices = nodes.Where(AroundThisPc).ToList();
        if (devices.Count > MapLimit)
        {
            var keep = devices.Select((node, index) => (node, index))
                .OrderBy(x => x.node.Id == selected ? 0 : x.node.Health == NodeHealth.Attention ? 1
                    : x.node.Roles.Any(r => DeviceComponent.JobRole(r.Component) is not null) ? 2 : 3)
                .ThenBy(x => x.index).Take(MapLimit - 1).Select(x => x.node).ToHashSet();
            var hidden = devices.Where(n => !keep.Contains(n)).ToList();
            devices = [.. devices.Where(keep.Contains), hidden.All(OnLeft) ? More(hidden, "services", "service", NodeKind.Cloud)
                : hidden.Any(OnLeft) ? More(hidden, "devices", "device", NodeKind.Computer)
                : More(hidden, "computers", "computer", NodeKind.Computer)];
        }
        return [.. nodes.Where(n => n.Kind == NodeKind.ThisPc), .. devices, .. nodes.Where(n => n.Kind == NodeKind.Add)];
    }

    /// <summary>Where a card goes on the map (<see cref="MapLayout.Assign"/>).</summary>
    internal static MapSlot Slot(NetworkNode node) =>
        IsMore(node.Id) ? MapSlot.More : node.Kind == NodeKind.Add ? MapSlot.Add : OnLeft(node) ? MapSlot.Cloud : MapSlot.Device;

    /// <summary>The card standing for devices the map has no room for; selecting it opens the list.</summary>
    internal static NetworkNode More(IReadOnlyList<NetworkNode> hidden, string plural, string singular, NodeKind kind)
    {
        var attention = hidden.Count(n => n.Health == NodeHealth.Attention);
        var health = attention > 0 ? NodeHealth.Attention : hidden.Any(n => n.Health == NodeHealth.Ready) ? NodeHealth.Ready : NodeHealth.Unknown;
        var text = attention > 0 ? $"{attention} need{(attention == 1 ? "s" : "")} attention" : "Show all in the list";
        return new(MorePrefix + plural, kind, $"{hidden.Count} more {(hidden.Count == 1 ? singular : plural)}", text, MoreGlyph, health,
            text, [], [], [], []);
    }

    internal static bool Matches(NetworkNode node, string filter) => filter switch
    {
        "attention" => node.Health == NodeHealth.Attention,
        "hosts" => node.Kind == NodeKind.Host || node.PairedHostId is not null,
        "computers" => node.Kind is NodeKind.ThisPc or NodeKind.Host or NodeKind.Computer,
        "cloud" => OnLeft(node),
        _ => true
    };

    /// <summary>Whether a device's name, subtitle, status or what it runs contains <paramref name="search"/>.</summary>
    internal static bool Found(NetworkNode node, string search) => search.Length == 0 ||
        new[] { node.Title, node.Subtitle, node.HealthText, node.PairedHostId ?? "" }
            .Concat(node.Roles.SelectMany(r => new[] { r.Name, r.Chip }))
            .Any(text => text.Contains(search, StringComparison.CurrentCultureIgnoreCase));

    /// <summary>The list in the order it shows: devices needing attention, your computers, then cloud services (This PC first
    /// in its group, the others by name); Add a computer last when nothing narrows the list.</summary>
    internal static IReadOnlyList<NetworkNode> ListNodes(IReadOnlyList<NetworkNode> nodes, string filter, string search) =>
        [.. ListGroups(nodes, filter, search).SelectMany(g => g.Nodes)];

    /// <summary>The list's groups, each only when it holds a device: "Needs attention", "Your computers" and "Cloud services",
    /// then Add a computer without a heading.</summary>
    internal static IReadOnlyList<DeviceGroup> ListGroups(IReadOnlyList<NetworkNode> nodes, string filter, string search)
    {
        search = search.Trim();
        var shown = nodes
            .Where(n => n.Kind == NodeKind.Add ? filter == "all" && search.Length == 0 : Matches(n, filter) && Found(n, search))
            .OrderBy(n => n.Kind != NodeKind.ThisPc)
            .ThenBy(n => n.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        string Group(NetworkNode node) => node.Kind == NodeKind.Add ? "add"
            : node.Health == NodeHealth.Attention ? "attention"
            : OnLeft(node) ? "cloud" : "computers";
        return [.. new[] { ("attention", "Needs attention"), ("computers", "Your computers"), ("cloud", "Cloud services"), ("add", "") }
            .Select(g => new DeviceGroup(g.Item1, g.Item2, [.. shown.Where(n => Group(n) == g.Item1)]))
            .Where(g => g.Nodes.Count > 0)];
    }

    /// <summary>"Needs attention: 2 devices", what a list heading says to a screen reader. Like the filters and the summary it
    /// counts devices, not a job nobody does yet.</summary>
    internal static string GroupName(DeviceGroup group)
    {
        var count = Devices(group.Nodes);
        return $"{group.Label}: {count} device{(count == 1 ? "" : "s")}";
    }

    /// <summary>The jobs a device's row shows: up to <paramref name="most"/> chips, then "+N" for the rest.</summary>
    internal static IReadOnlyList<string> Chips(NetworkNode node, int most = 3)
    {
        var chips = node.Roles.Where(r => r.Component != DeviceComponent.App).Select(r => r.Chip).Distinct().ToList();
        return chips.Count <= most ? chips : [.. chips.Take(most), $"+{chips.Count - most}"];
    }

    /// <summary>A device's card or row as text, its accessible name and what MCP's Node-&lt;id&gt; returns: "IMOUTO,
    /// desktop-imouto · imouto-host. Connected. Runs: Martlet companion, Listening".</summary>
    internal static string Describe(NetworkNode node) =>
        node.Kind == NodeKind.Add ? $"{node.Title}: {node.Subtitle}"
        : IsMore(node.Id) ? $"{node.Title}: {node.HealthText}. Opens the list of every device."
        : $"{node.Title}, {node.Subtitle}. {node.HealthText}. Runs: {string.Join(", ", node.Roles.Select(r => r.Name))}";

    /// <summary>"53 devices, 2 need attention; select one to see details."</summary>
    internal static string Summary(IReadOnlyList<NetworkNode> nodes)
    {
        var devices = Devices(nodes);
        var attention = nodes.Count(n => n.Kind is not (NodeKind.Add or NodeKind.Missing) && n.Health == NodeHealth.Attention);
        return $"{devices} device{(devices == 1 ? "" : "s")}" +
               (attention > 0 ? $", {attention} need{(attention == 1 ? "s" : "")} attention" : "") + "; select one to see details.";
    }

    /// <summary>"Showing 12 of 53 devices." or why nothing shows.</summary>
    internal static string ListStatus(IReadOnlyList<NetworkNode> nodes, IReadOnlyList<NetworkNode> shown, string search)
    {
        var total = Devices(nodes);
        var count = Devices(shown);
        if (count == 0) return search.Trim().Length > 0 ? $"No device matches \"{search.Trim()}\"." : "No device here.";
        return count == total ? $"Showing all {total} device{(total == 1 ? "" : "s")}." : $"Showing {count} of {total} devices.";
    }
}
