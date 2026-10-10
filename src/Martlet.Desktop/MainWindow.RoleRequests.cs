using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Core.Contracts;
using Martlet.Core.Installation;
using Martlet.Core.Sync;

namespace Martlet.Desktop;

/// <summary>Switching another of your computers between companion and host PC from here (the Devices map's Make it a host PC,
/// or Settings › What this PC is for › Your other computers), and following such a switch asked on another computer. The ask
/// travels with the shared settings as that computer's role entry ("role.desktop-b", <see cref="SharedPc.RoleKey"/>): last
/// writer wins, so a later choice made on that computer itself wins over an earlier ask, and the other way around. This PC
/// says once when the other computer has switched as asked.</summary>
public partial class MainWindow
{
    /// <summary>Asks made here that the other computer hasn't followed yet, by device ID.</summary>
    private readonly Dictionary<string, DeviceRole> roleAsks = new(StringComparer.Ordinal);
    /// <summary>What Settings' Your other computers shows now, so it is drawn again only when that changes.</summary>
    private string? otherRolesShown;

    /// <summary>The name a member computer of the Martlet network goes by ("IMOUTO"), or its device ID.</summary>
    private string ComputerName(string deviceId) =>
        networkState.Roster?.ActiveDesktops.FirstOrDefault(d => d.Id == deviceId)?.Name ?? deviceId;

    /// <summary>The host service Martlet runs on this PC, as your other computers should know it: the one paired here, the one
    /// this PC's host dashboard reads or, on a host PC while Docker Desktop is stopped, the one Martlet saw set up here before.
    /// Null when this PC has none, so another computer can say that a host PC still needs one.</summary>
    private string? OwnHostServiceId() => ThisPcHost()?.HostId ?? hostState?.HostId ??
        (Role == DeviceRole.Host && Pc is { } pc && ThisPcHostRoles.Load(pc.Directory) is not null
            ? HostSetupCommands.SuggestedHostId(Environment.MachineName) : null);

    /// <summary>The host service of another computer that this PC is paired with: the one that computer names, or the one Martlet
    /// names after it (DIVA runs diva-host). Null when this PC knows none.</summary>
    private string? PairedHostOf(MartletComputer computer)
    {
        var id = computer.HostId ?? HostSetupCommands.SuggestedHostId(computer.Name);
        return homeHosts.Any(h => h.HostId == id) ? id : null;
    }

    /// <summary>Another computer asked this PC to be a companion or a host PC: it switches, except while Martlet is talking with
    /// you here (it switches right after that reply), and Home shows what it is now.</summary>
    private SharedApply FollowRoleRequest(SharedSetting setting)
    {
        if (SharedPc.ReadRole(setting.Value) is not { } wanted)
            return SharedApply.Waiting("It was chosen on a newer Martlet. Update this PC to follow it.");
        if (wanted == Role && deviceRole is not null) return SharedApply.Done;
        var host = wanted == DeviceRole.Host;
        if (host && Talking)
            return SharedApply.Waiting("Martlet is talking with you here; this PC becomes a host PC once that reply ends.");
        var by = ComputerName(setting.UpdatedBy);
        ErrorLog.Info($"{by} ({setting.UpdatedBy}) asked this PC to be a {(host ? "host" : "companion")} PC, so it is one now.");
        if (Tour.Visibility == Visibility.Visible) HideTour();
        SetRole(wanted);
        Navigate(NavHome);
        ActionText.Text = host
            ? $"{by} made this PC a Martlet host. Home shows its host service and what it still needs."
            : $"{by} made this PC your companion PC again.";
        return SharedApply.Done;
    }

    /// <summary>Asks another computer to become a companion or a host PC (or, while an earlier ask waits, to stay what it is):
    /// writes its role entry here and gives it to the hosts at once. It switches on its next settings sync while Martlet runs
    /// there, or when Martlet starts there next; this PC says so once it has.</summary>
    private async Task RequestRoleAsync(string? deviceId, DeviceRole role)
    {
        if (settingsNode is null || closing || deviceId is null || IsThisDevice(deviceId)) return;
        var computer = OtherComputers().FirstOrDefault(c => c.DeviceId == deviceId);
        if (computer?.Role is not { } now) return;
        var host = role == DeviceRole.Host;
        var name = computer.Name;
        if (!clusterEnabled)
        {
            ActionText.Text = $"Turn on Keep Martlet the same on all my computers (Devices, Settings for all devices) to switch {name} from " +
                "here: the switch travels with the shared settings.";
            return;
        }
        var withdraw = now == role;
        if (!withdraw && !ConfirmationDialog.Confirm(this, host
                ? $"Make {name} a host PC? Martlet there stops talking and listening (after a reply it is giving), shows its host " +
                  "dashboard and starts its host service's roles by itself. " +
                  (computer.HostId is null && PairedHostOf(computer) is null
                      ? $"{name} has no host service yet: someone at it sets one up once (Set up host service on its Home; Windows may " +
                        "ask to allow it). "
                      : "") +
                  "Its companion choices are kept, and you can make it a companion PC again from here or there."
                : $"Make {name} a companion PC again? Martlet there shows the character and listens again as it did before it became a " +
                  "host (or as its startup choices say). A host service it runs keeps serving your other computers.",
                host ? "Make it a host PC" : "Make it a companion PC"))
            return;
        while (settingsBusy && !closing) await Task.Delay(100);
        if (closing) return;
        try { settingsNode.Put(SharedPc.RoleKey(deviceId), SharedPc.WriteRole(role), DateTimeOffset.UtcNow); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ContractException)
        {
            ActionText.Text = $"Couldn't ask {name} to switch: {error.Message}";
            return;
        }
        if (withdraw) roleAsks.Remove(deviceId);
        else roleAsks[deviceId] = role;
        var what = host ? "host" : "companion";
        ErrorLog.Info(withdraw
            ? $"Asked {name} ({deviceId}) to stay a {what} PC, withdrawing the earlier ask."
            : $"Asked {name} ({deviceId}) to become a {what} PC.");
        ActionText.Text = withdraw
            ? $"{name} stays a {what} PC."
            : $"Asked {name} to become a {what} PC. It switches within a minute while Martlet runs there, or when Martlet starts there " +
              "next. Martlet says here when it has.";
        if (DevicesPage.IsVisible) RenderMap();
        if (SettingsPage.IsVisible) RenderOtherRoles();
        await SyncSettingsAsync();
    }

    /// <summary>After each settings sync: says once, on the status line and in the log, when another computer has switched as
    /// asked here, and drops an ask that a later choice replaced (made on that computer, or asked from another one).</summary>
    private void ObserveRoleAsks()
    {
        if (roleAsks.Count == 0 || settingsNode is null) return;
        var computers = OtherComputers();
        foreach (var (device, wanted) in roleAsks.ToArray())
        {
            var computer = computers.FirstOrDefault(c => c.DeviceId == device);
            var name = computer?.Name ?? ComputerName(device);
            var what = wanted == DeviceRole.Host ? "host" : "companion";
            if (computer?.Role == wanted)
            {
                roleAsks.Remove(device);
                var text = $"{name} is a {what} PC now, as you asked." +
                    (wanted == DeviceRole.Host && computer.HostId is null && PairedHostOf(computer) is null ? " " + NetworkMap.NoHostServiceYet : "");
                ErrorLog.Info(text);
                ActionText.Text = text;
                continue;
            }
            // Still asked for (by this PC, or by another one that asked the same): it hasn't switched yet.
            var entry = settingsNode.Document.Find(SharedPc.RoleKey(device));
            if (entry is not null && SharedPc.ReadRole(entry.Value) == wanted) continue;
            roleAsks.Remove(device);
            if (entry is null) continue;
            var replacedText = $"A later choice made on {ComputerName(entry.UpdatedBy)} replaced your ask that {name} become a {what} PC.";
            ErrorLog.Info(replacedText);
            ActionText.Text = replacedText;
        }
    }

    /// <summary>Settings › What this PC is for › Your other computers: what each other computer of your Martlet network is, with
    /// the same switch as its row on the Devices map (Make it a host PC, Make it a companion PC, or Keep it a companion PC
    /// while an ask waits). Drawn again only when something in it changed, so a sync never takes a button away mid-click.</summary>
    private void RenderOtherRoles()
    {
        if (closing) return;
        var computers = OtherComputers().Where(c => c.Standing == ComputerStanding.Member)
            .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var lines = computers.Select(c => (Computer: c, Line: NetworkMap.RoleLine(c, PairedHostOf(c)), Command: NetworkMap.RoleCommand(c))).ToList();
        var signature = clusterEnabled + "|" + string.Join("|", lines.Select(l => $"{l.Computer.DeviceId}={l.Computer.Name}:{l.Line}:{l.Command?.Label}"));
        if (signature == otherRolesShown && OtherRolesPanel.Children.Count == lines.Count) return;
        otherRolesShown = signature;
        OtherRolesPanel.Children.Clear();
        OtherRolesText.Text = computers.Count == 0
            ? "Your other computers show here once they are in your Martlet network (Devices, Your Martlet network)."
            : !clusterEnabled
            ? "Turn on Keep Martlet the same on all my computers (Devices, Settings for all devices) to switch them from here: the switch " +
              "travels with the shared settings."
            : "Make one a host PC or a companion PC from here. It switches within a minute while Martlet runs there, or when Martlet " +
              "starts there next.";
        foreach (var (computer, line, command) in lines)
        {
            var row = NetworkRowFrame();
            if (clusterEnabled && command is not null)
            {
                var button = new Button { Content = command.Label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
                AutomationProperties.SetAutomationId(button, "OtherRoleSwitch-" + computer.DeviceId);
                AutomationProperties.SetName(button, $"{command.Label}: {computer.Name}");
                var (action, argument) = (command.Action, command.Argument);
                button.Click += (_, _) => RunNodeAction(action, argument);
                DockPanel.SetDock(button, Dock.Right);
                row.Children.Add(button);
            }
            row.Children.Add(NetworkRowText("OtherRole-" + computer.DeviceId,
                computer.Name == computer.DeviceId ? computer.Name : $"{computer.Name} ({computer.DeviceId})", line));
            OtherRolesPanel.Children.Add(NetworkRowCard(row, warning: computer.Asked is not null));
        }
    }
}
