using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Martlet.Core.Platforms;

namespace Martlet.Desktop;

/// <summary>One device on the devices map: its card, the track and the moving dots of its connection to This PC, and its ring.</summary>
internal sealed record MapElement(NetworkNode Node, Button Card, System.Windows.Shapes.Path? Track, System.Windows.Shapes.Path? Flow, Ellipse? Ring);

/// <summary>Devices › the map, the UI only: This PC in the middle and a card for each other device around it, joined by
/// connections through the gaps between the cards (<see cref="MapLayout"/> places them). Which devices there are, and what each
/// card says, is <see cref="NetworkMap"/>'s and <see cref="DeviceOverview"/>'s decision; MainWindow gives the view the nodes to
/// draw (MainWindow.DevicesMap.cs). <paramref name="canvas"/> is the map's canvas (MapCanvas) in <paramref name="host"/> (MapHost);
/// a card's click calls <paramref name="select"/> with its device ID, or <paramref name="showList"/> for "N more".</summary>
internal sealed class DevicesMapView(Canvas canvas, FrameworkElement host, Action<string> select, Action showList)
{
    private readonly List<MapElement> elements = [];
    private Ellipse? glow;
    /// <summary>The cards around This PC in the order <see cref="MapLayout.Assign"/> places them, and each one's place by device ID.</summary>
    private List<(string Id, MapSlot Slot)> slots = [];
    private readonly Dictionary<string, MapCell> cells = new(StringComparer.Ordinal);

    /// <summary>The devices drawn on the map now.</summary>
    internal IReadOnlyList<MapElement> Elements => elements;

    /// <summary>Whether the device with <paramref name="id"/> has a card on the map.</summary>
    internal bool Shows(string id) => elements.Any(m => m.Node.Id == id);

    private double Available => host.ActualWidth - 18;

    /// <summary>Takes every card and connection off the map.</summary>
    internal void Clear()
    {
        canvas.Children.Clear();
        elements.Clear();
    }

    /// <summary>Draws <paramref name="nodes"/> (This PC and the devices the map shows, <see cref="DeviceOverview.MapNodes"/>), sizes
    /// the map for a page <paramref name="pageHeight"/> tall and starts the cards' motion.</summary>
    internal void Draw(IReadOnlyList<NetworkNode> nodes, double pageHeight)
    {
        var around = nodes.Where(n => n.Kind != NodeKind.ThisPc).ToList();
        slots = [.. around.Select(n => (n.Id, DeviceOverview.Slot(n)))];
        // Many devices: compact cards (name, computer and status) keep the map short; the list and the details show the jobs.
        var compact = around.Count >= MapLayout.CompactFrom;
        glow = new Ellipse { Width = 210, Height = 210, Opacity = 0.55, IsHitTestVisible = false };
        glow.SetResourceReference(Shape.FillProperty, "GlowBrush");
        canvas.Children.Add(glow);
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
            canvas.Children.Add(track);
            canvas.Children.Add(flow);
            elements.Add(new(node, null!, track, flow, null));
        }
        var index = 0;
        foreach (var node in nodes)
        {
            var (card, ring) = NodeCard(node, compact && node.Kind != NodeKind.ThisPc);
            canvas.Children.Add(card);
            var existing = elements.FindIndex(m => m.Node.Id == node.Id);
            if (existing >= 0) elements[existing] = elements[existing] with { Card = card, Ring = ring };
            else elements.Insert(0, new(node, card, null, null, ring));
            // A big map comes in at once rather than one card after another for seconds.
            Motion.Enter(card, dy: 0, milliseconds: 360, delay: Math.Min(index++, 10) * 60);
        }
        Size(pageHeight);
        SizeCanvas();
        Layout();
        foreach (var element in elements)
        {
            if (element.Flow is { } flow && element.Node.Kind is not (NodeKind.Add or NodeKind.Missing)) Motion.Flow(flow);
            if (element.Ring is { } ring && element.Node.Health == NodeHealth.Ready) Motion.PulseRing(ring, delay: index++ * 0.3 % 2);
        }
        Motion.Breathe(glow);
    }

    /// <summary>The canvas fills the map at least <see cref="MapLayout.MinWidth"/> wide; a narrow window shrinks the whole map
    /// rather than squeezing devices into each other.</summary>
    internal void SizeCanvas()
    {
        var available = Available;
        if (available <= 0) return;
        canvas.Width = Math.Max(available, MapLayout.MinWidth(MapLayout.Columns(available)));
        canvas.Height = Math.Max((host.ActualHeight - 18) * canvas.Width / available, 300);
    }

    /// <summary>The map takes the top of a page <paramref name="pageHeight"/> tall, a little under half its height, or as tall as
    /// its cards need (<see cref="MapLayout.Height"/>) at the size the map is drawn.</summary>
    internal void Size(double pageHeight)
    {
        var available = Available;
        PlaceCells();
        var needed = MapLayout.Height(HubSize(), [.. Cards().Select(c => (c.Cell, c.Size))]);
        var scale = available > 0 ? Math.Min(1, available / MapLayout.MinWidth(MapLayout.Columns(available))) : 1;
        host.Height = Math.Max(Math.Clamp(pageHeight * 0.42, 300, 400), needed * scale + 18);
    }

    /// <summary>Where each card goes for the map's width now: four columns, or two on a narrow map (<see cref="MapLayout"/>).</summary>
    private void PlaceCells()
    {
        var assigned = MapLayout.Assign([.. slots.Select(s => s.Slot)], MapLayout.Columns(Available));
        cells.Clear();
        for (var i = 0; i < slots.Count; i++) cells[slots[i].Id] = assigned[i];
    }

    /// <summary>The cards around This PC with their places and sizes as drawn.</summary>
    private List<(MapElement Element, MapCell Cell, Size Size)> Cards() =>
        [.. elements.Where(m => m.Node.Kind != NodeKind.ThisPc && cells.ContainsKey(m.Node.Id))
            .Select(m => (m, cells[m.Node.Id], CardSize(m)))];

    /// <summary>A card's size as drawn (its chips can take one row or two, its subtitle one line or two).</summary>
    private static Size CardSize(MapElement element)
    {
        element.Card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return element.Card.DesiredSize;
    }

    private Size HubSize() => elements.FirstOrDefault(m => m.Node.Kind == NodeKind.ThisPc) is { } hub ? CardSize(hub) : default;

    /// <summary>Places This PC, the cards around it and their connections for the map's size now.</summary>
    internal void Layout()
    {
        if (elements.Count == 0 || glow is null) return;
        PlaceCells();
        var hubSize = HubSize();
        var cards = Cards();
        var sized = cards.Select(c => (c.Cell, c.Size)).ToArray();
        // Never squeeze cards into each other: a canvas smaller than they need grows, and the Viewbox shrinks the whole map.
        canvas.Width = Math.Max(canvas.Width, MapLayout.MinWidth(MapLayout.Columns(Available)));
        canvas.Height = Math.Max(canvas.Height, MapLayout.Height(hubSize, sized));
        var (hub, centers, routes) = MapLayout.Place(canvas.Width, canvas.Height, hubSize, sized);
        void Put(UIElement card, Point center, Size size)
        {
            Canvas.SetLeft(card, center.X - size.Width / 2);
            Canvas.SetTop(card, center.Y - size.Height / 2);
        }
        if (elements.FirstOrDefault(m => m.Node.Kind == NodeKind.ThisPc) is { } thisPc) Put(thisPc.Card, hub, hubSize);
        for (var i = 0; i < cards.Count; i++)
        {
            Put(cards[i].Element.Card, centers[i], cards[i].Size);
            // Edge to edge, so a connection never runs through a see-through card.
            var connector = Connector(routes[i]);
            if (cards[i].Element.Track is { } track) track.Data = connector;
            if (cards[i].Element.Flow is { } flow) flow.Data = connector;
        }
        Canvas.SetLeft(glow, hub.X - glow.Width / 2);
        Canvas.SetTop(glow, hub.Y - glow.Height / 2);
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
        var ring = MainWindow.Dot(node.Health, 12, default);
        ring.Opacity = 0;
        status.Children.Add(ring);
        status.Children.Add(MainWindow.Dot(node.Health, 10, default));
        // Add a computer has no status, so its name gets the room.
        if (node.Kind == NodeKind.Add) status.Visibility = Visibility.Collapsed;
        DockPanel.SetDock(status, Dock.Right);
        header.Children.Add(status);
        var bubble = new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(18), Margin = new Thickness(0, 0, 10, 0) };
        bubble.SetResourceReference(Border.BackgroundProperty, node.Kind == NodeKind.ThisPc ? "AccentBrush" : "SoftBrush");
        var glyph = MainWindow.Glyph(node.Glyph, 17, default);
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
            border.SetResourceReference(FrameworkElement.StyleProperty, "Chip");
            chips.Children.Add(border);
        }
        if (chips.Children.Count > 0) content.Children.Add(chips);

        var card = new Button { Content = content, Width = node.Kind == NodeKind.ThisPc ? MapLayout.HubWidth : MapLayout.CardWidth };
        card.SetResourceReference(FrameworkElement.StyleProperty, "NodeCard");
        AutomationProperties.SetAutomationId(card, "Node-" + node.Id);
        AutomationProperties.SetName(card, DeviceOverview.Describe(node));
        var id = node.Id;
        card.Click += (_, _) =>
        {
            if (DeviceOverview.IsMore(id)) showList();
            else select(id);
        };
        return (card, ring);
    }
}
