using System.Runtime.CompilerServices;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Avatar.Hosting;
using Martlet.Core.Contracts;
using Martlet.Desktop;

namespace Martlet.Desktop.Tests;

public sealed class LipSyncPoolTests
{
    private sealed class Link(string authority) : IAvatarHostLink
    {
        internal volatile bool Ready = true;
        internal volatile bool Busy;
        internal volatile bool Unreachable;
        internal int Invalidated, Requests;
        internal bool Disposed;
        public string Authority => authority;
        public Task<bool> ReadyAsync(CancellationToken token) => Task.FromResult(Ready);
        public async IAsyncEnumerable<RemoteFaceFrame> AnimateAsync(CorrelationIds ids, long epoch, int sampleRate,
            ReadOnlyMemory<byte> pcm, [EnumeratorCancellation] CancellationToken token)
        {
            Interlocked.Increment(ref Requests);
            await Task.Yield();
            if (Busy) throw new Audio2FaceHostException("job.busy", "The host is busy.");
            if (Unreachable) throw new Audio2FaceHostException("host.unreachable", "The host didn't answer.");
            yield return new(0, new Dictionary<string, double> { ["jawOpen"] = 0.5 });
        }
        public void Invalidate()
        {
            Interlocked.Increment(ref Invalidated);
            Ready = false;
        }
        public void Dispose() => Disposed = true;
    }

    private static AvatarRemoteHost Remote(string id, int octet) => new()
    {
        Origin = $"https://192.168.1.{octet}:9443", HostId = id, SpkiFingerprint = "sha256:" + new string('a', 64),
        DeviceId = "desktop-test", CredentialId = new string('B', 22)
    };

    private static readonly CorrelationIds Ids = new() { SessionId = Guid.NewGuid(), TurnId = Guid.NewGuid(), RequestId = Guid.NewGuid() };

    private static async Task<int> Frames(LipSyncPool pool, IReadOnlyList<LipSyncMember> members)
    {
        var count = 0;
        await foreach (var _ in pool.AnimateAsync(members, Ids with { RequestId = Guid.NewGuid() }, 1, 24_000, new byte[960], 480, default))
            count++;
        return count;
    }

    // Lip-sync assigned to a4 (unique host IDs per test: the desktop's queue is shared by the whole process).
    private static (LipSyncPool Pool, AvatarRemoteHost Assigned, Link Mine, Dictionary<string, Link> Others) Pool(string prefix,
        params string[] others)
    {
        var assigned = Remote(prefix + "-a4", 14);
        var links = others.Select((id, i) => (Id: prefix + "-" + id, Link: new Link($"192.168.1.{20 + i}:9443")))
            .ToDictionary(p => p.Id, p => p.Link);
        IReadOnlyList<AvatarRemoteHost> pool = [assigned, .. links.Keys.Select((id, i) => Remote(id, 20 + i))];
        return (new LipSyncPool(remote => links.GetValueOrDefault(remote.HostId), _ => pool), assigned, new Link("192.168.1.14:9443"), links);
    }

    [Fact]
    public async Task The_pool_is_the_assigned_computer_then_the_ready_others()
    {
        var (pool, assigned, mine, others) = Pool("ready", "b5", "c6");
        others["ready-c6"].Ready = false;
        var members = await pool.ReadyAsync(assigned, mine, default);
        Assert.Equal(["ready-a4", "ready-b5"], members.Select(m => m.HostId));
        Assert.Same(mine, members[0].Link);
        pool.Clear();
        Assert.True(others["ready-b5"].Disposed);
        Assert.False(mine.Disposed);
    }

    [Fact]
    public async Task A_busy_assigned_computer_hands_the_chunk_to_the_next_and_stays_in_the_pool()
    {
        var (pool, assigned, mine, others) = Pool("busy", "b5");
        mine.Busy = true;
        var members = await pool.ReadyAsync(assigned, mine, default);
        Assert.Equal(1, await Frames(pool, members));
        Assert.Equal(1, others["busy-b5"].Requests);
        Assert.Equal(0, mine.Invalidated);
        Assert.Equal(1, pool.Sharing.Moved);
        Assert.Equal("busy-b5", pool.Sharing.LastHost);
        // The same reply stays on the computer that took it.
        mine.Busy = false;
        Assert.Equal(1, await Frames(pool, members));
        Assert.Equal(2, others["busy-b5"].Requests);
        Assert.Equal(1, mine.Requests);
    }

    [Fact]
    public async Task An_unanswering_computer_is_left_out_for_a_while()
    {
        var (pool, assigned, mine, others) = Pool("gone", "b5");
        mine.Unreachable = true;
        var members = await pool.ReadyAsync(assigned, mine, default);
        Assert.Equal(1, await Frames(pool, members));
        Assert.Equal(1, mine.Invalidated);
        Assert.Equal(["gone-b5"], (await pool.ReadyAsync(assigned, mine, default)).Select(m => m.HostId));
    }

    [Fact]
    public async Task Every_computer_busy_gives_no_frames_so_loudness_moves_the_mouth()
    {
        var (pool, assigned, mine, others) = Pool("full", "b5");
        mine.Busy = true;
        others["full-b5"].Busy = true;
        var members = await pool.ReadyAsync(assigned, mine, default);
        Assert.Equal(0, await Frames(pool, members));
        Assert.Equal(1, pool.Sharing.Skipped);
        Assert.Equal(0, mine.Invalidated + others["full-b5"].Invalidated);
    }

    [Fact]
    public async Task Without_a_data_directory_the_pool_is_the_assigned_computer_alone()
    {
        var assigned = Remote("alone-a4", 14);
        var opened = 0;
        using var pool = new LipSyncPool(_ => { opened++; return null; });
        Assert.Equal(["alone-a4"], (await pool.ReadyAsync(assigned, new Link("192.168.1.14:9443"), default)).Select(m => m.HostId));
        Assert.Equal(0, opened);
    }
}
