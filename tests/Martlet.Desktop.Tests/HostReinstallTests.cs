using Martlet.Avatar.Hosting;
using Martlet.Core.Hardware;
using Martlet.Desktop;

namespace Martlet.Desktop.Tests;

/// <summary>What a PC saves to recognize a host's computer after an OS reinstall (hosts.json recognition) and the answers the
/// run that sets it up again gives (docs/NETWORK.md, "When a host is reinstalled").</summary>
public sealed class HostReinstallTests
{
    private const string Spki = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static PairedHost Host(string id) => new()
    {
        Pairing = new AvatarRemoteHost
        {
            Origin = "https://192.168.1.20:9443", HostId = id, SpkiFingerprint = Spki, DeviceId = "home-pc", CredentialId = new string('C', 22)
        },
        Method = HostSetupMethod.SshNative, SshTarget = "mart@192.168.1.20", SshHostKey = "ssh-ed25519 SHA256:abc"
    };

    [Fact]
    public void Recognition_is_kept_in_hosts_json_and_survives_a_change_of_the_host_entry()
    {
        var directory = Path.Combine(Path.GetTempPath(), "martlet-reinstall-" + Guid.NewGuid().ToString("N"));
        try
        {
            var keys = HardwareKeys.From(new SimulatedTpm().Open).Keys;
            var host = Host("aihost1-host") with
            {
                Recognition = new()
                {
                    Keys = keys, Facts = MachineFacts.Hashes("aihost1-host", "mac=d8:bb:c1:12:34:56\ngpu=GPU-1"), Roles = ["ollama", "stt"],
                    KeysCheckedAt = DateTimeOffset.UnixEpoch
                }
            };
            HostRegistry.Save(directory, [host]);
            var loaded = Assert.Single(HostRegistry.Load(directory));
            Assert.Equal(keys, loaded.Recognition!.Keys);
            Assert.Equal(2, loaded.Recognition.Facts.Count);
            Assert.Equal(["ollama", "stt"], loaded.Recognition.Roles);
            var moved = HostRegistry.Moved(loaded, "192.168.1.37:9443");
            Assert.Same(loaded.Recognition, moved.Recognition);
            var json = File.ReadAllText(Path.Combine(directory, HostRegistry.FileName));
            Assert.Contains("\"recognition\"", json);
            Assert.DoesNotContain("d8:bb", json);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void The_set_up_again_run_trusts_only_the_one_new_SSH_key_and_signs_in_with_the_remembered_password_once()
    {
        var target = HostShellTarget.Parse("mart@192.168.1.20");
        var prompts = new HostReinstall.Prompts("secret", expected: "ssh-ed25519 SHA256:new", approved: true);
        Assert.True(prompts.TrustHostKey(target, "ssh-ed25519 SHA256:new"));
        Assert.Equal("ssh-ed25519 SHA256:new", prompts.OfferedKey);
        // Every later connection of the run must show the same key.
        Assert.False(prompts.TrustHostKey(target, "ssh-ed25519 SHA256:other"));
        Assert.True(prompts.TrustHostKey(target, "ssh-ed25519 SHA256:new"));
        Assert.Equal("secret", prompts.LoginPassword(target, false));
        Assert.Null(prompts.LoginPassword(target, true));
        Assert.Equal(new HostShellSudo("secret", false), prompts.SudoPassword(target, false));
        Assert.Null(new HostReinstall.Prompts(null).LoginPassword(target, false));
        // Not approved by the route checks, or another key than the one seen: only the owner can trust it.
        Assert.False(new HostReinstall.Prompts("secret", expected: "ssh-ed25519 SHA256:new").TrustHostKey(target, "ssh-ed25519 SHA256:new"));
        Assert.False(new HostReinstall.Prompts("secret", expected: "ssh-ed25519 SHA256:new", approved: true).TrustHostKey(target, "ssh-ed25519 SHA256:x"));

        var watch = new HostReinstall.Watch();
        Assert.False(watch.TrustHostKey(target, "ssh-ed25519 SHA256:new"));
        Assert.Equal("ssh-ed25519 SHA256:new", watch.OfferedKey);
        Assert.Contains("sudo sh -c", HostReinstall.FactsCommand);
        Assert.Contains("/sys/class/dmi/id/", HostReinstall.FactsCommand);
    }

    [Fact]
    public void A_password_goes_only_to_the_saved_network_card_at_home_or_over_Tailscale()
    {
        const string id = "aihost1-host";
        var saved = MachineFacts.Hashes(id, "mac=d8:bb:c1:12:34:56\ngpu=GPU-1");
        var card = MachineFacts.HashOf(id, MachineFacts.Mac, "d8:bb:c1:12:34:56");
        var stranger = MachineFacts.HashOf(id, MachineFacts.Mac, "00:11:22:33:44:55");
        Assert.Equal("Go", HostReinstall.BeforePassword(RouteTrust.Home, saved, card).Decision);
        Assert.Equal("Stop", HostReinstall.BeforePassword(RouteTrust.Home, saved, stranger).Decision);
        Assert.Equal("Ask", HostReinstall.BeforePassword(RouteTrust.Home, saved, null).Decision);
        Assert.Equal("Ask", HostReinstall.BeforePassword(RouteTrust.Home, [], card).Decision);
        Assert.Equal("Go", HostReinstall.BeforePassword(RouteTrust.Tailnet, [], null).Decision);
        Assert.Equal("Stop", HostReinstall.BeforePassword(RouteTrust.Other, saved, card).Decision);
    }
}
