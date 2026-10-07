using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Shapes;
using Martlet.Core.Planning;

namespace Martlet.Desktop;

/// <summary>The Devices page's resource view: in the selected device's details, how much of its graphics memory, memory,
/// processor and disk each job takes (planned, from Martlet's footprint estimates, and in use now where the device reports
/// it), what is left and what else fits there; and, for the whole network, what your computers cover and could still run.
/// The numbers come from <see cref="DeviceCapacity"/>; this only draws them.</summary>
public partial class MainWindow
{
    private void AddResourcesSection(NetworkNode node)
    {
        if (node.Kind is not (NodeKind.ThisPc or NodeKind.Host) || CapacityFor(node) is not { } view) return;
        DetailContent.Children.Add(DetailSection("Resources", "Planned from Martlet's estimates. A range is what the jobs usually hold, " +
            "then the most they take while they work hardest. \"In use now\" is what the device reports."));
        var panel = new StackPanel();
        AutomationProperties.SetName(panel, "Resources");
        var specs = new TextBlock { Text = view.Specs, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
        AutomationProperties.SetAutomationId(specs, "DeviceSpecs");
        panel.Children.Add(specs);

        foreach (var bar in view.Bars.Where(b => b.Total is > 0 || b.Planned > 0))
        {
            var text = new TextBlock { Text = bar.Text, TextWrapping = TextWrapping.Wrap };
            AutomationProperties.SetAutomationId(text, "DeviceResource-" + DeviceCapacity.Key(bar.Resource));
            panel.Children.Add(text);
            panel.Children.Add(BarVisual(bar));
        }

        if (view.Components.Count > 0)
        {
            var shares = new TextBlock { Text = "What each job takes", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 4) };
            panel.Children.Add(shares);
            foreach (var component in view.Components)
            {
                var line = new TextBlock { Text = DeviceCapacity.ShareText(component, view.Bars), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 2) };
                line.SetResourceReference(StyleProperty, "Muted");
                AutomationProperties.SetAutomationId(line, "DeviceShare-" + component.Key.Replace(':', '-'));
                panel.Children.Add(line);
            }
        }

        var headroom = new TextBlock { Text = view.Headroom, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), FontWeight = FontWeights.SemiBold };
        AutomationProperties.SetAutomationId(headroom, "DeviceHeadroom");
        panel.Children.Add(headroom);
        for (var i = 0; i < view.AlsoFits.Count; i++)
        {
            var fit = new TextBlock { Text = "\u2022 " + view.AlsoFits[i], TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
            AutomationProperties.SetAutomationId(fit, $"DeviceAlsoFits-{i}");
            AutomationProperties.SetName(fit, view.AlsoFits[i]);
            panel.Children.Add(fit);
        }

        var card = new Border { Child = panel, CornerRadius = new CornerRadius(16), Padding = new Thickness(16, 14, 16, 14) };
        card.SetResourceReference(Border.BackgroundProperty, "CanvasBrush");
        AutomationProperties.SetAutomationId(card, "DeviceResources");
        DetailContent.Children.Add(card);
    }

    /// <summary>A bar split into one segment per job (its share of the total): solid for what the job usually holds, lighter
    /// for what it grows by while it works hardest. The rest is free, and a thin mark shows what the device reports in use now.
    /// A bar the jobs overfill turns the warning color; on a tight bar only the growth does.</summary>
    private static FrameworkElement BarVisual(CapacityBar bar)
    {
        var track = new Grid { Height = 10, Margin = new Thickness(0, 4, 0, 10), ToolTip = bar.Text };
        AutomationProperties.SetName(track, bar.Text);
        var background = new Border { CornerRadius = new CornerRadius(5) };
        background.SetResourceReference(Border.BackgroundProperty, "SoftBrush");
        track.Children.Add(background);

        var segments = new Grid();
        var shares = bar.Shares.Select(s => Math.Max(0, s.Percent)).ToList();
        var used = shares.Sum();
        var scale = used > 100 ? 100 / used : 1;
        double[] opacities = [1, 0.7, 0.5, 0.85, 0.6, 0.4];
        for (var i = 0; i < shares.Count; i++)
        {
            segments.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(shares[i] * scale, 0.5), GridUnitType.Star) });
            var usual = Math.Clamp(bar.Shares[i].UsualPercent, 0, shares[i]);
            var grows = shares[i] > usual;
            var segment = new Grid
            {
                Opacity = opacities[i % opacities.Length], Margin = new Thickness(0, 0, 1, 0),
                ToolTip = $"{bar.Shares[i].Component.Name}: {(grows ? $"{usual:0}-" : "")}{shares[i]:0}%"
            };
            segment.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(grows ? usual : 1, GridUnitType.Star) });
            var held = new Border { CornerRadius = new CornerRadius(i == 0 ? 5 : 0, 0, 0, i == 0 ? 5 : 0) };
            held.SetResourceReference(Border.BackgroundProperty, bar.Over ? "WarningBrush" : "AccentBrush");
            segment.Children.Add(held);
            if (grows)
            {
                segment.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(shares[i] - usual, GridUnitType.Star) });
                var busy = new Border { Opacity = 0.45 };
                busy.SetResourceReference(Border.BackgroundProperty, bar.Over || bar.Tight ? "WarningBrush" : "AccentBrush");
                Grid.SetColumn(busy, 1);
                segment.Children.Add(busy);
            }
            Grid.SetColumn(segment, i);
            segments.Children.Add(segment);
        }
        segments.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0, 100 - used * scale), GridUnitType.Star) });
        track.Children.Add(segments);

        if (bar.LivePercent is { } live)
        {
            var marker = new Grid();
            marker.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Clamp(live, 0, 100), GridUnitType.Star) });
            marker.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0, 100 - live), GridUnitType.Star) });
            var line = new Rectangle { Width = 3, Margin = new Thickness(-1.5, -3, 0, -3), HorizontalAlignment = HorizontalAlignment.Left };
            line.SetResourceReference(Shape.FillProperty, "TextBrush");
            Grid.SetColumn(line, 1);
            marker.Children.Add(line);
            track.Children.Add(marker);
        }
        return track;
    }

    /// <summary>Fills the network-wide capacity card under the selected device.</summary>
    private void RenderNetworkCapacity(IReadOnlyList<NetworkNode> nodes)
    {
        capacity = MeasureCapacity(nodes);
        var view = DeviceCapacityInputs.Network(nodes, capacity.Machines, capacity.Plan, FootprintCatalog.Default);
        CapacityCard.Visibility = Visibility.Visible;
        CapacityCoverageText.Text = view.Coverage;
        CapacityTotalsText.Text = view.Totals;
        CapacityFitsText.Text = view.Fits;
    }

    private (IReadOnlyList<NetworkNode> Nodes, IReadOnlyList<MachineSpecs> Machines, IReadOnlyList<CurrentAssignment> Current, PlacementPlan Plan)? capacityCache;

    private (IReadOnlyList<NetworkNode> Nodes, IReadOnlyList<MachineSpecs> Machines, IReadOnlyList<CurrentAssignment> Current, PlacementPlan Plan) capacity
    {
        get => capacityCache ?? MeasureCapacity(mapNodes);
        set => capacityCache = value;
    }

    /// <summary>The engine's measure of today's setup on this PC and the hosts that reported their hardware. Pure and quick:
    /// it contacts nothing.</summary>
    private (IReadOnlyList<NetworkNode>, IReadOnlyList<MachineSpecs>, IReadOnlyList<CurrentAssignment>, PlacementPlan) MeasureCapacity(
        IReadOnlyList<NetworkNode> nodes)
    {
        var inputs = Inputs();
        var machines = DeviceCapacityInputs.Machines(inputs, nodes, ThisPcDiskFreeGb());
        var current = DeviceCapacityInputs.Current(inputs, nodes, FootprintCatalog.Default);
        return (nodes, machines, current, PlacementEngine.Measure(new PlanRequest(machines) { Current = current }));
    }

    private double? ThisPcDiskFreeGb()
    {
        try
        {
            var root = System.IO.Path.GetPathRoot(store?.DataDirectory ?? AppContext.BaseDirectory);
            return root is null ? null : Math.Round(new System.IO.DriveInfo(root).AvailableFreeSpace / (1024d * 1024 * 1024));
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    /// <summary>The selected device's resource view; this PC's memory in use is read live (one system call).</summary>
    private DeviceCapacityView? CapacityFor(NetworkNode node)
    {
        var (_, machines, current, plan) = capacity;
        var live = node.Kind == NodeKind.ThisPc ? new CapacityLive(null, MachineInfo.MemoryInUseGb()) : null;
        return DeviceCapacityInputs.Device(node, machines, plan, current, FootprintCatalog.Default, live);
    }
}
