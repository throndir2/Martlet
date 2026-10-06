using Martlet.Core.Access;
using Martlet.Gateway.Host.Linux;

namespace Martlet.Gateway.Host.Linux.Tests;

public sealed class ExposureTests
{
    private static Task<int> Run(FixturePlatform platform, StringWriter output, params string[] options) =>
        HostApplication.RunAsync(["owner-exposure", "--config", "/srv/martlet/host.json", .. options], output, default, platform);

    /// <summary>Answers each ReadLine from a function, so the authenticator code can follow the secret the host printed.</summary>
    private sealed class ComputedInput(params Func<string?>[] lines) : TextReader
    {
        private int next;
        public override string? ReadLine() => next < lines.Length ? lines[next++]() : null;
    }

    /// <summary>Sets the owner account (with an authenticator) on the host, as martlet-host owner-signin-owner does.</summary>
    private static async Task SetUpOwnerAsync(FixturePlatform platform)
    {
        using var owner = new StringWriter();
        platform.Input = new ComputedInput(() => "a long owner passphrase",
            () => Totp.Code(owner.ToString().Split('\n').Single(l => l.StartsWith("secret: ", StringComparison.Ordinal))["secret: ".Length..].Trim(),
                DateTimeOffset.UtcNow));
        Assert.Equal(0, await HostApplication.RunAsync(["owner-signin-owner", "--config", "/srv/martlet/host.json", "--user", "owner"], owner, default, platform));
    }

    [Fact]
    public async Task Owner_exposure_saves_outside_addresses_and_serve_applies_and_advertises_them()
    {
        using var platform = new FixturePlatform();
        using var output = new StringWriter();
        platform.Terminal = new() { Interactive = false };
        Assert.Equal(0, await platform.Run("owner-init", output));

        using var shown = new StringWriter();
        Assert.Equal(0, await Run(platform, shown));
        Assert.Contains("outside addresses: none; pairing codes from outside home: refused", shown.ToString());

        // A host becomes a public endpoint only once sign-in is set up on it; how requests are classified needs none.
        using var early = new StringWriter();
        Assert.Equal(5, await Run(platform, early, "--outside", "gpu-box.tailnet.ts.net:9443"));
        Assert.Contains("outside.needs_signin: sign-in isn't set up on this host", early.ToString());
        using var earlyCodes = new StringWriter();
        Assert.Equal(5, await Run(platform, earlyCodes, "--allow-pairing-outside-home", "yes"));
        Assert.False(platform.Fs.Parent.Children.ContainsKey("exposure.json"));
        using var classify = new StringWriter();
        Assert.Equal(0, await Run(platform, classify, "--treat-all-as-outside", "no"));
        await SetUpOwnerAsync(platform);

        using var set = new StringWriter();
        Assert.Equal(0, await Run(platform, set, "--outside", "GPU-Box.tailnet.ts.net:9443", "--outside", "100.101.102.103:9443",
            "--allow-pairing-outside-home", "yes"));
        Assert.Contains("now has outside addresses: gpu-box.tailnet.ts.net:9443, 100.101.102.103:9443; pairing codes from outside home: allowed",
            set.ToString());
        Assert.True(platform.Fs.Parent.Children.ContainsKey("exposure.json"));

        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var serving = new WatchingWriter("serving:");
        var run = platform.Run("serve", serving, cancel.Token);
        await serving.Seen.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var exposure = platform.Owner!.Exposure;
        Assert.Equal(["gpu-box.tailnet.ts.net:9443", "100.101.102.103:9443"], exposure.OutsideAddresses);
        Assert.NotNull(exposure.OutsideAddressesSetAt);
        Assert.True(exposure.AllowPairingOutsideHome);
        Assert.False(exposure.TreatAllAsOutside);
        await cancel.CancelAsync();
        try { await run.WaitAsync(TimeSpan.FromSeconds(10)); } catch (OperationCanceledException) { }

        using var cleared = new StringWriter();
        Assert.Equal(0, await Run(platform, cleared, "--clear-outside", "--treat-all-as-outside", "yes"));
        Assert.Contains("outside addresses: none; pairing codes from outside home: allowed; treat every connection as outside home: yes", cleared.ToString());

        // Sign-in removed later: the addresses stay, outside access is paused, removing addresses works and adding them doesn't.
        using var again = new StringWriter();
        Assert.Equal(0, await Run(platform, again, "--outside", "gpu-box.tailnet.ts.net:9443"));
        platform.Fs.Parent.Children.Remove("signin.json");
        using var paused = new StringWriter();
        Assert.Equal(0, await Run(platform, paused));
        Assert.Contains("outside addresses: gpu-box.tailnet.ts.net:9443", paused.ToString());
        Assert.Contains("Outside access paused: sign-in isn't set up on this host", paused.ToString());
        using var kept = new StringWriter();
        Assert.Equal(0, await Run(platform, kept, "--outside", "gpu-box.tailnet.ts.net:9443", "--treat-all-as-outside", "yes"));
        using var added = new StringWriter();
        Assert.Equal(5, await Run(platform, added, "--outside", "gpu-box.tailnet.ts.net:9443", "--outside", "home.example.net:9443"));
        using var removed = new StringWriter();
        Assert.Equal(0, await Run(platform, removed, "--clear-outside"));
        await SetUpOwnerAsync(platform);

        // martlet-host pair (owner-pair) serves the same choices: a typed code then works from outside home.
        platform.Input = new StringReader("cancel\n");
        using var pairing = new StringWriter();
        Assert.Equal(3, await HostApplication.RunAsync(["owner-pair", "--config", "/srv/martlet/host.json"], pairing, default, platform)
            .WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Contains("Reaching this host from outside home: outside addresses: none; pairing codes from outside home: allowed; " +
            "treat every connection as outside home: yes.", pairing.ToString());
        Assert.Contains("pairing.canceled", pairing.ToString());
    }

    [Theory]
    [InlineData("--outside", "not an address")]
    [InlineData("--allow-pairing-outside-home", "maybe")]
    [InlineData("--outside")]
    [InlineData("--clear-outside", "--outside", "a.example:1")]
    public void Invalid_owner_exposure_options_are_refused(params string[] options) =>
        Assert.Throws<HostInputException>(() => HostOptions.Parse(["owner-exposure", "--config", "/srv/martlet/host.json", .. options]));
}
