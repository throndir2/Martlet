using System.Runtime.CompilerServices;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Avatar.Hosting;
using Martlet.Avatars;
using Martlet.Core.Cluster;
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

    private static AvatarRemoteHost Remote(string id, int octet = 14) => new()
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

    // Lip-sync assigned to <prefix>-a4 and other computers (unique host IDs per test: the desktop's queue is shared by the
    // whole process).
    private static (LipSyncPool Pool, AvatarRemoteHost Assigned, Link Mine, Dictionary<string, Link> Others, LipSyncPlace[] Hosts) Pool(
        string prefix, params string[] others)
    {
        var assigned = Remote(prefix + "-a4");
        var links = others.Select((id, i) => (Id: prefix + "-" + id, Link: new Link($"192.168.1.{20 + i}:9443")))
            .ToDictionary(p => p.Id, p => p.Link);
        LipSyncPlace[] hosts = [new("host:" + assigned.HostId, assigned), .. links.Keys.Select((id, i) => new LipSyncPlace("host:" + id, Remote(id, 20 + i)))];
        return (new LipSyncPool(remote => links.GetValueOrDefault(remote.HostId), _ => hosts), assigned, new Link("192.168.1.14:9443"), links, hosts);
    }

    [Fact]
    public async Task The_pool_is_the_assigned_computer_then_the_ready_others()
    {
        var (pool, assigned, mine, others, hosts) = Pool("ready", "b5", "c6");
        others["ready-c6"].Ready = false;
        var members = await pool.ReadyAsync(hosts, assigned.HostId, mine, default);
        Assert.Equal(["ready-a4", "ready-b5"], members.Select(m => m.HostId));
        Assert.Equal("host:ready-b5", members[1].Key);
        Assert.Same(mine, members[0].Link);
        pool.Clear();
        Assert.True(others["ready-b5"].Disposed);
        Assert.False(mine.Disposed);
    }

    [Fact]
    public async Task A_busy_assigned_computer_hands_the_chunk_to_the_next_and_stays_in_the_pool()
    {
        var (pool, assigned, mine, others, hosts) = Pool("busy", "b5");
        mine.Busy = true;
        var members = await pool.ReadyAsync(hosts, assigned.HostId, mine, default);
        Assert.Equal(1, await Frames(pool, members));
        Assert.Equal(1, others["busy-b5"].Requests);
        Assert.Equal(0, mine.Invalidated);
        Assert.Equal(1, pool.Sharing.Moved);
        Assert.Equal("host:busy-b5", pool.Sharing.LastHost);
        // The same reply stays on the computer that took it.
        mine.Busy = false;
        Assert.Equal(1, await Frames(pool, members));
        Assert.Equal(2, others["busy-b5"].Requests);
        Assert.Equal(1, mine.Requests);
    }

    [Fact]
    public async Task An_unanswering_computer_is_left_out_for_a_while()
    {
        var (pool, assigned, mine, _, hosts) = Pool("gone", "b5");
        mine.Unreachable = true;
        var members = await pool.ReadyAsync(hosts, assigned.HostId, mine, default);
        Assert.Equal(1, await Frames(pool, members));
        Assert.Equal(1, mine.Invalidated);
        Assert.Equal(["gone-b5"], (await pool.ReadyAsync(hosts, assigned.HostId, mine, default)).Select(m => m.HostId));
    }

    [Fact]
    public async Task Every_computer_busy_gives_no_frames_so_loudness_moves_the_mouth()
    {
        var (pool, assigned, mine, others, hosts) = Pool("full", "b5");
        mine.Busy = true;
        others["full-b5"].Busy = true;
        var members = await pool.ReadyAsync(hosts, assigned.HostId, mine, default);
        Assert.Equal(0, await Frames(pool, members));
        Assert.Equal(1, pool.Sharing.Skipped);
        Assert.Equal(0, mine.Invalidated + others["full-b5"].Invalidated);
    }

    [Fact]
    public void Until_a_list_is_saved_lip_sync_keeps_its_older_choice()
    {
        var auto = new AvatarProfileStub().Auto(Remote("legacy-a4"));
        Assert.Equal(["host:legacy-a4", "this-pc"], LipSyncPool.Migrate(auto, null).Members.Select(m => m.Key));
        Assert.Equal(["this-pc"], LipSyncPool.Migrate(auto with { RemoteHost = null }, null).Members.Select(m => m.Key));
        Assert.Empty(LipSyncPool.Migrate(auto with { LipSync = AvatarLipSync.Loudness }, null).Members);
        Assert.Equal(["this-pc"], LipSyncPool.Migrate(null, null).Members.Select(m => m.Key));
    }

    [Fact]
    public void A_saved_list_gives_one_row_per_computer_this_pc_with_its_endpoint_and_skips_what_this_pc_cannot_use()
    {
        var paired = new Dictionary<string, AvatarRemoteHost>(StringComparer.Ordinal)
        {
            ["list-a4"] = Remote("list-a4"), ["list-b5"] = Remote("list-b5", 15)
        };
        var list = new PoolList { Area = PoolAreas.LipSync.Id }
            .With(PoolMember.Gpu("list-a4", 2))
            .With(PoolMember.ThisPc().WithSetting(PoolSettingKeys.Endpoint, "http://127.0.0.1:52010/"))
            .With(PoolMember.Computer("list-a4"))
            .With(PoolMember.Computer("list-unpaired"))
            .With(PoolMember.Computer("list-b5") with { Off = true })
            .With(PoolMember.Computer("list-kept") with { OnlyFor = ["another-desk"] });
        // A card row (from a list saved before) is its computer's row: a host runs one Audio2Face relay.
        Assert.Equal(["host:list-a4", "this-pc", "host:list-unpaired", "host:list-b5", "host:list-kept"],
            LipSyncSharing.OneRowPerComputer(list).Members.Select(m => m.Key));
        Assert.False(PoolAreas.LipSync.Takes(PoolMemberKind.Gpu));
        var places = LipSyncPool.FromList(list, paired);
        Assert.Equal(["host:list-a4", "this-pc"], places.Select(p => p.Key));
        Assert.Equal("list-a4", places[0].Host!.HostId);
        Assert.Equal("http://127.0.0.1:52010/", places[1].Endpoint);

        var empty = PoolRouting.Order(PoolAreas.LipSync, new PoolList { Area = PoolAreas.LipSync.Id }, "desktop-test");
        Assert.True(empty.Fallback);
        Assert.Empty(LipSyncPool.Resolve(empty, paired));
        Assert.Equal("Voice loudness on this PC", PoolAreas.LipSync.WhenEmpty);
    }

    [Fact]
    public void A_friends_host_chosen_on_this_pc_goes_first_and_never_twice()
    {
        var friend = Remote("friend-f1", 40);
        var paired = new Dictionary<string, AvatarRemoteHost>(StringComparer.Ordinal) { ["friend-a4"] = Remote("friend-a4"), [friend.HostId] = friend };
        var list = new PoolList { Area = PoolAreas.LipSync.Id }.With(PoolMember.Computer("friend-a4")).With(PoolMember.ThisPc());
        Assert.Equal(["host:friend-f1", "host:friend-a4", "this-pc"], LipSyncPool.FromList(list, paired, friend).Select(p => p.Key));
        Assert.Equal(["host:friend-a4", "this-pc"], LipSyncPool.FromList(list, paired).Select(p => p.Key));
        var off = list with { Members = [.. list.Members.Select(m => m with { Off = true })] };
        Assert.Equal(["host:friend-f1"], LipSyncPool.FromList(off, paired, friend).Select(p => p.Key));
        Assert.Single(LipSyncPool.FromList(list.With(PoolMember.Computer("friend-f1")), paired, friend), p => p.Host?.HostId == "friend-f1");
    }

    // A minimal automatic-lip-sync profile.
    private sealed class AvatarProfileStub
    {
        internal AvatarProfile Auto(AvatarRemoteHost host) => new()
        {
            Version = 1, ProfileId = Guid.NewGuid(), Renderer = AvatarRenderer.Live2D, ModelPath = "builtin:Hiyori",
            Endpoint = AvatarProfile.DefaultEndpoint, Configuration = System.Text.Json.JsonDocument.Parse("{}").RootElement,
            LipSync = AvatarLipSync.Auto, RemoteHost = host
        };
    }
}
