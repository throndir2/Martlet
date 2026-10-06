using System.Text;
using Martlet.Core.Access;
using Martlet.Core.Network;
using Martlet.Gateway.Persistence;

namespace Martlet.Gateway.Host.Linux.Tests;

public sealed class SignInCommandTests
{
    /// <summary>Answers each ReadLine from a function, so a line can depend on what the command printed before it.</summary>
    private sealed class ComputedInput(params Func<string?>[] lines) : TextReader
    {
        private int next;
        public override string? ReadLine() => next < lines.Length ? lines[next++]() : null;
    }

    private static string[] Args(string command, params string[] extra) => [command, "--config", "/srv/martlet/host.json", .. extra];

    [Fact]
    public async Task Owner_sets_up_sign_in_and_makes_an_invite_from_the_host()
    {
        using var platform = new FixturePlatform();
        platform.Terminal = new() { Interactive = false };
        using (var init = new StringWriter()) Assert.Equal(0, await platform.Run("owner-init", init));

        using var owner = new StringWriter();
        platform.Input = new ComputedInput(() => "a long owner passphrase",
            () => Totp.Code(owner.ToString().Split('\n').Single(l => l.StartsWith("secret: ", StringComparison.Ordinal))["secret: ".Length..].Trim(),
                DateTimeOffset.UtcNow));
        var ownerExit = await HostApplication.RunAsync(Args("owner-signin-owner", "--user", "owner"), owner, default, platform);
        Assert.True(ownerExit == 0, owner.ToString());
        Assert.Contains("otpauth: otpauth://totp/Martlet", owner.ToString());
        Assert.Contains("Owner account owner set.", owner.ToString());
        var saved = Encoding.UTF8.GetString(platform.Fs.Parent.Children["signin.json"].Bytes);
        Assert.DoesNotContain("a long owner passphrase", saved);

        using (var allow = new StringWriter())
            Assert.Equal(0, await HostApplication.RunAsync(Args("owner-signin-allow", "--provider", "google", "--subject", "1234", "--label", "me@example.net"),
                allow, default, platform));
        using (var status = new StringWriter())
        {
            Assert.Equal(0, await platform.Run("owner-signin-status", status));
            Assert.Contains("\"user\":\"owner\"", status.ToString());
            Assert.Contains("me@example.net", status.ToString());
            Assert.Contains("\"recoveryCodesLeft\":10", status.ToString());
        }
        using (var disallow = new StringWriter())
            Assert.Equal(0, await HostApplication.RunAsync(Args("owner-signin-disallow", "--provider", "google", "--subject", "1234"), disallow, default, platform));
        Assert.DoesNotContain("me@example.net", Encoding.UTF8.GetString(platform.Fs.Parent.Children["signin.json"].Bytes));

        using var invite = new StringWriter();
        Assert.Equal(0, await HostApplication.RunAsync(Args("owner-invite", "--address", "home.example.net", "--label", "Home"), invite, default, platform));
        var parsed = NetworkInvite.Parse(invite.ToString().Split('\n').Single(l => l.StartsWith(NetworkInvite.Prefix, StringComparison.Ordinal)));
        Assert.Equal(["home.example.net:9443"], parsed.Addresses);
        Assert.Equal(platform.Origin.CanonicalOrigin, parsed.Origin);
        Assert.Equal("Home", parsed.Label);

        Assert.Throws<HostInputException>(() => HostOptions.Parse(Args("owner-signin-allow", "--provider", "google")));
        Assert.Throws<HostInputException>(() => HostOptions.Parse(Args("owner-invite", "--address", "user@host:22")));
        Assert.Throws<HostInputException>(() => HostOptions.Parse(Args("owner-signin-owner")));
    }
}
