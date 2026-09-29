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

    [Fact]
    public void This_pc_script_starts_docker_desktop_and_waits_before_running()
    {
        var script = HostSetupCommands.Script(Target(HostSetupMethod.ThisPcDocker), HostAction.Pair);
        Assert.True(script.IndexOf("Docker Desktop.exe", StringComparison.Ordinal) < script.IndexOf("\r\n:ready\r\n", StringComparison.Ordinal));
        Assert.True(script.IndexOf("\r\n:ready\r\n", StringComparison.Ordinal) < script.IndexOf("docker run", StringComparison.Ordinal));
        Assert.Contains("for /l %%i in (1,1,100) do (", script);
        Assert.Equal(2, HostSetupCommands.Preview(Target(HostSetupMethod.ThisPcDocker), HostAction.Pair).Split("\r\n").Length);
    }

    [Fact]
    public void Firewall_rule_is_scoped_to_the_host_port_on_private_networks_and_local_subnet()
    {
        var script = WindowsFirewall.ApplyScript(null);
        Assert.Contains("New-NetFirewallRule -Name 'Martlet-Host-Gateway'", script);
        Assert.Contains("-Direction Inbound -Action Allow -Protocol TCP -LocalPort 9443 -Profile Private,Domain -RemoteAddress LocalSubnet", script);
        Assert.DoesNotContain("Set-NetConnectionProfile", script);
        Assert.Contains("Set-NetConnectionProfile -InterfaceIndex 14 -NetworkCategory Private;", WindowsFirewall.ApplyScript(14));
        Assert.Throws<InvalidOperationException>(() => WindowsFirewall.ProbeScript("192.168.1.2'; calc; '"));
        Assert.Equal(new WindowsFirewall.State(true, "Public", 7, false), WindowsFirewall.Parse("True|Public|7|False\r\n"));
        Assert.Equal(new WindowsFirewall.State(false, null, null, false), WindowsFirewall.Parse("unexpected"));
    }
}
