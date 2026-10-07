using System.Runtime.CompilerServices;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Providers;
using Martlet.Providers.Tests;

namespace Martlet.Conversation.Tests;

// NOT AI: controlled host clients stand in for the conversation's Thinking model and a Thinking pool member.
public sealed class ThinkingBackupTests
{
    private static readonly TextModelSelection Model = new(SelfHostSetup.GatewayOllamaAlias, "qwen3-8b");
    private static readonly HostTextTarget Conversation = new("https://192.168.1.20:9443", "conversation-host",
        "sha256:" + new string('a', 64), "desktop-test", Guid.NewGuid());
    private static readonly HostTextTarget Member = new("https://192.168.1.30:9443", "member-host",
        "sha256:" + new string('b', 64), "desktop-test", Guid.NewGuid());
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(900);

    // Answers once its gate opens (at once without one); notes a stop that came before it answered.
    private sealed class GatedHost(string words, Task? gate = null) : IHostTextClient
    {
        private int calls, stopped;
        internal int Calls => Volatile.Read(ref calls);
        internal bool Stopped => Volatile.Read(ref stopped) == 1;

        public async IAsyncEnumerable<string> StreamAsync(HostTextTarget target, TextModelSelection model, BoundedTextInput input,
            TextGenerationLimits limits, CorrelationIds ids, long epoch, DateTimeOffset deadline, GenerationSettings? generation,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            try
            {
                if (gate is not null) await gate.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Volatile.Write(ref stopped, 1);
                throw;
            }
            await Task.Yield();
            yield return words;
        }
    }

    private sealed class Lease : IAsyncDisposable
    {
        private int disposed;
        internal bool Disposed => Volatile.Read(ref disposed) == 1;
        public ValueTask DisposeAsync()
        {
            Volatile.Write(ref disposed, 1);
            return ValueTask.CompletedTask;
        }
    }

    // The desktop's stand-in: the member's own runtime opens the same request on its host (ConversationRuntime.OpenTextAsync).
    private sealed class FixtureBackup(ConversationRuntime members, FixturePermissions permissions, bool none = false, bool paid = false)
        : IThinkingBackup
    {
        private readonly List<bool> asked = [];
        private readonly List<ThinkingBackupResult> results = [];
        internal Lease Lease { get; } = new();
        internal IReadOnlyList<bool> Asked { get { lock (asked) return [.. asked]; } }
        internal IReadOnlyList<ThinkingBackupResult> Results { get { lock (results) return [.. results]; } }
        public TimeSpan Delay => ThinkingBackupTests.Delay;

        public async Task<ThinkingBackupStream?> OpenAsync(ConversationRequest reply, BoundedTextInput input, CorrelationIds ids, long epoch,
            bool held, CancellationToken token)
        {
            lock (asked) asked.Add(held);
            // A paid member is never asked for a reply started early that isn't taken yet.
            if (none || paid && held) return null;
            var request = new ConversationRequest(input, Model, reply.TextLimits, reply.Limits, host: Member);
            return new("member-host (qwen3-8b)", await members.OpenTextAsync(request, permissions, ids, epoch, token), Lease);
        }

        public void Ended(ThinkingBackupResult result)
        {
            lock (results) results.Add(result);
        }
    }

    private static FixturePermissions Permissions(RuntimeClock clock, HostTextTarget target) => new(clock)
    {
        Text = (action, _) =>
        {
            var until = clock.GetUtcNow().AddSeconds(30);
            return ValueTask.FromResult<AuthorizedTextOperation?>(new(new(new(new Uri(target.Origin), ProviderRole.Llm, Model.UpstreamModelId),
                action.Model, action.Context.Ids, action.Context.Epoch, action.Limits, until, true, true), new(action.Budget, until)));
        }
    };

    private static ConversationRuntime Runtime(RuntimeClock clock, IHostTextClient host) => ConversationRuntime.ForFixture(
        OpenAiTextGenerationAdapter.CreateForFixture(new TextRecordingHandler(), new FixtureCredentials(), clock), null, null, new(), clock,
        hostText: host);

    private static ConversationRequest Request(IThinkingBackup backup) =>
        new(new BoundedTextInput("Hi", "Be brief."), Model, new(), new(), host: Conversation) { Backup = backup };

    [Fact]
    public async Task A_slow_reply_asks_the_backup_after_the_delay_and_the_first_words_answer_while_the_other_stream_stops()
    {
        var clock = new RuntimeClock();
        var conversation = new GatedHost("From the conversation's model.", new TaskCompletionSource().Task);
        var member = new GatedHost("From the member.");
        await using var runtime = Runtime(clock, conversation);
        await using var members = Runtime(clock, member);
        var backup = new FixtureBackup(members, Permissions(clock, Member));

        var turn = runtime.Start(Request(backup), Permissions(clock, Conversation));
        var snapshot = await Harness.Finish(turn, clock);

        Assert.Equal(ConversationState.Completed, snapshot.State);
        Assert.Equal("From the member.", turn.Content.Text);
        var result = Assert.Single(backup.Results);
        Assert.Equal(ThinkingBackupOutcome.Won, result.Outcome);
        Assert.Equal(result, snapshot.Backup);
        Assert.Equal("member-host (qwen3-8b)", result.Member);
        // Asked once the delay passed after the request started, never sooner.
        Assert.True(result.AskedAfter >= snapshot.Timings!.TextRequestAfter + Delay, $"{result.AskedAfter} {snapshot.Timings.TextRequestAfter}");
        Assert.Equal(new[] { false }, backup.Asked);
        // The conversation's own stream was stopped at once, and the member's slot let go once its stream was read.
        await Harness.Until(() => conversation.Stopped && backup.Lease.Disposed, clock);
        Assert.Equal(1, conversation.Calls);
        Assert.Equal(1, member.Calls);
    }

    [Fact]
    public async Task A_fast_reply_never_asks_the_backup()
    {
        var clock = new RuntimeClock();
        var conversation = new GatedHost("Quick answer.");
        var member = new GatedHost("Never asked.");
        await using var runtime = Runtime(clock, conversation);
        await using var members = Runtime(clock, member);
        var backup = new FixtureBackup(members, Permissions(clock, Member));

        var turn = runtime.Start(Request(backup), Permissions(clock, Conversation));
        var snapshot = await Harness.Finish(turn, clock);

        Assert.Equal("Quick answer.", turn.Content.Text);
        Assert.Equal(ThinkingBackupOutcome.NotNeeded, Assert.Single(backup.Results).Outcome);
        Assert.Empty(backup.Asked);
        Assert.Equal(0, member.Calls);
        Assert.Null(snapshot.Backup!.AskedAfter);
    }

    [Fact]
    public async Task The_conversations_model_starting_first_after_the_backup_was_asked_stops_the_backup()
    {
        var clock = new RuntimeClock();
        var late = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var conversation = new GatedHost("The conversation's model, a little late.", late.Task);
        var member = new GatedHost("Too slow.", new TaskCompletionSource().Task);
        await using var runtime = Runtime(clock, conversation);
        await using var members = Runtime(clock, member);
        var backup = new FixtureBackup(members, Permissions(clock, Member));

        var turn = runtime.Start(Request(backup), Permissions(clock, Conversation));
        await Harness.Until(() => member.Calls == 1, clock);
        late.SetResult();
        var snapshot = await Harness.Finish(turn, clock);

        Assert.Equal("The conversation's model, a little late.", turn.Content.Text);
        var result = Assert.Single(backup.Results);
        Assert.Equal(ThinkingBackupOutcome.Lost, result.Outcome);
        Assert.NotNull(result.AskedAfter);
        Assert.True(result.FirstWordsAfter > result.AskedAfter && result.FirstWordsAfter <= snapshot.FirstTextAfter);
        await Harness.Until(() => member.Stopped && backup.Lease.Disposed, clock);
    }

    [Fact]
    public async Task With_no_member_to_ask_the_conversations_model_answers_alone()
    {
        var clock = new RuntimeClock();
        var late = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var conversation = new GatedHost("Slow, but alone.", late.Task);
        await using var runtime = Runtime(clock, conversation);
        await using var members = Runtime(clock, new GatedHost("Never asked."));
        var backup = new FixtureBackup(members, Permissions(clock, Member), none: true);

        var turn = runtime.Start(Request(backup), Permissions(clock, Conversation));
        await Harness.Until(() => backup.Asked.Count == 1, clock);
        late.SetResult();
        var snapshot = await Harness.Finish(turn, clock);

        Assert.Equal("Slow, but alone.", turn.Content.Text);
        Assert.Equal(ThinkingBackupOutcome.NoMember, Assert.Single(backup.Results).Outcome);
        Assert.Equal(ThinkingBackupOutcome.NoMember, snapshot.Backup!.Outcome);
    }

    [Fact]
    public async Task A_member_that_fails_is_let_go_at_once_and_the_conversations_model_answers()
    {
        var clock = new RuntimeClock();
        var late = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var conversation = new GatedHost("The conversation's model, alone.", late.Task);
        await using var runtime = Runtime(clock, conversation);
        await using var members = Runtime(clock, new FailingHost());
        var backup = new FixtureBackup(members, Permissions(clock, Member));

        var turn = runtime.Start(Request(backup), Permissions(clock, Conversation));
        // The member failed: its slot and computer are let go while the conversation's model still works.
        await Harness.Until(() => backup.Lease.Disposed, clock);
        Assert.False(turn.Completion.IsCompleted);
        late.SetResult();
        var snapshot = await Harness.Finish(turn, clock);

        Assert.Equal("The conversation's model, alone.", turn.Content.Text);
        Assert.Equal(ThinkingBackupOutcome.Failed, snapshot.Backup!.Outcome);
        Assert.Equal("it failed", snapshot.Backup.Why);
    }

    private sealed class FailingHost : IHostTextClient
    {
        public async IAsyncEnumerable<string> StreamAsync(HostTextTarget target, TextModelSelection model, BoundedTextInput input,
            TextGenerationLimits limits, CorrelationIds ids, long epoch, DateTimeOffset deadline, GenerationSettings? generation,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            throw new HostTextException(ProviderFailureCode.RequestRejected);
#pragma warning disable CS0162 // An async iterator needs a yield.
            yield break;
#pragma warning restore CS0162
        }
    }

    [Fact]
    public async Task A_reply_started_early_asks_a_paid_member_only_once_it_is_taken()
    {
        var clock = new RuntimeClock();
        var conversation = new GatedHost("Never in time.", new TaskCompletionSource().Task);
        var member = new GatedHost("From the paid member.");
        await using var runtime = Runtime(clock, conversation);
        await using var members = Runtime(clock, member);
        var backup = new FixtureBackup(members, Permissions(clock, Member), paid: true);

        var turn = runtime.StartEarly(Request(backup), Permissions(clock, Conversation), prepareVoice: true);
        await Harness.Until(() => backup.Asked.Count == 1, clock);
        // Held: the paid member wasn't asked, and nothing is asked again until the reply is taken.
        await Task.Delay(50);
        Assert.Equal(new[] { true }, backup.Asked);
        Assert.Equal(0, member.Calls);
        Assert.True(turn.Release());
        var snapshot = await Harness.Finish(turn, clock);

        Assert.Equal(new[] { true, false }, backup.Asked);
        Assert.Equal("From the paid member.", turn.Content.Text);
        Assert.Equal(ThinkingBackupOutcome.Won, snapshot.Backup!.Outcome);
        Assert.True(snapshot.Backup.AskedAfter >= snapshot.Timings!.ReleasedAfter);
    }

    [Fact]
    public async Task A_reply_started_early_and_let_go_stops_its_backup_too()
    {
        var clock = new RuntimeClock();
        var conversation = new GatedHost("Never in time.", new TaskCompletionSource().Task);
        var member = new GatedHost("Never in time either.", new TaskCompletionSource().Task);
        await using var runtime = Runtime(clock, conversation);
        await using var members = Runtime(clock, member);
        var backup = new FixtureBackup(members, Permissions(clock, Member));

        var turn = runtime.StartEarly(Request(backup), Permissions(clock, Conversation), prepareVoice: true);
        await Harness.Until(() => member.Calls == 1, clock);
        Assert.Equal(new[] { true }, backup.Asked);
        var stopping = turn.StopAsync();
        await Harness.Until(() => stopping.IsCompleted, clock);

        Assert.Equal(ConversationState.Canceled, (await stopping).State);
        Assert.Empty(backup.Results);
        await Harness.Until(() => conversation.Stopped && member.Stopped && backup.Lease.Disposed, clock);
        Assert.Equal("", turn.Content.Text);
    }

    [Fact]
    public void The_automatic_delay_is_the_95th_percentile_of_recent_first_words_and_never_under_900_ms()
    {
        var times = new FirstWordTimes();
        Assert.Equal(FirstWordTimes.Starting, times.Delay);
        times.Add(TimeSpan.FromMilliseconds(300));
        times.Add(TimeSpan.FromMilliseconds(400));
        Assert.Equal(FirstWordTimes.Starting, times.Delay);
        times.Add(TimeSpan.FromMilliseconds(500));
        // Fast replies: never under the minimum.
        Assert.Equal(FirstWordTimes.Minimum, times.Delay);

        times.Clear();
        for (var i = 0; i < 19; i++) times.Add(TimeSpan.FromMilliseconds(1_000 + i * 10));
        times.Add(TimeSpan.FromMilliseconds(5_000));
        // 20 replies: the 19th slowest is the 95th percentile, so one outlier doesn't stretch the wait.
        Assert.Equal(TimeSpan.FromMilliseconds(1_180), times.Delay);
        // Only the last 20 count.
        times.Add(TimeSpan.FromMilliseconds(5_000));
        Assert.Equal(20, times.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(5_000), times.Delay);
        Assert.Equal(TimeSpan.FromMilliseconds(1_200), times.DelayFor(1_200));
    }

    private static readonly DeepThinkingSettings Lan = new()
    {
        Place = DeepThinkingPlace.Endpoint, Origin = "https://10.77.0.20:8443/v1", ModelId = "qwen3:8b"
    };
    private static readonly DeepThinkingSettings Cloud = new()
    {
        Place = DeepThinkingPlace.Endpoint, Origin = "https://openrouter.ai/api/v1", ModelId = "x-ai/grok-4.3"
    };
    private static readonly DeepThinkingSettings Paired = new()
    {
        Place = DeepThinkingPlace.Host, HostId = "diva", HostOrigin = "https://10.77.0.30:9443", HostSpkiFingerprint = "sha256:" + new string('0', 64),
        HostDeviceId = "desktop-test", HostCredentialId = Guid.Parse("00000000-0000-0000-0000-000000000001"),
        HostRouteId = SelfHostSetup.DeepThinkingRouteId, ModelId = "qwen3-8b"
    };

    private static ThinkingBackupMembers.Choice Choose(ThinkingPoolSettings pool, BoundedTextInput? input = null, bool held = false,
        LiveResources? live = null, Func<DeepThinkingSettings, string?>? cannotTake = null)
    {
        var plan = pool.Plan([]);
        return ThinkingBackupMembers.Choose(pool, plan, ThinkLonger.Places(plan), live ?? LiveResources.None, input ?? new("Hi"), held, cannotTake);
    }

    [Fact]
    public void Only_a_member_ticked_to_answer_for_the_conversation_is_asked_and_a_cloud_one_never_by_default()
    {
        var pool = new ThinkingPoolSettings { Members = [Lan, Cloud, Paired] };
        // Off by default, and no member answers for the conversation by default.
        Assert.False(pool.BackupThinking);
        Assert.Empty(pool.AnswersForConversation);
        Assert.Null(Choose(pool with { AnswersForConversation = [Lan.Key] }).Spot);
        var on = pool with { BackupThinking = true };
        Assert.Equal("no member may answer for the conversation", Choose(on).Why);
        // The cloud member isn't ticked: the member on the home network answers.
        Assert.Equal(Lan.Key, Choose(on with { AnswersForConversation = [Lan.Key] }).Spot?.Key);
        Assert.True(ThinkingBackupMembers.Paid(Cloud));
        Assert.False(ThinkingBackupMembers.Paid(Lan));
        Assert.False(ThinkingBackupMembers.Paid(Paired));
        // Ticked, a cloud member answers a reply that is taken, never one started early and still held.
        var cloud = on with { AnswersForConversation = [Cloud.Key] };
        Assert.Equal(Cloud.Key, Choose(cloud).Spot?.Key);
        Assert.Null(Choose(cloud, held: true).Spot);
        Assert.Contains("paid cloud provider", Choose(cloud, held: true).Why);
    }

    [Fact]
    public void A_member_that_shares_the_conversations_computer_or_cant_take_the_request_as_it_is_is_passed_over()
    {
        var pool = new ThinkingPoolSettings { Members = [Lan, Cloud, Paired], BackupThinking = true };
        var both = pool with { AnswersForConversation = [Lan.Key, Cloud.Key] };
        // The conversation runs on the member's computer: the next one answers.
        Assert.Equal(Cloud.Key, Choose(both, live: new([new LiveResource("thinking", "lan:10.77.0.20", [])])).Spot?.Key);
        // A paired computer's gateway takes no tools, and a text-only member no picture.
        var tools = new BoundedTextInput("Hi", tools: [new TextToolDefinition("think_longer", "Think it over.", """{"type":"object"}""")]);
        Assert.Contains("can't use tools", Choose(pool with { AnswersForConversation = [Paired.Key] }, tools).Why);
        Assert.Equal(Lan.Key, Choose(pool with { AnswersForConversation = [Paired.Key, Lan.Key] }, tools).Spot?.Key);
        var picture = new BoundedTextInput("Hi", image: new BoundedImage([0xFF, 0xD8, 0xFF, .. new byte[32]], ImageMediaType.Jpeg, 4, 4));
        Assert.Contains("doesn't see pictures", Choose(pool with { AnswersForConversation = [Lan.Key] }, picture).Why);
        // A request too long for the member.
        Assert.Contains("this long", Choose(pool with { AnswersForConversation = [Lan.Key] }, cannotTake: _ => "can't take a request this long").Why);
    }

    [Fact]
    public void The_members_that_may_answer_are_saved_with_the_pool_and_go_with_a_member_that_leaves()
    {
        var pool = new ThinkingPoolSettings { Members = [Lan, Cloud] }.WithAnswers(Lan.Key, true).WithAnswers(Cloud.Key, true);
        Assert.Equal(new[] { Lan.Key, Cloud.Key }, pool.AnswersForConversation);
        Assert.Equal(new[] { Cloud.Key }, pool.WithAnswers(Lan.Key, false).AnswersForConversation);
        Assert.Equal(new[] { Cloud.Key }, pool.Remove(Lan.Key).AnswersForConversation);
        var directory = Path.Combine(Path.GetTempPath(), "Martlet.Backup." + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.True((pool with { BackupThinking = true, BackupDelayMs = 1_200 }).Save(directory));
            var loaded = ThinkingPoolSettings.Load(directory);
            Assert.True(loaded.BackupThinking);
            Assert.Equal(1_200, loaded.BackupDelayMs);
            Assert.Equal(new[] { Lan.Key, Cloud.Key }, loaded.AnswersForConversation);
            Assert.Throws<ContractException>(() => (pool with { BackupDelayMs = 1_234 }).Validate());
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
