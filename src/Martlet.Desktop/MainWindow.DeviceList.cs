using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Martlet.Desktop;

/// <summary>The Devices list: every device as one compact row (status, name, computer, status text and jobs), grouped under
/// Needs attention, Your computers and Cloud services, filtered and searched (<see cref="DeviceOverview"/>). It replaces the map
/// by itself once the map can't fit every device, and the owner can switch between the two. It is a list box: one Tab stop,
/// arrow keys, Home, End and typing a name move through the devices, and selecting one shows its details below.</summary>
public partial class MainWindow
{
    private IReadOnlyList<NetworkNode> mapNodes = [];
    /// <summary>The owner's choice of map (false) or list (true); null shows the list once the map can't fit every device.</summary>
    private bool? deviceListChosen;
    private bool deviceListShown;
    private string deviceFilter = "all";
    private readonly List<(string Id, ListBoxItem Row)> deviceListRows = [];
    private bool settingDevicesView;
    private bool remapping;
    /// <summary>True while code (not the owner) changes the list's selection.</summary>
    private bool markingDevice;
    /// <summary>The filter and search the list shows now; drawing the same list again (a device changed) keeps its place.</summary>
    private string? deviceListShownFor;

    private void DevicesView_Checked(object sender, RoutedEventArgs e)
    {
        if (settingDevicesView) return;
        deviceListChosen = ReferenceEquals(sender, DevicesViewList);
        RenderMap();
    }

    /// <summary>A map's "N more" card opens the list with every device.</summary>
    private void ShowDeviceList()
    {
        deviceListChosen = true;
        deviceFilter = "all";
        settingDevicesView = true;
        DeviceSearch.Text = "";
        settingDevicesView = false;
        RenderMap();
    }

    private void DeviceSearch_Changed(object sender, TextChangedEventArgs e)
    {
        if (!settingDevicesView && deviceListShown) RenderDeviceList(animate: false);
    }

    /// <summary>A list that scrolls on its own within about half the page, so the selected device's details stay close.</summary>
    private void SizeDeviceList() => DeviceList.MaxHeight = Math.Max(300, DevicesPage.ActualHeight * 0.5);

    private void ClearDeviceList()
    {
        markingDevice = true;
        try { DeviceList.Items.Clear(); }
        finally { markingDevice = false; }
        deviceListRows.Clear();
        deviceListShownFor = null;
    }

    private void RenderDeviceList(bool animate = true)
    {
        FillDeviceFilters();
        var search = DeviceSearch.Text;
        var groups = DeviceOverview.ListGroups(mapNodes, deviceFilter, search);
        var shownFor = deviceFilter + "\n" + search.Trim();
        var again = shownFor == deviceListShownFor;
        var offset = again ? DeviceListScroller()?.VerticalOffset ?? 0 : 0;
        // The rows are drawn again: someone moving through the list with the keyboard keeps the focus on their row.
        var focused = DeviceList.IsKeyboardFocusWithin;
        ClearDeviceList();
        deviceListShownFor = shownFor;
        var index = 0;
        foreach (var group in groups)
        {
            if (group.Label.Length > 0) DeviceList.Items.Add(GroupRow(group));
            foreach (var node in group.Nodes)
            {
                var row = DeviceRow(node);
                DeviceList.Items.Add(row);
                deviceListRows.Add((node.Id, row));
                // The first rows of a new list come in; a list drawn again (a device changed) stays still.
                if (animate && !again && index < 14) Motion.Enter(row, dy: 6, milliseconds: 220, delay: index++ * 20);
            }
        }
        DeviceListStatus.Text = DeviceOverview.ListStatus(mapNodes, [.. groups.SelectMany(g => g.Nodes)], search);
        SizeDeviceList();
        MarkSelectedDevice();
        // A list drawn again keeps its place; a new one starts at the top (devices needing attention first), or shows the device
        // the owner chose (on Home, a job's Show or the map) when it is further down.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            var selected = deviceListRows.FirstOrDefault(r => r.Id == selectedNode).Row;
            if (focused) (selected ?? (UIElement)DeviceList).Focus();
            if (DeviceListScroller() is not { } scroll) return;
            if (again)
            {
                scroll.ScrollToVerticalOffset(offset);
                return;
            }
            scroll.ScrollToTop();
            if (selectedNode != "this-pc" && selected is not null) DeviceList.ScrollIntoView(selected);
        });
    }

    /// <summary>The list box's own scroll viewer, once its template is applied.</summary>
    private ScrollViewer? DeviceListScroller()
    {
        DependencyObject? element = DeviceList;
        while (element is not null and not ScrollViewer)
            element = VisualTreeHelper.GetChildrenCount(element) > 0 ? VisualTreeHelper.GetChild(element, 0) : null;
        return element as ScrollViewer;
    }

    /// <summary>One device on one line: its status dot and icon, name, computer, status and up to three jobs.</summary>
    private ListBoxItem DeviceRow(NetworkNode node)
    {
        var add = node.Kind == NodeKind.Add;
        var grid = new Grid();
        foreach (var width in new[] { new GridLength(20), new GridLength(38), new GridLength(1.5, GridUnitType.Star),
                     new GridLength(2.4, GridUnitType.Star), new GridLength(1.6, GridUnitType.Star), new GridLength(2.5, GridUnitType.Star) })
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        void Place(UIElement element, int column)
        {
            Grid.SetColumn(element, column);
            grid.Children.Add(element);
        }
        TextBlock Cell(string text, string brush, bool strong = false, double size = 13)
        {
            var cell = new TextBlock
            {
                Text = text, FontSize = size, FontWeight = strong ? FontWeights.SemiBold : FontWeights.Normal, Margin = new Thickness(0, 0, 12, 0),
                TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center
            };
            cell.SetResourceReference(TextBlock.ForegroundProperty, brush);
            return cell;
        }

        // Add a computer has no status, so it shows no dot.
        if (!add) Place(Dot(node.Health, 10, default), 0);
        var bubble = new Border
        {
            Width = 28, Height = 28, CornerRadius = new CornerRadius(14), HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };
        bubble.SetResourceReference(Border.BackgroundProperty, node.Kind == NodeKind.ThisPc ? "AccentBrush" : "SurfaceBrush");
        var glyph = Glyph(node.Glyph, 14, default);
        glyph.SetResourceReference(TextBlock.ForegroundProperty, node.Kind == NodeKind.ThisPc ? "OnAccentBrush" : "AccentBrush");
        bubble.Child = glyph;
        Place(bubble, 1);
        Place(Cell(node.Title, add ? "MutedBrush" : "TextBrush", strong: true), 2);
        Place(Cell(node.Subtitle, "MutedBrush", size: 12), 3);
        if (!add)
            Place(Cell(node.HealthText, node.Health switch
            {
                NodeHealth.Attention => "WarningBrush",
                NodeHealth.Configuring => "AccentBrush",
                NodeHealth.Ready => "TextBrush",
                _ => "MutedBrush"
            }, size: 12), 4);
        var chips = new StackPanel { Orientation = Orientation.Horizontal, ClipToBounds = true, VerticalAlignment = VerticalAlignment.Center };
        foreach (var chip in DeviceOverview.Chips(node))
        {
            var border = new Border
            {
                Child = new TextBlock { Text = chip, FontSize = 11, TextWrapping = TextWrapping.NoWrap }, Margin = new Thickness(0, 0, 6, 0),
                Padding = new Thickness(8, 1, 8, 1)
            };
            border.SetResourceReference(StyleProperty, "Chip");
            chips.Children.Add(border);
        }
        Place(chips, 5);

        var row = new ListBoxItem { Content = grid, Tag = node.Id };
        row.SetResourceReference(StyleProperty, "DeviceRow");
        TextSearch.SetText(row, node.Title);
        AutomationProperties.SetAutomationId(row, "Node-" + node.Id);
        AutomationProperties.SetName(row, DeviceOverview.Describe(node));
        return row;
    }

    /// <summary>A group's heading, such as "Needs attention  2" (how many devices; a job nobody does yet isn't one).</summary>
    private static ListBoxItem GroupRow(DeviceGroup group)
    {
        var heading = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold };
        heading.Inlines.Add(new Run(group.Label));
        if (DeviceOverview.Devices(group.Nodes) is > 0 and var count)
            heading.Inlines.Add(new Run("   " + count) { FontWeight = FontWeights.Normal });
        if (group.Key == "attention") heading.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
        var row = new ListBoxItem { Content = heading };
        row.SetResourceReference(StyleProperty, "DeviceGroupRow");
        AutomationProperties.SetAutomationId(row, "DeviceGroup-" + group.Key);
        AutomationProperties.SetName(row, DeviceOverview.GroupName(group));
        return row;
    }

    /// <summary>Selecting a row (a click, the arrow keys, typing a name or MCP's ui_click) shows that device's details; a click
    /// also brings them into view, the keyboard leaves the page where it is so the list stays in view.</summary>
    private void DeviceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (markingDevice) return;
        if (DeviceList.SelectedItem is not ListBoxItem { Tag: string id })
        {
            // A heading is not a device: keep the device that was selected.
            MarkSelectedDevice();
            return;
        }
        if (id != selectedNode) SelectNode(id, animate: InputManager.Current.MostRecentInputDevice is not KeyboardDevice);
    }

    /// <summary>All, Needs attention, Hosts, Computers and Cloud, each with how many devices it shows; a filter that would
    /// show nothing is left out unless it is the chosen one.</summary>
    private void FillDeviceFilters()
    {
        DeviceFilters.Children.Clear();
        foreach (var (key, label) in DeviceOverview.Filters)
        {
            var count = DeviceOverview.Devices(mapNodes.Where(n => DeviceOverview.Matches(n, key)));
            if (count == 0 && key != "all" && key != deviceFilter) continue;
            var option = new RadioButton
            {
                Content = $"{label}  {count}", GroupName = "DeviceFilter", IsChecked = key == deviceFilter, FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            };
            option.SetResourceReference(StyleProperty, "FilterPill");
            AutomationProperties.SetAutomationId(option, "DeviceFilter-" + key);
            AutomationProperties.SetName(option, $"Show {label.ToLowerInvariant()}: {count}");
            option.Checked += (_, _) =>
            {
                if (deviceFilter == key) return;
                deviceFilter = key;
                Dispatcher.BeginInvoke(() => RenderDeviceList());
            };
            DeviceFilters.Children.Add(option);
        }
    }

    /// <summary>Highlights the selected device on the map and in the list, and greys Add a computer, a job nobody does and
    /// "N more" on the map.</summary>
    private void MarkSelectedDevice()
    {
        object? Tag(string id, NodeKind kind) =>
            id == selectedNode ? "Selected" : kind is NodeKind.Add or NodeKind.Missing || DeviceOverview.IsMore(id) ? "Ghost" : null;
        foreach (var element in DevicesMap.Elements)
            element.Card.Tag = Tag(element.Node.Id, element.Node.Kind);
        markingDevice = true;
        try { DeviceList.SelectedItem = deviceListRows.FirstOrDefault(r => r.Id == selectedNode).Row; }
        finally { markingDevice = false; }
    }
}
