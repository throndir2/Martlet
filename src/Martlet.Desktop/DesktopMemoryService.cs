using System.IO;
using Martlet.Core.Settings;
using Martlet.Memory;

namespace Martlet.Desktop;

internal enum DesktopMemoryPoint
{
    RetrievalCompleted
}

internal sealed class DesktopMemoryException(string code, string message) : Exception(message)
{
    internal string Code { get; } = code;
}

internal sealed record MemoryConfigurationSaveResult(SetupSaveResult Save, AppSettings Settings);
internal sealed record DesktopMemoryRetrieval(long? StoreRevision, IReadOnlyList<MemoryRetrievalHit> Hits);

internal sealed class DesktopMemoryService : IDisposable
{
    internal const int MaximumRetrievedFacts = 3;

    private readonly object gate = new();
    private readonly SettingsStore settings;
    private readonly TimeProvider clock;
    private readonly Func<MemoryStoreActivationPreview, MemoryStoreAuthorization, CancellationToken, MemoryStore> openStore;
    private TaskCompletionSource? cleanupRetry;
    private CancellationTokenSource invalidation = new();
    private long generation;
    private bool disposed;

    internal Action<DesktopMemoryPoint, CancellationToken>? TestHook { get; set; }
    internal bool HasPendingCleanup { get { lock (gate) return cleanupRetry is not null; } }
    internal string DefaultDirectory =>
        Path.Combine(settings.DataDirectory, MemorySettings.AppLocalDirectoryName);

    internal DesktopMemoryService(SettingsStore settings, TimeProvider? clock = null,
        Func<MemoryStoreActivationPreview, MemoryStoreAuthorization, CancellationToken, MemoryStore>? openStore = null)
    {
        this.settings = settings;
        this.clock = clock ?? TimeProvider.System;
        this.openStore = openStore ?? MemoryStore.Open;
    }

    internal Task<SettingsLoadResult> LoadAsync(CancellationToken token = default) =>
        settings.LoadAsync(token);

    internal async Task<MemoryConfigurationSaveResult> SaveConfigurationAsync(
        AppSettings current,
        string? revision,
        bool enabled,
        bool enableApproved,
        MemoryStoragePolicy policy,
        string? customDirectory,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (enabled && !enableApproved)
            throw new DesktopMemoryException("memory.enable_permission_required",
                "Memory remains OFF until the local scope and behavior are explicitly accepted.");

        var draft = SetupSettings.Begin(current);
        var priorMemory = draft.Memory ?? MemorySettings.Create();
        var nextMemory = priorMemory
            .Configure(enabled, policy, customDirectory);
        var next = draft with { Memory = nextMemory };
        next.Validate();

        Invalidate();
        token.ThrowIfCancellationRequested();
        var scopeChanged = priorMemory.StoragePolicy != nextMemory.StoragePolicy ||
            !string.Equals(priorMemory.CustomDirectory, nextMemory.CustomDirectory,
                StringComparison.Ordinal);
        if (enabled || scopeChanged)
        {
            var preview = MemoryStoreActivationPreview.Create(
                nextMemory.ResolveDirectory(settings.DataDirectory));
            preview.ValidateLocalScope();
        }

        var saved = await settings.SaveAsync(next, revision, token).ConfigureAwait(false);
        return new(new(saved, next), next);
    }

    internal Task<MemoryInspection> InspectAsync(Guid expectedConfigurationRevision,
        CancellationToken token = default) =>
        WithStoreAsync(expectedConfigurationRevision,
            (store, operationToken) => store.InspectAsync(operationToken), token);

    internal Task<MemoryMutationReceipt> SaveFactAsync(
        Guid expectedConfigurationRevision,
        string content,
        MemoryRetention retention,
        CancellationToken token = default)
    {
        Invalidate();
        return WithStoreAsync(expectedConfigurationRevision, (store, operationToken) =>
            store.SaveAsync(new()
            {
                Content = content,
                Provenance = MemoryProvenance.UserEntry(Guid.NewGuid(), CurrentUtc()),
                Retention = retention
            }, operationToken), token);
    }

    internal Task<MemoryMutationReceipt> EditFactAsync(
        Guid expectedConfigurationRevision,
        MemoryFact fact,
        string content,
        MemoryRetention retention,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(fact);
        Invalidate();
        return WithStoreAsync(expectedConfigurationRevision, (store, operationToken) =>
            store.EditAsync(new()
            {
                Id = fact.Id,
                ExpectedRevision = fact.Revision,
                Content = content,
                Provenance = MemoryProvenance.UserEntry(Guid.NewGuid(), CurrentUtc()),
                Retention = retention
            }, operationToken), token);
    }

    internal Task<MemoryDeleteReceipt> DeleteFactAsync(
        Guid expectedConfigurationRevision,
        MemoryFact fact,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(fact);
        Invalidate();
        return WithStoreAsync(expectedConfigurationRevision, (store, operationToken) =>
            store.DeleteAsync(new()
            {
                Id = fact.Id,
                ExpectedRevision = fact.Revision,
                ConsentId = Guid.NewGuid()
            }, operationToken), token);
    }

    internal Task<MemoryExpiryReceipt> PurgeExpiredAsync(
        Guid expectedConfigurationRevision,
        CancellationToken token = default)
    {
        Invalidate();
        return WithStoreAsync(expectedConfigurationRevision,
            (store, operationToken) => store.PurgeExpiredAsync(operationToken), token);
    }

    internal Task<MemoryExportPreview> CreateExportPreviewAsync(
        Guid expectedConfigurationRevision,
        CancellationToken token = default) =>
        WithStoreAsync(expectedConfigurationRevision,
            (store, operationToken) => store.CreateExportPreviewAsync(operationToken), token);

    internal Task<MemoryExportReceipt> ExportAsync(
        Guid expectedConfigurationRevision,
        MemoryExportPreview preview,
        MemoryExportAuthorization authorization,
        string destination,
        CancellationToken token = default) =>
        WithStoreAsync(expectedConfigurationRevision,
            (store, operationToken) => store.ExportAsync(
                preview, authorization, destination, operationToken), token);

    internal async Task<DesktopMemoryRetrieval> RetrieveAsync(
        MemorySettings expected,
        string query,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var snapshot = SnapshotInvalidation();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, snapshot.Token);
        DesktopMemoryRetrieval result;
        try
        {
            var bounded = MemoryQuery.TryFromBoundedSource(query, MaximumRetrievedFacts);
            if (bounded is null)
            {
                await RequireEnabledAsync(expected.ConfigurationRevision, linked.Token)
                    .ConfigureAwait(false);
                result = new(null, Array.Empty<MemoryRetrievalHit>());
            }
            else
            {
                var retrieved = await WithStoreAsync(expected.ConfigurationRevision,
                    (store, operationToken) => store.RetrieveAsync(
                        bounded, operationToken), linked.Token).ConfigureAwait(false);
                result = new(retrieved.StoreRevision, retrieved.Hits);
            }
        }
        catch (OperationCanceledException) when (snapshot.Token.IsCancellationRequested && !token.IsCancellationRequested)
        {
            throw new DesktopMemoryException("memory.retrieval_invalidated",
                "Memory changed or consent was revoked while retrieval was active.");
        }

        TestHook?.Invoke(DesktopMemoryPoint.RetrievalCompleted, linked.Token);
        lock (gate)
        {
            EnsureOpen();
            if (snapshot.Generation != generation || snapshot.Token.IsCancellationRequested)
                throw new DesktopMemoryException("memory.retrieval_invalidated",
                    "Memory changed or consent was revoked while retrieval was active.");
        }
        return result;
    }

    internal void Invalidate()
    {
        CancellationTokenSource previous;
        lock (gate)
        {
            EnsureOpen();
            previous = invalidation;
            invalidation = new();
            generation = checked(generation + 1);
        }
        CancelAndDisposeAsync(previous).Forget();
    }

    private async Task<T> WithStoreAsync<T>(
        Guid expectedConfigurationRevision,
        Func<MemoryStore, CancellationToken, Task<T>> action,
        CancellationToken token)
    {
        EnsureOpen();
        token.ThrowIfCancellationRequested();
        var configured = await RequireEnabledAsync(expectedConfigurationRevision, token)
            .ConfigureAwait(false);
        var preview = MemoryStoreActivationPreview.Create(configured.Directory);
        preview.ValidateLocalScope();
        var approval = preview.Authorize(MemoryConsentDecision.Allow);
        var store = openStore(preview, approval, token);
        try
        {
            return await action(store, token).ConfigureAwait(false);
        }
        finally
        {
            // Cancellation/closed observers cannot abandon private partials or release the app effect slot.
            while (store.HasPendingCleanup)
            {
                Task retry;
                lock (gate)
                {
                    cleanupRetry = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    retry = cleanupRetry.Task;
                }
                await retry.ConfigureAwait(false);
                try
                {
                    await store.RetryCleanupAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (MemoryException error) when (error.Failure is MemoryFailure.CleanupPending or MemoryFailure.AccessDenied)
                {
                    // The same owner remains quarantined until a later explicit retry succeeds.
                }
            }
            store.Dispose();
            lock (gate) cleanupRetry = null;
        }
    }

    internal void RetryCleanup()
    {
        lock (gate)
            cleanupRetry?.TrySetResult();
    }

    private async Task<(MemorySettings Settings, string Directory)> RequireEnabledAsync(
        Guid expectedConfigurationRevision,
        CancellationToken token)
    {
        var loaded = await settings.LoadAsync(token).ConfigureAwait(false);
        if (loaded.State != SettingsLoadState.Loaded || loaded.Error is not null ||
            loaded.Settings?.Memory is not { Enabled: true } memory)
            throw new DesktopMemoryException("memory.disabled",
                "Local memory is OFF. Open Memory, review the scope and enable it explicitly.");
        if (memory.ConfigurationRevision != expectedConfigurationRevision)
            throw new DesktopMemoryException("memory.configuration_changed",
                "Memory configuration changed. Reload and review it before a fresh action.");
        return (memory, memory.ResolveDirectory(settings.DataDirectory));
    }

    private (long Generation, CancellationToken Token) SnapshotInvalidation()
    {
        lock (gate)
        {
            EnsureOpen();
            return (generation, invalidation.Token);
        }
    }

    private DateTimeOffset CurrentUtc()
    {
        var now = clock.GetUtcNow();
        if (now.Offset != TimeSpan.Zero)
            throw new DesktopMemoryException("memory.clock_invalid",
                "The local UTC clock is unavailable for memory provenance.");
        return now;
    }

    private void EnsureOpen()
    {
        lock (gate)
        {
            if (disposed)
                throw new ObjectDisposedException(nameof(DesktopMemoryService));
        }
    }

    public void Dispose()
    {
        CancellationTokenSource source;
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
            generation = checked(generation + 1);
            source = invalidation;
        }
        CancelAndDisposeAsync(source).Forget();
    }

    private static async Task CancelAndDisposeAsync(CancellationTokenSource source)
    {
        try
        {
            await source.CancelAsync().ConfigureAwait(false);
        }
        finally
        {
            source.Dispose();
        }
    }
}
