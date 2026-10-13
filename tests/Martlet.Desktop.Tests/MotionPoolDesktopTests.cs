using System.IO;
using Martlet.Avatar.Hosting;
using Martlet.Core.Cluster;
using Martlet.Core.Motion;
using Martlet.Desktop;

namespace Martlet.Desktop.Tests;

/// <summary>The desktop's motion pool (MotionClient): the computers a motion tries from this PC's files, the Motion list winning
/// over the older choice, the maker and studio it gives the conversation, and the computers kept from background thinks.</summary>
public sealed class MotionPoolDesktopTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 15, 0, 0, TimeSpan.Zero);

    private static PairedHost Paired(string id, int octet, bool friend = false) => new()
    {
        Pairing = new AvatarRemoteHost
        {
            Origin = $"https://192.168.1.{octet}:9443/", HostId = id, SpkiFingerprint = "sha256:" + new string('a', 64),
            DeviceId = "desktop-test", CredentialId = new string('B', 22)
        },
        Access = friend ? Martlet.Avatar.Audio2Face.Remote.HostSignInAccess.Friend : null,
        Method = HostSetupMethod.Agent
    };

    [Fact]
    public void Motions_try_the_motion_list_in_order_and_an_empty_list_is_off()
    {
        var directory = Directory.CreateTempSubdirectory("martlet-motion-pool-").FullName;
        try
        {
            HostRegistry.Save(directory, [Paired("m1-host", 11), Paired("m3-host", 13), Paired("desk-host", 20), Paired("friend-host", 40, friend: true)]);
            ClusterSync.SavePlan(directory, ClusterPlan.Empty
                .Observe("m3-host", null, [new() { Kind = "motion", Model = "hy-motion-1.0-lite" }], false, "desk-1", Now)
                .Observe("desk-host", null, [new() { Kind = "motion", Model = "hy-motion-1.0" }], false, "desk-1", Now)
                .Observe("friend-host", null, [new() { Kind = "motion", Model = "hy-motion-1.0" }], false, "desk-1", Now));
            WorkSharingRoster.Forget();

            // No list and no motion.json: automatic, every own paired computer that runs Motion (never a friend's host).
            Assert.Equal(["desk-host", "m3-host"], MotionClient.Hosts(directory));
            Assert.True(MotionClient.IsSetUp(directory));
            Assert.All(MotionClient.Hosts(directory), host => Assert.Contains("motions", BackgroundDuties.Of(directory)[host]));

            // The Motion list wins: its members that are on and paired here, in its order.
            Assert.True(PoolSettings.SaveFor(directory, PoolAreas.Motion, new PoolList
            {
                Area = PoolAreas.Motion.Id,
                Members = [PoolMember.Computer("m3-host"), PoolMember.Computer("m1-host") with { Off = true }, PoolMember.Computer("friend-host"),
                    PoolMember.Computer("gone-host"), PoolMember.Computer("desk-host")]
            }));
            WorkSharingRoster.Forget();
            Assert.Equal(["m3-host", "desk-host"], MotionClient.Hosts(directory));
            using (var maker = Assert.IsType<PoolMotionMaker>(MotionClient.For(directory)))
            {
                Assert.Equal(["m3-host", "desk-host"], maker.Hosts);
                Assert.Empty(maker.Used);
                Assert.Contains("2 computers", maker.Where);
            }
            using (var studio = MotionStudio.For(directory)!)
                Assert.Equal("m3-host", studio.HostId);

            // A list with nothing on turns motion off, whatever motion.json says.
            Assert.True(new MotionSettings { Place = MotionPlace.Host, HostId = "m3-host" }.Save(directory));
            Assert.True(PoolSettings.SaveFor(directory, PoolAreas.Motion, new PoolList { Area = PoolAreas.Motion.Id }));
            WorkSharingRoster.Forget();
            Assert.Empty(MotionClient.Hosts(directory));
            Assert.False(MotionClient.IsSetUp(directory));
            Assert.Null(MotionClient.For(directory));
            Assert.Null(MotionStudio.For(directory));
            Assert.DoesNotContain(BackgroundDuties.Of(directory).Values, duties => duties.Contains("motions"));
        }
        finally
        {
            WorkSharingRoster.Forget();
            Directory.Delete(directory, true);
        }
    }
}
