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
/// <summary>Facts recalled for one turn: the best lexical matches first, then the most recently changed facts (the speaker's own
/// and those about no one in particular before other people's). <paramref name="People"/> labels the voices the facts belong to.</summary>
internal sealed record DesktopMemoryRecall(long? StoreRevision, IReadOnlyList<MemoryFact> Facts,
    IReadOnlyDictionary<string, string>? People = null);
/// <summary>One change remembering made: <paramref name="VoiceId"/> is whose fact it is, <paramref name="Person"/> its label.</summary>
internal sealed record MemoryCaptureChange(MemoryCaptureKind Kind, string Content, string? VoiceId = null, string? Person = null);

internal sealed class DesktopMemoryService : IDisposable
{
    internal const int MaximumRecalledFacts = 12;
    private static readonly TimeSpan StoreWait = TimeSpan.FromSeconds(10);

    private readonly object gate = new();
    // One store owner at a time in this process: conversation recall, background remembering and Memory window actions.
    private readonly SemaphoreSlim storeGate = new(1, 1);
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
        MemoryStoragePolicy policy,
        string? customDirectory,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(current);
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

    /// <param name="voiceId">Whose fact it is (a voice list ID), or null for no one in particular.</param>
    internal Task<MemoryMutationReceipt> SaveFactAsync(
        Guid expectedConfigurationRevision,
        string content,
        MemoryRetention retention,
        string? voiceId = null,
        CancellationToken token = default)
    {
        Invalidate();
        return WithStoreAsync(expectedConfigurationRevision, (store, operationToken) =>
            store.SaveAsync(new()
            {
                Content = content,
                Provenance = MemoryProvenance.UserEntry(Guid.NewGuid(), CurrentUtc()),
                Retention = retention,
                VoiceId = voiceId
            }, operationToken), token);
    }

    /// <param name="voiceId">Whose fact it is after the edit; pass the fact's own to keep it.</param>
    internal Task<MemoryMutationReceipt> EditFactAsync(
        Guid expectedConfigurationRevision,
        MemoryFact fact,
        string content,
        MemoryRetention retention,
        string? voiceId,
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
                Retention = retention,
                VoiceId = voiceId
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

    /// <param name="speaker">The voice IDs of the person speaking (<see cref="MemoryPeople.Ids"/>), whose facts and those about no
    /// one in particular fill the recall before other people's; null when nobody was recognized.</param>
    internal async Task<DesktopMemoryRecall> RecallAsync(
        MemorySettings expected,
        string query,
        int maximum = MaximumRecalledFacts,
        IReadOnlySet<string>? speaker = null,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximum, 1);
        var snapshot = SnapshotInvalidation();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, snapshot.Token);
        DesktopMemoryRecall result;
        try
        {
            result = await WithStoreAsync(expected.ConfigurationRevision,
                (store, operationToken) => RecallAsync(store, query, maximum, speaker, operationToken),
                linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (snapshot.Token.IsCancellationRequested && !token.IsCancellationRequested)
        {
            throw Invalidated();
        }

        TestHook?.Invoke(DesktopMemoryPoint.RetrievalCompleted, linked.Token);
        lock (gate)
        {
            EnsureOpen();
            if (snapshot.Generation != generation || snapshot.Token.IsCancellationRequested)
                throw Invalidated();
        }
        return result;
    }

    /// <summary>Related facts shown to the model while remembering. Unlike a turn's recall it is not invalidated by Stop;
    /// <see cref="RememberAsync"/> rechecks every fact revision it acts on.</summary>
    internal Task<DesktopMemoryRecall> KnownFactsAsync(MemorySettings expected, string query, int maximum,
        IReadOnlySet<string>? speaker = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximum, 1);
        return WithStoreAsync(expected.ConfigurationRevision,
            (store, operationToken) => RecallAsync(store, query, maximum, speaker, operationToken), token);
    }

    private static async Task<DesktopMemoryRecall> RecallAsync(MemoryStore store, string query, int maximum,
        IReadOnlySet<string>? speaker, CancellationToken token)
    {
        var facts = new List<MemoryFact>(maximum);
        var included = new HashSet<Guid>();
        if (MemoryQuery.TryFromBoundedSource(query, Math.Min(maximum, MemoryLimits.MaximumResults)) is { } bounded)
            foreach (var hit in (await store.RetrieveAsync(bounded, token).ConfigureAwait(false)).Hits)
                if (included.Add(hit.Fact.Id))
                    facts.Add(hit.Fact);
        var inspection = await store.InspectAsync(token).ConfigureAwait(false);
        // Someone else's facts come after the speaker's own and those about no one in particular (all by recency when nobody
        // was recognized); the best matches above already include anyone's.
        foreach (var fact in inspection.Facts
            .OrderByDescending(fact => speaker is null || fact.VoiceId is null || speaker.Contains(fact.VoiceId))
            .ThenByDescending(fact => fact.UpdatedAtUtc).ThenBy(fact => fact.Id))
        {
            if (facts.Count >= maximum)
                break;
            if (included.Add(fact.Id))
                facts.Add(fact);
        }
        return new(inspection.StoreRevision, facts);
    }

    /// <summary>Applies what the model picked out of a conversation. New facts belong to the voice each operation names (the
    /// speaker's, unless the model named another voice heard). Near-duplicates of the same person's facts (or of facts about no
    /// one in particular) are skipped, updates keep whose fact it is, updates and forgets only touch the exact fact revisions
    /// that were shown to the model, and a full store makes room by dropping the oldest conversation fact (never one the user
    /// typed).</summary>
    /// <param name="person">Maps a voice ID to the voice it stands for now (<see cref="MemoryPeople.Canonical"/>), so merged
    /// voices count as one person; IDs compare as they are without it.</param>
    internal Task<IReadOnlyList<MemoryCaptureChange>> RememberAsync(
        Guid expectedConfigurationRevision,
        IReadOnlyList<MemoryFact> shown,
        IReadOnlyList<MemoryCaptureOperation> operations,
        Func<string?, string?>? person = null,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(shown);
        ArgumentNullException.ThrowIfNull(operations);
        person ??= id => id;
        // A fact about no one in particular already covers anyone's same fact, and an unattributed one anyone's.
        bool SamePerson(string? left, string? right) =>
            left is null || right is null || string.Equals(person(left), person(right), StringComparison.Ordinal);
        return WithStoreAsync(expectedConfigurationRevision, async (store, operationToken) =>
        {
            var changes = new List<MemoryCaptureChange>();
            var current = (await store.InspectAsync(operationToken).ConfigureAwait(false)).Facts.ToList();
            foreach (var operation in operations)
            {
                var target = operation.Index is { } index && index >= 1 && index <= shown.Count &&
                    current.FirstOrDefault(fact => fact.Id == shown[index - 1].Id) is { } found &&
                    found.Revision == shown[index - 1].Revision ? found : null;
                switch (operation.Kind)
                {
                    case MemoryCaptureKind.Remember when operation.Content is { } content:
                        if (current.Any(fact => MemoryCapture.SameFact(fact.Content, content) && SamePerson(fact.VoiceId, operation.VoiceId)))
                            continue;
                        if (current.Count >= MemoryLimits.MaximumFacts)
                        {
                            var oldest = current.Where(fact => fact.LastModifiedBy.SourceKind == MemorySourceKind.Conversation)
                                .OrderBy(fact => fact.UpdatedAtUtc).FirstOrDefault();
                            if (oldest is null)
                                continue;
                            await store.DeleteAsync(new()
                            {
                                Id = oldest.Id, ExpectedRevision = oldest.Revision, ConsentId = Guid.NewGuid()
                            }, operationToken).ConfigureAwait(false);
                            current.Remove(oldest);
                        }
                        var saved = await store.SaveAsync(new()
                        {
                            Content = content,
                            Provenance = MemoryProvenance.Conversation(Guid.NewGuid(), CurrentUtc()),
                            Retention = MemoryRetention.UntilDeleted(),
                            VoiceId = operation.VoiceId
                        }, operationToken).ConfigureAwait(false);
                        current.Add(saved.Fact);
                        changes.Add(new(MemoryCaptureKind.Remember, saved.Fact.Content, saved.Fact.VoiceId));
                        break;
                    case MemoryCaptureKind.Update when target is not null && operation.Content is { } content:
                        if (MemoryCapture.SameFact(target.Content, content) ||
                            current.Any(fact => fact.Id != target.Id && MemoryCapture.SameFact(fact.Content, content) &&
                                SamePerson(fact.VoiceId, target.VoiceId)))
                            continue;
                        var edited = await store.EditAsync(new()
                        {
                            Id = target.Id, ExpectedRevision = target.Revision, Content = content,
                            Provenance = MemoryProvenance.Conversation(Guid.NewGuid(), CurrentUtc()),
                            Retention = target.Retention, VoiceId = target.VoiceId
                        }, operationToken).ConfigureAwait(false);
                        current[current.IndexOf(target)] = edited.Fact;
                        changes.Add(new(MemoryCaptureKind.Update, edited.Fact.Content, edited.Fact.VoiceId));
                        break;
                    case MemoryCaptureKind.Forget when target is not null:
                        await store.DeleteAsync(new()
                        {
                            Id = target.Id, ExpectedRevision = target.Revision, ConsentId = Guid.NewGuid()
                        }, operationToken).ConfigureAwait(false);
                        current.Remove(target);
                        changes.Add(new(MemoryCaptureKind.Forget, target.Content, target.VoiceId));
                        break;
                }
            }
            return (IReadOnlyList<MemoryCaptureChange>)changes;
        }, token);
    }

    private static DesktopMemoryException Invalidated() => new("memory.retrieval_invalidated",
        "Memory changed while it was being read.");

    /// <summary>Runs the memory sync's local step with the store, only while memory is on and nobody else holds the store
    /// right now (a conversation's recall or remembering never waits for it). Ran is false, with why, when it didn't run.</summary>
    internal async Task<(bool Ran, string? Why, T? Result)> TryWithFreeStoreAsync<T>(
        Func<MemoryStore, CancellationToken, Task<T>> action, CancellationToken token = default)
    {
        EnsureOpen();
        var loaded = await settings.LoadAsync(token).ConfigureAwait(false);
        if (loaded.State != SettingsLoadState.Loaded || loaded.Error is not null || loaded.Settings?.Memory is not { Enabled: true } memory)
            return (false, "off", default);
        if (HasPendingCleanup) return (false, "busy", default);
        try
        {
            return (true, null, await WithStoreAsync(memory.ConfigurationRevision, action, token, TimeSpan.Zero).ConfigureAwait(false));
        }
        catch (DesktopMemoryException error) when (error.Code is "memory.busy" or "memory.disabled" or "memory.configuration_changed")
        {
            return (false, error.Code == "memory.busy" ? "busy" : "off", default);
        }
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
        CancellationToken token,
        TimeSpan? wait = null)
    {
        EnsureOpen();
        token.ThrowIfCancellationRequested();
        var configured = await RequireEnabledAsync(expectedConfigurationRevision, token)
            .ConfigureAwait(false);
        var preview = MemoryStoreActivationPreview.Create(configured.Directory);
        preview.ValidateLocalScope();
        var approval = preview.Authorize(MemoryConsentDecision.Allow);
        if (!await storeGate.WaitAsync(wait ?? StoreWait, token).ConfigureAwait(false))
            throw new DesktopMemoryException("memory.busy",
                "Memory is busy. Try again in a moment.");
        MemoryStore store;
        try
        {
            store = openStore(preview, approval, token);
        }
        catch
        {
            storeGate.Release();
            throw;
        }
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
            storeGate.Release();
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
                "Memory is off. Turn it on in Memory to remember and recall facts.");
        if (memory.ConfigurationRevision != expectedConfigurationRevision)
            throw new DesktopMemoryException("memory.configuration_changed",
                "Memory settings changed. Reload and try again.");
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
                "The system clock needs UTC support for memory.");
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
