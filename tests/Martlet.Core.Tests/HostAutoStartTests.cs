using Martlet.Core.Installation;

namespace Martlet.Core.Tests;

public sealed class HostAutoStartTests
{
    private static readonly string[] Roles = ["ollama", "stt"];

    [Fact]
    public void StoppedDockerWithRememberedRolesStartsDockerThenWarms()
    {
        var plan = HostAutoStart.Decide(LocalHostServiceStage.DockerNotRunning, null, Roles, pairedWithOwnHost: false, windowsBlocked: false);

        Assert.Equal(HostAutoStartStep.StartDockerThenWarm, plan.Step);
        Assert.Contains("(ollama, stt)", plan.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void BeforeAnyReadAPairingWithItsOwnHostServiceCountsAsRoles()
    {
        Assert.Equal(HostAutoStartStep.StartDockerThenWarm,
            HostAutoStart.Decide(LocalHostServiceStage.DockerNotRunning, null, null, pairedWithOwnHost: true, windowsBlocked: false).Step);
        Assert.Equal(HostAutoStartStep.Skip,
            HostAutoStart.Decide(LocalHostServiceStage.DockerNotRunning, null, null, pairedWithOwnHost: false, windowsBlocked: false).Step);
        // Once read, what it runs counts, not the pairing.
        Assert.Equal(HostAutoStartStep.Skip,
            HostAutoStart.Decide(LocalHostServiceStage.DockerNotRunning, null, [], pairedWithOwnHost: true, windowsBlocked: false).Step);
    }

    [Fact]
    public void NothingIsInstalledOrApprovedByItself()
    {
        Assert.Equal(HostAutoStartStep.Skip,
            HostAutoStart.Decide(LocalHostServiceStage.DockerMissing, null, Roles, pairedWithOwnHost: true, windowsBlocked: false).Step);
        Assert.Equal(HostAutoStartStep.Skip,
            HostAutoStart.Decide(LocalHostServiceStage.DockerNotRunning, null, Roles, pairedWithOwnHost: true, windowsBlocked: true).Step);
        Assert.Equal(HostAutoStartStep.Skip,
            HostAutoStart.Decide(LocalHostServiceStage.NotSetUp, null, Roles, pairedWithOwnHost: true, windowsBlocked: false).Step);
    }

    [Fact]
    public void RunningOrStoppedHostServicesWithRolesAreWarmed()
    {
        Assert.Equal(HostAutoStartStep.Warm,
            HostAutoStart.Decide(LocalHostServiceStage.Running, Roles, null, pairedWithOwnHost: false, windowsBlocked: false).Step);
        Assert.Equal(HostAutoStartStep.Warm,
            HostAutoStart.Decide(LocalHostServiceStage.Stopped, null, Roles, pairedWithOwnHost: false, windowsBlocked: false).Step);
        Assert.Equal(HostAutoStartStep.Skip,
            HostAutoStart.Decide(LocalHostServiceStage.Running, [], Roles, pairedWithOwnHost: true, windowsBlocked: false).Step);
    }

    [Fact]
    public void RolesAreRememberedUntilForgotten()
    {
        var directory = Path.Combine(Path.GetTempPath(), "martlet-host-roles-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Null(ThisPcHostRoles.Load(directory));
            ThisPcHostRoles.Save(directory, ["stt", "ollama", "../bad"]);
            Assert.Equal(["ollama", "stt"], ThisPcHostRoles.Load(directory));
            ThisPcHostRoles.Save(directory, []);
            Assert.Empty(ThisPcHostRoles.Load(directory)!);
            ThisPcHostRoles.Forget(directory);
            Assert.Null(ThisPcHostRoles.Load(directory));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
