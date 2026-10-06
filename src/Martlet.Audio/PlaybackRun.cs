using System.Security.Cryptography;
using System.Threading.Channels;
using Martlet.Core.Audio;
using Martlet.Core.Contracts;

namespace Martlet.Audio;

public sealed class PlaybackRun
{
    private const int HistoryLimit = 256;
    private readonly object gate = new();
    private readonly PlaybackRequest request;
    private readonly PlaybackOptions options;
    private readonly TimeProvider time;
    private readonly long startedAt;
    private readonly TimeSpan deadline;
    private readonly CancellationTokenSource cancellation = new();
    private readonly TaskCompletionSource stop = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<PlaybackDeviceInfo?> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Queue<PcmFrame> queue = new();
    private readonly Dictionary<long, Fingerprint> history = new();
    private readonly Channel<PlaybackEvent> events;
    private readonly CancellationTokenRegistration callerCancellation;
    private long nextSequence, accepted, read, submitted, consumed, eventSequence, droppedEvents;
    private int frameByteOffset;
    private bool inputCompleted, prebuffered, started, drained, terminal;
    private bool deviceOpened, deviceReleased;
    private long? underrunAt;
    private int underruns;
    private TimeSpan underrunTime;
    private PlaybackState state = PlaybackState.Starting;
    private PlaybackState stopOutcome = PlaybackState.Canceled;
    private MartletError? error;
    private PlaybackDeviceInfo? deviceInfo;
    private PlaybackClockSnapshot clockSnapshot;
    private bool clockInvalidated;
    private ulong? lastClockPosition;
    private ulong lastClockFrequency;
    private readonly Func<double>? volume;

    internal PlaybackRun(IPlaybackDeviceFactory devices, PlaybackRequest request, PlaybackOptions options,
        TimeProvider time, CancellationToken callerToken, Func<double>? volume = null)
    {
        this.volume = volume;
        this.request = request;
        this.options = options;
        this.time = time;
        clockSnapshot = new(request.Ids, request.Epoch, request.Format.SampleRate,
            request.ObserveDeviceClock ? PlaybackClockState.Waiting : PlaybackClockState.NotRequested,
            null, 0, null);
        startedAt = time.GetTimestamp();
        deadline = request.Deadline - time.GetUtcNow();
        events = Channel.CreateBounded<PlaybackEvent>(new BoundedChannelOptions(128)
        {
            FullMode = BoundedChannelFullMode.DropOldest, SingleWriter = false, SingleReader = false,
            AllowSynchronousContinuations = false
        }, _ => Interlocked.Increment(ref droppedEvents));
        Emit(PlaybackEventKind.Starting);
        callerCancellation = callerToken.UnsafeRegister(_ => RequestStop(PlaybackState.Canceled), null);
        var worker = Task.Factory.StartNew(() => Drive(devices), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
        DeviceRelease = ObserveDeviceAsync(worker);
        Completion = SuperviseAsync();
    }

    public ChannelReader<PlaybackEvent> Events => events.Reader;
    public Task<PlaybackDeviceInfo?> Ready => ready.Task;
    public Task<PlaybackSnapshot> Completion { get; }
    // Completes only after worker-owned native teardown; an overdue worker prevents another Start.
    public Task<MartletError?> DeviceRelease { get; }
    internal bool WorkerReleased { get { lock (gate) return DeviceRelease.IsCompleted && deviceReleased; } }
    public PlaybackSnapshot Snapshot { get { lock (gate) return GetSnapshot(); } }
    public PlaybackClockSnapshot DeviceClock { get { lock (gate) return clockSnapshot; } }
    public TimeSpan? DeviceClockAge
    {
        get
        {
            lock (gate) return clockSnapshot.State == PlaybackClockState.Available
                ? time.GetElapsedTime(clockSnapshot.ObservedTimestamp) : null;
        }
    }

    public FrameAcceptance Submit(PcmFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        lock (gate)
        {
            if (terminal || stop.Task.IsCompleted || frame.Epoch < request.Epoch)
                return FrameAcceptance.StaleDiscarded;
            Require(frame.Epoch == request.Epoch && frame.Ids == request.Ids, ErrorCode.InvalidContract);
            Require(frame.Format == request.Format, ErrorCode.InvalidContract);
            var fingerprint = new Fingerprint(frame.SampleOffset, frame.Data.Length, SHA256.HashData(frame.Data.Span));
            if (frame.Sequence < nextSequence)
            {
                Require(history.TryGetValue(frame.Sequence, out var previous) && previous.Matches(fingerprint),
                    ErrorCode.InvalidContract);
                return FrameAcceptance.DuplicateDiscarded;
            }
            Require(!inputCompleted && frame.Sequence == nextSequence && frame.SampleOffset == accepted,
                ErrorCode.InvalidContract);
            var count = frame.SamplesPerChannel;
            Require(accepted + count <= Math.Min(4_320_000L, request.Format.SampleRate * 90L),
                ErrorCode.PayloadTooLarge);
            // Includes the device buffer and the pump's in-flight read, not just queued frame objects.
            Require(accepted - consumed + count <= (long)(request.Format.SampleRate * options.Capacity.TotalSeconds)
                && queue.Count < options.MaximumQueuedFrames, ErrorCode.PayloadTooLarge);
            queue.Enqueue(frame);
            history.Add(frame.Sequence, fingerprint);
            if (history.Count > HistoryLimit)
                history.Remove(frame.Sequence - HistoryLimit);
            accepted += count;
            nextSequence++;
            return FrameAcceptance.Accepted;
        }
    }

    public bool CompleteInput(long finalSampleCount)
    {
        lock (gate)
        {
            if (terminal || stop.Task.IsCompleted) return false;
            Require(finalSampleCount == accepted, ErrorCode.StreamTruncated);
            if (inputCompleted) return true;
            inputCompleted = true;
            Emit(PlaybackEventKind.InputCompleted);
            return true;
        }
    }

    public Task<PlaybackSnapshot> StopAsync(bool replaced = false)
    {
        RequestStop(replaced ? PlaybackState.Replaced : PlaybackState.Canceled);
        return Completion;
    }

    private void Require(bool condition, ErrorCode code)
    {
        if (condition) return;
        var failure = PlaybackErrors.Create(code);
        RequestStop(PlaybackState.Failed, failure);
        throw new ContractException(code, failure.Summary);
    }

    private void RequestStop(PlaybackState outcome, MartletError? failure = null)
    {
        lock (gate)
        {
            if (terminal || stop.Task.IsCompleted) return;
            stopOutcome = outcome;
            error = failure;
            state = PlaybackState.Stopping;
            EndUnderrun();
            InvalidateClock(PlaybackClockState.Stopped);
            queue.Clear();
            history.Clear();
            frameByteOffset = 0;
            stop.TrySetResult();
            Emit(PlaybackEventKind.StopRequested);
        }
    }

    private void CheckActive()
    {
        if (stop.Task.IsCompleted)
            throw new OperationCanceledException(cancellation.Token);
        cancellation.Token.ThrowIfCancellationRequested();
    }

    private int Read(Span<byte> destination, out bool end)
    {
        lock (gate)
        {
            CheckActive();
            if (!prebuffered)
                prebuffered = inputCompleted || accepted - read >= Math.Ceiling(request.Format.SampleRate * options.Prebuffer.TotalSeconds);
            var written = 0;
            while (prebuffered && queue.TryPeek(out var frame) && written < destination.Length)
            {
                var count = Math.Min(destination.Length - written, frame.Data.Length - frameByteOffset);
                frame.Data.Span.Slice(frameByteOffset, count).CopyTo(destination[written..]);
                written += count;
                frameByteOffset += count;
                if (frameByteOffset == frame.Data.Length)
                {
                    queue.Dequeue();
                    frameByteOffset = 0;
                }
            }
            read += written / request.Format.BlockAlignment;
            end = inputCompleted && queue.Count == 0 && written == 0;
            return written;
        }
    }

    private void Drive(IPlaybackDeviceFactory devices)
    {
        CheckActive();
        var device = devices.Open(request.Output, request.Format, cancellation.Token);
        lock (gate) deviceOpened = true;
        try
        {
            CheckActive();
            var info = device.Info;
            if (info.BufferCapacitySamples <= 0 || info.BufferCapacitySamples > request.Format.SampleRate / 5
                || info.MixSampleRate is < 8000 or > 192000 || info.MixChannels is < 1 or > 8
                || info.MixBitsPerSample is not (16 or 24 or 32) || !Enum.IsDefined(info.MixEncoding)
                || (info.MixEncoding == DeviceSampleEncoding.IeeeFloat && info.MixBitsPerSample != 32))
                throw new ContractException(ErrorCode.AudioFormatUnsupported, "The device format or buffer exceeds the supported bounds.");
            lock (gate)
            {
                CheckActive();
                deviceInfo = info;
                state = PlaybackState.Buffering;
                ready.TrySetResult(info);
                Emit(PlaybackEventKind.Bound);
            }
            var alignment = request.Format.BlockAlignment;
            var buffer = new byte[Math.Min(info.BufferCapacitySamples, request.Format.SampleRate / 10) * alignment];
            var pending = 0;
            var offset = 0;
            long committed = 0;
            while (true)
            {
                CheckActive();
                var padding = device.GetPadding(cancellation.Token);
                if (padding < 0 || padding > info.BufferCapacitySamples || padding > committed)
                    throw new ContractException(ErrorCode.AudioPlaybackFailed, "The device returned invalid sample accounting.");
                if (started && padding == 0)
                {
                    lock (gate) InvalidateClock(PlaybackClockState.Underrun);
                }
                ReportProgress(committed, committed - padding);
                if (started && request.ObserveDeviceClock && !clockInvalidated)
                    ObserveClock(device, committed);
                var available = info.BufferCapacitySamples - padding;
                var end = false;
                if (pending == 0 && available > 0)
                {
                    pending = Read(buffer.AsSpan(0, Math.Min(available * alignment, buffer.Length)), out end);
                    // The volume is read for each device buffer (at most 100 ms), so a change is heard almost at once.
                    if (volume is not null) PcmGain.Apply(buffer.AsSpan(0, pending), volume());
                    offset = 0;
                }
                if (pending > 0 && available > 0)
                {
                    CheckActive();
                    var bytes = Math.Min(pending, available * alignment);
                    var written = device.Write(buffer.AsSpan(offset, bytes), cancellation.Token);
                    if (written <= 0 || written > bytes / alignment)
                        throw new ContractException(ErrorCode.AudioPlaybackFailed, "The device returned an invalid write count.");
                    committed += written;
                    offset += written * alignment;
                    pending -= written * alignment;
                    ReportProgress(committed, committed - padding - written);
                    CheckActive();
                    if (!started)
                    {
                        device.Start(cancellation.Token);
                        lock (gate)
                        {
                            started = true;
                            if (!stop.Task.IsCompleted)
                                state = PlaybackState.Playing;
                            Emit(PlaybackEventKind.Progress);
                        }
                    }
                    lock (gate)
                    {
                        if (underrunAt is not null && !stop.Task.IsCompleted)
                        {
                            EndUnderrun();
                            state = PlaybackState.Playing;
                            Emit(PlaybackEventKind.Resumed);
                        }
                    }
                }
                else if (end && pending == 0 && padding == 0)
                {
                    lock (gate)
                    {
                        CheckActive();
                        drained = true;
                    }
                    return;
                }
                else if (started && padding == 0 && pending == 0)
                {
                    lock (gate)
                    {
                        if (underrunAt is null && !inputCompleted && !stop.Task.IsCompleted)
                        {
                            underrunAt = time.GetTimestamp();
                            underruns++;
                            InvalidateClock(PlaybackClockState.Underrun);
                            state = PlaybackState.Underrun;
                            Emit(PlaybackEventKind.Underrun);
                        }
                    }
                }
                cancellation.Token.WaitHandle.WaitOne(10);
            }
        }
        finally
        {
            // Only this worker touches Stop/Reset/Dispose, including interrupted writes and open/stop races.
            try { device.StopAndReset(); }
            finally
            {
                device.Dispose();
                lock (gate) deviceReleased = true;
            }
        }
    }

    private void ReportProgress(long committed, long played)
    {
        lock (gate)
        {
            if (terminal) return;
            if (committed < submitted || played < consumed || played > committed || committed > read)
                throw new ContractException(ErrorCode.AudioPlaybackFailed, "The device returned invalid sample accounting.");
            if (committed == submitted && played == consumed) return;
            submitted = committed;
            consumed = played;
            Emit(PlaybackEventKind.Progress);
        }

    }

    private void ObserveClock(IPlaybackDevice device, long committed)
    {
        DeviceClockReading? reading;
        try { reading = (device as IPlaybackClockDevice)?.ReadClock(cancellation.Token); }
        catch (OperationCanceledException) when (stop.Task.IsCompleted) { return; }
        catch (Exception)
        {
            // An optional external/native clock is an independent failure domain.
            lock (gate) InvalidateClock(PlaybackClockState.Unavailable);
            return;
        }
        lock (gate)
        {
            if (terminal || stop.Task.IsCompleted || clockInvalidated) return;
            if (reading is null || reading.Frequency == 0 || !Enum.IsDefined(reading.Origin))
            {
                InvalidateClock(PlaybackClockState.Unavailable);
                return;
            }
            if (lastClockPosition is { } raw && (reading.Position < raw || reading.Frequency != lastClockFrequency))
            {
                InvalidateClock(PlaybackClockState.Regressed);
                return;
            }
            var converted = (UInt128)reading.Position * (uint)request.Format.SampleRate / reading.Frequency;
            if (converted > long.MaxValue)
            {
                InvalidateClock(PlaybackClockState.Unavailable);
                return;
            }
            lastClockPosition = reading.Position;
            lastClockFrequency = reading.Frequency;
            // Never animate source samples not yet committed, even if the endpoint advances through silence.
            long offset = Math.Min((long)converted, committed);
            clockSnapshot = new(request.Ids, request.Epoch, request.Format.SampleRate,
                PlaybackClockState.Available, offset, time.GetTimestamp(), reading.Origin);
        }
    }

    private void InvalidateClock(PlaybackClockState reason)
    {
        if (!request.ObserveDeviceClock || clockInvalidated) return;
        clockInvalidated = true;
        clockSnapshot = clockSnapshot with { State = reason, SampleOffset = null };
    }

    private async Task<MartletError?> ObserveDeviceAsync(Task worker)
    {
        MartletError? failure = null;
        try
        {
            await worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.Task.IsCompleted) { }
        catch (ContractException ex)
        {
            failure = PlaybackErrors.Create(ex.Code is ErrorCode.AudioDeviceUnavailable or ErrorCode.AudioDeviceLost
                or ErrorCode.AudioFormatUnsupported or ErrorCode.AudioPlaybackFailed ? ex.Code : ErrorCode.AudioPlaybackFailed);
        }
        // Last-resort device boundary: report failure, never expose native messages or manufacture completion.
        catch (Exception) { failure = PlaybackErrors.Create(ErrorCode.AudioPlaybackFailed); }
        lock (gate)
        {
            // Open's contract releases resources on failure. An unexpected failure remains quarantined.
            if (!deviceOpened && failure?.Code != ErrorCode.AudioPlaybackFailed)
                deviceReleased = true;
        }
        return failure;
    }

    private async Task<PlaybackSnapshot> SuperviseAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10), time);
        while (!DeviceRelease.IsCompleted && !stop.Task.IsCompleted)
        {
            var tick = timer.WaitForNextTickAsync().AsTask();
            await Task.WhenAny(DeviceRelease, stop.Task, tick).ConfigureAwait(false);
            lock (gate)
            {
                var elapsed = time.GetElapsedTime(startedAt);
                if (elapsed >= deadline || (!started && elapsed >= options.FirstAudioTimeout))
                    RequestStop(PlaybackState.Failed, PlaybackErrors.Create(ErrorCode.DeadlineExceeded));
                else if (underrunAt is { } since && time.GetElapsedTime(since) >= options.UnderrunTimeout)
                    RequestStop(PlaybackState.Failed, PlaybackErrors.Create(ErrorCode.StreamTruncated));
            }
        }

        Task cancelCallbacks = Task.CompletedTask;
        var releaseTimedOut = false;
        if (stop.Task.IsCompleted)
        {
            cancelCallbacks = cancellation.CancelAsync();
            try
            {
                await Task.WhenAll(DeviceRelease, cancelCallbacks).WaitAsync(options.ShutdownTimeout, time).ConfigureAwait(false);
            }
            catch (TimeoutException) { releaseTimedOut = true; }
            catch (Exception) { error = PlaybackErrors.Create(ErrorCode.AudioPlaybackFailed); }
        }
        var releaseError = DeviceRelease.IsCompleted ? await DeviceRelease.ConfigureAwait(false) : null;
        lock (gate)
        {
            error = releaseTimedOut ? PlaybackErrors.Create(ErrorCode.AudioPlaybackFailed) : releaseError ?? error;
            if (error is not null)
                state = PlaybackState.Failed;
            else if (stop.Task.IsCompleted)
                state = stopOutcome;
            else if (drained && inputCompleted && consumed == accepted)
                state = PlaybackState.Completed;
            else
            {
                state = PlaybackState.Failed;
                error = PlaybackErrors.Create(ErrorCode.StreamTruncated);
            }
            terminal = true;
            EndUnderrun();
            InvalidateClock(PlaybackClockState.Stopped);
            queue.Clear();
            history.Clear();
            ready.TrySetResult(null);
            Emit(PlaybackEventKind.Terminal);
        }
        callerCancellation.Dispose();
        _ = FinishReleaseAsync(cancelCallbacks);
        return Snapshot;
    }

    private async Task FinishReleaseAsync(Task cancelCallbacks)
    {
        var releaseError = await DeviceRelease.ConfigureAwait(false);
        try { await cancelCallbacks.ConfigureAwait(false); }
        catch (Exception) { releaseError = PlaybackErrors.Create(ErrorCode.AudioPlaybackFailed); }
        lock (gate)
        {
            if (releaseError is not null)
                Emit(PlaybackEventKind.LateDeviceFailure);
            if (deviceReleased)
                Emit(PlaybackEventKind.DeviceReleased);
            events.Writer.TryComplete();
        }
        cancellation.Dispose();
    }

    private PlaybackSnapshot GetSnapshot() => new(request.Ids, request.Epoch, state, accepted, read,
        submitted, consumed, terminal || stop.Task.IsCompleted ? 0 : accepted - read,
        queue.Count, drained, deviceReleased, Interlocked.Read(ref droppedEvents), error)
    {
        Underruns = underruns,
        UnderrunTime = underrunTime + (underrunAt is { } since ? time.GetElapsedTime(since) : TimeSpan.Zero)
    };

    // Ends the wait for more audio that is going on, adding how long it lasted. Called with the gate held.
    private void EndUnderrun()
    {
        if (underrunAt is not { } since) return;
        underrunTime += time.GetElapsedTime(since);
        underrunAt = null;
    }

    private void Emit(PlaybackEventKind kind) => events.Writer.TryWrite(new(++eventSequence,
        time.GetUtcNow(), time.GetElapsedTime(startedAt), kind, GetSnapshot(), deviceInfo));

    private sealed record Fingerprint(long Offset, int Length, byte[] Hash)
    {
        internal bool Matches(Fingerprint other) =>
            Offset == other.Offset && Length == other.Length && Hash.AsSpan().SequenceEqual(other.Hash);
    }
}
