using System.Collections.Concurrent;
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
            Assert.Contains("\"kind\":\"action_naming\",\"route\":\"fallback\"", status);
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
        var adapter = new ThinkingPoolHelpers(() => new ThinkingPool(board));

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

    private sealed class FixturePool : IHelperJobPool
    {
        internal bool Text { get; init; }
        internal bool Vision { get; init; }
        internal bool Free { get; init; } = true;
        internal string Answer { get; init; } = "pool answer";
        internal ConcurrentQueue<HelperJob> Jobs { get; } = new();

        public bool Has(HelperCapability capability) => capability == HelperCapability.Vision ? Vision : Text;

        public Task<HelperPoolAnswer?> TryRunAsync(HelperJob job, CancellationToken token)
        {
            if (!Free || !Has(job.Capability)) return Task.FromResult<HelperPoolAnswer?>(null);
            Jobs.Enqueue(job);
            return Task.FromResult<HelperPoolAnswer?>(new(job.Capability == HelperCapability.Vision ? "vision-member" : "text-member", Answer, null));
        }
    }
}
