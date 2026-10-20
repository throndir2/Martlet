using System.IO;
using Martlet.HostClient;
using Martlet.Avatar.Hosting;
using Martlet.Core.Cluster;
using Martlet.Core.Contracts;
using Martlet.Desktop;
using Martlet.Providers;

namespace Martlet.Desktop.Tests;

public sealed class WorkSharingRosterTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 15, 0, 0, TimeSpan.Zero);

    private static PairedHost Paired(string id, int octet) => new()
    {
        Pairing = new AvatarRemoteHost
        {
            Origin = $"https://192.168.1.{octet}:9443/", HostId = id, SpkiFingerprint = "sha256:" + new string('a', 64),
            DeviceId = "desktop-test", CredentialId = new string('B', 22)
        }
    };

    [Fact]
    public void Order_offers_paired_computers_that_run_the_engine_with_their_models()
    {
        var directory = Directory.CreateTempSubdirectory("martlet-roster-").FullName;
        try
        {
            HostRegistry.Save(directory, [Paired("m1-host", 11), Paired("m3-host", 13), Paired("m4-host", 14)]);
            var plan = ClusterPlan.Empty
                .Assign(ClusterJobs.Speaking, "m1-host", false, true, null, "desk-1", Now)
                .Observe("m1-host", null, [new() { Kind = "chatterbox", Model = "chatterbox-turbo" }, new() { Kind = "ollama", Model = "gemma4:12b" }], false, "desk-1", Now)
                .Observe("m3-host", null, [new() { Kind = "chatterbox", Model = "chatterbox-turbo" }], false, "desk-1", Now)
                .Observe("m4-host", null, [new() { Kind = "audio2face", Model = "a2f" }], false, "desk-1", Now)
                .Observe("m5-host", null, [new() { Kind = "chatterbox", Model = "chatterbox-turbo" }], false, "desk-1", Now);
            ClusterSync.SavePlan(directory, plan);

            var order = WorkSharingRoster.Shared.Order(directory, WorkSharingJobs.Speaking, "chatterbox", null, "m1-host");
            // The planned computer keeps its route's own model; m5-host isn't paired here and m4-host doesn't speak.
            Assert.Equal(["m1-host", "m3-host"], order.Select(p => p.Host!.HostId));
            Assert.Null(order[0].Model);
            Assert.Equal("chatterbox-turbo", order[1].Model);

            // Thinking needs the same model; unshared by default.
            Assert.Single(WorkSharingRoster.Shared.Order(directory, WorkSharingJobs.Thinking, "ollama", "gemma4:12b", "m3-host"));

            Assert.True(new WorkSharingSettings().With(new WorkSharingHost { HostId = "m3-host", OnlyFor = ["someone-else"] }).Save(directory));
            Assert.Equal(["m1-host"], WorkSharingRoster.Shared.Order(directory, WorkSharingJobs.Speaking, "chatterbox", null, "m1-host").Select(p => p.Host!.HostId));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void Order_follows_the_pool_list_once_the_area_has_one()
    {
        var directory = Directory.CreateTempSubdirectory("martlet-roster-pool-").FullName;
        try
        {
            HostRegistry.Save(directory, [Paired("m1-host", 11), Paired("m3-host", 13), Paired("m4-host", 14)]);
            var plan = ClusterPlan.Empty
                .Observe("m1-host", null, [new() { Kind = "chatterbox", Model = "chatterbox-turbo" }, new() { Kind = "ollama", Model = "gemma4:12b" }], false, "desk-1", Now)
                .Observe("m3-host", null, [new() { Kind = "chatterbox", Model = "chatterbox-turbo" }, new() { Kind = "ollama", Model = "gemma4:12b" }], false, "desk-1", Now)
                .Observe("m4-host", null, [new() { Kind = "audio2face", Model = "a2f" }], false, "desk-1", Now);
            ClusterSync.SavePlan(directory, plan);
            // The list puts m3-host before the route's own m1-host, and m4-host (no voice) is skipped.
            Assert.True(PoolSettings.SaveFor(directory, PoolAreas.Speaking, new PoolList
            {
                Area = PoolAreas.Speaking.Id, Members = [PoolMember.Computer("m4-host"), PoolMember.Computer("m3-host"), PoolMember.Computer("m1-host")]
            }));
            Assert.True(PoolSettings.SaveFor(directory, PoolAreas.Thinking, new PoolList { Area = PoolAreas.Thinking.Id, Members = [PoolMember.Computer("m3-host")] }));
            Assert.Equal(["m3-host", "m1-host"],
                WorkSharingRoster.Shared.Order(directory, WorkSharingJobs.Speaking, "chatterbox", null, "m1-host").Select(p => p.Host!.HostId));
            // Thinking: the conversation's own model first, then the list.
            Assert.Equal(["m1-host", "m3-host"],
                WorkSharingRoster.Shared.Order(directory, WorkSharingJobs.Thinking, "ollama", "gemma4:12b", "m1-host").Select(p => p.Host!.HostId));
            // Every member off: the route's own computer still does it (the page turns the route off instead).
            Assert.True(PoolSettings.SaveFor(directory, PoolAreas.Speaking, new PoolList
            {
                Area = PoolAreas.Speaking.Id, Members = [PoolMember.Computer("m3-host") with { Off = true }]
            }));
            Assert.Equal(["m1-host"], WorkSharingRoster.Shared.Order(directory, WorkSharingJobs.Speaking, "chatterbox", null, "m1-host").Select(p => p.Host!.HostId));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void A_store_that_saves_a_file_tells_the_roster_and_another_program_s_change_is_seen_within_a_second()
    {
        var directory = Directory.CreateTempSubdirectory("martlet-roster-changed-").FullName;
        var clock = new Martlet.Core.Tests.ManualClock();
        using var roster = new WorkSharingRoster(new WorkQueue(), clock);
        var changed = new List<string>();
        roster.Changed += changed.Add;
        try
        {
            static PoolList Speaking(params string[] hosts) =>
                new() { Area = PoolAreas.Speaking.Id, Members = [.. hosts.Select(h => PoolMember.Computer(h))] };
            Assert.Null(roster.Pool(directory, PoolAreas.Speaking));
            // Saved through its store: the roster reads the new list at once, without its clock moving, and says so.
            Assert.True(PoolSettings.SaveFor(directory, PoolAreas.Speaking, Speaking("m1-host")));
            Assert.Equal(["m1-host"], roster.Pool(directory, PoolAreas.Speaking)!.Members.Select(m => m.HostId));
            Assert.Equal([Path.Combine(directory, PoolSettings.FileName)], changed);
            // Written by another program: the copy stays for up to a second, then the new file time is seen.
            var other = Path.Combine(directory, "other");
            Assert.True(PoolSettings.SaveFor(other, PoolAreas.Speaking, Speaking("m3-host")));
            File.Copy(Path.Combine(other, PoolSettings.FileName), Path.Combine(directory, PoolSettings.FileName), overwrite: true);
            File.SetLastWriteTimeUtc(Path.Combine(directory, PoolSettings.FileName), DateTime.UtcNow.AddMinutes(1));
            Assert.Equal(["m1-host"], roster.Pool(directory, PoolAreas.Speaking)!.Members.Select(m => m.HostId));
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(["m3-host"], roster.Pool(directory, PoolAreas.Speaking)!.Members.Select(m => m.HostId));
            Assert.Single(changed);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void Busy_and_unreachable_refusals_move_on_and_others_fail()
    {
        Assert.Equal(WorkRefusal.Busy, WorkSharingRoster.Classify(new HostConnectionException("job.busy", "busy")));
        Assert.Equal(WorkRefusal.Unavailable, WorkSharingRoster.Classify(new HostConnectionException("host.unreachable", "gone")));
        Assert.Equal(WorkRefusal.Unavailable, WorkSharingRoster.Classify(new HostTextException(ProviderFailureCode.ModelNotFound)));
        Assert.Equal(WorkRefusal.None, WorkSharingRoster.Classify(new HostConnectionException("request.invalid", "bad")));
        Assert.Equal(WorkRefusal.None, WorkSharingRoster.Classify(new HostTextException(ProviderFailureCode.Authentication)));
    }
}
