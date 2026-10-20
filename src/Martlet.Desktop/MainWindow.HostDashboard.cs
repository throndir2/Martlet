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

// The app shell's host dashboard (see MainWindow.Shell.cs for the shell's shared state).
public partial class MainWindow
{
    private HostSetupTarget ThisPcTarget() => new(HostSetupMethod.ThisPcDocker, "",
        machine.LanAddress ?? HostSetupCommands.ThisPcAddress() ?? "", HostSetupCommands.SuggestedHostId(Environment.MachineName), Version);

    private void RenderHost()
    {
        if (Role != DeviceRole.Host) return;
        var state = hostState;
        var lan = machine.LanAddress ?? HostSetupCommands.ThisPcAddress();
        HostAddressText.Text = state?.Address is { } published
            ? $"https://{published}" + (state.PublishedEverywhere == true ? " (and this PC's other addresses, for outside home)" : "")
            : lan is not null ? $"https://{lan}:{WindowsFirewall.Port}" : "No home network address found yet";
        HostStatusText.Text = HostHeadline(state);
        if (state?.Ready == true) Motion.PulseRing(HostPulse, to: 1.35);
        else
        {
            HostPulse.BeginAnimation(OpacityProperty, null);
            HostPulse.Opacity = 0;
        }
        var setUp = state?.Stage is LocalHostServiceStage.Stopped or LocalHostServiceStage.Running;
        var steps = new List<HomeStep> { DockerStep(state), ServiceStep(state), PairStep(state), RolesStep(state), UpdateStep(state, setUp) };
        // Your other computers asking to join the network show right under pairing: this PC may be the only one that can
        // let them in (it started the network while it was a companion PC).
        var joinAt = steps.FindIndex(s => s.Id == "pair") + 1;
        foreach (var join in networkJoins.Reverse())
            steps.Insert(joinAt, new($"join-{join.DeviceId}", $"Let {join.DisplayName} into your Martlet network",
                $"{join.DisplayName} ({join.DeviceId}) is paired with {join.HostId} and asks to join to use all your hosts: allow it " +
                $"only if that computer shows check number {join.CheckNumber}.",
                false, false, [new("Allow", () => AllowJoin(join), true), new("Turn down", () => DenyJoinAsync(join).Forget())]));
        RenderSteps(HostStepsPanel, steps, numbered: true);

        var left = steps.Where(s => !s.Optional && !s.Done).ToList();
        HostStepsHeading.Text = left.Count == 0 && state is not null ? "This host is ready" : "Get this host running";
        var checkedAt = hostProbedAt == default ? "" : $", last checked {hostProbedAt.ToLocalTime():t}";
        HostStepsSummary.Text = state is null ? "Checking this PC's host service..."
            : left.Count == 0
                ? "All set: the host service is running and paired" +
                  (state.Roles is { Count: 0 } ? "; add a role so it has work to do" : "") +
                  $"; Martlet checks it every {HostProbeInterval.TotalSeconds:0} seconds{checkedAt}."
                : $"{left.Count} step{(left.Count == 1 ? "" : "s")} left, next: {left[0].Title}; steps tick by themselves, and " +
                  $"Martlet checks every {HostProbeInterval.TotalSeconds:0} seconds{checkedAt}.";
        ShowHostRuns();
    }

    /// <summary>The setup runs working now under the host dashboard's steps, each with its status line ("Start Docker Desktop:
    /// Waiting for Docker Desktop to start..."). They run side by side: what they share waits for the run doing it.</summary>
    private void ShowHostRuns()
    {
        if (closing || Role != DeviceRole.Host) return;
        var runs = HostRunWindow.Running;
        HostRunsText.Visibility = runs.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        HostRunsText.Text = runs.Count == 0 ? ""
            : (runs.Count == 1 ? "Working now: " : $"{runs.Count} runs working side by side: ") +
              string.Join(" · ", runs.Select(run => run.CurrentStatus.Length == 0 ? run.Heading : $"{run.Heading}: {run.CurrentStatus}"));
    }

    /// <summary>Docker Desktop is being installed, Windows readied for it or it is being started, by a run working now.</summary>
    private static bool DockerUnderway => DockerWorkNow() is not null;

    /// <summary>The Docker Desktop step's greyed-out label for what the runs working now do with Docker Desktop, or null when
    /// none does (<see cref="DockerWorkLabel"/>).</summary>
    private static string? DockerWorkNow() =>
        DockerWorkLabel(SharedSteps.Now().Select(step => step.Key), HostRunWindow.IsRunningTitled(HostsWindow.InstallDockerTitle));

    /// <summary>What the Docker Desktop step's button says, greyed out, while runs work on Docker Desktop: installing it
    /// (<paramref name="installing"/>: the Install Docker Desktop run works), starting it, or getting Windows ready for it.
    /// A start checks Windows too, so that check doesn't change the label. <paramref name="steps"/> are the keys of the
    /// <see cref="SharedSteps"/> running now. Null when no run works on Docker Desktop.</summary>
    internal static string? DockerWorkLabel(IEnumerable<string> steps, bool installing)
    {
        var running = steps.ToHashSet(StringComparer.Ordinal);
        return installing || running.Contains(SharedSteps.DockerInstall) ? "Installing Docker Desktop..."
            : running.Contains(SharedSteps.DockerStart) ? "Starting Docker Desktop..."
            : running.Contains(SharedSteps.WindowsReady) ? "Getting Windows ready..."
            : null;
    }

    /// <summary>What the runs working on Docker Desktop are doing now ("\"Start Docker Desktop\" is starting Docker Desktop"),
    /// or null when none is.</summary>
    private static string? DockerWork()
    {
        var working = SharedSteps.Now().Where(step => step.Key is SharedSteps.DockerInstall or SharedSteps.WindowsReady or SharedSteps.DockerStart)
            .Select(step => $"\"{step.By}\" is {step.Doing}").ToList();
        if (working.Count == 0 && HostRunWindow.IsRunningTitled(HostsWindow.InstallDockerTitle)) working.Add($"\"{HostsWindow.InstallDockerTitle}\" is working");
        return working.Count == 0 ? null : string.Join("; ", working);
    }

    private string HostHeadline(LocalHostServiceState? state) => state?.Stage switch
    {
        null => "Checking...",
        LocalHostServiceStage.DockerMissing => "Needs Docker Desktop",
        LocalHostServiceStage.DockerNotRunning when virtualization?.RestartRequired == true => "Needs Windows restart",
        LocalHostServiceStage.DockerNotRunning when virtualization?.Blocked == true => "Windows isn't ready for Docker Desktop",
        LocalHostServiceStage.DockerNotRunning => "Waiting for Docker Desktop",
        LocalHostServiceStage.NotSetUp => "Not set up yet",
        LocalHostServiceStage.Stopped => "Host service stopped",
        _ when state.AddressOnThisPc == false => HostMoving ? "Moving to the new address" : "Address changed",
        _ when state.Answering != true => "Not answering yet",
        _ => "Host is running"
    };

    private HomeStep DockerStep(LocalHostServiceState? state)
    {
        var installed = state is null ? machine.DockerInstalled : state.Stage != LocalHostServiceStage.DockerMissing;
        var done = state is not null && state.Stage is not (LocalHostServiceStage.DockerMissing or LocalHostServiceStage.DockerNotRunning);
        var windowsBlocks = !done && installed && virtualization?.Blocked == true;
        var engineWaiting = machine.DockerRunning && state?.Stage == LocalHostServiceStage.DockerNotRunning;
        var underway = done ? null : DockerWork();
        // While a run works on Docker Desktop (for example the one a new host PC starts by itself), the step's button says what
        // that run does and stays greyed out until it ends, so nobody starts Docker Desktop a second time.
        var working = done ? null : DockerWorkNow();
        var action = virtualization switch
        {
            { RestartRequired: true } => "Restart Windows",
            { FirmwareOff: true, VirtualMachine: false } => "Turn on virtualization",
            { NeedsChanges: true } => "Turn on Windows features",
            _ => "Review Windows setup"
        };
        StepCommand next = windowsBlocks ? new(action, PrepareWindows, true)
            : installed ? new("Start Docker Desktop", StartDocker, true)
            : new("Install Docker Desktop", InstallDocker, true);
        return new("docker", "Docker Desktop",
            done ? "Running."
                : underway is not null ? underway + "; other setup can go ahead meanwhile."
                : windowsBlocks ? string.Join("; ", virtualization!.Problems()) + ". " + virtualization.Recovery
                : engineWaiting ? "Docker Desktop is open, but its engine isn't answering yet; the first start can take a few minutes."
                : state is null && installed ? "Checking Docker Desktop's engine..."
                : installed ? "Installed, but not running." : "Required for the host service.",
            done, false,
            done ? [] : [working is null ? next : next with { Label = working, Enabled = false }],
            StepTip("Docker Desktop", "The host service runs in Docker Desktop. Its first start can take a few minutes; you don't have to " +
                "wait, because steps that need it carry on once it runs.", "HostDocker", wiki: "Setting-Up-a-Host#windows-with-docker-desktop"));
    }

    private HomeStep ServiceStep(LocalHostServiceState? state)
    {
        StepCommand SetUp(string label, bool primary = true) => new(label, () => SetUpHostServiceAsync().Forget(), primary);
        // While Docker Desktop is still being installed or started, a host service Martlet hasn't seen set up can be set up
        // already: its run waits for Docker Desktop and carries on as soon as it runs.
        var early = state?.Stage is LocalHostServiceStage.DockerMissing or LocalHostServiceStage.DockerNotRunning && DockerUnderway &&
            ThisPcHost() is null && thisPcHostVersion is null;
        (string Detail, StepCommand[] Commands) step = state?.Stage switch
        {
            null => ("Checking this PC's host service...", Array.Empty<StepCommand>()),
            LocalHostServiceStage.DockerMissing or LocalHostServiceStage.DockerNotRunning when early =>
                ("Runs in Docker Desktop; set it up now and it carries on as soon as Docker Desktop runs.",
                 [SetUp("Set up host service", primary: false)]),
            LocalHostServiceStage.DockerMissing or LocalHostServiceStage.DockerNotRunning =>
                ("Runs in Docker Desktop; Martlet checks it again once Docker Desktop is running.", []),
            LocalHostServiceStage.NotSetUp =>
                ("Not set up yet; Windows may ask to allow private-network access.", [SetUp("Set up host service")]),
            LocalHostServiceStage.Stopped => ("Set up, but not running: start it again.", [SetUp("Start host service")]),
            _ when state.AddressOnThisPc == false && HostMoving =>
                ("This PC's network address changed since setup, so Martlet is moving the host service to the new address now.", []),
            _ when state.AddressOnThisPc == false =>
                ("This PC's network address changed and Martlet couldn't move the host service by itself (Background tasks says " +
                 "why): set it up again for the new address.", [SetUp("Set up again")]),
            _ when state.Answering != true =>
                ("Running, but not answering on your network yet; if this lasts, set it up again.", [SetUp("Set up again", primary: false)]),
            _ => ($"Running and reachable on your network{(state.HostId is { } id ? $" as {id}" : "")}.", [])
        };
        // A setup run working now says so (pressing its button again brings that run's window forward).
        if (state?.Ready != true && HostRunWindow.IsRunningTitled(HostActions.ThisPcSetupTitle))
            step.Detail = $"Being set up now (\"{HostActions.ThisPcSetupTitle}\"). " + step.Detail;
        return new("service", "Host service", step.Detail, state?.Ready == true, false, step.Commands,
            StepTip("the host service", "The host service does the work your other computers send to this PC. Starting it or setting it " +
                "up again keeps its pairings and roles. Windows may ask to allow private-network access when you set it up.",
                "HostService", wiki: "Setting-Up-a-Host#windows-with-docker-desktop"));
    }

    /// <summary>The other computers paired with this PC's host service, as it said on the last network sync: members of its
    /// network or not (such as one still waiting to join), never a friend's computer. Empty when this PC isn't paired with its
    /// own host service, or before the first sync.</summary>
    private IReadOnlyList<HostPairedDevice> PairedComputers() =>
        ThisPcHost() is { } own && PairedWith(own.HostId) is { } devices ? devices.Where(d => !IsThisDevice(d.DeviceId) && !d.Friend).ToArray() : [];

    private HomeStep PairStep(LocalHostServiceState? state)
    {
        var self = NetworkIdentity.DeviceId(homeHosts);
        var desktops = state?.Desktops ?? [];
        // The host's network members, and every computer the host service reports as paired with it.
        var now = DateTimeOffset.UtcNow;
        var reported = PairedComputers();
        var others = desktops.Where(d => d.Id != self && !IsThisDevice(d.Id)).Select(d => (d.Id, d.Name))
            .Concat(reported.Select(d => (Id: d.DeviceId, Name: d.DisplayName)))
            .DistinctBy(d => d.Id, StringComparer.Ordinal)
            .Select(d => reported.FirstOrDefault(r => r.DeviceId == d.Id) is { } seen ? $"{d.Name} ({Seen(seen.LastSeen, now)})" : d.Name)
            .ToList();
        var thisPc = desktops.Any(d => d.Id == self) || homeHosts.Any(h => h.Method == HostSetupMethod.ThisPcDocker);
        var justPaired = hostPairedAt is { } at && DateTime.UtcNow - at < TimeSpan.FromMinutes(5);
        var paired = others.Count > 0 || thisPc || justPaired;
        var help = StepTip("pairing", "On your main PC, choose Devices > Add a computer: this PC is listed under Martlet on your network. " +
            "Press Connect there, then Allow here when both show the same check number. Or show a pairing code here and type it there " +
            "(Enter a pairing code).", "HostPair", wiki: "Setting-Up-a-Host#pairing");
        // Who is paired is read from the running host service; until it runs there is nothing to pair with.
        if (!paired && state?.Stage != LocalHostServiceStage.Running)
            return new("pair", "Pair your main PC", state?.Stage switch
            {
                null => "Checking which computers are paired...",
                LocalHostServiceStage.NotSetUp => "Set up the host service first, then pair your main PC here.",
                _ => "Martlet sees which computers are paired once the host service runs."
            }, false, false, [], help);
        var detail = others.Count > 0 ? $"Paired with {JoinNames(others)}{(thisPc ? " and this PC" : "")}."
            : thisPc ? "Paired with this PC; to use this host from another computer too, choose Devices > Add a computer there."
            : justPaired ? "Paired; your main PC shows here by name once the host service reports it."
            : "On your main PC, choose Devices > Add a computer and connect to this PC, then press Allow here when both show the " +
              "same check number.";
        if (NearbyBlocked)
            detail += " Windows Firewall doesn't let your other computers find this PC yet; Let my other computers find this PC " +
                "fixes that (administrator approval once).";
        var pair = new StepCommand(paired ? "Pair another computer" : "Show a pairing code", () => LaunchHost(HostAction.Pair), !paired);
        return new("pair", "Pair your main PC", detail, paired, false, NearbyBlocked
            ? [pair, new("Let my other computers find this PC", () => NearbyFirewall_Click(this, new RoutedEventArgs()))]
            : [pair], help);
    }

    private HomeStep RolesStep(LocalHostServiceState? state)
    {
        var nvidia = machine.BestGpu is { IsNvidia: true } gpu ? $"; this PC has {gpu.Describe()}"
            : machine.ProcessorType.WindowsOnArm ? "; NVIDIA graphics cards don't work on this Windows on Arm PC, so GPU roles need another computer"
            : "; no NVIDIA graphics card found";
        var help = StepTip("roles", "Roles are the jobs this host does for your computers, such as Thinking, Listening or lip-sync. Add " +
            "or remove them any time.", "HostRoles", wiki: "Setting-Up-a-Host#choose-the-hosts-jobs");
        // Roles are read from the running host service; until it runs there is nothing to add them to.
        if (state?.Roles is not { } installed)
            return new("roles", "Add roles", "Add tasks this host can handle once the host service runs" + nvidia + ".", false, true, [], help);
        string Name(string kind) => HostRoles.All.FirstOrDefault(r => r.Kind == kind)?.Name ?? kind;
        // Jobs your Martlet network gives this PC (it was set up to do them, as a companion or a host PC) need their roles here.
        var needed = clusterEnabled && state.HostId is { } self
            ? ClusterJobs.All.Where(j => clusterPlan.For(j)?.HostId == self).Select(j => (Job: j, Kind: ClusterSync.RoleKind(j)))
                .Where(n => !installed.Contains(n.Kind, StringComparer.Ordinal)).ToList()
            : [];
        var nvidiaCards = machine.Gpus.Count(g => g.IsNvidia);
        var missing = HostRoles.All.Where(r => !installed.Contains(r.Kind, StringComparer.Ordinal) && HostRoles.OfferedOn(r, nvidiaCards))
            .OrderBy(r => needed.Any(n => n.Kind == r.Kind) ? 0 : 1).ToList();
        IReadOnlyList<StepCommand> commands =
        [
            .. missing.Select((r, i) => new StepCommand($"Add {r.Name}", () => LaunchHost(r.Add),
                needed.Any(n => n.Kind == r.Kind) || installed.Count == 0 && i == 0)),
            .. HostRoles.All.Where(r => installed.Contains(r.Kind, StringComparer.Ordinal))
                .Select(r => new StepCommand($"Remove {r.Name}", () => LaunchHost(r.Remove)))
        ];
        var asked = needed.Count == 0 ? ""
            : $"Your computers use this PC for {JoinNames([.. needed.Select(n => n.Job)])}, so add " +
              $"{JoinNames([.. needed.Select(n => Name(n.Kind))])} here; until then they keep what they use now. ";
        return new("roles", "Add roles",
            asked + (installed.Count == 0 ? "No roles yet: add tasks this host can handle" + nvidia + "."
                : $"Runs {JoinNames([.. installed.Select(Name)])}."),
            installed.Count > 0 && needed.Count == 0, true, commands, help);
    }

    private HomeStep UpdateStep(LocalHostServiceState? state, bool setUp)
    {
        var update = new StepCommand("Update host service", () => LaunchHost(HostAction.Update));
        var help = StepTip("host updates", "Martlet keeps the host service on this app's version by itself. An update keeps its pairings " +
            "and roles.", "HostUpdate", wiki: "Setting-Up-a-Host#updates");
        if (!setUp)
            return new("update", "Keep it up to date", state?.Stage switch
            {
                null => "Checking the host service's version...",
                LocalHostServiceStage.NotSetUp => $"Once it's set up, the host service runs this app's version (Martlet {Version}).",
                _ => "Martlet checks the host service's version once Docker Desktop is running."
            }, false, true, [], help);
        if (state!.Version is not { } running)
            return new("update", "Keep it up to date", $"Updates the host service to Martlet {Version}.", false, true, [update], help);
        if (!AppVersions.IsOlder(running, Version))
            return new("update", "Keep it up to date", $"Up to date: the host service runs Martlet {running}.", true, true, [], help);
        if (hostUpdates.IsUpdating(ThisPcHostId))
            return new("update", "Keep it up to date",
                $"Updating the host service from Martlet {running} to {Version}.", false, true, [], help);
        return new("update", "Keep it up to date",
            $"The host service runs Martlet {running}; Martlet updates it to {Version} by itself " +
            (state.Stage == LocalHostServiceStage.Running ? "in the background" : "once it runs again") +
            ", or Update host service does it now.", false, true, [update with { Primary = true }], help);
    }

    /// <summary>"A", "A and B", "A, B and C", "A, B, C and 2 more".</summary>
    private static string JoinNames(IReadOnlyList<string> names) => names.Count switch
    {
        0 => "",
        1 => names[0],
        <= 3 => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1],
        _ => string.Join(", ", names.Take(3)) + $" and {names.Count - 3} more"
    };

    /// <summary>Reads this PC's host service from Docker (<see cref="LocalHostService"/>) and shows it on the host dashboard. A
    /// call while a read runs returns that read, which then reads once more so it sees what just changed.</summary>
    private Task CheckThisPcHostAsync()
    {
        if (hostProbe is { IsCompleted: false } running)
        {
            hostProbeAgain = true;
            return running;
        }
        return hostProbe = ProbeThisPcHostAsync();
    }

    private async Task ProbeThisPcHostAsync()
    {
        do
        {
            hostProbeAgain = false;
            if (closing || !HostsHere) return;
            LocalHostServiceState state;
            try
            {
                state = await LocalHostService.ProbeAsync(lifetime.Token);
                // An open Docker Desktop window is not a working engine. Diagnose Windows even while that window is open.
                virtualization = state.Stage == LocalHostServiceStage.DockerNotRunning
                    ? await WindowsVirtualization.ProbeAsync(lifetime.Token) : null;
            }
            catch (OperationCanceledException) { return; }
            if (closing) return;
            ApplyHostState(state);
        } while (hostProbeAgain);
    }

    private void ApplyHostState(LocalHostServiceState state)
    {
        var wasReady = hostState?.Ready == true;
        hostState = state;
        hostProbedAt = DateTime.UtcNow;
        RememberThisPcHostRoles(state);
        var before = thisPcHostVersion;
        if (state.Stage is LocalHostServiceStage.Stopped or LocalHostServiceStage.Running) thisPcHostVersion = state.Version;
        else if (state.Stage == LocalHostServiceStage.NotSetUp) thisPcHostVersion = null;
        var self = NetworkIdentity.DeviceId(homeHosts);
        if (state.Desktops.Any(d => d.Id != self)) hostPairedAt = null;
        var signature = $"{state.Stage}|{state.Ready}|{state.Version}|{virtualization?.Describe()}|{string.Join(",", state.Roles ?? [])}|" +
            string.Join(",", state.Desktops.Select(d => d.Id));
        if (signature != hostStateSignature)
        {
            hostStateSignature = signature;
            ErrorLog.Info($"This PC's host service: {HostHeadline(state)} (stage {state.Stage}, version {state.Version ?? "-"}, " +
                $"roles {(state.Roles is { } roles ? roles.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) : "unknown")}, " +
                $"network {state.Network ?? "unknown"} with {state.Desktops.Count} desktop(s))" +
                (state.Problem is { } problem ? $": {problem}" : "."));
        }
        if (before != thisPcHostVersion) UpdateNearby();
        // This PC got another address: publish the host service there by itself.
        FollowThisPcAddress(state);
        // An update that finished by any route (or one that reported busy against Martlet's own run) leaves no stale note.
        HostFoundCurrent(ThisPcHostId, thisPcHostVersion);
        RenderHost();
        UpdateStayAwake();
        // A companion PC that also hosts shows its host service on Home and the Devices map, and in each job it does.
        if (Role == DeviceRole.Companion)
        {
            // Back up (Docker Desktop or the host service started): the jobs it does check it again rather than wait.
            if (state.Ready && !wasReady && ThisPcHost() is { } own && hostChecks.GetValueOrDefault(own.HostId)?.Reachable == false)
                CheckHostsAsync([own]).Forget();
            RefreshCoverage();
            RenderHealth();
            RenderDevicePages();
        }
    }

    /// <summary>What the last read of this PC's host service means, as one sentence for the status line.</summary>
    private string HostServiceSentence(LocalHostServiceState state) => state.Stage switch
    {
        LocalHostServiceStage.DockerMissing => "Docker Desktop isn't installed: install it first (step 1).",
        LocalHostServiceStage.DockerNotRunning when virtualization?.Blocked == true =>
            string.Join("; ", virtualization.Problems()) + ". " + virtualization.Recovery,
        LocalHostServiceStage.DockerNotRunning => machine.DockerRunning
            ? "Docker Desktop is open, but its engine isn't answering yet, so the host service can't run; the first start can take a few minutes."
            : "Docker Desktop isn't running yet, so the host service can't run: start Docker Desktop (step 1).",
        LocalHostServiceStage.NotSetUp => "The host service isn't set up yet: use Set up host service.",
        LocalHostServiceStage.Stopped => "The host service is set up but stopped: use Start host service (pairings and roles stay).",
        _ when state.AddressOnThisPc == false && HostMoving => "This PC's network address changed since setup, so Martlet is moving the host service there now.",
        _ when state.AddressOnThisPc == false => "This PC's network address changed since setup: use Set up again (pairings and roles stay).",
        _ when state.Answering != true => "The host service runs but isn't answering on your network yet: check again in a moment.",
        _ => "The host service is running and reachable on your network" + (PairStep(state).Done ? "." : "; pair your main PC next.")
    };

    /// <summary>Runs a host-dashboard step on this PC's host service in a run window (never a console). Pairing shows the
    /// one-use code for the main PC. Steps run side by side: changes to the host service run side by side in its engine,
    /// where only colliding ones wait (a run waiting for another says so in its window), what they share (starting Docker Desktop, the host image) is done
    /// once, and pressing a step that is still working brings its window forward.</summary>
    private void LaunchHost(HostAction action) => LaunchHostAsync(action).Forget();

    private async Task LaunchHostAsync(HostAction action)
    {
        if (closing || store is null) return;
        // Martlet's automatic host update leaves this PC's host service to this run rather than colliding with it.
        using var updating = action.Verb == HostVerb.Update ? hostUpdates.Begin(ThisPcHostId) : null;
        try
        {
            ActionText.Text = "Running host setup; the progress window shows the details.";
            var done = action.Verb == HostVerb.Pair
                ? await HostActions.PairOtherDesktopAsync(this, ThisPcTarget())
                : await HostActions.RunAsync(this, store.DataDirectory, ThisPcTarget(), null, action);
            if (closing) return;
            if (done is not null && action.Verb == HostVerb.Update) HostUpdateSettled(ThisPcHostId);
            ActionText.Text = done ?? "Stopped; see the progress window for details.";
            if (done is not null && action.Verb == HostVerb.Pair) hostPairedAt = DateTime.UtcNow;
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException or
            System.ComponentModel.Win32Exception)
        {
            ActionText.Text = error.Message;
        }
        finally
        {
            if (!closing)
            {
                RenderHost();
                CheckThisPcHostAsync().Forget();
            }
        }
    }

    private void HostStatusConsole_Click(object sender, RoutedEventArgs e) => LaunchHost(HostAction.Status);

    private async Task SetUpHostServiceAsync()
    {
        var target = ThisPcTarget();
        if (!HostSetupCommands.IsPrivate(target.Address))
        {
            ActionText.Text = "Connect this PC to your home network first.";
            return;
        }
        string? firewall;
        try
        {
            firewall = await HostsWindow.OpenFirewallAsync(this, target.Address, text => ActionText.Text = text, lifetime.Token,
                HostActions.ThisPcSetupTitle);
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        { firewall = $"Windows Firewall wasn't changed, so other PCs may not reach this host: {error.Message}"; }
        catch (OperationCanceledException) { return; }
        await LaunchHostAsync(HostAction.Setup);
        if (firewall is not null) ActionText.Text = firewall + " " + ActionText.Text;
    }

    private async void InstallDocker()
    {
        if ((await HostsWindow.InstallDockerDesktopAsync(this)).Status is { } status && !closing) ActionText.Text = status;
        if (!closing) await ReadMachineAsync();
    }

    /// <summary>Turns on what Docker Desktop needs from Windows (virtualization features and WSL) in a run window; a restart,
    /// when needed, continues by starting Docker Desktop after the next sign-in.</summary>
    private async void PrepareWindows()
    {
        if (closing) return;
        var done = await HostRunWindow.RunAsync(this, "Get Windows ready for Docker Desktop", async run =>
        {
            await HostLocal.EnsureDockerAsync(run, ContinueSetupKind.Docker);
            return "Docker Desktop is running.";
        }, join: true);
        if (!closing) ActionText.Text = done ?? "Windows isn't ready for Docker Desktop yet; see the progress window for details.";
        if (!closing) await ReadMachineAsync();
    }

    /// <summary>After Windows restarted to finish turning on virtualization, continues the setup that asked for it
    /// (<see cref="HostSetupResume"/>): this PC's host service and its pairing, the host dashboard's host service, or
    /// starting Docker Desktop so the owner can repeat the step that needed it.</summary>
    private async Task ContinueSetupAsync()
    {
        if (closing || store is null || HostSetupResume.Take() is not { } note) return;
        ErrorLog.Info($"Continuing after a Windows restart: {note.Kind} ({note.Task})");
        ActionText.Text = $"Continuing setup: {note.Task}...";
        switch (note.Kind)
        {
            case ContinueSetupKind.HostService when Role == DeviceRole.Host:
                await SetUpHostServiceAsync();
                break;
            case ContinueSetupKind.ThisPc when ThisPcHost() is null:
                await SetUpThisPcHostAsync();
                break;
            default:
                await StartDockerAfterRestartAsync(note.Task);
                break;
        }
    }

    private async Task StartDockerAfterRestartAsync(string task)
    {
        if (closing) return;
        var done = await HostRunWindow.RunAsync(this, "Start Docker Desktop", async run =>
        {
            await HostLocal.EnsureDockerAsync(run, ContinueSetupKind.Docker);
            return $"Docker Desktop is running. Continue: {task}.";
        }, join: true);
        if (!closing) ActionText.Text = done ?? "Docker Desktop didn't start; see the progress window for details.";
        if (!closing) await ReadMachineAsync();
    }

    private void StartDocker() => StartDockerAfterRestartAsync("host setup").Forget();

    /// <summary>Check again: reads this PC and its host service now (Docker, the gateway's roles and network, its published
    /// port) and says what that means. Read-only; it changes nothing.</summary>
    private async void CheckHostService_Click(object sender, RoutedEventArgs e)
    {
        if (closing) return;
        CheckHostServiceButton.IsEnabled = false;
        HostStatusText.Text = "Checking...";
        try
        {
            await ReadMachineAsync();
            // Reading this PC also starts a read of its host service; wait for that one rather than starting another.
            await (hostProbe is { IsCompleted: false } reading ? reading : CheckThisPcHostAsync());
        }
        finally { if (!closing) CheckHostServiceButton.IsEnabled = true; }
        if (closing) return;
        RenderHost();
        if (hostState is { } state) ActionText.Text = HostServiceSentence(state);
    }
}
