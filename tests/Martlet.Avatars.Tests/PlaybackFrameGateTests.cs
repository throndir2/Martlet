using Martlet.Core.Audio;
using Martlet.Core.Contracts;

namespace Martlet.Avatars.Tests;

public sealed class PlaybackFrameGateTests
{
    [Fact]
    public void AcceptsDueFrameWithinBoundedLatenessRatherThanExactSampleEquality()
    {
        var gate = new PlaybackFrameGate(TestData.Binding);
        Assert.Equal(FrameDisposition.Accepted, gate.TryAccept(TestData.Frame, TestData.Position(5500)));
        Assert.Equal(FrameDisposition.OutOfOrder, gate.TryAccept(TestData.Frame, TestData.Position(5500)));
    }

    [Fact]
    public void LatenessBoundaryUsesOriginalPlaybackRate()
    {
        Assert.Equal(FrameDisposition.Accepted,
            new PlaybackFrameGate(TestData.Binding).TryAccept(TestData.Frame, TestData.Position(4800 + 12000)));
        Assert.Equal(FrameDisposition.TooLate,
            new PlaybackFrameGate(TestData.Binding).TryAccept(TestData.Frame, TestData.Position(4800 + 12001)));
    }

    [Fact]
    public void FutureFrameIsNotConsumedAndNeedsActualPlaybackToAdvance()
    {
        var gate = new PlaybackFrameGate(TestData.Binding);
        Assert.Equal(FrameDisposition.PlaybackUnavailable, gate.TryAccept(TestData.Frame, null));
        Assert.Equal(FrameDisposition.Future, gate.TryAccept(TestData.Frame, TestData.Position(0)));
        Assert.Equal(FrameDisposition.Future, gate.TryAccept(TestData.Frame, TestData.Position(4799)));
        Assert.Equal(FrameDisposition.Accepted, gate.TryAccept(TestData.Frame, TestData.Position(4800)));
    }

    [Fact]
    public void ExcessiveFutureLeadIsExplicitlyRejected()
    {
        var gate = new PlaybackFrameGate(TestData.Binding);
        Assert.Equal(FrameDisposition.TooFarAhead,
            gate.TryAccept(TestData.Frame with { SampleOffset = 48001 }, TestData.Position(0)));
        Assert.Equal(FrameDisposition.Future,
            gate.TryAccept(TestData.Frame with { SampleOffset = 48000 }, TestData.Position(0)));
    }

    [Fact]
    public void SequenceAndSourceTimeAreMonotonic()
    {
        var gate = new PlaybackFrameGate(TestData.Binding);
        Assert.Equal(FrameDisposition.Accepted, gate.TryAccept(TestData.Frame, TestData.Position()));
        Assert.Equal(FrameDisposition.OutOfOrder, gate.TryAccept(TestData.Frame with { Sequence = 6 }, TestData.Position()));
        Assert.Equal(FrameDisposition.OutOfOrder,
            gate.TryAccept(TestData.Frame with { Sequence = 8, SampleOffset = 4799 }, TestData.Position()));
        Assert.Equal(FrameDisposition.Accepted,
            gate.TryAccept(TestData.Frame with { Sequence = 9, SampleOffset = 4800 }, TestData.Position()));
    }

    [Fact]
    public void PositionCannotRegressEvenWhileFramesAreHeld()
    {
        var gate = new PlaybackFrameGate(TestData.Binding);
        Assert.Equal(FrameDisposition.Future, gate.TryAccept(TestData.Frame, TestData.Position(4000)));
        Assert.Equal(FrameDisposition.PlaybackRegressed, gate.TryAccept(TestData.Frame, TestData.Position(3999)));
    }

    [Fact]
    public void EveryFrameIdentityDimensionIsBound()
    {
        var variants = new[]
        {
            TestData.Frame with { Ids = TestData.Ids with { SessionId = Guid.NewGuid() } },
            TestData.Frame with { Ids = TestData.Ids with { TurnId = Guid.NewGuid() } },
            TestData.Frame with { Ids = TestData.Ids with { RequestId = Guid.NewGuid() } },
            TestData.Frame with { Epoch = 2 },
            TestData.Frame with { SourceId = "amplitude" },
            TestData.Frame with { SampleRate = 16000 }
        };
        foreach (var frame in variants)
            Assert.Equal(FrameDisposition.WrongBinding, new PlaybackFrameGate(TestData.Binding).TryAccept(frame, TestData.Position()));
    }

    [Fact]
    public void EveryPlaybackIdentityDimensionIsBound()
    {
        var variants = new[]
        {
            TestData.Position() with { Ids = TestData.Ids with { SessionId = Guid.NewGuid() } },
            TestData.Position() with { Ids = TestData.Ids with { TurnId = Guid.NewGuid() } },
            TestData.Position() with { Ids = TestData.Ids with { RequestId = Guid.NewGuid() } },
            TestData.Position() with { Epoch = 2 },
            TestData.Position() with { SampleRate = 16000 }
        };
        foreach (var position in variants)
            Assert.Equal(FrameDisposition.WrongBinding,
                new PlaybackFrameGate(TestData.Binding).TryAccept(TestData.Frame, position));
    }

    [Fact]
    public void StopRejectsPendingAndNewFramesUntilFreshBinding()
    {
        var gate = new PlaybackFrameGate(TestData.Binding);
        Assert.Equal(FrameDisposition.Future, gate.TryAccept(TestData.Frame, TestData.Position(0)));
        gate.Stop();
        Assert.Equal(FrameDisposition.Stopped, gate.TryAccept(TestData.Frame, TestData.Position()));
        Assert.Throws<ContractException>(() => gate.Reset(TestData.Binding));
        Assert.Throws<ContractException>(() => gate.Reset(TestData.Binding with { Epoch = 2 }));
        gate.Reset(TestData.Binding with { Epoch = 4 });
        Assert.Equal(FrameDisposition.WrongBinding, gate.TryAccept(TestData.Frame, TestData.Position()));
        Assert.Equal(FrameDisposition.Accepted, gate.TryAccept(TestData.Frame with { Epoch = 4, Sequence = 0 },
            TestData.Position() with { Epoch = 4 }));
    }

    [Fact]
    public void NewCorrelationCanStartAnIndependentEpoch()
    {
        var ids = TestData.Ids with { RequestId = Guid.NewGuid() };
        var gate = new PlaybackFrameGate(TestData.Binding);
        gate.Reset(TestData.Binding with { Ids = ids, Epoch = 0 });
        Assert.Equal(FrameDisposition.WrongBinding, gate.TryAccept(TestData.Frame, TestData.Position()));
        Assert.Equal(FrameDisposition.Accepted, gate.TryAccept(TestData.Frame with { Ids = ids, Epoch = 0 },
            TestData.Position() with { Ids = ids, Epoch = 0 }));
    }

    [Fact]
    public void BindingReusesValidatedOriginalPcmContract()
    {
        var pcm = new PcmFrame(TestData.Ids, 3, 0, 0, new PcmFormat
        {
            SampleRate = 44100, Channels = 2, Encoding = PcmEncoding.Signed16LittleEndian
        }, new byte[1764]);
        var binding = PlaybackBinding.FromPcm(pcm, "audio2face");
        Assert.Equal(44100, binding.SampleRate);
        Assert.Equal(TestData.Ids, binding.Ids);
    }

    [Theory]
    [InlineData(1600, 16000, 48000, 4800)]
    [InlineData(1600, 16000, 44100, 4410)]
    [InlineData(1, 48000, 16000, 0)]
    [InlineData(4410, 44100, 24000, 2400)]
    public void AnalysisOffsetsConvertToOriginalPlaybackSamples(long input, int from, int to, long expected) =>
        Assert.Equal(expected, SampleClock.ConvertOffset(input, from, to));

    [Fact]
    public void ClockConversionRejectsUnsafeIntegersAndUnsupportedPcmRates()
    {
        Assert.Throws<ContractException>(() => SampleClock.ConvertOffset(-1, 16000, 48000));
        Assert.Throws<ContractException>(() => SampleClock.ConvertOffset(9_007_199_254_740_991, 16000, 48000));
        Assert.Throws<ContractException>(() => SampleClock.ConvertOffset(1, 22050, 48000));
        Assert.Equal(9_007_199_254_740_991, SampleClock.ConvertOffset(9_007_199_254_740_991, 48000, 48000));
    }
}
