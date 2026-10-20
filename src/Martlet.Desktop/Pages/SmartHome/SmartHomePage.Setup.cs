using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Home;
using Martlet.Presentation;

namespace Martlet.Desktop;

/// <summary>Companion › Smart home's cards for a new Home Assistant: the setup form (its owner account), Home Assistant on your
/// Martlet hosts, and sharing the connection with your other computers.</summary>
internal sealed partial class SmartHomePage
{
    // ---------- setting up a new Home Assistant ----------

    private Border SetupCard(Uri target)
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
        InputHint.SetText(name, "Alex");
        InputHint.SetText(username, "alex");
        InputHint.SetText(password, "Choose a password for Home Assistant");
        InputHint.SetText(confirm, "Type the same password again");
        var control = new CheckBox
        {
            IsChecked = true, Margin = new Thickness(0, 10, 0, 0),
            Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "Use Home Assistant when I ask" }
        };
        AutomationProperties.SetAutomationId(control, "SmartHomeSetupControl");
        Button? setUp = null;
        setUp = PageButton("Set up Home Assistant", () => services.SetUp(target, name.Text, username.Text, password, confirm,
            control.IsChecked == true, setUp!), primary: true, id: "SmartHomeSetUp");
        var cancel = PageButton("Not now", services.CancelSetup, id: "SmartHomeSetupCancel");
        var children = new List<UIElement>
        {
            Heading("Set up this Home Assistant"),
            Status($"Nobody has set up the Home Assistant at {HomeAssistantEndpoint.Display(target)} yet: Martlet can do it now.", "SmartHomeSetupTarget"),
            new Label { Content = "Your _name", Target = name, Padding = new Thickness(0, 10, 0, 4) }, name,
            new Label { Content = "_Username", Target = username, Padding = new Thickness(0, 8, 0, 4) }, username,
            new Label { Content = "_Password", Target = password, Padding = new Thickness(0, 8, 0, 4) }, password,
            new Label { Content = "_Repeat password", Target = confirm, Padding = new Thickness(0, 8, 0, 4) }, confirm,
            control, Row(setUp, cancel),
            HelpTip.Explain("Martlet creates the owner account (an administrator) with this name and password, connects with its own access token " +
                "and doesn't keep the password. It gives Home Assistant this PC's time zone, country, currency, units and language and leaves " +
                "usage analytics off. Set your home's location in Home Assistant afterwards (Settings > System > General).",
                new Thickness(0, 6, 0, 0), "SmartHomeSetup", "setting up Home Assistant", wiki: "Smart-Home#set-up-a-new-home-assistant")
        };
        if (services.SetupStatus is { } status) children.Insert(2, Status(status, "SmartHomeSetupStatus"));
        return Card([.. children]);
    }

    // ---------- Martlet hosts ----------

    private Border HostsCard(SmartHomePageModel model)
    {
        var children = new List<UIElement>
        {
            Heading("Home Assistant on your Martlet hosts"),
            HelpTip.Explain("Martlet can install Home Assistant on a Linux computer that is one of your Martlet hosts (with Docker Engine), set it up and keep it shared with your other computers.",
                new Thickness(0, 0, 0, 6), "SmartHomeHosts", "Home Assistant on your hosts", wiki: "Smart-Home#install-home-assistant-on-one-of-your-computers")
        };
        foreach (var host in model.Hosts)
        {
            var (text, offer, display) = model.HostLine(host);
            var action = offer switch
            {
                HostOffer.Use => PageButton("Set up or sign in", () => services.CheckAddress(display, signInIfReady: true), link: true,
                    id: $"SmartHomeHostUse-{host.HostId}"),
                HostOffer.Install => PageButton("Install Home Assistant", () => services.Install(host), link: true, id: $"SmartHomeInstall-{host.HostId}"),
                _ => null
            };
            children.Add(Status(text, $"SmartHomeHost-{host.HostId}"));
            if (HomeAssistantHosts.Extras(model.Report(host)) is { } extras) children.Add(Note(extras, new Thickness(12, 2, 0, 0)));
            if (action is not null) children.Add(action);
        }
        children.Add(Note("A host's facts come from its last check, so use Check connection on the Devices map after you change it.", new Thickness(0, 8, 0, 0)));
        return Card([.. children]);
    }

    // ---------- sharing ----------

    private Border ShareCard(SmartHomePageModel model)
    {
        var children = new List<UIElement> { Heading("Your other computers") };
        children.Add(Status(model.ShareText, "SmartHomeShareState"));
        children.Add(Status(services.ShareStatus, "SmartHomeShareStatus"));
        var check = PageButton("Check now", services.SyncShare, link: true, id: "SmartHomeShareCheck");
        check.IsEnabled = services.ClusterEnabled;
        children.Add(Row(check));
        children.Add(HelpTip.Explain("Home Assistant is one connection for all your computers: your paired Martlet hosts keep its address and Martlet's " +
            "access token privately and hand them only to your paired computers, which connect by themselves. Disconnecting disconnects " +
            "every computer. What Martlet may do with it (below) is the same everywhere too.", new Thickness(0, 6, 0, 0), "HomeAssistantShared", "sharing Home Assistant",
            wiki: "Smart-Home#on-all-your-computers"));
        return Card([.. children]);
    }
}
