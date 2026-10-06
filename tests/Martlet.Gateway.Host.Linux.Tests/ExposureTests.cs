using Martlet.Gateway.Host.Linux;

namespace Martlet.Gateway.Host.Linux.Tests;

public sealed class ExposureTests
{
    private static Task<int> Run(FixturePlatform platform, StringWriter output, params string[] options) =>
        HostApplication.RunAsync(["owner-exposure", "--config", "/srv/martlet/host.json", .. options], output, default, platform);

    [Fact]
    public async Task Owner_exposure_saves_outside_addresses_and_serve_applies_and_advertises_them()
    {
        using var platform = new FixturePlatform();
        using var output = new StringWriter();
        platform.Terminal = new() { Interactive = false };
        Assert.Equal(0, await platform.Run("owner-init", output));

        using var shown = new StringWriter();
        Assert.Equal(0, await Run(platform, shown));
        Assert.Contains("outside addresses: none; pairing from outside home: refused", shown.ToString());

        using var set = new StringWriter();
        Assert.Equal(0, await Run(platform, set, "--outside", "GPU-Box.tailnet.ts.net:9443", "--outside", "100.101.102.103:9443",
            "--allow-pairing-outside-home", "yes"));
        Assert.Contains("now has outside addresses: gpu-box.tailnet.ts.net:9443, 100.101.102.103:9443; pairing from outside home: allowed",
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
        Assert.Contains("outside addresses: none; pairing from outside home: allowed; treat every connection as outside home: yes", cleared.ToString());
    }

    [Theory]
    [InlineData("--outside", "not an address")]
    [InlineData("--allow-pairing-outside-home", "maybe")]
    [InlineData("--outside")]
    [InlineData("--clear-outside", "--outside", "a.example:1")]
    public void Invalid_owner_exposure_options_are_refused(params string[] options) =>
        Assert.Throws<HostInputException>(() => HostOptions.Parse(["owner-exposure", "--config", "/srv/martlet/host.json", .. options]));
}
