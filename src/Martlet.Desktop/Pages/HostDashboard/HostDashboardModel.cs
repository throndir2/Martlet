using Martlet.Core.Cluster;
using Martlet.Core.Installation;
using Martlet.HostClient;

namespace Martlet.Desktop;

/// <summary>The host dashboard's steps on a host PC, without WPF: Docker Desktop, the host service, pairing your main PC, its roles
/// and keeping it up to date, each with what it says and its buttons, and the one-line headline and sentence about this PC's host
/// service. Home, the welcome tour and the health checks use the Docker Desktop and host service steps too. MainWindow draws the
/// steps (MainWindow.HostDashboard.cs) and runs what their buttons start; <see cref="IHostDashboardServices"/> is all the model
/// reads and starts in the app.</summary>
internal sealed class HostDashboardModel(IHostDashboardServices services)
{
    private static readonly string Version = AppVersions.Current;

    private static StepHelp Tip(string topic, string text, string id, string? wiki = null) => new(topic, text, id, wiki);

    /// <summary>Docker Desktop is being installed, Windows readied for it or it is being started, by a run working now.</summary>
    internal bool DockerUnderway => DockerWorkNow() is not null;

    /// <summary>The Docker Desktop step's greyed-out label for what the runs working now do with Docker Desktop, or null when
    /// none does (<see cref="DockerWorkLabel"/>).</summary>
    internal string? DockerWorkNow() =>
        DockerWorkLabel(SharedSteps.Now().Select(step => step.Key), services.RunningTitled(HostsWindow.InstallDockerTitle));

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
    private string? DockerWork()
    {
        var working = SharedSteps.Now().Where(step => step.Key is SharedSteps.DockerInstall or SharedSteps.WindowsReady or SharedSteps.DockerStart)
            .Select(step => $"\"{step.By}\" is {step.Doing}").ToList();
        if (working.Count == 0 && services.RunningTitled(HostsWindow.InstallDockerTitle)) working.Add($"\"{HostsWindow.InstallDockerTitle}\" is working");
        return working.Count == 0 ? null : string.Join("; ", working);
    }

    internal string Headline(LocalHostServiceState? state) => state?.Stage switch
    {
        null => "Checking...",
        LocalHostServiceStage.DockerMissing => "Needs Docker Desktop",
        LocalHostServiceStage.DockerNotRunning when services.Virtualization?.RestartRequired == true => "Needs Windows restart",
        LocalHostServiceStage.DockerNotRunning when services.Virtualization?.Blocked == true => "Windows isn't ready for Docker Desktop",
        LocalHostServiceStage.DockerNotRunning => "Waiting for Docker Desktop",
        LocalHostServiceStage.NotSetUp => "Not set up yet",
        LocalHostServiceStage.Stopped => "Host service stopped",
        _ when state.AddressOnThisPc == false => services.HostMoving ? "Moving to the new address" : "Address changed",
        _ when state.Answering != true => "Not answering yet",
        _ => "Host is running"
    };

    internal HomeStep DockerStep(LocalHostServiceState? state)
    {
        var installed = state is null ? services.Machine.DockerInstalled : state.Stage != LocalHostServiceStage.DockerMissing;
        var done = state is not null && state.Stage is not (LocalHostServiceStage.DockerMissing or LocalHostServiceStage.DockerNotRunning);
        var windowsBlocks = !done && installed && services.Virtualization?.Blocked == true;
        var engineWaiting = services.Machine.DockerRunning && state?.Stage == LocalHostServiceStage.DockerNotRunning;
        var underway = done ? null : DockerWork();
        // While a run works on Docker Desktop (for example the one a new host PC starts by itself), the step's button says what
        // that run does and stays greyed out until it ends, so nobody starts Docker Desktop a second time.
        var working = done ? null : DockerWorkNow();
        var action = services.Virtualization switch
        {
            { RestartRequired: true } => "Restart Windows",
            { FirmwareOff: true, VirtualMachine: false } => "Turn on services.Virtualization",
            { NeedsChanges: true } => "Turn on Windows features",
            _ => "Review Windows setup"
        };
        StepCommand next = windowsBlocks ? new(action, services.PrepareWindows, true)
            : installed ? new("Start Docker Desktop", services.StartDocker, true)
            : new("Install Docker Desktop", services.InstallDocker, true);
        return new("docker", "Docker Desktop",
            done ? "Running."
                : underway is not null ? underway + "; other setup can go ahead meanwhile."
                : windowsBlocks ? string.Join("; ", services.Virtualization!.Problems()) + ". " + services.Virtualization.Recovery
                : engineWaiting ? "Docker Desktop is open, but its engine isn't answering yet; the first start can take a few minutes."
                : state is null && installed ? "Checking Docker Desktop's engine..."
                : installed ? "Installed, but not running." : "Required for the host service.",
            done, false,
            done ? [] : [working is null ? next : next with { Label = working, Enabled = false }],
            Tip("Docker Desktop", "The host service runs in Docker Desktop. Its first start can take a few minutes; you don't have to " +
                "wait, because steps that need it carry on once it runs.", "HostDocker", wiki: "Setting-Up-a-Host#windows-with-docker-desktop"));
    }

    internal HomeStep ServiceStep(LocalHostServiceState? state)
    {
        StepCommand SetUp(string label, bool primary = true) => new(label, services.SetUpHostService, primary);
        // While Docker Desktop is still being installed or started, a host service Martlet hasn't seen set up can be set up
        // already: its run waits for Docker Desktop and carries on as soon as it runs.
        var early = state?.Stage is LocalHostServiceStage.DockerMissing or LocalHostServiceStage.DockerNotRunning && DockerUnderway &&
            !services.HasThisPcHost && services.ThisPcHostVersion is null;
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
            _ when state.AddressOnThisPc == false && services.HostMoving =>
                ("This PC's network address changed since setup, so Martlet is moving the host service to the new address now.", []),
            _ when state.AddressOnThisPc == false =>
                ("This PC's network address changed and Martlet couldn't move the host service by itself (Background tasks says " +
                 "why): set it up again for the new address.", [SetUp("Set up again")]),
            _ when state.Answering != true =>
                ("Running, but not answering on your network yet; if this lasts, set it up again.", [SetUp("Set up again", primary: false)]),
            _ => ($"Running and reachable on your network{(state.HostId is { } id ? $" as {id}" : "")}.", [])
        };
        // A setup run working now says so (pressing its button again brings that run's window forward).
        if (state?.Ready != true && services.RunningTitled(HostActions.ThisPcSetupTitle))
            step.Detail = $"Being set up now (\"{HostActions.ThisPcSetupTitle}\"). " + step.Detail;
        return new("service", "Host service", step.Detail, state?.Ready == true, false, step.Commands,
            Tip("the host service", "The host service does the work your other computers send to this PC. Starting it or setting it " +
                "up again keeps its pairings and roles. Windows may ask to allow private-network access when you set it up.",
                "HostService", wiki: "Setting-Up-a-Host#windows-with-docker-desktop"));
    }

    internal HomeStep PairStep(LocalHostServiceState? state)
    {
        var self = services.SelfDeviceId;
        var desktops = state?.Desktops ?? [];
        // The host's network members, and every computer the host service reports as paired with it.
        var now = DateTimeOffset.UtcNow;
        var reported = services.PairedComputers();
        var others = desktops.Where(d => d.Id != self && !services.IsThisDevice(d.Id)).Select(d => (d.Id, d.Name))
            .Concat(reported.Select(d => (Id: d.DeviceId, Name: d.DisplayName)))
            .DistinctBy(d => d.Id, StringComparer.Ordinal)
            .Select(d => reported.FirstOrDefault(r => r.DeviceId == d.Id) is { } seen ? $"{d.Name} ({MainWindow.Seen(seen.LastSeen, now)})" : d.Name)
            .ToList();
        var thisPc = desktops.Any(d => d.Id == self) || services.UsesOwnHost;
        var justPaired = services.HostPairedAt is { } at && DateTime.UtcNow - at < TimeSpan.FromMinutes(5);
        var paired = others.Count > 0 || thisPc || justPaired;
        var help = Tip("pairing", "On your main PC, choose Devices > Add a computer: this PC is listed under Martlet on your network. " +
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
        if (services.NearbyBlocked)
            detail += " Windows Firewall doesn't let your other computers find this PC yet; Let my other computers find this PC " +
                "fixes that (administrator approval once).";
        var pair = new StepCommand(paired ? "Pair another computer" : "Show a pairing code", () => services.LaunchHost(HostAction.Pair), !paired);
        return new("pair", "Pair your main PC", detail, paired, false, services.NearbyBlocked
            ? [pair, new("Let my other computers find this PC", services.LetComputersFind)]
            : [pair], help);
    }

    internal HomeStep RolesStep(LocalHostServiceState? state)
    {
        var nvidia = services.Machine.BestGpu is { IsNvidia: true } gpu ? $"; this PC has {gpu.Describe()}"
            : services.Machine.ProcessorType.WindowsOnArm ? "; NVIDIA graphics cards don't work on this Windows on Arm PC, so GPU roles need another computer"
            : "; no NVIDIA graphics card found";
        var help = Tip("roles", "Roles are the jobs this host does for your computers, such as Thinking, Listening or lip-sync. Add " +
            "or remove them any time.", "HostRoles", wiki: "Setting-Up-a-Host#choose-the-hosts-jobs");
        // Roles are read from the running host service; until it runs there is nothing to add them to.
        if (state?.Roles is not { } installed)
            return new("roles", "Add roles", "Add tasks this host can handle once the host service runs" + nvidia + ".", false, true, [], help);
        string Name(string kind) => HostRoles.All.FirstOrDefault(r => r.Kind == kind)?.Name ?? kind;
        // Jobs your Martlet network gives this PC (it was set up to do them, as a companion or a host PC) need their roles here.
        var needed = services.ClusterEnabled && state.HostId is { } self
            ? ClusterJobs.All.Where(j => services.ClusterPlan.For(j)?.HostId == self).Select(j => (Job: j, Kind: ClusterSync.RoleKind(j)))
                .Where(n => !installed.Contains(n.Kind, StringComparer.Ordinal)).ToList()
            : [];
        var nvidiaCards = services.Machine.Gpus.Count(g => g.IsNvidia);
        var missing = HostRoles.All.Where(r => !installed.Contains(r.Kind, StringComparer.Ordinal) && HostRoles.OfferedOn(r, nvidiaCards))
            .OrderBy(r => needed.Any(n => n.Kind == r.Kind) ? 0 : 1).ToList();
        IReadOnlyList<StepCommand> commands =
        [
            .. missing.Select((r, i) => new StepCommand($"Add {r.Name}", () => services.LaunchHost(r.Add),
                needed.Any(n => n.Kind == r.Kind) || installed.Count == 0 && i == 0)),
            .. HostRoles.All.Where(r => installed.Contains(r.Kind, StringComparer.Ordinal))
                .Select(r => new StepCommand($"Remove {r.Name}", () => services.LaunchHost(r.Remove)))
        ];
        var asked = needed.Count == 0 ? ""
            : $"Your computers use this PC for {JoinNames([.. needed.Select(n => n.Job)])}, so add " +
              $"{JoinNames([.. needed.Select(n => Name(n.Kind))])} here; until then they keep what they use now. ";
        return new("roles", "Add roles",
            asked + (installed.Count == 0 ? "No roles yet: add tasks this host can handle" + nvidia + "."
                : $"Runs {JoinNames([.. installed.Select(Name)])}."),
            installed.Count > 0 && needed.Count == 0, true, commands, help);
    }

    internal HomeStep UpdateStep(LocalHostServiceState? state, bool setUp)
    {
        var update = new StepCommand("Update host service", () => services.LaunchHost(HostAction.Update));
        var help = Tip("host updates", "Martlet keeps the host service on this app's version by itself. An update keeps its pairings " +
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
        if (services.UpdatingThisPcHost)
            return new("update", "Keep it up to date",
                $"Updating the host service from Martlet {running} to {Version}.", false, true, [], help);
        return new("update", "Keep it up to date",
            $"The host service runs Martlet {running}; Martlet updates it to {Version} by itself " +
            (state.Stage == LocalHostServiceStage.Running ? "in the background" : "once it runs again") +
            ", or Update host service does it now.", false, true, [update with { Primary = true }], help);
    }

    /// <summary>"A", "A and B", "A, B and C", "A, B, C and 2 more".</summary>
    internal static string JoinNames(IReadOnlyList<string> names) => names.Count switch
    {
        0 => "",
        1 => names[0],
        <= 3 => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1],
        _ => string.Join(", ", names.Take(3)) + $" and {names.Count - 3} more"
    };

    /// <summary>What the last read of this PC's host service means, as one sentence for the status line.</summary>
    internal string ServiceSentence(LocalHostServiceState state) => state.Stage switch
    {
        LocalHostServiceStage.DockerMissing => "Docker Desktop isn't installed: install it first (step 1).",
        LocalHostServiceStage.DockerNotRunning when services.Virtualization?.Blocked == true =>
            string.Join("; ", services.Virtualization.Problems()) + ". " + services.Virtualization.Recovery,
        LocalHostServiceStage.DockerNotRunning => services.Machine.DockerRunning
            ? "Docker Desktop is open, but its engine isn't answering yet, so the host service can't run; the first start can take a few minutes."
            : "Docker Desktop isn't running yet, so the host service can't run: start Docker Desktop (step 1).",
        LocalHostServiceStage.NotSetUp => "The host service isn't set up yet: use Set up host service.",
        LocalHostServiceStage.Stopped => "The host service is set up but stopped: use Start host service (pairings and roles stay).",
        _ when state.AddressOnThisPc == false && services.HostMoving => "This PC's network address changed since setup, so Martlet is moving the host service there now.",
        _ when state.AddressOnThisPc == false => "This PC's network address changed since setup: use Set up again (pairings and roles stay).",
        _ when state.Answering != true => "The host service runs but isn't answering on your network yet: check again in a moment.",
        _ => "The host service is running and reachable on your network" + (PairStep(state).Done ? "." : "; pair your main PC next.")
    };
}
