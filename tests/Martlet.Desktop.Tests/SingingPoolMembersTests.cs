using System.IO;
using Martlet.Avatar.Hosting;
using Martlet.Core.Cluster;
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

            Assert.Equal(["m3-host", "m4-host", "m1-host"], SongClient.Members(directory).Select(h => h.HostId));
        }
        finally { Directory.Delete(directory, true); }
    }
}
