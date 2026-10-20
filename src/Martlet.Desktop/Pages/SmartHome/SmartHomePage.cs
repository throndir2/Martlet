using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Core.Settings;
using Martlet.Home;
using Martlet.Presentation;

namespace Martlet.Desktop;

/// <summary>Companion › Smart home, the UI only, in the standard page order: Now; the main choice (Home Assistant, or Off: Martlet
/// doesn't control your smart home); connecting to Home Assistant, setting one up and installing it on a Martlet host
/// (SmartHomePage.Setup.cs); then what Martlet may do, the devices, managing Home Assistant (SmartHomePage.Control.cs), sharing it
/// with your other computers and the recent actions. What each line says is in <see cref="SmartHomePageModel"/>; what the page
/// needs from the app is <see cref="ISmartHomePageServices"/>.</summary>
internal sealed partial class SmartHomePage(ISmartHomePageServices services)
{
    /// <summary>Draws the page's cards into <paramref name="page"/>.</summary>
    internal void Render(Panel page)
    {
        var model = new SmartHomePageModel(services);
        var hasHosts = model.Hosts.Count > 0;

        page.Children.Add(PageNowCard(model.NowText, null, "SmartHomeNow"));
        page.Children.Add(ChoiceCard(model));
        page.Children.Add(ConnectionCard(model, hasHosts));
        if (services.SetupTarget is { } target) page.Children.Add(SetupCard(target));
        if (hasHosts) page.Children.Add(HostsCard(model));

        page.Children.Add(PermissionsCard(model));
        if (model.Connected)
        {
            page.Children.Add(DevicesCard(model));
            page.Children.Add(ManageCard(model));
            if (model.NeedsManagement) services.RefreshManagement();
        }
        if (hasHosts) page.Children.Add(ShareCard(model));

        var recent = services.RecentActions;
        var log = new List<UIElement> { Heading("Recent actions") };
        if (recent.Count == 0)
            log.Add(HelpTip.Explain("No smart-home actions yet. This list clears when you close Martlet.",
                new Thickness(0, 0, 0, 4), "SmartHomeRecent", "recent actions", wiki: "Smart-Home#recent-actions"));
        else
            foreach (var action in recent)
                log.Add(Note($"{action.At:t}  {action.Summary}", new Thickness(0, 0, 0, 4)));
        page.Children.Add(Card([.. log]));
    }

    // ---------- the main choice ----------

    /// <summary>Smart home's main choice in an option picker: Home Assistant (its button turns on "Use Home Assistant when I ask"
    /// once it is connected) or Off (Martlet doesn't control your smart home). More smart-home systems join this list.</summary>
    private Border ChoiceCard(SmartHomePageModel model)
    {
        var saved = model.Saved;
        var connected = model.Connected;
        var inUse = model.InUse;
        var homeAssistant = new PickerOption("HomeAssistant", "Home Assistant",
            "Martlet answers questions about your home and controls the devices you expose to voice assistants in Home Assistant, when you ask.")
        {
            Badge = inUse ? "in use" : connected ? "connected" : null,
            InUse = inUse,
            Facts =
            [
                new("runs-on", "Runs on", "Your Home Assistant: on your home network, or installed by Martlet on a Linux Martlet host with Docker",
                    "Your Home Assistant"),
                new("this-pc", "On this PC", "Nothing extra: Martlet only sends Home Assistant short requests", "Nothing extra on this PC"),
                new("needs", "Needs", "A Home Assistant you sign in to (or a long-lived access token)", "A sign-in"),
                new("sends", "Sends", "Your requests only to your Home Assistant; with flexible requests on, also the exposed devices' names, " +
                    "areas and states to your Thinking model"),
                new("cost", "Cost", "Free", "Free")
            ],
            State = connected ? $"Connected to {model.Where}" + (inUse ? "; Martlet uses it when you ask." : ".")
                : "Not connected yet: connect it under Home Assistant below, then choose it here.",
            Warn = !connected,
            Action = () => !inUse && connected
                ? PageButton("Use Home Assistant when I ask", () => services.SaveControl(true, false, saved.ModelTools), primary: true, id: "SmartHomeControl")
                : null
        };
        var off = PickerOption.Off("Martlet doesn't control your smart home", !inUse,
            () => inUse ? PageButton("Turn smart home off", () => services.SaveControl(false, false, false), id: "SmartHomeControlOff") : null);
        return services.OptionPicker("SmartHome", "Smart home", "Choose what Martlet controls your home with: status questions and actions work only " +
            "with the system chosen here.", [homeAssistant, off]);
    }

    // ---------- connecting ----------

    private Border ConnectionCard(SmartHomePageModel model, bool hasHosts)
    {
        var saved = model.Saved;
        var connected = model.Connected;
        var address = new TextBox
        {
            MaxLength = 512, Width = 420, HorizontalAlignment = HorizontalAlignment.Left,
            Text = services.AddressDraft ?? (saved.Address.Length > 0 ? saved.Address : HomeAssistantEndpoint.Example)
        };
        AutomationProperties.SetAutomationId(address, "SmartHomeAddress");
        InputHint.SetText(address, HomeAssistantEndpoint.Example);
        address.TextChanged += (_, _) => services.AddressDraft = address.Text;
        var token = new PasswordBox { MaxLength = SecretLease.MaximumLength, Width = 420, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(token, "Home Assistant long-lived access token");
        AutomationProperties.SetAutomationId(token, "SmartHomeToken");
        // Once connected, the "Token saved" mark fills the empty box instead.
        if (!connected) InputHint.SetText(token, "Paste the token from your Home Assistant profile");
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

        var find = PageButton(services.Finding ? "Looking..." : "Find on my network", services.Find, id: "SmartHomeFind");
        find.IsEnabled = !services.Finding;
        var signIn = PageButton(connected ? "Sign in again" : "Sign in with Home Assistant", () => services.SignIn(address.Text),
            primary: !connected, id: "SmartHomeSignIn");
        var check = PageButton("Set up a new one", () => services.CheckAddress(address.Text), id: "SmartHomeCheck");
        Button? connect = null;
        connect = PageButton(connected ? "Reconnect" : "Connect with token", () => services.Connect(address.Text, token, connect!),
            id: "SmartHomeConnect");
        var disconnect = connected ? PageButton("Disconnect", services.Disconnect, id: "SmartHomeDisconnect") : null;

        var children = new List<UIElement>
        {
            Heading("Home Assistant"),
            HelpTip.Explain("Connect Home Assistant so Martlet can answer questions and control devices when you ask. Martlet only uses the address below." +
                (hasHosts && services.ClusterEnabled ? " It is one connection for all your computers: connecting here connects them too." : ""),
                new Thickness(0, 0, 0, 8), "SmartHome", "Home Assistant", wiki: "Smart-Home#connect-home-assistant"),
            Status(model.ConnectionText, "SmartHomeStatus", 15),
            new Label { Content = "Home Assistant _address", Target = address, Padding = new Thickness(0, 10, 0, 4) },
            address,
            HelpTip.Explain("Use the address you open Home Assistant with, like http://homeassistant.local:8123 (https outside your home network), or let Martlet find it.",
                new Thickness(0, 4, 0, 0), "SmartHomeAddress", "the Home Assistant address", wiki: "Smart-Home#find-or-type-the-address"),
            Row(find)
        };
        if (services.FindStatus is { } findStatus) children.Add(Status(findStatus, "SmartHomeFindStatus"));
        var index = 0;
        foreach (var found in services.Found ?? [])
        {
            var n = index++;
            var use = PageButton("Use this one", () => services.UseFound(found), link: true, id: $"SmartHomeFoundUse-{n}");
            var line = Status(SmartHomePageModel.FoundText(found), $"SmartHomeFound-{n}");
            line.Margin = new Thickness(0, 6, 0, 0);
            children.Add(line);
            children.Add(use);
        }
        children.Add(Row(signIn, check, disconnect));
        children.Add(HelpTip.Explain("Sign in opens Home Assistant's own sign-in page in your browser; Martlet then keeps its own access token. " +
            "Set up a new one creates the owner account of a Home Assistant nobody has set up yet.", new Thickness(0, 2, 0, 0), "SmartHomeSignIn",
            "signing in", wiki: "Smart-Home#sign-in-or-set-up"));
        children.Add(new Label { Content = "Or paste a long-lived access _token", Target = token, Padding = new Thickness(0, 12, 0, 4) });
        children.Add(tokenField);
        children.Add(HelpTip.Explain("Create one in your Home Assistant profile under Security; Martlet keeps it in Windows Credential Manager and sends it only to this address.",
            new Thickness(0, 4, 0, 0), "SmartHomeToken", "the access token", wiki: "Smart-Home#access-token"));
        children.Add(Row(connect));
        return Card([.. children]);
    }
}
