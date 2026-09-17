using System.Security.Cryptography;
using Martlet.Audio;

namespace Martlet.VoiceActivity;

public sealed class VoiceActivityOperation
{
    private readonly object gate = new();
    private readonly CapturedUtterance input;
    private readonly LocalVoiceActivityAuthorization authorization;
    private readonly string modelPath;
    private readonly IVoiceActivityInferenceFactory factory;
    private readonly VoiceActivityOptions options;
    private readonly TimeProvider clock;
    private readonly CancellationToken caller;
    private readonly long started;
    private readonly TaskCompletionSource<VoiceActivityCompletion> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<VoiceActivityOwnershipRelease> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IVoiceActivityInferenceSession? backend;
    private PcmVadWindowAdapter? windows;
    private byte[]? pcm;
    private ITimer? watcher;
    private Task? cancellation;
    private VoiceActivityFailure? stopFailure, releaseFailure;
    private VoiceActivityResult? result;
    private VoiceActivityResult? candidate;
    private VoiceActivityFailure? workFailure;
    private VoiceActivityCompletion? terminal;
    private bool retiring, finished, taken;

    internal VoiceActivityOperation(CapturedUtterance input, LocalVoiceActivityAuthorization authorization,
        string modelPath, IVoiceActivityInferenceFactory factory, VoiceActivityOptions options,
        TimeProvider clock, CancellationToken caller)
    {
        this.input = input;
        this.authorization = authorization;
        this.modelPath = modelPath;
        this.factory = factory;
        this.options = options;
        this.clock = clock;
        this.caller = caller;
        started = clock.GetTimestamp();
    }

    internal void Begin()
    {
        Task worker;
        try
        {
            watcher = clock.CreateTimer(_ => ObserveRevocation(), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(20));
            worker = Task.Factory.StartNew(Drive, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        catch (Exception ex) { worker = Task.FromException(ex); }
        _ = RetireAsync(worker);
    }

    public Task<VoiceActivityCompletion> Completion => completion.Task;
    public Task<VoiceActivityOwnershipRelease> OwnershipRelease => release.Task;

    public VoiceActivitySnapshot Snapshot
    {
        get
        {
            ObserveRevocation();
            lock (gate)
            {
                var failure = releaseFailure ?? stopFailure ?? terminal?.Failure;
                var outcome = failure is null ? terminal?.Outcome :
                    failure.Code == VoiceActivityFailureCode.Canceled ? VoiceActivityOutcome.Canceled : VoiceActivityOutcome.Failed;
                return new(terminal is not null, outcome, OwnershipState(), failure, pcm?.Length ?? 0);
            }
        }
    }

    public Task<VoiceActivityCompletion> Cancel()
    {
        Stop(new(VoiceActivityFailureCode.Canceled));
        return Completion;
    }

    public VoiceActivityResult TakeResult()
    {
        lock (gate)
        {
            Check();
            VadCheck.Require(release.Task.IsCompletedSuccessfully && release.Task.Result.Released && result is not null && !taken,
                VoiceActivityFailureCode.InvalidState);
            taken = true;
            var value = result;
            result = null;
            return value!;
        }
    }

    private VoiceActivityOwnershipState OwnershipState() => !finished ? VoiceActivityOwnershipState.Pending
        : releaseFailure is null ? VoiceActivityOwnershipState.Released : VoiceActivityOwnershipState.Quarantined;

    private void ObserveRevocation()
    {
        if (caller.IsCancellationRequested) Stop(new(VoiceActivityFailureCode.Canceled));
        else
        {
            try { authorization.Check(); }
            catch (VoiceActivityException ex) { Stop(ex.Failure); }
            if (clock.GetElapsedTime(started) >= options.ProcessingBudget)
                Stop(new(VoiceActivityFailureCode.DeadlineExceeded));
        }
    }

    private void Check()
    {
        ObserveRevocation();
        lock (gate)
        {
            if (stopFailure is not null) throw new VoiceActivityException(stopFailure.Code);
        }
    }

    private void Stop(VoiceActivityFailure failure)
    {
        lock (gate)
        {
            stopFailure ??= failure;
            result = null;
            if (terminal is null)
            {
                terminal = new(stopFailure.Code == VoiceActivityFailureCode.Canceled ? VoiceActivityOutcome.Canceled : VoiceActivityOutcome.Failed,
                    OwnershipState(), stopFailure);
                completion.TrySetResult(terminal);
            }
            if (!retiring && !finished && backend is not null && cancellation is null)
            {
                var owned = backend;
                cancellation = Task.Run(owned.RequestCancellation);
            }
        }
    }

    private void Drive()
    {
        var scores = new List<VoiceActivityWindowScore>(VoiceActivityOptions.HardMaximumScores);
        try
        {
            Check();
            pcm = new byte[checked(input.SampleCount * 2)];
            Check();
            try { input.CopyPcmTo(pcm); }
            catch (ObjectDisposedException) { throw new VoiceActivityException(VoiceActivityFailureCode.InputUnavailable); }
            Check();
            var created = factory.Create();
            lock (gate) backend = created;
            Check();
            created.Load(modelPath, new(Check));
            Check();
            var endpointer = new SpeechEndpointer(options);
            windows = new(options.MaximumInputSamples, (offset, valid, window) =>
            {
                Check();
                var score = created.Score(window);
                Check();
                var frame = new VoiceActivityWindowScore(offset, valid, score);
                frame.Validate();
                VadCheck.Require(scores.Count < VoiceActivityOptions.HardMaximumScores, VoiceActivityFailureCode.PayloadTooLarge);
                scores.Add(frame);
                endpointer.Append(frame);
            });
            for (var offset = 0; offset < pcm.Length; offset += 640)
            {
                Check();
                windows.Append(pcm.AsSpan(offset, Math.Min(640, pcm.Length - offset)));
            }
            Check();
            windows.Complete();
            Check();
            candidate = new(authorization.Binding, factory.Evidence, scores, endpointer.Complete());
        }
        catch (VoiceActivityException ex) { workFailure = ex.Failure; }
        catch (Exception) { workFailure = new(VoiceActivityFailureCode.InferenceFailed); }
        finally
        {
            Task? cancel;
            lock (gate) { retiring = true; cancel = cancellation; }
            if (cancel is not null)
            {
                try { cancel.GetAwaiter().GetResult(); }
                catch (Exception) { releaseFailure = new(VoiceActivityFailureCode.CancellationFailed); }
            }
            try { backend?.Dispose(); }
            catch (Exception) { releaseFailure ??= new(VoiceActivityFailureCode.CleanupFailed); }
        }
    }

    private async Task RetireAsync(Task worker)
    {
        try { await worker.ConfigureAwait(false); }
        catch (Exception) { workFailure ??= new(VoiceActivityFailureCode.InferenceFailed); }
        lock (gate) retiring = true;
        if (watcher is not null)
        {
            try { await watcher.DisposeAsync().ConfigureAwait(false); }
            catch (Exception) { releaseFailure ??= new(VoiceActivityFailureCode.CleanupFailed); }
        }
        if (releaseFailure is null)
        {
            try
            {
                windows?.Dispose();
                if (pcm is not null) CryptographicOperations.ZeroMemory(pcm);
                pcm = null;
                windows = null;
                backend = null;
            }
            catch (Exception) { releaseFailure = new(VoiceActivityFailureCode.CleanupFailed); }
        }
        ObserveRevocation();
        lock (gate)
        {
            finished = true;
            var failure = releaseFailure ?? stopFailure ?? workFailure;
            if (failure is null) result = candidate;
            candidate = null;
            release.TrySetResult(new(releaseFailure is null, releaseFailure));
            if (terminal is null)
            {
                terminal = new(failure is null ? result!.Outcome :
                    failure.Code == VoiceActivityFailureCode.Canceled ? VoiceActivityOutcome.Canceled : VoiceActivityOutcome.Failed,
                    OwnershipState(), failure);
                completion.TrySetResult(terminal);
            }
        }
    }
}
