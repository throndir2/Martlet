using Martlet.Avatar.Audio2Face.Remote;

namespace Martlet.Gateway.Tests;

/// <summary>The desktop's paired connections to one host share kept TLS connections (no new TCP and TLS connection for every
/// check or sync), and a host that stops is still noticed at once.</summary>
public sealed class HostConnectionPoolTests
{
    private static async Task<(Audio2FaceHostPairing Pairing, string Secret)> PairAsync(GatewayTestHost host)
    {
        var card = host.OpenPairing(GatewayRole.Voice, "desktop-test");
        return await Audio2FaceHostClient.PairAsync(host.Origin.CanonicalOrigin, card.HostId, card.SpkiFingerprint, "desktop-test",
            card.PairingId, card.Token.Reveal());
    }

    [Fact]
    public async Task Paired_connections_made_one_per_check_share_one_kept_connection_to_the_host()
    {
        await using var host = await GatewayTestHost.StartAsync();
        var (pairing, secret) = await PairAsync(host);
        var before = HostRoutes.For(pairing.Origin)?.Connections ?? 0;

        // As the desktop does: a new paired connection for each regular check, two requests each, then disposed.
        for (var check = 0; check < 8; check++)
        {
            using var connection = new Audio2FaceHostConnection(pairing, secret, host.Clock);
            await connection.ReadRoutesAsync();
            await connection.ReadMachineReportAsync();
        }

        var route = HostRoutes.For(pairing.Origin);
        Assert.NotNull(route);
        Assert.Equal("home", route.Route);
        Assert.Equal(before + 1, route.Connections);
    }

    [Fact]
    public async Task A_wrong_pinned_key_never_shares_the_right_keys_connection()
    {
        await using var host = await GatewayTestHost.StartAsync();
        var (pairing, secret) = await PairAsync(host);
        using (var connection = new Audio2FaceHostConnection(pairing, secret, host.Clock)) await connection.ReadRoutesAsync();

        var wrongPin = pairing with { SpkiFingerprint = "sha256:" + new string('0', 64) };
        using var spoofed = new Audio2FaceHostConnection(wrongPin, secret, host.Clock);
        Assert.Equal("host.unreachable", (await Assert.ThrowsAsync<Audio2FaceHostException>(() => spoofed.ReadRoutesAsync())).Code);
    }

    [Fact]
    public async Task A_host_that_stops_is_refused_on_the_next_check_not_held_on_a_kept_connection()
    {
        await using var host = await GatewayTestHost.StartAsync();
        var (pairing, secret) = await PairAsync(host);
        using (var connection = new Audio2FaceHostConnection(pairing, secret, host.Clock)) await connection.ReadRoutesAsync();

        await host.Listener.DisposeAsync();

        using var after = new Audio2FaceHostConnection(pairing, secret, host.Clock);
        var failure = await Assert.ThrowsAsync<Audio2FaceHostException>(() => after.ReadRoutesAsync());
        Assert.Equal("host.unreachable", failure.Code);
        var route = HostRoutes.For(pairing.Origin);
        Assert.NotNull(route);
        Assert.Equal("none", route.Route);
        Assert.Contains("didn't answer (refused)", route.Error, StringComparison.Ordinal);
    }
}
