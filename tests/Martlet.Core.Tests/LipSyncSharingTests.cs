using System.Runtime.CompilerServices;
using Martlet.Core.Cluster;

namespace Martlet.Core.Tests;

public sealed class LipSyncSharingTests
{
    private sealed class Busy : Exception;
    private sealed class Gone : Exception;
    private sealed class Broken : Exception;

    private static WorkRefusal Classify(Exception error) => error switch
    {
        Busy => WorkRefusal.Busy,
        Gone => WorkRefusal.Unavailable,
        _ => WorkRefusal.None
    };

    // A computer's Audio2Face relay: one chunk at a time, another turned away at once.
    private sealed class Face(string id, int workMs)
    {
        private int running;
        public string Id { get; } = id;
        public bool Down { get; init; }
        public bool Fails { get; init; }
        public int Served;

        public async IAsyncEnumerable<string> Animate([EnumeratorCancellation] CancellationToken token)
        {
            if (Down) throw new Gone();
            if (Fails) throw new Broken();
            if (Interlocked.CompareExchange(ref running, 1, 0) != 0) throw new Busy();
            try
            {
                Interlocked.Increment(ref Served);
                await Task.Delay(workMs, token);
                yield return Id;
            }
            finally { Volatile.Write(ref running, 0); }
        }
    }

    private static async Task<string?> Chunk(LipSyncSharing pool, WorkQueue queue, Guid turn, IReadOnlyList<Face> faces, int waitMs = 500)
    {
        await foreach (var by in pool.ChunkAsync(queue, turn, faces, f => f.Id, (f, t) => f.Animate(t), Classify,
            TimeSpan.FromMilliseconds(waitMs), null, CancellationToken.None))
            return by;
        return null;
    }

    [Fact]
    public void Lip_sync_is_shared_by_default_but_has_no_devices_card()
    {
        Assert.True(WorkSharingJobs.SharedByDefault(WorkSharingJobs.LipSync));
        Assert.Equal("Lip-sync", WorkSharingJobs.Title(WorkSharingJobs.LipSync));
        Assert.DoesNotContain(WorkSharingJobs.LipSync, WorkSharingJobs.All);
        Assert.Equal(["m4-host", "m5-host"], WorkSharing.Order(new(), WorkSharingJobs.LipSync, "desk-2", "m4-host", [new("m5-host")]));
    }

    [Fact]
    public void A_chunk_waits_half_its_length_at_least_100_ms()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(250), LipSyncSharing.Wait(12_000, 24_000));
        Assert.Equal(TimeSpan.FromMilliseconds(500), LipSyncSharing.Wait(24_000, 24_000));
        Assert.Equal(TimeSpan.FromMilliseconds(100), LipSyncSharing.Wait(240, 24_000));
    }

    [Fact]
    public void The_list_from_older_choices_is_the_computers_then_this_pc_or_empty_when_off()
    {
        var list = LipSyncSharing.FromOlderChoices(false, ["m4-host", "m5-host", "m4-host"]);
        Assert.Equal(PoolAreas.LipSync.Id, list.Area);
        Assert.Equal(["host:m4-host", "host:m5-host", "this-pc"], list.Members.Select(m => m.Key));
        Assert.Equal(["this-pc"], LipSyncSharing.FromOlderChoices(false, []).Members.Select(m => m.Key));
        Assert.Empty(LipSyncSharing.FromOlderChoices(true, ["m4-host"]).Members);
        Assert.True(PoolRouting.Order(PoolAreas.LipSync, LipSyncSharing.FromOlderChoices(true, []), "desk-2").Fallback);
        Assert.Equal(WorkSharingJobs.LipSync, PoolAreas.LipSync.Id);
    }

    [Fact]
    public async Task One_reply_stays_on_the_computer_that_took_its_chunk_and_the_next_reply_chooses_again()
    {
        var pool = new LipSyncSharing();
        var (m4, m5) = (new Face("m4-host", 5), new Face("m5-host", 5));
        var turn = Guid.NewGuid();
        Assert.Equal(["m4-host", "m5-host"], pool.Order([m4, m5], f => f.Id, turn).Select(f => f.Id));
        Assert.Equal("m5-host", await Chunk(pool, new WorkQueue(), turn, [new Face("m4-host", 5) { Down = true }, m5]));
        Assert.Equal(["m5-host", "m4-host"], pool.Order([m4, m5], f => f.Id, turn).Select(f => f.Id));
        Assert.Equal("m5-host", await Chunk(pool, new WorkQueue(), turn, [m4, m5]));
        Assert.Equal("m4-host", await Chunk(pool, new WorkQueue(), Guid.NewGuid(), [m4, m5]));
        Assert.Equal(3, pool.Chunks);
        Assert.Equal(1, pool.Moved);
        Assert.Equal(0, pool.Skipped);
        Assert.Equal("m4-host", pool.LastHost);
    }

    [Fact]
    public async Task A_busy_computer_is_passed_over_for_the_next()
    {
        var (m4, m5) = (new Face("m4-host", 300), new Face("m5-host", 5));
        var holding = Chunk(new LipSyncSharing(), new WorkQueue(), Guid.NewGuid(), [m4]);
        await Task.Delay(30);
        var pool = new LipSyncSharing();
        Assert.Equal("m5-host", await Chunk(pool, new WorkQueue(), Guid.NewGuid(), [m4, m5]));
        Assert.Equal(1, pool.Moved);
        Assert.Equal("m4-host", await holding);
    }

    [Fact]
    public async Task Every_computer_busy_past_the_wait_skips_the_chunk_so_loudness_moves_the_mouth()
    {
        var (m4, m5) = (new Face("m4-host", 600), new Face("m5-host", 600));
        var holds = new[] { Chunk(new LipSyncSharing(), new WorkQueue(), Guid.NewGuid(), [m4]), Chunk(new LipSyncSharing(), new WorkQueue(), Guid.NewGuid(), [m5]) };
        await Task.Delay(30);
        var pool = new LipSyncSharing();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Null(await Chunk(pool, new WorkQueue { Retry = TimeSpan.FromMilliseconds(20) }, Guid.NewGuid(), [m4, m5], waitMs: 150));
        Assert.InRange(watch.ElapsedMilliseconds, 120, 500);
        Assert.Equal(1, pool.Skipped);
        Assert.Equal(0, pool.Chunks);
        await Task.WhenAll(holds);
    }

    [Fact]
    public async Task No_computer_answering_or_a_real_failure_is_thrown()
    {
        var pool = new LipSyncSharing();
        await Assert.ThrowsAsync<Gone>(() => Chunk(pool, new WorkQueue(), Guid.NewGuid(), [new Face("m4-host", 5) { Down = true }]));
        await Assert.ThrowsAsync<Broken>(() => Chunk(pool, new WorkQueue(), Guid.NewGuid(), [new Face("m4-host", 5) { Fails = true }, new Face("m5-host", 5)]));
        Assert.Equal(0, pool.Skipped);
    }
}
