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
    public void Ssh_methods_run_the_same_engine_unattended_through_the_in_app_runner()
    {
        var docker = HostSetupCommands.DockerShell(Target(HostSetupMethod.SshDocker), HostAction.AddAudio2Face);
        Assert.EndsWith("$D run --rm -it -u 0 -v /var/run/docker.sock:/var/run/docker.sock martlet-host:1.2.3 add audio2face", docker);
        var native = HostSetupCommands.NativeShell(Target(HostSetupMethod.SshNative), HostAction.Setup);
        Assert.EndsWith("MARTLET_HOST_ADDRESS=192.168.1.20 ~/Martlet/deploy/host/martlet-host setup", native);
        Assert.EndsWith("$D run --rm -i --log-driver none -u 0 -v /var/run/docker.sock:/var/run/docker.sock martlet-host:1.2.3 --yes pair --device-id d --name 'PC'",
            HostSetupCommands.RemoteShell(Target(HostSetupMethod.SshDocker), "pair --device-id d --name 'PC'", false));
        Assert.Contains("-e MARTLET_HOST_ADDRESS=192.168.1.20 martlet-host:1.2.3 --yes setup",
            HostSetupCommands.RemoteShell(Target(HostSetupMethod.SshDocker), "setup", true));
        var remoteNative = HostSetupCommands.RemoteShell(Target(HostSetupMethod.SshNative), "setup", true);
        Assert.EndsWith("MARTLET_HOST_ADDRESS=192.168.1.20 ~/Martlet/deploy/host/martlet-host --yes setup", remoteNative);
        Assert.Contains("git clone --depth 1 https://github.com/throndir2/Martlet.git ~/Martlet) </dev/null", remoteNative);
        foreach (var method in new[] { HostSetupMethod.SshDocker, HostSetupMethod.SshNative })
            Assert.Throws<InvalidOperationException>(() => HostSetupCommands.Script(Target(method), HostAction.Pair));
        Assert.Throws<InvalidOperationException>(() => HostSetupCommands.RemoteShell(Target(HostSetupMethod.ThisPcDocker), "status", false));
    }

    [Fact]
    public void Runner_wraps_commands_for_sh_and_keeps_sudo_passwords_off_the_command_line()
    {
        Assert.Equal("'it'\\''s'", HostShell.Quote("it's"));
        Assert.Equal("sh -c 'echo '\\''hi'\\'''", HostShell.Wrap("echo 'hi'", sudo: false));
        var sudo = HostShell.Wrap("sudo id -u", sudo: true);
        Assert.StartsWith("sh -c '", sudo);
        Assert.Contains("IFS= read -r __martlet_pw", sudo);
        Assert.Contains("SUDO_ASKPASS", sudo);
        Assert.EndsWith("sudo id -u'", sudo);
        Assert.Equal("bold", HostShell.Clean("\u001b[1mbold\u001b[0m\r"));
        Assert.Equal(new HostShellTarget("me", "gpu-pc", 2222), HostShellTarget.Parse("me@gpu-pc:2222"));
        Assert.Equal("me@gpu-pc", HostShellTarget.Parse(" me@gpu-pc ").ToString());
        Assert.Equal("gpu-pc:22", HostShellTarget.Parse("me@GPU-PC").Machine);
        foreach (var bad in new[] { "gpu-pc", "me@gpu pc", "me@host;calc", "me@host:0", "'me'@host" })
            Assert.Throws<InvalidOperationException>(() => HostShellTarget.Parse(bad));
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var line = HostShell.PublicKey(key.ExportParameters(false)).Split(' ');
        Assert.Equal("ecdsa-sha2-nistp256", line[0]);
        Assert.Equal(104, Convert.FromBase64String(line[1]).Length);
        Assert.StartsWith("martlet@", line[2]);
    }

    [Fact]
    public void Role_inputs_come_from_the_hosts_role_conf()
    {
        var role = HostRemote.ParseRole([
            "role.title=NVIDIA Audio2Face-3D lip-sync", "role.requires=gpu docker nvidia-toolkit", "role.terms=Accept NVIDIA's terms.",
            "role.installed=no", "role.secret=ngc_api_key|NVIDIA NGC API key (https://org.ngc.nvidia.com/setup/api-key)|stored",
            "role.choice=A2F_3D_MODEL_NAME|Audio2Face face model|claire mark james|claire"
        ]);
        Assert.False(role.Installed);
        Assert.Equal(new HostRoleSecret("ngc_api_key", "NVIDIA NGC API key (https://org.ngc.nvidia.com/setup/api-key)", true), Assert.Single(role.Secrets));
        var choice = Assert.Single(role.Choices);
        Assert.Equal(("A2F_3D_MODEL_NAME", "claire"), (choice.Variable, choice.Default));
        Assert.Equal(["claire", "mark", "james"], choice.Options);
        var probe = new HostProbe(false, false, "password", "Ubuntu 24.04.1 LTS", "x86_64", "192.168.1.20", "gpu");
        Assert.Contains("Docker is the one thing", HostRemote.Blocker(HostSetupMethod.SshDocker, probe, "me@gpu"));
        Assert.Null(HostRemote.Blocker(HostSetupMethod.SshNative, probe, "me@gpu"));
        Assert.True(HostRemote.NeedsSudo(HostSetupMethod.SshDocker, probe with { Docker = true }));
        Assert.False(HostRemote.NeedsSudo(HostSetupMethod.SshDocker, probe with { Docker = true, DockerAccess = true }));
    }

    [Theory]
    [InlineData("me@host & calc", "192.168.1.20")]
    [InlineData("me@host\"x", "192.168.1.20")]
    [InlineData("me@192.168.1.20", "8.8.8.8")]
    [InlineData("me@192.168.1.20", "192.168.1.20 & calc")]
    public void Unsafe_targets_and_public_addresses_are_rejected(string ssh, string address) =>
        Assert.Throws<InvalidOperationException>(() =>
            HostSetupCommands.RemoteShell(Target(HostSetupMethod.SshDocker, ssh, address), "setup", true));

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
