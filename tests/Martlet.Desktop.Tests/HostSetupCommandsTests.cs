using Martlet.Desktop;

namespace Martlet.Desktop.Tests;

public sealed class HostSetupCommandsTests
{
    private static HostSetupTarget Target(HostSetupMethod method, string ssh = "me@192.168.1.20", string address = "192.168.1.20",
        string? hostId = null) => new(method, ssh, address, hostId, "1.2.3");

    [Fact]
    public void This_pc_docker_builds_this_version_then_runs_the_engine_with_the_lan_address()
    {
        var script = HostSetupCommands.Script(Target(HostSetupMethod.ThisPcDocker, hostId: "gaming-pc-host"), HostAction.Setup);
        Assert.Contains("docker image inspect martlet-host:1.2.3 >NUL 2>&1 || docker build -t martlet-host:1.2.3 -f deploy/host/Dockerfile https://github.com/throndir2/Martlet.git#v1.2.3 || docker build -t martlet-host:1.2.3 -f deploy/host/Dockerfile https://github.com/throndir2/Martlet.git#main", script);
        Assert.Contains("docker run --rm -it -u 0 -v /var/run/docker.sock:/var/run/docker.sock -e MARTLET_HOST_ADDRESS=192.168.1.20 -e MARTLET_HOST_ID=gaming-pc-host martlet-host:1.2.3 setup", script);
    }

    [Fact]
    public void Ssh_methods_run_the_same_engine_commands_remotely_without_double_quotes()
    {
        var docker = HostSetupCommands.DockerShell(Target(HostSetupMethod.SshDocker), HostAction.AddAudio2Face);
        Assert.EndsWith("$D run --rm -it -u 0 -v /var/run/docker.sock:/var/run/docker.sock martlet-host:1.2.3 add audio2face", docker);
        var native = HostSetupCommands.NativeShell(Target(HostSetupMethod.SshNative), HostAction.Setup);
        Assert.EndsWith("MARTLET_HOST_ADDRESS=192.168.1.20 ~/Martlet/deploy/host/martlet-host setup", native);
        foreach (var method in new[] { HostSetupMethod.SshDocker, HostSetupMethod.SshNative })
        {
            var script = HostSetupCommands.Script(Target(method), HostAction.Pair);
            var line = script.Split("\r\n").Single(l => l.StartsWith("ssh -t me@192.168.1.20 \"", StringComparison.Ordinal));
            Assert.Equal(2, line.Count(c => c == '"'));
            Assert.DoesNotContain('%', line);
        }
    }

    [Theory]
    [InlineData("me@host & calc", "192.168.1.20")]
    [InlineData("me@host\"x", "192.168.1.20")]
    [InlineData("me@192.168.1.20", "8.8.8.8")]
    [InlineData("me@192.168.1.20", "192.168.1.20 & calc")]
    public void Unsafe_targets_and_public_addresses_are_rejected(string ssh, string address) =>
        Assert.Throws<InvalidOperationException>(() =>
            HostSetupCommands.Script(Target(HostSetupMethod.SshDocker, ssh, address), HostAction.Setup));

    [Fact]
    public void Suggested_ids_are_valid_gateway_identifiers()
    {
        Assert.Equal("gaming-pc-host", HostSetupCommands.SuggestedHostId("GAMING-PC"));
        Assert.Equal("martlet-host", HostSetupCommands.SuggestedHostId("---"));
        Assert.StartsWith("desktop-", HostSetupCommands.SuggestedDeviceId());
    }
}
