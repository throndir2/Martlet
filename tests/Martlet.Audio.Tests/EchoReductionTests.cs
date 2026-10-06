using Martlet.Core.Contracts;

namespace Martlet.Audio.Tests;

// Whether echo reduction can tell what the speakers play from the user (EchoReducer.Works, EchoTimeline.Reducing): always
// listening goes on while Martlet speaks only when it can.
public sealed class EchoReductionTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(5);
    private static readonly Guid Session = Guid.Parse("5c1d8f0e-2a4b-4c7d-9e3f-6a8b0c2d4e61");

    [Fact]
    public async Task AWorkingCaptureDecidesForItselfAndItsReportFollows()
    {
        var speakers = new Speakers { OpenFailure = new CaptureDeviceException(ErrorCode.AudioAccessDenied) };
        using var reducer = new EchoReducer(new ControlledCapture(), speakers, () => new Canceller(), new CaptureClock());
        // Before any capture nothing says it can't.
        Assert.True(reducer.Works());

        var failed = new EchoTimeline(TimeSpan.FromSeconds(30));
        await WhileOpen(reducer, failed, () =>
        {
            // The speakers couldn't be read: the microphone works without echo reduction and says so at once.
            Assert.Null(failed.Reducing);
            Assert.Equal(EchoReductionState.NoSpeakerAudio, reducer.Report.State);
            Assert.False(reducer.Works(failed));
            Assert.False(reducer.Works());
        });

        speakers.OpenFailure = null;
        var working = new EchoTimeline(TimeSpan.FromSeconds(30));
        await WhileOpen(reducer, working, () =>
        {
            // This capture's own echo reduction works, whatever the last one reported.
            Assert.True(working.Reducing);
            Assert.True(reducer.Works(working));
            Assert.False(reducer.Works());
        });
        Assert.Equal(EchoReductionState.Active, reducer.Report.State);
        Assert.True(reducer.Works());
        Assert.True(working.Reducing);
    }

    [Fact]
    public async Task ACancellerThatCantStartDoesntWork()
    {
        using var reducer = new EchoReducer(new ControlledCapture(), new Speakers(),
            () => throw new DllNotFoundException("PRIVATE native detail"), new CaptureClock());
        var timeline = new EchoTimeline(TimeSpan.FromSeconds(30));
        await WhileOpen(reducer, timeline, () =>
        {
            Assert.Null(timeline.Reducing);
            Assert.Equal(EchoReductionState.Unavailable, reducer.Report.State);
            Assert.False(reducer.Works(timeline));
        });
    }

    [Fact]
    public async Task SpeakersLostAsTheCaptureStartsStopWorking()
    {
        var speakers = new Speakers { StartFailure = new CaptureDeviceException(ErrorCode.AudioDeviceLost) };
        using var reducer = new EchoReducer(new ControlledCapture(), speakers, () => new Canceller(), new CaptureClock());
        var timeline = new EchoTimeline(TimeSpan.FromSeconds(30));
        await WhileOpen(reducer, timeline, () =>
        {
            Assert.False(timeline.Reducing);
            Assert.False(reducer.Works(timeline));
        });
        Assert.Equal(EchoReductionState.NoSpeakerAudio, reducer.Report.State);
        Assert.False(reducer.Works());
    }

    [Fact]
    public async Task ACancellerThatFailsMidCaptureStopsWorking()
    {
        var microphone = new ControlledCapture();
        var canceller = new Canceller();
        using var reducer = new EchoReducer(microphone, new Speakers(), () => canceller, new CaptureClock());
        var timeline = new EchoTimeline(TimeSpan.FromSeconds(30));
        await WhileOpen(reducer, timeline, async () =>
        {
            Assert.True(timeline.Reducing);
            canceller.Fail = true;
            // A second of microphone audio: enough that its 10 ms frames are cleaned without waiting for the clock.
            for (var packet = 0; packet < 10; packet++) microphone.Packets.Enqueue(new byte[3200]);
            await CaptureTests.Until(() => timeline.Reducing == false);
            Assert.False(reducer.Works(timeline));
        });
        Assert.Equal(EchoReductionState.Unavailable, reducer.Report.State);
    }

    private static Task WhileOpen(EchoReducer reducer, EchoTimeline timeline, Action check) =>
        WhileOpen(reducer, timeline, () => { check(); return Task.CompletedTask; });

    // One capture through echo reduction: runs check once it is capturing, then ends it and waits for its devices to go.
    private static async Task WhileOpen(EchoReducer reducer, EchoTimeline timeline, Func<Task> check)
    {
        await using var capture = new MicrophoneCapture(Session, reducer.For(null, timeline));
        var request = new CaptureRequest(new() { SessionId = Session, TurnId = Guid.NewGuid(), RequestId = Guid.NewGuid() }, 0,
            new(InputPolicy.FixedEndpoint, "private-microphone-canary"), TimeSpan.FromSeconds(30), DateTimeOffset.UtcNow.AddSeconds(30));
        var run = capture.Press(request, new(request, true));
        await run.Ready.WaitAsync(WaitLimit);
        await check();
        await run.CancelAsync().WaitAsync(WaitLimit);
        Assert.True((await run.DeviceRelease.WaitAsync(WaitLimit)).Released);
    }

    private sealed class Speakers : IEchoReferenceFactory
    {
        internal Exception? OpenFailure { get; set; }
        internal Exception? StartFailure { get; init; }

        public IEchoReference Open(string? outputEndpointId, CancellationToken cancellationToken) =>
            OpenFailure is { } failure ? throw failure : new Reference(StartFailure);
    }

    // Speakers that play nothing: every read finds no packet waiting.
    private sealed class Reference(Exception? startFailure) : IEchoReference
    {
        public CaptureSourceFormat Format { get; } = new(16000, 1, 32, DeviceSampleEncoding.IeeeFloat);
        public void Start() { if (startFailure is not null) throw startFailure; }
        public CapturePacket Read(Span<byte> destination) => new(0);
        public void Stop() { }
        public void Dispose() { }
    }

    private sealed class Canceller : IEchoCanceller
    {
        internal volatile bool Fail;
        public void Process(ReadOnlySpan<float> speaker, Span<float> microphone)
        {
            if (Fail) throw new InvalidOperationException("PRIVATE canceller detail");
        }
        public void Dispose() { }
    }
}
