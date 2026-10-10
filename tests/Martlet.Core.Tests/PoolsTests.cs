using Martlet.Core.Cluster;

namespace Martlet.Core.Tests;

public sealed class PoolsTests
{
    [Fact]
    public void Member_keys_name_each_kind_for_the_queue()
    {
        Assert.Equal("this-pc", PoolMember.ThisPc().Key);
        Assert.Equal("host:m1-host", PoolMember.Computer("m1-host").Key);
        Assert.Equal("host:m1-host#gpu2", PoolMember.Gpu("m1-host", 2).Key);
        Assert.Equal("address:http://lab:8188/", PoolMember.Service("http://lab:8188/").Key);
        Assert.Equal("cloud:openai/gpt-4o-mini-tts", PoolMember.Cloud("openai", "gpt-4o-mini-tts").Key);
        Assert.Equal("cloud:chat-completions@https://openrouter.ai/api/v1/x", PoolMember.Cloud("chat-completions", "x", "https://openrouter.ai/api/v1").Key);
        Assert.Equal("m1-host, card 2", PoolMember.Gpu("m1-host", 2).Name);
    }

    [Fact]
    public void Lists_round_trip_as_the_same_canonical_json_and_keep_member_order()
    {
        var list = new PoolList { Area = PoolAreas.Speaking.Id }
            .With(PoolMember.Computer("m3-host").WithSetting(PoolSettingKeys.Engine, "chatterbox"))
            .With(PoolMember.ThisPc() with { OnlyFor = ["desk-2", "desk-1", "desk-1"] })
            .With(PoolMember.Cloud("openai", "gpt-4o-mini-tts").WithConsent(PoolAreas.Speaking.Id, DateTimeOffset.UnixEpoch));
        var settings = new PoolSettings().With(list).With(new PoolList { Area = PoolAreas.LipSync.Id });
        var shared = settings.Share();
        var parsed = PoolSettings.Parse(shared);
        Assert.NotNull(parsed);
        Assert.Equal(shared, parsed.Share());
        Assert.Contains("\"kind\":\"this-pc\"", shared);
        Assert.Equal(["host:m3-host", "this-pc", "cloud:openai/gpt-4o-mini-tts"], parsed.Pool(PoolAreas.Speaking.Id).Members.Select(m => m.Key));
        Assert.Equal(["desk-1", "desk-2"], parsed.Pool(PoolAreas.Speaking.Id).Members[1].OnlyFor);
        Assert.Equal("chatterbox", parsed.Pool(PoolAreas.Speaking.Id).Members[0].Setting(PoolSettingKeys.Engine));
        // An empty list is kept: the area is off (or runs its fallback), not "never set up".
        Assert.True(parsed.Has(PoolAreas.LipSync.Id));
        Assert.Empty(parsed.Pool(PoolAreas.LipSync.Id).Members);
        Assert.False(parsed.Has(PoolAreas.Listening.Id));
    }

    [Fact]
    public void Lists_this_martlet_does_not_read_are_refused()
    {
        Assert.Null(PoolSettings.Parse("""{"schema_version":2,"pools":[]}"""));
        Assert.Null(PoolSettings.Parse("""{"schema_version":1,"pools":[{"area":"speaking","members":[{"kind":"computer"}]}]}"""));
        Assert.Null(PoolSettings.Parse("""{"schema_version":1,"pools":[{"area":"speaking","members":[{"kind":"address","address":"file:///c:/x"}]}]}"""));
        Assert.Null(PoolSettings.Parse("""{"schema_version":1,"pools":[{"area":"speaking","members":[{"kind":"computer","host_id":"a"},{"kind":"computer","host_id":"a"}]}]}"""));
        Assert.Null(PoolSettings.Parse("""{"schema_version":1,"pools":[{"area":"speaking","members":[{"kind":"quantum"}]}]}"""));
    }

    [Fact]
    public void Order_keeps_the_owners_order_and_skips_members_that_cannot_take_the_work()
    {
        var area = PoolAreas.Speaking;
        var list = new PoolList
        {
            Area = area.Id,
            Members =
            [
                PoolMember.Computer("m1-host") with { OnlyFor = ["desk-1"] },
                PoolMember.Computer("m2-host") with { Off = true },
                PoolMember.Cloud("openai", "tts-1"),
                PoolMember.Service("http://lab:8188/"),
                PoolMember.ThisPc(),
                PoolMember.Gpu("m3-host", 2)
            ]
        };
        var order = PoolRouting.Order(area, list, "desk-2", m => m.HostId != "m9-host");
        // Kept for desk-1, off, a cloud provider without the owner's agreement, and an address Speaking doesn't take are left out.
        Assert.Equal(["this-pc", "host:m3-host#gpu2"], order.Members.Select(m => m.Key));
        Assert.False(order.Off);
        Assert.Equal(["host:m1-host", "this-pc", "host:m3-host#gpu2"], PoolRouting.Order(area, list, "desk-1").Members.Select(m => m.Key));
        var agreed = list.With(PoolMember.Cloud("openai", "tts-1").WithConsent(area.Id, DateTimeOffset.UnixEpoch));
        Assert.Contains(PoolRouting.Order(area, agreed, "desk-2").Members, m => m.Kind == PoolMemberKind.Cloud);
        // An agreement is for one area and one member: another model needs a new one.
        var moved = agreed.Find("cloud:openai/tts-1")! with { Model = "tts-1-hd" };
        Assert.False(moved.Consented(area.Id));
        Assert.False(agreed.Find("cloud:openai/tts-1")!.Consented(PoolAreas.Listening.Id));
    }

    [Fact]
    public void An_empty_list_is_off_for_an_optional_area_and_the_fallback_for_a_required_one()
    {
        var off = PoolRouting.Order(PoolAreas.Speaking, new PoolList { Area = PoolAreas.Speaking.Id }, "desk-1");
        Assert.True(off.Configured);
        Assert.True(off.Off);
        Assert.False(off.Fallback);
        var lipSync = PoolRouting.Order(PoolAreas.LipSync, new PoolList { Area = PoolAreas.LipSync.Id, Members = [PoolMember.Computer("m4") with { Off = true }] }, "desk-1");
        Assert.False(lipSync.Off);
        Assert.True(lipSync.Fallback);
        Assert.Equal("Voice loudness on this PC", PoolAreas.LipSync.WhenEmpty);
        Assert.False(PoolRouting.Order(PoolAreas.Listening, null, "desk-1").Configured);
    }

    [Fact]
    public void Moving_and_removing_members_change_the_order()
    {
        var list = new PoolList { Area = "speaking", Members = [PoolMember.Computer("a"), PoolMember.Computer("b"), PoolMember.Computer("c")] };
        Assert.Equal(["host:c", "host:a", "host:b"], list.Move("host:c", -5).Members.Select(m => m.Key));
        Assert.Equal(["host:a", "host:c", "host:b"], list.Move("host:b", 1).Members.Select(m => m.Key));
        Assert.Equal(["host:a", "host:c"], list.Without("host:b").Members.Select(m => m.Key));
        Assert.Equal(["host:a", "host:b", "host:c"], list.With(PoolMember.Computer("b") with { Off = true }).Members.Select(m => m.Key));
        Assert.True(list.With(PoolMember.Computer("b") with { Off = true }).Find("host:b")!.Off);
    }

    [Fact]
    public void Migration_keeps_the_order_never_and_kept_for_choices_of_sharing_work()
    {
        WorkPlace[] runs = [new("m3-host"), new("m5-host", Jobs: 1), new("m6-host"), new("m7-host", Own: true)];
        var legacy = new WorkSharingSettings()
            .With(new WorkSharingJob { Job = WorkSharingJobs.Speaking, Order = [WorkSharingSettings.ThisPc, "m5-host"], Never = ["m6-host"] })
            .With(new WorkSharingHost { HostId = "m3-host", OnlyFor = ["desk-3"] });
        var speaking = PoolMigration.FromWorkSharing(PoolAreas.Speaking, legacy, PoolMember.Computer("m1-host"), runs);
        Assert.Equal(["this-pc", "host:m5-host", "host:m1-host", "host:m3-host"], speaking.Members.Select(m => m.Key));
        Assert.Equal(["desk-3"], speaking.Find("host:m3-host")!.OnlyFor);
        // Not shared: only the area's own choice.
        var off = legacy.With(new WorkSharingJob { Job = WorkSharingJobs.Listening, Share = false });
        Assert.Equal(["cloud:openai/whisper-1"],
            PoolMigration.FromWorkSharing(PoolAreas.Listening, off, PoolMember.Cloud("openai", "whisper-1"), runs).Members.Select(m => m.Key));
        // Listening is shared by default: its own choice, this PC's own host service, then the rest.
        Assert.Equal(["host:m1-host", "this-pc", "host:m3-host", "host:m6-host", "host:m5-host"],
            PoolMigration.FromWorkSharing(PoolAreas.Listening, legacy, PoolMember.Computer("m1-host"), runs).Members.Select(m => m.Key));
        // Thinking isn't shared unless chosen; when it is, the list holds only the other computers (its own model goes first).
        Assert.Empty(PoolMigration.FromWorkSharing(PoolAreas.Thinking, legacy, PoolMember.Computer("m1-host"), runs).Members);
        var thinking = legacy.With(new WorkSharingJob { Job = WorkSharingJobs.Thinking, Share = true });
        Assert.Equal(["host:m3-host", "host:m6-host", "host:m7-host", "host:m5-host"],
            PoolMigration.FromWorkSharing(PoolAreas.Thinking, thinking, PoolMember.Computer("m1-host"), runs).Members.Select(m => m.Key));
    }

    [Fact]
    public void Shared_and_local_lists_live_in_their_own_files_and_keys_stay_on_this_pc()
    {
        var directory = Path.Combine(Path.GetTempPath(), "martlet-pools-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Null(PoolSettings.LoadFor(directory, PoolAreas.Pictures));
            Assert.True(PoolSettings.SaveFor(directory, PoolAreas.Pictures, new PoolList
            {
                Area = PoolAreas.Pictures.Id, Members = [PoolMember.Service("http://lab:8188/").WithSetting(PoolSettingKeys.Workflow, "custom")]
            }));
            Assert.True(PoolSettings.SaveFor(directory, PoolAreas.Speaking, new PoolList { Area = PoolAreas.Speaking.Id, Members = [PoolMember.ThisPc()] }));
            Assert.True(PoolSettings.Saved(directory, shared: false));
            Assert.True(PoolSettings.Saved(directory, shared: true));
            Assert.False(PoolSettings.Load(directory).Has(PoolAreas.Pictures.Id));
            Assert.Equal("custom", PoolSettings.LoadFor(directory, PoolAreas.Pictures)!.Members[0].Setting(PoolSettingKeys.Workflow));
            Assert.Equal(["this-pc"], PoolSettings.LoadFor(directory, PoolAreas.Speaking)!.Members.Select(m => m.Key));

            var id = Guid.NewGuid();
            Assert.True(new PoolKeys().With("pictures", "cloud:openrouter/x", id).Save(directory));
            Assert.Equal(id, PoolKeys.Load(directory).For("pictures", "cloud:openrouter/x"));
            Assert.Null(PoolKeys.Load(directory).For("speaking", "cloud:openrouter/x"));
            Assert.DoesNotContain(id.ToString(), File.ReadAllText(Path.Combine(directory, PoolSettings.FileName)));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Hosts_resolve_members_to_computers_that_run_the_engine()
    {
        var runs = new HashSet<string>(["m3-host", "m7-host"], StringComparer.Ordinal);
        var list = new PoolList
        {
            Area = PoolAreas.Speaking.Id,
            Members = [PoolMember.Cloud("openai", "tts-1"), PoolMember.ThisPc(), PoolMember.Gpu("m3-host", 2), PoolMember.Computer("m3-host"),
                PoolMember.Computer("m9-host"), PoolMember.Computer("m1-host")]
        };
        // A cloud member isn't a computer; this PC is its own host service; a card is its computer; m9-host doesn't run it.
        Assert.Equal(["m7-host", "m3-host", "m1-host"], PoolRouting.Hosts(PoolAreas.Speaking, list, "desk-7", "m1-host", "m7-host", runs));
        Assert.Equal(["m3-host", "m1-host"], PoolRouting.Hosts(PoolAreas.Speaking, list, "desk-2", "m1-host", null, runs));
        Assert.Equal(["m1-host"], PoolRouting.Hosts(PoolAreas.Speaking, new PoolList { Area = "speaking" }, "desk-2", "m1-host", null, runs));
        Assert.Equal(["m1-host", "m3-host"],
            PoolRouting.Hosts(PoolAreas.Thinking, new PoolList { Area = "thinking", Members = [PoolMember.Computer("m3-host")] }, "desk-2", "m1-host", null, runs));
    }

    [Fact]
    public void Cloud_and_address_failures_map_to_queue_refusals()
    {
        Assert.Equal(WorkRefusal.Busy, PoolRefusals.Http(429));
        Assert.Equal(WorkRefusal.Busy, PoolRefusals.Http(503));
        Assert.Equal(WorkRefusal.Unavailable, PoolRefusals.Http(401));
        Assert.Equal(WorkRefusal.Unavailable, PoolRefusals.Http(500));
        Assert.Equal(WorkRefusal.None, PoolRefusals.Http(400));
        Assert.Equal(WorkRefusal.Unavailable, PoolRefusals.Network(new HttpRequestException("down")));
    }

    [Fact]
    public async Task The_queue_passes_a_busy_cloud_member_for_the_next_by_member_key()
    {
        var queue = new WorkQueue { Retry = TimeSpan.FromMilliseconds(5) };
        PoolMember[] members = [PoolMember.Cloud("openai", "tts-1"), PoolMember.Computer("m3-host")];
        var answer = await queue.RunAsync(PoolAreas.Speaking.Id, members, m => m.Key,
            (m, _) => m.Kind == PoolMemberKind.Cloud ? Task.FromException<string>(new RateLimited()) : Task.FromResult(m.Key),
            e => e is RateLimited ? PoolRefusals.Http(429) : WorkRefusal.None, DateTimeOffset.UtcNow.AddSeconds(5), null, CancellationToken.None);
        Assert.Equal("host:m3-host", answer);
    }

    private sealed class RateLimited : Exception;
}
