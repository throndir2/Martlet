using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Martlet.Desktop;

/// <summary>The Devices list: every device as a card in a grid, filtered and searched (<see cref="DeviceOverview"/>). It
/// replaces the map by itself once the map can't fit every device, and the owner can switch between the two.</summary>
public partial class MainWindow
{
    private IReadOnlyList<NetworkNode> mapNodes = [];
    /// <summary>The owner's choice of map (false) or list (true); null shows the list once the map can't fit every device.</summary>
    private bool? deviceListChosen;
    private bool deviceListShown;
    private string deviceFilter = "all";
    private readonly List<(string Id, Button Card)> deviceListCards = [];
    private bool settingDevicesView;
    private bool remapping;

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
        if (!settingDevicesView && deviceListShown) RenderDeviceList();
    }

    private void DeviceListScroll_SizeChanged(object sender, SizeChangedEventArgs e) => SizeDeviceList();

    /// <summary>As many columns of cards as fit, and a list that scrolls on its own within about half the page.</summary>
    private void SizeDeviceList()
    {
        DeviceListScroll.MaxHeight = Math.Max(300, DevicesPage.ActualHeight * 0.5);
        if (DeviceListScroll.ActualWidth > 0)
            DeviceList.Columns = Math.Max(1, (int)((DeviceListScroll.ActualWidth - 12) / 196));
    }

    private void RenderDeviceList()
    {
        FillDeviceFilters();
        var search = DeviceSearch.Text;
        var shown = DeviceOverview.ListNodes(mapNodes, deviceFilter, search);
        DeviceList.Children.Clear();
        deviceListCards.Clear();
        var index = 0;
        foreach (var node in shown)
        {
            var (card, _) = NodeCard(node);
            card.Width = double.NaN;
            card.Margin = new Thickness(0, 0, 10, 10);
            DeviceList.Children.Add(card);
            deviceListCards.Add((node.Id, card));
            Motion.Enter(card, dy: 8, milliseconds: 240, delay: Math.Min(index++, 12) * 25);
        }
        DeviceListStatus.Text = DeviceOverview.ListStatus(mapNodes, shown, search);
        SizeDeviceList();
        MarkSelectedDevice();
        // Start at the top, or at the selected device when it would be out of view.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            DeviceListScroll.ScrollToTop();
            if (deviceListCards.FirstOrDefault(c => c.Id == selectedNode).Card is { IsLoaded: true } selected &&
                selected.TranslatePoint(new Point(0, 0), DeviceList).Y is var top && top + selected.ActualHeight > DeviceListScroll.ViewportHeight)
                DeviceListScroll.ScrollToVerticalOffset(top - 10);
        });
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
                Dispatcher.BeginInvoke(RenderDeviceList);
            };
            DeviceFilters.Children.Add(option);
        }
    }

    /// <summary>Highlights the selected device, and greys Add a computer, a job nobody does and "N more", on the map and the list.</summary>
    private void MarkSelectedDevice()
    {
        object? Tag(string id, NodeKind kind) =>
            id == selectedNode ? "Selected" : kind is NodeKind.Add or NodeKind.Missing || DeviceOverview.IsMore(id) ? "Ghost" : null;
        foreach (var element in mapElements)
            element.Card.Tag = Tag(element.Node.Id, element.Node.Kind);
        foreach (var (id, card) in deviceListCards)
            card.Tag = Tag(id, mapNodes.FirstOrDefault(n => n.Id == id)?.Kind ?? NodeKind.Computer);
    }
}
