using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Martlet.Avatar.Hosting;
using Martlet.Core.Cluster;
using Martlet.Core.Installation;
using Martlet.Core.Platforms;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Desktop;

/// <summary>How much a Home health item matters: a problem stops Martlet replying; a warning means something chosen doesn't
/// work (or works in a reduced way); a notice is good to know (optional setup, an update, the last run ending badly).</summary>
internal enum HealthLevel { Problem, Warning, Notice }

/// <summary>A fix offered next to a health item. A <paramref name="Passive"/> fix only opens a page or hides the item.</summary>
internal sealed record HealthFix(string Id, string Label, Action Run, bool Passive = false);

/// <summary>Something on Home that is wrong, missing or worth knowing. <paramref name="Headline"/> replaces the hero's title
/// when this is the top problem.</summary>
internal sealed record HealthIssue(string Id, HealthLevel Level, string Title, string Detail, IReadOnlyList<HealthFix> Fixes,
    string? Headline = null);

/// <summary>One part Martlet checks, for the Health tiles: its state in a few words and the page where it changes.</summary>
internal sealed record HealthTile(string Id, string Name, string Glyph, NodeHealth Health, string Status, Action Open);

/// <summary>Home: what is wrong, missing or worth knowing, each with its fixes, then a tile per part Martlet checks. Everything
/// comes from what this PC already knows (saved settings, the last host checks, Windows' device and app lists, the local log
/// and this PC's own loopback services); nothing here contacts another computer or a provider.</summary>
public partial class MainWindow
{
    private enum LocalOllamaState { NotUsed, NotInstalled, NotRunning, ModelMissing, Ready }

    private SettingsLoadState? homeSettingsState;
    private string? homeSettingsProblem;
    private LocalOllamaState ollamaState;
    private bool webView2Missing, microphoneBlocked, localCheckAgain;
    private Task? localCheck;
    private string? localCheckKey;
    private readonly HashSet<string> dismissedHealth = new(StringComparer.Ordinal);
    private int errorsAcknowledged;
    private string? healthSignature;
    private Action? stageFix;
    private DispatcherTimer? healthTimer;

    private void InitializeHealth()
    {
        healthTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        healthTimer.Tick += (_, _) =>
        {
            healthTimer.Stop();
            if (!closing) RenderHealth();
        };
        ErrorLog.ErrorRecorded += QueueHealth;
        ErrorLog.PreviousRunDescribed += QueueHealth;
        if (conversation is not null) conversation.FailuresChanged += QueueHealth;
    }

    private void ReleaseHealth()
    {
        ErrorLog.ErrorRecorded -= QueueHealth;
        ErrorLog.PreviousRunDescribed -= QueueHealth;
        if (conversation is not null) conversation.FailuresChanged -= QueueHealth;
        healthTimer?.Stop();
    }

    /// <summary>Re-renders Home's health at most once a second after something changed on another thread.</summary>
    private void QueueHealth() => Dispatcher.BeginInvoke(() =>
    {
        if (!closing && healthTimer is { IsEnabled: false }) healthTimer.Start();
    });

    private async void HealthRecheck_Click(object sender, RoutedEventArgs e)
    {
        if (closing) return;
        HealthRecheckButton.IsEnabled = false;
        try
        {
            ActionText.Text = "Checking again on this PC...";
            await ReadMachineAsync();
            await RefreshAsync();
            await CheckLocalServicesAsync();
            if (closing) return;
            var (issues, _) = BuildHealth();
            var open = issues.Count(i => i.Level != HealthLevel.Notice);
            ActionText.Text = open == 0 ? "Checked again: nothing needs your attention."
                : $"Checked again: {open} thing{(open == 1 ? "" : "s")} need{(open == 1 ? "s" : "")} your attention.";
        }
        finally { if (!closing) HealthRecheckButton.IsEnabled = true; }
    }

    private void HealthChecks_SizeChanged(object sender, SizeChangedEventArgs e) =>
        HealthChecks.Columns = Math.Clamp((int)(HealthChecks.ActualWidth / 240), 1, 4);

    /// <summary>Checks this PC's own pieces that Home reports on: whether Ollama answers on its loopback port with the Thinking
    /// model (when Thinking uses it), and the WebView2 runtime and Windows microphone permission (registry only). A call while
    /// a check runs returns that check, which then runs once more so it sees the latest settings.</summary>
    private Task CheckLocalServicesAsync()
    {
        if (localCheck is { IsCompleted: false } running)
        {
            localCheckAgain = true;
            return running;
        }
        return localCheck = RunLocalChecksAsync();
    }

    /// <summary>Starts a local check when the Thinking route changed since the last one (a new choice in Companion, a sync).</summary>
    private void FollowLocalServices()
    {
        var thinking = homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Llm);
        var key = thinking is null ? "" : $"{thinking.RouteType}|{thinking.Origin}|{thinking.ModelId}|{thinking.Enabled}";
        if (key == localCheckKey) return;
        localCheckKey = key;
        ollamaState = LocalOllamaState.NotUsed;
        CheckLocalServicesAsync().Forget();
    }

    private async Task RunLocalChecksAsync()
    {
        if (closing || Role != DeviceRole.Companion) return;
        do
        {
            localCheckAgain = false;
            var state = LocalOllamaState.NotUsed;
            webView2Missing = Prerequisites.IsMissing(Prerequisites.WebView2);
            microphoneBlocked = Prerequisites.IsMissing(Prerequisites.Microphone);
            var thinking = homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Llm);
            if (IsLocalOllama(thinking) && thinking!.Enabled != false)
            {
                IReadOnlyList<string>? models;
                try { models = await LocalOllama.ModelsAsync(TimeSpan.FromSeconds(2), lifetime.Token); }
                catch (OperationCanceledException) { return; }
                state = models is null
                    ? Prerequisites.IsMissing(Prerequisites.Ollama) ? LocalOllamaState.NotInstalled : LocalOllamaState.NotRunning
                    : LocalOllama.Serves(models, thinking.ModelId) ? LocalOllamaState.Ready : LocalOllamaState.ModelMissing;
            }
            if (closing) return;
            ollamaState = state;
        } while (localCheckAgain);
        RenderHealth();
    }

    private void StartOllama()
    {
        if (!LocalOllama.Start())
        {
            ActionText.Text = "Ollama couldn't start. Start it from the Start menu, or install it again.";
            return;
        }
        ActionText.Text = "Starting Ollama on this PC...";
        WaitForOllamaAsync().Forget();
    }

    private async Task WaitForOllamaAsync()
    {
        for (var attempt = 0; attempt < 15 && !closing; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), lifetime.Token);
            await CheckLocalServicesAsync();
            if (ollamaState != LocalOllamaState.NotRunning) break;
        }
        if (!closing && ollamaState == LocalOllamaState.NotRunning)
            ActionText.Text = "Ollama didn't start. Start it from the Start menu, then press Check again.";
    }

    private async Task PullThinkingModelAsync(string model)
    {
        await PullOllamaModelAsync(model);
        if (!closing) await CheckLocalServicesAsync();
    }

    private async Task InstallThinkingOllamaAsync(string model)
    {
        await InstallPrerequisitesAsync([Prerequisites.Ollama], model);
        if (!closing) await CheckLocalServicesAsync();
    }

    private async Task InstallPrerequisiteAsync(Prerequisite item)
    {
        await InstallPrerequisitesAsync([item]);
        if (!closing) await CheckLocalServicesAsync();
    }

    private void OpenLogsFolder()
    {
        if (!ErrorLog.OpenFolder()) ActionText.Text = "The logs folder isn't available. Check access to Martlet's data folder.";
    }

    private void OpenMicrophonePrivacy()
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:privacy-microphone") { UseShellExecute = true })?.Dispose(); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ActionText.Text = "Couldn't open Windows Settings. Open Microphone privacy settings and allow desktop apps.";
        }
    }

    private void ShowDevice(string? nodeId)
    {
        Navigate(NavDevices);
        if (nodeId is not null) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => SelectNode(nodeId, animate: true));
    }

    // ---------- what is wrong, missing or worth knowing ----------

    private (List<HealthIssue> Issues, List<HealthTile> Tiles) BuildHealth()
    {
        var issues = new List<HealthIssue>();
        var tiles = new List<HealthTile>();
        void Add(string id, HealthLevel level, string title, string detail, IReadOnlyList<HealthFix> fixes, string? headline = null)
        {
            if (!dismissedHealth.Contains(id)) issues.Add(new(id, level, title, detail, fixes, headline));
        }
        HealthFix Open(CompanionTab tab, string label) => new("open-" + tab.ToString().ToLowerInvariant(), label, () => OpenCompanion(tab), Passive: true);
        HealthFix Dismiss(string id) => new("dismiss", "Dismiss", () => { dismissedHealth.Add(id); RenderHealth(); }, Passive: true);
        HealthFix Logs() => new("logs", "Open logs", OpenLogsFolder);
        HealthFix DiagnosticsPage() => new("diagnostics", "Open Diagnostics", () => Navigate(NavDiagnostics), Passive: true);

        var routes = homeSettings?.Setup?.Routes ?? [];
        SetupRoute? Route(SetupRole role) => routes.FirstOrDefault(r => r.Role == role);
        var llm = Route(SetupRole.Llm);
        var stt = Route(SetupRole.Stt);
        var tts = Route(SetupRole.Tts);
        JobCoverage? Down(string job) => coverage.FirstOrDefault(c => c.Job == job && c.IsProblem);
        var failures = conversation?.RecentFailures ?? [];
        var settingsBroken = homeSettingsState is SettingsLoadState.Invalid or SettingsLoadState.Inaccessible;
        var hosts = NetworkMap.Hosts(Inputs());
        var localHost = ThisPcHost();

        // This app: its data folder, settings and unexpected errors.
        if (store is null)
            Add("data-folder", HealthLevel.Problem, "Martlet can't use its data folder",
                startupError ?? "Its settings can't be read or saved.", [new("troubleshooting", "Open Troubleshooting", () => OpenTroubleshooting(this))],
                "Martlet can't start properly");
        if (settingsBroken)
            Add("settings", HealthLevel.Problem, "Martlet can't read its settings",
                (homeSettingsProblem ?? "Your settings can't be used.") + " Restore a backup to get your setup back.",
                [new("restore", "Restore a backup", () => Recovery_Click(this, new RoutedEventArgs())),
                 new("troubleshooting", "Open Troubleshooting", () => OpenTroubleshooting(this))],
                "Martlet can't read its settings");

        // Thinking: the one job Martlet needs.
        var thinkingDown = Down(ClusterJobs.Thinking);
        if (store is not null && !settingsBroken && llm is null)
        {
            var nothingYet = homeSettings is null || routes.Count == 0;
            Add("thinking-setup", HealthLevel.Problem, "Set up thinking",
                "Martlet needs a conversation model before it can reply. Everything else is optional.",
                [Open(CompanionTab.Thinking, "Set up thinking"), new("advisor", "Get a recommendation", () => Advisor_Click(this, new RoutedEventArgs()))],
                nothingYet ? "Let's bring your companion to life" : "Set up thinking to start talking");
        }
        if (llm is not null && ChatCompletionsEndpointCatalog.RetiredOn(llm.Origin, llm.ModelId) is { } retired)
            Add("thinking-retired", HealthLevel.Problem, "Your thinking model was retired",
                $"This model is no longer available. Choose {retired.DefaultModelId} in Companion › Thinking.",
                [Open(CompanionTab.Thinking, "Change thinking")], "Martlet can't reply right now");
        else if (llm is not null && thinkingDown is null)
        {
            var model = llm.ModelId;
            switch (ollamaState)
            {
                case LocalOllamaState.NotInstalled:
                    Add("ollama", HealthLevel.Problem, "Ollama isn't installed on this PC",
                        $"Thinking uses {model} on this PC. Install Ollama with that model.",
                        [new("install", $"Install Ollama and {model}", () => InstallThinkingOllamaAsync(model).Forget()),
                         Open(CompanionTab.Thinking, "Change thinking")], "Martlet can't reply right now");
                    break;
                case LocalOllamaState.NotRunning:
                    Add("ollama", HealthLevel.Problem, "Ollama isn't running on this PC",
                        $"Thinking uses {model} on this PC, but Ollama isn't running.",
                        [new("start", "Start Ollama", StartOllama), Open(CompanionTab.Thinking, "Change thinking")], "Martlet can't reply right now");
                    break;
                case LocalOllamaState.ModelMissing:
                    Add("ollama", HealthLevel.Problem, $"{model} isn't downloaded",
                        $"Thinking uses {model} on this PC, but it hasn't been downloaded yet.",
                        [new("download", $"Download {model}", () => PullThinkingModelAsync(model).Forget()), Open(CompanionTab.Thinking, "Change thinking")],
                        "Martlet can't reply right now");
                    break;
            }
        }

        // Jobs that were chosen but don't work: from coverage (hosts, keys, consent, unsupported routes).
        foreach (var job in coverage.Where(c => c.IsProblem))
        {
            var thinking = job.Job == ClusterJobs.Thinking && job.State == CoverageState.Unavailable;
            var fixes = job.Fixes.Select(fix => new HealthFix(fix.ToString().ToLowerInvariant(), CoverageFixLabel(job, fix),
                () => RunCoverageFix(job, fix), Passive: fix is CoverageFix.OpenSetup or CoverageFix.OpenDevices)).ToList();
            Add("job-" + job.Job.Replace(' ', '-'), thinking ? HealthLevel.Problem : HealthLevel.Warning,
                job.State == CoverageState.Unavailable ? $"{job.Title} isn't working" : $"{job.Title} is working in a reduced way",
                (job.Problem + " " + job.Effect).Trim(), fixes, thinking ? JobCoverageRules.Headline(coverage) : null);
        }

        // This PC's host service (Docker Desktop) runs jobs it can't do while Docker is stopped.
        if (localHost is not null && !ReferenceEquals(machine, MachineInfo.Unknown) && !machine.DockerRunning)
        {
            var jobs = new[] { (SetupRole.Llm, "thinking"), (SetupRole.Stt, "listening"), (SetupRole.Tts, "speaking") }
                .Where(j => NetworkMap.JobHost(homeSettings, j.Item1) == localHost.HostId).Select(j => j.Item2).ToList();
            if (NetworkMap.LipSync(homeAvatar) == LipSyncHandler.Host && homeAvatar?.RemoteHost?.HostId == localHost.HostId) jobs.Add("lip-sync");
            if (jobs.Count > 0)
                Add("docker", jobs.Contains("thinking") ? HealthLevel.Problem : HealthLevel.Warning,
                    machine.DockerInstalled ? "Docker Desktop isn't running" : "Docker Desktop isn't installed",
                    $"This PC's host service does the {string.Join(" and ", jobs)} in Docker Desktop, so {(jobs.Count == 1 ? "it stops" : "they stop")} until Docker runs.",
                    [machine.DockerInstalled ? new("start-docker", "Start Docker Desktop", StartDocker) : new("install-docker", "Install Docker Desktop", InstallDocker)],
                    jobs.Contains("thinking") ? "Martlet can't reply right now" : null);
        }

        // Requests that failed while talking and haven't worked since.
        foreach (var failure in failures)
        {
            var (tab, label) = failure.Role switch
            {
                SetupRole.Stt => (CompanionTab.Listening, "listening"),
                SetupRole.Tts => (CompanionTab.Voice, "voice"),
                _ => (CompanionTab.Thinking, "thinking")
            };
            Add("failed-" + label, HealthLevel.Warning, $"The last {failure.What.ToLowerInvariant()} failed",
                $"Last attempt: {failure.Outcome}. Details are in the local log. This clears when the next one works.",
                [Open(tab, $"Check {label}"), Logs()]);
        }

        // Listening and its microphone.
        if (stt is null && store is not null && !settingsBroken)
            Add("listening-setup", HealthLevel.Notice, "Listening isn't set up",
                "Optional: Martlet hears you talk. You can always type instead.", [Open(CompanionTab.Listening, "Set up listening")]);
        var micMissing = AudioMissing(output: false);
        if (stt is not null && micMissing)
            Add("microphone", HealthLevel.Warning,
                homeSettings?.Audio?.Input.EndpointId is null ? "No microphone found" : "Your chosen microphone isn't connected",
                "Martlet can't hear you until a microphone is connected or another is chosen. You can still type.",
                [Open(CompanionTab.Listening, "Choose a microphone")]);
        if (stt is not null && microphoneBlocked)
            Add("microphone-blocked", HealthLevel.Warning, "Windows blocks the microphone",
                "Windows privacy settings stop desktop apps from using the microphone, so Martlet can't hear you.",
                [new("privacy", "Open microphone settings", OpenMicrophonePrivacy)]);

        // Voice and its speakers.
        if (tts is null && store is not null && !settingsBroken)
            Add("voice-setup", HealthLevel.Notice, "Voice isn't set up",
                "Optional: Martlet speaks its replies. Until then it replies in text.", [Open(CompanionTab.Voice, "Set up a voice")]);
        var speakersMissing = AudioMissing(output: true);
        if (tts is not null && speakersMissing)
            Add("speakers", HealthLevel.Warning, "Your chosen speakers aren't connected",
                "Martlet can't speak until they're back or others are chosen. Replies still show as text.",
                [Open(CompanionTab.Voice, "Choose speakers")]);

        // Lip-sync and the character.
        if (NetworkMap.LipSync(homeAvatar) == LipSyncHandler.ThisPc && ownLipSyncAnswers == false && machine.BestGpu is { IsNvidia: true })
            Add("audio2face", HealthLevel.Notice, "Lifelike lip-sync is available",
                "Use this PC's NVIDIA graphics card for smoother lip-sync.",
                [Open(CompanionTab.LipSync, "Set up lip-sync")]);
        if (webView2Missing)
            Add("webview2", HealthLevel.Warning, "The desktop character can't show",
                "Install the WebView2 Runtime to show the character.",
                [new("install", "Install WebView2", () => InstallPrerequisiteAsync(Prerequisites.WebView2).Forget())]);

        // Vision: on, but with nothing it can use.
        var talk = Talk;
        if (talk.Watch && llm is not null && LiveConversationConfiguration.Vision(llm) == VisionSupport.Unsupported)
            Add("vision", HealthLevel.Warning, "Martlet can't see with your thinking model",
                LiveConversationConfiguration.VisionAdvice(llm), [Open(CompanionTab.Vision, "Open vision")]);
        else if (talk.Watch && VisionSource(talk) is { IsScreen: false, Id.Length: 0 })
            Add("vision-source", HealthLevel.Warning, "Vision has nothing to look at",
                "Watching is on with a camera, but no camera is chosen.", [Open(CompanionTab.Vision, "Choose a camera")]);

        // Paired computers: not answering or on an older Martlet (when no job problem above already names them).
        var hardware = HardwareStore?.Load() ?? [];
        var namedHosts = coverage.Where(c => c.IsProblem && c.HostId is not null).Select(c => c.HostId!).ToHashSet(StringComparer.Ordinal);
        var localHostId = localHost?.HostId;
        var hostsAnswering = 0;
        var hostsDown = 0;
        var hostsOld = 0;
        foreach (var host in hosts.Where(h => h.HostId != localHostId))
        {
            var id = host.HostId;
            var probe = clusterEnabled ? clusterProbes.GetValueOrDefault(id) : null;
            var check = hostChecks.GetValueOrDefault(id);
            bool? reachable = probe?.Reachable ?? check?.Reachable;
            if (reachable == true) hostsAnswering++;
            var saved = hardware.FirstOrDefault(h => h.HostId == id)?.MartletVersion;
            var reported = check?.Reachable == true ? check.MartletVersion ?? saved : saved;
            var outdated = (check?.Reachable == true || reported is not null) && AppVersions.IsOlder(reported, Version);
            if (outdated) hostsOld++;
            if (reachable == false) hostsDown++;
            if (namedHosts.Contains(id)) continue;
            HealthFix Show() => new("show", "Show on the map", () => ShowDevice("host:" + id), Passive: true);
            if (reachable == false)
                Add("host-" + id, HealthLevel.Warning, $"{id} isn't answering",
                    "It is not handling any task right now. Check that it is on and on your network.",
                    [new("check", $"Check {id} now", () => CheckHostsAsync([host]).Forget()), Show()]);
            else if (outdated)
                Add("host-update-" + id, HealthLevel.Warning, $"{id} runs an older Martlet",
                    $"It runs {reported ?? "an older version"}. Update it to Martlet {Version}.",
                    [new("update", $"Update {id}", () => RunNodeAction(NodeAction.UpdateHost, id)), Show()]);
        }

        // Tool servers.
        var toolServers = mcpTools.Servers;
        var toolStatus = mcpTools.Hub.Status;
        if (mcpTools.ConfigurationError is { } toolsProblem)
            Add("tools-config", HealthLevel.Warning, "Your tool servers can't start", toolsProblem, [Open(CompanionTab.Tools, "Open tools")]);
        foreach (var failed in toolStatus.Where(s => s.State == Martlet.Mcp.Client.McpServerState.Failed))
            Add("tools-" + failed.Name, HealthLevel.Warning, $"The {failed.Name} tool server isn't working",
                failed.Error ?? "It stopped or failed to start.", [Open(CompanionTab.Tools, "Open tools")]);

        // Updates.
        if (failedInstallMessage is { } installProblem)
            Add("update-failed", HealthLevel.Warning, "The last update didn't install", installProblem,
                [new("settings", "Open app updates", () => Navigate(NavSettings), Passive: true), Dismiss("update-failed")]);
        if (interruptedUpdateCleanup is { } cleanup)
            Add("update-cleanup", HealthLevel.Warning, "An update download wasn't cleaned up", cleanup,
                [new("settings", "Open app updates", () => Navigate(NavSettings), Passive: true)]);
        if (availableUpdate is { } update)
            Add("update", HealthLevel.Notice, $"Martlet {update.Version.ToString(3)} is available",
                $"You run {Version}. Install the update when you're ready.",
                [new("install", $"Install {update.Version.ToString(3)}", () => ConfirmAndInstallAsync(update, prompted: false).Forget()),
                 new("details", "Release notes", () => ReviewUpdate_Click(this, new RoutedEventArgs()))]);

        // Unexpected errors since Martlet started, and the last run ending badly.
        var errors = ErrorLog.ErrorCount;
        if (errors > errorsAcknowledged && ErrorLog.LastError is { } last)
        {
            var fresh = errors - errorsAcknowledged;
            var message = last.Message.Length > 300 ? last.Message[..300] + "…" : last.Message;
            Add("errors", HealthLevel.Warning, $"Martlet hit {fresh} unexpected error{(fresh == 1 ? "" : "s")}",
                $"The latest, at {last.At.LocalDateTime:t}: {message}",
                [DiagnosticsPage(), Logs(), new("troubleshooting", "Open Troubleshooting", () => OpenTroubleshooting(this)),
                 new("dismiss", "Dismiss", () => { errorsAcknowledged = errors; RenderHealth(); }, Passive: true)]);
        }
        if ((Application.Current as App)?.CrashedLastTime == true)
            Add("crash", HealthLevel.Notice, "Martlet closed unexpectedly last time",
                ErrorLog.PreviousCrash is { } crash
                    ? $"Windows recorded {crash}. Details are in the local log."
                    : "Details are in the local log if Martlet or Windows captured them.",
                [DiagnosticsPage(), Logs(), Dismiss("crash")]);

        // ---------- tiles ----------
        tiles.Add(JobTile("thinking", CompanionTab.Thinking, llm, thinkingDown, required: true,
            issues.Any(i => i.Level == HealthLevel.Problem && i.Id is "ollama" or "thinking-retired" or "docker")));
        tiles.Add(JobTile("listening", CompanionTab.Listening, stt, Down(ClusterJobs.Listening), required: false, false));
        tiles.Add(JobTile("voice", CompanionTab.Voice, tts, Down(ClusterJobs.Speaking), required: false, false,
            talk.SpeakReplies ? null : "replies aren't spoken"));
        var lipSyncDown = Down(ClusterJobs.LipSync);
        tiles.Add(new("lipsync", "Lip-sync", TabGlyph(CompanionTab.LipSync), lipSyncDown is null ? NodeHealth.Ready : NodeHealth.Attention,
            lipSyncDown is null ? "By " + LipSyncOwnerName() : "Not working: " + lipSyncDown.Problem, () => OpenCompanion(CompanionTab.LipSync)));
        tiles.Add(new("microphone", "Microphone", "\uE720",
            !(microphoneBlocked || micMissing) ? NodeHealth.Ready : stt is null ? NodeHealth.Unknown : NodeHealth.Attention,
            microphoneBlocked ? "Blocked by Windows"
                : micMissing ? (homeSettings?.Audio?.Input.EndpointId is null ? "None found" : "Chosen one isn't connected") +
                    (stt is null ? ". Listening is optional" : "")
                : homeSettings?.Audio?.Input.EndpointId is null ? "Windows default" : "Connected",
            () => OpenCompanion(CompanionTab.Listening)));
        tiles.Add(new("speakers", "Speakers", "\uE7F5", !speakersMissing ? NodeHealth.Ready : tts is null ? NodeHealth.Unknown : NodeHealth.Attention,
            speakersMissing ? "Chosen ones aren't connected" : homeSettings?.Audio?.Output.EndpointId is null ? "Windows default" : "Connected",
            () => OpenCompanion(CompanionTab.Voice)));
        tiles.Add(new("character", "Character", TabGlyph(CompanionTab.Character), webView2Missing ? NodeHealth.Attention : NodeHealth.Ready,
            webView2Missing ? "Needs the WebView2 runtime" : $"{CharacterModelName()}, {(avatar.IsShowing ? "on your desktop" : "hidden")}",
            () => OpenCompanion(CompanionTab.Character)));
        var others = hosts.Count(h => h.HostId != localHostId);
        tiles.Add(new("devices", "Devices", NetworkMap.ComputerGlyph,
            hostsDown > 0 || hostsOld > 0 ? NodeHealth.Attention : others == 0 || hostsAnswering < others ? NodeHealth.Unknown : NodeHealth.Ready,
            others == 0 ? "Only this PC" + (localHostId is null ? "" : " and its host service")
                : $"{hostsAnswering} of {others} other computer{(others == 1 ? "" : "s")} answering" +
                  (hostsDown > 0 ? $", {hostsDown} not" : hostsAnswering < others ? ", others not checked yet" : "") +
                  (hostsOld > 0 ? $", {hostsOld} need updates" : ""),
            () => Navigate(NavDevices)));
        if (toolServers.Count > 0 || mcpTools.ConfigurationError is not null)
        {
            var enabled = toolServers.Count(s => !s.Disabled);
            var failedTools = toolStatus.Count(s => s.State == Martlet.Mcp.Client.McpServerState.Failed);
            var ready = toolStatus.Count(s => s.State == Martlet.Mcp.Client.McpServerState.Ready);
            tiles.Add(new("tools", "Tools", TabGlyph(CompanionTab.Tools),
                mcpTools.ConfigurationError is not null || failedTools > 0 ? NodeHealth.Attention : ready > 0 ? NodeHealth.Ready : NodeHealth.Unknown,
                mcpTools.ConfigurationError is not null ? "Tool setup can't be read"
                    : failedTools > 0 ? $"{failedTools} of {enabled} server{(enabled == 1 ? "" : "s")} not working"
                    : ready > 0 ? $"{ready} of {enabled} server{(enabled == 1 ? "" : "s")} ready"
                    : $"{enabled} server{(enabled == 1 ? "" : "s")}, start when you talk",
                () => OpenCompanion(CompanionTab.Tools)));
        }
        tiles.Add(new("updates", "Updates", "\uE895",
            failedInstallMessage is not null ? NodeHealth.Attention : availableUpdate is not null || !updateChecksEnabled ? NodeHealth.Unknown : NodeHealth.Ready,
            failedInstallMessage is not null ? "Last install failed"
                : availableUpdate is { } next ? $"Martlet {next.Version.ToString(3)} available. You run {Version}"
                : !updateChecksEnabled ? $"Martlet {Version}. Automatic checks off"
                : $"Martlet {Version}",
            () => Navigate(NavSettings)));
        var appBroken = store is null || settingsBroken;
        tiles.Add(new("app", "This app", "\uE7B8", appBroken || errors > errorsAcknowledged ? NodeHealth.Attention : NodeHealth.Ready,
            store is null ? "Data folder unusable" : settingsBroken ? "Settings can't be read"
                : errors > 0 ? $"{errors} unexpected error{(errors == 1 ? "" : "s")} since it started" : "No errors since it started",
            () => Navigate(NavSettings)));

        issues.Sort((a, b) => a.Level.CompareTo(b.Level));
        return (issues, tiles);
    }

    private HealthTile JobTile(string id, CompanionTab tab, SetupRoute? route, JobCoverage? down, bool required, bool broken, string? note = null)
    {
        var title = TabTitle(tab);
        var (health, status) = down is not null ? (NodeHealth.Attention, "Not working: " + down.Problem)
            : route is null ? (required ? NodeHealth.Attention : NodeHealth.Unknown, required ? "Not set up. Martlet needs it" : "Not set up (optional)")
            : broken ? (NodeHealth.Attention, $"{PlaceName(route)}: {route.ModelId}, not working")
            : (NodeHealth.Ready, tab == CompanionTab.Voice ? PlaceName(route) + VoiceSuffix(route) : $"{PlaceName(route)}: {route.ModelId}");
        if (note is not null && route is not null) status += ". " + note;
        return new(id, title, TabGlyph(tab), health, status, () => OpenCompanion(tab));
    }

    private static string CoverageFixLabel(JobCoverage job, CoverageFix fix) => fix switch
    {
        CoverageFix.UseFallback => job.Job == ClusterJobs.LipSync ? "Take lip-sync back to this PC" : $"Use {job.Fallback} instead",
        CoverageFix.CheckHost => $"Check {job.HostId} now",
        CoverageFix.OpenSetup => $"Change {(job.Job == ClusterJobs.Speaking ? "voice" : job.Job)}",
        CoverageFix.InstallRole => $"Install {HostRoles.Get(ClusterSync.RoleKind(job.Job)).Name} on {job.HostId}",
        _ => "Open Devices"
    };

    // ---------- rendering ----------

    /// <summary>Rebuilds Home's attention list, Health tiles and hero from <see cref="BuildHealth"/>; skips the rebuild when
    /// nothing changed unless <paramref name="force"/>.</summary>
    private void RenderHealth(bool force = false)
    {
        if (closing || Role != DeviceRole.Companion) return;
        FollowLocalServices();
        var (issues, tiles) = BuildHealth();
        var signature = string.Join("\n", issues.Select(i => $"{i.Id}|{i.Level}|{i.Title}|{i.Detail}|{string.Join(",", i.Fixes.Select(f => f.Label))}")) +
            "\n--\n" + string.Join("\n", tiles.Select(t => $"{t.Id}|{t.Health}|{t.Status}"));
        if (!force && signature == healthSignature) return;
        var first = healthSignature is null;
        healthSignature = signature;
        RenderStage(issues);
        RenderIssues(issues, first);
        RenderTiles(tiles, first);
    }

    private void RenderStage(IReadOnlyList<HealthIssue> issues)
    {
        var top = issues.FirstOrDefault(i => i.Level == HealthLevel.Problem);
        var warnings = issues.Count(i => i.Level == HealthLevel.Warning);
        var llm = homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == SetupRole.Llm);
        AdvisorButton.Visibility = issues.Any(i => i.Id == "thinking-setup") ? Visibility.Visible : Visibility.Collapsed;
        if (top is not null)
        {
            StageTitle.Text = top.Headline ?? top.Title;
            StageText.Text = top.Headline is null || top.Headline == top.Title || top.Id == "thinking-setup" ? top.Detail : $"{top.Title}. {top.Detail}";
            var fix = top.Fixes.FirstOrDefault();
            stageFix = fix?.Run;
            PrimaryStageButton.Content = fix?.Label ?? "";
            PrimaryStageButton.Visibility = fix is null ? Visibility.Collapsed : Visibility.Visible;
            stageReady = false;
            RenderListening();
            return;
        }
        stageFix = null;
        PrimaryStageButton.Visibility = Visibility.Collapsed;
        stageReady = true;
        RenderListening();
        StageTitle.Text = warnings == 0 ? "Ready when you are" : $"Ready, with {warnings} thing{(warnings == 1 ? "" : "s")} to look at";
        StageText.Text = warnings == 0
            ? "Martlet is ready. Start listening to talk by voice, or start talking to chat by voice or text."
            : "Martlet can reply. Review the items below when you have time.";
    }

    private void RenderIssues(IReadOnlyList<HealthIssue> issues, bool first)
    {
        HealthIssues.Children.Clear();
        var problems = issues.Count(i => i.Level == HealthLevel.Problem);
        var warnings = issues.Count(i => i.Level == HealthLevel.Warning);
        var notices = issues.Count(i => i.Level == HealthLevel.Notice);
        HealthTitle.Text = problems + warnings == 0 ? "All good" : "Needs attention";
        var parts = new List<string>();
        if (problems > 0) parts.Add($"{problems} problem{(problems == 1 ? "" : "s")} stop{(problems == 1 ? "s" : "")} Martlet replying");
        if (warnings > 0) parts.Add($"{warnings} warning{(warnings == 1 ? "" : "s")}");
        if (notices > 0) parts.Add($"{notices} good to know");
        HealthSummary.Text = problems + warnings == 0
            ? "Nothing needs your attention." + (notices > 0 ? $" {notices} good to know below." : "")
            : string.Join(" · ", parts) + ".";

        var index = 0;
        if (problems + warnings == 0)
        {
            var clear = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 2, 0, 8) };
            var check = Glyph("\uE930", 18, new Thickness(0, 0, 10, 0));
            check.SetResourceReference(TextBlock.ForegroundProperty, "SuccessBrush");
            clear.Children.Add(check);
            var text = new TextBlock { Text = "Everything Martlet can check looks good.", VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetAutomationId(text, "HealthAllClear");
            clear.Children.Add(text);
            HealthIssues.Children.Add(clear);
            if (first) Motion.Enter(clear);
        }
        var noticeHeading = false;
        foreach (var issue in issues)
        {
            if (issue.Level == HealthLevel.Notice && !noticeHeading)
            {
                noticeHeading = true;
                var heading = new TextBlock { Text = "Good to know", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, problems + warnings == 0 ? 4 : 12, 0, 6) };
                heading.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
                HealthIssues.Children.Add(heading);
            }
            var row = IssueRow(issue);
            HealthIssues.Children.Add(row);
            if (first) Motion.Enter(row, delay: index++ * 50);
        }
    }

    private Border IssueRow(HealthIssue issue)
    {
        var (glyph, brush, word) = issue.Level switch
        {
            HealthLevel.Problem => ("\uEA39", "WarningBrush", "Problem"),
            HealthLevel.Warning => ("\uE7BA", "WarningBrush", "Warning"),
            _ => ("\uE946", "AccentBrush", "Good to know")
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var icon = Glyph(glyph, 18, new Thickness(0, 1, 12, 0));
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.SetResourceReference(TextBlock.ForegroundProperty, brush);
        grid.Children.Add(icon);

        var body = new StackPanel();
        var title = new TextBlock { Text = issue.Title, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(title, "HealthIssue-" + issue.Id);
        AutomationProperties.SetName(title, $"{word}: {issue.Title}. {issue.Detail}");
        body.Children.Add(title);
        var detail = new TextBlock { Text = issue.Detail, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
        detail.SetResourceReference(StyleProperty, "Muted");
        body.Children.Add(detail);
        if (issue.Fixes.Count > 0)
        {
            var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            var primary = issue.Level == HealthLevel.Problem;
            foreach (var fix in issue.Fixes)
            {
                var button = new Button { Content = fix.Label, Margin = new Thickness(0, 0, 8, 4), Padding = new Thickness(12, 6, 12, 6) };
                if (fix.Id == "dismiss") button.SetResourceReference(StyleProperty, "LinkButton");
                else if (primary) button.SetResourceReference(StyleProperty, "PrimaryButton");
                primary = false;
                AutomationProperties.SetAutomationId(button, $"{(fix.Passive ? "HealthOpen" : "HealthFix")}-{issue.Id}-{fix.Id}");
                AutomationProperties.SetName(button, $"{fix.Label}: {issue.Title}");
                var run = fix.Run;
                button.Click += (_, _) => run();
                actions.Children.Add(button);
            }
            body.Children.Add(actions);
        }
        Grid.SetColumn(body, 1);
        grid.Children.Add(body);

        var row = new Border
        {
            Child = grid, Padding = new Thickness(14, 12, 14, 12), CornerRadius = new CornerRadius(14), Margin = new Thickness(0, 0, 0, 8),
            BorderThickness = new Thickness(issue.Level == HealthLevel.Problem ? 1.5 : issue.Level == HealthLevel.Warning ? 1 : 0)
        };
        row.SetResourceReference(Border.BorderBrushProperty, "WarningBrush");
        row.SetResourceReference(Border.BackgroundProperty, issue.Level == HealthLevel.Notice ? "CanvasBrush" : "SoftBrush");
        AutomationProperties.SetName(row, $"{word}: {issue.Title}");
        return row;
    }

    private void RenderTiles(IReadOnlyList<HealthTile> tiles, bool first)
    {
        HealthChecks.Children.Clear();
        for (var i = 0; i < tiles.Count; i++)
        {
            var tile = tiles[i];
            var status = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            status.Children.Add(Dot(tile.Health, 8, new Thickness(0, 0, 6, 0)));
            var text = new TextBlock { Text = tile.Status, TextWrapping = TextWrapping.Wrap, FontSize = 12.5, MaxWidth = 170 };
            text.SetResourceReference(StyleProperty, "Muted");
            status.Children.Add(text);
            var content = new StackPanel();
            content.Children.Add(new TextBlock { Text = tile.Name, FontWeight = FontWeights.SemiBold, FontSize = 14 });
            content.Children.Add(status);
            var button = new Button { Content = content, Tag = tile.Glyph, Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 0, 10, 10) };
            button.SetResourceReference(StyleProperty, "CardButton");
            var state = tile.Health switch
            {
                NodeHealth.Ready => "OK",
                NodeHealth.Attention => "needs attention",
                _ => "not checked or not set up"
            };
            AutomationProperties.SetAutomationId(button, "HealthCheck-" + tile.Id);
            AutomationProperties.SetName(button, $"{tile.Name}: {state}. {tile.Status}");
            var open = tile.Open;
            button.Click += (_, _) => open();
            HealthChecks.Children.Add(button);
            if (first) Motion.Enter(button, delay: i * 40);
        }
    }
}
