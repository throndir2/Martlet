using System.Collections.Concurrent;
using Martlet.Conversation;
using Martlet.Desktop;
using Martlet.Providers;

namespace Martlet.Desktop.Tests;

public sealed class HelperJobsTests
{
    [Fact]
    public async Task A_free_pool_member_takes_the_job_and_the_conversation_model_is_not_asked()
    {
        var pool = new FixturePool { Text = true };
        var helpers = new HelperJobs(() => pool, () => true, null);
        var fellBack = false;

        var result = await helpers.RunAsync(HelperJobKind.ActionNaming, "Naming character emotes", HelperCapability.Text,
            () => new BoundedTextInput("list", "instructions"), _ => { fellBack = true; return Task.FromResult<(string?, string?)>(("x", null)); },
            CancellationToken.None);

        Assert.True(result.Pooled);
        Assert.False(fellBack);
        Assert.Equal("pool answer", result.Answer);
        var job = Assert.Single(pool.Jobs);
        Assert.Equal(HelperJobKind.ActionNaming, job.Kind);
        Assert.Equal(HelperJobPriority.Low, job.Priority);
        Assert.Equal(HelperCapability.Text, job.Capability);
        var route = Assert.Single(helpers.Last);
        Assert.Equal("pool", route.Route);
        Assert.Equal("text-member", route.Member);
    }

    [Fact]
    public async Task Without_a_member_for_the_capability_it_falls_back_only_after_the_reply_finished()
    {
        var pool = new FixturePool { Text = true };
        var busy = 1;
        var helpers = new HelperJobs(() => pool, () => Volatile.Read(ref busy) == 1, null);
        var built = false;
        var asked = new TaskCompletionSource();

        var running = helpers.RunAsync(HelperJobKind.TouchZones, "Finding touch zones", HelperCapability.Vision,
            () => { built = true; return new BoundedTextInput("list", "instructions"); },
            _ => { asked.TrySetResult(); return Task.FromResult<(string?, string?)>(("zones", null)); }, CancellationToken.None);

        await Task.Delay(HelperJobs.IdleCheck * 3);
        Assert.False(asked.Task.IsCompleted);
        Volatile.Write(ref busy, 0);
        var result = await running.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(result.Pooled);
        Assert.False(built);
        Assert.Empty(pool.Jobs);
        Assert.Equal("zones", result.Answer);
        var route = Assert.Single(helpers.Last);
        Assert.Equal("fallback", route.Route);
        Assert.Equal(HelperJobPriority.Normal, HelperJobs.PriorityOf(route.Kind));
        Assert.True(route.WaitedMs > 0);
    }

    [Fact]
    public async Task No_pool_or_no_free_member_falls_back_and_the_status_file_says_so()
    {
        var directory = Directory.CreateTempSubdirectory("martlet-helpers-").FullName;
        try
        {
            IHelperJobPool? pool = null;
            var helpers = new HelperJobs(() => pool, () => false, directory);
            var answer = await helpers.RunAsync(HelperJobKind.Memory, "Remembering", HelperCapability.Text,
                () => throw new InvalidOperationException("not built without a pool"),
                _ => Task.FromResult<(string?, string?)>((null, "OutputTokenLimit")), CancellationToken.None);
            Assert.False(answer.Pooled);
            pool = new FixturePool { Text = true, Free = false };
            await helpers.RunAsync(HelperJobKind.ActionNaming, "Naming", HelperCapability.Text, () => new BoundedTextInput("a", "b"),
                _ => Task.FromResult<(string?, string?)>(("named", null)), CancellationToken.None);

            var status = File.ReadAllText(Path.Combine(directory, HelperJobs.StatusFile));
            Assert.Contains("\"kind\":\"memory\",\"route\":\"fallback\"", status);
            Assert.Contains("\"outcome\":\"failed: OutputTokenLimit\"", status);
            Assert.Contains("\"kind\":\"action-naming\",\"route\":\"fallback\"", status);
            Assert.Equal(2, helpers.Last.Count);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Remembering_after_a_reply_goes_to_a_pool_member_with_the_excerpt_not_the_conversation_model()
    {
        await using var fixture = await LiveFixture.Create();
        await fixture.EnableMemory();
        var pool = new FixturePool { Text = true, Answer = "REMEMBER: The user's dog is called Biscuit." };
        fixture.Controller.HelperPool = pool;
        fixture.Answer("Biscuit is a lovely name.");

        var first = fixture.Start("My dog is called Biscuit.");
        await fixture.Finish(first);
        await fixture.FinishRemembering();

        Assert.Equal(1, fixture.Llm.Calls);
        var job = Assert.Single(pool.Jobs);
        Assert.Equal(HelperJobKind.Memory, job.Kind);
        Assert.Equal(HelperJobPriority.Low, job.Priority);
        Assert.Contains("My dog is called Biscuit.", job.Input.UserText);
        Assert.Contains("Biscuit is a lovely name.", job.Input.UserText);
        Assert.Empty(job.Input.History);
        var revision = (await fixture.Store.LoadAsync()).Settings!.Memory!.ConfigurationRevision;
        Assert.Equal("The user's dog is called Biscuit.", Assert.Single((await fixture.Memory.InspectAsync(revision)).Facts).Content);
        Assert.Equal("pool", Assert.Single(fixture.Controller.Helpers.Last).Route);
    }

    [Fact]
    public async Task The_pool_adapter_posts_each_helper_kind_as_its_pool_kind_and_falls_back_when_the_pool_cannot_finish()
    {
        var text = new Martlet.Conversation.BackgroundPlace("text", "Text PC") { Model = "qwen3:8b" };
        var eyes = new Martlet.Conversation.BackgroundPlace("eyes", "Vision PC", 1) { Can = Martlet.Conversation.ThinkingCapability.Text | Martlet.Conversation.ThinkingCapability.Vision };
        var members = new List<Martlet.Conversation.BackgroundPlace> { text };
        var posted = new ConcurrentQueue<Martlet.Conversation.ThinkingJob>();
        var fail = false;
        var board = new Martlet.Conversation.ThinkingJobBoard(new Martlet.Conversation.BackgroundPlaces(), () => [.. members], (member, job, _) =>
        {
            posted.Enqueue(job);
            return Task.FromResult(fail ? Martlet.Conversation.ThinkingAnswer.Failed("broken") : Martlet.Conversation.ThinkingAnswer.Done("answer from " + member.Name));
        });
        var adapter = new ThinkingPoolHelpers(() => new DesktopThinkingPool(board));

        Assert.True(adapter.Has(HelperCapability.Text));
        Assert.False(adapter.Has(HelperCapability.Vision));
        var named = await adapter.TryRunAsync(new(HelperJobKind.Temperament, "Temperament", new BoundedTextInput("persona", "decide")), CancellationToken.None);
        Assert.Equal("answer from Text PC", named!.Answer);
        Assert.Equal("Text PC (qwen3:8b)", named.Member);
        var job = Assert.Single(posted);
        Assert.Equal(Martlet.Conversation.ThinkingJobKind.Naming, job.Kind);
        Assert.Equal("decide", job.Instructions);
        Assert.Equal("persona", job.Text);

        members.Add(eyes);
        Assert.True(adapter.Has(HelperCapability.Vision));
        Assert.Equal(Martlet.Conversation.ThinkingJobKind.Memory, ThinkingPoolHelpers.Kind(HelperJobKind.Memory));
        Assert.Equal(Martlet.Conversation.ThinkingJobKind.Naming, ThinkingPoolHelpers.Kind(HelperJobKind.ActionNaming));
        Assert.Equal(Martlet.Conversation.ThinkingJobKind.TouchZones, ThinkingPoolHelpers.Kind(HelperJobKind.TouchZones));

        // Measuring the eyes needs vision, like touch zones, but waits at the helpers' low priority.
        var image = new BoundedImage([0xFF, 0xD8, 0xFF, .. new byte[32]], ImageMediaType.Jpeg, 4, 4);
        var measured = await adapter.TryRunAsync(new(HelperJobKind.Eyes, "Measuring the eyes", new BoundedTextInput("face", "measure", image: image)), CancellationToken.None);
        Assert.Equal("answer from Vision PC", measured!.Answer);
        var eyeJob = posted.Last();
        Assert.Equal(Martlet.Conversation.ThinkingJobKind.TouchZones, eyeJob.Kind);
        Assert.Equal(Martlet.Conversation.ThinkingPriority.Helper, eyeJob.Priority);
        Assert.Equal(HelperJobPriority.Low, HelperJobs.PriorityOf(HelperJobKind.Eyes));
        Assert.Equal("eyes", HelperJobs.Name(HelperJobKind.Eyes));

        fail = true;
        Assert.Null(await adapter.TryRunAsync(new(HelperJobKind.Memory, "Remembering", new BoundedTextInput("a", "b")), CancellationToken.None));
        members.Clear();
        Assert.Null(await adapter.TryRunAsync(new(HelperJobKind.Memory, "Remembering", new BoundedTextInput("a", "b")), CancellationToken.None));
    }

    [Fact]
    public async Task Remembering_without_a_pool_uses_the_conversation_model_as_before()
    {
        await using var fixture = await LiveFixture.Create();
        await fixture.EnableMemory();
        fixture.Answer("Biscuit is a lovely name.");

        var first = fixture.Start("My dog is called Biscuit.");
        await fixture.Finish(first);
        await fixture.FinishRemembering();

        Assert.Equal(2, fixture.Llm.Calls);
        var route = Assert.Single(fixture.Controller.Helpers.Last);
        Assert.Equal(HelperJobKind.Memory, route.Kind);
        Assert.Equal("fallback", route.Route);
    }

    [Fact]
    public async Task Tidying_memory_runs_only_on_a_pool_member_and_gives_facts_search_words()
    {
        await using var fixture = await LiveFixture.Create();
        await fixture.EnableMemory();
        var revision = (await fixture.Store.LoadAsync()).Settings!.Memory!.ConfigurationRevision;
        await fixture.Memory.SaveFactAsync(revision, "The user is vegetarian.", Martlet.Memory.MemoryRetention.UntilDeleted());

        // No pool member: nothing runs, and the conversation's own model is never asked.
        fixture.Controller.HelperPool = new FixturePool { Text = false };
        Assert.Null(await fixture.Controller.MemoryUpkeepPassAsync(CancellationToken.None));
        Assert.Equal(0, fixture.Llm.Calls);

        var pool = new FixturePool { Text = true, Answer = "WORDS 1: diet, food, meat, cooking" };
        fixture.Controller.HelperPool = pool;
        Assert.Null(await fixture.Controller.MemoryUpkeepPassAsync(CancellationToken.None));
        var job = Assert.Single(pool.Jobs);
        Assert.Equal((HelperJobKind.MemoryUpkeep, HelperJobPriority.Low), (job.Kind, job.Priority));
        Assert.Contains("1. The user is vegetarian.", job.Input.UserText);
        Assert.Contains("SAME <number>", job.Input.Personality);
        Assert.Equal(0, fixture.Llm.Calls);
        var fact = Assert.Single((await fixture.Memory.InspectAsync(revision)).Facts);
        Assert.Equal("diet, food, meat, cooking", fact.SearchWords);
        Assert.Equal("memory-upkeep", HelperJobs.Name(fixture.Controller.Helpers.Last.Single(r => r.Kind == HelperJobKind.MemoryUpkeep).Kind));

        // Nothing is left to do: the next pass asks nothing.
        Assert.Null(await fixture.Controller.MemoryUpkeepPassAsync(CancellationToken.None));
        Assert.Single(pool.Jobs);
    }

    private sealed class FixturePool : IHelperJobPool
    {
        internal bool Text { get; init; }
        internal bool Vision { get; init; }
        internal bool Free { get; init; } = true;
        internal bool Tools { get; init; }
        internal string Answer { get; init; } = "pool answer";
        internal Func<HelperJob, string>? AnswerFor { get; init; }
        internal ConcurrentQueue<HelperJob> Jobs { get; } = new();

        public bool Has(HelperCapability capability) => capability == HelperCapability.Vision ? Vision : Text;

        public bool CallsTools(HelperJobKind kind) => Tools;

        public Task<HelperPoolAnswer?> TryRunAsync(HelperJob job, CancellationToken token)
        {
            if (!Free || !Has(job.Capability)) return Task.FromResult<HelperPoolAnswer?>(null);
            Jobs.Enqueue(job);
            return Task.FromResult<HelperPoolAnswer?>(new(job.Capability == HelperCapability.Vision ? "vision-member" : "text-member",
                AnswerFor?.Invoke(job) ?? Answer, null));
        }
    }

    [Fact]
    public async Task Learning_names_goes_to_a_naming_member_with_the_people_tools_when_one_calls_tools()
    {
        var caller = new Martlet.Conversation.BackgroundPlace("tools", "Tools PC")
        {
            Can = Martlet.Conversation.ThinkingCapability.Text | Martlet.Conversation.ThinkingCapability.Tools
        };
        var members = new List<Martlet.Conversation.BackgroundPlace>();
        var posted = new ConcurrentQueue<Martlet.Conversation.ThinkingJob>();
        var board = new Martlet.Conversation.ThinkingJobBoard(new Martlet.Conversation.BackgroundPlaces(), () => [.. members], (member, job, _) =>
        {
            posted.Enqueue(job);
            return Task.FromResult(Martlet.Conversation.ThinkingAnswer.Done("NOTHING"));
        });
        var adapter = new ThinkingPoolHelpers(() => new DesktopThinkingPool(board));
        Assert.False(adapter.CallsTools(HelperJobKind.SpeakerNaming));
        members.Add(caller);
        Assert.True(adapter.CallsTools(HelperJobKind.SpeakerNaming));
        Assert.Equal(Martlet.Conversation.ThinkingJobKind.Naming, ThinkingPoolHelpers.Kind(HelperJobKind.SpeakerNaming));
        Assert.Equal("speaker-naming", HelperJobs.Name(HelperJobKind.SpeakerNaming));
        Assert.Equal(HelperJobPriority.Low, HelperJobs.PriorityOf(HelperJobKind.SpeakerNaming));

        var host = new Martlet.Conversation.CheckInToolHost([Martlet.Conversation.PeopleTools.SetId],
            new("learning-names", "Learning names", null, DateTimeOffset.UtcNow),
            new Dictionary<string, Martlet.Conversation.CheckInToolHandler>
            {
                [Martlet.Conversation.PeopleTools.SetId] = (call, context, token) =>
                    new(new Martlet.Conversation.ConversationToolResult("People: 0 voice(s) saved, 0 listed."))
            });
        var answer = await adapter.TryRunAsync(new(HelperJobKind.SpeakerNaming, "Learning names", new BoundedTextInput("voices", "who"))
        {
            Tools = new(host.Tools, host, 2)
        }, CancellationToken.None);

        Assert.Equal("NOTHING", answer!.Answer);
        var job = Assert.Single(posted);
        Assert.Equal(Martlet.Conversation.ThinkingJobKind.Naming, job.Kind);
        Assert.Equal(Martlet.Conversation.ThinkingPriority.Helper, Martlet.Conversation.ThinkingJobKinds.Priority(job.Kind));
        Assert.Null(job.Priority);
        Assert.Equal("Learning names", job.Label);
        Assert.Equal([Martlet.Conversation.PeopleTools.List, Martlet.Conversation.PeopleTools.Lately], job.Tools.Select(t => t.Name));
        Assert.Same(host, job.ToolHost);
        Assert.Equal(2, job.MaxToolRounds);
        Assert.True(job.Required.HasFlag(Martlet.Conversation.ThinkingCapability.Tools));
        Assert.True(Martlet.Conversation.LiveFloorRules.Stops(job.Kind));
    }

    // Learning names is a request of its own (before remembering, which keeps its own), and a SPOKE line about a voice Martlet
    // wasn't sure of only labels the message: the voice list stays exactly as it was.
    [Fact]
    public async Task Learning_names_is_its_own_request_and_a_spoke_line_only_labels_the_message()
    {
        await using var fixture = await LiveFixture.Create(voices: true);
        Assert.True(fixture.Voices!.Active, "the voice recognition models are in the test's folder");
        var now = DateTimeOffset.UtcNow;
        var roster = Martlet.Core.Speakers.VoiceRoster.Empty;
        (roster, var added) = roster.Add(Print(1), 3, "desk", now);
        fixture.Voices.Merge(roster.AddHeardName(added!.Id, "Kim", "desk", now));
        var kim = fixture.Voices.Roster.Resolve(added.Id)!;
        await fixture.EnableMemory();
        var pool = new FixturePool
        {
            Text = true, Tools = true,
            AnswerFor = job => job.Kind == HelperJobKind.SpeakerNaming ? $"SPOKE {kim.Tag}\nNAME {kim.Tag}: Kimmy" : "REMEMBER: The user's dog is called Biscuit."
        };
        fixture.Controller.HelperPool = pool;
        var named = new ConcurrentQueue<Martlet.Core.Speakers.VoiceUpdateResult>();
        fixture.Controller.VoicesNamed += learned => { foreach (var result in learned) named.Enqueue(result); };
        fixture.Answer("Biscuit is a lovely name.");
        var heard = new HeardVoices([new(null, Martlet.Core.Speakers.VoiceMatchKind.Unsure, 0.6, 3, false) { Closest = kim }], false);
        var before = fixture.Voices.Roster.Digest();

        var first = fixture.Controller.Start("My dog is called Biscuit.", voice: false, microphone: false, approved: true, spoken: true, heard: heard);
        await fixture.Finish(first);
        await fixture.FinishRemembering();

        Assert.Equal(1, fixture.Llm.Calls);
        Assert.Equal(new[] { HelperJobKind.SpeakerNaming, HelperJobKind.Memory }, pool.Jobs.Select(j => j.Kind).ToArray());
        var naming = pool.Jobs.First();
        Assert.Equal("Learning names", naming.Purpose);
        Assert.Contains("SPOKE V<number>", naming.Input.Personality);
        Assert.DoesNotContain("REMEMBER:", naming.Input.Personality);
        Assert.Contains($"It sounds most like {kim.Tag}: goes by Kim, but not enough to be sure.", naming.Input.UserText);
        Assert.Contains(Martlet.Conversation.PeopleTools.List, naming.Input.UserText);
        Assert.Equal([Martlet.Conversation.PeopleTools.List, Martlet.Conversation.PeopleTools.Lately], naming.Tools!.Definitions.Select(t => t.Name));
        var remembering = pool.Jobs.Last();
        Assert.Null(remembering.Tools);
        Assert.Contains("REMEMBER:", remembering.Input.Personality);
        Assert.DoesNotContain("NAME V<number>", remembering.Input.Personality);
        var label = Assert.Single(named);
        Assert.Equal(Martlet.Core.Speakers.VoiceUpdateKind.Spoke, label.Kind);
        Assert.Equal(kim.Id, label.Voice.Id);
        Assert.Equal(before, fixture.Voices.Roster.Digest());
    }

    private static float[] Print(int seed)
    {
        var random = new Random(seed);
        return Martlet.Core.Speakers.VoicePrints.Normalize(Enumerable.Range(0, Martlet.Core.Speakers.VoicePrints.Dimension)
            .Select(_ => (float)(random.NextDouble() - 0.5)).ToArray());
    }

    [Fact]
    public async Task The_fallback_on_the_conversation_model_waits_for_the_live_floor_and_runs_again_when_it_goes_live()
    {
        using var floor = new Martlet.Conversation.LiveFloor();
        IHelperJobPool? pool = null;
        var helpers = new HelperJobs(() => pool, () => false, null) { Floor = floor };
        var calls = 0;
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // You are talking with Martlet: the conversation model is busy with you, so the fallback doesn't start.
        var reply = floor.BeginReply();
        var running = helpers.RunAsync(HelperJobKind.Memory, "Remembering", HelperCapability.Text,
            () => throw new InvalidOperationException("no pool"), async token =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    first.TrySetResult();
                    await Task.Delay(Timeout.Infinite, token);
                }
                return ("facts", (string?)null);
            }, CancellationToken.None);
        await Task.Delay(HelperJobs.IdleCheck * 3);
        Assert.Equal(0, Volatile.Read(ref calls));
        floor.Clear();
        await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // The floor goes Live while it runs: it stops, waits for the conversation to be quiet, then runs again.
        floor.Words();
        await Task.Delay(HelperJobs.IdleCheck * 3);
        Assert.False(running.IsCompleted);
        Assert.Equal(1, Volatile.Read(ref calls));
        floor.Clear();
        var result = await running.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("facts", result.Answer);
        Assert.False(result.Pooled);
        Assert.Equal(2, Volatile.Read(ref calls));
        reply.End();
    }

    [Fact]
    public void The_thinking_pool_page_says_which_members_wait_while_you_talk()
    {
        var pool = new Martlet.Core.Settings.ThinkingPoolSettings();
        Assert.Equal("", ThinkingPoolPageModel.LiveFloorLine(pool with { UseConversationModelWhenEmpty = false }, 0, []));
        Assert.StartsWith("While you talk with Martlet, thinking longer and research on the conversation model wait",
            ThinkingPoolPageModel.LiveFloorLine(pool, 0, []));
        Assert.Equal("No member shares the conversation's computer, so pool work never waits while you talk with Martlet.",
            ThinkingPoolPageModel.LiveFloorLine(pool, 2, []));
        Assert.StartsWith("While you talk with Martlet, this PC starts no new pool work, because it shares the conversation's computer.",
            ThinkingPoolPageModel.LiveFloorLine(pool, 2, ["this PC"]));
        Assert.StartsWith("While you talk with Martlet, this PC and diva start no new pool work, because they share",
            ThinkingPoolPageModel.LiveFloorLine(pool, 3, ["this PC", "diva"]));
    }

    [Fact]
    public void The_machine_list_is_the_pool_and_says_it_is_off_with_no_machine_on()
    {
        var local = new Martlet.Core.Settings.DeepThinkingSettings
        {
            Place = Martlet.Core.Settings.DeepThinkingPlace.Endpoint, Origin = MainWindow.LocalOllamaBaseUrl, ModelId = "gemma4:e4b"
        };
        var empty = new Martlet.Core.Settings.ThinkingPoolSettings();
        Assert.Equal("The Thinking pool is off: no machine is on, so thinking longer and research use the conversation model meanwhile; " +
            "add a machine below.", ThinkingPoolPageModel.PoolSummary(empty, 0));
        var off = empty.Add(local).TurnOff(local.Key);
        Assert.Equal("The Thinking pool is off: no machine is on and 1 machine is turned off, and the conversation model isn't used either, " +
            "so Martlet doesn't think in the background; tick On to use one again.",
            ThinkingPoolPageModel.PoolSummary(off with { UseConversationModelWhenEmpty = false }, 0));
        Assert.Equal("1 machine is on, with 2 usable slots in all.", ThinkingPoolPageModel.PoolSummary(off.TurnOn(local.Key), 2));
    }

    [Fact]
    public void The_busy_pool_line_says_the_choices_and_counts()
    {
        var pool = new Martlet.Core.Settings.ThinkingPoolSettings();
        Assert.Equal("More important requests may stop less important ones, and a request stopped 3 times becomes more important; " +
            "a failed request is tried once more.", ThinkingPoolPageModel.PriorityLine(pool, null));
        Assert.Equal("Requests wait for a free slot and never stop each other; a failed request isn't tried again.",
            ThinkingPoolPageModel.PriorityLine(pool with { PreemptLowerPriority = false, RetriesOnFailure = 0 }, null));
        var status = new Martlet.Conversation.ThinkingPoolStatus([], 2, 1, false, new Dictionary<string, int>(), new Dictionary<string, int>(), [])
        {
            StoppedForPriority = 4, Raised = 1, Retried = 2
        };
        Assert.Equal("More important requests may stop less important ones, and a request stopped once becomes more important; " +
            "a failed request is tried up to 5 more times (since Martlet started: 4 stopped, 1 made more important, 2 tried again).",
            ThinkingPoolPageModel.PriorityLine(pool with { RaisePriorityAfterStops = 1, RetriesOnFailure = 5 }, status));
    }

    [Fact]
    public void A_hosts_refusal_for_a_live_turn_is_preempted_not_busy_or_a_failure()
    {
        var held = new Martlet.HostClient.HostConnectionException("job.busy", "busy") { Detail = "live" };
        Assert.True(held.HeldForLive);
        Assert.Equal(Martlet.Core.Cluster.WorkRefusal.Preempted, WorkSharingRoster.Classify(held));
        Assert.Equal(Martlet.Core.Cluster.WorkRefusal.Preempted,
            WorkSharingRoster.Classify(new Martlet.HostClient.HostConnectionException("job.preempted", "stopped")));
        Assert.Equal(Martlet.Core.Cluster.WorkRefusal.Busy,
            WorkSharingRoster.Classify(new Martlet.HostClient.HostConnectionException("job.busy", "busy")));
    }
}