using System.Text.Json;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Providers;
using Martlet.Providers.Tests;

namespace Martlet.Conversation.Tests;

// If Thinking fails, timed: ThinkingFallbackTimer's wait and skip rules, and a reply that gives way to the fallback after a short
// first-token wait or asks it first.
public sealed class ThinkingFallbackTimerTests
{
    private const string Key = "https://integrate.api.nvidia.com nvidia/nemotron-nano";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-07T20:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    private static ConversationSnapshot Answered(int ms) => Reply() with
    {
        State = ConversationState.Completed, FirstTextAfter = TimeSpan.FromMilliseconds(100 + ms),
        Timings = new(TextRequestAfter: TimeSpan.FromMilliseconds(100))
    };

    private static ConversationSnapshot FellBack(string why = "FirstDeltaTimeout") => Reply() with
    {
        State = ConversationState.Completed, FellBackAfter = why, FirstTextAfter = TimeSpan.FromSeconds(4)
    };

    private static ConversationSnapshot Reply() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, 1,
        ConversationState.Completed, ConversationFailure.None, null, null, 40, true, 0, 1, 2, 0, 80, 48_000, null, null, null,
        48_000, 48_000, 48_000, true, true, false, 0, null, null, false);

    [Fact]
    public void The_wait_is_three_times_the_usual_first_token_within_its_bounds()
    {
        Assert.Equal(ThinkingFallbackTimer.Starting, ThinkingFallbackTimer.WaitFor([800, 900]));
        Assert.Equal(TimeSpan.FromMilliseconds(2_775), ThinkingFallbackTimer.WaitFor([800, 900, 950, 20_000]));
        Assert.Equal(ThinkingFallbackTimer.Minimum, ThinkingFallbackTimer.WaitFor([300, 400, 500]));
        Assert.Equal(ThinkingFallbackTimer.Maximum, ThinkingFallbackTimer.WaitFor([5_000, 6_000, 7_000]));
    }

    [Fact]
    public void Measured_replies_and_model_speed_set_the_wait_and_a_new_model_starts_again()
    {
        var timer = new ThinkingFallbackTimer();
        Assert.Equal(ThinkingFallbackTimer.Starting, timer.Plan(Key, Now).Wait);
        timer.Seed(Key, [1_000, 1_100]);
        timer.Noted(Key, Answered(1_200), Now);
        var plan = timer.Plan(Key, Now);
        Assert.Equal(TimeSpan.FromMilliseconds(3_300), plan.Wait);
        Assert.False(plan.FallbackFirst);
        // A reply the fallback answered isn't the model's own time.
        timer.Noted(Key, FellBack(), Now);
        Assert.Equal(3, timer.Samples.Count);
        Assert.Equal(ThinkingFallbackTimer.Starting, timer.Plan("https://openrouter.ai other/model:free", Now).Wait);
        Assert.Empty(timer.Samples);
    }

    [Fact]
    public void Two_failures_in_a_row_ask_the_fallback_first_for_a_while_and_an_answer_ends_it()
    {
        var timer = new ThinkingFallbackTimer();
        timer.Noted(Key, FellBack(), Now);
        Assert.False(timer.Plan(Key, Now).FallbackFirst);
        timer.Noted(Key, Answered(900), Now);
        timer.Noted(Key, FellBack("RateLimited"), Now);
        Assert.False(timer.Plan(Key, Now).FallbackFirst);
        timer.Noted(Key, FellBack(), Now);
        Assert.True(timer.Plan(Key, Now).FallbackFirst);
        // A reply that asked the fallback first doesn't count as the model failing again.
        timer.Noted(Key, FellBack(ThinkingFallbackTimer.SkippedReason), Now.AddSeconds(30));
        Assert.Equal(2, timer.Failures);
        Assert.True(timer.Plan(Key, Now.AddSeconds(119)).FallbackFirst);
        Assert.False(timer.Plan(Key, Now + ThinkingFallbackTimer.SkipFor).FallbackFirst);
        // Tried again and failed again: skipped again. Then it answers: asked first again.
        timer.Noted(Key, FellBack(), Now.AddMinutes(3));
        Assert.True(timer.Plan(Key, Now.AddMinutes(3)).FallbackFirst);
        timer.Noted(Key, Answered(900), Now.AddMinutes(4));
        Assert.False(timer.Plan(Key, Now.AddMinutes(4)).FallbackFirst);
        Assert.Equal(0, timer.Failures);
    }

    private const string FallbackBase = "https://fallback.test/v1";
    private const string FallbackModel = "org/fallback";

    private static string Chunk(string? text = null, string? finish = null, object? delta = null) =>
        "data: " + JsonSerializer.Serialize(new
        {
            id = "chat-fixture", @object = "chat.completion.chunk", model = FallbackModel,
            choices = new[] { new { index = 0, delta = delta ?? new { content = text }, finish_reason = finish } }
        }) + "\n\n";

    private static string FallbackTrace => Chunk(delta: new { role = "assistant", content = "" }) +
        Chunk("From the ") + Chunk("fallback.", "stop") + "data: [DONE]\n\n";

    private sealed class FallbackHarness : IAsyncDisposable
    {
        internal RuntimeClock Clock { get; } = new();
        internal TextRecordingHandler Llm { get; } = new();
        internal TextRecordingHandler Chat { get; } = new();
        internal FixturePermissions Permissions { get; }
        internal ConversationRuntime Runtime { get; }

        internal FallbackHarness()
        {
            Permissions = new(Clock)
            {
                Text = (action, _) =>
                {
                    if (action.Model.ModelAlias != ChatCompletionsSetup.FallbackAlias)
                        return ValueTask.FromResult<AuthorizedTextOperation?>(Permissions!.Allow(action));
                    var until = action.Context.Deadline;
                    return ValueTask.FromResult<AuthorizedTextOperation?>(new(new(new(new Uri(FallbackBase), ProviderRole.Llm, FallbackModel),
                        action.Model, action.Context.Ids, action.Context.Epoch, action.Limits, until, true, true), new(action.Budget, until)));
                }
            };
            Runtime = ConversationRuntime.ForFixture(OpenAiTextGenerationAdapter.CreateForFixture(Llm, new FixtureCredentials(), Clock),
                null, null, new(), Clock, chat: target => ChatCompletionsTextGenerationAdapter.CreateForFixture(target.BaseUrl, Chat, null, Clock));
        }

        internal static ConversationRequest Request(TimeSpan? after = null, bool first = false) => new(
            new BoundedTextInput("Hello there."), TextFixtures.Selection, new(), new(),
            fallback: new(new(FallbackBase, Keyless: true), new(ChatCompletionsSetup.FallbackAlias, FallbackModel)))
        {
            FallbackAfter = after, FallbackFirst = first
        };

        public async ValueTask DisposeAsync()
        {
            var disposal = Runtime.DisposeAsync().AsTask();
            await Harness.Until(() => disposal.IsCompleted, Clock);
            await disposal;
        }
    }

    private static async Task<HttpResponseMessage> Hang(CancellationToken token)
    {
        await Task.Delay(Timeout.Infinite, token);
        throw new OperationCanceledException(token);
    }

    [Fact]
    public async Task A_slow_model_gives_way_to_the_fallback_after_its_short_wait()
    {
        await using var harness = new FallbackHarness();
        harness.Llm.Respond = (_, token) => Hang(token);
        harness.Chat.Respond = (_, _) => Task.FromResult(TextRecordingHandler.Sse(FallbackTrace));
        var turn = harness.Runtime.Start(FallbackHarness.Request(TimeSpan.FromSeconds(3)), harness.Permissions);
        var snapshot = await Harness.Finish(turn, harness.Clock);

        Assert.Equal(ConversationState.Completed, snapshot.State);
        Assert.Equal("From the fallback.", turn.Content.Text);
        Assert.Contains(snapshot.FellBackAfter, new[] { nameof(ConversationFailure.DeadlineExceeded), nameof(ProviderFailureCode.FirstDeltaTimeout) });
        var actions = harness.Permissions.TextActions.ToArray();
        Assert.Equal(TimeSpan.FromSeconds(3), actions[0].Limits.FirstDeltaTimeout);
        // The fallback keeps the route's own limit.
        Assert.Equal(new TextGenerationLimits().FirstDeltaTimeout, actions[^1].Limits.FirstDeltaTimeout);
        Assert.InRange(snapshot.FirstTextAfter!.Value, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_reply_asks_the_fallback_first_and_the_model_only_when_the_fallback_fails()
    {
        await using (var harness = new FallbackHarness())
        {
            harness.Chat.Respond = (_, _) => Task.FromResult(TextRecordingHandler.Sse(FallbackTrace));
            var turn = harness.Runtime.Start(FallbackHarness.Request(TimeSpan.FromSeconds(3), first: true), harness.Permissions);
            var snapshot = await Harness.Finish(turn, harness.Clock);
            Assert.Equal(ConversationState.Completed, snapshot.State);
            Assert.Equal("From the fallback.", turn.Content.Text);
            Assert.Equal(ThinkingFallbackTimer.SkippedReason, snapshot.FellBackAfter);
            Assert.Equal(0, harness.Llm.Calls);
        }
        await using (var harness = new FallbackHarness())
        {
            harness.Chat.Respond = (_, _) => Task.FromResult(TextRecordingHandler.Sse("""{"error":{"message":"busy"}}""", 503));
            harness.Llm.Respond = (_, _) => Task.FromResult(TextRecordingHandler.Sse(Harness.Trace("From the ", "model.")));
            var turn = harness.Runtime.Start(FallbackHarness.Request(TimeSpan.FromSeconds(3), first: true), harness.Permissions);
            var snapshot = await Harness.Finish(turn, harness.Clock);
            Assert.Equal(ConversationState.Completed, snapshot.State);
            Assert.Equal("From the model.", turn.Content.Text);
            Assert.Null(snapshot.FellBackAfter);
            Assert.Equal(1, harness.Llm.Calls);
            Assert.Equal("the Thinking model, after the fallback failed", turn.SentRequests[^1].Why);
        }
    }
}
