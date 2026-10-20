using System.Windows.Controls;
using Martlet.Core.Installation;
using Martlet.Home;
using Martlet.HostClient;
using Martlet.Mcp.Client;

namespace Martlet.Desktop;

/// <summary>The services MainWindow gives Companion › Smart home (<see cref="SmartHomePage"/> in Pages\SmartHome). The work the page
/// shows stays in the window (MainWindow.SmartHome.cs and MainWindow.HomeShare.cs).</summary>
public partial class MainWindow : ISmartHomePageServices
{
    private SmartHomePage? smartHomePage;

    private SmartHomePage SmartHomePage => smartHomePage ??= new(this);

    HomePreferences ISmartHomePageServices.Preferences => smartHome.Preferences;
    bool ISmartHomePageServices.Connected => smartHome.Connected;
    bool ISmartHomePageServices.ModelToolsEnabled => smartHome.ModelToolsEnabled;
    (McpServerStatus? Status, bool Conflict) ISmartHomePageServices.HomeAssistantTools() =>
        (mcpTools.Hub.Status.FirstOrDefault(s => s.Name == SmartHome.ServerName), mcpTools.ManagedConflicts.Any(s => s.Name == SmartHome.ServerName));
    bool ISmartHomePageServices.ClusterEnabled => clusterEnabled;
    IReadOnlyList<PairedHost> ISmartHomePageServices.Hosts() => NetworkMap.Hosts(Inputs());
    IReadOnlyList<HostHardware> ISmartHomePageServices.Hardware() => HardwareStore?.Load() ?? [];
    IReadOnlyList<HomeAction> ISmartHomePageServices.RecentActions => smartHome.RecentActions;
    string? ISmartHomePageServices.AddressDraft
    {
        get => homeAddressDraft;
        set => homeAddressDraft = value;
    }
    IReadOnlyList<FoundHomeAssistant>? ISmartHomePageServices.Found => homeFound;
    string? ISmartHomePageServices.FindStatus => homeFindStatus;
    bool ISmartHomePageServices.Finding => homeFinding;
    Uri? ISmartHomePageServices.SetupTarget => homeSetupTarget;
    string? ISmartHomePageServices.SetupStatus => homeSetupStatus;
    string? ISmartHomePageServices.SharedAddress => homeShared?.Address;
    string ISmartHomePageServices.ShareStatus => homeShareStatus;
    HomeSystem? ISmartHomePageServices.System => homeSystem;
    IReadOnlyList<HomeDiscovery>? ISmartHomePageServices.Discoveries => homeDiscoveries;
    IReadOnlyList<HomeUpdate>? ISmartHomePageServices.Updates => homeUpdates;
    HomeBackups? ISmartHomePageServices.Backups => homeBackups;
    string? ISmartHomePageServices.ManageStatus => homeManageStatus;
    string? ISmartHomePageServices.DevicesStatus => homeDevicesStatus;
    bool ISmartHomePageServices.ManageBusy => homeManageBusy;
    string? ISmartHomePageServices.ManageLoadedFor => homeManageLoadedFor;

    void ISmartHomePageServices.Find() => FindHomeAssistantAsync().Forget();
    void ISmartHomePageServices.UseFound(FoundHomeAssistant found) => UseFoundHomeAssistantAsync(found).Forget();
    void ISmartHomePageServices.CheckAddress(string address, bool signInIfReady) => CheckHomeAddressAsync(address, signInIfReady).Forget();
    void ISmartHomePageServices.SignIn(string address) => SignInHomeAssistantAsync(address).Forget();
    void ISmartHomePageServices.Connect(string address, PasswordBox token, Button connect) => ConnectSmartHomeAsync(address, token, connect).Forget();
    void ISmartHomePageServices.Disconnect() => DisconnectSmartHomeAsync().Forget();
    void ISmartHomePageServices.SetUp(Uri target, string name, string username, PasswordBox password, PasswordBox confirm, bool control, Button setUp) =>
        SetUpHomeAssistantAsync(target, name, username, password, confirm, control, setUp).Forget();
    void ISmartHomePageServices.CancelSetup() => CancelHomeSetup();
    void ISmartHomePageServices.Install(PairedHost host) => InstallHomeAssistantAsync(host).Forget();
    void ISmartHomePageServices.SyncShare() => SyncHomeShareAsync().Forget();
    void ISmartHomePageServices.SaveControl(bool control, bool allowSensitive, bool modelTools) => SaveSmartHomeControl(control, allowSensitive, modelTools);
    void ISmartHomePageServices.AddDiscovered(HomeDiscovery found) => AddDiscoveredAsync(found).Forget();
    void ISmartHomePageServices.IgnoreDiscovered(HomeDiscovery found) => IgnoreDiscoveredAsync(found).Forget();
    void ISmartHomePageServices.AddMqtt(PairedHost host) => AddMqttAsync(host).Forget();
    void ISmartHomePageServices.RefreshManagement() => RefreshHomeManagementAsync().Forget();
    void ISmartHomePageServices.InstallUpdate(HomeUpdate update) => InstallHomeUpdateAsync(update).Forget();
    void ISmartHomePageServices.Restart() => RestartHomeAssistantAsync().Forget();
    void ISmartHomePageServices.Backup() => BackupHomeAssistantAsync().Forget();
    void ISmartHomePageServices.OpenHomePage(string path) => OpenHomePage(path);
    Border ISmartHomePageServices.OptionPicker(string id, string heading, string? note, IReadOnlyList<PickerOption> options) =>
        OptionPicker(id, heading, note, options);
}
