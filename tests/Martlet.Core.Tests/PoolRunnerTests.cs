using Martlet.Core.Cluster;

namespace Martlet.Core.Tests;

/// <summary>The one runner of the ordered pools (PoolRunner, PoolRouting.Stops): stops in the list's order, each key once, and a request
/// that goes to the first stop that takes it, with the stop, its position and the busy and unavailable tries it passed.</summary>
public sealed class PoolRunnerTests
{
    private sealed class Refused(WorkRefusal refusal) : Exception(refusal.ToString())
    {
        public WorkRefusal Refusal { get; } = refusal;
    }

    private static WorkRefusal Classify(Exception error) => error is Refused refused ? refused.Refusal : WorkRefusal.None;

    private static PoolRunner<string> Runner(WorkQueue queue) => new("test-lane", stop => stop, Classify, WorkPriority.Background, queue);

    [Fact]
    public void Stops_keep_the_list_order_and_each_key_once()
    {
        var list = new PoolList
        {
            Area = PoolAreas.Singing.Id,
            Members =
            [
                PoolMember.Computer("m3-host"), PoolMember.Gpu("m3-host", 2), PoolMember.ThisPc(), PoolMember.Computer("off") with { Off = true },
                PoolMember.Computer("kept") with { OnlyFor = ["desk-2"] }, PoolMember.Computer("unpaired"), PoolMember.Computer("m4-host")
            ]
        };
        string[] paired = ["m3-host", "desk-host", "m4-host", "off", "kept"];
        PoolStop? ByHost(PoolMember member) =>
            PoolRouting.HostOf(member, "desk-host") is { } host && paired.Contains(host) ? new(member, host) : null;

        Assert.Equal(["m3-host", "desk-host", "m4-host"], PoolRouting.Stops(PoolAreas.Singing, list, "desk-1", ByHost).Select(p => p.Key));
        Assert.Equal(["host:m3-host", "host:m3-host#gpu2", "this-pc", "host:m4-host"],
            PoolRouting.Stops(PoolAreas.Singing, list, "desk-1", m => ByHost(m) is { } p ? p with { Key = m.Key } : null).Select(p => p.Key));
        Assert.Equal(["m3-host", "m4-host"], PoolRouting.Stops(PoolAreas.Singing, list, "desk-1",
            m => PoolRouting.HostOf(m, null) is { } host && paired.Contains(host) ? new(m, host) : null).Select(p => p.Key));
        Assert.Null(PoolRouting.HostOf(PoolMember.Cloud("openrouter", "model"), "desk-host"));
        Assert.Empty(PoolRouting.Stops(PoolAreas.Singing, null, "desk-1", ByHost));
    }

    [Fact]
    public async Task A_request_goes_to_the_first_stop_that_takes_it_and_says_where()
    {
        var queue = new WorkQueue { Retry = TimeSpan.FromMilliseconds(5) };
        List<string> asked = [];
        Func<string, PoolTry, CancellationToken, Task<string>> Attempt(Func<string, int, WorkRefusal?> refuse) => (stop, tried, _) =>
        {
            int count;
            lock (asked)
            {
                asked.Add(stop);
                count = asked.Count(p => p == stop);
            }
            return refuse(stop, count) is { } refusal ? Task.FromException<string>(new Refused(refusal))
                : Task.FromResult($"{stop}@{tried.Position}/{tried.Busy}/{tried.Unavailable}");
        };

        var first = await Runner(queue).RunAsync(["a", "b", "c"], Attempt((_, _) => null), TimeSpan.Zero, CancellationToken.None);
        Assert.Equal(("a", 0, 0, 0, "a@0/0/0"), (first.Stop, first.Position, first.Busy, first.Unavailable, first.Result));
        Assert.Equal(["a"], asked);

        asked.Clear();
        var moved = await Runner(queue).RunAsync(["a", "b", "c"],
            Attempt((stop, _) => stop switch { "a" => WorkRefusal.Busy, "b" => WorkRefusal.Unavailable, _ => null }), TimeSpan.Zero,
            CancellationToken.None);
        Assert.Equal(("c", 2, 1, 1, "c@2/1/1"), (moved.Stop, moved.Position, moved.Busy, moved.Unavailable, moved.Result));

        // Every stop busy: the request waits for the first to free.
        asked.Clear();
        var waited = await Runner(queue).RunAsync(["a", "b"], Attempt((stop, n) => stop == "b" || n < 3 ? WorkRefusal.Busy : null),
            TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.Equal("a", waited.Stop);
        Assert.True(waited.Busy >= 3);

        // A real failure ends the request; no other stop tries it.
        asked.Clear();
        await Assert.ThrowsAsync<Refused>(() => Runner(queue).RunAsync(["a", "b"],
            Attempt((stop, _) => stop == "a" ? WorkRefusal.None : null), TimeSpan.Zero, CancellationToken.None));
        Assert.Equal(["a"], asked);

        // No stop took it: the last refusal.
        var none = await Assert.ThrowsAsync<Refused>(() => Runner(queue).RunAsync(["a", "b"],
            Attempt((_, _) => WorkRefusal.Unavailable), TimeSpan.Zero, CancellationToken.None));
        Assert.Equal(WorkRefusal.Unavailable, none.Refusal);
    }
}
