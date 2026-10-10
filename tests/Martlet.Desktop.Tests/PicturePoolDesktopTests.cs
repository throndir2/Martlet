using System.IO;
using Martlet.Avatar.Hosting;
using Martlet.Core.Cluster;
using Martlet.Core.Pictures;
using Martlet.Desktop;
using Martlet.Providers.Pictures;

namespace Martlet.Desktop.Tests;

public sealed class PicturePoolDesktopTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 15, 0, 0, TimeSpan.Zero);

    private static PairedHost Paired(string id, int octet) => new()
    {
        Pairing = new AvatarRemoteHost
        {
            Origin = $"https://192.168.1.{octet}:9443/", HostId = id, SpkiFingerprint = "sha256:" + new string('a', 64),
            DeviceId = "desktop-test", CredentialId = new string('B', 22)
        }
    };

    [Fact]
    public void The_pictures_list_is_made_once_from_pictures_json_and_draws_on_a_pool_of_the_computers_that_run_the_role()
    {
        var directory = Directory.CreateTempSubdirectory("martlet-picture-pool-").FullName;
        try
        {
            HostRegistry.Save(directory, [Paired("m1-host", 11), Paired("m3-host", 13), Paired("m4-host", 14)]);
            ClusterSync.SavePlan(directory, ClusterPlan.Empty
                .Assign(ClusterJobs.Speaking, "m3-host", false, true, null, "desk-1", Now)
                .Observe("m1-host", null, [new() { Kind = "pictures", Model = "z-image-turbo" }], false, "desk-1", Now)
                .Observe("m3-host", null, [new() { Kind = "pictures", Model = "z-image-turbo" }, new() { Kind = "chatterbox", Model = "c" }], false, "desk-1", Now)
                .Observe("m4-host", null, [new() { Kind = "pictures", Model = "z-image-turbo" }], false, "desk-1", Now)
                .Observe("m5-host", null, [new() { Kind = "pictures", Model = "z-image-turbo" }], false, "desk-1", Now));
            Assert.True(new PicturesSettings { Place = PicturePlace.Host, HostId = "m4-host", Workflow = PictureWorkflow.Checkpoint, Checkpoint = "c.safetensors" }
                .Save(directory));

            // m5-host isn't paired here; m1-host does fewer jobs than m3-host.
            var list = PictureClient.List(directory);
            Assert.Equal(["host:m4-host", "host:m1-host", "host:m3-host"], list.Members.Select(m => m.Key));
            Assert.True(PoolSettings.Saved(directory, shared: false));
            Assert.Equal(list.Members.Select(m => m.Key), PoolSettings.LoadFor(directory, PoolAreas.Pictures)!.Members.Select(m => m.Key));
            Assert.True(PictureClient.IsSetUp(directory));
            Assert.Equal("m4-host", PictureClient.Painter(directory));

            using (var pool = Assert.IsType<PicturePool>(PictureClient.For(directory, Guid.Empty, null)))
            {
                Assert.Equal(["m4-host", "m1-host", "m3-host"], pool.Members.Select(m => m.HostId));
                Assert.All(pool.Members, m => Assert.Equal("c.safetensors", Assert.IsType<ComfyPictureMaker>(m.Maker).Model));
                Assert.StartsWith("m4-host's Pictures role (or m1-host or m3-host", pool.Where);
            }

            // A later change in Companion › Pictures still reads the list made before; the list is the source now.
            Assert.True(new PicturesSettings { Place = PicturePlace.ComfyUi, Address = "http://lab:8188" }.Save(directory));
            Assert.Equal(3, PictureClient.List(directory).Members.Count);

            // One place: its maker alone, as before. An empty list: off.
            Assert.True(PictureClient.SaveList(directory, list with { Members = [list.Members[1]] }));
            using (var one = PictureClient.For(directory, Guid.Empty, null) as IDisposable) Assert.IsType<ComfyPictureMaker>(one);
            Assert.True(PictureClient.SaveList(directory, list with { Members = [] }));
            Assert.False(PictureClient.IsSetUp(directory));
            Assert.Null(PictureClient.For(directory, Guid.Empty, null));
            Assert.Null(PictureClient.Painter(directory));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void A_cloud_place_needs_the_owners_agreement_and_is_named_before_a_paid_picture()
    {
        var directory = Directory.CreateTempSubdirectory("martlet-picture-pool-").FullName;
        try
        {
            var cloud = PoolMember.Cloud(PicturePoolMembers.OpenRouter, "google/gemini-3.1-flash-image");
            var address = PoolMember.Service("http://lab:8188/").WithSetting(PoolSettingKeys.Workflow, PicturePoolMembers.ZImageTurbo);
            Assert.True(PictureClient.SaveList(directory, new PoolList { Area = PoolAreas.Pictures.Id, Members = [address, cloud] }));
            // Without the agreement the provider takes no pictures, so nothing paid is named.
            Assert.Null(PictureClient.FirstPaid(directory));
            Assert.IsType<ComfyPictureMaker>(PictureClient.For(directory, Guid.Empty, null));

            Assert.True(PictureClient.SaveList(directory, PictureClient.List(directory).With(cloud.WithConsent(PoolAreas.Pictures.Id, Now))));
            Assert.Equal("OpenRouter (google/gemini-3.1-flash-image)", PictureClient.FirstPaid(directory));
            using (var pool = Assert.IsType<PicturePool>(PictureClient.For(directory, Guid.Empty, null)))
                Assert.Equal(["address:http://lab:8188/", "cloud:openrouter/google/gemini-3.1-flash-image"], pool.Members.Select(m => m.Id));

            // Its own key's reference is read from pool-keys.json.
            var id = Guid.NewGuid();
            Assert.True(PoolKeys.Load(directory).With(PoolAreas.Pictures.Id, cloud.Key, id).Save(directory));
            Assert.Equal(id, PictureClient.CloudPlace(directory, cloud)!.CredentialId);
            Assert.Null(PictureClient.CloudPlace(directory, address));
        }
        finally { Directory.Delete(directory, true); }
    }
}
