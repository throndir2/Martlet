using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Avatar.Audio2Face;
using Martlet.Avatar.Hosting;
using Martlet.Core.Cluster;
using Martlet.Core.Installation;
using Martlet.Core.Platforms;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>What still works: each job's coverage from what this PC already knows (saved routes, the last host checks,
/// shared who-does-what and failover), shown at the top of Home and under the Devices map with the effect and a fix; and the
/// platform guardrails that keep impossible choices off the menus. Contacts nothing by itself.</summary>
public partial class MainWindow
{
    private IReadOnlyList<JobCoverage> coverage = [];
    private string? coverageSignature;

    /// <summary>Everything this PC knows about each job, in the order thinking, listening, speaking, lip-sync.</summary>
    private IReadOnlyList<JobSituation> JobSituations()
    {
        var hardware = HardwareStore?.Load() ?? [];
        var jobs = HostJob.All.Select(job => Situation(job, hardware)).ToList();
        jobs.Add(LipSyncSituation(hardware));
        return jobs;
    }

    private JobSituation Situation(HostJob job, IReadOnlyList<HostHardware> hardware)
    {
        var route = homeSettings?.Setup?.Routes.FirstOrDefault(r => r.Role == job.Role);
        if (route is null) return new() { Job = job.Job, Doer = JobDoer.NotChosen };
        var name = NetworkMap.ProviderName(route);
        var enabled = route.Enabled != false;
        var reviewed = route.Consent is not null && route.Consent == route.Selection();
        if (SelfHostSetup.IsGateway(route.RouteType) && route.Gateway is { } gateway)
        {
            var saved = store is null ? null : JobSavedRoute.Load(store.DataDirectory, job.SavedFile);
            return new()
            {
                Job = job.Job, Doer = JobDoer.Host, DoerName = gateway.HostId, Enabled = enabled, Reviewed = reviewed,
                Host = HostState(gateway.HostId, job.Job, job.RouteId, job.Engine, hardware,
                    paired: route.CredentialId is not null && route.GatewayDeviceId is not null),
                Fallback = saved is null ? null : SavedName(saved), FallbackIsCloud = saved is not null && IsCloud(saved),
                StandIn = job.Role == SetupRole.Stt && ListeningStandIn(route) is { } standIn ? ParakeetName(standIn) : null
            };
        }
        if (route.RouteType == SetupRouteType.LocalParakeet)
            return new()
            {
                Job = job.Job, Doer = JobDoer.ThisDevice, DoerName = name, Enabled = enabled, Reviewed = reviewed,
                NotConnected = store is not null && Martlet.Sherpa.ParakeetEngine.Installed(LocalVoices.SpeechRoot(store.DataDirectory), route.ModelId)
                    ? null
                    : $"{ParakeetName(route.ModelId)} isn't downloaded. Choose it again in Listening to download it"
            };
        if (route.RouteType is SetupRouteType.LocalWindowsStt or SetupRouteType.LocalWhisper)
            return new()
            {
                Job = job.Job, Doer = JobDoer.ThisDevice, DoerName = name, Enabled = enabled, Reviewed = reviewed,
                NotConnected = "this option can be saved, but conversations don't use it yet. Choose OpenAI or one of your hosts"
            };
        var keyMissing = route.CredentialId is null && (route.RouteType is null or SetupRouteType.OpenAi or SetupRouteType.ElevenLabs ||
            route.RouteType == SetupRouteType.ChatCompletions && ChatCompletionsEndpointCatalog.Named(route.Origin) is not null);
        return new()
        {
            Job = job.Job, Doer = JobDoer.Provider, DoerName = name, Enabled = enabled, Reviewed = reviewed, KeyMissing = keyMissing
        };
    }

    private JobSituation LipSyncSituation(IReadOnlyList<HostHardware> hardware) => NetworkMap.LipSync(homeAvatar) switch
    {
        LipSyncHandler.Loudness => new() { Job = ClusterJobs.LipSync, Doer = JobDoer.Nobody, DoerName = "Nobody" },
        LipSyncHandler.Host => new()
        {
            Job = ClusterJobs.LipSync, Doer = JobDoer.Host, DoerName = homeAvatar!.RemoteHost!.HostId,
            Host = HostState(homeAvatar.RemoteHost.HostId, ClusterJobs.LipSync, HostRoles.Get(HostRoles.Audio2Face).RouteId,
                "Audio2Face", hardware, paired: true),
            Fallback = "this PC"
        },
        _ => new() { Job = ClusterJobs.LipSync, Doer = JobDoer.ThisDevice, DoerName = "This PC" }
    };

    /// <summary>Whether this PC's own Audio2Face service answered its last check; null while lip-sync isn't handled by it or
    /// before the first check. Not a coverage problem: this PC's lip-sync uses that service only when one runs.</summary>
    private bool? ownLipSyncAnswers;
    private bool checkingOwnLipSync;

    private Uri OwnLipSyncEndpoint() => new(homeAvatar?.Endpoint ?? AvatarProfile.DefaultEndpoint);

    /// <summary>When lip-sync is handled by this PC's own Audio2Face service, checks that something answers on its loopback
    /// port (a 300 ms local connect; nothing is sent), so Home, Devices and Companion › Lip-sync say when nothing runs there.</summary>
    private async Task CheckOwnLipSyncAsync()
    {
        if (closing || checkingOwnLipSync) return;
        bool? answers = null;
        if (NetworkMap.LipSync(homeAvatar) == LipSyncHandler.ThisPc)
        {
            checkingOwnLipSync = true;
            try
            {
                answers = await Audio2FaceProbe.IsListeningAsync(new Audio2FaceOptions { Endpoint = OwnLipSyncEndpoint() },
                    TimeSpan.FromMilliseconds(300), lifetime.Token);
            }
            catch (Exception error) when (error is OperationCanceledException or ArgumentException or UriFormatException) { return; }
            finally { checkingOwnLipSync = false; }
        }
        if (closing || answers == ownLipSyncAnswers) return;
        ownLipSyncAnswers = answers;
        RenderHome();
        if (DevicesPage.IsVisible) RenderMap();
    }

    /// <summary>What this PC's own lip-sync does right now, for the places that show who handles lip-sync.</summary>
    private string OwnLipSyncState() => ownLipSyncAnswers switch
    {
        true => "Lifelike lip-sync is running on this PC.",
        false => "Lifelike lip-sync is not running on this PC. Set it up in Companion › Lip-sync.",
        _ => "Lifelike lip-sync when available. Otherwise, basic mouth movement."
    };

    /// <summary>The freshest thing this PC knows about a host: the background sync check when sync is on, otherwise the
    /// last Check connection. Null reachability means it has not been checked since Martlet started.</summary>
    private JobHostState HostState(string hostId, string job, string routeId, string engine, IReadOnlyList<HostHardware> hardware, bool paired)
    {
        var probe = clusterEnabled ? clusterProbes.GetValueOrDefault(hostId) : null;
        var check = hostChecks.GetValueOrDefault(hostId);
        bool? reachable = probe?.Reachable ?? check?.Reachable;
        var failover = clusterEnabled && clusterPlan.For(job)?.Failover == true;
        var own = hostId == ThisPcHost()?.HostId;
        return new()
        {
            HostId = hostId, Paired = paired && (FindHost(hostId) ?? FindSharedHost(hostId)) is not null, Reachable = reachable,
            Serves = HostServes(hostId, job, routeId),
            Engine = engine, SyncOn = clusterEnabled, Failover = failover,
            FailoverTarget = failover ? ClusterSync.FailoverTarget(clusterPlan, job, hostId, clusterProbes.Values, hardware) : null,
            ForegroundOnly = PlatformDevice.FromHost(hostId, hardware.FirstOrDefault(h => h.HostId == hostId)).ForegroundOnly,
            ThisPc = own, Trouble = own ? OwnHostTrouble() : null
        };
    }

    /// <summary>Why this PC's own host service can't work, from its last Docker reading, or null (ready, or not read yet).</summary>
    private string? OwnHostTrouble() => hostState is { } state ? LocalHostService.Trouble(state) : null;

    /// <summary>Whether a host runs a job's role, from the background sync check or the last Check connection; null when
    /// it has not answered a check since Martlet started.</summary>
    private bool? HostServes(string hostId, string job, string routeId)
    {
        var probe = clusterEnabled ? clusterProbes.GetValueOrDefault(hostId) : null;
        if (probe is not null) return probe.Serves(job);
        var check = hostChecks.GetValueOrDefault(hostId);
        return check is { Reachable: true } ? check.Routes?.Any(r => r.RouteId == routeId) ?? check.Offers?.ContainsKey(ClusterSync.RoleKind(job)) : null;
    }

    private IReadOnlyList<JobCoverage> EvaluateCoverage()
    {
        if (Role != DeviceRole.Companion) return coverage = [];
        coverage = JobSituations().Select(JobCoverageRules.Evaluate).ToArray();
        coverageSignature = string.Join("|", coverage.Select(c => $"{c.Job}:{c.State}:{c.Problem}"));
        return coverage;
    }

    /// <summary>Re-renders Home and Devices when a job's coverage changed (after a background check).</summary>
    private void RefreshCoverage()
    {
        if (closing || Role != DeviceRole.Companion) return;
        var before = coverageSignature;
        EvaluateCoverage();
        if (coverageSignature == before) return;
        RenderHome();
        if (DevicesPage.IsVisible) RenderMap();
    }

    /// <summary>The "What isn't working" card. On Devices (<paramref name="showNode"/> names the map node doing a job) it is
    /// compact: one line per job, its fixes and a button that selects that device; it also lists jobs not checked yet.</summary>
    private void ShowCoverage(Panel target, Func<string, string?>? showNode = null)
    {
        target.Children.Clear();
        var devices = showNode is not null;
        var problems = coverage.Where(c => c.IsProblem).ToList();
        var unknown = devices ? coverage.Where(c => c.State == CoverageState.Unknown).ToList() : [];
        if (problems.Count == 0 && unknown.Count == 0) return;
        var panel = new StackPanel();
        if (JobCoverageRules.Headline(coverage) is { } headline)
        {
            var title = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            var glyph = Glyph("\uE7BA", 16, new Thickness(0, 0, 8, 0));
            glyph.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
            title.Children.Add(glyph);
            title.Children.Add(new TextBlock { Text = headline, FontSize = 16, FontWeight = FontWeights.SemiBold });
            panel.Children.Add(title);
        }
        foreach (var job in problems.Concat(unknown)) panel.Children.Add(CoverageRow(job, showNode));
        var card = new Border
        {
            Child = panel, Padding = new Thickness(16, 12, 16, 12), CornerRadius = new CornerRadius(16),
            BorderThickness = new Thickness(problems.Count > 0 ? 1.5 : 1), Margin = new Thickness(0, 0, 0, devices ? 18 : 16)
        };
        card.SetResourceReference(Border.BorderBrushProperty, problems.Count > 0 ? "WarningBrush" : "BorderBrush");
        card.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        AutomationProperties.SetAutomationId(card, devices ? "DevicesCoverage" : "HomeCoverage");
        AutomationProperties.SetName(card, (JobCoverageRules.Headline(coverage) ?? "Jobs not checked yet") + ". " +
            string.Join(" ", problems.Concat(unknown).Select(c => $"{c.Title}: {c.Problem} {c.Effect}")));
        AutomationProperties.SetLiveSetting(card, AutomationLiveSetting.Polite);
        target.Children.Add(card);
    }

    private FrameworkElement CoverageRow(JobCoverage job, Func<string, string?>? showNode)
    {
        var compact = showNode is not null;
        var state = job.State switch
        {
            CoverageState.Unavailable => "not working",
            CoverageState.Limited => "working in a reduced way",
            _ => "not checked yet"
        };
        var actions = new WrapPanel { Margin = compact ? new Thickness(12, 0, 0, 0) : new Thickness(0, 6, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        foreach (var fix in job.Fixes)
        {
            if (fix == CoverageFix.OpenDevices && compact) continue;
            var label = CoverageFixLabel(job, fix);
            var button = new Button { Content = label, Margin = new Thickness(0, 0, 8, 4), Padding = new Thickness(12, 6, 12, 6) };
            // This PC's host service's repair is greyed out while a run already does it (starting Docker Desktop, for example).
            if (fix == CoverageFix.RepairHostService && OwnHostRepair() is { Enabled: false }) button.IsEnabled = false;
            AutomationProperties.SetAutomationId(button, $"CoverageFix-{job.Job}-{fix}");
            var current = job;
            button.Click += (_, _) => RunCoverageFix(current, fix);
            actions.Children.Add(button);
        }
        if (showNode?.Invoke(job.Job) is { } nodeId)
        {
            var show = new Button { Content = "Show", Margin = new Thickness(0, 0, 0, 4), Padding = new Thickness(12, 6, 12, 6) };
            AutomationProperties.SetAutomationId(show, $"CoverageShow-{job.Job}");
            AutomationProperties.SetName(show, $"Show the device doing {job.Job}");
            show.Click += (_, _) => SelectNode(nodeId, animate: true);
            actions.Children.Add(show);
        }

        if (compact)
        {
            var line = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
            DockPanel.SetDock(actions, Dock.Right);
            line.Children.Add(actions);
            var text = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            text.Inlines.Add(new System.Windows.Documents.Run($"{job.Title}: {state}") { FontWeight = FontWeights.SemiBold });
            var problem = new System.Windows.Documents.Run("  " + job.Problem);
            problem.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "MutedBrush");
            text.Inlines.Add(problem);
            line.Children.Add(text);
            return line;
        }

        var row = new StackPanel { Margin = new Thickness(0, 4, 0, 6) };
        row.Children.Add(new TextBlock { Text = $"{job.Title}: {state}", FontWeight = FontWeights.SemiBold });
        var detail = new TextBlock { Text = (job.Problem + " " + job.Effect).Trim(), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
        detail.SetResourceReference(StyleProperty, "Muted");
        row.Children.Add(detail);
        if (actions.Children.Count > 0) row.Children.Add(actions);
        return row;
    }

    private void RunCoverageFix(JobCoverage job, CoverageFix fix)
    {
        switch (fix)
        {
            case CoverageFix.UseFallback when job.Job == ClusterJobs.LipSync: AssignLipSyncAsync("this-pc").Forget(); break;
            case CoverageFix.UseFallback when HostJob.All.FirstOrDefault(j => j.Job == job.Job) is { } hostJob: AssignJobAsync(hostJob, "saved").Forget(); break;
            case CoverageFix.CheckHost when job.OwnHost: CheckOwnHostServiceAsync().Forget(); break;
            case CoverageFix.CheckHost when FindHost(job.HostId) is { } host: CheckHostsAsync([host]).Forget(); break;
            case CoverageFix.RepairHostService: OwnHostRepair()?.Run(); break;
            // Lip-sync re-checks the host and asks before installing Audio2Face; other roles install as on the Devices map.
            case CoverageFix.InstallRole when job.Job == ClusterJobs.LipSync: AssignLipSyncAsync("host:" + job.HostId).Forget(); break;
            case CoverageFix.InstallRole: RunHostRole($"{job.HostId}/{ClusterSync.RoleKind(job.Job)}", add: true); break;
            case CoverageFix.OpenSetup: OpenCompanion(TabFor(job.Job)); break;
            case CoverageFix.OpenDevices: Navigate(NavDevices); break;
        }
    }

    // ---------- platform guardrails ----------

    /// <summary>Whether a paired host can run a role's engine at all, from the hardware and platform it last reported;
    /// null for a role the catalog does not know.</summary>
    private PlatformCheck? HostCan(string hostId, string roleKind) =>
        PlatformCatalog.EngineForHostRole(roleKind) is { } engine
            ? PlatformCatalog.Check(engine, PlatformSide.Host, PlatformDevice.FromHost(hostId, HardwareStore?.Find(hostId)))
            : null;

    /// <summary>Why a job cannot be handed to a host that does not run its role yet, or null when Martlet may install it
    /// there (or cannot tell from what the host reported, and says so in the install confirmation).</summary>
    private string? CannotHand(string hostId, string roleKind, string job)
    {
        var device = PlatformDevice.FromHost(hostId, HardwareStore?.Find(hostId));
        if (!PlatformCatalog.ManagesRolesRemotely(device))
            return $"turn on {job} in Martlet on {hostId} first. This PC can't set it up there.";
        return HostCan(hostId, roleKind) is { Allowed: false } cannot ? cannot.Reason : null;
    }

    /// <summary>"Ollama qwen2.5:7b" on a Linux host; just the model on a Mac, phone or tablet, whose engine the model names.</summary>
    private string EngineLabel(string hostId, string engine, string model) =>
        PlatformDevice.FromHost(hostId, HardwareStore?.Find(hostId)).Platform == DevicePlatform.Linux ? $"{engine} {model}" : model;

    /// <summary>Notes to add when handing a job to a host, such as an iPhone that hosts only while Martlet is open on it.</summary>
    private string HostCaveats(string hostId) =>
        string.Concat(PlatformCatalog.HostNotes(PlatformDevice.FromHost(hostId, HardwareStore?.Find(hostId))).Select(note => " " + note));

    /// <summary>The shared job a host role does for this PC; null for a role that does none (Singing, Deep thinking).</summary>
    private static string? JobForRole(string roleKind) => roleKind switch
    {
        HostRoles.Ollama => ClusterJobs.Thinking,
        HostRoles.Stt => ClusterJobs.Listening,
        HostRoles.Audio2Face => ClusterJobs.LipSync,
        _ when HostRoles.Speaks(roleKind) => ClusterJobs.Speaking,
        _ => null
    };

    /// <summary>Removes a role from a host after saying what it takes away: the job moves by failover, goes back to its
    /// Setup choice first (named, with what it sends there) or nobody does it. Other computers following the shared plan
    /// are named too. A role that does no shared job says what it stops on this PC, if anything (Deep thinking there).</summary>
    private async Task RemoveHostRoleAsync(PairedHost host, HostRoleInfo role)
    {
        if (JobForRole(role.Kind) is not { } job)
        {
            var deep = store is null ? null : ThinkingPoolSettings.Load(store.DataDirectory).Places;
            if (role.Kind == HostRoles.DeepThinking && deep is { OnHostRole: true } && deep.HostId == host.HostId)
            {
                if (!ConfirmationDialog.Confirm(this, $"Remove {role.Name} from {host.HostId}? It is in this PC's Thinking pool, so " +
                        "pool jobs stop running there. Add another member in Companion > Thinking pool if the pool becomes empty.",
                        "Remove role"))
                    return;
                RunHostActionAsync(host, role.Remove, confirmed: true).Forget();
            }
            else LaunchOnHost(host, role.Remove);
            RenderHome();
            if (DevicesPage.IsVisible) RenderMap();
            return;
        }
        var situation = JobSituations().First(s => s.Job == job);
        var shared = clusterEnabled && clusterPlan.For(job)?.HostId == host.HostId;
        var lines = JobCoverageRules.RemoveRoleImpact(situation, host.HostId, shared);
        var handBack = JobCoverageRules.HandBackFirst(situation, host.HostId);
        if (lines.Count > 0 && !ConfirmationDialog.Confirm(this,
                $"Remove {role.Name} from {host.HostId}? " + string.Join(" ", lines),
                handBack ? "Hand back and remove" : "Remove role"))
            return;
        if (handBack && HostJob.All.First(j => j.Job == job) is var hostJob &&
            store is not null && JobSavedRoute.Load(store.DataDirectory, hostJob.SavedFile) is { } saved)
        {
            ChangeTurns.Turn? turn = null;
            try
            {
                turn = await ChangeTurnAsync();
                await HandBackAsync(hostJob, saved);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException or
                Martlet.Core.Contracts.ContractException or ArgumentException)
            {
                ActionText.Text = $"{role.Name} stayed on {host.HostId}. {hostJob.Job} couldn't move back first ({error.Message}).";
                return;
            }
            finally { turn?.Dispose(); }
        }
        LaunchOnHost(host, role.Remove);
        RenderHome();
        if (DevicesPage.IsVisible) RenderMap();
    }
}
