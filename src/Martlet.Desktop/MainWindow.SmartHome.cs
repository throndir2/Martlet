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
            Note("Connect Home Assistant so Martlet can answer questions and control devices when you ask. Martlet only uses the address below.",
                new Thickness(0, 0, 0, 8)),
            new TextBlock
            {
                Text = connected
                    ? $"Connected to {saved.LocationName} at {saved.Address}."
                    : "Not connected.",
                FontSize = 15, TextWrapping = TextWrapping.Wrap
            },
            new Label { Content = "Home Assistant _address", Target = address, Padding = new Thickness(0, 10, 0, 4) },
            address,
            Note("Use the address you open Home Assistant with, like http://homeassistant.local:8123. Use https outside your home network.",
                new Thickness(0, 4, 0, 0)),
            new Label { Content = "Long-lived access _token", Target = token, Padding = new Thickness(0, 10, 0, 4) },
            tokenField,
            Note("Create one in your Home Assistant profile under Security. Martlet stores it in Windows Credential Manager and sends it only to this address.",
                new Thickness(0, 4, 0, 0)),
            Row(connect, disconnect)));

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
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = "Allow locks, doors and alarms"
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
                Text = "Allow flexible smart-home requests"
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
            Note("• Status questions always work.\n" +
                "• Lights, switches, climate, media, scenes and blinds run right away.\n" +
                "• Locks, doors, garage doors, gates, alarms and valves need the setting above and a Yes each time.",
                new Thickness(0, 6, 0, 0)),
            Note("Martlet can reach only devices exposed to voice assistants in Home Assistant. Keep sensitive devices hidden unless you need them.",
                new Thickness(0, 8, 0, 0)),
            Note("Screen and camera looks never control your home. Use Voice ID if you want Martlet to answer only you.",
                new Thickness(0, 8, 0, 0))));

        page.Children.Add(Card(Heading("Flexible requests"),
            modelTools,
            new TextBlock { Text = SmartHomeToolsStatus(saved), FontSize = 14, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) },
            Note("With this on, Martlet sends exposed device names, areas and states to the Thinking model when needed. Risky actions still ask first.",
                new Thickness(0, 8, 0, 0)),
            Note("Requires Home Assistant's Model Context Protocol Server integration and a Thinking model that can use tools. If unavailable, Martlet uses standard commands.",
                new Thickness(0, 8, 0, 0))));

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
                ? $"Connected to {info.LocationName}."
                : $"Connected to {info.LocationName}. Turn on \"Use Home Assistant when I ask\" to use it.";
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
        if (!ConfirmationDialog.Confirm(this, "Disconnect Home Assistant?\n\nMartlet will remove the saved token and stop using your smart home. " +
            "Revoke the token in Home Assistant if you no longer need it.", "Martlet - smart home"))
            return;
        smartHome.Disconnect();
        ActionText.Text = "Home Assistant disconnected.";
        RenderTab();
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
}
