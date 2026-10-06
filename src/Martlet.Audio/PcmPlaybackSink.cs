namespace Martlet.Audio;

public sealed class PcmPlaybackSink : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly IPlaybackDeviceFactory devices;
    private readonly PlaybackOptions options;
    private readonly TimeProvider time;
    private PlaybackRun? active;
    private long highestEpoch = -1;
    private bool disposed;
    private double volume = PcmGain.Full;

    /// <summary>How loud what this sink plays is, 0 (silent) to 1 (as received, the default). A change applies to the playback
    /// running now within one device buffer, and to every later one.</summary>
    public double Volume
    {
        get => Volatile.Read(ref volume);
        set => Volatile.Write(ref volume, PcmGain.Clamp(value));
    }

    public PcmPlaybackSink(IPlaybackDeviceFactory devices, PlaybackOptions? options = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(devices);
        this.devices = devices;
        this.options = options ?? new();
        this.options.Validate();
        time = timeProvider ?? TimeProvider.System;
    }

    public PlaybackRun Start(PlaybackRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate(time.GetUtcNow());
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            Core.Contracts.ContractRules.Require(request.Epoch > highestEpoch,
                "A playback epoch can never be reused or decreased.");
            if (active is not null && (!active.Completion.IsCompleted || !active.WorkerReleased))
                throw new InvalidOperationException("Stop playback and await device release before starting another epoch.");
            highestEpoch = request.Epoch;
            active = new PlaybackRun(devices, request, options, time, cancellationToken, () => Volume);
            return active;
        }
    }

    public async Task<PlaybackRun> ReplaceAsync(PlaybackRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate(time.GetUtcNow());
        cancellationToken.ThrowIfCancellationRequested();
        PlaybackRun? previous;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            Core.Contracts.ContractRules.Require(request.Epoch > highestEpoch, "Replacement needs a new playback epoch.");
            previous = active;
        }
        if (previous is not null)
            await previous.StopAsync(replaced: true).ConfigureAwait(false);
        // Start's atomic check rejects competing replacements and quarantined native workers; never reopens them.
        return Start(request, cancellationToken);
    }

    public async Task<PlaybackSnapshot?> StopAsync()
    {
        PlaybackRun? run;
        lock (gate) run = active;
        return run is null ? null : await run.StopAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        PlaybackRun? run;
        lock (gate)
        {
            disposed = true;
            run = active;
        }
        if (run is not null)
            await run.StopAsync().ConfigureAwait(false);
    }
}
