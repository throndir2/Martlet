using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Conversation.Tests;

public sealed class LiveFloorTests
{
    private static LiveFloor Floor(RuntimeClock clock) => new(clock);

    [Fact]
    public void The_users_voice_is_listening_until_words_or_six_quiet_seconds()
    {
        var clock = new RuntimeClock();
        using var floor = Floor(clock);
        List<LiveFloorChange> changes = [];
        floor.Changed += changes.Add;
        Assert.Equal(LiveFloorLevel.Idle, floor.Level);
        floor.Heard();
        Assert.Equal(LiveFloorLevel.Listening, floor.Level);
        // Every 20 ms frame of a voice calls it again: nothing more changes.
        floor.Heard();
        floor.Heard();
        clock.Advance(TimeSpan.FromSeconds(5.9));
        Assert.Equal(LiveFloorLevel.Listening, floor.Level);
        clock.Advance(TimeSpan.FromSeconds(0.2));
        Assert.Equal(LiveFloorLevel.Idle, floor.Level);
        Assert.Equal([(LiveFloorLevel.Idle, LiveFloorLevel.Listening), (LiveFloorLevel.Listening, LiveFloorLevel.Idle)],
            changes.Select(c => (c.From, c.To)));
        Assert.Equal("your voice", changes[0].Why);
        Assert.Equal(0, floor.Periods);
    }

    [Fact]
    public void A_sound_or_filler_ends_listening_at_once()
    {
        var clock = new RuntimeClock();
        using var floor = Floor(clock);
        floor.Heard();
        floor.NotWords("not words");
        Assert.Equal(LiveFloorLevel.Idle, floor.Level);
        Assert.Equal("not words", floor.Recent[^1].Why);
    }

    [Fact]
    public void Real_words_are_live_until_the_reply_is_made_and_its_grace_ends()
    {
        var clock = new RuntimeClock();
        using var floor = Floor(clock);
        floor.Heard();
        floor.Words();
        Assert.Equal(LiveFloorLevel.Live, floor.Level);
        Assert.Equal(1, floor.Periods);
        var reply = floor.BeginReply();
        // The reply holds the floor however long it takes; the words' own hold is no longer needed.
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(LiveFloorLevel.Live, floor.Level);
        Assert.Equal(1, floor.Replies);
        reply.End();
        reply.End();
        Assert.Equal(LiveFloorLevel.Live, floor.Level);
        clock.Advance(TimeSpan.FromSeconds(1.9));
        Assert.Equal(LiveFloorLevel.Live, floor.Level);
        clock.Advance(TimeSpan.FromSeconds(0.2));
        Assert.Equal(LiveFloorLevel.Idle, floor.Level);
        Assert.Equal(1, floor.Periods);
        Assert.Equal("the reply was done", floor.Recent[^1].Why);
    }

    [Fact]
    public void Words_without_a_reply_hold_the_floor_for_eight_seconds_and_a_fast_answer_keeps_it()
    {
        var clock = new RuntimeClock();
        using var floor = Floor(clock);
        floor.Words();
        clock.Advance(TimeSpan.FromSeconds(7.9));
        Assert.Equal(LiveFloorLevel.Live, floor.Level);
        clock.Advance(TimeSpan.FromSeconds(0.2));
        Assert.Equal(LiveFloorLevel.Idle, floor.Level);
        // A fast answer within the grace keeps the floor Live: one period, not two.
        var reply = floor.BeginReply();
        reply.End();
        clock.Advance(TimeSpan.FromSeconds(1));
        floor.Words();
        Assert.Equal(LiveFloorLevel.Live, floor.Level);
        Assert.Equal(2, floor.Periods);
    }

    [Fact]
    public void Dismissed_words_drop_the_floor_but_a_reply_still_holds_it_and_clear_ends_everything()
    {
        var clock = new RuntimeClock();
        using var floor = Floor(clock);
        floor.Words();
        floor.Dismiss();
        Assert.Equal(LiveFloorLevel.Idle, floor.Level);
        var reply = floor.BeginReply();
        floor.Words();
        floor.Dismiss();
        Assert.Equal(LiveFloorLevel.Live, floor.Level);
        floor.Clear();
        Assert.Equal(LiveFloorLevel.Idle, floor.Level);
        Assert.Equal(0, floor.Replies);
        // A reply cleared away ending later changes nothing.
        reply.End();
        Assert.Equal(LiveFloorLevel.Idle, floor.Level);
    }

    [Theory]
    [InlineData("Mmm.", false)]
    [InlineData("Yeah, right.", false)]
    [InlineData("Uh-huh, okay.", false)]
    [InlineData("Haha.", false)]
    [InlineData("What time is it in Tokyo?", true)]
    [InlineData("Martlet.", true)]
    [InlineData("Okay Martlet.", true)]
    public void Real_words_are_words_that_are_not_only_backchannel_or_filler(string said, bool real)
    {
        var context = new UtteranceContext { Voiced = TimeSpan.FromSeconds(1), Speech = TimeSpan.FromSeconds(2) };
        Assert.Equal(real, LiveFloor.RealWords(said, context, ListeningSensitivity.Normal));
    }

    [Fact]
    public void Changes_are_raised_in_order_and_a_failing_observer_changes_nothing()
    {
        var clock = new RuntimeClock();
        using var floor = Floor(clock);
        List<LiveFloorLevel> seen = [];
        floor.Changed += _ => throw new InvalidOperationException("observer");
        floor.Changed += change => seen.Add(change.To);
        floor.Heard();
        floor.Words();
        var reply = floor.BeginReply();
        reply.End();
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal([LiveFloorLevel.Listening, LiveFloorLevel.Live, LiveFloorLevel.Idle], seen);
        Assert.Equal(3, floor.Recent.Count);
    }

    // ---------- what the conversation runs on ----------

    private static SetupRoute Route(SetupRole role, string origin, SetupRouteType type = SetupRouteType.ChatCompletions) => new()
    {
        RouteType = type, Role = role, ProviderAlias = "chat-completions", Origin = origin, ModelId = "model", ConfigurationRevision = Guid.NewGuid()
    };

    [Fact]
    public void A_member_shares_the_conversation_on_the_same_computer_and_graphics_card()
    {
        var resources = new LiveResources([new("thinking", LiveResources.ThisPc, []), new("voice", "lan:192.168.1.20", ["GPU-1"])]);
        Assert.True(resources.Shares(new("endpoint:a", "this PC") { Machine = LiveResources.ThisPc }));
        // Unknown graphics cards on either side: the same computer is enough.
        Assert.True(resources.Shares(new("host:b", "b") { Machine = "lan:192.168.1.20" }));
        Assert.True(resources.Shares(new("host:b", "b") { Machine = "lan:192.168.1.20", Gpus = ["GPU-1", "GPU-2"] }));
        Assert.False(resources.Shares(new("host:b", "b") { Machine = "lan:192.168.1.20", Gpus = ["GPU-2"] }));
        Assert.False(resources.Shares(new("host:c", "c") { Machine = "lan:192.168.1.30" }));
        // A cloud provider shares nothing with your computers; the conversation's own model always shares.
        Assert.False(resources.Shares(new("endpoint:cloud", "openrouter.ai")));
        Assert.True(resources.Shares(new(LiveResources.ConversationModel, "openrouter.ai")));
        Assert.True(LiveResources.None.Shares(new(LiveResources.ConversationModel, "x")));
    }

    [Fact]
    public void The_live_routes_are_found_by_computer_and_a_paired_hosts_routes_can_be_held()
    {
        var routes = new[]
        {
            Route(SetupRole.Llm, "http://127.0.0.1:11434/v1"),
            Route(SetupRole.Stt, "https://api.openai.com", SetupRouteType.OpenAi),
            Route(SetupRole.Tts, "https://192.168.1.20:9443", SetupRouteType.GatewayF5) with
            {
                Gateway = new()
                {
                    SchemaVersion = 1, Origin = "https://192.168.1.20:9443", HostId = "diva",
                    SpkiFingerprint = "sha256:" + new string('a', 64), DeviceRole = SelfHostSetup.GatewayRole
                }
            }
        };
        var resources = LiveResources.For(routes, (host, route) => host == "diva" ? ["GPU-0"] : []);
        Assert.Equal([("thinking", LiveResources.ThisPc), ("listening", null), ("voice", "lan:192.168.1.20")],
            resources.Items.Select(i => (i.Job, i.Machine)));
        Assert.Equal(["GPU-0"], resources.Items[2].Gpus);
        var (host, held) = Assert.Single(resources.Hosts);
        Assert.Equal("diva", host);
        Assert.Equal([SelfHostSetup.Gateway(SetupRouteType.GatewayF5).RouteId], held);
        // A route turned off isn't live.
        Assert.Empty(LiveResources.For([Route(SetupRole.Llm, "http://127.0.0.1:11434/v1") with { Enabled = false }]).Items);
    }

    [Theory]
    [InlineData("http://127.0.0.1:11434/v1", LiveResources.ThisPc)]
    [InlineData("http://localhost:1234/v1", LiveResources.ThisPc)]
    [InlineData("https://192.168.1.20:9443", "lan:192.168.1.20")]
    [InlineData("https://openrouter.ai/api/v1", null)]
    [InlineData("not a url", null)]
    public void A_computer_is_this_pc_a_home_computer_or_a_cloud_provider(string origin, string? machine) =>
        Assert.Equal(machine, LiveResources.MachineOf(origin));

    [Fact]
    public void The_reply_latency_part_says_what_was_held_and_stopped()
    {
        Assert.Null(LiveFloorCounts.Empty.Describe());
        var counts = new LiveFloorCounts(new Dictionary<string, int> { ["digest"] = 1, ["memory"] = 1 },
            new Dictionary<string, int> { ["think-longer"] = 1, ["digest"] = 2 });
        Assert.Equal("held 2 pool jobs, stopped 3 (digest x2, think longer)", counts.Describe());
        Assert.Equal("stopped 1 (think longer)", new LiveFloorCounts(new Dictionary<string, int>(),
            new Dictionary<string, int> { ["think-longer"] = 1 }).Describe());
    }
}
