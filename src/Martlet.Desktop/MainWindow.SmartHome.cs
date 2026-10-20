using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Core.Contracts;
using Martlet.Core.Installation;
using Martlet.Core.Settings;
using Martlet.Home;
using Martlet.Mcp.Client;
using Martlet.Presentation;

namespace Martlet.Desktop;

/// <summary>The Companion page's Smart home tab: finding, setting up, signing in to and connecting Home Assistant (including
/// installing it on a Linux Martlet host), sharing the connection with the owner's other computers, what Martlet may do
/// with it (with the lock/door/garage/gate/alarm/valve safety tier), the devices Home Assistant found, managing Home
/// Assistant itself (updates, backups, restart) and the actions Martlet took since it started.</summary>
public partial class MainWindow
{
    private string? homeAddressDraft;
    private IReadOnlyList<FoundHomeAssistant>? homeFound;
    private string? homeFindStatus;
    private bool homeFinding;
    /// <summary>A Home Assistant waiting for its owner account (nobody has set it up yet).</summary>
    private Uri? homeSetupTarget;
    private string? homeSetupStatus;
    private HomeSystem? homeSystem;
    private IReadOnlyList<HomeDiscovery>? homeDiscoveries;
    private IReadOnlyList<HomeUpdate>? homeUpdates;
    private HomeBackups? homeBackups;
    private string? homeManageStatus, homeDevicesStatus, homeManageLoadedFor;
    private bool homeManageBusy;

    private async Task FindHomeAssistantAsync()
    {
        if (homeFinding || closing) return;
        homeFinding = true;
        homeFindStatus = "Asking your network for Home Assistant...";
        RenderTab();
        try
        {
            homeFound = await SmartHome.FindAsync(lifetime.Token);
            homeFindStatus = homeFound.Count switch
            {
                0 => "No Home Assistant answered on this network: type its address instead, or install one on a Linux Martlet host below.",
                1 => "Found 1 Home Assistant.",
                var count => $"Found {count} Home Assistants."
            };
        }
        catch (OperationCanceledException) { return; }
        catch (System.Net.Sockets.SocketException error) { homeFindStatus = $"Couldn't ask the network ({error.SocketErrorCode})."; }
        finally
        {
            homeFinding = false;
            if (!closing) RenderTab();
        }
    }

    private Task UseFoundHomeAssistantAsync(FoundHomeAssistant found)
    {
        homeAddressDraft = HomeAssistantEndpoint.Display(found.Address);
        return CheckHomeAddressAsync(homeAddressDraft, signInIfReady: true);
    }

    /// <summary>Checks whether a Home Assistant still needs its owner account: if so the setup form opens; otherwise Martlet
    /// says to sign in (or signs in straight away).</summary>
    private async Task CheckHomeAddressAsync(string address, bool signInIfReady = false)
    {
        if (closing) return;
        try
        {
            ActionText.Text = "Checking Home Assistant...";
            var state = await smartHome.OnboardingAsync(address, lifetime.Token);
            homeAddressDraft = HomeAssistantEndpoint.Display(HomeAssistantEndpoint.Normalize(address));
            if (state.NeedsOwner)
            {
                homeSetupTarget = HomeAssistantEndpoint.Normalize(address);
                homeSetupStatus = null;
                ActionText.Text = "Nobody has set up this Home Assistant yet: choose its owner account below.";
            }
            else
            {
                homeSetupTarget = null;
                if (signInIfReady) { RenderTab(); await SignInHomeAssistantAsync(address); return; }
                ActionText.Text = "This Home Assistant is already set up: use Sign in with Home Assistant to connect.";
            }
        }
        catch (HomeAssistantException error) { ActionText.Text = error.Message; }
        catch (OperationCanceledException) { }
        if (!closing) RenderTab();
    }

    private async Task SignInHomeAssistantAsync(string address)
    {
        if (closing) return;
        try
        {
            ActionText.Text = "Sign in to Home Assistant in your browser (Martlet waits up to 5 minutes)...";
            var info = await smartHome.SignInAsync(address, uri => Dispatcher.Invoke(() => OpenInBrowser(uri)), lifetime.Token);
            homeAddressDraft = null;
            homeManageLoadedFor = null;
            await ConnectedAsync(info);
        }
        catch (HomeAssistantException error) { ActionText.Text = error.Message; }
        catch (ContractException error) { ActionText.Text = error.Message; }
        catch (OperationCanceledException) { }
        if (!closing) RenderTab();
    }

    private async Task ConnectSmartHomeAsync(string address, PasswordBox tokenBox, Button connect)
    {
        if (closing) return;
        SecretLease? token = null;
        connect.IsEnabled = false;
        try
        {
            using (var entered = tokenBox.SecurePassword)
                if (entered.Length > 0) token = TakeKey(tokenBox);
            ActionText.Text = "Checking Home Assistant...";
            var wasConnected = smartHome.Connected;
            var info = await smartHome.ConnectAsync(address, token, lifetime.Token);
            homeAddressDraft = null;
            homeManageLoadedFor = null;
            if (wasConnected && token is null) ActionText.Text = $"Connected to {info.LocationName}.";
            else await ConnectedAsync(info);
        }
        catch (HomeAssistantException error) { ActionText.Text = error.Message; }
        catch (ContractException error) { ActionText.Text = error.Message; }
        catch (OperationCanceledException) { }
        finally
        {
            token?.Dispose();
            if (!closing) RenderTab();
        }
    }

    // After a new connection: it becomes the one every computer uses, and say what to do next.
    private async Task ConnectedAsync(HomeAssistantInfo info)
    {
        var text = smartHome.ControlEnabled
            ? $"Connected to {info.LocationName}."
            : $"Connected to {info.LocationName}: choose Home Assistant at the top of this page and press \"Use Home Assistant when I ask\" to use it.";
        if (clusterEnabled && NetworkMap.Hosts(Inputs()).Count > 0 && await ShareHomeAssistantAsync())
            text += " " + ActionText.Text;
        ActionText.Text = text;
    }

    private async Task DisconnectSmartHomeAsync()
    {
        var everywhere = clusterEnabled && NetworkMap.Hosts(Inputs()).Count > 0;
        if (!ConfirmationDialog.Confirm(this, everywhere
                ? "Disconnect Home Assistant and remove its saved token from all your computers and Martlet hosts?"
                : "Disconnect Home Assistant and remove its saved token from this PC?", "Martlet - smart home"))
            return;
        var reached = everywhere && await DisconnectEverywhereAsync();
        if (!reached) smartHome.Disconnect();
        homeManageLoadedFor = null;
        homeSystem = null;
        homeDiscoveries = null;
        homeUpdates = null;
        homeBackups = null;
        ActionText.Text = reached ? "Home Assistant is disconnected on all your computers."
            : everywhere ? "Home Assistant disconnected here; no Martlet host answered, so your other computers disconnect when one does."
            : "Home Assistant disconnected.";
        RenderTab();
    }

    // ---------- setting up a new Home Assistant ----------

    private void CancelHomeSetup()
    {
        homeSetupTarget = null;
        RenderTab();
    }

    private async Task SetUpHomeAssistantAsync(Uri target, string name, string username, PasswordBox password, PasswordBox confirm,
        bool control, Button button)
    {
        if (closing) return;
        if (name.Trim().Length == 0 || username.Trim().Length == 0)
        {
            homeSetupStatus = "Enter your name and a username.";
            RenderTab();
            return;
        }
        var chosen = password.Password;
        var repeated = confirm.Password;
        password.Clear();
        confirm.Clear();
        if (chosen.Length < 8 || chosen != repeated)
        {
            homeSetupStatus = chosen.Length < 8 ? "Choose a password of at least 8 characters." : "The passwords don't match.";
            RenderTab();
            return;
        }
        button.IsEnabled = false;
        try
        {
            homeSetupStatus = "Setting up Home Assistant...";
            ActionText.Text = "Setting up Home Assistant: creating the owner account and connecting...";
            var info = await smartHome.SetUpAsync(target.AbsoluteUri, name, username, chosen.AsMemory(), control, lifetime.Token);
            homeSetupTarget = null;
            homeSetupStatus = null;
            homeAddressDraft = null;
            homeManageLoadedFor = null;
            var text = $"Home Assistant is set up and connected ({info.LocationName}, version {info.Version}): sign in to it as {username.Trim()}.";
            if (clusterEnabled && NetworkMap.Hosts(Inputs()).Count > 0 && await ShareHomeAssistantAsync()) text += " " + ActionText.Text;
            ActionText.Text = text;
        }
        catch (HomeAssistantException error) { homeSetupStatus = error.Message; ActionText.Text = error.Message; }
        catch (ContractException error) { homeSetupStatus = error.Message; ActionText.Text = error.Message; }
        catch (ArgumentException error) { homeSetupStatus = error.Message; }
        catch (OperationCanceledException) { }
        if (!closing) RenderTab();
    }

    // ---------- Martlet hosts ----------

    /// <summary>Installs the home-assistant role on a Linux host the way this PC reaches it (SSH: its terms and the Install
    /// click come first), then opens the setup form for the new Home Assistant.</summary>
    private async Task InstallHomeAssistantAsync(PairedHost host)
    {
        if (HomeAssistantHosts.CannotInstall(host, HardwareStore?.Load().FirstOrDefault(h => h.HostId == host.HostId)) is { } why)
        {
            ActionText.Text = $"Home Assistant can't be installed on {host.HostId}: {why}";
            return;
        }
        var done = await RunHostActionAsync(host, HostAction.Add(HomeAssistantHosts.Role));
        if (done is null || closing) return;
        await CheckHostsAsync([host]);
        OpenCompanion(CompanionTab.SmartHome);
        await CheckHomeAddressAsync(HomeAssistantEndpoint.Display(HomeAssistantHosts.Address(host)));
    }

    // ---------- sharing ----------

    private void SaveSmartHomeControl(bool control, bool allowSensitive, bool modelTools)
    {
        var current = smartHome.Preferences;
        if (current.Control == control && current.AllowSensitive == allowSensitive && current.ModelTools == modelTools) return;
        ForgetPicker("SmartHome");
        var saved = smartHome.SetControl(control, allowSensitive, modelTools);
        if (saved) QueueSettingsSync();
        if (saved && modelTools && control) mcpTools.EnsureStarted(retry: true);
        ActionText.Text = !saved ? "Couldn't save the smart home setting: check access to your data directory."
            : !control ? "Home Assistant is off."
            : modelTools != current.ModelTools
                ? modelTools
                    ? "Flexible smart-home requests are on: reload an open talk window to use them."
                    : "Flexible smart-home requests are off."
            : allowSensitive ? "Locks, doors and alarms are allowed after you click Yes each time."
            : "Martlet will use Home Assistant when you ask.";
        RenderTab();
    }

    private async Task AddDiscoveredAsync(HomeDiscovery found)
    {
        try
        {
            ActionText.Text = $"Adding {found.Name}...";
            var step = await smartHome.AddDiscoveredAsync(found, lifetime.Token);
            ActionText.Text = SmartHomePageModel.FlowText(found.Name, step);
            if (step.Kind is HomeStepKind.Form or HomeStepKind.External or HomeStepKind.Menu) OpenHomePage("config/integrations/dashboard");
        }
        catch (HomeAssistantException error) { ActionText.Text = error.Message; }
        catch (OperationCanceledException) { }
        await RefreshHomeManagementAsync();
    }

    private async Task IgnoreDiscoveredAsync(HomeDiscovery found)
    {
        try
        {
            await smartHome.IgnoreDiscoveredAsync(found, lifetime.Token);
            ActionText.Text = $"Home Assistant won't offer {found.Name} again (you can still add it from its Integrations page).";
        }
        catch (HomeAssistantException error) { ActionText.Text = error.Message; }
        catch (OperationCanceledException) { }
        await RefreshHomeManagementAsync();
    }

    private async Task AddMqttAsync(PairedHost host)
    {
        try
        {
            ActionText.Text = "Adding MQTT to Home Assistant...";
            var step = await smartHome.AddMqttAsync(host.Address, lifetime.Token);
            ActionText.Text = SmartHomePageModel.FlowText("MQTT", step);
            if (step.Kind is HomeStepKind.Form or HomeStepKind.External or HomeStepKind.Menu) OpenHomePage("config/integrations/dashboard");
        }
        catch (HomeAssistantException error) { ActionText.Text = error.Message; }
        catch (OperationCanceledException) { }
        await RefreshHomeManagementAsync();
    }

    /// <summary>Reads Home Assistant's version, integrations, discovered devices, updates and backups for this page. Each part
    /// fails on its own, so a non-administrator token still shows what it may read.</summary>
    private async Task RefreshHomeManagementAsync()
    {
        if (homeManageBusy || closing || !smartHome.Connected) return;
        homeManageBusy = true;
        homeManageLoadedFor = smartHome.Preferences.Address;
        try
        {
            var problems = new List<string>();
            async Task<T?> Read<T>(Func<CancellationToken, Task<T>> read) where T : class
            {
                try { return await read(lifetime.Token); }
                catch (HomeAssistantException error)
                {
                    if (!problems.Contains(error.Message)) problems.Add(error.Message);
                    return null;
                }
            }
            var system = Read(smartHome.SystemAsync);
            var discoveries = Read(smartHome.DiscoveriesAsync);
            var updates = Read(smartHome.UpdatesAsync);
            var backups = Read(smartHome.BackupsAsync);
            homeSystem = await system ?? homeSystem;
            homeDiscoveries = await discoveries;
            homeUpdates = await updates;
            homeBackups = await backups ?? homeBackups;
            homeManageStatus = problems.Count == 0 ? null : string.Join(" ", problems);
            homeDevicesStatus = homeDiscoveries is null ? homeManageStatus ?? "Couldn't read the devices Home Assistant found."
                : homeDiscoveries.Count == 0 ? $"No new devices waiting (checked {DateTime.Now:t})."
                : $"{homeDiscoveries.Count} waiting to be added (checked {DateTime.Now:t}).";
        }
        catch (OperationCanceledException) { }
        finally
        {
            homeManageBusy = false;
            if (SmartHomeCanRender()) RenderTab();
        }
    }

    private async Task InstallHomeUpdateAsync(HomeUpdate update)
    {
        if (!ConfirmationDialog.Confirm(this, $"Install {update.Title} {update.Latest} (Home Assistant may restart to finish it)?", "Martlet - smart home"))
            return;
        try
        {
            await smartHome.InstallUpdateAsync(update, lifetime.Token);
            ActionText.Text = $"Home Assistant is installing {update.Title}, so refresh in a few minutes.";
        }
        catch (HomeAssistantException error) { ActionText.Text = error.Message; }
        catch (OperationCanceledException) { }
        await RefreshHomeManagementAsync();
    }

    private async Task RestartHomeAssistantAsync()
    {
        if (!ConfirmationDialog.Confirm(this, "Restart Home Assistant (your smart home doesn't respond for a minute or so)?", "Martlet - smart home"))
            return;
        try
        {
            await smartHome.RestartAsync(lifetime.Token);
            ActionText.Text = "Home Assistant is restarting and is back in a minute or so.";
        }
        catch (HomeAssistantException error) { ActionText.Text = error.Message; }
        catch (OperationCanceledException) { }
        RenderTab();
    }

    private async Task BackupHomeAssistantAsync()
    {
        try
        {
            await smartHome.BackupAsync(lifetime.Token);
            ActionText.Text = "Home Assistant started a backup on its own disk: refresh in a minute to see it.";
        }
        catch (HomeAssistantException error) { ActionText.Text = error.Message; }
        catch (OperationCanceledException) { }
        RenderTab();
    }

    private void OpenHomePage(string path)
    {
        if (!smartHome.Connected) return;
        try { OpenInBrowser(new Uri(HomeAssistantEndpoint.Normalize(smartHome.Preferences.Address), path)); }
        catch (HomeAssistantException error) { ActionText.Text = error.Message; }
    }

    private void OpenInBrowser(Uri address)
    {
        if (address.Scheme is not ("http" or "https")) return;
        try { Process.Start(new ProcessStartInfo(address.AbsoluteUri) { UseShellExecute = true })?.Dispose(); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ActionText.Text = $"Couldn't open the browser ({error.Message}): open {address.AbsoluteUri} yourself.";
        }
    }

    /// <summary>The Smart home page may be rebuilt: it is open and nobody is typing in one of its fields.</summary>
    private bool SmartHomeCanRender() => !closing && openTab == CompanionTab.SmartHome &&
        System.Windows.Input.Keyboard.FocusedElement is not (System.Windows.Controls.Primitives.TextBoxBase or PasswordBox);
}
