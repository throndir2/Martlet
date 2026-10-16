using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Martlet.Core.Network;

namespace Martlet.Core.Tests;

/// <summary>Finding a paired host again after its computer got another address on the home network (HostFind).</summary>
public sealed class HostFindTests
{
    [Fact]
    public void A_find_is_read_back_and_Nearby_finds_are_not_host_finds()
    {
        Assert.True(HostFind.IsQuery(HostFind.Query("abc123"), out var query));
        Assert.Equal("abc123", query);
        Assert.False(HostFind.IsQuery(Encoding.UTF8.GetBytes("{\"martlet\":\"find\",\"v\":1,\"q\":\"abc123\"}"), out _));
        Assert.False(HostFind.IsQuery(Encoding.UTF8.GetBytes("{\"martlet\":\"find-host\",\"v\":2,\"q\":\"abc123\"}"), out _));
        Assert.False(HostFind.IsQuery(Encoding.UTF8.GetBytes("not json"), out _));
        Assert.False(HostFind.IsQuery(new byte[600], out _));
    }

    [Fact]
    public void An_answer_names_host_IDs_only()
    {
        var answer = HostFind.Answer("q1", ["box-1-host", "bad id", "box-1-host", "box-2-host"]);
        using var document = JsonDocument.Parse(answer);
        Assert.Equal(["martlet", "v", "q", "hosts"], document.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(["box-1-host", "box-2-host"], HostFind.ParseAnswer(answer, "q1"));
        Assert.Null(HostFind.ParseAnswer(answer, "another query"));
        Assert.Null(HostFind.ParseAnswer(Encoding.UTF8.GetBytes("{\"martlet\":\"host-here\",\"v\":1,\"q\":\"q1\",\"hosts\":[\"../x\"]}"), "q1"));
        Assert.Null(HostFind.ParseAnswer(Encoding.UTF8.GetBytes("{\"martlet\":\"host-here\",\"v\":1,\"q\":\"q1\",\"hosts\":[]}"), "q1"));
    }

    [Fact]
    public void Only_home_network_and_loopback_senders_are_answered_and_answers_are_limited()
    {
        var recent = new Queue<DateTime>();
        var query = HostFind.Query("q2");
        Assert.Null(HostFindResponder.Reply(query, IPAddress.Parse("203.0.113.9"), ["box-1-host"], recent));
        Assert.Null(HostFindResponder.Reply(query, IPAddress.Parse("192.168.1.30"), [], recent));
        Assert.NotNull(HostFindResponder.Reply(query, IPAddress.Parse("::ffff:192.168.1.30"), ["box-1-host"], recent));
        for (var i = 1; i < 40; i++) Assert.NotNull(HostFindResponder.Reply(query, IPAddress.Loopback, ["box-1-host"], recent));
        Assert.Null(HostFindResponder.Reply(query, IPAddress.Loopback, ["box-1-host"], recent));
    }

    [Fact]
    public async Task A_desktop_finds_the_host_where_it_answers_and_stops_at_the_first_answer()
    {
        using var responder = HostFindResponder.Start(() => ["box-1-host"], IPAddress.Loopback, 0);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var seen = await HostFind.FindAsync(["box-1-host"], TimeSpan.FromSeconds(3), CancellationToken.None, [IPAddress.Loopback], responder.Port);
        Assert.Equal([new HostSighting("box-1-host", IPAddress.Loopback)], seen);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"took {clock.ElapsedMilliseconds} ms");
        Assert.True(responder.Answered >= 1);

        // A host nobody asked for is not listed; asking for every host lists it.
        Assert.Empty(await HostFind.FindAsync(["box-2-host"], TimeSpan.FromMilliseconds(400), CancellationToken.None, [IPAddress.Loopback], responder.Port));
        Assert.Equal(["box-1-host"], (await HostFind.FindAsync(null, TimeSpan.FromMilliseconds(400), CancellationToken.None, [IPAddress.Loopback],
            responder.Port)).Select(s => s.HostId));
    }

    [Fact]
    public void A_taken_port_says_so()
    {
        using var responder = HostFindResponder.Start(() => ["box-1-host"], IPAddress.Loopback, 0);
        if (OperatingSystem.IsWindows())
            Assert.Throws<SocketException>(() => HostFindResponder.Start(() => ["box-1-host"], IPAddress.Loopback, responder.Port).Dispose());
    }
}
