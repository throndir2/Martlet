using Martlet.Avatars;
using Martlet.Core.Audio;
using Martlet.Core.Contracts;
using NvidiaAce.Controller.V1;
using static Martlet.Avatar.Audio2Face.Tests.Audio2FaceAdapterTests;

namespace Martlet.Avatar.Audio2Face.Tests;

public sealed class GeneratedSpeechStreamTests
{
    [Fact]
    public async Task Live_frame_arrives_before_input_completion_and_preserves_playback_clock()
    {
        var chunks = 0;
        await using var fixture = await ProtocolFixture.StartAsync(async (writer, _) =>
            await writer.WriteAsync(Success()), onInput: async (input, writer, _) =>
        {
            if (input.StreamPartCase == AudioStream.StreamPartOneofCase.AudioStreamHeader)
                await writer.WriteAsync(Header("JawOpen"));
            if (input.StreamPartCase == AudioStream.StreamPartOneofCase.AudioWithEmotion)
                await writer.WriteAsync(Data(chunks++ * 0.1, 0.5f));
        });
        var first = Pcm(sequence: 5);
        using var input = Stream(first);
        Assert.Equal(SpeechIngressResult.Accepted, input.TrySubmit(first));
        var options = Options(fixture);
        using var authorization = Permit(input, options);
        var gotFirst = new TaskCompletionSource<AvatarFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = Read();
        var firstOutput = await gotFirst.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(run.IsCompleted);
        Assert.Equal(first.Ids, firstOutput.Ids);
        Assert.Equal(first.Epoch, firstOutput.Epoch);
        Assert.Equal(first.SampleOffset, firstOutput.SampleOffset);
        Assert.Equal(first.Format.SampleRate, firstOutput.SampleRate);
        Assert.Equal(0, firstOutput.Sequence);
        Assert.Equal(SpeechIngressResult.Accepted, input.TrySubmit(Next(first)));
        Assert.Equal(SpeechIngressResult.Completed, input.CompleteInput(4800));
        var outputs = await run.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(2, outputs.Count);
        Assert.Equal(first.SampleOffset + 2400, outputs[1].SampleOffset);
        Assert.Equal(1, outputs[1].Sequence);
        Assert.Equal(4, fixture.Requests.Count);
        Assert.Equal(AudioStream.StreamPartOneofCase.EndOfAudio, fixture.Requests.Last().StreamPartCase);
        Assert.Equal(SpeechIngressResult.Closed, input.TrySubmit(Next(first)));

        async Task<List<AvatarFrame>> Read()
        {
            var result = new List<AvatarFrame>();
            await foreach (var frame in new Audio2FaceAdapter(options).AnimateAsync(input, authorization))
            {
                result.Add(frame);
                gotFirst.TrySetResult(frame);
            }
            return result;
        }
    }

    [Fact]
    public async Task Full_queue_is_immediate_explicit_segment_failure_and_not_a_silent_drop()
    {
        await using var fixture = await ValidFixture();
        var first = Pcm();
        using var input = Stream(first, capacity: 1);
        Assert.Equal(SpeechIngressResult.Accepted, input.TrySubmit(first));
        Assert.Equal(SpeechIngressResult.QueueFull, input.TrySubmit(Next(first)));
        Assert.Equal(Audio2FaceFailure.Backpressure, input.Failure);
        Assert.Equal(SpeechIngressResult.Closed, input.TrySubmit(Next(first)));
        Assert.Equal(SpeechIngressResult.Closed, input.CompleteInput(2400));
        var options = Options(fixture);
        using var permission = Permit(input, options);
        Assert.Equal(Audio2FaceFailure.Backpressure,
            (await Assert.ThrowsAsync<Audio2FaceException>(() => Collect(new Audio2FaceAdapter(options)
                .AnimateAsync(input, permission)))).Failure);
        Assert.Empty(fixture.Requests);
    }

    [Theory]
    [InlineData("ids")]
    [InlineData("epoch")]
    [InlineData("sequence")]
    [InlineData("offset")]
    [InlineData("format")]
    [InlineData("stereo")]
    public void Ingress_rejects_invalid_identity_or_discontinuous_pcm(string defect)
    {
        var first = Pcm();
        using var input = Stream(first);
        Assert.Equal(SpeechIngressResult.Accepted, input.TrySubmit(first));
        var good = Next(first);
        var frame = new PcmFrame(
            defect == "ids" ? first.Ids with { RequestId = Guid.NewGuid() } : first.Ids,
            defect == "epoch" ? first.Epoch + 1 : first.Epoch,
            defect == "sequence" ? first.Sequence : good.Sequence,
            defect == "offset" ? first.SampleOffset : good.SampleOffset,
            defect switch
            {
                "format" => first.Format with { SampleRate = 48000 },
                "stereo" => first.Format with { Channels = 2 },
                _ => first.Format
            }, first.Data.Span);
        Assert.Equal(SpeechIngressResult.InvalidFrame, input.TrySubmit(frame));
        Assert.Equal(Audio2FaceFailure.InvalidInput, input.Failure);
    }

    [Fact]
    public void Ingress_enforces_max_samples_and_exact_nonempty_completion()
    {
        var first = Pcm();
        using var bounded = Stream(first, maxSamples: 2400);
        Assert.Equal(SpeechIngressResult.Accepted, bounded.TrySubmit(first));
        Assert.Equal(SpeechIngressResult.LimitExceeded, bounded.TrySubmit(Next(first)));
        Assert.Equal(Audio2FaceFailure.LimitExceeded, bounded.Failure);
        using var wrongFinal = Stream(first);
        wrongFinal.TrySubmit(first);
        Assert.Equal(SpeechIngressResult.InvalidFrame, wrongFinal.CompleteInput(2399));
        using var empty = Stream(first);
        Assert.Equal(SpeechIngressResult.InvalidFrame, empty.CompleteInput(0));
    }

    [Fact]
    public async Task Invalid_live_ingress_cancels_waiting_protocol_immediately()
    {
        var gotAudio = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = await ProtocolFixture.StartAsync((_, _) => Task.CompletedTask,
            onInput: (input, _, _) =>
            {
                if (input.StreamPartCase == AudioStream.StreamPartOneofCase.AudioWithEmotion) gotAudio.TrySetResult();
                return Task.CompletedTask;
            });
        var first = Pcm();
        using var input = Stream(first);
        input.TrySubmit(first);
        var options = Options(fixture);
        using var permission = Permit(input, options);
        var run = Collect(new Audio2FaceAdapter(options).AnimateAsync(input, permission));
        await gotAudio.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(SpeechIngressResult.InvalidFrame, input.TrySubmit(first));
        Assert.Equal(Audio2FaceFailure.InvalidInput, (await Assert.ThrowsAsync<Audio2FaceException>(() => run)).Failure);
        await fixture.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rejects_unbacked_future_output_before_or_after_completion(bool complete)
    {
        await using var fixture = await ProtocolFixture.StartAsync((_, _) => Task.CompletedTask,
            onInput: async (input, writer, _) =>
            {
                if (input.StreamPartCase != AudioStream.StreamPartOneofCase.AudioWithEmotion) return;
                await writer.WriteAsync(Header("JawOpen"));
                await writer.WriteAsync(Data(0.2, 0.5f));
            });
        var first = Pcm();
        using var input = Stream(first, maxSamples: 24000);
        input.TrySubmit(first);
        if (complete) input.CompleteInput(2400);
        var options = Options(fixture);
        using var permission = Permit(input, options);
        Assert.Equal(Audio2FaceFailure.InvalidProtocol,
            (await Assert.ThrowsAsync<Audio2FaceException>(() => Collect(new Audio2FaceAdapter(options)
                .AnimateAsync(input, permission)))).Failure);
    }

    [Fact]
    public async Task Rejects_zero_time_coefficients_before_any_pcm_is_submitted_without_yielding()
    {
        await using var fixture = await ProtocolFixture.StartAsync((_, _) => Task.CompletedTask,
            onInput: async (message, writer, _) =>
            {
                if (message.StreamPartCase != AudioStream.StreamPartOneofCase.AudioStreamHeader) return;
                await writer.WriteAsync(Header("JawOpen"));
                await writer.WriteAsync(Data(0, 0.5f));
            });
        using var input = Stream(Pcm());
        var options = Options(fixture);
        using var permission = Permit(input, options);
        await using var output = new Audio2FaceAdapter(options).AnimateAsync(input, permission).GetAsyncEnumerator();
        Assert.Equal(Audio2FaceFailure.InvalidProtocol,
            (await Assert.ThrowsAsync<Audio2FaceException>(() => output.MoveNextAsync().AsTask())).Failure);
        Assert.Equal(0, input.SampleCount);
        Assert.Equal(AudioStream.StreamPartOneofCase.AudioStreamHeader, Assert.Single(fixture.Requests).StreamPartCase);
        await fixture.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Missing_input_completion_cannot_become_success_and_deadline_stops_ingress()
    {
        await using var fixture = await ProtocolFixture.StartAsync((_, _) => Task.CompletedTask,
            onInput: async (input, writer, _) =>
            {
                if (input.StreamPartCase != AudioStream.StreamPartOneofCase.AudioWithEmotion) return;
                await writer.WriteAsync(Header("JawOpen"));
                await writer.WriteAsync(Data(0, 0.5f));
                await writer.WriteAsync(Success());
            });
        var first = Pcm();
        using var input = Stream(first);
        input.TrySubmit(first);
        var options = Options(fixture) with { IdleTimeout = TimeSpan.FromMilliseconds(150) };
        using var permission = Permit(input, options);
        Assert.Equal(Audio2FaceFailure.DeadlineExceeded,
            (await Assert.ThrowsAsync<Audio2FaceException>(() => Collect(new Audio2FaceAdapter(options)
                .AnimateAsync(input, permission)))).Failure);
        Assert.Equal(SpeechIngressResult.Closed, input.TrySubmit(Next(first)));
    }

    [Fact]
    public async Task Premature_successful_eof_rejects_an_unfinished_input_instead_of_waiting_for_it()
    {
        await using var fixture = await ProtocolFixture.StartAsync(async (writer, _) =>
        {
            await writer.WriteAsync(Header("JawOpen"));
            await writer.WriteAsync(Data(0, 0.5f));
            await writer.WriteAsync(Success());
        }, completeAfterFirstInput: true);
        var first = Pcm();
        using var input = Stream(first);
        input.TrySubmit(first);
        var options = Options(fixture);
        using var permission = Permit(input, options);
        Assert.Equal(Audio2FaceFailure.InvalidProtocol,
            (await Assert.ThrowsAsync<Audio2FaceException>(() => Collect(new Audio2FaceAdapter(options)
                .AnimateAsync(input, permission)).WaitAsync(TimeSpan.FromSeconds(2)))).Failure);
    }

    [Fact]
    public async Task Stream_authorization_is_exact_instance_bound_and_disposal_cancels_pending_reads()
    {
        await using var fixture = await ValidFixture();
        var first = Pcm();
        using var input = Stream(first);
        using var other = Stream(first);
        var options = Options(fixture);
        using var permission = Permit(input, options);
        Assert.Equal(Audio2FaceFailure.InvalidBinding,
            (await Assert.ThrowsAsync<Audio2FaceException>(() => Collect(new Audio2FaceAdapter(options)
                .AnimateAsync(other, permission)))).Failure);
        Assert.Empty(fixture.Requests);
        var run = Collect(new Audio2FaceAdapter(options).AnimateAsync(input, permission));
        input.Dispose();
        input.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(SpeechIngressResult.Closed, input.TrySubmit(first));
    }

    private static GeneratedSpeechStream Stream(PcmFrame first, int capacity = 16, long maxSamples = 4800) =>
        new(first.Ids, first.Epoch, first.Format, first.Sequence, first.SampleOffset, maxSamples, capacity);
    private static PcmFrame Next(PcmFrame first) => new(first.Ids, first.Epoch, first.Sequence + 1,
        first.SampleOffset + first.SamplesPerChannel, first.Format, first.Data.Span);
    private static Audio2FaceAuthorization Permit(GeneratedSpeechStream input, Audio2FaceOptions options) =>
        new(input, options, DateTimeOffset.UtcNow.AddSeconds(10), allowGeneratedSpeechAnalysis: true);
}
