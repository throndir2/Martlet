using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Presentation;

namespace Martlet.Desktop;

/// <summary>Companion › Smart home's cards for a connected Home Assistant: what Martlet may do (with the lock, door, garage, gate,
/// alarm and valve safety tier) and flexible requests, the devices Home Assistant found, and managing Home Assistant itself
/// (updates, backups, restart).</summary>
internal sealed partial class SmartHomePage
{
    // ---------- what Martlet may do ----------

    private UIElement PermissionsCard(SmartHomePageModel model)
    {
        var saved = model.Saved;
        var connected = model.Connected;
        var sensitive = new CheckBox
        {
            IsChecked = saved.AllowSensitive && saved.Control && connected, IsEnabled = connected && saved.Control,
            Margin = new Thickness(0, 4, 0, 6),
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
        sensitive.Checked += (_, _) => services.SaveControl(true, true, saved.ModelTools);
        sensitive.Unchecked += (_, _) => services.SaveControl(true, false, saved.ModelTools);
        modelTools.Checked += (_, _) => services.SaveControl(true, saved.AllowSensitive, true);
        modelTools.Unchecked += (_, _) => services.SaveControl(true, saved.AllowSensitive, false);

        var panel = new StackPanel();
        panel.Children.Add(Card(Heading("What Martlet may do"),
            sensitive,
            Note("• Status questions always work.\n" +
                "• Lights, switches, climate, media, scenes and blinds run right away.\n" +
                "• Locks, doors, garage doors, gates, alarms and valves need the setting above and a Yes each time.",
                new Thickness(0, 6, 0, 0)),
            HelpTip.Explain("Martlet can reach only devices exposed to voice assistants in Home Assistant. Keep sensitive devices hidden unless you need them.",
                new Thickness(0, 8, 0, 0), "SmartHomeExposed", "exposed devices", wiki: "Smart-Home#what-martlet-may-do"),
            HelpTip.Explain("Screen and camera looks never control your home. Use Voice ID if you want Martlet to answer only you.",
                new Thickness(0, 8, 0, 0), "SmartHomeWhoAsks", "who can control your home", wiki: "Smart-Home#what-martlet-may-do")));
        var tools = Status(model.ToolsText, "SmartHomeToolsStatus");
        tools.Margin = new Thickness(0, 4, 0, 0);
        panel.Children.Add(Card(Heading("Flexible requests"),
            modelTools,
            tools,
            HelpTip.Explain("With this on, Martlet sends exposed device names, areas and states to the Thinking model when needed. Risky actions still ask first.",
                new Thickness(0, 8, 0, 0), "SmartHomeFlexibleData", "what flexible requests send", wiki: "Smart-Home#flexible-requests"),
            HelpTip.Explain("Requires Home Assistant's Model Context Protocol Server integration and a Thinking model that can use tools. If unavailable, Martlet uses standard commands.",
                new Thickness(0, 8, 0, 0), "SmartHomeFlexibleNeeds", "what flexible requests need", wiki: "Smart-Home#flexible-requests")));
        return panel;
    }

    // ---------- devices Home Assistant found ----------

    private Border DevicesCard(SmartHomePageModel model)
    {
        var children = new List<UIElement>
        {
            Heading("Devices Home Assistant found"),
            HelpTip.Explain("Home Assistant looks for devices on its own network (Hue, Sonos, Chromecast, Shelly, ESPHome, HomeKit, Matter and more). " +
                "Add the ones you want here. New lights, switches, climate, media and blinds become available to Martlet by themselves; locks stay hidden.",
                new Thickness(0, 0, 0, 6), "SmartHomeDevices", "devices Home Assistant found", wiki: "Smart-Home#add-devices"),
            Status(model.DevicesText, "SmartHomeDevicesStatus")
        };
        var index = 0;
        foreach (var found in services.Discoveries ?? [])
        {
            var n = index++;
            var line = Status(found.Title.Length > 0 ? $"{found.Name}: {found.Title}" : found.Name, $"SmartHomeDevice-{n}");
            line.Margin = new Thickness(0, 8, 0, 0);
            children.Add(line);
            children.Add(Row(PageButton("Add", () => services.AddDiscovered(found), primary: true, id: $"SmartHomeDeviceAdd-{n}"),
                PageButton("Ignore", () => services.IgnoreDiscovered(found), id: $"SmartHomeDeviceIgnore-{n}")));
        }
        // An MQTT broker on the host that runs this Home Assistant (Zigbee2MQTT, Frigate, Shelly and Tasmota report through it).
        if (model.MqttHost is { } mqttHost)
        {
            var line = Status($"An MQTT broker runs on {mqttHost.HostId} too.", "SmartHomeMqtt");
            line.Margin = new Thickness(0, 8, 0, 0);
            children.Add(line);
            children.Add(Row(PageButton("Add MQTT to Home Assistant", () => services.AddMqtt(mqttHost), id: "SmartHomeAddMqtt")));
        }
        children.Add(Row(PageButton("Check for devices", services.RefreshManagement, id: "SmartHomeDevicesRefresh")));
        return Card([.. children]);
    }

    // ---------- managing Home Assistant ----------

    private Border ManageCard(SmartHomePageModel model)
    {
        var children = new List<UIElement> { Heading("Manage Home Assistant"), Status(model.ManageText, "SmartHomeManageStatus") };
        if (services.System is not null && services.ManageStatus is { } problem) children.Add(Status(problem, "SmartHomeManageProblem"));
        if (services.Updates is { Count: > 0 } updates)
        {
            var index = 0;
            foreach (var update in updates)
            {
                var n = index++;
                var line = Status(SmartHomePageModel.UpdateText(update), $"SmartHomeUpdate-{n}");
                line.Margin = new Thickness(0, 8, 0, 0);
                children.Add(line);
                if (!update.Installing)
                    children.Add(Row(PageButton("Install update", () => services.InstallUpdate(update), id: $"SmartHomeUpdateInstall-{n}")));
            }
        }
        else if (services.Updates is not null) children.Add(Note("No updates waiting.", new Thickness(0, 6, 0, 0)));
        if (model.UpdatedWithText is { } updatedWith) children.Add(Note(updatedWith, new Thickness(0, 6, 0, 0)));
        children.Add(Row(
            PageButton("Back up now", services.Backup, id: "SmartHomeBackup"),
            PageButton("Restart", services.Restart, id: "SmartHomeRestart"),
            PageButton("Open Home Assistant", () => services.OpenHomePage(""), id: "SmartHomeOpen"),
            PageButton(services.ManageBusy ? "Reading..." : "Refresh", services.RefreshManagement, link: true, id: "SmartHomeManageRefresh")));
        children.Add(HelpTip.Explain("Only your clicks here run these, and the Thinking model never gets them. They need an administrator's sign-in " +
            "(Martlet's own setup, or Sign in with Home Assistant as the owner, gives one).", new Thickness(0, 6, 0, 0), "SmartHomeManage",
            "managing Home Assistant", wiki: "Smart-Home#manage-home-assistant"));
        return Card([.. children]);
    }
}
