using Martlet.Avatar.Hosting;

namespace Martlet.Desktop;

/// <summary>How the questions of a role are asked before it is installed on this PC.</summary>
internal enum LocalRoleQuestions
{
    /// <summary>No questions: the answers Martlet already has go as they are.</summary>
    None,
    /// <summary>With several graphics cards, the owner picks the one the role runs on (<see cref="HostInputDialog.WithGpu"/>).</summary>
    Gpu,
    /// <summary>The role's own dialog (<see cref="HostInputDialog.ForRole"/>), for example Motion's model and license.</summary>
    Role,
}

/// <summary>One host role to install on this PC through martlet-host: <paramref name="Role"/> (a <see cref="HostRoles"/> kind),
/// the <paramref name="Name"/> the dialogs and progress use, the <paramref name="Answers"/> already chosen in Martlet and the
/// run window texts. <paramref name="InstallFailed"/> gives the message for a nonzero exit code of martlet-host.</summary>
internal sealed record LocalRoleInstall(string Role, string Name, IReadOnlyDictionary<string, string> Answers, string Installing,
    Func<int, string> InstallFailed)
{
    internal LocalRoleQuestions Questions { get; init; } = LocalRoleQuestions.Gpu;
    /// <summary>The run window status while martlet-host reads the role's questions (none: the status stays).</summary>
    internal string? QuestionsStatus { get; init; }
    /// <summary>Gets why martlet-host stopped ("Stopped: ...") from its progress; null: the run window shows its plain output.</summary>
    internal Action<string>? Stopped { get; init; }
}

/// <summary>The steps every host role page (Pictures, Reading, Singing, Motion and the conversation jobs) uses to set a role up
/// on this PC and to wait until a host offers it ready, so a fix to one is a fix to all.</summary>
public partial class MainWindow
{
    /// <summary>Sets the role up on this PC: with the host service there, in a run window titled <paramref name="title"/>;
    /// without it (<paramref name="thisPc"/> null), sets the host service up first in the same window
    /// (<paramref name="setUpTitle"/>) and reads this PC again. <paramref name="install"/> runs once this PC's host answers.
    /// Returns the run's last status, or <paramref name="notDone"/> when the run window stopped without one.</summary>
    private async Task<string?> RunRoleSetupHereAsync(PairedHost? thisPc, string title, string setUpTitle,
        Func<HostRunWindow, PairedHost, Task<string>> install, Func<string> notDone)
    {
        if (thisPc is not null)
            return await HostRunWindow.RunAsync(this, title, run => install(run, thisPc)) ?? notDone();
        var (_, status) = await HostsWindow.SetUpThisPcAsync(this, new AvatarProfileStore(store!.DataDirectory), setupService!,
            text => ActionText.Text = text, lifetime.Token, install, setUpTitle);
        await ReadMachineAsync();
        return status;
    }

    /// <summary>Installs <paramref name="install"/>'s role with martlet-host on <paramref name="pc"/> (this PC): makes sure
    /// Docker Desktop runs, picks the engine for the change, asks the role's questions, then adds the role with the run window
    /// showing its progress. Throws <see cref="OperationCanceledException"/> when the owner closes a question, and
    /// <see cref="InvalidOperationException"/> with the role's own text when martlet-host stops.</summary>
    private async Task InstallRoleHereAsync(HostRunWindow run, PairedHost pc, LocalRoleInstall install)
    {
        var target = pc.Target(Version);
        await HostLocal.EnsureDockerAsync(run, Martlet.Core.Installation.ContinueSetupKind.Docker);
        target = await HostLocal.EngineForChangeAsync(target, install.Role, run);
        var chosen = install.Answers;
        if (install.Questions != LocalRoleQuestions.None)
        {
            if (install.QuestionsStatus is { } asking) run.Status(asking);
            var inputs = await HostLocal.DescribeAsync(target, install.Role, run.Output, run.Token);
            chosen = (install.Questions == LocalRoleQuestions.Role
                ? HostInputDialog.ForRole(run, "this PC", install.Role, inputs, local: true)
                : HostInputDialog.WithGpu(run, "this PC", install.Name, inputs, install.Answers)) ?? throw new OperationCanceledException();
        }
        run.Status(install.Installing);
        var output = install.Stopped is { } stopped ? SetupProgress(run, install.Name, stopped) : run.Output;
        var exit = await HostLocal.EngineAsync(target, ["add", install.Role], output, run.Token, answers: chosen);
        if (exit != 0) throw new InvalidOperationException(install.InstallFailed(exit));
    }

    /// <summary>After martlet-host added (or changed) a role, checks <paramref name="host"/> (about a minute) and keeps each
    /// check for the pages, until <paramref name="probe"/> says the role is ready there. The probe gets the newest check (and
    /// uses the caller's token) and returns null when the role is ready, else why it isn't yet (<see cref="RoleNotReady.GiveUp"/>: stop asking at once).
    /// Returns null once the role is ready, else the last reason.</summary>
    private async Task<string?> WaitForRoleAsync(PairedHost host, Func<HostCheck, Task<RoleNotReady?>> probe,
        CancellationToken token) =>
        await RoleWait.UntilReadyAsync(async ct =>
        {
            var check = await HostControl.CheckAsync(host.Pairing, HardwareStore, ct);
            hostChecks[host.HostId] = check;
            return check;
        }, probe, RoleWait.Interval, token);
}

/// <summary>Why a role isn't ready on a host yet; <paramref name="GiveUp"/> when waiting longer can't help (it failed to load).</summary>
internal sealed record RoleNotReady(string Why, bool GiveUp = false);

/// <summary>The wait after a host role was set up: check the host, ask the role's probe, try again a little later.</summary>
internal static class RoleWait
{
    /// <summary>The checks <see cref="UntilReadyAsync"/> makes: 13, five seconds apart (about a minute).</summary>
    internal const int Attempts = 13;
    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    /// <summary>Checks with <paramref name="check"/> up to <see cref="Attempts"/> times, <paramref name="interval"/> apart, until
    /// <paramref name="probe"/> returns null (ready). Returns null then, else the last reason; at once when the probe gives up.</summary>
    internal static async Task<string?> UntilReadyAsync(Func<CancellationToken, Task<HostCheck>> check,
        Func<HostCheck, Task<RoleNotReady?>> probe, TimeSpan interval, CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            if (await probe(await check(token)) is not { } notReady) return null;
            if (notReady.GiveUp || attempt >= Attempts - 1) return notReady.Why;
            await Task.Delay(interval, token);
        }
    }
}
