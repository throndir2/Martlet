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

    private DevicesMapView? devicesMap;

    /// <summary>The map's drawing (<see cref="DevicesMapView"/> in Pages\Devices), made once.</summary>
    private DevicesMapView DevicesMap => devicesMap ??= new(MapCanvas, MapHost, id => SelectNode(id, animate: true), ShowDeviceList);

    private void MapHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (MapHost.ActualWidth <= 18) return;
        // Narrow windows scale the whole map down, so the host then needs less height.
        if (e.WidthChanged) DevicesMap.Size(DevicesPage.ActualHeight);
        DevicesMap.SizeCanvas();
        DevicesMap.Layout();
    }

    /// <summary>The map takes the top of the page, a little under half its height (taller when its cards need it);
    /// the selected device's details follow.</summary>
    private void DevicesPage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        DevicesMap.Size(DevicesPage.ActualHeight);
        SizeDeviceList();
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
        DevicesMap.Clear();
        if (deviceListShown)
        {
            RenderDeviceList();
            SelectNode(selectedNode, animate: false);
            return;
        }
        ClearDeviceList();
        nodes = DeviceOverview.MapNodes(nodes, selectedNode);
        DevicesMap.Draw(nodes, DevicesPage.ActualHeight);
        SelectNode(selectedNode, animate: false);
    }

    private void SelectNode(string id, bool animate)
    {
        // A device the map folded into "N more" (selected from Home or a job's Show) takes a place on the map.
        if (!deviceListShown && !remapping && mapNodes.Any(n => n.Id == id) && !DevicesMap.Shows(id))
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
