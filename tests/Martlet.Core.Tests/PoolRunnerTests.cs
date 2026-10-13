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

    [Fact]
    public async Task Every_stop_busy_waits_in_the_shortest_line_once_free_stops_are_tried()
    {
        var queue = new WorkQueue { Retry = TimeSpan.FromMilliseconds(5) };
        List<string> asked = [];
        Func<string, PoolTry, CancellationToken, Task<string>> Attempt(bool shortLineFull) => (stop, tried, _) =>
        {
            lock (asked) asked.Add(tried.Ahead is null ? stop : $"{stop} in line");
            if (tried.Ahead is { } ahead)
                return stop == "c" && shortLineFull ? Task.FromException<string>(new Refused(WorkRefusal.Busy))
                    : Task.FromResult($"{stop} behind {ahead}");
            switch (stop)
            {
                case "a": tried.Line(2); return Task.FromException<string>(new Refused(WorkRefusal.Busy));
                case "b": return Task.FromException<string>(new Refused(WorkRefusal.Unavailable));
                default: tried.Line(1); return Task.FromException<string>(new Refused(WorkRefusal.Busy));
            }
        };

        var queued = await Runner(queue).RunOrWaitInLineAsync(["a", "b", "c"], Attempt(false), 1, CancellationToken.None);
        Assert.Equal(("c", 2, 1, "c behind 1"), (queued.Stop, queued.Position, queued.Ahead, queued.Result));
        Assert.Equal((2, 1), (queued.Busy, queued.Unavailable));
        Assert.Equal(["a", "b", "c", "c in line"], asked);

        // The shortest line turns it away: with one line it gives up, with more it tries the next shortest.
        asked.Clear();
        await Assert.ThrowsAsync<Refused>(() => Runner(queue).RunOrWaitInLineAsync(["a", "b", "c"], Attempt(true), 1, CancellationToken.None));
        asked.Clear();
        var next = await Runner(queue).RunOrWaitInLineAsync(["a", "b", "c"], Attempt(true), int.MaxValue, CancellationToken.None);
        Assert.Equal(("a", 0, 2), (next.Stop, next.Position, next.Ahead));
        Assert.Equal(["a", "b", "c", "c in line", "a in line"], asked);

        // A free stop takes it at once, without a line.
        var free = await Runner(queue).RunOrWaitInLineAsync(["b", "d"], (stop, tried, _) => stop == "b"
            ? Task.FromException<string>(new Refused(WorkRefusal.Unavailable)) : Task.FromResult(stop), 1, CancellationToken.None);
        Assert.Equal(("d", (int?)null, 1), (free.Stop, free.Ahead, free.Unavailable));
    }
}
