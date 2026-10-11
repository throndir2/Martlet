using System.Net;
using System.Text;
using System.Text.Json;
using Martlet.Desktop;

namespace Martlet.Desktop.Tests;

public sealed class NearbyTests
{
    [Fact]
    public void A_find_listens_long_enough_for_a_slow_computer_and_takes_every_host_it_offers()
    {
        // 15 s instead of 1.6 s, with queries sent again while it listens; answers are listed as they come.
        Assert.Equal(TimeSpan.FromSeconds(15), Nearby.FindWindow);
        Assert.True(Nearby.QueryTimes.Length > 2 && Nearby.QueryTimes[^1] < Nearby.FindWindow);
        // A computer offers every host it can remember (64), not 16.
        Assert.Equal(HostRegistry.MaximumHosts, Nearby.MaximumHosts);
        static byte[] Answer(int hosts) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            martlet = "here", v = 1, q = "query", instance = "abc", name = "PC", device = "pc-1", version = "1.2.3",
            hosts = Enumerable.Range(0, hosts).Select(i => $"host-{i:D2}-" + new string('h', 54)).ToArray()
        }));
        var from = IPAddress.Parse("192.168.1.5");
        Assert.Equal(Nearby.MaximumHosts, Nearby.ParseAnswer(Answer(Nearby.MaximumHosts), from, "query")!.Hosts.Count);
        Assert.Null(Nearby.ParseAnswer(Answer(Nearby.MaximumHosts + 1), from, "query"));
    }
}
