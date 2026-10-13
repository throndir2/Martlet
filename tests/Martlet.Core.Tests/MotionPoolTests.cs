using Martlet.Core.Cluster;
using Martlet.Core.Motion;
using Martlet.Core.Sync;

namespace Martlet.Core.Tests;

/// <summary>The motion pool (MotionPool): the Motion list's order, the older choice before a list, the first list made from it,
/// and a motion going to the first free computer through the work queue.</summary>
public sealed class MotionPoolTests
{
    private static readonly string[] Paired = ["m1-host", "m3-host", "desk-host"];

    private static ClusterPlan Plan()
    {
        var now = DateTimeOffset.UtcNow;
        return ClusterPlan.Empty
            .Observe("m3-host", "https://192.168.1.33:9443", [new() { Kind = "motion", Model = "hy-motion-1.0-lite" }], false, "desk-2", now)
            .Observe("desk-host", "https://192.168.1.20:9443", [new() { Kind = "motion", Model = "hy-motion-1.0" }], false, "desk-2", now)
            .Observe("m1-host", "https://192.168.1.31:9443", [new() { Kind = "pictures", Model = "z-image-turbo" }], false, "desk-2", now)
            .Observe("friend-host", "https://192.168.1.40:9443", [new() { Kind = "motion", Model = "hy-motion-1.0" }], false, "desk-2", now);
    }

    [Fact]
    public void Motion_is_a_shared_pool_area_of_this_pc_and_computers()
    {
        Assert.Contains(PoolAreas.Motion, PoolAreas.All);
        Assert.Same(PoolAreas.Motion, PoolAreas.Find("motion"));
        Assert.True(PoolAreas.Motion.Shared);
        Assert.False(PoolAreas.Motion.Required);
        Assert.Equal("motion", PoolAreas.Motion.HostRole);
        Assert.Equal([PoolMemberKind.ThisPc, PoolMemberKind.Computer], PoolAreas.Motion.Kinds);
        Assert.Equal("motion", MotionPool.Lane);
    }

    [Fact]
    public void The_list_wins_in_its_order_with_only_members_that_are_on_paired_and_kept_for_this_pc()
    {
        var list = new PoolList
        {
            Area = PoolAreas.Motion.Id,
            Members = [PoolMember.Computer("m3-host"), PoolMember.ThisPc(), PoolMember.Computer("m1-host") with { Off = true },
                PoolMember.Computer("friend-host"), PoolMember.Computer("desk-host") with { OnlyFor = ["desk-9"] }]
        };
        var members = MotionPool.Members(list, new MotionSettings { Place = MotionPlace.Off }, Plan(), "desk-2", "desk-host", Paired);
        Assert.Equal(["m3-host", "desk-host"], members.Select(m => m.HostId));
        Assert.Equal(PoolMemberKind.ThisPc, members[1].Member.Kind);
        Assert.Empty(MotionPool.Members(new PoolList { Area = "motion" }, new MotionSettings(), Plan(), "desk-2", "desk-host", Paired));
        Assert.Empty(MotionPool.Members(list with { Members = [.. list.Members.Select(m => m with { Off = true })] }, new MotionSettings(), Plan(),
            "desk-2", "desk-host", Paired));
        // A companion PC without a host service of its own: This PC has no computer, so it is left out.
        Assert.Equal(["m3-host"], MotionPool.Members(list, new MotionSettings(), Plan(), "desk-2", null, Paired).Select(m => m.HostId));
    }

    [Fact]
    public void Before_a_list_the_older_choice_says_where()
    {
        Assert.Equal(["desk-host", "m3-host"], MotionPool.Members(null, new MotionSettings(), Plan(), "desk-2", "desk-host", Paired).Select(m => m.HostId));
        Assert.Equal(["m3-host", "desk-host"], MotionPool.Members(null, new MotionSettings(), Plan(), "desk-2", "m3-host", Paired).Select(m => m.HostId));
        Assert.Equal(["desk-host", "m3-host"], MotionPool.Members(null, new MotionSettings(), Plan(), "desk-2", null, Paired).Select(m => m.HostId));
        // One computer is tried even when it isn't paired here, so the motion says why it can't be made there.
        Assert.Equal(["gpu-x"], MotionPool.Members(null, new MotionSettings { Place = MotionPlace.Host, HostId = "gpu-x" }, Plan(), "desk-2", null, Paired)
            .Select(m => m.HostId));
        Assert.Empty(MotionPool.Members(null, new MotionSettings { Place = MotionPlace.Off }, Plan(), "desk-2", "desk-host", Paired));
        Assert.Empty(MotionPool.Members(null, new MotionSettings(), Plan(), "desk-2", null, ["m1-host"]));
    }

    [Fact]
    public void The_first_list_keeps_the_older_choice()
    {
        var runners = MotionPool.Runners(Plan(), Paired, "desk-host");
        Assert.Equal(["desk-host", "m3-host"], runners);
        // The shared first list takes every companion PC's motion computers, not only this PC's pairings.
        Assert.Equal(["desk-host", "friend-host", "m3-host"], MotionPool.Runners(Plan(), null, null));
        var one = MotionPool.FirstList(new MotionSettings { Place = MotionPlace.Host, HostId = "m3-host" }, runners)!;
        Assert.Equal(["host:m3-host", "host:desk-host"], one.Members.Select(m => m.Key));
        Assert.All(one.Members, m => Assert.False(m.Off));
        var automatic = MotionPool.FirstList(new MotionSettings(), runners)!;
        Assert.Equal(["host:desk-host", "host:m3-host"], automatic.Members.Select(m => m.Key));
        var off = MotionPool.FirstList(new MotionSettings { Place = MotionPlace.Off }, runners)!;
        Assert.True(off.NoneOn);
        Assert.Equal(2, off.Members.Count);
        Assert.Empty(MotionPool.FirstList(new MotionSettings { Place = MotionPlace.Off }, [])!.Members);
        // Automatic while no computer runs Motion makes no list, so a computer that adds the role later still turns motion on.
        Assert.Null(MotionPool.FirstList(new MotionSettings(), []));
    }

    [Fact]
    public void A_motion_list_made_on_one_computer_is_the_same_on_another()
    {
        var a = Directory.CreateTempSubdirectory("martlet-motion-pool-a-").FullName;
        var b = Directory.CreateTempSubdirectory("martlet-motion-pool-b-").FullName;
        try
        {
            var list = new PoolList { Area = "motion", Members = [PoolMember.Computer("m3-host"), PoolMember.Computer("desk-host") with { Off = true }] };
            Assert.True(PoolSettings.SaveFor(a, PoolAreas.Motion, list));
            // Computer A shares pools.json as the "pools" shared setting; computer B, which never chose, takes it.
            var document = SharedSettings.Empty.Put(PoolSettings.SharedKey, PoolSettings.Load(a).Share(), null, "desk-a", DateTimeOffset.UtcNow);
            var arrived = SharedSettings.Parse(SharedSettings.Merge(SharedSettings.Empty, document).Write()).Find(PoolSettings.SharedKey)!;
            Assert.True(PoolSettings.Parse(arrived.Value)!.Save(b));
            Assert.Equal(PoolSettings.LoadFor(a, PoolAreas.Motion), PoolSettings.LoadFor(b, PoolAreas.Motion), new ListComparer());
            Assert.Equal(["m3-host"], MotionPool.Members(PoolSettings.LoadFor(b, PoolAreas.Motion), new MotionSettings(), Plan(), "desk-b", null, Paired)
                .Select(m => m.HostId));
        }
        finally
        {
            Directory.Delete(a, true);
            Directory.Delete(b, true);
        }
    }

    private sealed class ListComparer : IEqualityComparer<PoolList?>
    {
        public bool Equals(PoolList? x, PoolList? y) => x is not null && y is not null && x.Area == y.Area &&
            x.Members.Select(m => (m.Key, m.Off)).SequenceEqual(y.Members.Select(m => (m.Key, m.Off)));

        public int GetHashCode(PoolList? list) => list?.Area.GetHashCode() ?? 0;
    }

    [Fact]
    public async Task A_motion_goes_to_the_first_free_computer_and_a_failure_is_not_made_again()
    {
        var asked = new Dictionary<string, int>();
        Func<string, CancellationToken, Task<string>> Make(Func<string, int, Exception?> fail) => (host, _) =>
        {
            lock (asked) asked[host] = asked.GetValueOrDefault(host) + 1;
            return fail(host, asked[host]) is { } error ? Task.FromException<string>(error) : Task.FromResult(host);
        };
        var queue = new WorkQueue { Retry = TimeSpan.FromMilliseconds(5) };

        Assert.Equal(("m3-host", "m3-host"), await MotionPool.RunAsync(queue, ["m3-host", "desk-host"], Make((_, _) => null), null, CancellationToken.None));
        Assert.False(asked.ContainsKey("desk-host"));

        asked.Clear();
        var busy = await MotionPool.RunAsync(queue, ["m3-host", "desk-host"],
            Make((host, _) => host == "m3-host" ? new MotionException(MotionErrorCodes.Busy, "busy") : null), null, CancellationToken.None);
        Assert.Equal("desk-host", busy.HostId);

        asked.Clear();
        var down = await MotionPool.RunAsync(queue, ["m3-host", "desk-host"],
            Make((host, _) => host == "m3-host" ? new HttpRequestException("down") : null), null, CancellationToken.None);
        Assert.Equal("desk-host", down.HostId);

        asked.Clear();
        var waited = await MotionPool.RunAsync(queue, ["m3-host", "desk-host"],
            Make((host, n) => host == "desk-host" || n < 3 ? new MotionException(MotionErrorCodes.Busy, "busy") : null), null, CancellationToken.None);
        Assert.Equal("m3-host", waited.HostId);
        Assert.True(asked["m3-host"] >= 3);

        asked.Clear();
        var failed = await Assert.ThrowsAsync<MotionException>(() => MotionPool.RunAsync(queue, ["m3-host", "desk-host"],
            Make((host, _) => host == "m3-host" ? new MotionException(MotionErrorCodes.Failed, "failed") : null), null, CancellationToken.None));
        Assert.Equal(MotionErrorCodes.Failed, failed.Code);
        Assert.False(asked.ContainsKey("desk-host"));

        var none = await Assert.ThrowsAsync<MotionException>(() => MotionPool.RunAsync(queue, [], Make((_, _) => null), null, CancellationToken.None));
        Assert.Equal(MotionErrorCodes.Unavailable, none.Code);
    }

    [Fact]
    public void Failures_are_sorted_into_busy_unavailable_or_real()
    {
        Assert.Equal(WorkRefusal.Busy, MotionPool.Classify(new MotionException(MotionErrorCodes.Busy, "")));
        Assert.Equal(WorkRefusal.Unavailable, MotionPool.Classify(new MotionException(MotionErrorCodes.Unavailable, "")));
        Assert.Equal(WorkRefusal.Unavailable, MotionPool.Classify(new MotionException(MotionErrorCodes.OutOfMemory, "")));
        Assert.Equal(WorkRefusal.Unavailable, MotionPool.Classify(new HttpRequestException("")));
        Assert.Equal(WorkRefusal.None, MotionPool.Classify(new MotionException(MotionErrorCodes.Failed, "")));
        Assert.Equal(WorkRefusal.None, MotionPool.Classify(new MotionException(MotionErrorCodes.RequestInvalid, "")));
    }
}
