using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.HostClient;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.F5;

namespace Martlet.Desktop;

/// <summary>More of MainWindow.HostJobs.cs, in a file of its own so that each file stays under 20 KB.</summary>
public partial class MainWindow
{
    private Task AssignThinkingAsync(string key) => AssignJobAsync(HostJob.Thinking, key);

    private ComboBox JobChoice(HostJob job)
    {
        var hosts = NetworkMap.Hosts(Inputs());
        var current = NetworkMap.JobHost(homeSettings, job.Role) is { } id ? "host:" + id : "saved";
        var choice = new ComboBox { MinWidth = 200, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(choice, $"Who handles {job.Job}");
        AutomationProperties.SetAutomationId(choice, job.Title + "Owner");
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
        var saved = SavedRoute(job);
        Option("saved", saved is null ? "Choose in Setup" : SavedName(saved));
        foreach (var host in hosts)
        {
            var check = hostChecks.GetValueOrDefault(host.HostId);
            var model = check?.Offers?.GetValueOrDefault(job.HostRoleKind);
            // A host that cannot run the engine at all (no NVIDIA GPU for F5, an iPhone for Ollama...) or whose roles are
            // switched on on the device itself is shown, not offered.
            if (model is null && CannotHand(host.HostId, job.HostRoleKind, job.Job) is { } cannot)
            {
                Option("host:" + host.HostId, $"{host.HostId} (not available)", cannot);
                continue;
            }
            Option("host:" + host.HostId, host.HostId + (model is not null ? " (ready)"
                : check?.Reachable == true ? " (not installed)" : check?.Reachable == false ? " (not reachable)" : ""));
        }
        // Hosts friends share with this PC: this job on this PC only, never your network's choice.
        foreach (var host in sharedHosts) Option("host:" + host.HostId, SharedOption(host, job.HostRoleKind));
        choice.SelectionChanged += (_, _) =>
        {
            if (!renderingBoard && choice.SelectedItem is ComboBoxItem { Tag: string key } && key != current) AssignJobAsync(job, key).Forget();
        };
        return choice;
    }

    /// <summary>Hands a job to a host a friend shares with this PC, for this PC only (<see cref="LocalJob.Shared"/>): reads only
    /// the engines it offers (its hardware and roles are its owner's), installs and stops nothing there, and never records the
    /// choice in your shared plan, so your other computers keep your network's choice. Speaking uses the voice engine it runs.</summary>
    private async Task AssignSharedJobAsync(HostJob job, PairedHost host)
    {
        ActionText.Text = $"Checking {host.HostId}...";
        var check = await HostControl.CheckAsync(host.Pairing, null, lifetime.Token, shared: true);
        hostChecks[host.HostId] = check;
        if (check.Reachable != true)
        {
            ActionText.Text = check.Code == "auth.revoked" ? $"{job.Title} wasn't moved: {host.HostId}'s owner stopped sharing it with this PC."
                : $"{job.Title} wasn't moved because {host.HostId} didn't respond ({check.Text}).";
            return;
        }
        var route = check.Routes?.FirstOrDefault(r => r.RouteId == job.RouteId);
        // Speaking on a friend's host speaks with the voice engine its owner runs there.
        if (route is null && job.Role == SetupRole.Tts &&
            check.Routes?.FirstOrDefault(r => SpeechEngines.ForRoute(r.RouteId) is not null) is { } voiceRoute)
            (job, route) = (HostJob.SpeakingFor(SpeechEngines.ForRoute(voiceRoute.RouteId)!), voiceRoute);
        if (route is null)
        {
            ActionText.Text = $"{job.Title} wasn't moved: {host.HostId} doesn't offer {job.Job} to this PC, and only its owner can add it there.";
            return;
        }
        F5ReferenceSnapshot? voice = null;
        if (job.RouteType == SetupRouteType.GatewayF5)
            voice = await F5Voices.DefaultAsync(store!.DataDirectory, route.DestinationId, lifetime.Token, SpeechEngines.ForRoute(job.RouteId));
        var withVoice = voice is null ? "" : $" using \"{voice.PresetName}\"";
        // One sentence: what goes to a friend's computer, and that only this PC uses it. The rest is in the wiki.
        var sent = Lowered(job.Sent) + (job.Role == SetupRole.Tts ? " and the voice's recording" : "");
        if (!ConfirmationDialog.Confirm(this,
                $"Use {host.HostId}, a friend's computer, for {job.Job} on this PC only{withVoice}, sending {sent} there while its owner's " +
                "own work comes first?", $"Use {host.HostId}"))
            return;
        pendingJobHosts.Remove(job.Role);
        await SaveJobHostAsync(job, host, route, voice);
        clusterObserved[job.Job] = ObservedJob(job.Job);
        ErrorLog.Info($"Shared hosts: {job.Job} on this PC now uses {host.HostId}, a host a friend shares (not in the shared plan).");
        ActionText.Text = $"{job.Title} now uses {host.HostId}, a friend's computer, on this PC only{withVoice}.{OpenConversationFollows}";
    }

    /// <summary>The route this job uses when no host does it: the current one, or the one kept aside while a host does it.</summary>
    private SetupRoute? SavedRoute(HostJob job)
    {
        var route = homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == job.Role);
        return route is not null && !SelfHostSetup.IsGateway(route.RouteType) ? route
            : store is null ? null : JobSavedRoute.Load(store.DataDirectory, job.SavedFile);
    }

    /// <summary>A route that sends the job to a provider off this PC; Ollama or another model app on this PC isn't one.</summary>
    private static bool IsCloud(SetupRoute route) =>
        route.RouteType is null or SetupRouteType.OpenAi or SetupRouteType.ElevenLabs ||
        route.RouteType == SetupRouteType.ChatCompletions && !Martlet.Providers.LocalModelServers.IsOnThisComputer(route.Origin);

    private static string SavedName(SetupRoute route) => (IsCloud(route) ? "Cloud: " : "") + NetworkMap.ProviderName(route);

    /// <summary>Hands the job to a paired host ("host:ID") or back to its Setup choice ("saved"). A host without the role can
    /// install it in the same step; Martlet keeps the job where it is until the host's role is ready.</summary>
    private async Task AssignJobAsync(HostJob job, string key)
    {
        if (store is null || setupService is null || closing) return;
        ChangeTurns.Turn? turn = null;
        try
        {
            turn = await ChangeTurnAsync();
            if (key == "saved")
            {
                await JobBackAsync(job, turn);
                return;
            }
            var host = FindHost(key[5..]) ?? FindSharedHost(key[5..]) ?? throw new InvalidOperationException("That host is no longer paired.");
            if (host.Shared)
            {
                await AssignSharedJobAsync(job, host);
                return;
            }
            // A voice engine still being stopped there (Speaking just left it) can't take Speaking yet: wait for the stop
            // without holding up other changes, then hand Speaking over through an install as for any engine it doesn't run.
            while (job.Role == SetupRole.Tts && releasingEngines.TryGetValue((host.HostId, job.HostRoleKind), out var stopping))
            {
                turn.Dispose();
                ActionText.Text = $"Waiting for {job.Engine} to finish stopping on {host.HostId}...";
                await stopping;
                turn = await ChangeTurnAsync();
            }
            ActionText.Text = $"Checking {host.HostId}...";
            var check = await HostControl.CheckAsync(host.Pairing, HardwareStore, lifetime.Token);
            hostChecks[host.HostId] = check;
            if (check.Reachable != true)
            {
                ActionText.Text = $"{job.Title} wasn't moved because {host.HostId} didn't respond ({check.Text}).";
                return;
            }
            var route = check.Routes?.FirstOrDefault(r => r.RouteId == job.RouteId);
            if (route is null && CannotHand(host.HostId, job.HostRoleKind, job.Job) is { } cannot)
            {
                ActionText.Text = $"{job.Title} wasn't moved: {cannot}";
                return;
            }
            // One voice engine runs on a computer (role.conf exclusive=voice): speaking with another engine there stops the
            // others, so the model each keeps in the graphics card's memory never starves the engine that speaks.
            var others = job.Role == SetupRole.Tts ? HostRoles.OtherVoiceEngines(check.Offers, job.HostRoleKind) : [];
            // The engine Speaking leaves on another computer stops there once Speaking has moved.
            var leaving = job.Role == SetupRole.Tts ? LeavingEngine(host.HostId, SpeechEngines.ForRoute(job.RouteId)) : null;
            var stops = StopsClause(others, leaving);
            // Speaking already on that host stops with the engine it replaces there, so it pauses until the new one is ready.
            var pauses = others.Count > 0 && NetworkMap.JobHost(homeSettings, job.Role) == host.HostId ? $", so {job.Job} pauses until then" : "";
            // The engines copy a reference voice. Handing Speaking to a host keeps the voice chosen on all computers (else the one
            // applied here, else the first in the list); Companion › Voice › Voices adds voices and switches between them.
            F5ReferenceSnapshot? voice = null;
            if (job.RouteType == SetupRouteType.GatewayF5)
                voice = await F5Voices.DefaultAsync(store.DataDirectory, route?.DestinationId ?? F5Destination, lifetime.Token,
                    SpeechEngines.ForRoute(job.RouteId));
            var withVoice = voice is null ? "" : $" using \"{voice.PresetName}\"";
            if (route is null)
            {
                var role = HostRoles.Get(job.HostRoleKind);
                // On this PC Martlet already knows what suits it, so the install asks nothing more: whisper goes on the
                // graphics card or the processor by what already runs there, F5 has nothing to choose.
                IReadOnlyDictionary<string, string>? answers = null;
                if (host.Method == HostSetupMethod.ThisPcDocker && job.Role == SetupRole.Stt)
                {
                    var advice = await ListeningAdviceAsync();
                    answers = advice.InstallAnswers(advice.UseGpu);
                }
                else if (host.Method == HostSetupMethod.ThisPcDocker && job.RouteType == SetupRouteType.GatewayF5)
                    answers = new Dictionary<string, string>(StringComparer.Ordinal);
                // One sentence: what it installs, what it needs and what it stops; until the role is ready the job stays where it is.
                var needs = HostCan(host.HostId, job.HostRoleKind) is { Verdict: Martlet.Core.Platforms.PlatformVerdict.Unknown }
                    ? $"it needs {role.Needs}, which Martlet can't confirm there" : $"it needs {role.Needs}";
                if (!ConfirmationDialog.Confirm(this,
                        $"Install the {role.Name} role on {host.HostId} ({needs}) and switch {job.Job} to it{withVoice} when it's ready{stops}{pauses}?",
                        "Install and switch"))
                    return;
                pendingJobHosts[job.Role] = host.HostId;
                if (voice is not null) pendingJobVoices[job.Role] = voice;
                LaunchOnHost(host, role.Add, answers);
                WatchJobHandoffAsync(job, host, leaving).Forget();
                return;
            }
            if (!ConfirmationDialog.Confirm(this, $"Use {host.HostId} to {job.Use}{withVoice}{stops}?", $"Use {host.HostId}"))
                return;
            pendingJobHosts.Remove(job.Role);
            await SaveJobHostAsync(job, host, route, voice);
            RecordClusterJob(job.Job, new(host.HostId, false));
            // Saved. Stopping the voice engines Speaking left are host runs: other changes go ahead meanwhile.
            turn.Dispose();
            var moved = $"{job.Title} now uses {host.HostId}{withVoice}.{OpenConversationFollows}";
            ActionText.Text = moved;
            if (others.Count > 0) ActionText.Text = moved + await ReleaseVoiceEnginesAsync(host, others);
            if (leaving is not null) ActionText.Text += await StopLeftVoiceEngineAsync(leaving);
        }
        catch (OperationCanceledException) { }
        catch (F5Exception error) { ActionText.Text = F5Voices.Describe(error); }
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
                RenderHome();
                RenderDevicePages();
            }
        }
    }
}
