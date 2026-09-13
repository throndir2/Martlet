using Martlet.Core.Contracts;

namespace Martlet.Audio;

public sealed class MicrophoneCapture : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly Guid sessionId;
    private readonly ICaptureDeviceFactory devices;
    private readonly CaptureOptions options;
    private readonly TimeProvider time;
    private CaptureRun? active;
    private long highestEpoch = -1;
    private bool disposed, muted, paused, sessionLocked, ownOutputActive;

    public MicrophoneCapture(Guid sessionId, ICaptureDeviceFactory devices, CaptureOptions? options = null, TimeProvider? timeProvider = null)
    {
        ContractRules.Require(sessionId != Guid.Empty, "Capture needs a current session identifier.");
        ArgumentNullException.ThrowIfNull(devices);
        this.sessionId = sessionId;
        this.devices = devices;
        this.options = options ?? new();
        this.options.Validate();
        time = timeProvider ?? TimeProvider.System;
    }

    public CaptureArmingDecision ArmingDecision(bool manualPushToTalk)
    {
        lock (gate) return CaptureArming.Evaluate(manualPushToTalk, ownOutputActive);
    }

    public CaptureRun Press(CaptureRequest request, CaptureAuthorization? authorization, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            request.Validate(sessionId, time.GetUtcNow(), options);
            if (authorization is not { MicrophoneCaptureRequested: true } || authorization.Request != request)
                throw new CaptureDeviceException(ErrorCode.NotConfigured);
            cancellationToken.ThrowIfCancellationRequested();
            ContractRules.Require(request.Epoch > highestEpoch, "Every microphone press needs a fresh increasing epoch.");
            if (muted || paused || sessionLocked)
                throw new InvalidOperationException("Microphone capture is muted, paused or locked.");
            if (ownOutputActive)
                throw new InvalidOperationException("Coordinate Stop with this session's playback and signal inactive before pressing.");
            if (active is not null && (!active.Completion.IsCompleted || !active.WorkerReleased))
                throw new InvalidOperationException("Await the previous capture and device release before pressing again.");
            active?.DiscardUnclaimedUtterance();
            highestEpoch = request.Epoch;
            active = new CaptureRun(devices, request, options, time, cancellationToken);
            return active;
        }
    }

    public Task<CaptureSnapshot?> StopAsync() => SetControl(null, CaptureEndReason.Stopped);
    public Task<CaptureSnapshot?> SetMutedAsync(bool value) => SetControl(() => muted = value, CaptureEndReason.Muted, value);
    public Task<CaptureSnapshot?> SetPausedAsync(bool value) => SetControl(() => paused = value, CaptureEndReason.Paused, value);
    public Task<CaptureSnapshot?> SetSessionLockedAsync(bool value) => SetControl(() => sessionLocked = value, CaptureEndReason.Locked, value);
    public Task<CaptureSnapshot?> SetOwnOutputActiveAsync(bool value) => SetControl(() => ownOutputActive = value, CaptureEndReason.Stopped, value);

    private async Task<CaptureSnapshot?> SetControl(Action? update, CaptureEndReason reason, bool cancel = true)
    {
        Task<CaptureSnapshot>? completion;
        lock (gate)
        {
            update?.Invoke();
            // Invalidate under the same lock as Press; a concurrent unmute cannot overtake cancellation.
            completion = cancel ? active?.CancelAsync(reason) : null;
        }
        return completion is null ? null : await completion.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        Task<CaptureSnapshot>? completion;
        lock (gate)
        {
            disposed = true;
            completion = active?.CancelAsync(CaptureEndReason.Disposed);
        }
        if (completion is not null) await completion.ConfigureAwait(false);
    }
}
