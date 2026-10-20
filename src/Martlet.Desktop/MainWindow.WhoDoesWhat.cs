using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Martlet.HostClient;
using Martlet.Avatar.Hosting;
using Martlet.Core.Cluster;
using Martlet.Core.Contracts;
using Martlet.Core.Installation;
using Martlet.Core.Platforms;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

// The app shell's who does what (see MainWindow.Shell.cs for the shell's shared state).
public partial class MainWindow
{
    private HostPairings Pairings() => new(store!.DataDirectory, new AvatarProfileStore(store.DataDirectory), setupService!);

    private PairedHost? FindHost(string? hostId) => hostId is null ? null
        : NetworkMap.Hosts(Inputs()).FirstOrDefault(h => h.HostId == hostId);

    /// <summary>A host a friend shares with this PC, or null.</summary>
    private PairedHost? FindSharedHost(string? hostId) => hostId is null ? null : sharedHosts.FirstOrDefault(h => h.HostId == hostId);

    /// <summary>A shared host's option text in a job's choice: its name, that a friend shares it, and whether it offers the job
    /// (for Speaking, any voice engine: this PC speaks with the one it runs).</summary>
    private string SharedOption(PairedHost host, string roleKind)
    {
        var check = hostChecks.GetValueOrDefault(host.HostId);
        var offers = check?.Offers?.Keys.Any(k => k == roleKind || HostRoles.Speaks(roleKind) && HostRoles.Speaks(k)) == true;
        return $"{host.HostId}, shared by a friend" + (offers ? " (ready)"
            : check?.Reachable == true ? " (doesn't offer it)" : check?.Reachable == false ? " (not reachable)" : "");
    }

    /// <summary>Who handles lip-sync: this PC, a paired host (installing Audio2Face there if needed) or nobody.</summary>
    private ComboBox LipSyncChoice()
    {
        var current = NetworkMap.LipSync(homeAvatar) switch
        {
            LipSyncHandler.Loudness => "off",
            LipSyncHandler.Host => "host:" + homeAvatar!.RemoteHost!.HostId,
            _ => "this-pc"
        };
        var choice = new ComboBox { MinWidth = 200, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(choice, "Who handles lip-sync");
        AutomationProperties.SetAutomationId(choice, "LipSyncOwner");
        void Option(string key, string text, string? blocked = null)
        {
            var item = new ComboBoxItem { Content = text, Tag = key };
            if (blocked is not null)
            {
                item.IsEnabled = false;
                item.ToolTip = blocked;
                ToolTipService.SetShowOnDisabled(item, true);
                AutomationProperties.SetHelpText(item, blocked);
            }
            choice.Items.Add(item);
            if (key == current) choice.SelectedItem = item;
        }
        Option("this-pc", ownLipSyncAnswers == false ? "This PC lip-sync (not running)" : "This PC lip-sync");
        var local = ThisPcHost()?.HostId;
        foreach (var host in NetworkMap.Hosts(Inputs()))
        {
            var check = hostChecks.GetValueOrDefault(host.HostId);
            var offers = check?.Offers?.ContainsKey(HostRoles.Audio2Face) == true;
            var name = host.HostId == local ? $"{host.HostId}, this PC" : host.HostId;
            if (!offers && CannotHand(host.HostId, HostRoles.Audio2Face, ClusterJobs.LipSync) is { } cannot)
            {
                Option("host:" + host.HostId, $"{name} (can't take it now)", cannot);
                continue;
            }
            Option("host:" + host.HostId, name + (offers ? " (ready for lip-sync)"
                : check?.Reachable == true ? " (needs lip-sync setup)" : check?.Reachable == false ? " (not reachable)" : ""));
        }
        // Hosts friends share with this PC: lip-sync on this PC only.
        foreach (var host in sharedHosts) Option("host:" + host.HostId, SharedOption(host, HostRoles.Audio2Face));
        Option("off", "No one (basic mouth movement)");
        choice.SelectionChanged += (_, _) =>
        {
            if (!renderingBoard && choice.SelectedItem is ComboBoxItem { Tag: string key } && key != current) AssignLipSyncAsync(key).Forget();
        };
        return choice;
    }

    /// <summary>Hands lip-sync to a paired host ("host:ID"), this PC ("this-pc") or nobody ("off"). A host that does not run
    /// Audio2Face yet can install it in the same step; the showing character switches without restarting.</summary>
    private async Task AssignLipSyncAsync(string key)
    {
        if (store is null || setupService is null || closing) return;
        ChangeTurns.Turn? turn = null;
        try
        {
            turn = await ChangeTurnAsync();
            PairedHost? host = null;
            var install = false;
            if (key.StartsWith("host:", StringComparison.Ordinal))
            {
                host = FindHost(key[5..]) ?? FindSharedHost(key[5..]) ?? throw new InvalidOperationException("That host is no longer paired.");
                ActionText.Text = $"Checking {host.HostId}...";
                var check = await HostControl.CheckAsync(host.Pairing, HardwareStore, lifetime.Token, host.Shared);
                hostChecks[host.HostId] = check;
                if (check.Reachable != true)
                {
                    ActionText.Text = $"Lip-sync stays where it is: {host.HostId} didn't answer ({check.Text})";
                    return;
                }
                if (host.Shared)
                {
                    // A friend's host offers what its owner set up there; this PC can't add roles to it.
                    if (check.Offers?.ContainsKey(HostRoles.Audio2Face) != true)
                    {
                        ActionText.Text = $"Lip-sync stays where it is: {host.HostId} doesn't offer lip-sync to this PC, and only its owner can add it there.";
                        return;
                    }
                    if (!ConfirmationDialog.Confirm(this, $"Use your friend's {host.HostId} for lip-sync on this PC, so the generated voice " +
                            "audio goes to their computer (it isn't kept there)?", $"Use {host.HostId}"))
                        return;
                }
                else if (check.Offers?.ContainsKey(HostRoles.Audio2Face) != true)
                {
                    if (CannotHand(host.HostId, HostRoles.Audio2Face, ClusterJobs.LipSync) is { } cannot)
                    {
                        ActionText.Text = $"Lip-sync stays where it is: {cannot}";
                        return;
                    }
                    var role = HostRoles.Get(HostRoles.Audio2Face);
                    if (!ConfirmationDialog.Confirm(this,
                            $"Install lip-sync support on {host.HostId}, which needs {role.Needs}?" +
                            (host.CanLaunch ? "" : " Set how Martlet reaches it on the Devices map first."),
                            "Install lip-sync"))
                        return;
                    install = true;
                }
            }
            await ApplyLipSyncAsync(host, key == "off");
            FollowLipSyncChoiceIntoPool(key);
            tabPlace.Remove(CompanionTab.LipSync);
            RecordClusterJob(ClusterJobs.LipSync, ClusterSync.Local(ClusterJobs.LipSync, homeSettings, homeAvatar, shared: SharedHostIds()));
            var who = key == "off" ? "no one (basic mouth movement)" : host?.HostId ?? "this PC";
            var message = $"Lip-sync is now handled by {who}.";
            if (install) LaunchOnHost(host!, HostRoles.Get(HostRoles.Audio2Face).Add);
            ActionText.Text = install ? message + " " + ActionText.Text : avatar.IsShowing ? message + " " + avatar.Status : message;
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ContractException or
            JsonException or ArgumentException or HostConnectionException)
        {
            ActionText.Text = error.Message;
        }
        finally
        {
            turn?.Dispose();
            if (!closing)
            {
                UpdateCharacterButton();
                RenderHome();
                RenderDevicePages();
                CheckOwnLipSyncAsync().Forget();
            }
        }
    }
}
