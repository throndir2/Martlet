namespace Martlet.Desktop;

/// <summary>Keeps the Devices page readable with any number of devices. The map shows at most <see cref="MapSideLimit"/>
/// devices on each side of This PC and folds the rest into a "N more" card; the list shows every device, filtered and
/// searched. Reads nothing itself.</summary>
internal static class DeviceOverview
{
    internal const int MapSideLimit = 6;
    internal const string MorePrefix = "more:";
    internal const string MoreGlyph = "\uE712";

    internal static readonly IReadOnlyList<(string Key, string Label)> Filters =
        [("all", "All"), ("attention", "Needs attention"), ("hosts", "Hosts"), ("computers", "Computers"), ("cloud", "Cloud")];

    internal static bool IsMore(string id) => id.StartsWith(MorePrefix, StringComparison.Ordinal);

    /// <summary>Cloud services (and a job nobody does yet) sit left of This PC on the map.</summary>
    private static bool OnLeft(NetworkNode node) => node.Kind is NodeKind.Cloud or NodeKind.Missing;

    private static bool OnRight(NetworkNode node) => node.Kind is NodeKind.Host or NodeKind.Computer;

    /// <summary>The computers and services shown (This PC included; not Add a computer or a job nobody does).</summary>
    internal static int Devices(IEnumerable<NetworkNode> nodes) => nodes.Count(n => n.Kind is not (NodeKind.Add or NodeKind.Missing));

    /// <summary>Whether every device fits on the map without folding any into a "more" card.</summary>
    internal static bool FitsMap(IReadOnlyList<NetworkNode> nodes) =>
        nodes.Count(OnLeft) <= MapSideLimit && nodes.Count(OnRight) <= MapSideLimit;

    /// <summary>What the map draws: This PC; up to <see cref="MapSideLimit"/> devices a side (the selected one, those needing
    /// attention and those doing this PC's jobs first, in their usual order), the rest folded into a "more" card; and Add a
    /// computer while a side has room (or it is selected).</summary>
    internal static IReadOnlyList<NetworkNode> MapNodes(IReadOnlyList<NetworkNode> nodes, string? selected)
    {
        var left = Fold([.. nodes.Where(OnLeft)], selected, "services", "service", NodeKind.Cloud);
        var right = Fold([.. nodes.Where(OnRight)], selected, "computers", "computer", NodeKind.Computer);
        var shown = nodes.Where(n => n.Kind == NodeKind.ThisPc).Concat(left).Concat(right).ToList();
        if (Math.Min(left.Count, right.Count) < MapSideLimit || selected == "add")
            shown.AddRange(nodes.Where(n => n.Kind == NodeKind.Add));
        return shown;
    }

    private static List<NetworkNode> Fold(List<NetworkNode> side, string? selected, string plural, string singular, NodeKind kind)
    {
        if (side.Count <= MapSideLimit) return side;
        var keep = side.Select((node, index) => (node, index))
            .OrderBy(x => x.node.Id == selected ? 0 : x.node.Health == NodeHealth.Attention ? 1
                : x.node.Roles.Any(r => DeviceComponent.JobRole(r.Component) is not null) ? 2 : 3)
            .ThenBy(x => x.index).Take(MapSideLimit - 1).Select(x => x.node).ToHashSet();
        return [.. side.Where(keep.Contains), More([.. side.Where(n => !keep.Contains(n))], plural, singular, kind)];
    }

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

    /// <summary>The list: This PC, then devices needing attention, computers, then cloud services, each by name; Add a
    /// computer last when nothing narrows the list.</summary>
    internal static IReadOnlyList<NetworkNode> ListNodes(IReadOnlyList<NetworkNode> nodes, string filter, string search)
    {
        search = search.Trim();
        return [.. nodes
            .Where(n => n.Kind == NodeKind.Add ? filter == "all" && search.Length == 0 : Matches(n, filter) && Found(n, search))
            .OrderBy(n => n.Kind switch
            {
                NodeKind.ThisPc => 0,
                NodeKind.Add => 9,
                _ when n.Health == NodeHealth.Attention => 1,
                NodeKind.Host or NodeKind.Computer => 2,
                _ => 3
            })
            .ThenBy(n => n.Title, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>"53 devices, 2 need attention. Select one to see details."</summary>
    internal static string Summary(IReadOnlyList<NetworkNode> nodes)
    {
        var devices = Devices(nodes);
        var attention = nodes.Count(n => n.Kind is not (NodeKind.Add or NodeKind.Missing) && n.Health == NodeHealth.Attention);
        return $"{devices} device{(devices == 1 ? "" : "s")}" +
               (attention > 0 ? $", {attention} need{(attention == 1 ? "s" : "")} attention" : "") + ". Select one to see details.";
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
