using Martlet.Core.Cluster;
using Martlet.Core.Pictures;

namespace Martlet.Core.Tests;

public sealed class PicturePoolMembersTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Pictures_role_choice_becomes_the_chosen_computer_then_the_others_with_the_same_workflow()
    {
        var older = new PicturesSettings { Place = PicturePlace.Host, HostId = "m4-host", Workflow = PictureWorkflow.Checkpoint, Checkpoint = "dreamshaper_8.safetensors" };
        var list = PicturePoolMembers.Migrate(older, ["m4-host", "m3-host", "m1-host"], At);
        Assert.Equal(PoolAreas.Pictures.Id, list.Area);
        Assert.Equal(["host:m4-host", "host:m3-host", "host:m1-host"], list.Members.Select(m => m.Key));
        Assert.All(list.Members, m =>
        {
            Assert.Equal(PicturePoolMembers.Checkpoint, m.Setting(PoolSettingKeys.Workflow));
            Assert.Equal("dreamshaper_8.safetensors", m.Setting(PoolSettingKeys.Checkpoint));
            Assert.Null(m.Setting(PoolSettingKeys.File));
        });
    }

    [Fact]
    public void Other_choices_become_one_member_and_off_an_empty_list()
    {
        var comfy = PicturePoolMembers.Migrate(new PicturesSettings { Place = PicturePlace.ComfyUi, Address = "http://lab:8188", Workflow = PictureWorkflow.Custom },
            ["m1-host"], At);
        var member = Assert.Single(comfy.Members);
        Assert.Equal("address:http://lab:8188", member.Key);
        Assert.Equal(PicturePoolMembers.Custom, member.Setting(PoolSettingKeys.Workflow));
        Assert.Equal(PicturesSettings.WorkflowFile, member.Setting(PoolSettingKeys.File));

        var cloud = Assert.Single(PicturePoolMembers.Migrate(new PicturesSettings { Place = PicturePlace.NvidiaBuild }, [], At).Members);
        Assert.Equal("cloud:nvidia-build/" + PicturesSettings.NvidiaDefaultModel, cloud.Key);
        Assert.True(cloud.Consented(PoolAreas.Pictures.Id));
        Assert.Empty(cloud.Settings);

        var off = PicturePoolMembers.Migrate(new PicturesSettings(), ["m1-host"], At);
        Assert.Empty(off.Members);
        Assert.True(PoolRouting.Order(PoolAreas.Pictures, off, "desk-1").Off);

        Assert.Equal(PoolMember.ThisPcKey, Assert.Single(PicturePoolMembers.Migrate(new PicturesSettings { Place = PicturePlace.Host }, [], At).Members).Key);
    }

    [Fact]
    public void Each_member_reads_back_as_the_one_place_choice_the_picture_makers_take()
    {
        var credential = Guid.NewGuid();
        var computer = PicturePoolMembers.Place(PicturePoolMembers.WithWorkflow(PoolMember.Computer("m3-host"), PictureWorkflow.Checkpoint, "a.safetensors", null), "own", null)!;
        Assert.Equal((PicturePlace.Host, "m3-host", PictureWorkflow.Checkpoint, "a.safetensors"), (computer.Place, computer.HostId, computer.Workflow, computer.Checkpoint));
        Assert.Equal("own", PicturePoolMembers.Place(PoolMember.ThisPc(), "own", null)!.HostId);
        var address = PicturePoolMembers.Place(PoolMember.Service("http://lab:8188"), null, null)!;
        Assert.Equal((PicturePlace.ComfyUi, "http://lab:8188", PictureWorkflow.ZImageTurbo), (address.Place, address.Address, address.Workflow));
        var cloud = PicturePoolMembers.Place(PoolMember.Cloud(PicturePoolMembers.OpenRouter, "m/x"), null, credential)!;
        Assert.Equal((PicturePlace.OpenRouter, "m/x", credential), (cloud.Place, cloud.ModelId, cloud.CredentialId));
        Assert.Null(PicturePoolMembers.Place(PoolMember.Cloud("openai", "gpt-image-1"), null, null));
        Assert.Null(PicturePoolMembers.Place(PoolMember.Computer("m3-host").WithSetting(PoolSettingKeys.Workflow, "flux"), null, null));
    }

    [Fact]
    public void Others_are_the_planned_picture_computers_own_first_then_fewest_jobs_never_kept_for_another_pc()
    {
        ClusterNode Node(string host, string role) => new()
        {
            HostId = host, Roles = [new ClusterNodeRole { Kind = role, Model = "" }], Revision = 1, UpdatedAt = At, UpdatedBy = "test"
        };
        var plan = ClusterPlan.Empty with
        {
            Nodes = [Node("m1-host", "pictures"), Node("m2-host", "stt"), Node("m3-host", "pictures"), Node("m4-host", "pictures"),
                Node("m5-host", "pictures") with { Removed = true }, Node("m6-host", "pictures"), Node("m7-host", "pictures")],
            Assignments = [new ClusterAssignment { Job = ClusterJobs.Speaking, HostId = "m6-host", Revision = 1, UpdatedAt = At, UpdatedBy = "test" }]
        };
        var sharing = new WorkSharingSettings().With(new WorkSharingHost { HostId = "m7-host", OnlyFor = ["desk-9"] });
        var others = PicturePoolMembers.Others(plan, ["m1-host", "m2-host", "m3-host", "m4-host", "m5-host", "m6-host", "m7-host"], "m4-host",
            "m3-host", sharing, "desk-1");
        Assert.Equal(["m4-host", "m1-host", "m6-host"], others);
        Assert.Equal(["m4-host"], PicturePoolMembers.Others(plan, ["m4-host"], null, "m3-host", sharing, "desk-1"));
    }

    [Fact]
    public void A_member_workflow_file_is_a_json_file_name_in_the_data_directory()
    {
        var directory = Directory.CreateTempSubdirectory("martlet-picture-pool-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(directory, "pictures-workflow-2.json"), """{"1":{"class_type":"SaveImage","inputs":{}}}""");
            Assert.NotNull(PicturesSettings.LoadWorkflow(directory, "pictures-workflow-2.json"));
            Assert.Null(PicturesSettings.LoadWorkflow(directory, "..\\pictures-workflow-2.json"));
            Assert.Null(PicturesSettings.LoadWorkflow(directory, "pictures-workflow-2.txt"));
            Assert.Null(PicturesSettings.LoadWorkflow(directory, "missing.json"));
            Assert.Equal(PicturesSettings.WorkflowFile, PicturePoolMembers.WorkflowFile(PoolMember.ThisPc()));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
