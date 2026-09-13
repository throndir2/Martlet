using System.Reflection;
using Martlet.Core.Contracts;
using static Martlet.Audio.Tests.CaptureNormalizerTests;

namespace Martlet.Audio.Tests;

public sealed class CaptureReviewTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(5);
    private static readonly Guid Session = Guid.Parse("650dcf81-6338-451d-a23f-418af7f715b8");
    private static CaptureRequest Request(CaptureClock clock) => new(
        new() { SessionId = Session, TurnId = Guid.NewGuid(), RequestId = Guid.NewGuid() }, 0,
        new(InputPolicy.FollowDefaultOnNextPress), TimeSpan.FromSeconds(30), clock.GetUtcNow().AddSeconds(30));

    [Theory]
    [InlineData("release")]
    [InlineData("sealed-cleanup")]
    [InlineData("take")]
    public async Task BlockedNewerCallerCallbackCannotAuthorizeUtteranceTransfer(string boundary)
    {
        var clock = new CaptureClock();
        using var caller = new CancellationTokenSource();
        using var blocker = new ManualResetEventSlim();
        using var callbackEntered = new ManualResetEventSlim();
        using var cleanup = new ManualResetEventSlim(boundary != "sealed-cleanup");
        var device = new ControlledCapture { DisposeBlock = cleanup };
        await using var capture = new MicrophoneCapture(Session, device, timeProvider: clock);
        var request = Request(clock);
        var run = capture.Press(request, new(request, true), caller.Token);
        await run.Ready.WaitAsync(WaitLimit);
        device.Packets.Enqueue(Pcm(Enumerable.Repeat((short)1234, 320).ToArray()));
        await CaptureTests.Until(() => run.Snapshot.CanonicalSamples == 320);
        // CancellationToken callbacks are LIFO. The capture registration must not be the authority.
        using var registration = caller.Token.Register(() => { callbackEntered.Set(); blocker.Wait(); });
        Task? cancel = null;
        try
        {
            if (boundary == "take") await run.ReleaseAsync().WaitAsync(WaitLimit);
            if (boundary == "sealed-cleanup")
            {
                _ = run.ReleaseAsync();
                await CaptureTests.Until(() => device.DisposeEntered.IsSet);
            }
            cancel = Task.Run(caller.Cancel);
            await CaptureTests.Until(() => callbackEntered.IsSet);
            Assert.True(caller.IsCancellationRequested);
            cleanup.Set();
            var result = await run.ReleaseAsync().WaitAsync(WaitLimit);
            using var utterance = run.TakeUtterance();
            Assert.Null(utterance);
            Assert.Equal(0, run.Snapshot.RetainedPcmBytes);
            if (boundary != "take")
            {
                Assert.Equal(CaptureState.Canceled, result.State);
                Assert.Equal(CaptureEndReason.CallerCanceled, result.EndReason);
            }
            Assert.True((await run.DeviceRelease.WaitAsync(WaitLimit)).Released);
            Assert.False(cancel.IsCompleted);
        }
        finally
        {
            cleanup.Set();
            blocker.Set();
            if (cancel is not null) await cancel.WaitAsync(WaitLimit);
            await run.CancelAsync().WaitAsync(WaitLimit);
        }
    }

    [Fact]
    public async Task BlockedNewerCallerCallbackCannotAdmitLatePacket()
    {
        var clock = new CaptureClock();
        using var caller = new CancellationTokenSource();
        using var blocker = new ManualResetEventSlim();
        using var callbackEntered = new ManualResetEventSlim();
        using var read = new ManualResetEventSlim();
        var device = new ControlledCapture { ReadBlock = read };
        await using var capture = new MicrophoneCapture(Session, device, timeProvider: clock);
        var request = Request(clock);
        var run = capture.Press(request, new(request, true), caller.Token);
        await run.Ready.WaitAsync(WaitLimit);
        await CaptureTests.Until(() => device.ReadBlocked.IsSet);
        using var registration = caller.Token.Register(() => { callbackEntered.Set(); blocker.Wait(); });
        var cancel = Task.Run(caller.Cancel);
        try
        {
            await CaptureTests.Until(() => callbackEntered.IsSet);
            device.Packets.Enqueue(Pcm(1234));
            read.Set();
            await CaptureTests.Until(() => run.Completion.IsCompleted || run.Snapshot.CanonicalSamples != 0);
            var result = await run.ReleaseAsync().WaitAsync(WaitLimit);
            Assert.Equal(0, result.CanonicalSamples);
            Assert.Equal(CaptureState.Canceled, result.State);
            Assert.Null(run.TakeUtterance());
        }
        finally
        {
            read.Set();
            blocker.Set();
            await cancel.WaitAsync(WaitLimit);
            await run.CancelAsync().WaitAsync(WaitLimit);
        }
    }

    [Theory]
    [InlineData("caller")]
    [InlineData("utc")]
    public async Task LiveFramePullRejectsRevocationWithoutWaitingForReadOrCallerCallback(string cause)
    {
        var clock = new CaptureClock();
        using var caller = new CancellationTokenSource();
        using var blocker = new ManualResetEventSlim();
        using var callbackEntered = new ManualResetEventSlim();
        using var read = new ManualResetEventSlim();
        var device = new ControlledCapture();
        await using var capture = new MicrophoneCapture(Session, device, timeProvider: clock);
        var request = Request(clock);
        var run = capture.Press(request, new(request, true), caller.Token);
        await run.Ready.WaitAsync(WaitLimit);
        device.Packets.Enqueue(Pcm(Enumerable.Repeat((short)1234, 320).ToArray()));
        await CaptureTests.Until(() => run.Snapshot.CanonicalSamples == 320);
        using var registration = caller.Token.Register(() => { callbackEntered.Set(); blocker.Wait(); });
        Task? cancel = null;
        try
        {
            device.ReadBlock = read;
            await CaptureTests.Until(() => device.ReadBlocked.IsSet);
            if (cause == "caller")
            {
                cancel = Task.Run(caller.Cancel);
                await CaptureTests.Until(() => callbackEntered.IsSet);
            }
            else clock.ShiftUtc(TimeSpan.FromSeconds(31));
            var destination = Enumerable.Repeat((byte)42, 640).ToArray();
            Assert.Throws<OperationCanceledException>(() => run.TryCopyMonoFrame(0, destination));
            Assert.All(destination, value => Assert.Equal(0, value));
            Assert.Equal(0, run.Snapshot.RetainedPcmBytes);
            read.Set();
            var result = await run.Completion.WaitAsync(WaitLimit);
            Assert.Equal(cause == "caller" ? CaptureState.Canceled : CaptureState.Failed, result.State);
            Assert.Null(run.TakeUtterance());
        }
        finally
        {
            read.Set();
            blocker.Set();
            if (cancel is not null) await cancel.WaitAsync(WaitLimit);
            await run.CancelAsync().WaitAsync(WaitLimit);
        }
    }

    [Theory]
    [InlineData("caller", "open")]
    [InlineData("caller", "start")]
    [InlineData("utc", "open")]
    [InlineData("utc", "start")]
    public async Task RevocationIsRecheckedAtNativeAuthorizationBoundary(string cause, string boundary)
    {
        var clock = new CaptureClock();
        using var caller = new CancellationTokenSource();
        using var blocker = new ManualResetEventSlim();
        using var callbackEntered = new ManualResetEventSlim();
        using var open = new ManualResetEventSlim();
        using var opening = new ManualResetEventSlim();
        var device = new ControlledCapture();
        if (boundary == "open") device.BeforeOpenAuthorization = () => { opening.Set(); open.Wait(); };
        else device.OpenBlock = open;
        await using var capture = new MicrophoneCapture(Session, device, timeProvider: clock);
        var request = Request(clock);
        var run = capture.Press(request, new(request, true), caller.Token);
        using var registration = caller.Token.Register(() => { callbackEntered.Set(); blocker.Wait(); });
        Task? cancel = null;
        try
        {
            await CaptureTests.Until(() => boundary == "open" ? opening.IsSet : device.OpenEntered.IsSet);
            if (cause == "caller")
            {
                cancel = Task.Run(caller.Cancel);
                await CaptureTests.Until(() => callbackEntered.IsSet);
            }
            else
            {
                clock.ShiftUtc(TimeSpan.FromSeconds(31));
                Assert.Equal(0, clock.GetTimestamp());
            }
            open.Set();
            await CaptureTests.Until(() => run.Completion.IsCompleted || device.Starts != 0);
            var result = await run.ReleaseAsync().WaitAsync(WaitLimit);
            Assert.Equal(0, device.Starts);
            Assert.Equal(boundary == "open" ? 0 : 1, device.Opens);
            Assert.Equal(cause == "caller" ? CaptureState.Canceled : CaptureState.Failed, result.State);
            if (cause == "utc") Assert.Equal(ErrorCode.DeadlineExceeded, result.Error!.Code);
            if (cause == "utc") Assert.Equal(CaptureEndReason.AuthorizationExpired, result.EndReason);
            Assert.Null(run.TakeUtterance());
        }
        finally
        {
            open.Set();
            blocker.Set();
            if (cancel is not null) await cancel.WaitAsync(WaitLimit);
            await run.CancelAsync().WaitAsync(WaitLimit);
        }
    }

    [Theory]
    [InlineData("packet")]
    [InlineData("release")]
    [InlineData("take")]
    public async Task IndependentUtcExpiryInvalidatesAdmissionSealAndUnclaimedTransfer(string boundary)
    {
        var clock = new CaptureClock();
        using var read = new ManualResetEventSlim();
        var device = new ControlledCapture();
        await using var capture = new MicrophoneCapture(Session, device, timeProvider: clock);
        var request = Request(clock);
        var run = capture.Press(request, new(request, true));
        await run.Ready.WaitAsync(WaitLimit);
        device.Packets.Enqueue(Pcm(1234));
        await CaptureTests.Until(() => run.Snapshot.CanonicalSamples == 1);
        try
        {
            if (boundary == "take") await run.ReleaseAsync().WaitAsync(WaitLimit);
            else
            {
                device.ReadBlock = read;
                await CaptureTests.Until(() => device.ReadBlocked.IsSet);
            }
            clock.ShiftUtc(TimeSpan.FromSeconds(31));
            Assert.Equal(0, clock.GetTimestamp());
            if (boundary == "packet")
            {
                device.Packets.Enqueue(Pcm(4321));
                read.Set();
                await CaptureTests.Until(() => run.Completion.IsCompleted || run.Snapshot.CanonicalSamples > 1);
            }
            var ending = run.ReleaseAsync();
            read.Set();
            var result = await ending.WaitAsync(WaitLimit);
            using var utterance = run.TakeUtterance();
            Assert.Null(utterance);
            Assert.Equal(1, result.CanonicalSamples);
            Assert.Equal(0, run.Snapshot.RetainedPcmBytes);
            if (boundary != "take")
            {
                Assert.Equal(CaptureState.Failed, result.State);
                Assert.Equal(ErrorCode.DeadlineExceeded, result.Error!.Code);
                Assert.Equal(CaptureEndReason.AuthorizationExpired, result.EndReason);
            }
        }
        finally
        {
            read.Set();
            await run.CancelAsync().WaitAsync(WaitLimit);
        }
    }

    [Fact]
    public async Task UtcRollbackCannotExtendTheMonotonicWindowWithTimerDeliveryDeferred()
    {
        var clock = new CaptureClock();
        using var read = new ManualResetEventSlim();
        var device = new ControlledCapture { ReadBlock = read };
        await using var capture = new MicrophoneCapture(Session, device, timeProvider: clock);
        var request = Request(clock) with { MaximumDuration = TimeSpan.FromSeconds(1) };
        var run = capture.Press(request, new(request, true));
        await run.Ready.WaitAsync(WaitLimit);
        await CaptureTests.Until(() => device.ReadBlocked.IsSet);
        clock.ShiftUtc(TimeSpan.FromDays(-1));
        clock.Advance(TimeSpan.FromSeconds(2), deliverTimers: false);
        device.Packets.Enqueue(Pcm(1234));
        read.Set();
        var result = await run.Completion.WaitAsync(WaitLimit);
        Assert.Equal(CaptureEndReason.DurationLimit, result.EndReason);
        Assert.Equal(0, result.CanonicalSamples);
        Assert.Null(run.TakeUtterance());
    }

    [Fact]
    public async Task AbsoluteExpiryWinsWhenDurationAndAuthorizationExpireTogether()
    {
        var clock = new CaptureClock();
        using var read = new ManualResetEventSlim();
        var device = new ControlledCapture();
        await using var capture = new MicrophoneCapture(Session, device, timeProvider: clock);
        var request = Request(clock) with
        {
            MaximumDuration = TimeSpan.FromSeconds(1),
            ExpiresAt = clock.GetUtcNow().AddSeconds(1)
        };
        var run = capture.Press(request, new(request, true));
        await run.Ready.WaitAsync(WaitLimit);
        device.Packets.Enqueue(Pcm(1234));
        await CaptureTests.Until(() => run.Snapshot.CanonicalSamples == 1);
        try
        {
            device.ReadBlock = read;
            await CaptureTests.Until(() => device.ReadBlocked.IsSet);
            clock.Advance(TimeSpan.FromSeconds(1), deliverTimers: false);
            var ending = run.ReleaseAsync();
            read.Set();
            var result = await ending.WaitAsync(WaitLimit);
            Assert.Equal(CaptureState.Failed, result.State);
            Assert.Equal(CaptureEndReason.AuthorizationExpired, result.EndReason);
            Assert.Equal(ErrorCode.DeadlineExceeded, result.Error!.Code);
            Assert.Null(run.TakeUtterance());
        }
        finally
        {
            read.Set();
            await run.CancelAsync().WaitAsync(WaitLimit);
        }
    }

#if WINDOWS
    [Theory]
    [InlineData("default", ErrorCode.AudioDeviceChanged)]
    [InlineData("properties", ErrorCode.AudioDeviceChanged)]
    [InlineData("removed", ErrorCode.AudioDeviceLost)]
    [InlineData("state", ErrorCode.AudioDeviceLost)]
    public async Task RecordedNotificationIsObservedByActualNativeStopBeforeAnotherRead(string change, ErrorCode expected)
    {
        var clock = new CaptureClock();
        using var allowRead = new ManualResetEventSlim();
        var device = new NativeStopFacade(allowRead);
        await using var capture = new MicrophoneCapture(Session, device, timeProvider: clock);
        var request = Request(clock);
        var run = capture.Press(request, new(request, true));
        try
        {
            await run.Ready.WaitAsync(WaitLimit);
            await CaptureTests.Until(() => run.Snapshot.CanonicalSamples == 320 && device.NextReadEntered.IsSet);
            device.RecordNotification(change);
            var ending = run.ReleaseAsync();
            allowRead.Set();
            var result = await ending.WaitAsync(WaitLimit);
            using var utterance = run.TakeUtterance();
            Assert.Null(utterance);
            Assert.Equal(CaptureState.Failed, result.State);
            Assert.Equal(expected, result.Error!.Code);
            Assert.Equal(0, result.RetainedPcmBytes);
            Assert.Equal(2, device.Reads);
            Assert.Equal(1, device.NativeDisposals);
            Assert.True((await run.DeviceRelease.WaitAsync(WaitLimit)).Released);
        }
        finally
        {
            allowRead.Set();
            await run.CancelAsync().WaitAsync(WaitLimit);
        }
    }

    // The real private Windows Device.Stop/Dispose execute on the production run worker.
    // Only Start/Read supply offline PCM; native handles stay null, so no COM activation occurs.
    private sealed class NativeStopFacade(ManualResetEventSlim allowRead) : ICaptureDeviceFactory, ICaptureDevice
    {
        private ICaptureDevice? native;
        private object? notifications;
        private Type? notificationType;
        internal ManualResetEventSlim NextReadEntered { get; } = new();
        internal int Reads, NativeDisposals;
        public CaptureSourceFormat Format => new(16000, 1, 16, DeviceSampleEncoding.IntegerPcm);

        public ICaptureDevice Open(CaptureDeviceAccess access, CancellationToken cancellationToken)
        {
            access.CheckAuthorization();
            var deviceType = typeof(Windows.WasapiCaptureDeviceFactory).GetNestedType("Device", BindingFlags.NonPublic)!;
            native = (ICaptureDevice)Activator.CreateInstance(deviceType, access, (Action)(() => NativeDisposals++))!;
            notificationType = deviceType.Assembly.GetType("Martlet.Audio.Windows.NativeInputNotifications")!;
            notifications = Activator.CreateInstance(notificationType)!;
            notificationType.GetField("active", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(notifications, 1);
            notificationType.GetMethod("Bind")!.Invoke(notifications, new object[] { "selected-input", access.Input.Policy });
            deviceType.GetField("notifications", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(native, notifications);
            return this;
        }
        public void Start(CancellationToken cancellationToken) { }
        public CapturePacket Read(Span<byte> destination, CancellationToken cancellationToken)
        {
            if (++Reads == 1)
            {
                Pcm(Enumerable.Repeat((short)1234, 320).ToArray()).CopyTo(destination);
                return new(640);
            }
            NextReadEntered.Set();
            allowRead.Wait();
            return new(0);
        }
        public void Stop() => native!.Stop();
        public void Dispose() => native!.Dispose();

        internal void RecordNotification(string change)
        {
            var (method, args) = change switch
            {
                "default" => ("DefaultChanged", (object)new NAudio.CoreAudioApi.DefaultDeviceChangedEventArgs(
                    NAudio.CoreAudioApi.DataFlow.Capture, NAudio.CoreAudioApi.Role.Console, "replacement")),
                "properties" => ("PropertyChanged", new NAudio.CoreAudioApi.DevicePropertyChangedEventArgs("selected-input", default)),
                "removed" => ("Removed", new NAudio.CoreAudioApi.DeviceNotificationEventArgs("selected-input")),
                "state" => ("StateChanged", new NAudio.CoreAudioApi.DeviceStateChangedEventArgs("selected-input", NAudio.CoreAudioApi.DeviceState.Unplugged)),
                _ => throw new ArgumentOutOfRangeException(nameof(change))
            };
            notificationType!.GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(notifications, new[] { null, args });
        }
    }
#endif
}
