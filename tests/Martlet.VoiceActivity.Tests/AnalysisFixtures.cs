using System.Buffers.Binary;
using System.Collections.Concurrent;
using Martlet.Audio;
using Martlet.Core.Contracts;

namespace Martlet.VoiceActivity.Tests;

internal sealed class AnalysisClock : TimeProvider
{
    private readonly object gate = new();
    private readonly List<ClockTimer> timers = [];
    private long ticks, utcShift;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() { lock (gate) return ticks; }
    public override DateTimeOffset GetUtcNow() { lock (gate) return DateTimeOffset.UnixEpoch.AddTicks(ticks + utcShift); }
    internal void ShiftUtc(TimeSpan shift) { lock (gate) utcShift += shift.Ticks; }
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (gate)
        {
            var timer = new ClockTimer(this, callback, state);
            timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }
    }
    internal void Advance(TimeSpan amount, bool deliver = true)
    {
        List<ClockTimer> due;
        lock (gate)
        {
            ticks += amount.Ticks;
            due = deliver ? timers.Where(t => t.Due <= ticks).ToList() : [];
            foreach (var timer in due) timer.Due = timer.Period > 0 ? ticks + timer.Period : long.MaxValue;
        }
        foreach (var timer in due) timer.Callback(timer.State);
    }
    private sealed class ClockTimer(AnalysisClock clock, TimerCallback callback, object? state) : ITimer
    {
        internal long Due, Period;
        internal TimerCallback Callback => callback;
        internal object? State => state;
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock.gate)
            {
                Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock.ticks + dueTime.Ticks;
                Period = period == Timeout.InfiniteTimeSpan ? 0 : period.Ticks;
                return true;
            }
        }
        public void Dispose() { lock (clock.gate) clock.timers.Remove(this); }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}

internal sealed class AuthoredCapture : ICaptureDeviceFactory, ICaptureDevice
{
    internal readonly ConcurrentQueue<byte[]> Packets = new();
    public CaptureSourceFormat Format { get; init; } = new(16000, 1, 16, DeviceSampleEncoding.IntegerPcm);
    public ICaptureDevice Open(CaptureDeviceAccess access, CancellationToken token) { access.CheckAuthorization(); return this; }
    public void Start(CancellationToken token) { }
    public CapturePacket Read(Span<byte> destination, CancellationToken token)
    {
        if (!Packets.TryDequeue(out var bytes)) return new(0);
        bytes.CopyTo(destination);
        return new(bytes.Length);
    }
    public void Stop() { }
    public void Dispose() { }
}

internal static class AnalysisFixtures
{
    internal static readonly Guid Session = Guid.Parse("cfeeb6d1-69ec-4025-bdbd-6a501564252c");
    internal static readonly Guid Profile = Guid.Parse("251f9d14-892e-4d82-9b88-a8b4859f2e07");
    internal static readonly Guid Revision = Guid.Parse("d590ea50-0403-4f57-9e01-0313d11a65dd");
    internal static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    internal const string PrivateCanary = "PRIVATE-VAD-CANARY";
    internal static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(Wait);
        while (!condition()) await Task.Delay(1, timeout.Token);
    }

    internal static async Task<CapturedUtterance> Capture(int samples = 1200, long epoch = 0, bool resample = false)
    {
        var clock = new AnalysisClock();
        var device = new AuthoredCapture
        {
            Format = resample ? new(48000, 2, 32, DeviceSampleEncoding.IeeeFloat) : new(16000, 1, 16, DeviceSampleEncoding.IntegerPcm)
        };
        await using var capture = new MicrophoneCapture(Session, device, timeProvider: clock);
        var request = new CaptureRequest(new() { SessionId = Session, TurnId = Guid.NewGuid(), RequestId = Guid.NewGuid() },
            epoch, new(InputPolicy.FixedEndpoint, "authored-in-memory-input"), TimeSpan.FromSeconds(30), clock.GetUtcNow().AddSeconds(30));
        var run = capture.Press(request, new(request, true));
        await run.Ready.WaitAsync(Wait);
        var sourceSamples = resample ? samples * 3 : samples;
        var bytes = new byte[sourceSamples * device.Format.BlockAlignment];
        for (var i = 0; i < sourceSamples; i++)
        {
            if (resample)
            {
                BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 8), 0.25f);
                BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 8 + 4), 0.5f);
            }
            else BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), (short)(1234 + i % 100));
        }
        for (var offset = 0; offset < bytes.Length; offset += 997)
            device.Packets.Enqueue(bytes.AsSpan(offset, Math.Min(997, bytes.Length - offset)).ToArray());
        await Until(() => run.Snapshot.SourceSamples == sourceSamples || run.Completion.IsCompleted);
        Assert.Equal(CaptureState.Completed, (await run.ReleaseAsync().WaitAsync(Wait)).State);
        Assert.True((await run.DeviceRelease.WaitAsync(Wait)).Released);
        var utterance = run.TakeUtterance();
        Assert.NotNull(utterance);
        Assert.Equal(samples, utterance.SampleCount);
        return utterance;
    }

    internal static VoiceActivityBinding Binding(CapturedUtterance input) => new(input.Ids, input.Epoch, Profile, Revision, input.SampleCount);
    internal static LocalVoiceActivityAuthorization Authorize(CapturedUtterance input, AnalysisClock clock,
        VoiceActivityOptions options, TimeSpan? lifetime = null) =>
        new(input, Binding(input), options, clock.GetUtcNow() + (lifetime ?? TimeSpan.FromSeconds(30)), true, clock);
    internal static CpuVoiceActivityAnalyzer Analyzer(ControlledInferenceFactory factory, AnalysisClock clock, VoiceActivityOptions options) =>
        new(Session, Profile, Revision, "inert-fake-model-path", factory, options, clock);
    internal static byte[] Copy(CapturedUtterance input)
    {
        var copy = new byte[input.ByteCount];
        input.CopyPcmTo(copy);
        return copy;
    }
}

internal sealed class ControlledInferenceFactory : IVoiceActivityInferenceFactory
{
    internal Func<ControlledInference> NewSession { get; set; } = () => new();
    internal int Creates;
    public VoiceActivityEvidence Evidence => VoiceActivityEvidence.InternalFakeInference;
    public IVoiceActivityInferenceSession Create() { Interlocked.Increment(ref Creates); return NewSession(); }
}

internal sealed class ControlledInference : IVoiceActivityInferenceSession
{
    internal ManualResetEventSlim LoadEntered { get; } = new();
    internal ManualResetEventSlim ScoreEntered { get; } = new();
    internal ManualResetEventSlim CancelEntered { get; } = new();
    internal ManualResetEventSlim DisposeEntered { get; } = new();
    internal ManualResetEventSlim? LoadBlock, ScoreBlock, CancelBlock, DisposeBlock;
    internal Exception? LoadFailure, ScoreFailure, CancelFailure, DisposeFailure;
    internal Action? OnLoad, OnScore;
    internal float Result = 0.8f;
    internal int Loads, Scores, Cancels, Disposals;
    internal readonly ConcurrentQueue<float[]> Windows = new();
    internal readonly ConcurrentQueue<int> OwnerThreads = new();

    public void Load(string path, VoiceActivityInferenceAccess access)
    {
        OwnerThreads.Enqueue(Environment.CurrentManagedThreadId);
        Interlocked.Increment(ref Loads);
        LoadEntered.Set();
        LoadBlock?.Wait();
        OnLoad?.Invoke();
        if (LoadFailure is not null) throw LoadFailure;
        access.Check();
    }
    public float Score(ReadOnlySpan<float> window)
    {
        OwnerThreads.Enqueue(Environment.CurrentManagedThreadId);
        Interlocked.Increment(ref Scores);
        Windows.Enqueue(window.ToArray());
        ScoreEntered.Set();
        ScoreBlock?.Wait();
        OnScore?.Invoke();
        if (ScoreFailure is not null) throw ScoreFailure;
        return Result;
    }
    public void RequestCancellation()
    {
        Interlocked.Increment(ref Cancels);
        CancelEntered.Set();
        CancelBlock?.Wait();
        if (CancelFailure is not null) throw CancelFailure;
    }
    public void Dispose()
    {
        OwnerThreads.Enqueue(Environment.CurrentManagedThreadId);
        Interlocked.Increment(ref Disposals);
        DisposeEntered.Set();
        DisposeBlock?.Wait();
        if (DisposeFailure is not null) throw DisposeFailure;
    }
}
