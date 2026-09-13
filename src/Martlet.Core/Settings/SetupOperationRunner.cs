namespace Martlet.Core.Settings;

public enum SetupWorkOutcome { Completed, Canceled, Failed }

public sealed record SetupWorkResult(SetupWorkOutcome Outcome, SettingsLoadResult? Loaded = null,
    SetupSaveResult? Saved = null, CredentialError? Credential = null);

// Shared by every setup window in one app. Observation ending never releases this worker slot.
public sealed class SetupOperationRunner
{
    private readonly object gate = new();
    private SetupOperation? active;

    public bool IsRunning { get { lock (gate) return active is not null; } }

    // Ownership of the offered secret transfers even on rejection; never abandon a second lease.
    public SetupOperation? TryStart(Func<CancellationToken, Task<SetupWorkResult>> action, SecretLease? secret = null)
    {
        lock (gate)
        {
            if (active is not null)
            {
                secret?.Dispose();
                return null;
            }
            var operation = new SetupOperation();
            active = operation;
            operation.Start(action, secret, () =>
            {
                lock (gate) active = null;
            });
            return operation;
        }
    }

    public void RequestCancellation()
    {
        SetupOperation? operation;
        lock (gate) operation = active;
        operation?.RequestCancellation();
    }
}

public sealed class SetupOperation
{
    private readonly object gate = new();
    private readonly CancellationTokenSource stop = new();
    private Task cancellation = Task.CompletedTask;
    private bool retiring;
    public Task<SetupWorkResult> Completion { get; private set; } = null!;

    internal SetupOperation() { }

    internal void Start(Func<CancellationToken, Task<SetupWorkResult>> action, SecretLease? secret, Action released)
    {
        // Do not pass the token to Task.Run: even a pre-start cancellation must run lease cleanup.
        var work = Task.Run(async () =>
        {
            stop.Token.ThrowIfCancellationRequested();
            return await action(stop.Token).ConfigureAwait(false);
        });
        Completion = work.ContinueWith(async completed =>
        {
            Task callbacks;
            lock (gate) { retiring = true; callbacks = cancellation; }
            // Cancellation callbacks, like native calls, may be non-cooperative. Keep ownership.
            await Task.WhenAny(callbacks).ConfigureAwait(false);
            _ = completed.Exception;
            _ = callbacks.Exception;
            try
            {
                if (completed.IsFaulted || callbacks.IsFaulted)
                    return new SetupWorkResult(SetupWorkOutcome.Failed);
                if (completed.IsCanceled)
                    return new SetupWorkResult(SetupWorkOutcome.Canceled);
                return completed.Result ?? new SetupWorkResult(SetupWorkOutcome.Failed);
            }
            finally
            {
                secret?.Dispose();
                stop.Dispose();
                released();
            }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
    }

    public void RequestCancellation()
    {
        lock (gate)
        {
            if (!retiring && !stop.IsCancellationRequested)
                cancellation = stop.CancelAsync();
        }
    }
}
