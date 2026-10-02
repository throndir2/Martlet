using Martlet.Core.Settings;

namespace Martlet.Desktop;

// App-lifetime presentation-independent owner. Closing a window never releases native IO.
internal sealed class ConfigurationRecoveryController(SettingsStore store, SetupOperationRunner operations,
    Func<bool>? mayUseStorage = null)
{
    private readonly object gate = new();
    private SetupOperation? active;
    private ConfigurationRestorePlan? preview;
    private ConfigurationRecoveryReceipt? receipt;
    private string? cleanup;
    private long presentation;
    private string message = "No backup has been read or written. Choose a local file and action.";
    internal bool IsBusy => operations.IsRunning;
    internal bool HasResources { get { lock (gate) return active is { Completion.IsCompleted: false } || cleanup is not null; } }
    internal bool NeedsCleanup { get { lock (gate) return cleanup is not null; } }
    internal ConfigurationRestorePlan? Preview { get { lock (gate) return preview; } }
    internal ConfigurationRecoveryReceipt? Receipt { get { lock (gate) return receipt; } }
    internal string Message { get { lock (gate) return message +
        (active is { Completion.IsCompletedSuccessfully: true } && active.Completion.Result.Outcome == SetupWorkOutcome.Failed
            ? "\nThe last action failed. Review the result before trying again." : ""); } }

    internal void ClearPreview()
    {
        lock (gate) { presentation++; preview = null; }
    }

    internal SetupOperation? Backup(string destination) => Start(async token =>
    {
        var saved = await store.CreateConfigurationSnapshotAsync(destination, token).ConfigureAwait(false);
        lock (gate) { receipt = saved; message = $"Settings backup created: {saved.Path}"; }
    });

    internal SetupOperation? ReadPreview(string source)
    {
        ClearPreview();
        long version;
        lock (gate) version = presentation;
        return Start(async token =>
        {
            var plan = await store.PreviewConfigurationRestoreAsync(source, token).ConfigureAwait(false);
            lock (gate)
            {
                if (presentation != version || token.IsCancellationRequested) return;
                preview = plan;
                message = "Restore preview ready. Nothing has been changed.";
            }
        });
    }

    internal SetupOperation? Restore(ConfigurationRestorePlan plan, ConfigurationRestoreApproval approval)
    {
        lock (gate)
        {
            if (!ReferenceEquals(preview, plan))
            {
                message = "Preview changed. Read and review a fresh snapshot; confirmation was rejected.";
                return null;
            }
        }
        return Start(async token =>
        {
            ClearPreview();
            var restored = await store.RestoreConfigurationAsync(plan, approval, token).ConfigureAwait(false);
            lock (gate)
            {
                receipt = restored;
                message = "Settings restored. Reload setup and review destinations, keys and devices.";
            }
        });
    }

    internal SetupOperation? RetryCleanup() => Start(_ =>
    {
        string? path;
        lock (gate) path = cleanup;
        if (path is null) throw new RecoveryException(RecoveryFailure.Conflict);
        store.RetryRecoveryCleanup(path);
        lock (gate) { cleanup = null; message = "Cleanup finished. Read a fresh preview before trying again."; }
        return Task.CompletedTask;
    }, isCleanup: true);

    private SetupOperation? Start(Func<CancellationToken, Task> action, bool isCleanup = false)
    {
        // This predicate observes the existing support owner only; it never starts/stops the journal.
        if (mayUseStorage?.Invoke() == false)
        {
            lock (gate) message = "Close Troubleshooting before using backup and restore.";
            return null;
        }
        lock (gate)
        {
            if (operations.IsRunning || (cleanup is not null && !isCleanup))
            {
                message = "Another action is still finishing, or cleanup is pending. Wait, then retry cleanup or read a fresh preview.";
                return null;
            }
            active = operations.TryStart(async token =>
            {
                try
                {
                    await action(token).ConfigureAwait(false);
                    return new(SetupWorkOutcome.Completed);
                }
                catch (RecoveryException error)
                {
                    lock (gate)
                    {
                        preview = null;
                        cleanup = error.RetainedFile ?? cleanup;
                        message = error.Message +
                            (cleanup is null ? "" : "\nCleanup is needed before trying again.");
                    }
                    return new(SetupWorkOutcome.Failed);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    lock (gate) { preview = null; message = "Recovery canceled. Wait for the action to finish, then read a fresh preview."; }
                    return new(SetupWorkOutcome.Canceled);
                }
            });
            return active;
        }
    }

    internal void StopObserving()
    {
        SetupOperation? owned;
        lock (gate) { presentation++; preview = null; owned = active; }
        owned?.RequestCancellation();
    }
}
