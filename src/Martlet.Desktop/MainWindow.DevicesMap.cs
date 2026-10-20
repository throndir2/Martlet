using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Martlet.HostClient;
using Martlet.Avatar.Hosting;
using Martlet.Core.Cluster;
using Martlet.Core.Contracts;
using Martlet.Core.Installation;
using Martlet.Core.Platforms;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

// The app shell's devices map (see MainWindow.Shell.cs for the shell's shared state).
public partial class MainWindow
{
    private HostHardwareStore? HardwareStore => store is null ? null : new(store.DataDirectory);

    private NetworkInputs Inputs() => new(machine, Role, homeSettings, homeAvatar, avatar.IsShowing, hostChecks,
        HardwareStore?.Load() ?? [], homeHosts, hostUpdates.Notes, HostUsers(), clusterEnabled ? clusterPlan : null,
        SimulatedDevices.With(OtherComputers()),
        DeepThinkingHosts(), HostOutsideFacts(), OwnHostTrouble(), ThinkingPoolLeft(), PresenceAway(), ConfiguringMachines(), sharedHosts,
        NetworkHostStates());

    /// <summary>The paired computers the owner keeps out of the Thinking pool (unticked), or null.</summary>
    private IReadOnlyCollection<string>? ThinkingPoolLeft() =>
        store is not null && ThinkingPoolSettings.Load(store.DataDirectory).LeftByOwner is { Count: > 0 } left ? left : null;

    /// <summary>The paired computers whose Deep thinking role this PC thinks with, while Deep thinking is on.</summary>
    private IReadOnlyCollection<string>? DeepThinkingHosts() =>
        store is not null && ThinkLongerSettings.Of(homeSettings?.Generation).On &&
        ThinkingPoolSettings.Load(store.DataDirectory).Places.Places.Where(p => p.OnHostRole).Select(p => p.HostId!).ToArray() is { Length: > 0 } hosts ? hosts : null;

    private void RefreshDevices_Click(object sender, RoutedEventArgs e)
    {
        ReadMachineAsync().Forget();
        RefreshHomeAsync().Forget();
    }

    private void MapHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (MapHost.ActualWidth <= 18) return;
        // Narrow windows scale the whole map down, so the host then needs less height.
        if (e.WidthChanged) SizeMap();
        SizeMapCanvas();
        LayoutMap();
    }

    /// <summary>The canvas fills the map at least <see cref="MapLayout.MinWidth"/> wide; a narrow window shrinks the whole map
    /// rather than squeezing devices into each other.</summary>
    private void SizeMapCanvas()
    {
        var available = MapHost.ActualWidth - 18;
        if (available <= 0) return;
        MapCanvas.Width = Math.Max(available, MapLayout.MinWidth(MapLayout.Columns(available)));
        MapCanvas.Height = Math.Max((MapHost.ActualHeight - 18) * MapCanvas.Width / available, 300);
    }

    /// <summary>The map takes the top of the page, a little under half its height (taller when its cards need it);
    /// the selected device's details follow.</summary>
    private void DevicesPage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        SizeMap();
        SizeDeviceList();
    }

    /// <summary>As tall as the map's cards need (<see cref="MapLayout.Height"/>) at the size the map is drawn.</summary>
    private void SizeMap()
    {
        var available = MapHost.ActualWidth - 18;
        PlaceMapCells();
        var needed = MapLayout.Height(HubSize(), [.. MapCards().Select(c => (c.Cell, c.Size))]);
        var scale = available > 0 ? Math.Min(1, available / MapLayout.MinWidth(MapLayout.Columns(available))) : 1;
        MapHost.Height = Math.Max(Math.Clamp(DevicesPage.ActualHeight * 0.42, 300, 400), needed * scale + 18);
    }

    /// <summary>Where each card goes for the map's width now: four columns, or two on a narrow map (<see cref="MapLayout"/>).</summary>
    private void PlaceMapCells()
    {
        var cells = MapLayout.Assign([.. mapSlots.Select(s => s.Slot)], MapLayout.Columns(MapHost.ActualWidth - 18));
        mapCells.Clear();
        for (var i = 0; i < mapSlots.Count; i++) mapCells[mapSlots[i].Id] = cells[i];
    }

    private void RenderMap()
    {
        networkDevicesShown = NetworkDevicesSignature();
        var nodes = SimulatedDevices.WithJobs(NetworkMap.Build(Inputs()));
        RenderDeviceSettings(nodes);
        RenderNetworkCapacity(nodes);
        RenderExternalEndpoints();
        RenderShare();
        if (nodes.All(n => n.Id != selectedNode)) selectedNode = "this-pc";
        mapNodes = nodes;
        DevicesSummary.Text = DeviceOverview.Summary(nodes) + (NetworkGap() is { } gap ? " " + gap : "");
        deviceListShown = deviceListChosen ?? !DeviceOverview.FitsMap(nodes);
        settingDevicesView = true;
        DevicesViewMap.IsChecked = !deviceListShown;
        DevicesViewList.IsChecked = deviceListShown;
        settingDevicesView = false;
        MapHost.Visibility = deviceListShown ? Visibility.Collapsed : Visibility.Visible;
        DeviceListHost.Visibility = deviceListShown ? Visibility.Visible : Visibility.Collapsed;
        MapCanvas.Children.Clear();
        mapElements.Clear();
        if (deviceListShown)
        {
            RenderDeviceList();
            SelectNode(selectedNode, animate: false);
            return;
        }
        ClearDeviceList();
        nodes = DeviceOverview.MapNodes(nodes, selectedNode);
        var around = nodes.Where(n => n.Kind != NodeKind.ThisPc).ToList();
        mapSlots = [.. around.Select(n => (n.Id, DeviceOverview.Slot(n)))];
        // Many devices: compact cards (name, computer and status) keep the map short; the list and the details show the jobs.
        var compact = around.Count >= MapLayout.CompactFrom;
        mapGlow = new Ellipse { Width = 210, Height = 210, Opacity = 0.55, IsHitTestVisible = false };
        mapGlow.SetResourceReference(Shape.FillProperty, "GlowBrush");
        MapCanvas.Children.Add(mapGlow);
        // Connections share the gaps between cards: those needing attention go underneath, so a shared stretch stays calm and
        // only the last stretch into a device needing attention shows the warning colour.
        foreach (var node in around.OrderByDescending(n => n.Health == NodeHealth.Attention))
        {
            var track = new System.Windows.Shapes.Path { StrokeThickness = 2, Opacity = 0.35, IsHitTestVisible = false };
            track.SetResourceReference(Shape.StrokeProperty, "BorderBrush");
            var flow = new System.Windows.Shapes.Path
            {
                StrokeThickness = 2.5, StrokeDashArray = [2, 4], StrokeDashCap = PenLineCap.Round, IsHitTestVisible = false,
                Opacity = node.Kind is NodeKind.Add or NodeKind.Missing ? 0.4 : 0.9
            };
            flow.SetResourceReference(Shape.StrokeProperty, node.Health == NodeHealth.Attention ? "WarningBrush" : "AccentBrush");
            MapCanvas.Children.Add(track);
            MapCanvas.Children.Add(flow);
            mapElements.Add(new(node, null!, track, flow, null));
        }
        var index = 0;
        foreach (var node in nodes)
        {
            var (card, ring) = NodeCard(node, compact && node.Kind != NodeKind.ThisPc);
            MapCanvas.Children.Add(card);
            var existing = mapElements.FindIndex(m => m.Node.Id == node.Id);
            if (existing >= 0) mapElements[existing] = mapElements[existing] with { Card = card, Ring = ring };
            else mapElements.Insert(0, new(node, card, null, null, ring));
            // A big map comes in at once rather than one card after another for seconds.
            Motion.Enter(card, dy: 0, milliseconds: 360, delay: Math.Min(index++, 10) * 60);
        }
        SizeMap();
        SizeMapCanvas();
        LayoutMap();
        foreach (var element in mapElements)
        {
            if (element.Flow is { } flow && element.Node.Kind is not (NodeKind.Add or NodeKind.Missing)) Motion.Flow(flow);
            if (element.Ring is { } ring && element.Node.Health == NodeHealth.Ready) Motion.PulseRing(ring, delay: index++ * 0.3 % 2);
        }
        Motion.Breathe(mapGlow);
        SelectNode(selectedNode, animate: false);
    }

    /// <summary>The cards around This PC in the order <see cref="MapLayout.Assign"/> places them, and each one's place by device ID.</summary>
    private List<(string Id, MapSlot Slot)> mapSlots = [];
    private readonly Dictionary<string, MapCell> mapCells = new(StringComparer.Ordinal);

    /// <summary>The cards around This PC with their places and sizes as drawn.</summary>
    private List<(MapElement Element, MapCell Cell, Size Size)> MapCards() =>
        [.. mapElements.Where(m => m.Node.Kind != NodeKind.ThisPc && mapCells.ContainsKey(m.Node.Id))
            .Select(m => (m, mapCells[m.Node.Id], CardSize(m)))];

    /// <summary>A card's size as drawn (its chips can take one row or two, its subtitle one line or two).</summary>
    private static Size CardSize(MapElement element)
    {
        element.Card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return element.Card.DesiredSize;
    }

    private Size HubSize() => mapElements.FirstOrDefault(m => m.Node.Kind == NodeKind.ThisPc) is { } hub ? CardSize(hub) : default;

    private void LayoutMap()
    {
        if (mapElements.Count == 0 || mapGlow is null) return;
        PlaceMapCells();
        var hubSize = HubSize();
        var cards = MapCards();
        var sized = cards.Select(c => (c.Cell, c.Size)).ToArray();
        // Never squeeze cards into each other: a canvas smaller than they need grows, and the Viewbox shrinks the whole map.
        MapCanvas.Width = Math.Max(MapCanvas.Width, MapLayout.MinWidth(MapLayout.Columns(MapHost.ActualWidth - 18)));
        MapCanvas.Height = Math.Max(MapCanvas.Height, MapLayout.Height(hubSize, sized));
        var (hub, centers, routes) = MapLayout.Place(MapCanvas.Width, MapCanvas.Height, hubSize, sized);
        void Put(UIElement card, Point center, Size size)
        {
            Canvas.SetLeft(card, center.X - size.Width / 2);
            Canvas.SetTop(card, center.Y - size.Height / 2);
        }
        if (mapElements.FirstOrDefault(m => m.Node.Kind == NodeKind.ThisPc) is { } thisPc) Put(thisPc.Card, hub, hubSize);
        for (var i = 0; i < cards.Count; i++)
        {
            Put(cards[i].Element.Card, centers[i], cards[i].Size);
            // Edge to edge, so a connection never runs through a see-through card.
            var connector = Connector(routes[i]);
            if (cards[i].Element.Track is { } track) track.Data = connector;
            if (cards[i].Element.Flow is { } flow) flow.Data = connector;
        }
        Canvas.SetLeft(mapGlow, hub.X - mapGlow.Width / 2);
        Canvas.SetTop(mapGlow, hub.Y - mapGlow.Height / 2);
    }

    /// <summary>A connection along <paramref name="points"/> (straight stretches through the gaps between cards) with soft
    /// corners.</summary>
    private static PathGeometry Connector(IReadOnlyList<Point> points, double radius = 10)
    {
        var figure = new PathFigure { StartPoint = points[0] };
        for (var i = 1; i < points.Count - 1; i++)
        {
            var (before, corner, after) = (points[i - 1], points[i], points[i + 1]);
            var round = Math.Min(radius, Math.Min((corner - before).Length, (after - corner).Length) / 2);
            if (round < 0.5)
            {
                figure.Segments.Add(new LineSegment(corner, true));
                continue;
            }
            var into = before - corner;
            var outOf = after - corner;
            into.Normalize();
            outOf.Normalize();
            figure.Segments.Add(new LineSegment(corner + into * round, true));
            figure.Segments.Add(new QuadraticBezierSegment(corner, corner + outOf * round, true));
        }
        figure.Segments.Add(new LineSegment(points[^1], true));
        return new PathGeometry([figure]);
    }

    /// <summary>A device's card on the map. A <paramref name="compact"/> card (a map of many devices) shows its name, computer
    /// and status on three short lines instead of its jobs, so the map stays short.</summary>
    private (Button Card, Ellipse Ring) NodeCard(NetworkNode node, bool compact = false)
    {
        var header = new DockPanel();
        var status = new Grid { Width = 12, Height = 12, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8, 2, 0, 0) };
        var ring = Dot(node.Health, 12, default);
        ring.Opacity = 0;
        status.Children.Add(ring);
        status.Children.Add(Dot(node.Health, 10, default));
        // Add a computer has no status, so its name gets the room.
        if (node.Kind == NodeKind.Add) status.Visibility = Visibility.Collapsed;
        DockPanel.SetDock(status, Dock.Right);
        header.Children.Add(status);
        var bubble = new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(18), Margin = new Thickness(0, 0, 10, 0) };
        bubble.SetResourceReference(Border.BackgroundProperty, node.Kind == NodeKind.ThisPc ? "AccentBrush" : "SoftBrush");
        var glyph = Glyph(node.Glyph, 17, default);
        glyph.SetResourceReference(TextBlock.ForegroundProperty, node.Kind == NodeKind.ThisPc ? "OnAccentBrush" : "AccentBrush");
        bubble.Child = glyph;
        DockPanel.SetDock(bubble, Dock.Left);
        header.Children.Add(bubble);
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(new TextBlock { Text = node.Title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis });
        // A long subtitle ("desktop-imouto · imouto-host") takes a second line rather than being cut; the map sizes rows by height.
        var subtitle = new TextBlock
        {
            Text = node.Subtitle, FontSize = 12, TextWrapping = compact ? TextWrapping.NoWrap : TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 34
        };
        subtitle.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        names.Children.Add(subtitle);
        if (compact && node.Kind != NodeKind.Add)
        {
            var health = new TextBlock { Text = node.HealthText, FontSize = 12, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis };
            health.SetResourceReference(TextBlock.ForegroundProperty, node.Health == NodeHealth.Attention ? "WarningBrush" : "TextBrush");
            names.Children.Add(health);
        }
        header.Children.Add(names);

        var content = new StackPanel();
        content.Children.Add(header);
        var chips = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        foreach (var chip in compact ? Array.Empty<string>() : DeviceOverview.Chips(node))
        {
            var border = new Border { Child = new TextBlock { Text = chip, FontSize = 11, TextWrapping = TextWrapping.NoWrap } };
            border.SetResourceReference(StyleProperty, "Chip");
            chips.Children.Add(border);
        }
        if (chips.Children.Count > 0) content.Children.Add(chips);

        var card = new Button { Content = content, Width = node.Kind == NodeKind.ThisPc ? MapLayout.HubWidth : MapLayout.CardWidth };
        card.SetResourceReference(StyleProperty, "NodeCard");
        AutomationProperties.SetAutomationId(card, "Node-" + node.Id);
        AutomationProperties.SetName(card, DeviceOverview.Describe(node));
        var id = node.Id;
        card.Click += (_, _) =>
        {
            if (DeviceOverview.IsMore(id)) ShowDeviceList();
            else SelectNode(id, animate: true);
        };
        return (card, ring);
    }

    private void SelectNode(string id, bool animate)
    {
        // A device the map folded into "N more" (selected from Home or a job's Show) takes a place on the map.
        if (!deviceListShown && !remapping && mapNodes.Any(n => n.Id == id) && mapElements.All(m => m.Node.Id != id))
        {
            selectedNode = id;
            remapping = true;
            try { RenderMap(); }
            finally { remapping = false; }
            return;
        }
        selectedNode = id;
        MarkSelectedDevice();
        if (mapNodes.FirstOrDefault(n => n.Id == id) is not { } node) return;
        RenderDetail(node);
        if (!animate) return;
        Motion.Enter(DetailContent, dx: 0, dy: 12, milliseconds: 240);
        // When little of the details shows (a short window), scroll so their top part is in view.
        var top = DetailCard.TranslatePoint(new Point(0, 0), DevicesPage).Y;
        if (top > DevicesPage.ViewportHeight - 160)
            DevicesPage.ScrollToVerticalOffset(DevicesPage.VerticalOffset + top - Math.Max(0, DevicesPage.ViewportHeight - 280));
    }
}
