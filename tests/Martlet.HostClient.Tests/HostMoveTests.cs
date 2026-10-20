using Martlet.HostClient;

namespace Martlet.Avatar.Audio2Face.Tests;

/// <summary>Where a host answers after its computer got another address on the home network (HostRoutes.Moved). The end-to-end
/// look on the network with a real gateway is rehearsed by MCP host_move_selftest (Martlet.NodeLinkCheck move).</summary>
public sealed class HostMoveTests
{
    private const string Spki = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void A_saved_new_address_is_dialed_and_shown_and_a_later_find_is_kept_over_the_saved_one()
    {
        const string origin = "https://10.250.1.20:9443";
        HostRoutes.Remember(origin, "box-1-host", Spki, "10.250.1.37:9443");
        Assert.Equal("10.250.1.37:9443", HostRoutes.For(origin)!.MovedTo);
        Assert.Equal("box-1-host", HostRoutes.For(origin)!.HostId);

        var moves = new List<HostMove>();
        void Seen(HostMove move) { if (move.Origin == origin) lock (moves) moves.Add(move); }
        HostRoutes.Moved += Seen;
        try
        {
            HostRoutes.Move(origin, Spki, "10.250.1.41:9443");
            // Loading the pairings again doesn't put back what was saved before this PC found the host elsewhere.
            HostRoutes.Remember(origin, "box-1-host", Spki, "10.250.1.37:9443");
            Assert.Equal("10.250.1.41:9443", HostRoutes.For(origin)!.MovedTo);
            HostRoutes.Move(origin, Spki, "10.250.1.41:9443");
            // Back at its paired address: nothing is dialed instead.
            HostRoutes.Move(origin, Spki, "10.250.1.20:9443");
            Assert.Null(HostRoutes.For(origin)!.MovedTo);
        }
        finally { HostRoutes.Moved -= Seen; }
        Assert.Equal([new HostMove(origin, "box-1-host", "10.250.1.37:9443", "10.250.1.41:9443"),
            new HostMove(origin, "box-1-host", "10.250.1.41:9443", null)], moves);
    }
}
