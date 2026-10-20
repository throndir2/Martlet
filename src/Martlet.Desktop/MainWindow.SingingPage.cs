using System.Windows;
using System.Windows.Controls;
using Martlet.Core.Cluster;
using Martlet.Core.Singing;
using Martlet.HostClient;

namespace Martlet.Desktop;

/// <summary>The services MainWindow gives Companion › Singing (<see cref="SingingPage"/> in Pages\Singing). The work the page shows
/// stays in the window (MainWindow.Singing.cs and MainWindow.SingingCloud.cs).</summary>
public partial class MainWindow : ISingingPageServices
{
    private SingingPage? singingPage;

    /// <summary>The Singing page, made once: it keeps the cloud provider each Add form shows while the window lives.</summary>
    private SingingPage SingingPage => singingPage ??= new(this, PoolListCard, (id, options) => OptionPickerBody(id, options));

    string? ISingingPageServices.DataDirectory => store?.DataDirectory;
    PairedHost? ISingingPageServices.ThisPcHost() => ThisPcHost();
    PairedHost? ISingingPageServices.FindHost(string? hostId) => FindHost(hostId);
    IReadOnlyList<PairedHost> ISingingPageServices.OwnHosts() => [.. NetworkMap.Hosts(Inputs()).Where(h => !h.Shared)];
    bool ISingingPageServices.Offers(PairedHost host, string kind) => Offers(host, kind);
    bool? ISingingPageServices.Reachable(string hostId) => hostChecks.GetValueOrDefault(hostId)?.Reachable;
    string? ISingingPageServices.CannotHand(string hostId, string roleKind, string job) => CannotHand(hostId, roleKind, job);
    bool ISingingPageServices.CanAdd(PairedHost host) => !host.Shared && HostCan(host.HostId, HostRoles.Singing) is not { Allowed: false };
    string? ISingingPageServices.ThisPcCannotSing() => ThisPcCannotSing();
    bool ISingingPageServices.TryService(string hostId, out SongClient.SingingService? service) => singingServices.TryGetValue(hostId, out service);
    void ISingingPageServices.ReadServices(IEnumerable<PairedHost> hosts)
    {
        foreach (var host in hosts.Where(h => !singingServices.ContainsKey(h.HostId))) ReadSingingServiceAsync(host).Forget();
    }
    string? ISingingPageServices.PendingHost => singingPendingHost;
    SingingAddOn? ISingingPageServices.PendingAddOn => singingPendingAddOn;
    string? ISingingPageServices.Failure(string key) => singingFailures.GetValueOrDefault(key);
    string? ISingingPageServices.AddOnFailure(string key, string addOnId) => singingAddOnFailures.GetValueOrDefault((key, addOnId));
    string? ISingingPageServices.CloudState(string areaId) => singingCloudStates.GetValueOrDefault(areaId);

    void ISingingPageServices.EnsurePools(SingingPreferences preferences)
    {
        EnsureSingingPool(preferences);
        EnsureVoiceMatchPool(preferences);
    }
    void ISingingPageServices.SetUp(PairedHost? host, SingingAddOn? addOn) => SetUpSingingAsync(host, addOn).Forget();
    void ISingingPageServices.SaveMember(PoolArea area, PoolMember member, string done) => SaveSingingMember(area, member, done);
    void ISingingPageServices.Expand(PoolArea area, string key) => poolExpanded.Add(area.Id + " " + key);
    void ISingingPageServices.SaveKey(string provider, PasswordBox key) => SaveSingingKeyAsync(provider, key).Forget();
    void ISingingPageServices.AddCloud(PoolArea area, string provider, string model, PasswordBox key, bool consent) =>
        AddSingingCloudAsync(area, provider, model, key, consent).Forget();
    void ISingingPageServices.ClearCloudState(string areaId) => singingCloudStates.Remove(areaId);
    void ISingingPageServices.MarkEdited() => tabEdited = true;
    void ISingingPageServices.RedrawLater() => Dispatcher.InvokeAsync(() => { if (!closing && openTab == CompanionTab.Singing) RenderTab(); });
}
