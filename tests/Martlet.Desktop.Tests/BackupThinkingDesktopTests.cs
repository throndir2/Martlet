using Martlet.Conversation;
using Martlet.Conversation.Tests;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Providers;
using Martlet.Providers.Tests;

namespace Martlet.Desktop.Tests;

// NOT AI: the fixture's OpenAI handler stands in for the conversation's Thinking model and its Chat Completions handler for a
// Thinking pool member on the home network.
public sealed class BackupThinkingDesktopTests
{
    private static readonly DeepThinkingSettings Lan = new()
    {
        Place = DeepThinkingPlace.Endpoint, Origin = "https://10.77.0.20:8443/v1", ModelId = "qwen3:8b"
    };
    private static readonly DeepThinkingSettings Cloud = new()
    {
        Place = DeepThinkingPlace.Endpoint, Origin = "https://openrouter.ai/api/v1", ModelId = "x-ai/grok-4.3"
    };

    private static HttpResponseMessage ChatReply(string words) => TextRecordingHandler.Sse(
        "data: {\"id\":\"chat-fixture\",\"object\":\"chat.completion.chunk\",\"model\":\"qwen3:8b\",\"choices\":[{\"index\":0,\"delta\":" +
        "{\"role\":\"assistant\",\"content\":\"" + words + "\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n");

    [Fact]
    public async Task A_slow_reply_is_answered_by_the_member_that_may_answer_for_the_conversation_and_the_slow_request_is_stopped()
    {
        await using var fixture = await LiveFixture.Create();
        var aborted = 0;
        fixture.Llm.Respond = async (_, token) =>
        {
            // The conversation's model is busy: its request waits until it is stopped.
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { Interlocked.Increment(ref aborted); throw; }
            return TextRecordingHandler.Sse(Harness.Trace("Too late."));
        };
        fixture.Chat.Respond = (_, _) => Task.FromResult(ChatReply("From the backup member."));
        fixture.Controller.PoolSettings = new ThinkingPoolSettings
        {
            Members = [Lan], BackupThinking = true, BackupDelayMs = 900, AnswersForConversation = [Lan.Key]
        };

        var operation = fixture.Start("Hello there.");
        await fixture.Finish(operation);

        Assert.Equal("runtime.Completed", operation.Status.Code);
        Assert.Equal("From the backup member.", operation.Turn!.Content.Text);
        var backup = operation.Turn.Snapshot.Backup!;
        Assert.Equal(ThinkingBackupOutcome.Won, backup.Outcome);
        Assert.Equal(Lan.Describe(), backup.Member);
        Assert.True(backup.AskedAfter >= TimeSpan.FromMilliseconds(900));
        Assert.Equal(1, fixture.Chat.Calls);
        await fixture.Advance(() => Volatile.Read(ref aborted) == 1);
        Assert.Equal(1, fixture.Controller.BackupHistory.Counts[ThinkingBackupOutcome.Won]);
        // The member was live work only while its stream was read.
        Assert.DoesNotContain(fixture.Controller.LiveFloorRules.Resources.Items, item => item.Job == "backup thinking");
        // The first words count toward the automatic delay.
        Assert.Equal(1, fixture.Controller.FirstWords.Count);
    }

    [Fact]
    public async Task Backup_thinking_asks_only_a_ticked_member_a_cloud_one_never_for_a_held_reply_and_holds_its_computer_while_it_streams()
    {
        await using var fixture = await LiveFixture.Create();
        var configured = fixture.Controller.Configuration!;
        var reply = new ConversationRequest(new BoundedTextInput("Hi"), TextFixtures.Selection, new(), new());
        CorrelationIds Ids() => new() { SessionId = Guid.NewGuid(), TurnId = Guid.NewGuid(), RequestId = Guid.NewGuid() };
        Task<(ThinkingBackupStream? Stream, string Why)> Open(bool held) =>
            fixture.Controller.OpenBackupAsync(configured, reply, reply.Input, Ids(), 1, held, CancellationToken.None);

        // Off by default; on, but nobody ticked.
        fixture.Controller.PoolSettings = new ThinkingPoolSettings { Members = [Lan, Cloud] };
        Assert.Equal("Backup Thinking is off", (await Open(false)).Why);
        fixture.Controller.PoolSettings = new ThinkingPoolSettings { Members = [Lan, Cloud], BackupThinking = true };
        Assert.Null((await Open(false)).Stream);

        // The member on the home network is ticked, the cloud one isn't: only the first is asked.
        fixture.Controller.PoolSettings = new ThinkingPoolSettings { Members = [Lan, Cloud], BackupThinking = true, AnswersForConversation = [Lan.Key] };
        var (stream, _) = await Open(false);
        Assert.Equal(Lan.Describe(), stream!.Name);
        // While its stream is read, its computer is live work for the floor: pool work there stops and waits.
        var held = Assert.Single(fixture.Controller.LiveFloorRules.Resources.Items, item => item.Job == "backup thinking");
        Assert.Equal("lan:10.77.0.20", held.Machine);
        await stream.Lease!.DisposeAsync();
        Assert.DoesNotContain(fixture.Controller.LiveFloorRules.Resources.Items, item => item.Job == "backup thinking");

        // A ticked cloud member answers a reply that is taken, never one started early and still held.
        fixture.Controller.PoolSettings = new ThinkingPoolSettings { Members = [Lan, Cloud], BackupThinking = true, AnswersForConversation = [Cloud.Key] };
        var (none, why) = await Open(true);
        Assert.Null(none);
        Assert.Contains("paid cloud provider", why);
        var (paid, _) = await Open(false);
        Assert.Equal(Cloud.Describe(), paid!.Name);
        await paid.Lease!.DisposeAsync();
        // Nothing was sent: a stream sends its request only once it is read.
        Assert.Equal(0, fixture.Chat.Calls);
    }

    [Fact]
    public void The_backup_thinking_line_says_whether_it_is_on_who_may_answer_and_the_wait()
    {
        Assert.StartsWith("Off.", LiveConversationController.BackupLine(new(), [], null, 0));
        var on = new ThinkingPoolSettings { BackupThinking = true };
        Assert.Contains("no machine is a backup for slow replies yet", LiveConversationController.BackupLine(on, [], null, 0));
        Assert.Contains("no words after 1200 ms, diva (qwen3-8b) may answer instead",
            LiveConversationController.BackupLine(on with { BackupDelayMs = 1_200 }, ["diva (qwen3-8b)"], null, 0));
        Assert.Contains("1180 ms (automatic, from 20 recent replies), diva (qwen3-8b) and ripley (qwen3-8b) may answer",
            LiveConversationController.BackupLine(on, ["diva (qwen3-8b)", "ripley (qwen3-8b)"], TimeSpan.FromMilliseconds(1_180), 20));
    }
}
