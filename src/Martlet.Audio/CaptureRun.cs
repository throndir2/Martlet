using System.Security.Cryptography;
using System.Threading.Channels;
using Martlet.Core.Contracts;

namespace Martlet.Audio;

public sealed class CaptureRun
{
    private readonly object gate = new();
    private readonly CaptureRequest request;
    private readonly CaptureOptions options;
    private readonly TimeProvider time;
    private readonly long startedAt;
    private readonly TimeSpan authorizationLifetime;
    private readonly int capacity;
    private readonly CancellationTokenSource cancellation = new();
    private readonly TaskCompletionSource stop = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<CaptureSourceFormat?> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<CaptureSnapshot> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<CaptureDeviceRelease> nativeRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<CaptureDeviceRelease> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<CaptureEvent> events;
    private readonly CancellationToken callerToken;
    private readonly CancellationTokenRegistration callerCancellation;
    private readonly ITimer timer;
    private readonly byte[] normalized = new byte[3264];
    private CaptureNormalizer? normalizer;
    private CaptureSourceFormat? sourceFormat;
    private byte[]? pcm;
    private CapturedUtterance? utterance;
    private int byteCount;
    private long sourceSamples, canonicalSamples, sequence, droppedEvents;
    private CaptureState state = CaptureState.Starting;
    private CaptureEndReason? endReason;
    private MartletError? error;
    private bool preserve, terminal;
    private CaptureSnapshot? terminalSnapshot;

    internal CaptureRun(ICaptureDeviceFactory devices, CaptureRequest request, CaptureOptions options, TimeProvider time, CancellationToken callerToken)
    {
        this.request = request;
        this.options = options;
        this.time = time;
        this.callerToken = callerToken;
        startedAt = time.GetTimestamp();
        authorizationLifetime = request.ExpiresAt - time.GetUtcNow();
        var stopAfter = TimeSpan.FromTicks(Math.Min(request.MaximumDuration.Ticks, authorizationLifetime.Ticks));
        capacity = (int)Math.Min(options.MaximumPcmBytes, request.MaximumDuration.Ticks * 16000 / TimeSpan.TicksPerSecond * 2);
        events = Channel.CreateBounded<CaptureEvent>(new BoundedChannelOptions(128)
        {
            FullMode = BoundedChannelFullMode.DropOldest, AllowSynchronousContinuations = false
        }, _ => Interlocked.Increment(ref droppedEvents));
        Emit(CaptureEventKind.Starting);
        callerCancellation = callerToken.UnsafeRegister(_ => End(CaptureEndReason.CallerCanceled, false), null);
        timer = time.CreateTimer(_ => End(CaptureEndReason.DurationLimit, true), null,
            stopAfter > TimeSpan.Zero ? stopAfter : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        _ = Task.Factory.StartNew(() => Drive(devices), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        _ = SuperviseAsync();
    }

    public Task<CaptureSourceFormat?> Ready => ready.Task;
    public Task<CaptureSnapshot> Completion => completion.Task;
    public Task<CaptureDeviceRelease> DeviceRelease => release.Task;
    public ChannelReader<CaptureEvent> Events => events.Reader;
    public CaptureSnapshot Snapshot
    {
        get
        {
            lock (gate)
                return terminalSnapshot is { } snapshot
                    ? snapshot with { RetainedPcmBytes = utterance?.ByteCount ?? 0 } : GetSnapshot();
        }
    }
    internal bool WorkerReleased => release.Task.IsCompletedSuccessfully && release.Task.Result.Released;

    // Optional pull seam for future VAD: caller supplies one 20 ms buffer; no second audio queue.
    // Offsets are canonical samples for this run's IDs/epoch. Stop makes all further reads unavailable.
    public bool TryCopyMonoFrame(int frameIndex, Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frameIndex);
        if (destination.Length != 640) throw new ArgumentException("A mono frame destination must be exactly 640 bytes.", nameof(destination));
        lock (gate)
        {
            destination.Clear();
            if (stop.Task.IsCompleted || terminal) return false;
            CheckAuthorization();
            var offset = checked((long)frameIndex * 640);
            if (offset + 640 > byteCount) return false;
            pcm.AsSpan((int)offset, 640).CopyTo(destination);
            if (AuthorizationRevoked(out var reason, out var failure))
            {
                destination.Clear();
                End(reason, false, failure);
                return false;
            }
            return true;
        }
    }

    public Task<CaptureSnapshot> ReleaseAsync()
    {
        lock (gate)
            End(time.GetElapsedTime(startedAt) >= request.MaximumDuration ? CaptureEndReason.DurationLimit : CaptureEndReason.Released, true);
        return Completion;
    }

    public Task<CaptureSnapshot> CancelAsync(CaptureEndReason reason = CaptureEndReason.Stopped)
    {
        if (reason is not (CaptureEndReason.Stopped or CaptureEndReason.Muted or CaptureEndReason.Paused
            or CaptureEndReason.Locked or CaptureEndReason.Disposed or CaptureEndReason.CallerCanceled))
            throw new ArgumentOutOfRangeException(nameof(reason));
        End(reason, false);
        return Completion;
    }

    // Single ownership transfer. Completion/events contain metadata only, not a retained audio payload.
    public CapturedUtterance? TakeUtterance()
    {
        lock (gate)
        {
            if (!terminal) throw new InvalidOperationException("Await capture completion before taking the utterance.");
            if (AuthorizationRevoked(out var reason, out var failure))
                End(reason, false, failure);
            var result = utterance;
            utterance = null;
            return result;
        }
    }

    internal void DiscardUnclaimedUtterance()
    {
        lock (gate)
        {
            utterance?.Dispose();
            utterance = null;
        }
    }

    private void CheckAuthorization()
    {
        lock (gate)
        {
            if (AuthorizationRevoked(out var reason, out var failure))
                End(reason, false, failure);
            else if (!stop.Task.IsCompleted && time.GetElapsedTime(startedAt) >= request.MaximumDuration)
                End(CaptureEndReason.DurationLimit, true);
            if (stop.Task.IsCompleted) throw new OperationCanceledException(cancellation.Token);
        }
    }

    private bool AuthorizationRevoked(out CaptureEndReason reason, out MartletError? failure)
    {
        // Callback dispatch may be delayed by a newer blocking registration. The original token
        // and both consent clocks remain authoritative, separately from the capture-duration cap.
        var expired = time.GetUtcNow() >= request.ExpiresAt
            || time.GetElapsedTime(startedAt) >= authorizationLifetime;
        var callerCanceled = callerToken.IsCancellationRequested;
        reason = callerCanceled ? CaptureEndReason.CallerCanceled
            : expired ? CaptureEndReason.AuthorizationExpired : default;
        failure = reason == CaptureEndReason.AuthorizationExpired ? CaptureErrors.Create(ErrorCode.DeadlineExceeded) : null;
        return callerCanceled || expired;
    }

    private void End(CaptureEndReason reason, bool keepAudio, MartletError? failure = null)
    {
        lock (gate)
        {
            if (keepAudio && AuthorizationRevoked(out var revokedReason, out var revokedFailure))
            {
                reason = revokedReason;
                keepAudio = false;
                failure = revokedFailure;
            }
            if (terminal)
            {
                if (!keepAudio) DiscardUnclaimedUtterance();
                return;
            }
            if (stop.Task.IsCompleted && (keepAudio || !preserve)) return;
            preserve = keepAudio;
            endReason = reason;
            error = failure;
            state = CaptureState.Stopping;
            if (keepAudio && normalizer is not null)
            {
                try
                {
                    var bytes = normalizer.Complete(normalized);
                    Store(normalized.AsSpan(0, bytes));
                }
                catch (CaptureDeviceException ex)
                {
                    preserve = false;
                    error = CaptureErrors.Create(ex.Code);
                    endReason = CaptureEndReason.DeviceFailure;
                }
            }
            normalizer?.Dispose();
            normalizer = null;
            CryptographicOperations.ZeroMemory(normalized);
            if (!preserve) ClearPcm();
            stop.TrySetResult();
            Emit(CaptureEventKind.StopRequested);
        }
    }

    private void Store(ReadOnlySpan<byte> bytes)
    {
        var count = Math.Min(bytes.Length, capacity - byteCount);
        if (count > 0)
        {
            bytes[..count].CopyTo(pcm.AsSpan(byteCount));
            byteCount += count;
            canonicalSamples = byteCount / 2;
        }
    }

    private void Accept(ReadOnlySpan<byte> source)
    {
        lock (gate)
        {
            CheckAuthorization();
            var converter = normalizer!;
            var format = sourceFormat!;
            var maxSource = request.MaximumDuration.Ticks * format.SampleRate / TimeSpan.TicksPerSecond;
            var remaining = (maxSource - converter.SourceSamples) * format.BlockAlignment - converter.PendingSourceBytes;
            var bytes = (int)Math.Min(source.Length, remaining);
            var before = converter.SourceSamples;
            var converted = converter.Convert(source[..bytes], normalized);
            if (AuthorizationRevoked(out var reason, out var failure))
            {
                End(reason, false, failure);
                throw new OperationCanceledException(cancellation.Token);
            }
            sourceSamples = converter.SourceSamples;
            Store(normalized.AsSpan(0, converted));
            CryptographicOperations.ZeroMemory(normalized);
            if (sourceSamples > before) Emit(CaptureEventKind.Meter, converter.LastPeak, converter.LastRms);
            if (byteCount == capacity)
                End(capacity < request.MaximumDuration.Ticks * 16000 / TimeSpan.TicksPerSecond * 2
                    ? CaptureEndReason.ByteLimit : CaptureEndReason.DurationLimit, true);
            else if (converter.SourceSamples >= maxSource)
                End(CaptureEndReason.DurationLimit, true);
        }
    }

    private void Drive(ICaptureDeviceFactory devices)
    {
        ICaptureDevice? device = null;
        byte[]? scratch = null;
        MartletError? failure = null;
        var released = true;
        try
        {
            CheckAuthorization();
            var access = new CaptureDeviceAccess(request.Input, CheckAuthorization);
            device = devices.Open(access, cancellation.Token);
            CheckAuthorization();
            var format = device.Format;
            format.Validate();
            lock (gate)
            {
                CheckAuthorization();
                sourceFormat = format;
                normalizer = new(format);
                pcm = new byte[capacity];
                scratch = new byte[format.MaximumPacketBytes];
            }
            CheckAuthorization();
            device.Start(cancellation.Token);
            lock (gate)
            {
                CheckAuthorization();
                state = CaptureState.Capturing;
                ready.TrySetResult(format);
                Emit(CaptureEventKind.Bound);
            }
            while (true)
            {
                CheckAuthorization();
                var packet = device.Read(scratch, cancellation.Token);
                CheckAuthorization();
                if (packet.Discontinuity) throw new CaptureDeviceException(ErrorCode.StreamTruncated);
                if (packet.ByteCount < 0 || packet.ByteCount > scratch.Length)
                    throw new CaptureDeviceException(ErrorCode.PayloadTooLarge);
                if (packet.ByteCount != 0)
                {
                    try { Accept(scratch.AsSpan(0, packet.ByteCount)); }
                    finally { CryptographicOperations.ZeroMemory(scratch); }
                }
                else Thread.Sleep(5);
            }
        }
        catch (OperationCanceledException) when (stop.Task.IsCompleted) { }
        // Device boundary: never return native messages or success-shaped fallback audio.
        catch (Exception ex)
        {
            failure = CaptureErrors.Normalize(ex);
            if (device is null) released = ex is CaptureDeviceException { ResourcesReleased: true };
            End(CaptureEndReason.DeviceFailure, false, failure);
        }
        finally
        {
            if (scratch is not null) CryptographicOperations.ZeroMemory(scratch);
            if (device is not null)
            {
                try { device.Stop(); }
                catch (Exception ex) { failure = CaptureErrors.Normalize(ex); }
                try { device.Dispose(); }
                catch (Exception ex)
                {
                    released = false;
                    failure = CaptureErrors.Normalize(ex);
                }
            }
            nativeRelease.TrySetResult(new(released, failure));
        }
    }

    private async Task SuperviseAsync()
    {
        await Task.WhenAny(stop.Task, nativeRelease.Task).ConfigureAwait(false);
        var cancelCallbacks = cancellation.CancelAsync();
        _ = ObserveReleaseAsync(cancelCallbacks);
        CaptureDeviceRelease? deviceResult = null;
        try { deviceResult = await DeviceRelease.WaitAsync(options.ShutdownTimeout, time).ConfigureAwait(false); }
        catch (TimeoutException) { }
        lock (gate)
        {
            if (preserve && AuthorizationRevoked(out var reason, out var failure))
                End(reason, false, failure);
            if (deviceResult is null || deviceResult.Error is not null || !deviceResult.Released)
            {
                preserve = false;
                error = deviceResult?.Error ?? CaptureErrors.Create(ErrorCode.AudioCaptureFailed);
            }
            state = error is not null ? CaptureState.Failed : !preserve ? CaptureState.Canceled
                : byteCount == 0 ? CaptureState.NoFrames : CaptureState.Completed;
            if (state == CaptureState.Completed)
            {
                utterance = new(request, sourceFormat!, sourceSamples, pcm!, byteCount);
                pcm = null;
            }
            ClearPcm();
            normalizer?.Dispose();
            normalizer = null;
            CryptographicOperations.ZeroMemory(normalized);
            terminal = true;
            ready.TrySetResult(null);
            Emit(CaptureEventKind.Terminal);
            terminalSnapshot = GetSnapshot();
            events.Writer.TryComplete();
        }
        timer.Dispose();
        callerCancellation.Dispose();
        completion.TrySetResult(terminalSnapshot);
    }

    private async Task ObserveReleaseAsync(Task cancelCallbacks)
    {
        var result = await nativeRelease.Task.ConfigureAwait(false);
        try { await cancelCallbacks.ConfigureAwait(false); }
        catch (Exception) { result = result with { Error = CaptureErrors.Create(ErrorCode.AudioCaptureFailed) }; }
        cancellation.Dispose();
        release.TrySetResult(result);
    }

    private void ClearPcm()
    {
        if (pcm is not null) CryptographicOperations.ZeroMemory(pcm);
        pcm = null;
    }

    private CaptureSnapshot GetSnapshot() => new(request.Ids, request.Epoch, state, endReason,
        sourceSamples, canonicalSamples, pcm is null ? utterance?.ByteCount ?? 0 : byteCount,
        Interlocked.Read(ref droppedEvents), error);

    private void Emit(CaptureEventKind kind, double? peak = null, double? rms = null)
    {
        if (terminal && kind != CaptureEventKind.Terminal) return;
        events.Writer.TryWrite(new(++sequence, time.GetElapsedTime(startedAt), kind, GetSnapshot(), sourceFormat, peak, rms));
    }
}
