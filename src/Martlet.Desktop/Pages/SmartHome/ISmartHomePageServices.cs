using System.Windows.Controls;
using Martlet.Core.Installation;
using Martlet.Home;
using Martlet.HostClient;
using Martlet.Mcp.Client;

namespace Martlet.Desktop;

/// <summary>Everything Companion › Smart home (<see cref="SmartHomePage"/> and <see cref="SmartHomePageModel"/>) reads and starts in
/// the app. MainWindow gives it to the page (MainWindow.SmartHomePage.cs) and keeps the work the page shows (rule 4 of
/// Pages\README.md): finding, checking, signing in to, connecting, setting up and installing Home Assistant, sharing it with your
/// other computers, saving what Martlet may do, adding devices and managing Home Assistant (MainWindow.SmartHome.cs and
/// MainWindow.HomeShare.cs). The secret boxes go to the window as they are (<see cref="PasswordBox"/>).</summary>
internal interface ISmartHomePageServices
{
    // ---------- what the page reads ----------

    /// <summary>The saved connection and what Martlet may do (smart-home.json).</summary>
    HomePreferences Preferences { get; }

    bool Connected { get; }

    /// <summary>Whether flexible requests are on and usable (connected, on and allowed).</summary>
    bool ModelToolsEnabled { get; }

    /// <summary>Home Assistant's MCP server among the conversation's tools, and whether the owner's own mcp.json already has one.</summary>
    (McpServerStatus? Status, bool Conflict) HomeAssistantTools();

    /// <summary>Whether Keep Martlet the same on all my computers is on.</summary>
    bool ClusterEnabled { get; }

    /// <summary>Your paired Martlet hosts, and the last check of each.</summary>
    IReadOnlyList<PairedHost> Hosts();

    IReadOnlyList<HostHardware> Hardware();

    /// <summary>The actions Martlet took since it started, newest first.</summary>
    IReadOnlyList<HomeAction> RecentActions { get; }

    /// <summary>The typed address that isn't used yet, kept while the page draws again.</summary>
    string? AddressDraft { get; set; }

    /// <summary>What Find on my network found and said, and whether it runs now.</summary>
    IReadOnlyList<FoundHomeAssistant>? Found { get; }

    string? FindStatus { get; }

    bool Finding { get; }

    /// <summary>A Home Assistant waiting for its owner account, and the setup form's last outcome.</summary>
    Uri? SetupTarget { get; }

    string? SetupStatus { get; }

    /// <summary>The address the other computers use, when this PC isn't connected yet, or null.</summary>
    string? SharedAddress { get; }

    /// <summary>The last outcome of sharing the connection with your other computers.</summary>
    string ShareStatus { get; }

    /// <summary>What Manage Home Assistant read last (each can be null when it couldn't be read).</summary>
    HomeSystem? System { get; }

    IReadOnlyList<HomeDiscovery>? Discoveries { get; }

    IReadOnlyList<HomeUpdate>? Updates { get; }

    HomeBackups? Backups { get; }

    string? ManageStatus { get; }

    string? DevicesStatus { get; }

    bool ManageBusy { get; }

    /// <summary>The address Manage Home Assistant read last.</summary>
    string? ManageLoadedFor { get; }

    // ---------- what the page starts ----------

    void Find();

    void UseFound(FoundHomeAssistant found);

    /// <summary>Checks whether the Home Assistant at <paramref name="address"/> still needs its owner account (then the setup form
    /// opens), or signs in when <paramref name="signInIfReady"/>.</summary>
    void CheckAddress(string address, bool signInIfReady = false);

    void SignIn(string address);

    void Connect(string address, PasswordBox token, Button connect);

    void Disconnect();

    void SetUp(Uri target, string name, string username, PasswordBox password, PasswordBox confirm, bool control, Button setUp);

    void CancelSetup();

    void Install(PairedHost host);

    void SyncShare();

    void SaveControl(bool control, bool allowSensitive, bool modelTools);

    void AddDiscovered(HomeDiscovery found);

    void IgnoreDiscovered(HomeDiscovery found);

    void AddMqtt(PairedHost host);

    void RefreshManagement();

    void InstallUpdate(HomeUpdate update);

    void Restart();

    void Backup();

    /// <summary>Opens a page of the connected Home Assistant in the browser.</summary>
    void OpenHomePage(string path);

    /// <summary>The window's option picker (it remembers which option it shows).</summary>
    Border OptionPicker(string id, string heading, string? note, IReadOnlyList<PickerOption> options);
}
