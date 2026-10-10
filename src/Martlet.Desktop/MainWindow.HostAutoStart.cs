using System.IO;
using System.Windows;
using Martlet.Core.Installation;

namespace Martlet.Desktop;

/// <summary>A host PC brings its roles up by itself. When this PC becomes a host PC (or Martlet starts on one) and its host
/// service in Docker Desktop runs roles, Martlet starts Docker Desktop when it isn't running, waits for the host service's
/// network holder to restart the gateway and roles, then runs martlet-host warm: every role starts and loads its model, so
/// the first request from your other computers doesn't wait for it. It only does what needs no installing or approval
/// (<see cref="HostAutoStart"/>); otherwise the host dashboard's steps say what's missing. The run works hidden, in
/// Background tasks, and the host dashboard shows what it is doing.</summary>
public partial class MainWindow
{
    internal const string HostAutoStartRun = "Start this host's roles";
    private Task? hostAutoStart;

    /// <summary>Starts the roles unless that already runs.</summary>
    private void StartHostRolesByItself()
    {
        if (hostAutoStart is { IsCompleted: false } || HostRunWindow.IsRunningTitled(HostAutoStartRun)) return;
        hostAutoStart = StartHostRolesAsync();
    }

    private async Task StartHostRolesAsync()
    {
        if (closing || exiting || store is null || Role != DeviceRole.Host || SimulatedOwnHost.Active) return;
        LocalHostServiceState state;
        WindowsVirtualization? windows = null;
        try
        {
            state = await LocalHostService.ProbeAsync(lifetime.Token);
            if (state.Stage == LocalHostServiceStage.DockerNotRunning) windows = await WindowsVirtualization.ProbeAsync(lifetime.Token);
        }
        catch (OperationCanceledException) { return; }
        if (closing || Role != DeviceRole.Host) return;
        RememberThisPcHostRoles(state);
        var plan = HostAutoStart.Decide(state.Stage, state.Roles, RememberedHostRoles(), ThisPcHost() is not null,
            windows?.Blocked == true);
        ErrorLog.Info($"This host PC's roles, by themselves: {plan.Step} (host service {state.Stage}). {plan.Reason}");
        ShowHostAutoStart(plan.Reason);
        if (plan.Step == HostAutoStartStep.Skip) return;

        var target = ThisPcTarget();
        var done = await HostRunWindow.RunAsync(this, HostAutoStartRun, async run =>
        {
            if (plan.Step == HostAutoStartStep.StartDockerThenWarm)
            {
                run.Status("Starting Docker Desktop...");
                await HostLocal.EnsureDockerAsync(run, ContinueSetupKind.Docker);
            }
            run.Status("Waiting for the host service to restart its roles...");
            await HostLocal.WaitForHolderAsync(run.Output, run.Token);
            var engine = await HostLocal.EngineForChangeAsync(target, null, run);
            run.Status("Starting this host's roles and loading their models...");
            var exit = await HostLocal.EngineAsync(engine, ["warm"], run.Output, run.Token);
            if (exit != 0)
                throw new InvalidOperationException($"Some of this host's roles didn't start or warm up (exit {exit}). The output says which; " +
                    "adding a role again repairs it.");
            return "This host's roles are running and their models are loaded.";
        }, hidden: true);
        if (closing) return;
        ShowHostAutoStart(done is not null
            ? $"{done} Martlet started them by itself at {DateTime.Now:t}."
            : $"Martlet couldn't start this host's roles by itself at {DateTime.Now:t}. Background tasks shows why ({HostAutoStartRun}).");
        await ReadMachineAsync();
    }

    /// <summary>Keeps what this PC's host service runs in the PC folder, with the Windows user whose Docker Desktop runs it, so
    /// a host PC knows what to start before Docker Desktop runs. A host service that isn't set up in this Windows user's
    /// Docker Desktop forgets only what this Windows user's Docker Desktop ran.</summary>
    private void RememberThisPcHostRoles(LocalHostServiceState state)
    {
        if (Pc is not { } pc) return;
        try
        {
            if (state.Roles is { } roles)
            {
                pc.Prepare();
                ThisPcHostRoles.Save(pc.Directory, roles, PcRole.ThisWindowsUser);
            }
            else if (state.Stage == LocalHostServiceStage.NotSetUp &&
                     ThisPcHostRoles.WindowsUser(pc.Directory) is var user && (user is null || user == PcRole.ThisWindowsUser))
                ThisPcHostRoles.Forget(pc.Directory);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Warn("Couldn't remember this PC's host roles", error);
        }
        RoleWhereText.Text = PcRoleWhere();
    }

    /// <summary>The host dashboard's line about starting the roles by itself.</summary>
    private void ShowHostAutoStart(string text)
    {
        HostAutoStartText.Text = text;
        HostAutoStartText.Visibility = Visibility.Visible;
    }
}
