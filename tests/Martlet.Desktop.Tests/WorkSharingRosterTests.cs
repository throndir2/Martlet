using System.IO;
using Martlet.Avatar.Audio2Face.Remote;
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

            var order = WorkSharingRoster.Order(directory, WorkSharingJobs.Speaking, "chatterbox", null, "m1-host");
            // The planned computer keeps its route's own model; m5-host isn't paired here and m4-host doesn't speak.
            Assert.Equal(["m1-host", "m3-host"], order.Select(p => p.Host!.HostId));
            Assert.Null(order[0].Model);
            Assert.Equal("chatterbox-turbo", order[1].Model);

            // Thinking needs the same model; unshared by default.
            Assert.Single(WorkSharingRoster.Order(directory, WorkSharingJobs.Thinking, "ollama", "gemma4:12b", "m3-host"));

            Assert.True(new WorkSharingSettings().With(new WorkSharingHost { HostId = "m3-host", OnlyFor = ["someone-else"] }).Save(directory));
            WorkSharingRoster.Forget();
            Assert.Equal(["m1-host"], WorkSharingRoster.Order(directory, WorkSharingJobs.Speaking, "chatterbox", null, "m1-host").Select(p => p.Host!.HostId));
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
            WorkSharingRoster.Forget();
            Assert.Equal(["m3-host", "m1-host"],
                WorkSharingRoster.Order(directory, WorkSharingJobs.Speaking, "chatterbox", null, "m1-host").Select(p => p.Host!.HostId));
            // Thinking: the conversation's own model first, then the list.
            Assert.Equal(["m1-host", "m3-host"],
                WorkSharingRoster.Order(directory, WorkSharingJobs.Thinking, "ollama", "gemma4:12b", "m1-host").Select(p => p.Host!.HostId));
            // Every member off: the route's own computer still does it (the page turns the route off instead).
            Assert.True(PoolSettings.SaveFor(directory, PoolAreas.Speaking, new PoolList
            {
                Area = PoolAreas.Speaking.Id, Members = [PoolMember.Computer("m3-host") with { Off = true }]
            }));
            WorkSharingRoster.Forget();
            Assert.Equal(["m1-host"], WorkSharingRoster.Order(directory, WorkSharingJobs.Speaking, "chatterbox", null, "m1-host").Select(p => p.Host!.HostId));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void Busy_and_unreachable_refusals_move_on_and_others_fail()
    {
        Assert.Equal(WorkRefusal.Busy, WorkSharingRoster.Classify(new Audio2FaceHostException("job.busy", "busy")));
        Assert.Equal(WorkRefusal.Unavailable, WorkSharingRoster.Classify(new Audio2FaceHostException("host.unreachable", "gone")));
        Assert.Equal(WorkRefusal.Unavailable, WorkSharingRoster.Classify(new HostTextException(ProviderFailureCode.ModelNotFound)));
        Assert.Equal(WorkRefusal.None, WorkSharingRoster.Classify(new Audio2FaceHostException("request.invalid", "bad")));
        Assert.Equal(WorkRefusal.None, WorkSharingRoster.Classify(new HostTextException(ProviderFailureCode.Authentication)));
    }
}
