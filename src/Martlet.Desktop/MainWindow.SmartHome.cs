using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Core.Contracts;
using Martlet.Core.Installation;
using Martlet.Core.Settings;
using Martlet.Home;
using Martlet.Mcp.Client;

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
    private bool homeFinding, homeShareOnConnect = true;
    /// <summary>A Home Assistant waiting for its owner account (nobody has set it up yet).</summary>
    private Uri? homeSetupTarget;
    private string? homeSetupStatus;
    private HomeSystem? homeSystem;
    private IReadOnlyList<HomeDiscovery>? homeDiscoveries;
    private IReadOnlyList<HomeUpdate>? homeUpdates;
    private HomeBackups? homeBackups;
    private string? homeManageStatus, homeDevicesStatus, homeManageLoadedFor;
    private bool homeManageBusy;

    private void RenderSmartHomeTab(Panel page)
    {
        var saved = smartHome.Preferences;
        var connected = smartHome.Connected;
        var hosts = NetworkMap.Hosts(Inputs());

        page.Children.Add(ConnectionCard(saved, connected, hosts.Count > 0));
        if (homeSetupTarget is { } target) page.Children.Add(SetupCard(target, hosts.Count > 0));
        if (hosts.Count > 0) page.Children.Add(HostsCard(hosts, saved, connected));
        if (connected && hosts.Count > 0) page.Children.Add(ShareCard(saved));
        if (!connected && homeShared is { Address: not null } offered && hosts.Count > 0)
            page.Children.Add(Card(Heading("Shared by your other computers"),
                Status(offered.Revision > saved.SharedRevision
                    ? $"Your other computers share Home Assistant at {offered.Address}. Martlet connects to it by itself within a minute."
                    : $"Your other computers share Home Assistant at {offered.Address}.", "SmartHomeShareStatus"),
                Row(PageButton("Use the shared one", () => UseSharedHomeAssistantAsync().Forget(), primary: true, id: "SmartHomeUseShared"))));

        page.Children.Add(PermissionsCard(saved, connected));
        if (connected)
        {
            page.Children.Add(DevicesCard(saved, hosts));
            page.Children.Add(ManageCard(saved, hosts));
            if (homeManageLoadedFor != saved.Address && !homeManageBusy) RefreshHomeManagementAsync().Forget();
        }

        var recent = smartHome.RecentActions;
        var log = new List<UIElement> { Heading("Recent actions") };
        if (recent.Count == 0)
            log.Add(Note("No smart-home actions yet. This list clears when you close Martlet.",
                new Thickness(0, 0, 0, 4)));
        else
            foreach (var action in recent)
                log.Add(Note($"{action.At:t}  {action.Summary}", new Thickness(0, 0, 0, 4)));
        page.Children.Add(Card([.. log]));
    }

    // ---------- connecting ----------

    private Border ConnectionCard(HomePreferences saved, bool connected, bool hasHosts)
    {
        var address = new TextBox
        {
            MaxLength = 512, Width = 420, HorizontalAlignment = HorizontalAlignment.Left,
            Text = homeAddressDraft ?? (saved.Address.Length > 0 ? saved.Address : HomeAssistantEndpoint.Example)
        };
        AutomationProperties.SetAutomationId(address, "SmartHomeAddress");
        address.TextChanged += (_, _) => homeAddressDraft = address.Text;
        var token = new PasswordBox { MaxLength = SecretLease.MaximumLength, Width = 420, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(token, "Home Assistant long-lived access token");
        AutomationProperties.SetAutomationId(token, "SmartHomeToken");
        var tokenSavedMark = new TextBlock
        {
            Text = "••••••••  Token saved (type to replace)", IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0), Opacity = 0.7,
            Visibility = connected ? Visibility.Visible : Visibility.Collapsed
        };
        tokenSavedMark.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        token.PasswordChanged += (_, _) =>
        {
            using var entered = token.SecurePassword;
            tokenSavedMark.Visibility = connected && entered.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        };
        var tokenField = new Grid { Width = 420, HorizontalAlignment = HorizontalAlignment.Left, Children = { token, tokenSavedMark } };

        var find = PageButton(homeFinding ? "Looking..." : "Find on my network", () => FindHomeAssistantAsync().Forget(), id: "SmartHomeFind");
        find.IsEnabled = !homeFinding;
        var signIn = PageButton(connected ? "Sign in again" : "Sign in with Home Assistant", () => SignInHomeAssistantAsync(address.Text).Forget(),
            primary: !connected, id: "SmartHomeSignIn");
        var check = PageButton("Set up a new one", () => CheckHomeAddressAsync(address.Text).Forget(), id: "SmartHomeCheck");
        Button? connect = null;
        connect = PageButton(connected ? "Reconnect" : "Connect with token", () => ConnectSmartHomeAsync(address, token, connect!).Forget(),
            id: "SmartHomeConnect");
        var disconnect = connected ? PageButton("Disconnect", DisconnectSmartHome, id: "SmartHomeDisconnect") : null;

        var share = new CheckBox
        {
            IsChecked = homeShareOnConnect, Margin = new Thickness(0, 10, 0, 0), Visibility = hasHosts && !connected ? Visibility.Visible : Visibility.Collapsed,
            Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "Share it with my other computers through my Martlet hosts" }
        };
        AutomationProperties.SetAutomationId(share, "SmartHomeShareOnConnect");
        share.Checked += (_, _) => homeShareOnConnect = true;
        share.Unchecked += (_, _) => homeShareOnConnect = false;

        var children = new List<UIElement>
        {
            Heading("Home Assistant"),
            Note("Connect Home Assistant so Martlet can answer questions and control devices when you ask. Martlet only uses the address below.",
                new Thickness(0, 0, 0, 8)),
            Status(connected
                ? $"Connected to {saved.LocationName} at {saved.Address}" + (saved.Version.Length > 0 ? $" (Home Assistant {saved.Version})." : ".") +
                    (saved.FollowShare ? saved.SharedBy.Length > 0 ? $" Shared through {saved.SharedBy}." : " Shared with your other computers." : "")
                : "Not connected.", "SmartHomeStatus", 15),
            new Label { Content = "Home Assistant _address", Target = address, Padding = new Thickness(0, 10, 0, 4) },
            address,
            Note("Use the address you open Home Assistant with, like http://homeassistant.local:8123, or let Martlet find it. Use https outside your home network.",
                new Thickness(0, 4, 0, 0)),
            Row(find)
        };
        if (homeFindStatus is { } findStatus) children.Add(Status(findStatus, "SmartHomeFindStatus"));
        var index = 0;
        foreach (var found in homeFound ?? [])
        {
            var n = index++;
            var use = PageButton("Use this one", () => UseFoundHomeAssistantAsync(found).Forget(), link: true, id: $"SmartHomeFoundUse-{n}");
            var line = Status($"{found.Name}: {HomeAssistantEndpoint.Display(found.Address)}" + (found.Version is { } v ? $" (Home Assistant {v})" : ""),
                $"SmartHomeFound-{n}");
            line.Margin = new Thickness(0, 6, 0, 0);
            children.Add(line);
            children.Add(use);
        }
        children.Add(share);
        children.Add(Row(signIn, check, disconnect));
        children.Add(Note("Sign in opens Home Assistant's own sign-in page in your browser; Martlet then keeps its own access token. " +
            "Set up a new one creates the owner account of a Home Assistant nobody has set up yet.", new Thickness(0, 2, 0, 0)));
        children.Add(new Label { Content = "Or paste a long-lived access _token", Target = token, Padding = new Thickness(0, 12, 0, 4) });
        children.Add(tokenField);
        children.Add(Note("Create one in your Home Assistant profile under Security. Martlet stores it in Windows Credential Manager and sends it only to this address.",
            new Thickness(0, 4, 0, 0)));
        children.Add(Row(connect));
        return Card([.. children]);
    }

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
                0 => "No Home Assistant answered on this network. Type its address instead, or install one on a Linux Martlet host below.",
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
                ActionText.Text = "Nobody has set up this Home Assistant yet. Choose its owner account below.";
            }
            else
            {
                homeSetupTarget = null;
                if (signInIfReady) { RenderTab(); await SignInHomeAssistantAsync(address); return; }
                ActionText.Text = "This Home Assistant is already set up. Use Sign in with Home Assistant to connect.";
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
            ActionText.Text = "Sign in to Home Assistant in your browser. Martlet waits up to 5 minutes...";
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

    private async Task ConnectSmartHomeAsync(TextBox address, PasswordBox tokenBox, Button connect)
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
            var info = await smartHome.ConnectAsync(address.Text, token, lifetime.Token);
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

    // After a new connection: share it when chosen, and say what to do next.
    private async Task ConnectedAsync(HomeAssistantInfo info)
    {
        var text = smartHome.ControlEnabled
            ? $"Connected to {info.LocationName}."
            : $"Connected to {info.LocationName}. Turn on \"Use Home Assistant when I ask\" to use it.";
        if (homeShareOnConnect && NetworkMap.Hosts(Inputs()).Count > 0 && await ShareHomeAssistantAsync())
            text += " " + ActionText.Text;
        ActionText.Text = text;
    }

    private void DisconnectSmartHome()
    {
        var shared = smartHome.Preferences.FollowShare;
        if (!ConfirmationDialog.Confirm(this, "Disconnect Home Assistant?\n\nMartlet will remove the saved token on this PC and stop using your smart home here. " +
            (shared ? "Your other computers keep the shared connection; use Stop sharing for them. " : "") +
            "Revoke the token in Home Assistant if you no longer need it.", "Martlet - smart home"))
            return;
        smartHome.Disconnect();
        homeManageLoadedFor = null;
        homeSystem = null;
        homeDiscoveries = null;
        homeUpdates = null;
        homeBackups = null;
        ActionText.Text = "Home Assistant disconnected.";
        RenderTab();
    }

    // ---------- setting up a new Home Assistant ----------

    private Border SetupCard(Uri target, bool hasHosts)
    {
        TextBox Field(string id, string text = "")
        {
            var box = new TextBox { MaxLength = 64, Width = 320, HorizontalAlignment = HorizontalAlignment.Left, Text = text };
            AutomationProperties.SetAutomationId(box, id);
            return box;
        }
        PasswordBox Secret(string id, string name)
        {
            var box = new PasswordBox { MaxLength = 256, Width = 320, HorizontalAlignment = HorizontalAlignment.Left };
            AutomationProperties.SetAutomationId(box, id);
            AutomationProperties.SetName(box, name);
            return box;
        }
        var person = Environment.UserName;
        var name = Field("SmartHomeOwnerName", person.Length > 0 ? char.ToUpperInvariant(person[0]) + person[1..] : "");
        var username = Field("SmartHomeOwnerUser", new string(person.ToLowerInvariant().Where(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-').ToArray()));
        var password = Secret("SmartHomeOwnerPassword", "Owner password");
        var confirm = Secret("SmartHomeOwnerConfirm", "Repeat the owner password");
        var control = new CheckBox
        {
            IsChecked = true, Margin = new Thickness(0, 10, 0, 0),
            Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "Use Home Assistant when I ask" }
        };
        AutomationProperties.SetAutomationId(control, "SmartHomeSetupControl");
        var share = new CheckBox
        {
            IsChecked = hasHosts, IsEnabled = hasHosts, Margin = new Thickness(0, 6, 0, 0),
            Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "Share it with my other computers through my Martlet hosts" }
        };
        AutomationProperties.SetAutomationId(share, "SmartHomeSetupShare");
        Button? setUp = null;
        setUp = PageButton("Set up Home Assistant", () => SetUpHomeAssistantAsync(target, name.Text, username.Text, password, confirm,
            control.IsChecked == true, share.IsChecked == true, setUp!).Forget(), primary: true, id: "SmartHomeSetUp");
        var cancel = PageButton("Not now", () => { homeSetupTarget = null; RenderTab(); }, id: "SmartHomeSetupCancel");
        var children = new List<UIElement>
        {
            Heading("Set up this Home Assistant"),
            Status($"Nobody has set up the Home Assistant at {HomeAssistantEndpoint.Display(target)} yet. Martlet can do it now.", "SmartHomeSetupTarget"),
            new Label { Content = "Your _name", Target = name, Padding = new Thickness(0, 10, 0, 4) }, name,
            new Label { Content = "_Username", Target = username, Padding = new Thickness(0, 8, 0, 4) }, username,
            new Label { Content = "_Password", Target = password, Padding = new Thickness(0, 8, 0, 4) }, password,
            new Label { Content = "_Repeat password", Target = confirm, Padding = new Thickness(0, 8, 0, 4) }, confirm,
            control, share, Row(setUp, cancel),
            Note("Martlet creates the owner account (an administrator) with this name and password, gives Home Assistant this PC's time zone, " +
                "country, currency, units and language, leaves usage analytics off, and connects with its own access token. You sign in to " +
                "Home Assistant with this username and password; Martlet doesn't keep the password. Set your home's location in Home Assistant " +
                "afterwards (Settings > System > General).", new Thickness(0, 6, 0, 0))
        };
        if (homeSetupStatus is { } status) children.Insert(2, Status(status, "SmartHomeSetupStatus"));
        return Card([.. children]);
    }

    private async Task SetUpHomeAssistantAsync(Uri target, string name, string username, PasswordBox password, PasswordBox confirm,
        bool control, bool share, Button button)
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
            var text = $"Home Assistant is set up and connected ({info.LocationName}, version {info.Version}). Sign in to it as {username.Trim()}.";
            if (share && NetworkMap.Hosts(Inputs()).Count > 0 && await ShareHomeAssistantAsync()) text += " " + ActionText.Text;
            ActionText.Text = text;
        }
        catch (HomeAssistantException error) { homeSetupStatus = error.Message; ActionText.Text = error.Message; }
        catch (ContractException error) { homeSetupStatus = error.Message; ActionText.Text = error.Message; }
        catch (ArgumentException error) { homeSetupStatus = error.Message; }
        catch (OperationCanceledException) { }
        if (!closing) RenderTab();
    }

    // ---------- Martlet hosts ----------

    private Border HostsCard(IReadOnlyList<PairedHost> hosts, HomePreferences saved, bool connected)
    {
        var hardware = HardwareStore?.Load() ?? [];
        var children = new List<UIElement>
        {
            Heading("Home Assistant on your Martlet hosts"),
            Note("Martlet can install Home Assistant on a Linux computer that is one of your Martlet hosts (with Docker Engine), set it up and keep it shared with your other computers.",
                new Thickness(0, 0, 0, 6))
        };
        foreach (var host in hosts)
        {
            var report = hardware.FirstOrDefault(h => h.HostId == host.HostId);
            var address = HomeAssistantHosts.Address(host);
            var display = HomeAssistantEndpoint.Display(address);
            var usesIt = connected && string.Equals(saved.Address, display, StringComparison.OrdinalIgnoreCase);
            string text;
            Button? action = null;
            if (HomeAssistantHosts.Runs(report))
            {
                text = $"{host.HostId}: runs Home Assistant at {display}" + (usesIt ? ", and this PC uses it." : ".");
                if (!usesIt)
                    action = PageButton("Set up or sign in", () => CheckHomeAddressAsync(display, signInIfReady: true).Forget(), link: true,
                        id: $"SmartHomeHostUse-{host.HostId}");
            }
            else if (HomeAssistantHosts.CannotInstall(host, report) is { } why) text = $"{host.HostId}: {why}";
            else
            {
                text = $"{host.HostId}: can run Home Assistant.";
                action = PageButton("Install Home Assistant", () => InstallHomeAssistantAsync(host).Forget(), link: true,
                    id: $"SmartHomeInstall-{host.HostId}");
            }
            children.Add(Status(text, $"SmartHomeHost-{host.HostId}"));
            if (HomeAssistantHosts.Extras(report) is { } extras) children.Add(Note(extras, new Thickness(12, 2, 0, 0)));
            if (action is not null) children.Add(action);
        }
        children.Add(Note("A host's facts come from its last check. Use Check connection on the Devices map after changing it.", new Thickness(0, 8, 0, 0)));
        return Card([.. children]);
    }

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

    private Border ShareCard(HomePreferences saved)
    {
        var children = new List<UIElement> { Heading("Your other computers") };
        string state;
        Button? action;
        if (saved.FollowShare)
        {
            state = saved.SharedBy.Length > 0
                ? $"This PC uses the connection your other computers share (taken from {saved.SharedBy})."
                : "This PC shares its connection with your other computers.";
            action = PageButton("Stop sharing", () => StopSharingHomeAssistantAsync().Forget(), id: "SmartHomeStopShare");
        }
        else if (homeShared is { Address: not null } other && !string.Equals(other.Address.TrimEnd('/'), saved.Address, StringComparison.OrdinalIgnoreCase))
        {
            state = $"Your other computers share a different Home Assistant ({other.Address}). This PC keeps its own connection.";
            action = PageButton("Use the shared one", () => UseSharedHomeAssistantAsync().Forget(), id: "SmartHomeUseShared");
        }
        else
        {
            state = "Only this PC uses this connection.";
            action = PageButton("Share with my other computers", () => ShareHomeAssistantAsync().Forget(), primary: true, id: "SmartHomeShare");
        }
        children.Add(Status(state, "SmartHomeShareState"));
        children.Add(Status(homeShareStatus, "SmartHomeShareStatus"));
        children.Add(Row(action, PageButton("Check now", () => SyncHomeShareAsync().Forget(), link: true, id: "SmartHomeShareCheck")));
        children.Add(Note("Sharing gives your paired Martlet hosts this Home Assistant's address and Martlet's access token. They keep it privately " +
            "and hand it only to your paired computers, which then connect by themselves.", new Thickness(0, 6, 0, 0)));
        return Card([.. children]);
    }

    // ---------- what Martlet may do ----------

    private UIElement PermissionsCard(HomePreferences saved, bool connected)
    {
        var control = new CheckBox
        {
            IsChecked = saved.Control && connected, IsEnabled = connected, Margin = new Thickness(0, 4, 0, 6),
            Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "Use Home Assistant when I ask" }
        };
        AutomationProperties.SetAutomationId(control, "SmartHomeControl");
        var sensitive = new CheckBox
        {
            IsChecked = saved.AllowSensitive && saved.Control && connected, IsEnabled = connected && saved.Control,
            Margin = new Thickness(24, 0, 0, 6),
            Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "Allow locks, doors and alarms" }
        };
        AutomationProperties.SetAutomationId(sensitive, "SmartHomeSensitive");
        var modelTools = new CheckBox
        {
            IsChecked = saved.ModelTools && saved.Control && connected, IsEnabled = connected && saved.Control,
            Margin = new Thickness(0, 4, 0, 6),
            Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "Allow flexible smart-home requests" }
        };
        AutomationProperties.SetAutomationId(modelTools, "SmartHomeModelTools");
        control.Checked += (_, _) => SaveSmartHomeControl(true, false, saved.ModelTools);
        control.Unchecked += (_, _) => SaveSmartHomeControl(false, false, false);
        sensitive.Checked += (_, _) => SaveSmartHomeControl(true, true, saved.ModelTools);
        sensitive.Unchecked += (_, _) => SaveSmartHomeControl(true, false, saved.ModelTools);
        modelTools.Checked += (_, _) => SaveSmartHomeControl(true, saved.AllowSensitive, true);
        modelTools.Unchecked += (_, _) => SaveSmartHomeControl(true, saved.AllowSensitive, false);

        var panel = new StackPanel();
        panel.Children.Add(Card(Heading("What Martlet may do"),
            control,
            sensitive,
            Note("• Status questions always work.\n" +
                "• Lights, switches, climate, media, scenes and blinds run right away.\n" +
                "• Locks, doors, garage doors, gates, alarms and valves need the setting above and a Yes each time.",
                new Thickness(0, 6, 0, 0)),
            Note("Martlet can reach only devices exposed to voice assistants in Home Assistant. Keep sensitive devices hidden unless you need them.",
                new Thickness(0, 8, 0, 0)),
            Note("Screen and camera looks never control your home. Use Voice ID if you want Martlet to answer only you.",
                new Thickness(0, 8, 0, 0))));
        var tools = Status(SmartHomeToolsStatus(saved), "SmartHomeToolsStatus");
        tools.Margin = new Thickness(0, 4, 0, 0);
        panel.Children.Add(Card(Heading("Flexible requests"),
            modelTools,
            tools,
            Note("With this on, Martlet sends exposed device names, areas and states to the Thinking model when needed. Risky actions still ask first.",
                new Thickness(0, 8, 0, 0)),
            Note("Requires Home Assistant's Model Context Protocol Server integration and a Thinking model that can use tools. If unavailable, Martlet uses standard commands.",
                new Thickness(0, 8, 0, 0))));
        return panel;
    }

    private void SaveSmartHomeControl(bool control, bool allowSensitive, bool modelTools)
    {
        var current = smartHome.Preferences;
        if (current.Control == control && current.AllowSensitive == allowSensitive && current.ModelTools == modelTools) return;
        var saved = smartHome.SetControl(control, allowSensitive, modelTools);
        if (saved && modelTools && control) mcpTools.EnsureStarted(retry: true);
        ActionText.Text = !saved ? "Couldn't save the smart home setting. Check access to your data directory."
            : !control ? "Home Assistant is off."
            : modelTools != current.ModelTools
                ? modelTools
                    ? "Flexible smart-home requests are on. Reload an open talk window to use them."
                    : "Flexible smart-home requests are off."
            : allowSensitive ? "Locks, doors and alarms are allowed after you click Yes each time."
            : "Martlet will use Home Assistant when you ask.";
        RenderTab();
    }

    /// <summary>Whether Home Assistant's MCP server is running among the conversation's tools, in words.</summary>
    private string SmartHomeToolsStatus(HomePreferences saved)
    {
        if (!smartHome.ModelToolsEnabled) return saved.Control ? "Off. Martlet uses standard commands." : "Off.";
        var status = mcpTools.Hub.Status.FirstOrDefault(s => s.Name == SmartHome.ServerName);
        if (mcpTools.ManagedConflicts.Any(s => s.Name == SmartHome.ServerName))
            return "Using your existing Home Assistant tools setup.";
        return status?.State switch
        {
            McpServerState.Ready => $"Ready. {status.Tools.Count} tools available.",
            McpServerState.Starting => "Connecting to Home Assistant's tools...",
            McpServerState.Failed => $"Couldn't connect to Home Assistant's tools ({status.Error}). Martlet will use standard commands.",
            _ => "On. Connects when you open a talk window."
        };
    }

    // ---------- devices Home Assistant found ----------

    private Border DevicesCard(HomePreferences saved, IReadOnlyList<PairedHost> hosts)
    {
        var children = new List<UIElement>
        {
            Heading("Devices Home Assistant found"),
            Note("Home Assistant looks for devices on its own network (Hue, Sonos, Chromecast, Shelly, ESPHome, HomeKit, Matter and more). " +
                "Add the ones you want here. New lights, switches, climate, media and blinds become available to Martlet by themselves; locks stay hidden.",
                new Thickness(0, 0, 0, 6)),
            Status(homeDevicesStatus ?? (homeManageBusy ? "Checking..." : "Not checked yet."), "SmartHomeDevicesStatus")
        };
        var index = 0;
        foreach (var found in homeDiscoveries ?? [])
        {
            var n = index++;
            var line = Status(found.Title.Length > 0 ? $"{found.Name}: {found.Title}" : found.Name, $"SmartHomeDevice-{n}");
            line.Margin = new Thickness(0, 8, 0, 0);
            children.Add(line);
            children.Add(Row(PageButton("Add", () => AddDiscoveredAsync(found).Forget(), primary: true, id: $"SmartHomeDeviceAdd-{n}"),
                PageButton("Ignore", () => IgnoreDiscoveredAsync(found).Forget(), id: $"SmartHomeDeviceIgnore-{n}")));
        }
        // An MQTT broker on the host that runs this Home Assistant (Zigbee2MQTT, Frigate, Shelly and Tasmota report through it).
        var hardware = HardwareStore?.Load() ?? [];
        var mqttHost = hosts.FirstOrDefault(h => string.Equals(HomeAssistantEndpoint.Display(HomeAssistantHosts.Address(h)), saved.Address,
            StringComparison.OrdinalIgnoreCase) && HomeAssistantHosts.Has(hardware.FirstOrDefault(r => r.HostId == h.HostId), "mqtt-broker"));
        if (mqttHost is not null && homeSystem is { } system && !system.Integrations.Any(i => i.Contains("MQTT", StringComparison.OrdinalIgnoreCase)))
        {
            var line = Status($"An MQTT broker runs on {mqttHost.HostId} too.", "SmartHomeMqtt");
            line.Margin = new Thickness(0, 8, 0, 0);
            children.Add(line);
            children.Add(Row(PageButton("Add MQTT to Home Assistant", () => AddMqttAsync(mqttHost).Forget(), id: "SmartHomeAddMqtt")));
        }
        children.Add(Row(PageButton("Check for devices", () => RefreshHomeManagementAsync().Forget(), id: "SmartHomeDevicesRefresh")));
        return Card([.. children]);
    }

    private async Task AddDiscoveredAsync(HomeDiscovery found)
    {
        try
        {
            ActionText.Text = $"Adding {found.Name}...";
            var step = await smartHome.AddDiscoveredAsync(found, lifetime.Token);
            ActionText.Text = FlowText(found.Name, step);
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
            ActionText.Text = $"Home Assistant won't offer {found.Name} again. You can still add it from its Integrations page.";
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
            ActionText.Text = FlowText("MQTT", step);
            if (step.Kind is HomeStepKind.Form or HomeStepKind.External or HomeStepKind.Menu) OpenHomePage("config/integrations/dashboard");
        }
        catch (HomeAssistantException error) { ActionText.Text = error.Message; }
        catch (OperationCanceledException) { }
        await RefreshHomeManagementAsync();
    }

    private static string FlowText(string name, HomeFlowStep step) => step.Kind switch
    {
        HomeStepKind.Done => $"Added {name} to Home Assistant.",
        HomeStepKind.Aborted => step.Reason is "already_configured" or "already_in_progress"
            ? $"{name} is already set up in Home Assistant." : $"Home Assistant couldn't add {name} ({step.Reason ?? "stopped"}).",
        HomeStepKind.Progress => $"Home Assistant is still adding {name}. Check again in a moment.",
        _ when step.Errors.Count > 0 => $"Home Assistant couldn't add {name} ({string.Join(", ", step.Errors)}). Finish it on its Integrations page, which is opening.",
        _ => $"{name} needs a few more details (such as a code, key or choice). Finish it on Home Assistant's Integrations page, which is opening."
    };

    // ---------- managing Home Assistant ----------

    private Border ManageCard(HomePreferences saved, IReadOnlyList<PairedHost> hosts)
    {
        var onHost = hosts.FirstOrDefault(h => string.Equals(HomeAssistantEndpoint.Display(HomeAssistantHosts.Address(h)), saved.Address,
            StringComparison.OrdinalIgnoreCase));
        var system = homeSystem;
        var summary = system is null
            ? homeManageStatus ?? (homeManageBusy ? "Reading Home Assistant..." : "Not read yet.")
            : $"{system.LocationName}: Home Assistant {system.Version} ({system.Installation}" + (onHost is null ? "" : $" on {onHost.HostId}") + "). " +
                (system.Integrations.Count == 0 ? "No integrations yet. " : $"{system.Integrations.Count} integrations: {string.Join(", ", system.Integrations.Take(8))}" +
                    (system.Integrations.Count > 8 ? ", ..." : "") + ". ") +
                (homeBackups is { } backups ? backups.Latest is { } latest ? $"Last backup {latest.ToLocalTime():g} ({backups.Count} kept)." : "No backups yet." : "");
        var children = new List<UIElement> { Heading("Manage Home Assistant"), Status(summary, "SmartHomeManageStatus") };
        if (system is not null && homeManageStatus is { } problem) children.Add(Status(problem, "SmartHomeManageProblem"));
        if (homeUpdates is { Count: > 0 } updates)
        {
            var index = 0;
            foreach (var update in updates)
            {
                var n = index++;
                var line = Status($"Update: {update.Title} {update.Installed ?? "?"} → {update.Latest ?? "newer"}" + (update.Installing ? " (installing)" : ""),
                    $"SmartHomeUpdate-{n}");
                line.Margin = new Thickness(0, 8, 0, 0);
                children.Add(line);
                if (!update.Installing)
                    children.Add(Row(PageButton("Install update", () => InstallHomeUpdateAsync(update).Forget(), id: $"SmartHomeUpdateInstall-{n}")));
            }
        }
        else if (homeUpdates is not null) children.Add(Note("No updates waiting.", new Thickness(0, 6, 0, 0)));
        if (system is { Supervised: false } && onHost is not null)
            children.Add(Note($"Home Assistant itself is updated with Martlet: Update host on {onHost.HostId}'s Devices card brings the version this Martlet release uses.",
                new Thickness(0, 6, 0, 0)));
        else if (system is { Supervised: false })
            children.Add(Note("This Home Assistant runs as a container you manage; update it where it runs.", new Thickness(0, 6, 0, 0)));
        children.Add(Row(
            PageButton("Back up now", () => BackupHomeAssistantAsync().Forget(), id: "SmartHomeBackup"),
            PageButton("Restart", () => RestartHomeAssistantAsync().Forget(), id: "SmartHomeRestart"),
            PageButton("Open Home Assistant", () => OpenHomePage(""), id: "SmartHomeOpen"),
            PageButton(homeManageBusy ? "Reading..." : "Refresh", () => RefreshHomeManagementAsync().Forget(), link: true, id: "SmartHomeManageRefresh")));
        children.Add(Note("These need an administrator's sign-in (Martlet's own setup and Sign in with Home Assistant as the owner give one). " +
            "Only your clicks here run them; the Thinking model never gets them.", new Thickness(0, 6, 0, 0)));
        return Card([.. children]);
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
        if (!ConfirmationDialog.Confirm(this, $"Install {update.Title} {update.Latest}? Home Assistant may restart to finish it.", "Martlet - smart home"))
            return;
        try
        {
            await smartHome.InstallUpdateAsync(update, lifetime.Token);
            ActionText.Text = $"Home Assistant is installing {update.Title}. Refresh in a few minutes.";
        }
        catch (HomeAssistantException error) { ActionText.Text = error.Message; }
        catch (OperationCanceledException) { }
        await RefreshHomeManagementAsync();
    }

    private async Task RestartHomeAssistantAsync()
    {
        if (!ConfirmationDialog.Confirm(this, "Restart Home Assistant? Your smart home doesn't respond for a minute or so.", "Martlet - smart home"))
            return;
        try
        {
            await smartHome.RestartAsync(lifetime.Token);
            ActionText.Text = "Home Assistant is restarting. It's back in a minute or so.";
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
            ActionText.Text = "Home Assistant started a backup on its own disk. Refresh in a minute to see it.";
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
            ActionText.Text = $"Couldn't open the browser: {error.Message}. Open {address.AbsoluteUri} yourself.";
        }
    }

    /// <summary>The Smart home page may be rebuilt: it is open and nobody is typing in one of its fields.</summary>
    private bool SmartHomeCanRender() => !closing && openTab == CompanionTab.SmartHome &&
        System.Windows.Input.Keyboard.FocusedElement is not (System.Windows.Controls.Primitives.TextBoxBase or PasswordBox);

    private static TextBlock Status(string text, string id, double size = 14)
    {
        var block = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(block, id);
        return block;
    }
}
