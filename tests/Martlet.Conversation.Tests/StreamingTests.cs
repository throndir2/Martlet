using System.Text;
using Martlet.Audio;
using Martlet.Audio.Tests;
using Martlet.Core.Audio;
using Martlet.Core.Contracts;
using Martlet.Providers;
using Martlet.Providers.Tests;

namespace Martlet.Conversation.Tests;

public sealed class StreamingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Audio_starts_before_llm_terminal_and_later_failure_never_speaks_unfinished_text(bool truncate)
    {
        await using var h = new Harness();
        string answer = truncate ? "Early sentence. Unfinished" : "Early sentence. ";
        var trace = TextFixtures.Trace(answer);
        string prefix = string.Concat(trace.Take(5));
        string full = truncate ? prefix : string.Concat(trace);
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var body = new FragmentedTextBody(Encoding.UTF8.GetBytes(full), 1);
        body.BeforeRead = async token =>
        {
            if (body.BytesRead >= Encoding.UTF8.GetByteCount(prefix))
            {
                blocked.TrySetResult();
                await release.Task.WaitAsync(token);
            }
        };
        h.Llm.Respond = (_, _) => Task.FromResult(TextRecordingHandler.Sse(body));
        var turn = h.Start();
        try
        {
            await blocked.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await h.Device.EnteredWrite.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await Harness.Until(() => h.Device.Disposals == 1);
            Assert.False(turn.Snapshot.TextComplete);
            Assert.False(turn.Completion.IsCompleted);
            Assert.Equal(1, h.Tts.Calls);
            Assert.Equal("Early sentence.", Assert.Single(h.Permissions.SpeechActions).Input.Text);
        }
        finally { release.TrySetResult(); }
        var result = await Harness.Finish(turn);
        Assert.Equal(truncate ? ConversationState.Partial : ConversationState.Completed, result.State);
        Assert.Equal(answer, turn.Content.Text);
        Assert.Equal(!truncate, result.TextComplete);
        Assert.True(result.MayHavePlayed);
        Assert.Equal(1, h.Llm.Calls);
        Assert.Equal(1, h.Tts.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Speech_authorization_time_is_part_of_original_budget_even_with_utc_rollback(bool rollback)
    {
        await using var h = new Harness();
        h.Clock.DeferCallbacks = true;
        h.Permissions.Speech = (a, _) =>
        {
            var permission = h.Permissions.Allow(a);
            h.Clock.Advance(TimeSpan.FromMilliseconds(800));
            if (rollback) h.Clock.ShiftUtc(TimeSpan.FromHours(-1));
            return ValueTask.FromResult<AuthorizedSpeechOperation?>(permission);
        };
        h.Credentials.Resolve = (binding, _) =>
        {
            if (binding.Role == ProviderRole.Tts) h.Clock.Advance(TimeSpan.FromMilliseconds(300));
            return ValueTask.FromResult<BoundProviderCredential?>(new(binding, ProviderFixtures.Secret));
        };
        var turn = h.Start(Harness.Request(speechLimits: Harness.SpeechLimits with { MaxRequestTime = TimeSpan.FromSeconds(1) }));
        var result = await Harness.Finish(turn);
        Assert.Equal(ConversationState.Completed, result.State);
        Assert.Equal(ConversationFailure.DeadlineExceeded, result.SpeechFailure);
        Assert.Equal(1, h.Llm.Calls);
        Assert.Equal(0, h.Tts.Calls);
        Assert.Equal(0, h.Device.Opens);
    }

    [Fact]
    public async Task Next_sentence_is_synthesized_while_the_previous_one_plays_and_latency_is_reported()
    {
        await using var h = new Harness(new ControlledDevice { AutoConsume = false });
        h.Answer("First sentence. ", "Second sentence.");
        var turn = h.Start();
        // The first sentence never finishes playing, yet the second is already synthesized and waiting.
        await Harness.Until(() => h.Tts.Calls == 2, h.Clock);
        Assert.Equal(0, h.Device.Disposals);
        Assert.Equal(1, h.Device.Opens);
        Assert.Equal(["First sentence.", "Second sentence."], h.Permissions.SpeechActions.Select(a => a.Input.Text));
        var snapshot = turn.Snapshot;
        Assert.NotNull(snapshot.FirstTextAfter);
        Assert.Equal(2, snapshot.CommittedSegments);
        h.Device.AutoConsume = true;
        var result = await Harness.Finish(turn, h.Clock);
        Assert.Equal(ConversationState.Completed, result.State);
        Assert.Equal(2, h.Device.Opens);
        Assert.NotNull(result.FirstAudioAfter);
        Assert.True(result.FirstAudioAfter >= result.FirstTextAfter);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Playback_mapping_rejects_stale_or_foreign_provider_frames(bool stale)
    {
        var context = ProviderFixtures.Context();
        var frame = new PcmFrame(stale ? context.Ids : context.Ids with { RequestId = Guid.NewGuid() },
            stale ? context.Epoch - 1 : context.Epoch, 0, 0, OpenAiSpeechSynthesisCatalog.PcmFormat, new byte[960]);
        Assert.Throws<ConversationException>(() => PlaybackFrameMapping.Map(frame, context.Ids, context.Epoch, 100));
    }

    [Fact]
    public async Task Mapping_preserves_pcm_sequences_and_offsets_and_actual_sink_deduplicates_and_rejects_old_runs()
    {
        var device = new ControlledDevice();
        await using var sink = new PcmPlaybackSink(device);
        var context = ProviderFixtures.Context();
        var format = OpenAiSpeechSynthesisCatalog.PcmFormat;
        var first = sink.Start(new(context.Ids, 100, format, new(OutputPolicy.DefaultAtStart), DateTimeOffset.UtcNow.AddSeconds(30)));
        var frame = new PcmFrame(context.Ids, context.Epoch, 0, 0, format, SpeechFixtures.Audio(480));
        var mapped = PlaybackFrameMapping.Map(frame, context.Ids, context.Epoch, 100);
        Assert.Equal(0, mapped.Sequence);
        Assert.Equal(0, mapped.SampleOffset);
        Assert.Equal(FrameAcceptance.Accepted, first.Submit(mapped));
        Assert.Equal(FrameAcceptance.DuplicateDiscarded, first.Submit(mapped));
        var final = PlaybackFrameMapping.Map(new(context.Ids, context.Epoch, 1, 480, format, SpeechFixtures.Audio(41)),
            context.Ids, context.Epoch, 100);
        Assert.Equal(1, final.Sequence);
        Assert.Equal(480, final.SampleOffset);
        Assert.Equal(FrameAcceptance.Accepted, first.Submit(final));
        Assert.True(first.CompleteInput(521));
        Assert.Equal(PlaybackState.Completed, (await first.Completion.WaitAsync(TimeSpan.FromSeconds(20))).State);
        Assert.Equal(SpeechFixtures.Audio(480).Concat(SpeechFixtures.Audio(41)), device.Bytes);
        var second = sink.Start(new(context.Ids with { RequestId = Guid.NewGuid() }, 101, format,
            new(OutputPolicy.DefaultAtStart), DateTimeOffset.UtcNow.AddSeconds(30)));
        Assert.Equal(FrameAcceptance.StaleDiscarded, first.Submit(final));
        await first.StopAsync();
        Assert.False(second.Completion.IsCompleted);
        await second.StopAsync();
    }

    [Fact]
    public async Task Blank_input_is_rejected_locally_and_empty_provider_output_is_not_no_speech_or_completed()
    {
        await using var h = new Harness();
        Assert.Throws<ContractException>(() => new BoundedTextInput(" "));
        Assert.Equal(0, h.Credentials.Calls);
        h.Answer("   ");
        var result = await Harness.Finish(h.Start());
        Assert.NotEqual(ConversationState.Completed, result.State);
        Assert.NotEqual(ConversationState.Refused, result.State);
        Assert.Equal(0, h.Tts.Calls);
        Assert.Equal(0, h.Device.Opens);
    }
}
