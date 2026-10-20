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
        HostStatusText.Text = HostDashboard.Headline(state);
        if (state?.Ready == true) Motion.PulseRing(HostPulse, to: 1.35);
        else
        {
            HostPulse.BeginAnimation(OpacityProperty, null);
            HostPulse.Opacity = 0;
        }
        var setUp = state?.Stage is LocalHostServiceStage.Stopped or LocalHostServiceStage.Running;
        var steps = new List<HomeStep> { HostDashboard.DockerStep(state), HostDashboard.ServiceStep(state), HostDashboard.PairStep(state), HostDashboard.RolesStep(state), HostDashboard.UpdateStep(state, setUp) };
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

    /// <summary>The other computers paired with this PC's host service, as it said on the last network sync: members of its
    /// network or not (such as one still waiting to join), never a friend's computer. Empty when this PC isn't paired with its
    /// own host service, or before the first sync.</summary>
    private IReadOnlyList<HostPairedDevice> PairedComputers() =>
        ThisPcHost() is { } own && PairedWith(own.HostId) is { } devices ? devices.Where(d => !IsThisDevice(d.DeviceId) && !d.Friend).ToArray() : [];

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
            ErrorLog.Info($"This PC's host service: {HostDashboard.Headline(state)} (stage {state.Stage}, version {state.Version ?? "-"}, " +
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
        if (hostState is { } state) ActionText.Text = HostDashboard.ServiceSentence(state);
    }
}
