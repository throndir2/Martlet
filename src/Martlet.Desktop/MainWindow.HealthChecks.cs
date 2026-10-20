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

/// <summary>Home's health checks: what is wrong, missing or worth knowing, each with its fixes, and a tile per part Martlet checks.</summary>
public partial class MainWindow
{
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
        // Add your key (FreeKeyPrompt): Companion › Thinking with NVIDIA Build ready for a free key.
        HealthFix FreeKeyFix(FreeKeyUse use) => new("free-key", FreeKeyPrompt.AddLabel, () => OpenFreeKey(use, fromReview: false), Passive: true);
        var offerKey = Role == DeviceRole.Companion && FreeKeyPrompt.Shows(ConfiguredProviders());

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
            // A new PC joins your other computers first: it then uses your hosts and the setup they share, with nothing to set
            // up here (Add a computer opens on Martlet on your network).
            var joinFirst = nothingYet && hosts.All(h => h.HostId == localHost?.HostId);
            // Not in a Martlet network: one click sets up Thinking, Listening and Voice to fit this PC.
            var alone = hosts.All(h => h.HostId == localHost?.HostId);
            Add("thinking-setup", HealthLevel.Problem, "Set up thinking",
                "Martlet needs a conversation model before it can reply" +
                (joinFirst ? "; if you already use Martlet on another computer, connect to it first to use the same setup."
                    : alone ? "; Set it all up for me picks a setup that fits this PC." : "; everything else is optional."),
                [.. alone ? [DefaultsFix()] : Array.Empty<HealthFix>(),
                 Open(CompanionTab.Thinking, "Set up thinking"),
                 .. joinFirst ? [ConnectComputersFix()] : Array.Empty<HealthFix>(),
                 .. offerKey ? [FreeKeyFix(FreeKeyUse.Thinking)] : Array.Empty<HealthFix>(),
                 new("advisor", "Get a recommendation", () => Advisor_Click(this, new RoutedEventArgs()))],
                nothingYet ? "Let's bring your companion to life" : "Set up thinking to start talking");
        }
        if (RetiredFallbackIssue(SavedModelAbilities()) is { } fallbackRetired && !dismissedHealth.Contains(fallbackRetired.Id)) issues.Add(fallbackRetired);
        if (llm is not null && Martlet.Conversation.ModelSenses.Retired(llm, SavedModelAbilities()) is { } retired)
            Add("thinking-retired", HealthLevel.Problem, "Your thinking model was retired",
                $"This model is no longer available{(retired.Since is { } since ? $" ({retired.Server} said so on {since.LocalDateTime:d MMM})" : "")}. " +
                RetiredProposal(llm.Origin, llm.ModelId, retired),
                RetiredThinkingFixes(llm, retired), "Martlet can't reply right now");
        else if (llm is not null && thinkingDown is null)
        {
            var model = llm.ModelId;
            switch (ollamaState)
            {
                case LocalOllamaState.NotInstalled:
                    Add("ollama", HealthLevel.Problem, "Ollama isn't installed on this PC",
                        $"Thinking uses {model} on this PC: install Ollama with that model.",
                        [new("install", $"Install Ollama and {model}", () => InstallThinkingOllamaAsync(model).Forget()),
                         Open(CompanionTab.Thinking, "Change thinking")], "Martlet can't reply right now");
                    break;
                case LocalOllamaState.NotRunning:
                    var ollamaTrouble = LocalOllamaRecovery.Last;
                    var repairing = LocalOllamaRecovery.Repairing;
                    var (ollamaTitle, ollamaDetail) = OllamaHealthText(model, ollamaTrouble, repairing, LocalOllamaRecovery.LastRepair);
                    var tried = LocalOllamaRecovery.LastRepair is { Repaired: false } failed && failed.After?.RepairKey == ollamaTrouble?.RepairKey;
                    Add("ollama", HealthLevel.Problem, ollamaTitle, ollamaDetail,
                        [.. ollamaTrouble is { Stops: true, CanRepair: true }
                            ? [new HealthFix("repair", repairing ? "Fixing Ollama..." : tried ? "Try the fix again" : "Fix Ollama's models folder",
                                () => RepairOllamaAsync(automatic: false).Forget(), Enabled: !repairing)]
                            : Array.Empty<HealthFix>(),
                         .. ollamaTrouble is { Kind: OllamaTroubleKind.CrashLoop } ? Array.Empty<HealthFix>() : [new HealthFix("start", "Start Ollama", StartOllama)],
                         .. ollamaTrouble is { Stops: true } ? [new HealthFix("ollama-logs", "Open Ollama's logs", OpenOllamaLogs)] : Array.Empty<HealthFix>(),
                         Open(CompanionTab.Thinking, "Change thinking")], "Martlet can't reply right now");
                    break;
                case LocalOllamaState.ModelMissing:
                    Add("ollama", HealthLevel.Problem, $"{model} isn't downloaded",
                        $"Thinking uses {model} on this PC, but it hasn't been downloaded yet.",
                        [new("download", $"Download {model}", () => PullThinkingModelAsync(model).Forget()), Open(CompanionTab.Thinking, "Change thinking")],
                        "Martlet can't reply right now");
                    break;
            }
            if (localServerProblem is { } serverProblem && IsLocalServer(llm))
                Add("local-model-app", HealthLevel.Problem, $"{Capitalized(LocalServerName(llm.Origin))} isn't ready on this PC", serverProblem,
                    [new("recheck", "Check again", () => CheckLocalServicesAsync().Forget()), Open(CompanionTab.Thinking, "Change thinking")],
                    "Martlet can't reply right now");
        }
        // Ollama on this PC kept stopping and Martlet fixed it: it changed Ollama's own settings, so it says what and where the
        // backup is.
        if (LocalOllamaRecovery.LastRepair is { Repaired: true, Changes.Count: > 0 } ollamaFixed)
            Add("ollama-fixed", HealthLevel.Notice, "Martlet fixed Ollama on this PC",
                ollamaFixed.Summary + " Each change is in the local log.", [Logs(), Dismiss("ollama-fixed")]);

        // Jobs that were chosen but don't work: from coverage (hosts, keys, consent, unsupported routes). Jobs this PC's own
        // host service can't do are folded into its own item below, which names the cause and the step that fixes it.
        var ownHostDown = localHost is not null && hostState is { Ready: false };
        foreach (var job in coverage.Where(c => c.IsProblem))
        {
            if (ownHostDown && job.OwnHost && job.Fixes.Contains(CoverageFix.RepairHostService)) continue;
            var thinking = job.Job == ClusterJobs.Thinking && job.State == CoverageState.Unavailable;
            var fixes = job.Fixes.Select(fix => new HealthFix(fix.ToString().ToLowerInvariant(), CoverageFixLabel(job, fix),
                () => RunCoverageFix(job, fix), Passive: fix is CoverageFix.OpenSetup or CoverageFix.OpenDevices)).ToList();
            // No hosted key yet: a free one answers through If Thinking fails while the computer that thinks is away.
            if (thinking && offerKey) fixes.Add(FreeKeyFix(FreeKeyUse.Fallback));
            Add("job-" + job.Job.Replace(' ', '-'), thinking ? HealthLevel.Problem : HealthLevel.Warning,
                job.State == CoverageState.Unavailable ? $"{job.Title} isn't working" : $"{job.Title} is working in a reduced way",
                (job.Problem + " " + job.Effect).Trim(), fixes,
                thinking ? JobCoverageRules.Headline(coverage) : null);
        }

        // This PC's own host service (a companion PC that also hosts), checked as the host dashboard checks a host PC's:
        // Docker Desktop, then the host service itself. What it does for this PC and your other computers stops meanwhile.
        if (ownHostDown && LocalHostService.Trouble(hostState!) is { } trouble)
        {
            var own = hostState!;
            var here = coverage.Where(c => c.IsProblem && c.OwnHost && c.Fixes.Contains(CoverageFix.RepairHostService)).ToList();
            var forOthers = clusterEnabled
                ? ClusterJobs.All.Where(j => clusterPlan.For(j) is { HostId: { } id, Off: false } && id == localHost!.HostId &&
                    here.All(c => c.Job != j)).ToList()
                : [];
            var stopsThinking = here.Any(c => c.Job == ClusterJobs.Thinking && c.State == CoverageState.Unavailable);
            var step = new[] { DockerStep(own), ServiceStep(own) }.FirstOrDefault(s => !s.Done);
            var detail = char.ToUpperInvariant(trouble[0]) + trouble[1..] + "." +
                string.Concat(here.Select(c => $" {c.Title}: {c.Effect}")) +
                (forOthers.Count > 0 ? $" Your other computers use it for {JoinNames(forOthers)} too." : "") +
                (here.Count == 0 && forOthers.Count == 0 ? " Nothing uses it right now." : "");
            Add("host-service", stopsThinking ? HealthLevel.Problem : here.Count > 0 || forOthers.Count > 0 ? HealthLevel.Warning : HealthLevel.Notice,
                "This PC's host service isn't working", detail,
                [.. (step?.Commands ?? []).Select((c, i) => new HealthFix("repair-" + i, c.Label, c.Run, Enabled: c.Enabled)),
                 new("check", "Check again", () => CheckOwnHostServiceAsync().Forget()),
                 new("show", "Show on the map", () => ShowDevice("this-pc"), Passive: true)],
                stopsThinking ? "Martlet can't reply right now" : null);
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
                $"Last attempt: {failure.Outcome}; details are in the local log.",
                [Open(tab, $"Check {label}"), Logs()]);
        }

        // Listening and its microphone.
        var standalone = hosts.All(h => h.HostId == localHost?.HostId);
        if (stt is null && store is not null && !settingsBroken)
            Add("listening-setup", HealthLevel.Notice, "Listening isn't set up",
                "Martlet hears you talk on your default microphone once listening is set up; you can always type instead.",
                [.. standalone ? [new HealthFix("defaults", "Set it up for this PC", () => SetUpDefaultsAsync(thinking: false, voice: false).Forget())]
                    : Array.Empty<HealthFix>(),
                 Open(CompanionTab.Listening, "Set up listening")]);
        var micMissing = AudioMissing(output: false);
        if (stt is not null && micMissing)
            Add("microphone", HealthLevel.Warning,
                homeSettings?.Audio?.Input.EndpointId is null ? "No microphone found" : "Your chosen microphone isn't connected",
                "Martlet can't hear you until a microphone is connected or another is chosen; you can still type.",
                [Open(CompanionTab.Listening, "Choose a microphone")]);
        if (stt is not null && microphoneBlocked)
            Add("microphone-blocked", HealthLevel.Warning, "Windows blocks the microphone",
                "Windows privacy settings stop desktop apps from using the microphone, so Martlet can't hear you.",
                [new("privacy", "Open microphone settings", OpenMicrophonePrivacy)]);

        // Voice and its speakers.
        if (tts is null && store is not null && !settingsBroken)
            Add("voice-setup", HealthLevel.Notice, "Voice isn't set up",
                "Martlet speaks its replies once a voice is set up; until then it replies in text.",
                [.. standalone ? [new HealthFix("defaults", "Set it up for this PC", () => SetUpDefaultsAsync(thinking: false, listening: false).Forget())]
                    : Array.Empty<HealthFix>(),
                 Open(CompanionTab.Voice, "Set up a voice")]);
        var speakersMissing = AudioMissing(output: true);
        if (tts is not null && speakersMissing)
            Add("speakers", HealthLevel.Warning, "Your chosen speakers aren't connected",
                "Martlet can't speak until they're back or others are chosen; replies still show as text.",
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

        // Vision: on, but with nothing it can use (Thinking is text-only and no image model of its own sees).
        var talk = Talk;
        var abilities = SavedModelAbilities();
        var image = llm is null ? null : SavedImageRoute(llm, abilities);
        if (talk.Watch && llm is not null && image is { Path: Martlet.Conversation.SensePath.None })
            Add("vision", HealthLevel.Warning, image.Model is null ? "Martlet can't see with your thinking model" : "Martlet can't see with your image model",
                LiveConversationConfiguration.VisionAdvice(llm, abilities, image), [Open(CompanionTab.Vision, "Open vision")]);
        else if (talk.Watch && VisionSource(talk) is { IsScreen: false, Id.Length: 0 })
            Add("vision-source", HealthLevel.Warning, "Vision has nothing to look at",
                "Watching is on with a camera, but no camera is chosen.", [Open(CompanionTab.Vision, "Choose a camera")]);

        // Paired computers: not answering or on an older Martlet (when no job problem above already names them).
        var hardware = HardwareStore?.Load() ?? [];
        var computers = OtherComputers();
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
            var name = NetworkMap.ComputerName(id, computers, hardware);
            HealthFix Show() => new("show", "Show on the map", () => ShowDevice("host:" + id), Passive: true);
            // A host device sync checks waits for the presence notice below (one missed check says nothing).
            if (reachable == false && !PresenceCovers(id))
                Add("host-" + id, HealthLevel.Warning, $"{name} isn't answering",
                    "It isn't handling any task right now: check that it is on and on your network.",
                    [new("check", $"Check {name} now", () => CheckHostsAsync([host]).Forget()), Show()]);
            else if (outdated)
                Add("host-update-" + id, HealthLevel.Warning, $"{name} runs an older Martlet",
                    $"It runs {reported ?? "an older version"}: update it to Martlet {Version}.",
                    [new("update", $"Update {name}", () => RunNodeAction(NodeAction.UpdateHost, id)), Show()]);
        }
        // Your other computers going away and coming back (MainWindow.NodePresence.cs).
        foreach (var issue in PresenceIssues()) Add(issue.Id, issue.Level, issue.Title, issue.Detail, issue.Fixes);

        // Tool servers.
        var toolServers = mcpTools.Servers;
        var toolStatus = mcpTools.Hub.Status;
        if (mcpTools.ConfigurationError is { } toolsProblem)
            Add("tools-config", HealthLevel.Warning, "Your tool servers can't start", toolsProblem, [Open(CompanionTab.Tools, "Open tools")]);
        foreach (var failed in toolStatus.Where(s => s.State == Martlet.Mcp.Client.McpServerState.Failed))
            Add("tools-" + failed.Name, HealthLevel.Warning, $"The {failed.Name} tool server isn't working",
                failed.Error ?? "It stopped or failed to start.", [Open(CompanionTab.Tools, "Open tools")]);

        // A better setup for your computers, found after one came back or stayed away (MainWindow.RecommendedSetup.cs).
        if (RecommendedSetupIssue() is { } better) Add(better.Id, better.Level, better.Title, better.Detail, better.Fixes);

        // Voice recognition on this PC stopped working (VoiceEngineKeeper): Martlet reloads it and tries again by itself.
        if (localVoices.RecognitionFailure is { } recognition)
            Add("voice-recognition", HealthLevel.Warning, "Voice recognition stopped working on this PC",
                recognition + (localVoices.RecognitionTargets.Any(t => !t.Here)
                    ? " Until then, the computers in your Voice recognition list recognize who is talking when they answer."
                    : " Until then, Martlet doesn't know who is talking."),
                [new("retry", "Try again now", () => RetryVoiceRecognitionAsync().Forget()), Open(CompanionTab.People, "Open People")]);

        // Updates.
        if (failedInstallMessage is { } installProblem)
            Add("update-failed", HealthLevel.Warning, "The last update didn't install", installProblem,
                [new("settings", "Open app updates", () => Navigate(NavSettings), Passive: true), Dismiss("update-failed")]);
        if (interruptedUpdateCleanup is { } cleanup)
            Add("update-cleanup", HealthLevel.Warning, "An update download wasn't cleaned up", cleanup,
                [new("settings", "Open app updates", () => Navigate(NavSettings), Passive: true)]);
        if (availableUpdate is { } update)
            Add("update", HealthLevel.Notice, $"Martlet {update.Version.ToString(3)} is available",
                $"You run {Version}; install the update when you're ready.",
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
                    ? $"Windows recorded {crash}; details are in the local log."
                    : "Details are in the local log if Martlet or Windows captured them.",
                [DiagnosticsPage(), Logs(), Dismiss("crash")]);

        // ---------- tiles ----------
        tiles.Add(JobTile("thinking", CompanionTab.Thinking, llm, thinkingDown, required: true,
            issues.Any(i => i.Level == HealthLevel.Problem && i.Id is "ollama" or "thinking-retired" or "host-service")));
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
                    (stt is null ? " (listening is optional)" : "")
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
        if (localHost is not null)
            tiles.Add(new("hostservice", "Host service", NetworkMap.ComputerGlyph,
                hostState is null ? NodeHealth.Unknown : hostState.Ready ? NodeHealth.Ready : NodeHealth.Attention,
                hostState is null ? "Checking this PC's host service..."
                    : HostHeadline(hostState) + (hostState.Ready && hostState.Version is { } running ? $", Martlet {running}" : ""),
                () => ShowDevice("this-pc")));
        var terminalOn = mcpTools.Terminal.Enabled;
        if (toolServers.Count > 0 || mcpTools.ConfigurationError is not null || terminalOn)
        {
            var enabled = toolServers.Count(s => !s.Disabled);
            var failedTools = toolStatus.Count(s => s.State == Martlet.Mcp.Client.McpServerState.Failed);
            var ready = toolStatus.Count(s => s.State == Martlet.Mcp.Client.McpServerState.Ready);
            var terminalNote = !terminalOn ? "" : toolServers.Count == 0 ? "Terminal on" : ", terminal on";
            tiles.Add(new("tools", "Tools", TabGlyph(CompanionTab.Tools),
                mcpTools.ConfigurationError is not null || failedTools > 0 ? NodeHealth.Attention : ready > 0 || terminalOn ? NodeHealth.Ready : NodeHealth.Unknown,
                mcpTools.ConfigurationError is not null ? "Tool setup can't be read" + (terminalOn ? "; terminal on" : "")
                    : toolServers.Count == 0 ? terminalNote
                    : (failedTools > 0 ? $"{failedTools} of {enabled} server{(enabled == 1 ? "" : "s")} not working"
                        : ready > 0 ? $"{ready} of {enabled} server{(enabled == 1 ? "" : "s")} ready"
                        : $"{enabled} server{(enabled == 1 ? "" : "s")}, start when you talk") + terminalNote,
                () => OpenCompanion(CompanionTab.Tools)));
        }
        tiles.Add(new("updates", "Updates", "\uE895",
            failedInstallMessage is not null ? NodeHealth.Attention : availableUpdate is not null || !updateChecksEnabled ? NodeHealth.Unknown : NodeHealth.Ready,
            failedInstallMessage is not null ? "Last install failed"
                : availableUpdate is { } next ? $"Martlet {next.Version.ToString(3)} available (you run {Version})"
                : !updateChecksEnabled ? $"Martlet {Version}, automatic checks off"
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
            : route is null ? (required ? NodeHealth.Attention : NodeHealth.Unknown, required ? "Not set up (Martlet needs it)" : "Not set up (optional)")
            : broken ? (NodeHealth.Attention, $"{PlaceName(route)}: {route.ModelId}, not working")
            : (NodeHealth.Ready, tab == CompanionTab.Voice ? PlaceName(route) + VoiceSuffix(route) : $"{PlaceName(route)}: {route.ModelId}");
        if (note is not null && route is not null) status += ". " + note;
        return new(id, title, TabGlyph(tab), health, status, () => OpenCompanion(tab));
    }

    private string CoverageFixLabel(JobCoverage job, CoverageFix fix) => fix switch
    {
        // Lip-sync "back to this PC" from this PC's own host service would be meaningless: it means without the host service.
        CoverageFix.UseFallback when job.Job == ClusterJobs.LipSync =>
            job.OwnHost ? "Do lip-sync without the host service" : "Take lip-sync back to this PC",
        CoverageFix.UseFallback => $"Use {job.Fallback} instead",
        CoverageFix.CheckHost => job.OwnHost ? "Check this PC's host service" : $"Check {job.HostName} now",
        CoverageFix.RepairHostService => OwnHostRepair()?.Label ?? "Fix this PC's host service",
        CoverageFix.OpenSetup => $"Change {(job.Job == ClusterJobs.Speaking ? "voice" : job.Job)}",
        CoverageFix.InstallRole => $"Install {HostRoles.Get(ClusterSync.RoleKind(job.Job)).Name} on {(job.OwnHost ? "this PC" : job.HostName)}",
        _ => "Open Devices"
    };
}
