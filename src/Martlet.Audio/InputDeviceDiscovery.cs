using System.Threading.Channels;
using Martlet.Core.Contracts;

namespace Martlet.Audio;

[Flags]
public enum InputDeviceChanges { None = 0, Added = 1, Removed = 2, State = 4, Default = 8, Properties = 16 }

// Explicit UI data: endpoint IDs and friendly names must not be logged or put in diagnostic events.
public sealed record InputEndpoint(string EndpointId, string DisplayName, bool IsDefault);
public sealed record InputDeviceEvent(long Generation, long Sequence, InputDeviceChanges Changes, long DroppedEvents);
public sealed record InputDiscoveryRequest(bool DeviceDiscoveryRequested = false);
public sealed record InputDeviceList(long Generation, IReadOnlyList<InputEndpoint> Endpoints, MartletError? Error);

public interface IInputDeviceDiscoveryFactory
{
    IInputDeviceDiscovery Open(CancellationToken cancellationToken);
}

public interface IInputDeviceDiscovery : IDisposable
{
    IReadOnlyList<InputEndpoint> Enumerate(CancellationToken cancellationToken);
    InputDeviceChanges PollChanges();
}

public sealed class InputDeviceMonitor : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly IInputDeviceDiscoveryFactory factory;
    private readonly TimeProvider time;
    private readonly TimeSpan shutdownTimeout;
    private readonly CancellationTokenSource cancellation = new();
    private readonly TaskCompletionSource stop = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<CaptureDeviceRelease> nativeRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<InputDeviceEvent> events;
    private TaskCompletionSource<InputDeviceList>? refresh;
    private Task<CaptureDeviceRelease>? shutdown;
    private bool started, stopped;
    private long generation, sequence, dropped;

    public InputDeviceMonitor(IInputDeviceDiscoveryFactory factory, TimeProvider? timeProvider = null, TimeSpan? shutdownTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        this.factory = factory;
        time = timeProvider ?? TimeProvider.System;
        this.shutdownTimeout = shutdownTimeout ?? TimeSpan.FromSeconds(2);
        ContractRules.Require(this.shutdownTimeout > TimeSpan.Zero && this.shutdownTimeout <= TimeSpan.FromSeconds(2),
            "Device monitor shutdown must be positive and at most two seconds.");
        events = Channel.CreateBounded<InputDeviceEvent>(new BoundedChannelOptions(32)
        {
            FullMode = BoundedChannelFullMode.DropOldest, AllowSynchronousContinuations = false
        }, _ => Interlocked.Increment(ref dropped));
    }

    public ChannelReader<InputDeviceEvent> Events => events.Reader;
    public Task<CaptureDeviceRelease> DeviceRelease => nativeRelease.Task;

    public Task<InputDeviceList> StartAsync(InputDiscoveryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(stopped, this);
            ContractRules.Require(request.DeviceDiscoveryRequested, "Request input device discovery explicitly; this does not grant microphone capture permission.");
            if (started) throw new InvalidOperationException("This device monitor is already started.");
            started = true;
            generation++;
            refresh = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var result = refresh.Task;
            _ = Task.Factory.StartNew(Drive, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            return result;
        }
    }

    public Task<InputDeviceList> RefreshAsync()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(stopped, this);
            if (!started) throw new InvalidOperationException("Explicitly start device discovery first.");
            if (refresh is not null) throw new InvalidOperationException("A device refresh is already pending.");
            refresh = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return refresh.Task;
        }
    }

    private void Drive()
    {
        IInputDeviceDiscovery? source = null;
        MartletError? failure = null;
        var released = true;
        try
        {
            CheckActive();
            source = factory.Open(cancellation.Token);
            while (true)
            {
                CheckActive();
                TaskCompletionSource<InputDeviceList>? pending;
                lock (gate) pending = refresh;
                if (pending is not null)
                {
                    var endpoints = source.Enumerate(cancellation.Token);
                    if (endpoints.Count > 128) throw new CaptureDeviceException(ErrorCode.PayloadTooLarge);
                    var copy = endpoints.ToArray();
                    foreach (var endpoint in copy)
                    {
                        new InputSelection(InputPolicy.FixedEndpoint, endpoint.EndpointId).Validate();
                        ContractRules.Require(endpoint.DisplayName.Length <= 256 && !endpoint.DisplayName.Any(char.IsControl),
                            "The input display name exceeds safe UI bounds.");
                    }
                    lock (gate)
                    {
                        CheckActive();
                        pending.TrySetResult(new(generation, Array.AsReadOnly(copy), null));
                        refresh = null;
                    }
                }
                var changes = source.PollChanges();
                lock (gate)
                {
                    CheckActive();
                    if ((changes & ~(InputDeviceChanges.Added | InputDeviceChanges.Removed | InputDeviceChanges.State
                        | InputDeviceChanges.Default | InputDeviceChanges.Properties)) != 0)
                        throw new CaptureDeviceException(ErrorCode.AudioCaptureFailed);
                    if (changes != InputDeviceChanges.None)
                        events.Writer.TryWrite(new(++generation, ++sequence, changes, Interlocked.Read(ref dropped)));
                }
                Thread.Sleep(10);
            }
        }
        catch (OperationCanceledException) when (stop.Task.IsCompleted) { }
        catch (Exception ex)
        {
            failure = CaptureErrors.Normalize(ex);
            if (source is null) released = ex is CaptureDeviceException { ResourcesReleased: true };
        }
        finally
        {
            if (source is not null)
            {
                try { source.Dispose(); }
                catch (Exception ex) { released = false; failure = CaptureErrors.Normalize(ex); }
            }
            lock (gate)
            {
                stopped = true;
                refresh?.TrySetResult(new(generation, Array.Empty<InputEndpoint>(), failure ?? CaptureErrors.Create(ErrorCode.AudioCaptureFailed)));
                refresh = null;
                events.Writer.TryComplete();
            }
            nativeRelease.TrySetResult(new(released, failure));
        }
    }

    private void CheckActive()
    {
        if (stop.Task.IsCompleted) throw new OperationCanceledException(cancellation.Token);
    }

    public Task<CaptureDeviceRelease> StopAsync()
    {
        lock (gate)
        {
            if (shutdown is not null) return shutdown;
            stopped = true;
            generation++;
            stop.TrySetResult();
            refresh?.TrySetResult(new(generation, Array.Empty<InputEndpoint>(), CaptureErrors.Create(ErrorCode.AudioCaptureFailed)));
            refresh = null;
            events.Writer.TryComplete();
            if (!started) nativeRelease.TrySetResult(new(true, null));
            shutdown = StopCoreAsync();
            return shutdown;
        }
    }

    private async Task<CaptureDeviceRelease> StopCoreAsync()
    {
        var callbacks = cancellation.CancelAsync();
        var release = FinishReleaseAsync(callbacks);
        try { return await release.WaitAsync(shutdownTimeout, time).ConfigureAwait(false); }
        catch (TimeoutException) { return new(false, CaptureErrors.Create(ErrorCode.AudioCaptureFailed)); }
    }

    private async Task<CaptureDeviceRelease> FinishReleaseAsync(Task callbacks)
    {
        var result = await DeviceRelease.ConfigureAwait(false);
        try { await callbacks.ConfigureAwait(false); }
        catch (Exception) { result = result with { Error = CaptureErrors.Create(ErrorCode.AudioCaptureFailed) }; }
        cancellation.Dispose();
        return result;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
