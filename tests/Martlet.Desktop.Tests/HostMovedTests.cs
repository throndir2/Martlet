using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Avatar.Hosting;
using Martlet.Desktop;

namespace Martlet.Desktop.Tests;

/// <summary>A paired host found at another address on the home network (its computer got a new one) keeps that address with its
/// pairing (hosts.json movedTo), and an SSH target that named the old address moves with it.</summary>
public sealed class HostMovedTests
{
    private const string Spki = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static PairedHost Host(string id, string ip, HostSetupMethod method = HostSetupMethod.SshDocker, string? ssh = null) => new()
    {
        Pairing = new AvatarRemoteHost
        {
            Origin = $"https://{ip}:9443", HostId = id, SpkiFingerprint = Spki, DeviceId = "home-pc", CredentialId = new string('C', 22)
        },
        Method = method, SshTarget = ssh, SshHostKey = ssh is null ? null : "ssh-ed25519 SHA256:abc"
    };

    [Fact]
    public void An_SSH_target_that_named_the_old_address_moves_with_the_host_and_back()
    {
        var host = Host("box-1-host", "192.168.79.20", ssh: "mart@192.168.79.20");
        var moved = HostRegistry.Moved(host, "192.168.79.37:9443");
        Assert.Equal("192.168.79.37:9443", moved.MovedTo);
        Assert.Equal("mart@192.168.79.37", moved.SshTarget);
        // The SSH host key saved with the host still has to match at the new address.
        Assert.Equal(host.SshHostKey, moved.SshHostKey);
        Assert.Equal(host.Pairing, moved.Pairing);

        var again = HostRegistry.Moved(moved, "192.168.79.41:9443");
        Assert.Equal("mart@192.168.79.41", again.SshTarget);
        var back = HostRegistry.Moved(again, null);
        Assert.Null(back.MovedTo);
        Assert.Equal("mart@192.168.79.20", back.SshTarget);
        Assert.Same(back, HostRegistry.Moved(back, "192.168.79.20:9443"));
    }

    [Fact]
    public void An_SSH_target_that_names_the_computer_or_another_address_stays()
    {
        Assert.Equal("mart@box-1.local", HostRegistry.Moved(Host("box-1-host", "192.168.79.20", ssh: "mart@box-1.local"), "192.168.79.37:9443").SshTarget);
        Assert.Equal("mart@192.168.79.5:2222",
            HostRegistry.Moved(Host("box-1-host", "192.168.79.20", ssh: "mart@192.168.79.5:2222"), "192.168.79.37:9443").SshTarget);
        Assert.Equal("mart@192.168.79.37:2222",
            HostRegistry.Moved(Host("box-1-host", "192.168.79.20", ssh: "mart@192.168.79.20:2222"), "192.168.79.37:9443").SshTarget);
    }

    [Fact]
    public void Where_a_host_moved_to_is_saved_dialed_after_a_restart_and_keeps_this_PCs_own_host()
    {
        using var scope = new AvatarHostingTests.Scope();
        HostRegistry.Save(scope.DirectoryPath,
        [
            HostRegistry.Moved(Host("box-1-host", "192.168.80.20", ssh: "mart@192.168.80.20"), "192.168.80.37:9443"),
            HostRegistry.Moved(Host("this-pc-host", "192.168.80.5", HostSetupMethod.ThisPcDocker), "192.168.80.9:9443")
        ]);
        var loaded = HostRegistry.Load(scope.DirectoryPath, thisPcAddress: "192.168.80.9");
        Assert.Equal("192.168.80.37:9443", loaded[0].MovedTo);
        Assert.Equal("mart@192.168.80.37", loaded[0].SshTarget);
        Assert.Equal("192.168.80.37:9443", HostRoutes.For("https://192.168.80.20:9443", Spki)!.MovedTo);
        // This PC's own host service, moved to this PC's new address, is still this PC's (not another computer's).
        Assert.Equal(HostSetupMethod.ThisPcDocker, loaded[1].Method);
        Assert.Equal(HostSetupMethod.Agent, HostRegistry.Load(scope.DirectoryPath, thisPcAddress: "192.168.80.77")[1].Method);
    }
}
