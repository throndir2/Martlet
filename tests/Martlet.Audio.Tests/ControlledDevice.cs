using Martlet.Core.Audio;
using Martlet.Core.Contracts;

namespace Martlet.Audio.Tests;

internal sealed class ControlledDevice : IPlaybackDeviceFactory, IPlaybackDevice
{
    private readonly object gate = new();
    private readonly List<byte> output = new();
    private int padding, opens, stops, disposals, starts;
    private int workerThread;
    private PcmFormat? format;
    public bool AutoConsume { get; set; } = true;
    public bool BlockOpen { get; init; }
    public bool BlockOpenCancellation { get; init; }
    public bool BlockDispose { get; init; }
    public bool BlockWrite { get; set; }
    public int BlockAfterSamples { get; init; } = int.MaxValue;
    public bool IgnoreCancellation { get; init; }
    public int MaximumWriteSamples { get; init; } = int.MaxValue;
    public int? CapacityOverride { get; init; }
    public ErrorCode? OpenError { get; init; }
    public ErrorCode? PaddingError { get; set; }
    public bool FailCleanup { get; init; }
    public bool FailDispose { get; init; }
    public int? InvalidPadding { get; init; }
    public bool ReturnInvalidWriteCount { get; init; }
    public TaskCompletionSource EnteredOpen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource EnteredCancellation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource EnteredDispose { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource EnteredWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource EnteredBlockedWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ManualResetEventSlim Release { get; } = new();
    public int Opens => Volatile.Read(ref opens);
    public int Stops => Volatile.Read(ref stops);
    public int Disposals => Volatile.Read(ref disposals);
    public int Starts => Volatile.Read(ref starts);
    public int Samples { get { lock (gate) return output.Count / (format?.BlockAlignment ?? 1); } }
    public byte[] Bytes { get { lock (gate) return output.ToArray(); } }
    public OutputSelection? Selection { get; private set; }
    public PlaybackDeviceInfo Info => new(48000, 2, 32, DeviceSampleEncoding.IeeeFloat,
        CapacityOverride ?? format!.SampleRate / 20, true);

    public IPlaybackDevice Open(OutputSelection selection, PcmFormat input, CancellationToken cancellationToken)
    {
        workerThread = Environment.CurrentManagedThreadId;
#if WINDOWS
        Assert.Equal(ApartmentState.MTA, Thread.CurrentThread.GetApartmentState());
#endif
        Interlocked.Increment(ref opens);
        Selection = selection;
        format = input;
        if (BlockOpenCancellation && !Release.IsSet)
        {
            using var registration = cancellationToken.Register(() =>
            {
                EnteredCancellation.TrySetResult();
                Release.Wait();
            });
            EnteredOpen.TrySetResult();
            // Open cannot finish releasing its registration while the cancellation callback is held.
            EnteredCancellation.Task.GetAwaiter().GetResult();
            cancellationToken.ThrowIfCancellationRequested();
        }
        EnteredOpen.TrySetResult();
        if (BlockOpen)
            Release.Wait(IgnoreCancellation ? CancellationToken.None : cancellationToken);
        if (OpenError is { } code)
            throw new ContractException(code, "PRIVATE native exception body");
        cancellationToken.ThrowIfCancellationRequested();
        return this;
    }

    public int GetPadding(CancellationToken cancellationToken)
    {
        Assert.Equal(workerThread, Environment.CurrentManagedThreadId);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (PaddingError is { } code)
                throw new ContractException(code, "PRIVATE native exception body");
            if (AutoConsume && Starts > 0) padding = 0;
            return InvalidPadding ?? padding;
        }
    }

    public void Consume(int samples)
    {
        lock (gate) padding = Math.Max(0, padding - samples);
    }

    public int Write(ReadOnlySpan<byte> pcm, CancellationToken cancellationToken)
    {
        Assert.Equal(workerThread, Environment.CurrentManagedThreadId);
        EnteredWrite.TrySetResult();
        if (BlockWrite || Samples >= BlockAfterSamples)
        {
            EnteredBlockedWrite.TrySetResult();
            Release.Wait(IgnoreCancellation ? CancellationToken.None : cancellationToken);
        }
        if (!IgnoreCancellation) cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            var samples = Math.Min(pcm.Length / format!.BlockAlignment, MaximumWriteSamples);
            output.AddRange(pcm[..(samples * format.BlockAlignment)].ToArray());
            padding += samples;
            return ReturnInvalidWriteCount ? int.MaxValue : samples;
        }
    }

    public void Start(CancellationToken cancellationToken)
    {
        Assert.Equal(workerThread, Environment.CurrentManagedThreadId);
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref starts);
    }

    public void StopAndReset()
    {
        Assert.Equal(workerThread, Environment.CurrentManagedThreadId);
        Interlocked.Increment(ref stops);
        lock (gate) padding = 0;
        if (FailCleanup) throw new InvalidOperationException("PRIVATE cleanup detail");
    }

    public void Dispose()
    {
        Assert.Equal(workerThread, Environment.CurrentManagedThreadId);
        Interlocked.Increment(ref disposals);
        EnteredDispose.TrySetResult();
        if (BlockDispose) Release.Wait();
        if (FailDispose) throw new InvalidOperationException("PRIVATE disposal detail");
    }
}
