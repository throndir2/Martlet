using Martlet.Avatar.Hosting;
using Martlet.Core.Settings;
using Martlet.Desktop;

namespace Martlet.Desktop.Tests;

/// <summary>A saved job keeps its host's pairing as it was; pairing the host again (or removing it from the network and adding it
/// back) gives this PC a new pairing, and connections use that one (<see cref="HostPairingChoice"/>).</summary>
public sealed class HostPairingChoiceTests
{
    private const string OldSpki = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string NewSpki = "sha256:fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";

    private static AvatarRemoteHost Pairing(string host, string origin, string spki, string device, Guid credential) =>
        HostPairingCredential.Saved(origin, host, spki, device, credential);

    [Fact]
    public void A_job_saved_before_the_host_was_paired_again_uses_this_pcs_current_pairing()
    {
        // The Thinking route and a Thinking pool member saved with the first pairing; the owner removed the host and added it back.
        var saved = Pairing("aibox02-host", "https://192.168.0.219:9443", OldSpki, "desktop-gackto", Guid.NewGuid());
        var now = Pairing("aibox02-host", "https://192.168.0.220:9443", NewSpki, "desktop-gackto-2", Guid.NewGuid());
        PairedHost[] hosts = [new() { Pairing = Pairing("aibox01-host", "https://192.168.0.197:9443", OldSpki, "desktop-gackto", Guid.NewGuid()) },
            new() { Pairing = now }];

        var used = HostPairingCredential.Current(saved, hosts, out var paired);

        Assert.True(paired);
        Assert.Same(now, used);
    }

    [Fact]
    public void A_job_for_a_host_this_pc_isnt_paired_with_keeps_its_saved_pairing()
    {
        var saved = Pairing("aibox02-host", "https://192.168.0.219:9443", OldSpki, "desktop-gackto", Guid.NewGuid());
        PairedHost[] hosts = [new() { Pairing = Pairing("aibox01-host", "https://192.168.0.197:9443", OldSpki, "desktop-gackto", Guid.NewGuid()) }];

        var used = HostPairingCredential.Current(saved, hosts, out var paired);

        Assert.False(paired);
        Assert.Same(saved, used);
        Assert.Same(saved, HostPairingChoice.For(saved, []));
    }

    [Fact]
    public void The_log_says_why_a_host_key_is_missing_and_what_to_do()
    {
        var forgotten = HostPairingCredential.MissingText("aibox02-host", false, CredentialError.Missing);
        Assert.Contains("isn't paired with it now", forgotten, StringComparison.Ordinal);
        Assert.Contains("until you add aibox02-host again (Devices › Add a computer)", forgotten, StringComparison.Ordinal);

        var lost = HostPairingCredential.MissingText("aibox02-host", true, CredentialError.Missing);
        Assert.Contains("can't be read from Windows Credential Manager (Missing)", lost, StringComparison.Ordinal);
        Assert.Contains("until you pair aibox02-host again", lost, StringComparison.Ordinal);
    }
}
