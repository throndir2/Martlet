using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Home;
using Martlet.Mcp.Client;

namespace Martlet.Desktop;

/// <summary>The Companion page's Smart home tab: the Home Assistant connection, what Martlet may do with it (with the
/// lock/door/garage/gate/alarm/valve safety tier) and the actions it took since Martlet started.</summary>
public partial class MainWindow
{
    private void RenderSmartHomeTab(Panel page)
    {
        var saved = smartHome.Preferences;
        var connected = smartHome.Connected;

        var address = new TextBox
        {
            MaxLength = 512, Width = 420, HorizontalAlignment = HorizontalAlignment.Left,
            Text = saved.Address.Length > 0 ? saved.Address : HomeAssistantEndpoint.Example
        };
        AutomationProperties.SetAutomationId(address, "SmartHomeAddress");
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

        Button? connect = null;
        connect = PageButton(connected ? "Save and check" : "Connect", () => ConnectSmartHomeAsync(address, token, connect!).Forget(),
            primary: true, id: "SmartHomeConnect");
        var disconnect = connected ? PageButton("Disconnect", DisconnectSmartHome, id: "SmartHomeDisconnect") : null;

        page.Children.Add(Card(Heading("Home Assistant"),
            Note("Martlet runs your smart home through Home Assistant, the free, open-source hub that already connects Matter and Thread, " +
                "Zigbee, Z-Wave, Wi-Fi and Bluetooth devices, Philips Hue, IKEA, Shelly, ESPHome and thousands of other brands. " +
                "Martlet only talks to the address you enter here.", new Thickness(0, 0, 0, 8)),
            new TextBlock
            {
                Text = connected
                    ? $"Connected to {saved.LocationName} (Home Assistant {saved.Version}) at {saved.Address}."
                    : "Not connected.",
                FontSize = 15, TextWrapping = TextWrapping.Wrap
            },
            new Label { Content = "Home Assistant _address", Target = address, Padding = new Thickness(0, 10, 0, 4) },
            address,
            Note("The address you open Home Assistant with, usually http://homeassistant.local:8123 or http://<its IP address>:8123. " +
                "Plain http works only on your home network; use https:// for anything else.", new Thickness(0, 4, 0, 0)),
            new Label { Content = "Long-lived access _token", Target = token, Padding = new Thickness(0, 10, 0, 4) },
            tokenField,
            Note("Create one in Home Assistant: open your profile (your name at the bottom left), then Security, then Long-lived access " +
                "tokens > Create token. It is kept in Windows Credential Manager, never in settings, and only sent to this address.",
                new Thickness(0, 4, 0, 0)),
            Row(connect, disconnect)));

        var control = new CheckBox
        {
            IsChecked = saved.Control && connected, IsEnabled = connected, Margin = new Thickness(0, 4, 0, 6),
            Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "Let Martlet control and check my home when I ask" }
        };
        AutomationProperties.SetAutomationId(control, "SmartHomeControl");
        var sensitive = new CheckBox
        {
            IsChecked = saved.AllowSensitive && saved.Control && connected, IsEnabled = connected && saved.Control,
            Margin = new Thickness(24, 0, 0, 6),
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = "Also locks, doors, garage doors, gates, alarms and valves (Martlet asks me to click Yes every time)"
            }
        };
        AutomationProperties.SetAutomationId(sensitive, "SmartHomeSensitive");
        var modelTools = new CheckBox
        {
            IsChecked = saved.ModelTools && saved.Control && connected, IsEnabled = connected && saved.Control,
            Margin = new Thickness(0, 4, 0, 6),
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = "Let the Thinking model use Home Assistant's tools for anything I ask, not just exact commands"
            }
        };
        AutomationProperties.SetAutomationId(modelTools, "SmartHomeModelTools");
        control.Checked += (_, _) => SaveSmartHomeControl(true, false, saved.ModelTools);
        control.Unchecked += (_, _) => SaveSmartHomeControl(false, false, false);
        sensitive.Checked += (_, _) => SaveSmartHomeControl(true, true, saved.ModelTools);
        sensitive.Unchecked += (_, _) => SaveSmartHomeControl(true, false, saved.ModelTools);
        modelTools.Checked += (_, _) => SaveSmartHomeControl(true, saved.AllowSensitive, true);
        modelTools.Unchecked += (_, _) => SaveSmartHomeControl(true, saved.AllowSensitive, false);

        page.Children.Add(Card(Heading("What Martlet may do"),
            control,
            sensitive,
            Note("• Status questions always work: \"is the garage door open?\", \"what's the temperature in the bedroom?\"\n" +
                "• Lights, switches, fans, heating and cooling, media, scenes and blinds happen right away: \"turn off the kitchen lights\", " +
                "\"set the living room to 21 degrees\", \"pause the TV\".\n" +
                "• Locks, doors, garage doors, gates, alarms and valves are never sent unless you allow them above, and then only after you " +
                "click Yes each time. Martlet recognizes these words in English, German, French, Spanish, Italian and Dutch.",
                new Thickness(0, 6, 0, 0)),
            Note("Home Assistant decides which devices Martlet can reach: only the ones exposed to voice assistants (in Home Assistant: " +
                "Settings > Voice assistants > Expose). Home Assistant doesn't expose locks, garage doors or alarms unless you add them; " +
                "leaving them out is the safest choice.", new Thickness(0, 8, 0, 0)),
            Note("How it works: in a talk window, before each reply to what you say or type, Martlet hands your words to Home Assistant's " +
                "built-in Assist. It runs on your Home Assistant (no AI model, no cloud), acts only on commands it recognizes in its own " +
                "language, and Martlet then replies in its own voice. Screen and camera looks never control your home, and nobody else's " +
                "words reach it unless they talk to Martlet (turn on Voice ID to answer only you).", new Thickness(0, 8, 0, 0))));

        page.Children.Add(Card(Heading("Free-form requests"),
            modelTools,
            new TextBlock { Text = SmartHomeToolsStatus(saved), FontSize = 14, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) },
            Note("Assist only understands set phrases. With this on, Martlet's Thinking model can also handle requests like \"make it cozy " +
                "in here\", \"I'm going to bed\" or \"is anything still on downstairs?\" by calling Home Assistant's own tools: it sees your " +
                "exposed devices, their areas and states, and acts on them. Status and ordinary devices run right away; locks, doors, " +
                "garage doors, gates, alarms and valves follow the setting above (blocked, or you click Yes each time), and anything else, " +
                "such as an exposed script, asks you first. Every call is listed here and on the Tools page.", new Thickness(0, 8, 0, 0)),
            Note("Needs: Home Assistant's \"Model Context Protocol Server\" integration (in Home Assistant: Settings > Devices & services > " +
                "Add integration), and a Thinking model that can call tools on OpenAI or an OpenAI-compatible server (such as gpt-4.1-mini, " +
                "qwen3 or llama3.1 in Ollama). Otherwise, or if Home Assistant's tools aren't reachable, Martlet uses Assist as before. " +
                "Your devices' names, areas and states go to the Thinking model whenever it looks.", new Thickness(0, 8, 0, 0))));

        var recent = smartHome.RecentActions;
        var log = new List<UIElement> { Heading("Recent actions") };
        if (recent.Count == 0)
            log.Add(Note("Nothing yet. What Martlet asks Home Assistant to do is listed here until you close Martlet; it is never saved.",
                new Thickness(0, 0, 0, 4)));
        else
            foreach (var action in recent)
                log.Add(Note($"{action.At:t}  {action.Summary}", new Thickness(0, 0, 0, 4)));
        page.Children.Add(Card([.. log]));
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
            var info = await smartHome.ConnectAsync(address.Text, token, lifetime.Token);
            ActionText.Text = smartHome.ControlEnabled
                ? $"Connected to {info.LocationName} (Home Assistant {info.Version})."
                : $"Connected to {info.LocationName} (Home Assistant {info.Version}). Turn on \"Let Martlet control and check my home\" to use it.";
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

    private void DisconnectSmartHome()
    {
        if (!ConfirmationDialog.Confirm(this, "Disconnect Home Assistant? Martlet forgets the access token and stops using your smart home. " +
            "To fully revoke it, also delete the token in Home Assistant (your profile, then Security).", "Martlet - smart home"))
            return;
        smartHome.Disconnect();
        ActionText.Text = "Home Assistant disconnected. The access token was removed from Windows Credential Manager.";
        RenderTab();
    }

    private void SaveSmartHomeControl(bool control, bool allowSensitive, bool modelTools)
    {
        var current = smartHome.Preferences;
        if (current.Control == control && current.AllowSensitive == allowSensitive && current.ModelTools == modelTools) return;
        var saved = smartHome.SetControl(control, allowSensitive, modelTools);
        if (saved && modelTools && control) mcpTools.EnsureStarted(retry: true);
        ActionText.Text = !saved ? "Couldn't save the smart home setting. Check access to your data directory."
            : !control ? "Martlet won't use Home Assistant now."
            : modelTools != current.ModelTools
                ? modelTools
                    ? "The Thinking model can now use Home Assistant's tools when you ask. An open talk window picks it up on Reload."
                    : "The Thinking model no longer gets Home Assistant's tools; Martlet uses Assist for your commands."
            : allowSensitive ? "Martlet may now also operate locks, doors, garage doors, gates, alarms and valves, after you click Yes each time."
            : "Martlet now controls and checks your home through Home Assistant when you ask.";
        RenderTab();
    }

    /// <summary>Whether Home Assistant's MCP server is running among the conversation's tools, in words.</summary>
    private string SmartHomeToolsStatus(HomePreferences saved)
    {
        if (!smartHome.ModelToolsEnabled) return saved.Control ? "Off: Martlet uses Assist." : "Off.";
        var status = mcpTools.Hub.Status.FirstOrDefault(s => s.Name == SmartHome.ServerName);
        if (mcpTools.ManagedConflicts.Any(s => s.Name == SmartHome.ServerName))
            return "Not used: your mcp.json already has a server named \"home-assistant\", and that one is used instead.";
        return status?.State switch
        {
            McpServerState.Ready => $"Ready: Home Assistant offers {status.Tools.Count} tools.",
            McpServerState.Starting => "Connecting to Home Assistant's tools...",
            McpServerState.Failed => $"Home Assistant's tools aren't reachable ({status.Error}). Add the Model Context Protocol Server " +
                "integration in Home Assistant; until then Martlet uses Assist.",
            _ => "On: connects when you open a talk window."
        };
    }
}
