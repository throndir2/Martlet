using System.IO;
using System.Windows;
using Martlet.Core.Contracts;
using Martlet.Core.Sync;

namespace Martlet.Desktop;

/// <summary>Switching another of your computers between companion and host PC from here (the Devices map's Make it a host PC),
/// and following such a switch asked on another computer. The ask travels with the shared settings as that computer's
/// role entry ("role.desktop-b", <see cref="SharedPc.RoleKey"/>): last writer wins, so a later choice made on that computer
/// itself wins over an earlier ask, and the other way around.</summary>
public partial class MainWindow
{
    /// <summary>The name a member computer of the Martlet network goes by ("IMOUTO"), or its device ID.</summary>
    private string ComputerName(string deviceId) =>
        networkState.Roster?.ActiveDesktops.FirstOrDefault(d => d.Id == deviceId)?.Name ?? deviceId;

    /// <summary>Another computer asked this PC to be a companion or a host PC: it switches, except while Martlet is talking with
    /// you here (it switches right after that reply).</summary>
    private SharedApply FollowRoleRequest(SharedSetting setting)
    {
        if (SharedPc.ReadRole(setting.Value) is not { } wanted)
            return SharedApply.Waiting("It was chosen on a newer Martlet. Update this PC to follow it.");
        if (wanted == Role && deviceRole is not null) return SharedApply.Done;
        var host = wanted == DeviceRole.Host;
        if (host && (conversation?.Replying == true || openConversation?.HearingYou == true))
            return SharedApply.Waiting("Martlet is talking with you here; this PC becomes a host PC once that reply ends.");
        var by = ComputerName(setting.UpdatedBy);
        ErrorLog.Info($"{by} ({setting.UpdatedBy}) asked this PC to be a {(host ? "host" : "companion")} PC, so it is one now.");
        if (Tour.Visibility == Visibility.Visible) HideTour();
        SetRole(wanted);
        if (!host) Navigate(NavHome);
        return SharedApply.Done;
    }

    /// <summary>Asks another computer to become a companion or a host PC (or, while an earlier ask waits, to stay what it is):
    /// writes its role entry here and gives it to the hosts at once. It switches on its next settings sync while Martlet runs
    /// there, or when Martlet starts there next.</summary>
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
                ? $"Make {name} a host PC? Martlet there stops talking and listening (after a reply it is giving) and shows its host " +
                  "dashboard, which sets up its host service if it has none yet (that may need someone at that PC once, for example " +
                  "to install Docker Desktop). Its companion choices are kept, and you can make it a companion PC again from here or there."
                : $"Make {name} a companion PC again? You can talk with Martlet there; a host service it runs keeps serving your " +
                  "other computers.",
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
        var what = host ? "host" : "companion";
        ErrorLog.Info(withdraw
            ? $"Asked {name} ({deviceId}) to stay a {what} PC, withdrawing the earlier ask."
            : $"Asked {name} ({deviceId}) to become a {what} PC.");
        ActionText.Text = withdraw
            ? $"{name} stays a {what} PC."
            : $"Asked {name} to become a {what} PC. It switches within a minute while Martlet runs there, or when Martlet starts there next.";
        if (DevicesPage.IsVisible) RenderMap();
        await SyncSettingsAsync();
    }
}
