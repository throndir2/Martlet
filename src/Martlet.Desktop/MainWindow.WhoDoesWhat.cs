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

    /// <summary>Saves who handles lip-sync and switches a showing character over without restarting it (unless the
    /// lip-sync mode itself changed).</summary>
    private async Task ApplyLipSyncAsync(PairedHost? host, bool off)
    {
        var (before, after) = await Pairings().AssignLipSyncAsync(host, off, lifetime.Token);
        homeAvatar = after;
        if (!avatar.IsShowing) return;
        if (before.LipSync != after.LipSync)
        {
            if (await StopAvatarSafelyAsync()) await ShowSavedCharacterAsync(onlyIfAutoShow: false);
        }
        else await avatar.UseHostAsync(after.RemoteHost, lifetime.Token);
    }

    private void CheckHosts_Click(object sender, RoutedEventArgs e)
    {
        CheckHostsAsync(NetworkMap.Hosts(Inputs())).Forget();
        QueueSettingsSync();
    }

    private async Task CheckHostsAsync(IReadOnlyList<PairedHost> hosts)
    {
        if (hosts.Count == 0)
        {
            ActionText.Text = "No host is paired yet: add a computer first.";
            return;
        }
        foreach (var host in hosts) hostChecks[host.HostId] = new(null, "Checking...");
        RenderDevicePages();
        (string Id, HostCheck Check)[] results;
        try { results = await Task.WhenAll(hosts.Select(async h => (h.HostId, await HostControl.CheckAsync(h.Pairing, HardwareStore, lifetime.Token)))); }
        catch (OperationCanceledException) { return; }
        foreach (var (id, check) in results) hostChecks[id] = check;
        // What answered and what didn't counts for the Thinking pool too, also while Keep in sync is off.
        foreach (var (id, check) in results)
            if (check.Reachable is { } reachable) HostPresence.Note(id, reachable);
        if (closing) return;
        // A role whose settings changed there (from here or another computer) serves another model now: follow it.
        FollowHostModelsAsync(results.Where(r => r.Check.Reachable == true).Select(r => r.Id).ToArray()).Forget();
        // The Singing card reads each checked computer's voice matches again (a role added or removed elsewhere).
        foreach (var (id, _) in results) singingServices.Remove(id);
        NoteSingingHost();
        foreach (var host in hosts)
            if (hostChecks.GetValueOrDefault(host.HostId) is { Reachable: true } found) HostFoundCurrent(UpdateKey(host), found.MartletVersion);
        // A computer with a Thinking model joins the Thinking pool by itself, also while Keep in sync is off.
        var joined = results.Select(r => NoteThinkingPoolHost(r.Id)).OfType<string>().ToArray();
        ActionText.Text = (results.Length == 1 ? $"{results[0].Id}: {results[0].Check.Text}"
            : $"Checked {results.Length} hosts: {results.Count(r => r.Check.Reachable == true)} reachable, " +
              string.Join(", ", HostRoles.All.Select(role =>
                  $"{results.Count(r => r.Check.Offers?.ContainsKey(role.Kind) == true)} running {role.Name}")) + ".") +
            string.Concat(joined.Select(line => " " + line));
        RenderHome();
        RenderDevicePages();
        QueueClusterSync();
    }

    /// <summary>Runs a martlet-host command on a paired host the way this PC reaches it, in Martlet with its output and Cancel
    /// in a run window: over SSH, on this PC's Docker Desktop, or through Martlet on that computer (its paired connection).
    /// Never in a console, and never by asking you to type it there.</summary>
    private void LaunchOnHost(PairedHost host, HostAction action, IReadOnlyDictionary<string, string>? answers = null) =>
        RunHostActionAsync(host, action, answers).Forget();

    /// <summary><see cref="LaunchOnHost"/>, awaitable: returns the run's summary, or null when it stopped or could not run.
    /// <paramref name="confirmed"/>: the owner already agreed to a role removal, so its run window doesn't ask again.</summary>
    private async Task<string?> RunHostActionAsync(PairedHost host, HostAction action, IReadOnlyDictionary<string, string>? answers = null,
        bool confirmed = false)
    {
        try
        {
            if (store is null) return null;
            if (!host.CanLaunch)
            {
                ActionText.Text = $"Set how Martlet signs in to {host.HostId} over SSH on the Devices map, or choose Through Martlet on that computer.";
                ShowDevice("host:" + host.HostId);
                return null;
            }
            var local = host.Method == HostSetupMethod.ThisPcDocker;
            // One graphics card for each Thinking model: adding Deep thinking beside the host's Thinking asks first.
            if (answers is null && action.Verb == HostVerb.Add && action.Role == HostRoles.DeepThinking &&
                DeepThinkingFit.SharedCard(host.HostId, HardwareStore?.Find(host.HostId), hostChecks.GetValueOrDefault(host.HostId)?.Offers)
                    is { } shared &&
                !ConfirmationDialog.Confirm(this, shared + " Add the Thinking pool role there anyway?", "The Thinking pool shares a graphics card",
                    yes: "_Add anyway", no: "_Cancel", questionId: "DeepThinkingShareQuestion"))
            {
                ActionText.Text = $"The Thinking pool role wasn't added on {host.HostId}.";
                return null;
            }
            // Martlet's automatic host update leaves this host to this run rather than colliding with it (and reporting it busy).
            using var updating = action.Verb == HostVerb.Update ? hostUpdates.Begin(UpdateKey(host)) : null;
            ActionText.Text = $"Running {HostSetupCommands.Engine(action)} on {(local ? "this PC" : host.HostId)}" +
                (host.Method == HostSetupMethod.Agent ? " through Martlet there" : "") + ". The progress window shows details.";
            // Adding listening preselects what suits the computer: on this PC whisper on the graphics card or the processor by
            // what already runs there; elsewhere whisper or Parakeet by its hardware; and the Parakeet model for your language.
            var recommended = answers is null && action.Verb == HostVerb.Add && action.Role == HostRoles.Stt
                ? ListeningAdvisor.HostAnswers(HardwareStore?.Find(host.HostId), System.Globalization.CultureInfo.CurrentUICulture,
                    local ? (await ListeningAdviceAsync()).Answers() : null)
                // Deep thinking's thinks at once: what fits on its graphics card beside the host's other roles, for each model (a
                // Thinking pool model on an extra card has that card to itself).
                : answers is null && action.Verb == HostVerb.Add && SelfHostSetup.DeepThinkingCardOfRole(action.Role) is { } card
                ? DeepThinkingFit.Recommend(HardwareStore?.Find(host.HostId), hostChecks.GetValueOrDefault(host.HostId)?.Offers, card)
                : null;
            var done = await HostActions.RunAsync(this, store.DataDirectory, host.Target(Version), host.SshHostKey, action, answers, recommended,
                host.Pairing, confirmed);
            if (done is not null && action.Verb == HostVerb.Update) HostUpdateSettled(UpdateKey(host));
            if (closing) return done;
            ActionText.Text = done is null ? $"{HostSetupCommands.Engine(action)} on {host.HostId} stopped; see the progress window for details." : $"{host.HostId}: {done}";
            if (done is not null && action != HostAction.Status) CheckHostsAsync([host]).Forget();
            if (done is not null && action.Verb == HostVerb.Add && action.Role == HostRoles.Motion && !host.Shared) JoinMotionPool(host.HostId);
            if (local) gpuProbe = null;
            return done;
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException or
            System.ComponentModel.Win32Exception)
        {
            ActionText.Text = error.Message;
            return null;
        }
    }

    private void RunHostRole(string? argument, bool add)
    {
        var parts = argument?.Split('/') ?? [];
        if (parts.Length != 2 || FindHost(parts[0]) is not { } host) return;
        if (parts[1] == HomeAssistantHosts.Role)
        {
            if (add) InstallHomeAssistantAsync(host).Forget();
            else LaunchOnHost(host, HostAction.Remove(HomeAssistantHosts.Role));
            return;
        }
        var role = HostRoles.Get(parts[1]);
        if (add && CannotHand(host.HostId, role.Kind, role.Job) is { } cannot)
        {
            ActionText.Text = $"{role.Name} can't be set up on {host.HostId}: {cannot}";
            return;
        }
        var offered = hostChecks.GetValueOrDefault(host.HostId)?.Offers?.ContainsKey(role.Kind) == true;
        if (!add && (offered || hostChecks.GetValueOrDefault(host.HostId)?.Offers is null))
        {
            RemoveHostRoleAsync(host, role).Forget();
            return;
        }
        LaunchOnHost(host, add ? role.Add : role.Remove);
    }

    private async Task ForgetHostAsync(string? hostId)
    {
        if (FindHost(hostId) is not { } host) return;
        var impact = JobCoverageRules.ForgetImpact(JobSituations(), host.HostId);
        var shared = networkState.Roster?.Host(host.HostId) is { Removed: false };
        if (!ConfirmationDialog.Confirm(this,
                $"Forget {host.HostId}: stop using it on this PC and remove its pairing?" +
                (impact.Count > 0 ? $" It handles: {string.Join(" ", impact)}" : "") +
                (shared
                    ? " Your other computers keep using it; to remove it everywhere, use Remove from network instead."
                    : $" To remove this PC from the host too, revoke {host.Pairing.DeviceId} in the host console."),
                "Forget host"))
            return;
        await HostTaskAsync(async token =>
        {
            if (shared) IgnoreNetworkHost(host.HostId);
            var stranded = await ForgetPairingAsync(host, token);
            ActionText.Text = $"Forgot {host.HostId}" +
                (shared ? ", but your other computers still use it." : ".") +
                (stranded.Count > 0 ? $" Nobody handles {string.Join(" or ", stranded)} now: choose another device in Companion or Devices." : "");
        });
        await RefreshHomeAsync();
    }

    /// <summary>Forgets a host on this PC: jobs it did go back to the choice kept aside for them first (so no route is left
    /// pointing at it), then its pairing, lip-sync assignment and secret go. Returns the jobs nobody does now.</summary>
    private async Task<IReadOnlyList<string>> ForgetPairingAsync(PairedHost host, CancellationToken token)
    {
        var inCharge = homeAvatar?.RemoteHost?.HostId == host.HostId;
        var stranded = new List<string>();
        foreach (var job in HostJob.All.Where(j => NetworkMap.JobHost(homeSettings, j.Role) == host.HostId))
        {
            // A job on a host a friend shared was this PC's own choice: it goes back without changing your shared plan, and
            // this PC follows the plan again on its next check.
            if (store is not null && JobSavedRoute.Load(store.DataDirectory, job.SavedFile) is { } saved) await HandBackAsync(job, saved, record: !host.Shared);
            else stranded.Add(job.Job);
        }
        await Pairings().ForgetAsync(host.HostId, token);
        hostChecks.Remove(host.HostId);
        hostReleases.Remove(host.HostId);
        // A host a friend shares was never in your shared plan, so forgetting it changes nothing there.
        if (!host.Shared) ForgetClusterHost(host.HostId);
        if (inCharge)
        {
            if (!host.Shared) RecordClusterJob(ClusterJobs.LipSync, new(null, false));
            if (avatar.IsShowing) await avatar.UseHostAsync(null, token);
        }
        return stranded;
    }

    private async Task HostTaskAsync(Func<CancellationToken, Task> action)
    {
        if (store is null || setupService is null || closing) return;
        ChangeTurns.Turn? turn = null;
        try
        {
            turn = await ChangeTurnAsync();
            await action(lifetime.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ContractException or
            JsonException or ArgumentException)
        {
            ActionText.Text = error.Message;
        }
        finally
        {
            turn?.Dispose();
            if (!closing && DevicesPage.IsVisible) RenderMap();
        }
    }

    private void RunNodeAction(NodeAction action, string? argument = null)
    {
        var args = new RoutedEventArgs();
        switch (action)
        {
            case NodeAction.Companion: OpenCompanion(Enum.TryParse<SetupRole>(argument, out var jobRole) ? TabFor(jobRole)
                : Enum.TryParse<CompanionTab>(argument, out var tab) ? tab : CompanionTab.Thinking); break;
            case NodeAction.AudioSetup: AudioSetup_Click(this, args); break;
            case NodeAction.Character: Avatar_Click(this, args); break;
            case NodeAction.ToggleCharacter: Character_Click(this, args); break;
            case NodeAction.Prerequisites: Prerequisites_Click(this, args); break;
            case NodeAction.HostThisPc: SetUpThisPcHostAsync().Forget(); break;
            case NodeAction.AddComputer: OpenHosts(0); break;
            case NodeAction.ManageHost: OpenHosts(1, FindHost(argument)); break;
            case NodeAction.CheckHost:
                var hosts = NetworkMap.Hosts(Inputs());
                CheckHostsAsync(argument is null ? hosts : hosts.Where(h => h.HostId == argument).ToArray()).Forget();
                break;
            case NodeAction.HostDashboard: Navigate(NavHome); break;
            case NodeAction.Advisor: Advisor_Click(this, args); break;
            case NodeAction.UseForLipSync: AssignLipSyncAsync("host:" + argument).Forget(); break;
            case NodeAction.UseForThinking: AssignThinkingAsync("host:" + argument).Forget(); break;
            case NodeAction.UseForListening: AssignJobAsync(HostJob.Listening, "host:" + argument).Forget(); break;
            case NodeAction.UseForSpeaking: AssignJobAsync(HostJob.Speaking, "host:" + argument).Forget(); break;
            case NodeAction.LipSyncThisPc: AssignLipSyncAsync("this-pc").Forget(); break;
            case NodeAction.InstallRole: RunHostRole(argument, add: true); break;
            case NodeAction.ChangeRole: ChangeHostRole(argument); break;
            case NodeAction.RemoveRole: RunHostRole(argument, add: false); break;
            case NodeAction.HostStatus: if (FindHost(argument) is { } host) LaunchOnHost(host, HostAction.Status); break;
            case NodeAction.UpdateHost:
                if (FindHost(argument) is { } outdated)
                {
                    hostUpdates.ClearNote(outdated.HostId);
                    LaunchOnHost(outdated, HostAction.Update);
                }
                break;
            case NodeAction.ForgetHost: ForgetHostAsync(argument).Forget(); break;
            case NodeAction.PrepareHost: if (FindHost(argument) is { } prepare) OpenPrepare(prepare, PrepareStart.Status); break;
            case NodeAction.RebootHost: if (FindHost(argument) is { } reboot) OpenPrepare(reboot, PrepareStart.Reboot); break;
            case NodeAction.ShutdownHost: if (FindHost(argument) is { } shutdown) OpenPrepare(shutdown, PrepareStart.Shutdown); break;
            case NodeAction.WakeHost: if (FindHost(argument) is { } wake) OpenPrepare(wake, PrepareStart.Wake); break;
            case NodeAction.PrepareComputer: OpenPrepare(null, PrepareStart.Status); break;
            case NodeAction.MakeHostPc: RequestRoleAsync(argument, DeviceRole.Host).Forget(); break;
            case NodeAction.MakeCompanionPc: RequestRoleAsync(argument, DeviceRole.Companion).Forget(); break;
            case NodeAction.OutsideAccess: if (FindHost(argument) is { } exposed) EditHostExposureAsync(exposed).Forget(); break;
        }
    }

    private void OpenHosts(int step, PairedHost? manage = null, NearbyMartlet? connect = null)
    {
        if (store is null || setupService is null || closing) return;
        new HostsWindow(new AvatarProfileStore(store.DataDirectory), setupService, step, manage, connect) { Owner = this }.ShowDialog();
        RefreshHomeAsync().Forget();
    }

    /// <summary>Sets up and pairs Martlet's host service on this PC in one click, in a run window; with
    /// <paramref name="andThen"/> it then continues straight into handing a job to it ("host:ID"). Whisper and F5 chain
    /// their install into the same window instead (<see cref="SetUpJobHereAsync"/>). Started again (or by another flow)
    /// while one runs, it waits for that setup and pairing instead of repeating them, then carries on with its own next step.</summary>
    private async Task SetUpThisPcHostAsync(Func<string, Task>? andThen = null)
    {
        if (store is null || setupService is null || closing) return;
        PairedHost? host;
        try
        {
            string? status;
            (host, status) = await HostsWindow.SetUpThisPcAsync(this, new AvatarProfileStore(store.DataDirectory), setupService,
                text => ActionText.Text = text, lifetime.Token);
            if (status is not null && !closing) ActionText.Text = status;
        }
        catch (OperationCanceledException) { return; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or
            ContractException or JsonException)
        {
            ActionText.Text = error.Message;
            return;
        }
        if (closing || host is null) return;
        await ReadMachineAsync();
        await RefreshHomeAsync();
        if (andThen is not null && FindHost(host.HostId) is not null) await andThen("host:" + host.HostId);
    }
}
