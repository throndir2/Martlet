using System.IO;
using Martlet.Avatar.Hosting;
using Martlet.Core.Cluster;
using Martlet.Core.Singing;
using Martlet.Desktop;

namespace Martlet.Desktop.Tests;

public sealed class SingingPoolMembersTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 15, 0, 0, TimeSpan.Zero);

    private static PairedHost Paired(string id, int octet, bool friend = false) => new()
    {
        Pairing = new AvatarRemoteHost
        {
            Origin = $"https://192.168.1.{octet}:9443/", HostId = id, SpkiFingerprint = "sha256:" + new string('a', 64),
            DeviceId = "desktop-test", CredentialId = new string('B', 22)
        },
        Access = friend ? Martlet.Avatar.Audio2Face.Remote.HostSignInAccess.Friend : null
    };

    [Fact]
    public void Songs_try_the_saved_computer_then_the_plans_singers_then_the_other_own_computers_never_a_friends()
    {
        var directory = Directory.CreateTempSubdirectory("martlet-singing-pool-").FullName;
        try
        {
            HostRegistry.Save(directory, [Paired("m1-host", 11), Paired("m3-host", 13), Paired("m4-host", 14), Paired("friend-host", 15, friend: true)]);
            ClusterSync.SavePlan(directory, ClusterPlan.Empty
                .Assign(ClusterJobs.Speaking, "m1-host", false, true, null, "desk-1", Now)
                .Observe("m1-host", null, [new() { Kind = "singing", Model = "ace-step-v15-soulx-svc" }], false, "desk-1", Now)
                .Observe("m4-host", null, [new() { Kind = "singing", Model = "ace-step-v15-soulx-svc" }], false, "desk-1", Now)
                .Observe("friend-host", null, [new() { Kind = "singing", Model = "ace-step-v15-soulx-svc" }], false, "desk-1", Now));
            new SingingPreferences(Host: "m3-host").Save(directory);

            Assert.Equal(["m3-host", "m4-host", "m1-host"], SongClient.Members(directory).Select(m => m.Host.HostId));
            Assert.Equal(PoolMember.Computer("m4-host").Key, SongClient.Members(directory)[1].Member.Key);

            // A pool list wins: its members that are on and paired here, in its order; an empty list is off.
            Assert.True(PoolSettings.SaveFor(directory, PoolAreas.Singing, new PoolList
            {
                Area = PoolAreas.Singing.Id,
                Members = [PoolMember.Computer("m4-host"), PoolMember.Computer("m1-host") with { Off = true }, PoolMember.Computer("friend-host"),
                    PoolMember.Computer("gone-host"), SingingPool.WithQuality(PoolMember.Computer("m3-host"), SongQuality.HighQuality)]
            }));
            WorkSharingRoster.Forget();
            Assert.Equal(["m4-host", "m3-host"], SongClient.Members(directory).Select(m => m.Host.HostId));
            Assert.Equal(SongQuality.HighQuality, SingingPool.Quality(SongClient.Members(directory)[1].Member));
            Assert.Null(SingingPool.Quality(SongClient.Members(directory)[0].Member));
            Assert.True(SongClient.IsSetUp(directory));
            // Every member that sings is kept from background thinks; a song holds the first.
            Assert.Equal(["m3-host", "m4-host"], BackgroundDuties.Of(directory).Keys.Order(StringComparer.Ordinal));
            Assert.Equal("host:m4-host", BackgroundDuties.Singer(directory)!.Id);
            // An empty list is off, whatever singing.json says.
            Assert.True(PoolSettings.SaveFor(directory, PoolAreas.Singing, new PoolList { Area = PoolAreas.Singing.Id }));
            WorkSharingRoster.Forget();
            Assert.Empty(SongClient.Members(directory));
            Assert.False(SongClient.IsSetUp(directory));
            Assert.Null(BackgroundDuties.Singer(directory));
            Assert.Equal("singing is off: no computer in Companion › Singing's list is on.", new DesktopSongSource(directory).Current().Problem);
        }
        finally { Directory.Delete(directory, true); }
    }
}
