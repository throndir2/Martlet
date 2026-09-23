using Google.Protobuf;
using Grpc.Core;
using Martlet.Avatars;
using Martlet.Core.Audio;
using Martlet.Core.Contracts;
using NvidiaAce.AnimationData.V1;
using NvidiaAce.Controller.V1;
using Output = NvidiaAce.Controller.V1.AnimationDataStream;
using OutputHeader = NvidiaAce.Controller.V1.AnimationDataStreamHeader;
using UpstreamStatus = NvidiaAce.Status.V1.Status;

namespace Martlet.Avatar.Audio2Face.Tests;

public sealed class Audio2FaceAdapterTests
{
    [Theory]
    [InlineData(16000)]
    [InlineData(24000)]
    [InlineData(44100)]
    [InlineData(48000)]
    public async Task Real_http2_protobuf_preserves_pcm_clock_ids_and_named_coefficients(int rate)
    {
        await using var fixture = await ProtocolFixture.StartAsync(async (stream, _) =>
        {
            await stream.WriteAsync(Header("JawOpen", "EyeBlinkLeft", "HeadYaw"));
            var data = Data(0.05, 0.25f, 0.75f, -0.5f);
            data.AnimationData.Audio = new AudioWithTimeCode { AudioBuffer = ByteString.CopyFromUtf8("never replay") };
            await stream.WriteAsync(data);
            await stream.WriteAsync(new Output { Event = new Event() });
            await stream.WriteAsync(Success());
        });
        var clip = Clip(rate);
        var options = Options(fixture);
        using var authorization = Permit(clip, options);
        var frames = await Collect(new Audio2FaceAdapter(options).AnimateAsync(clip, authorization));
        var frame = Assert.Single(frames);
        Assert.Equal(clip.Ids, frame.Ids);
        Assert.Equal(clip.Epoch, frame.Epoch);
        Assert.Equal(rate, frame.SampleRate);
        Assert.Equal(12_000 + (long)Math.Floor(0.05 * rate), frame.SampleOffset);
        Assert.Equal(0, frame.Sequence);
        Assert.Equal(Audio2FaceAdapter.SourceId, frame.SourceId);
        Assert.Equal(0.25, frame.Blendshapes["jawOpen"]);
        Assert.Equal(0.75, frame.Blendshapes["eyeBlinkLeft"]);
        Assert.Equal(2, frame.Blendshapes.Count);
        Assert.Empty(frame.Semantics);
        var sent = fixture.Requests.ToArray();
        Assert.Equal(3, sent.Length);
        Assert.Equal((uint)rate, sent[0].AudioStreamHeader.AudioHeader.SamplesPerSecond);
        Assert.Equal(1u, sent[0].AudioStreamHeader.AudioHeader.ChannelCount);
        Assert.Equal(16u, sent[0].AudioStreamHeader.AudioHeader.BitsPerSample);
        Assert.True(sent[0].AudioStreamHeader.BlendshapeParams.EnableClampingBsWeight);
        Assert.Empty(sent[1].AudioWithEmotion.Emotions);
        Assert.Equal(Pcm(rate).Data.ToArray(), sent[1].AudioWithEmotion.AudioBuffer.ToByteArray());
        Assert.Equal(AudioStream.StreamPartOneofCase.EndOfAudio, sent[2].StreamPartCase);
        Assert.True(authorization.IsConsumed);
    }

    [Fact]
    public async Task All_52_channels_have_exact_canonical_mapping()
    {
        var canonical = AvatarChannels.BlendshapeNames.ToArray();
        var upstream = canonical.Select(name => char.ToUpperInvariant(name[0]) + name[1..]).ToArray();
        await using var fixture = await ProtocolFixture.StartAsync(async (stream, _) =>
        {
            await stream.WriteAsync(Header(upstream));
            await stream.WriteAsync(Data(0, Enumerable.Repeat(0.5f, 52).ToArray()));
            await stream.WriteAsync(Success());
        });
        var frame = Assert.Single(await Run(fixture));
        Assert.Equal(canonical.Order(), frame.Blendshapes.Keys.Order());
    }

    [Fact]
    public async Task Duplex_reads_prevent_flow_control_deadlock_during_large_upload()
    {
        var names = AvatarChannels.BlendshapeNames.Select(name => char.ToUpperInvariant(name[0]) + name[1..]).ToArray();
        await using var fixture = await ProtocolFixture.StartAsync(async (stream, _) =>
        {
            await stream.WriteAsync(Header(names));
            for (var i = 0; i < 1000; i++)
                await stream.WriteAsync(Data(i * 0.01, Enumerable.Repeat(0.5f, 52).ToArray()));
            await stream.WriteAsync(Success());
        }, respondBeforeUpload: true);
        var first = Pcm();
        var clip = new GeneratedSpeechClip(Enumerable.Range(0, 900).Select(i =>
            new PcmFrame(first.Ids, first.Epoch, i, i * 2400, first.Format, first.Data.Span)));
        var options = Options(fixture);
        using var permit = Permit(clip, options);
        var frames = await Collect(new Audio2FaceAdapter(options).AnimateAsync(clip, permit));
        Assert.Equal(1000, frames.Count);
        Assert.Equal(902, fixture.Requests.Count);
        Assert.Equal(999, frames[^1].Sequence);
    }

    [Theory]
    [InlineData("http://localhost:52000")]
    [InlineData("http://192.168.1.20:52000")]
    [InlineData("https://127.0.0.1:52000")]
    [InlineData("http://127.0.0.1:52000/path")]
    [InlineData("http://127.0.0.1:52000?token=secret")]
    [InlineData("http://user:secret@127.0.0.1:52000")]
    [InlineData("http://127.0.0.1:52000/#fragment")]
    public void Rejects_nonliteral_nonloopback_or_credential_destinations(string endpoint)
    {
        Assert.Throws<ArgumentException>(() => new Audio2FaceAdapter(new() { Endpoint = new Uri(endpoint) }));
    }

    [Fact]
    public async Task Construction_report_and_missing_authorization_are_network_inert()
    {
        await using var fixture = await ProtocolFixture.StartAsync((_, _) => Task.CompletedTask);
        var clip = Clip();
        var options = Options(fixture);
        var adapter = new Audio2FaceAdapter(options);
        var report = adapter.InspectPrerequisites();
        Assert.True(report.ConfigurationValid);
        Assert.False(report.RuntimeVerified);
        Assert.NotEmpty(report.Requirements);
        using var denied = new Audio2FaceAuthorization(clip, options, DateTimeOffset.UtcNow.AddSeconds(10));
        var exception = await Assert.ThrowsAsync<Audio2FaceException>(() => Collect(adapter.AnimateAsync(clip, denied)));
        Assert.Equal(Audio2FaceFailure.AuthorizationRequired, exception.Failure);
        Assert.Empty(fixture.Requests);
    }

    [Fact]
    public async Task Authorization_is_bound_to_exact_clip_destination_limits_and_single_use()
    {
        await using var fixture = await ValidFixture();
        var clip = Clip();
        var options = Options(fixture);
        using var permit = Permit(clip, options);
        var adapter = new Audio2FaceAdapter(options);
        var mismatch = await Assert.ThrowsAsync<Audio2FaceException>(() => Collect(adapter.AnimateAsync(Clip(), permit)));
        Assert.Equal(Audio2FaceFailure.InvalidBinding, mismatch.Failure);
        var different = new Audio2FaceAdapter(options with { MaxOutputFrames = 1 });
        mismatch = await Assert.ThrowsAsync<Audio2FaceException>(() => Collect(different.AnimateAsync(clip, permit)));
        Assert.Equal(Audio2FaceFailure.InvalidBinding, mismatch.Failure);
        var elsewhere = new Audio2FaceAdapter(options with { Endpoint = new Uri("http://127.0.0.1:1") });
        mismatch = await Assert.ThrowsAsync<Audio2FaceException>(() => Collect(elsewhere.AnimateAsync(clip, permit)));
        Assert.Equal(Audio2FaceFailure.InvalidBinding, mismatch.Failure);
        Assert.Empty(fixture.Requests);
        await Collect(adapter.AnimateAsync(clip, permit));
        var consumed = await Assert.ThrowsAsync<Audio2FaceException>(() => Collect(adapter.AnimateAsync(clip, permit)));
        Assert.Equal(Audio2FaceFailure.AuthorizationConsumed, consumed.Failure);
    }

    [Fact]
    public async Task Expired_and_revoked_authorization_do_not_connect()
    {
        await using var fixture = await ValidFixture();
        var clip = Clip();
        var options = Options(fixture);
        using var expired = new Audio2FaceAuthorization(clip, options, DateTimeOffset.UtcNow.AddMilliseconds(20), true);
        await Task.Delay(40);
        var adapter = new Audio2FaceAdapter(options);
        Assert.Equal(Audio2FaceFailure.AuthorizationExpired,
            (await Assert.ThrowsAsync<Audio2FaceException>(() => Collect(adapter.AnimateAsync(clip, expired)))).Failure);
        using var revoked = Permit(clip, options);
        revoked.Revoke();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Collect(adapter.AnimateAsync(clip, revoked)));
        Assert.Empty(fixture.Requests);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("missing-status")]
    [InlineData("missing-frame")]
    [InlineData("duplicate-header")]
    [InlineData("no-header")]
    [InlineData("unknown-name")]
    [InlineData("duplicate-name")]
    [InlineData("empty-names")]
    [InlineData("wrong-case")]
    [InlineData("nan")]
    [InlineData("infinity")]
    [InlineData("out-of-range")]
    [InlineData("wrong-count")]
    [InlineData("negative-time")]
    [InlineData("nan-time")]
    [InlineData("beyond-clip")]
    [InlineData("reordered")]
    [InlineData("same-sample")]
    [InlineData("late-data")]
    [InlineData("event-only")]
    public async Task Rejects_malformed_protocol_without_fallback(string fault)
    {
        await using var fixture = await ProtocolFixture.StartAsync(async (stream, _) =>
        {
            if (fault == "empty") return;
            if (fault == "no-header") { await stream.WriteAsync(Data(0, 0.5f)); return; }
            var names = fault switch
            {
                "unknown-name" => new[] { "Invented" },
                "duplicate-name" => ["JawOpen", "JawOpen"],
                "empty-names" => [],
                "wrong-case" => ["jawOpen"],
                _ => ["JawOpen"]
            };
            await stream.WriteAsync(Header(names));
            if (fault == "duplicate-header") await stream.WriteAsync(Header("JawOpen"));
            if (fault == "event-only") { await stream.WriteAsync(new Output { Event = new Event() }); return; }
            if (fault != "missing-frame")
            {
                var value = fault switch { "nan" => float.NaN, "infinity" => float.PositiveInfinity, "out-of-range" => 1.1f, _ => 0.5f };
                var time = fault switch { "negative-time" => -1, "nan-time" => double.NaN, "beyond-clip" => 10, _ => 0.05 };
                await stream.WriteAsync(Data(time, fault == "wrong-count" ? [] : [value]));
                if (fault == "reordered") await stream.WriteAsync(Data(0.025, 0.5f));
                if (fault == "same-sample") await stream.WriteAsync(Data(0.05000000001, 0.5f));
            }
            if (fault != "missing-status") await stream.WriteAsync(Success());
            if (fault == "late-data") await stream.WriteAsync(Data(0.075, 0.5f));
        });
        Assert.Equal(Audio2FaceFailure.InvalidProtocol,
            (await Assert.ThrowsAsync<Audio2FaceException>(() => Run(fixture))).Failure);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Upstream_warning_or_error_is_not_silenced_or_logged(bool warning)
    {
        await using var fixture = await ProtocolFixture.StartAsync(async (stream, _) =>
        {
            await stream.WriteAsync(new Output
            {
                Status = new UpstreamStatus
                {
                    Code = warning ? UpstreamStatus.Types.Code.Warning : UpstreamStatus.Types.Code.Error,
                    Message = "secret audio and credentials"
                }
            });
        });
        var exception = await Assert.ThrowsAsync<Audio2FaceException>(() => Run(fixture));
        Assert.Equal(Audio2FaceFailure.UpstreamFailure, exception.Failure);
        Assert.DoesNotContain("secret", exception.ToString());
    }

    [Fact]
    public async Task Transport_error_details_are_redacted()
    {
        await using var fixture = await ProtocolFixture.StartAsync((_, _) =>
            throw new RpcException(new Grpc.Core.Status(StatusCode.Internal, "secret")));
        var exception = await Assert.ThrowsAsync<Audio2FaceException>(() => Run(fixture));
        Assert.Equal(Audio2FaceFailure.TransportFailure, exception.Failure);
        Assert.DoesNotContain("secret", exception.ToString());
    }

    [Fact]
    public async Task Output_frame_and_byte_caps_are_enforced()
    {
        await using var fixture = await ProtocolFixture.StartAsync(async (stream, _) =>
        {
            await stream.WriteAsync(Header("JawOpen"));
            await stream.WriteAsync(Data(0, 0.5f));
            await stream.WriteAsync(Data(0.05, 0.5f));
            await stream.WriteAsync(Success());
        });
        Assert.Equal(Audio2FaceFailure.LimitExceeded,
            (await Assert.ThrowsAsync<Audio2FaceException>(() => Run(fixture, Options(fixture) with { MaxOutputFrames = 1 }))).Failure);
        await using var large = await ProtocolFixture.StartAsync(async (stream, _) =>
        {
            await stream.WriteAsync(Header("JawOpen"));
            var message = Data(0, 0.5f);
            message.AnimationData.Audio = new AudioWithTimeCode { AudioBuffer = ByteString.CopyFrom(new byte[2048]) };
            await stream.WriteAsync(message);
        });
        Assert.Equal(Audio2FaceFailure.LimitExceeded,
            (await Assert.ThrowsAsync<Audio2FaceException>(() => Run(large, Options(large) with { MaxResponseBytes = 1024 }))).Failure);
    }

    [Fact]
    public async Task Idle_timeout_cancels_actual_http2_call()
    {
        await using var fixture = await ProtocolFixture.StartAsync((_, context) =>
            Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken));
        Assert.Equal(Audio2FaceFailure.DeadlineExceeded,
            (await Assert.ThrowsAsync<Audio2FaceException>(() => Run(fixture,
                Options(fixture) with { IdleTimeout = TimeSpan.FromMilliseconds(200) }))).Failure);
        await fixture.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Disposing_active_authorization_is_idempotent_cancellation()
    {
        await using var fixture = await ProtocolFixture.StartAsync((_, context) =>
            Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken));
        var clip = Clip();
        var options = Options(fixture);
        using var permit = Permit(clip, options);
        var run = Collect(new Audio2FaceAdapter(options).AnimateAsync(clip, permit));
        await fixture.ReceivedAudio.Task.WaitAsync(TimeSpan.FromSeconds(3));
        permit.Dispose();
        permit.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        await fixture.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Caller_cancellation_and_revocation_cancel_active_calls(bool revoke)
    {
        await using var fixture = await ProtocolFixture.StartAsync((_, context) =>
            Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken));
        var clip = Clip();
        var options = Options(fixture);
        using var permit = Permit(clip, options);
        using var cancellation = new CancellationTokenSource();
        var run = Collect(new Audio2FaceAdapter(options).AnimateAsync(clip, permit, cancellation.Token));
        await fixture.ReceivedAudio.Task.WaitAsync(TimeSpan.FromSeconds(3));
        if (revoke) permit.Revoke(); else cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        await fixture.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Slow_consumer_gets_no_late_frames_after_total_deadline()
    {
        await using var fixture = await ProtocolFixture.StartAsync(async (stream, context) =>
        {
            await stream.WriteAsync(Header("JawOpen"));
            await stream.WriteAsync(Data(0, 0.5f));
            await stream.WriteAsync(Data(0.05, 0.5f));
            await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);
        });
        var clip = Clip();
        var options = Options(fixture) with { RequestTimeout = TimeSpan.FromMilliseconds(400), IdleTimeout = TimeSpan.FromMilliseconds(300) };
        using var permit = Permit(clip, options);
        await using (var frames = new Audio2FaceAdapter(options).AnimateAsync(clip, permit).GetAsyncEnumerator())
        {
            Assert.True(await frames.MoveNextAsync());
            await Task.Delay(500);
            Assert.Equal(Audio2FaceFailure.DeadlineExceeded,
                (await Assert.ThrowsAsync<Audio2FaceException>(async () => await frames.MoveNextAsync())).Failure);
        }
        await fixture.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Early_consumer_disposal_cancels_service_without_waiting_for_deadline()
    {
        await using var fixture = await ProtocolFixture.StartAsync(async (stream, context) =>
        {
            await stream.WriteAsync(Header("JawOpen"));
            await stream.WriteAsync(Data(0, 0.5f));
            await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);
        });
        var clip = Clip();
        var options = Options(fixture);
        using var permit = Permit(clip, options);
        var frames = new Audio2FaceAdapter(options).AnimateAsync(clip, permit).GetAsyncEnumerator();
        Assert.True(await frames.MoveNextAsync());
        await frames.DisposeAsync();
        await fixture.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Intermediate_success_and_info_do_not_replace_final_success()
    {
        await using var fixture = await ProtocolFixture.StartAsync(async (stream, _) =>
        {
            await stream.WriteAsync(Header("JawOpen"));
            await stream.WriteAsync(Success());
            await stream.WriteAsync(new Output { Status = new UpstreamStatus { Code = UpstreamStatus.Types.Code.Info } });
            await stream.WriteAsync(Data(0, 0.5f));
            await stream.WriteAsync(Success());
        });
        Assert.Single(await Run(fixture));
    }

    [Fact]
    public void Clip_rejects_unbounded_discontinuous_mixed_and_stereo_input()
    {
        Assert.Throws<ArgumentException>(() => new GeneratedSpeechClip([]));
        var first = Pcm();
        Assert.Throws<ArgumentException>(() => new GeneratedSpeechClip([first, first]));
        var differentIds = Pcm(sequence: 1, offset: 14_400);
        Assert.Throws<ArgumentException>(() => new GeneratedSpeechClip([first, differentIds]));
        var stereo = new PcmFrame(first.Ids, first.Epoch, 0, 0,
            first.Format with { Channels = 2 }, new byte[9600]);
        Assert.Throws<ArgumentException>(() => new GeneratedSpeechClip([stereo]));
        var frames = Enumerable.Range(0, 901).Select(i => new PcmFrame(first.Ids, first.Epoch, i, i * 2400,
            first.Format, new byte[4800]));
        Assert.Throws<ArgumentException>(() => new GeneratedSpeechClip(frames));
    }

    internal static Output Header(params string[] names)
    {
        var header = new SkelAnimationHeader();
        header.BlendShapes.Add(names);
        return new Output { AnimationDataStreamHeader = new OutputHeader { SkelAnimationHeader = header } };
    }

    internal static Output Data(double time, params float[] values)
    {
        var weights = new FloatArrayWithTimeCode { TimeCode = time };
        weights.Values.Add(values);
        var animation = new SkelAnimation();
        animation.BlendShapeWeights.Add(weights);
        return new Output { AnimationData = new AnimationData { SkelAnimation = animation } };
    }

    internal static Output Success() => new() { Status = new UpstreamStatus { Code = UpstreamStatus.Types.Code.Success } };
    internal static Task<ProtocolFixture> ValidFixture() => ProtocolFixture.StartAsync(async (stream, _) =>
    {
        await stream.WriteAsync(Header("JawOpen"));
        await stream.WriteAsync(Data(0, 0.5f));
        await stream.WriteAsync(Success());
    });
    internal static Audio2FaceOptions Options(ProtocolFixture fixture) => new()
    {
        Endpoint = fixture.Endpoint, RequestTimeout = TimeSpan.FromSeconds(5), IdleTimeout = TimeSpan.FromSeconds(2)
    };
    internal static Audio2FaceAuthorization Permit(GeneratedSpeechClip clip, Audio2FaceOptions options) =>
        new(clip, options, DateTimeOffset.UtcNow.AddSeconds(10), true);
    internal static PcmFrame Pcm(int rate = 24000, long sequence = 0, long offset = 12_000) => new(
        new CorrelationIds { SessionId = Guid.NewGuid(), TurnId = Guid.NewGuid(), RequestId = Guid.NewGuid() },
        7, sequence, offset, new PcmFormat { SampleRate = rate, Channels = 1, Encoding = PcmEncoding.Signed16LittleEndian },
        Enumerable.Repeat((byte)42, rate / 10 * 2).ToArray());
    internal static GeneratedSpeechClip Clip(int rate = 24000) => new([Pcm(rate)]);
    internal static async Task<List<AvatarFrame>> Run(ProtocolFixture fixture, Audio2FaceOptions? selected = null)
    {
        var options = selected ?? Options(fixture);
        var clip = Clip();
        using var permit = Permit(clip, options);
        return await Collect(new Audio2FaceAdapter(options).AnimateAsync(clip, permit));
    }
    internal static async Task<List<AvatarFrame>> Collect(IAsyncEnumerable<AvatarFrame> frames)
    {
        var result = new List<AvatarFrame>();
        await foreach (var frame in frames) result.Add(frame);
        return result;
    }
}
