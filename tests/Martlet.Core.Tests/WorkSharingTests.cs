using System.Runtime.CompilerServices;
using Martlet.Core.Cluster;
using Martlet.Core.Settings;

namespace Martlet.Core.Tests;

public sealed class WorkSharingTests
{
    // The owner's network: machine 1 (companion, Thinking, Chatterbox), machine 2 (companion, Deep thinking, no voice),
    // machine 3 (companion, Chatterbox) and machine 4 (lip-sync, pictures).
    private static readonly WorkPlace[] Voices = [new("m1-host", Jobs: 2), new("m3-host")];

    [Fact]
    public void Without_choices_the_planned_computer_goes_first_then_own_then_least_busy()
    {
        var settings = new WorkSharingSettings();
        Assert.Equal(["m1-host", "m3-host"], WorkSharing.Order(settings, WorkSharingJobs.Speaking, "desk-2", "m1-host", Voices));
        // Machine 3 speaks with machine 1's voice by the plan, and its own next.
        WorkPlace[] fromThree = [new("m1-host", Jobs: 2), new("m3-host", Own: true), new("m5-host")];
        Assert.Equal(["m1-host", "m3-host", "m5-host"], WorkSharing.Order(settings, WorkSharingJobs.Speaking, "desk-3", "m1-host", fromThree));
        // Thinking isn't shared unless chosen: only its own computer.
        Assert.Equal(["m1-host"], WorkSharing.Order(settings, WorkSharingJobs.Thinking, "desk-2", "m1-host", Voices));
    }

    [Fact]
    public void An_order_with_this_pc_puts_each_companions_own_computer_first()
    {
        var settings = new WorkSharingSettings().With(new WorkSharingJob
        {
            Job = WorkSharingJobs.Speaking, Order = [WorkSharingSettings.ThisPc, "m3-host", "m1-host"]
        });
        WorkPlace[] fromOne = [new("m1-host", Own: true), new("m3-host")];
        Assert.Equal(["m1-host", "m3-host"], WorkSharing.Order(settings, WorkSharingJobs.Speaking, "desk-1", "m1-host", fromOne));
        // Machine 2 has no voice of its own: machine 3, then machine 1.
        Assert.Equal(["m3-host", "m1-host"], WorkSharing.Order(settings, WorkSharingJobs.Speaking, "desk-2", "m1-host", Voices));
    }

    [Fact]
    public void Never_and_kept_computers_are_left_out_unless_nothing_is_left()
    {
        var settings = new WorkSharingSettings()
            .With(new WorkSharingJob { Job = WorkSharingJobs.Speaking, Never = ["m3-host"] })
            .With(new WorkSharingHost { HostId = "m1-host", OnlyFor = ["desk-1"] });
        Assert.Equal(["m1-host"], WorkSharing.Order(settings, WorkSharingJobs.Speaking, "desk-1", "m1-host", Voices));
        // Machine 1 is kept for desk-1 and machine 3 never speaks: desk-2's own planned computer still does it.
        Assert.Equal(["m1-host"], WorkSharing.Order(settings, WorkSharingJobs.Speaking, "desk-2", "m1-host", Voices));
        var kept = new WorkSharingSettings().With(new WorkSharingHost { HostId = "m1-host", OnlyFor = ["desk-1"] });
        Assert.Equal(["m3-host"], WorkSharing.Order(kept, WorkSharingJobs.Speaking, "desk-2", "m1-host", Voices));
        Assert.False(kept.Allows("m1-host", "desk-2"));
        Assert.True(kept.Allows("m3-host", "desk-2"));
    }

    [Fact]
    public void Turning_sharing_off_keeps_only_the_planned_computer()
    {
        var settings = new WorkSharingSettings().With(new WorkSharingJob { Job = WorkSharingJobs.Speaking, Share = false });
        Assert.Equal(["m1-host"], WorkSharing.Order(settings, WorkSharingJobs.Speaking, "desk-2", "m1-host", Voices));
        var thinking = new WorkSharingSettings().With(new WorkSharingJob { Job = WorkSharingJobs.Thinking, Share = true });
        Assert.Equal(["m1-host", "m3-host"], WorkSharing.Order(thinking, WorkSharingJobs.Thinking, "desk-2", "m1-host", Voices));
    }

    [Fact]
    public void Settings_share_canonically_and_round_trip()
    {
        var settings = new WorkSharingSettings()
            .With(new WorkSharingJob { Job = WorkSharingJobs.Speaking, Order = ["m3-host", "m3-host", "m1-host"], Never = ["z", "a"] })
            .With(new WorkSharingJob { Job = WorkSharingJobs.Listening, Share = true })
            .With(new WorkSharingHost { HostId = "m4-host", OnlyFor = ["desk-1"] })
            .With(new WorkSharingHost { HostId = "m2-host", OnlyFor = [] });
        var json = settings.Share();
        Assert.DoesNotContain("listening", json);
        Assert.DoesNotContain("m2-host", json);
        var parsed = WorkSharingSettings.Parse(json)!;
        Assert.Equal(json, parsed.Share());
        Assert.Equal(["m3-host", "m1-host"], parsed.Job(WorkSharingJobs.Speaking).Order);
        Assert.Equal(["a", "z"], parsed.Job(WorkSharingJobs.Speaking).Never);
        Assert.Equal(["desk-1"], parsed.OnlyFor("m4-host"));
        Assert.Null(WorkSharingSettings.Parse("{\"schema_version\":2}"));
        Assert.Null(WorkSharingSettings.Parse("{\"jobs\":[{\"job\":\"bad name!\"}]}"));
        Assert.True(new WorkSharingSettings().IsDefault);

        var directory = Directory.CreateTempSubdirectory("martlet-work-sharing-").FullName;
        try
        {
            Assert.True(settings.Save(directory));
            Assert.Equal(json, WorkSharingSettings.Load(directory).Share());
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void Deep_thinking_skips_computers_kept_for_others_or_never_used()
    {
        DeepThinkingSettings Host(string id) => new()
        {
            Place = DeepThinkingPlace.Host, HostId = id, HostOrigin = "https://192.168.1.2:9443", ModelId = "qwen3:8b",
            HostSpkiFingerprint = "sha256:" + new string('0', 64), HostDeviceId = "desk-2", HostCredentialId = Guid.NewGuid(),
            HostRouteId = SelfHostSetup.DeepThinkingRouteId
        };
        var deep = Host("m2-host").WithPool([Host("m4-host"), Host("m3-host")]);
        var settings = new WorkSharingSettings()
            .With(new WorkSharingHost { HostId = "m4-host", OnlyFor = ["desk-4"] })
            .With(new WorkSharingJob { Job = WorkSharingJobs.DeepThinking, Never = ["m3-host"] });
        var pool = DeepThinkingPool.For(deep, [], settings, "desk-2");
        Assert.Equal(["host:m2-host"], pool.Usable.Select(s => s.Key));
        Assert.Contains("kept for desk-4", pool.Find("host:m4-host")!.Plan.Why);
        Assert.Contains("never uses m3-host", pool.Find("host:m3-host")!.Plan.Why);
        Assert.Equal(3, DeepThinkingPool.For(deep, []).Usable.Count);
    }

    // ---------- the queue ----------

    private sealed class Busy : Exception;
    private sealed class Gone : Exception;
    // The computer keeps its graphics card for a live turn (a host's job.busy with detail live, or job.preempted).
    private sealed class Held : Exception;

    private static WorkRefusal Classify(Exception error) => error switch
    {
        Busy => WorkRefusal.Busy,
        Gone => WorkRefusal.Unavailable,
        Held => WorkRefusal.Preempted,
        _ => WorkRefusal.None
    };

    /// <summary>A computer that runs one request at a time, turns another away at once and takes <paramref name="work"/> for each.</summary>
    private sealed class Computer(string id, TimeSpan work)
    {
        private int running;
        public string Id { get; } = id;
        public int Served;
        public bool Down { get; set; }

        public async IAsyncEnumerable<string> Run(string request, [EnumeratorCancellation] CancellationToken token)
        {
            if (Down) throw new Gone();
            if (Interlocked.CompareExchange(ref running, 1, 0) != 0) throw new Busy();
            try
            {
                Interlocked.Increment(ref Served);
                await Task.Delay(work, token);
                yield return $"{request} by {Id}";
            }
            finally { Volatile.Write(ref running, 0); }
        }

        /// <summary>Holds the computer busy (another companion PC's request) until the returned task's token is canceled.</summary>
        public void Occupy() => Interlocked.Exchange(ref running, 1);
        public void Free() => Volatile.Write(ref running, 0);
    }

    private static async Task<string> Ask(WorkQueue queue, string request, params Computer[] order)
    {
        await foreach (var answer in queue.StreamAsync("speaking", order, c => c.Id, (c, t) => c.Run(request, t), Classify,
            DateTimeOffset.UtcNow.AddSeconds(10), null, CancellationToken.None))
            return answer;
        throw new InvalidOperationException();
    }

    [Fact]
    public async Task A_busy_computer_is_passed_over_for_the_next()
    {
        var queue = new WorkQueue();
        WorkRoute? route = null;
        queue.Rerouted += (_, r) => route = r;
        var one = new Computer("m1-host", TimeSpan.FromMilliseconds(10));
        var three = new Computer("m3-host", TimeSpan.FromMilliseconds(10));
        Assert.Equal("a by m1-host", await Ask(queue, "a", one, three));
        Assert.Null(route);
        one.Occupy();
        Assert.Equal("b by m3-host", await Ask(queue, "b", one, three));
        Assert.Equal(new WorkRoute("m3-host", 1, 1, 0, route!.Waited), route);
    }

    [Fact]
    public async Task When_all_are_busy_whichever_frees_first_takes_the_request()
    {
        var queue = new WorkQueue { Retry = TimeSpan.FromMilliseconds(20) };
        var one = new Computer("m1-host", TimeSpan.FromMilliseconds(5));
        var three = new Computer("m3-host", TimeSpan.FromMilliseconds(5));
        one.Occupy();
        three.Occupy();
        var asked = Ask(queue, "c", one, three);
        await Task.Delay(100);
        Assert.False(asked.IsCompleted);
        Assert.Equal(1, queue.Waiting);
        three.Free();
        Assert.Equal("c by m3-host", await asked);
        Assert.Equal(0, queue.Waiting);
    }

    [Fact]
    public async Task Requests_at_once_spread_over_the_computers_and_queue_for_the_rest()
    {
        var queue = new WorkQueue { Retry = TimeSpan.FromMilliseconds(10) };
        var one = new Computer("m1-host", TimeSpan.FromMilliseconds(150));
        var three = new Computer("m3-host", TimeSpan.FromMilliseconds(150));
        var answers = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => Ask(queue, "r" + i, one, three)));
        Assert.Equal(2, answers.Count(a => a.EndsWith("m1-host")));
        Assert.Equal(2, answers.Count(a => a.EndsWith("m3-host")));
    }

    [Fact]
    public async Task An_unreachable_computer_is_skipped_and_a_real_failure_is_not_retried()
    {
        var queue = new WorkQueue();
        var one = new Computer("m1-host", TimeSpan.Zero) { Down = true };
        var three = new Computer("m3-host", TimeSpan.Zero);
        Assert.Equal("d by m3-host", await Ask(queue, "d", one, three));
        three.Down = true;
        await Assert.ThrowsAsync<Gone>(() => Ask(queue, "e", one, three));
        static async IAsyncEnumerable<string> Broken([EnumeratorCancellation] CancellationToken token)
        {
            await Task.Yield();
            throw new InvalidOperationException("bad request");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
        var calls = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in queue.StreamAsync("speaking", ["m1-host", "m3-host"], h => h, (_, t) => { calls++; return Broken(t); },
                Classify, DateTimeOffset.UtcNow.AddSeconds(5), null, CancellationToken.None)) { }
        });
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Busy_until_the_deadline_gives_up_with_the_busy_refusal()
    {
        var queue = new WorkQueue { Retry = TimeSpan.FromMilliseconds(10) };
        var one = new Computer("m1-host", TimeSpan.Zero);
        one.Occupy();
        await Assert.ThrowsAsync<Busy>(async () =>
        {
            await foreach (var _ in queue.StreamAsync("speaking", [one], c => c.Id, (c, t) => c.Run("f", t), Classify,
                DateTimeOffset.UtcNow.AddMilliseconds(80), null, CancellationToken.None)) { }
        });
    }

    [Fact]
    public async Task A_computer_this_pc_already_uses_goes_last_for_its_next_request()
    {
        var queue = new WorkQueue();
        var one = new Computer("m1-host", TimeSpan.FromMilliseconds(200));
        var three = new Computer("m3-host", TimeSpan.FromMilliseconds(10));
        var first = Ask(queue, "g", one, three);
        await Task.Delay(50);
        Assert.Equal(1, queue.Running("speaking", "m1-host"));
        Assert.Equal("h by m3-host", await Ask(queue, "h", one, three));
        // It never asked machine 1 for the second request: one round trip saved.
        Assert.Equal(1, one.Served);
        Assert.Equal("g by m1-host", await first);
        Assert.Equal(0, queue.Running("speaking", "m1-host"));
    }

    // ---------- the live turn first ----------

    /// <summary>A computer's Ollama: one request at a time, another turned away at once; a background request works until it is
    /// stopped, a live one answers quickly. <see cref="HoldForLive"/>: it refuses background work for a live turn;
    /// <see cref="RefuseLive"/>: it refuses this PC's live requests too (another companion PC's live turn holds its card).</summary>
    private sealed class Ollama(string id)
    {
        private int running;
        public string Id { get; } = id;
        public bool HoldForLive { get; set; }
        public bool RefuseLive { get; set; }
        public List<string> Started { get; } = [];

        public async IAsyncEnumerable<string> Run(string request, bool background, [EnumeratorCancellation] CancellationToken token)
        {
            if (background && HoldForLive || !background && RefuseLive) throw new Held();
            if (Interlocked.CompareExchange(ref running, 1, 0) != 0) throw new Busy();
            try
            {
                lock (Started) Started.Add(request);
                yield return $"{request} started";
                await Task.Delay(background ? Timeout.InfiniteTimeSpan : TimeSpan.FromMilliseconds(20), token);
                yield return $"{request} by {Id}";
            }
            finally { Volatile.Write(ref running, 0); }
        }

        public void Occupy() => Interlocked.Exchange(ref running, 1);
        public void Free() => Volatile.Write(ref running, 0);
    }

    private static async Task<string> Stream(WorkQueue queue, string request, WorkPriority priority, params Ollama[] order)
    {
        var last = "";
        await foreach (var answer in queue.StreamAsync("thinking", order, c => c.Id,
            (c, t) => c.Run(request, priority == WorkPriority.Background, t), Classify, DateTimeOffset.UtcNow.AddSeconds(10), null,
            CancellationToken.None, priority))
            last = answer;
        return last;
    }

    [Fact]
    public async Task A_live_request_stops_this_pcs_own_background_request_instead_of_waiting_behind_it()
    {
        var queue = new WorkQueue { Retry = TimeSpan.FromMilliseconds(10) };
        (string Lane, string Host)? stopped = null;
        queue.Preempted += (lane, host) => stopped = (lane, host);
        var host = new Ollama("diva");
        var background = Stream(queue, "remembering", WorkPriority.Background, host);
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (queue.Background("thinking", "diva") == 0 && waited.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(5);
        Assert.Equal(1, queue.Background("thinking", "diva"));
        Assert.Equal("reply by diva", await Stream(queue, "reply", WorkPriority.Live, host));
        await Assert.ThrowsAsync<WorkPreemptedException>(() => background);
        Assert.Equal(("thinking", "diva"), stopped);
        Assert.Equal(1, queue.Stopped);
        Assert.Equal(["remembering", "reply"], host.Started);
        Assert.Equal(0, queue.Background("thinking", "diva"));
        Assert.Equal(0, queue.Running("thinking", "diva"));
    }

    [Fact]
    public async Task A_background_request_waits_while_a_live_request_of_its_lane_waits()
    {
        var queue = new WorkQueue { Retry = TimeSpan.FromMilliseconds(10) };
        var host = new Ollama("diva");
        // Another companion PC's reply holds the computer: this PC's live request waits for it.
        host.Occupy();
        var live = Stream(queue, "reply", WorkPriority.Live, host);
        await Task.Delay(60);
        var background = Stream(queue, "naming", WorkPriority.Background, host);
        await Task.Delay(60);
        Assert.Equal(2, queue.Waiting);
        Assert.Empty(host.Started);
        host.Free();
        Assert.Equal("reply by diva", await live);
        // The background request only starts once no live request waits, and then runs until it is stopped.
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (host.Started.Count < 2 && waited.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(5);
        Assert.Equal(["reply", "naming"], host.Started);
        Assert.Equal("reply2 by diva", await Stream(queue, "reply2", WorkPriority.Live, host));
        await Assert.ThrowsAsync<WorkPreemptedException>(() => background);
    }

    [Fact]
    public async Task Background_work_a_computer_holds_off_for_a_live_turn_goes_on_later_and_a_live_request_waits_for_it()
    {
        var queue = new WorkQueue { Retry = TimeSpan.FromMilliseconds(10) };
        var host = new Ollama("diva") { HoldForLive = true };
        await Assert.ThrowsAsync<WorkPreemptedException>(() => Stream(queue, "remembering", WorkPriority.Background, host));
        Assert.Empty(host.Started);
        // To a live request the same refusal (another companion PC's live turn holds the card) means busy: it waits.
        host.RefuseLive = true;
        var live = Stream(queue, "reply", WorkPriority.Live, host);
        await Task.Delay(50);
        Assert.False(live.IsCompleted);
        host.RefuseLive = false;
        Assert.Equal("reply by diva", await live);
    }
}
