using Martlet.Audio;
using Martlet.Core.Audio;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

public enum AudioSetupAction { Discovery, Microphone, Output }

public sealed record AudioTestStatus(AudioSetupAction Action, string Stage, bool Finished = false,
    bool Succeeded = false, bool Released = false, double? Peak = null, double? Rms = null,
    long Samples = 0, ErrorCode? Error = null, AudioCheckpoint? Checkpoint = null);

public sealed class AudioSetupOperation
{
    private AudioTestStatus status;
    public AudioTestStatus Status => Volatile.Read(ref status);
    public AudioDeviceList? Devices { get; internal set; }
    public CaptureSnapshot? CaptureSnapshot { get; internal set; }
    public SetupOperation Worker { get; internal set; } = null!;
    internal AudioSetupOperation(AudioSetupAction action) => status = new(action, "Starting");
    internal void Publish(AudioTestStatus value) => Volatile.Write(ref status, value);
    public void Stop() => Worker.RequestCancellation();
}

// All windows and the fixture use the existing app-shared worker owner. A closed observer is not a released device.
public sealed class AudioSetupService
{
    private readonly IAudioDeviceCatalog catalog;
    private readonly ICaptureDeviceFactory captureDevices;
    private readonly IPlaybackDeviceFactory playbackDevices;
    private readonly SetupOperationRunner operations;
    private readonly TimeProvider clock;
    private readonly Guid session = Guid.NewGuid();
    private long epoch;
    private AudioSetupOperation? active;
    private int sessionLocked;
    private readonly TaskCompletionSource quarantine = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool IsRunning => operations.IsRunning;
    public bool IsSessionLocked => Volatile.Read(ref sessionLocked) != 0;

    public void SetSessionLocked(bool value)
    {
        Volatile.Write(ref sessionLocked, value ? 1 : 0);
        if (value) Volatile.Read(ref active)?.Stop();
    }

    public AudioSetupService(SetupOperationRunner operations, IAudioDeviceCatalog catalog,
        ICaptureDeviceFactory captureDevices, IPlaybackDeviceFactory playbackDevices, TimeProvider? clock = null)
    {
        this.operations = operations;
        this.catalog = catalog;
        this.captureDevices = captureDevices;
        this.playbackDevices = playbackDevices;
        this.clock = clock ?? TimeProvider.System;
    }

    public AudioSetupOperation? Start(AudioSetupAction action, AudioChoice? choice, bool explicitlyApproved)
    {
        if (!explicitlyApproved || IsSessionLocked) return null;
        ContractRules.Defined(action);
        if (action != AudioSetupAction.Discovery) (choice ?? throw new ArgumentNullException(nameof(choice))).Validate();
        var view = new AudioSetupOperation(action);
        // Consent starts at the action, not after an arbitrarily delayed worker dispatch.
        var authorizedAt = clock.GetUtcNow();
        var timestamp = clock.GetTimestamp();
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = operations.TryStart(async original =>
        {
            try
            {
                // Publish the cancellable owner before any device boundary, including a concurrent session lock.
                await published.Task.ConfigureAwait(false);
                original.ThrowIfCancellationRequested();
                if (action == AudioSetupAction.Discovery)
                {
                    view.Devices = catalog.Discover(original);
                    WindowsAudioDeviceCatalog.Validate(view.Devices.Inputs);
                    WindowsAudioDeviceCatalog.Validate(view.Devices.Outputs);
                    original.ThrowIfCancellationRequested();
                    view.Publish(new(action, "Enumerated only - capture permission and audibility UNVERIFIED", true, true, true));
                }
                else if (action == AudioSetupAction.Microphone)
                    await CaptureAsync(view, choice!, original, authorizedAt, timestamp).ConfigureAwait(false);
                else
                    await OutputAsync(view, choice!, original, authorizedAt, timestamp).ConfigureAwait(false);
                return new SetupWorkResult(SetupWorkOutcome.Completed);
            }
            catch (OperationCanceledException) when (original.IsCancellationRequested)
            {
                view.Publish(new(action, "Canceled; no replacement started", true, Released: true));
                return new SetupWorkResult(SetupWorkOutcome.Canceled);
            }
            catch (AudioDiscoveryException error)
            {
                view.Publish(new(action, "Discovery failed", true, Released: error.Released, Error: error.Code));
                if (!error.Released) await quarantine.Task.ConfigureAwait(false);
                return new SetupWorkResult(SetupWorkOutcome.Failed);
            }
            catch (CaptureDeviceException error)
            {
                view.Publish(new(action, "Input authorization or device failed", true, Released: error.ResourcesReleased, Error: error.Code));
                if (!error.ResourcesReleased) await quarantine.Task.ConfigureAwait(false);
                return new SetupWorkResult(SetupWorkOutcome.Failed);
            }
            catch (ContractException error)
            {
                view.Publish(new(action, "Local audio request rejected", true, Released: true, Error: error.Code));
                return new SetupWorkResult(SetupWorkOutcome.Failed);
            }
        });
        if (worker is null) return null;
        view.Worker = worker;
        Volatile.Write(ref active, view);
        if (IsSessionLocked) view.Stop();
        published.SetResult();
        return view;
    }

    private CorrelationIds Ids() => new() { SessionId = session, TurnId = Guid.NewGuid(), RequestId = Guid.NewGuid() };

    private async Task CaptureAsync(AudioSetupOperation view, AudioChoice choice, CancellationToken original,
        DateTimeOffset authorizedAt, long timestamp)
    {
        CheckPermission(original, authorizedAt, timestamp, TimeSpan.FromSeconds(20));
        await using var microphone = new MicrophoneCapture(session, captureDevices, timeProvider: clock);
        var expiry = authorizedAt.AddSeconds(20);
        var monotonicExpiry = clock.GetUtcNow() + (TimeSpan.FromSeconds(20) - clock.GetElapsedTime(timestamp));
        if (monotonicExpiry < expiry) expiry = monotonicExpiry;
        var request = new CaptureRequest(Ids(), Interlocked.Increment(ref epoch),
            new(choice.EndpointId is null ? InputPolicy.FollowDefaultOnNextPress : InputPolicy.FixedEndpoint, choice.EndpointId),
            TimeSpan.FromSeconds(5), expiry);
        var run = microphone.Press(request, new(request, true), original);
        try
        {
            await foreach (var item in run.Events.ReadAllAsync().ConfigureAwait(false))
            {
                if (!original.IsCancellationRequested)
                    view.Publish(new(AudioSetupAction.Microphone, item.Kind == CaptureEventKind.Meter ? "Receiving selected-input PCM (amplitude, NOT VAD)" : item.Kind.ToString(),
                        Peak: item.Peak, Rms: item.Rms, Samples: item.Snapshot.CanonicalSamples));
            }
            var terminal = await run.Completion.ConfigureAwait(false);
            // Take enforces the capture library's ORIGINAL caller token, absolute UTC and monotonic lifetime.
            // Never copy raw PCM out of its lease. Dispose even when cancellation races transfer.
            using var utterance = run.TakeUtterance();
            var succeeded = terminal.State == CaptureState.Completed && utterance is { SampleCount: > 0 } && !original.IsCancellationRequested;
            utterance?.Dispose();
            view.Publish(new(AudioSetupAction.Microphone, "Capture ended; waiting for actual native/callback release",
                Samples: terminal.CanonicalSamples, Error: terminal.Error?.Code));
            var released = await run.DeviceRelease.ConfigureAwait(false);
            succeeded &= released.Released && released.Error is null && !original.IsCancellationRequested;
            view.Publish(new(AudioSetupAction.Microphone,
                succeeded ? "Samples received and discarded; speech/quality NOT evaluated" : terminal.State.ToString(),
                true, succeeded, released.Released, Samples: terminal.CanonicalSamples, Error: released.Error?.Code ?? terminal.Error?.Code,
                Checkpoint: succeeded ? new() { ConfigurationRevision = choice.ConfigurationRevision, TestedAt = clock.GetUtcNow(), Outcome = LocalAudioOutcome.SamplesReceived } : null));
        }
        finally
        {
            await run.CancelAsync().ConfigureAwait(false);
            var release = await run.DeviceRelease.ConfigureAwait(false);
            view.CaptureSnapshot = run.Snapshot;
            if (!release.Released) await quarantine.Task.ConfigureAwait(false);
        }
    }

    private async Task OutputAsync(AudioSetupOperation view, AudioChoice choice, CancellationToken original,
        DateTimeOffset authorizedAt, long timestamp)
    {
        void Guard() => CheckPermission(original, authorizedAt, timestamp, TimeSpan.FromSeconds(5));
        Guard();
        var factory = new AuthorizedOutputFactory(playbackDevices, Guard, original);
        await using var sink = new PcmPlaybackSink(factory, timeProvider: clock);
        var ids = Ids();
        var nextEpoch = Interlocked.Increment(ref epoch);
        var run = sink.Start(new(ids, nextEpoch, SyntheticTone.Format,
            new(choice.EndpointId is null ? OutputPolicy.DefaultAtStart : OutputPolicy.FixedEndpoint, choice.EndpointId),
            authorizedAt.AddSeconds(5)), original);
        try
        {
            view.Publish(new(AudioSetupAction.Output, "Opening selected output; no fallback"));
            if (await run.Ready.ConfigureAwait(false) is not null && !original.IsCancellationRequested)
            {
                foreach (var frame in SyntheticTone.Frames(ids, nextEpoch))
                {
                    Guard();
                    if (run.Submit(frame) != FrameAcceptance.Accepted) break;
                }
                Guard();
                run.CompleteInput(SyntheticTone.SampleCount);
            }
            var terminal = await run.Completion.ConfigureAwait(false);
            view.Publish(new(AudioSetupAction.Output, "Tone ended; waiting for actual native release", Error: terminal.Error?.Code));
            var error = await AwaitOutputReleaseAsync(run).ConfigureAwait(false);
            var released = OutputReleased(run, factory);
            var failure = factory.ExpiredOpenReleased ? ErrorCode.DeadlineExceeded : error?.Code ?? terminal.Error?.Code;
            var succeeded = terminal.State == PlaybackState.Completed && terminal.DeviceDrainObserved &&
                terminal.DeviceConsumedSamples == SyntheticTone.SampleCount && released && failure is null && !original.IsCancellationRequested;
            view.Publish(new(AudioSetupAction.Output, factory.ExpiredOpenReleased
                    ? "Output authorization expired; returned native device cleanup completed"
                    : succeeded ? "200 ms tone drained; audibility UNCONFIRMED" : terminal.State.ToString(),
                true, succeeded, released, Samples: terminal.DeviceConsumedSamples, Error: failure,
                Checkpoint: succeeded ? new() { ConfigurationRevision = choice.ConfigurationRevision, TestedAt = clock.GetUtcNow(), Outcome = LocalAudioOutcome.ToneDrained } : null));
        }
        finally
        {
            await run.StopAsync().ConfigureAwait(false);
            await AwaitOutputReleaseAsync(run).ConfigureAwait(false);
            if (!OutputReleased(run, factory))
            {
                view.Publish(view.Status with { Released = false, Error = ErrorCode.AudioPlaybackFailed });
                await quarantine.Task.ConfigureAwait(false);
                GC.KeepAlive(factory);
            }
        }
    }

    private static bool OutputReleased(PlaybackRun run, AuthorizedOutputFactory factory) =>
        !factory.ReleaseFailed && (run.Snapshot.DeviceReleased || factory.ExpiredOpenReleased);

    private static async Task<MartletError?> AwaitOutputReleaseAsync(PlaybackRun run)
    {
        var error = await run.DeviceRelease.ConfigureAwait(false);
        // Native worker exit alone does not include device-token cancellation callbacks.
        // The existing sink closes its bounded event stream only after those callbacks finish.
        // Do not cancel this cleanup observation when the UI stops observing the test.
        await foreach (var _ in run.Events.ReadAllAsync().ConfigureAwait(false)) { }
        return error;
    }

    private void CheckPermission(CancellationToken original, DateTimeOffset authorizedAt, long timestamp, TimeSpan lifetime)
    {
        original.ThrowIfCancellationRequested();
        if (clock.GetUtcNow() >= authorizedAt + lifetime || clock.GetElapsedTime(timestamp) >= lifetime)
            throw new ContractException(ErrorCode.DeadlineExceeded, "Local test authorization expired. Request a fresh test.");
    }

    private sealed class AuthorizedOutputFactory(IPlaybackDeviceFactory devices, Action guard, CancellationToken original) : IPlaybackDeviceFactory
    {
        private IPlaybackDevice? retained;
        private bool authorizationExpired;
        public bool ReleaseFailed { get; private set; }
        public bool ExpiredOpenReleased { get; private set; }

        private void CheckAuthorization()
        {
            try { guard(); }
            catch (ContractException error) when (error.Code == ErrorCode.DeadlineExceeded)
            {
                authorizationExpired = true;
                throw;
            }
        }

        public IPlaybackDevice Open(OutputSelection selection, PcmFormat format, CancellationToken token)
        {
            var linked = CancellationTokenSource.CreateLinkedTokenSource(original, token);
            IPlaybackDevice? device = null;
            try
            {
                CheckAuthorization();
                device = devices.Open(selection, format, linked.Token);
                CheckAuthorization();
                retained = device;
                return new AuthorizedOutput(device, guard, linked, () => retained = null);
            }
            catch
            {
                try { device?.Dispose(); }
                catch { retained = device; ReleaseFailed = true; throw; }
                finally { linked.Dispose(); }
                // Only our own guard's rejection plus completed cleanup is evidence that an
                // unreturned Open released. Arbitrary native AudioPlaybackFailed is not proof.
                ExpiredOpenReleased = authorizationExpired;
                throw;
            }
        }
    }

    private sealed class AuthorizedOutput(IPlaybackDevice device, Action guard, CancellationTokenSource linked, Action released) : IPlaybackDevice
    {
        public PlaybackDeviceInfo Info => device.Info;
        public int GetPadding(CancellationToken token) { guard(); return device.GetPadding(linked.Token); }
        public int Write(ReadOnlySpan<byte> pcm, CancellationToken token) { guard(); return device.Write(pcm, linked.Token); }
        public void Start(CancellationToken token) { guard(); device.Start(linked.Token); }
        public void StopAndReset() => device.StopAndReset();
        public void Dispose()
        {
            try { device.Dispose(); }
            finally { linked.Dispose(); }
            released();
        }
    }
}
