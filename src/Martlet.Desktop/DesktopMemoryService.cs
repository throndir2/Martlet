using System.IO;
using Martlet.Core.Settings;
using Martlet.Core.Sync;
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
/// and those about no one in particular before other people's). <paramref name="People"/> labels the voices the facts belong to.
/// <paramref name="Spaces"/> names the memory space of each fact kept in another space than the active one (null when none is).</summary>
internal sealed record DesktopMemoryRecall(long? StoreRevision, IReadOnlyList<MemoryFact> Facts,
    IReadOnlyDictionary<string, string>? People = null, IReadOnlyDictionary<Guid, string>? Spaces = null);
/// <summary>One change remembering made: <paramref name="VoiceId"/> is whose fact it is, <paramref name="Person"/> its label.</summary>
internal sealed record MemoryCaptureChange(MemoryCaptureKind Kind, string Content, string? VoiceId = null, string? Person = null);
/// <summary>The facts of one memory space, as the Memory window lists them.</summary>
internal sealed record MemorySpaceFacts(MemorySpace Space, MemoryInspection Inspection);

/// <summary>Martlet's memory on this device: one Martlet.Memory store per memory space of the account signed in (docs/MEMORY.md,
/// "Memory spaces on the desktop"). Recall, remembering and the reply's tools open the active space's store as before accounts;
/// the other spaces recall reads (household, spaces shared with the account) are kept as read-only snapshots, loaded at sign-in
/// or switch and after each change, so recall over several spaces costs what recall over one did.</summary>
internal sealed class DesktopMemoryService : IDisposable
{
    internal const int MaximumRecalledFacts = 12;
    private static readonly TimeSpan StoreWait = TimeSpan.FromSeconds(10);
    private static readonly IReadOnlyList<(string Space, MemorySnapshot Snapshot)> NoSnapshots = [];

    private readonly object gate = new();
    // One owner at a time per store in this process: conversation recall, background remembering, Memory window actions and the
    // sync. Each space's store has its own gate, so syncing the household never holds up recall of the account's space.
    private readonly Dictionary<string, SemaphoreSlim> storeGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Guid> migrated = [];
    private readonly SettingsStore settings;
    private readonly TimeProvider clock;
    private readonly Func<MemoryStoreActivationPreview, MemoryStoreAuthorization, CancellationToken, MemoryStore> openStore;
    private TaskCompletionSource? cleanupRetry;
    private CancellationTokenSource invalidation = new();
    private long generation;
    private MemoryAccount? account;
    private long accountVersion;
    private IReadOnlyList<(string Space, MemorySnapshot Snapshot)> snapshots = NoSnapshots;
    private bool disposed;

    internal Action<DesktopMemoryPoint, CancellationToken>? TestHook { get; set; }
    internal bool HasPendingCleanup { get { lock (gate) return cleanupRetry is not null; } }

    /// <summary>Raised off the dispatcher, once the store is free again, after work that can add, change or delete facts used
    /// it: the Memory window, remembering after a reply, the reply model's manage_memories tool, removing expired facts or the
    /// memory sync. The Memory window reads the facts again, so it always shows what Martlet remembers now.</summary>
    internal event Action? FactsChanged;

    /// <summary>The account whose memories this device uses (null: no account, one store as before accounts).</summary>
    internal MemoryAccount? Account { get { lock (gate) return account; } }

    internal string DefaultDirectory => Path.Combine(Account?.Folder ?? settings.DataDirectory, MemorySettings.AppLocalDirectoryName);

    /// <summary>The read-only copies of the other spaces recall reads, as loaded now (space ID and fact count, for status).</summary>
    internal IReadOnlyList<(string Space, MemorySnapshot Snapshot)> Snapshots { get { lock (gate) return snapshots; } }

    /// <summary>Uses <paramref name="next"/>'s memories from now on (sign-in or switch; never during a reply). Recall in flight
    /// is invalidated and the other spaces' snapshots are dropped until <see cref="LoadSpacesAsync"/> loads them again.</summary>
    internal void UseAccount(MemoryAccount? next)
    {
        lock (gate)
        {
            EnsureOpen();
            if (Equals(account, next)) return;
            account = next;
            accountVersion++;
            snapshots = NoSnapshots;
        }
        Invalidate();
    }

    /// <summary>The spaces of the account signed in with the memory settings saved now, or null while memory is off.</summary>
    internal async Task<MemorySpaceSet?> SpacesAsync(CancellationToken token = default)
    {
        var loaded = await settings.LoadAsync(token).ConfigureAwait(false);
        return loaded.State == SettingsLoadState.Loaded && loaded.Error is null && loaded.Settings?.Memory is { Enabled: true } memory
            ? MemorySpaces.Resolve(Account, memory, settings.DataDirectory) : null;
    }

    /// <summary>Loads a read-only snapshot of every space recall reads besides the active one (household and spaces shared with
    /// the account). Run at start and after sign-in or a switch, in the background: recall never waits for it, and recall before
    /// it finishes reads the active space alone. Returns how many spaces it loaded; a space that can't be read now is skipped
    /// and loaded after its next change.</summary>
    internal async Task<int> LoadSpacesAsync(CancellationToken token = default)
    {
        EnsureOpen();
        long version;
        lock (gate) version = accountVersion;
        if (await SpacesAsync(token).ConfigureAwait(false) is not { } set) return 0;
        var loaded = 0;
        foreach (var space in set.Others)
        {
            try
            {
                await OpenSpaceAsync(space, null, (_, store, operationToken) => store.SnapshotAsync(operationToken), token,
                    snapshot: true).ConfigureAwait(false);
                loaded++;
            }
            catch (Exception error) when (error is MemoryException or DesktopMemoryException or IOException or UnauthorizedAccessException)
            {
                ErrorLog.Warn($"Memory: couldn't read the memory space \"{MemorySpaces.Label(space.Id, Account)}\" yet; recall " +
                    "leaves it out until it changes.", error);
            }
            lock (gate) if (version != accountVersion) return loaded;
        }
        return loaded;
    }

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
                nextMemory.ResolveDirectory(Account?.Folder ?? settings.DataDirectory));
            preview.ValidateLocalScope();
        }

        var saved = await settings.SaveAsync(next, revision, token).ConfigureAwait(false);
        return new(new(saved, next), next);
    }

    internal Task<MemoryInspection> InspectAsync(Guid expectedConfigurationRevision,
        CancellationToken token = default) =>
        WithStoreAsync(expectedConfigurationRevision,
            (store, operationToken) => store.InspectAsync(operationToken), token);

    /// <summary>The facts of every space the account reads, the active space first (the Memory window). Each store is opened
    /// in turn, so the snapshots of the other spaces are refreshed on the way.</summary>
    internal async Task<IReadOnlyList<MemorySpaceFacts>> InspectSpacesAsync(Guid expectedConfigurationRevision,
        CancellationToken token = default)
    {
        var (_, set) = await RequireEnabledAsync(expectedConfigurationRevision, token).ConfigureAwait(false);
        var spaces = new List<MemorySpaceFacts>(set.Readable.Count);
        foreach (var space in set.Readable)
            spaces.Add(await WithSpaceAsync(expectedConfigurationRevision, space.Id, async (_, found, store, operationToken) =>
                new MemorySpaceFacts(found, await store.InspectAsync(operationToken).ConfigureAwait(false)), token,
                snapshot: true).ConfigureAwait(false));
        return spaces;
    }

    /// <param name="voiceId">Whose fact it is (a voice list ID), or null for no one in particular.</param>
    /// <param name="space">The memory space to keep it in; null: the active space.</param>
    internal Task<MemoryMutationReceipt> SaveFactAsync(
        Guid expectedConfigurationRevision,
        string content,
        MemoryRetention retention,
        string? voiceId = null,
        CancellationToken token = default,
        string? space = null)
    {
        Invalidate();
        return WithSpaceAsync(expectedConfigurationRevision, space, (_, found, store, operationToken) =>
            RequireWritable(found, store).SaveAsync(new()
            {
                Content = content,
                Provenance = MemoryProvenance.UserEntry(Guid.NewGuid(), CurrentUtc()),
                Retention = retention,
                VoiceId = voiceId
            }, operationToken), token, changes: true);
    }

    /// <param name="voiceId">Whose fact it is after the edit; pass the fact's own to keep it.</param>
    /// <param name="space">The memory space the fact is in; null: the active space.</param>
    internal Task<MemoryMutationReceipt> EditFactAsync(
        Guid expectedConfigurationRevision,
        MemoryFact fact,
        string content,
        MemoryRetention retention,
        string? voiceId,
        CancellationToken token = default,
        string? space = null)
    {
        ArgumentNullException.ThrowIfNull(fact);
        Invalidate();
        return WithSpaceAsync(expectedConfigurationRevision, space, (_, found, store, operationToken) =>
            RequireWritable(found, store).EditAsync(new()
            {
                Id = fact.Id,
                ExpectedRevision = fact.Revision,
                Content = content,
                Provenance = MemoryProvenance.UserEntry(Guid.NewGuid(), CurrentUtc()),
                Retention = retention,
                VoiceId = voiceId
            }, operationToken), token, changes: true);
    }

    /// <param name="space">The memory space the fact is in; null: the active space.</param>
    internal Task<MemoryDeleteReceipt> DeleteFactAsync(
        Guid expectedConfigurationRevision,
        MemoryFact fact,
        CancellationToken token = default,
        string? space = null)
    {
        ArgumentNullException.ThrowIfNull(fact);
        Invalidate();
        return WithSpaceAsync(expectedConfigurationRevision, space, (_, found, store, operationToken) =>
            RequireWritable(found, store).DeleteAsync(new()
            {
                Id = fact.Id,
                ExpectedRevision = fact.Revision,
                ConsentId = Guid.NewGuid()
            }, operationToken), token, changes: true);
    }

    /// <summary>Deletes all of <paramref name="facts"/> in one commit; facts already gone are skipped.</summary>
    /// <param name="space">The memory space the facts are in; null: the active space.</param>
    internal Task<MemoryExpiryReceipt> DeleteFactsAsync(
        Guid expectedConfigurationRevision,
        IReadOnlyCollection<MemoryFact> facts,
        CancellationToken token = default,
        string? space = null)
    {
        ArgumentNullException.ThrowIfNull(facts);
        Invalidate();
        return WithSpaceAsync(expectedConfigurationRevision, space, (_, found, store, operationToken) =>
            RequireWritable(found, store).DeleteManyAsync(facts.Select(fact => new DeleteFactRequest
            {
                Id = fact.Id,
                ExpectedRevision = fact.Revision,
                ConsentId = Guid.NewGuid()
            }).ToArray(), operationToken), token, changes: true);
    }

    /// <summary>Runs <paramref name="action"/> with the active space's store (the reply model's manage_memories tool). Pass
    /// <paramref name="changes"/> when it may change facts, so a turn's recall in flight reads them again.</summary>
    internal Task<T> UseStoreAsync<T>(Guid expectedConfigurationRevision, bool changes,
        Func<MemoryStore, CancellationToken, Task<T>> action, CancellationToken token = default)
    {
        if (changes) Invalidate();
        return WithStoreAsync(expectedConfigurationRevision, action, token, changes: changes);
    }

    /// <summary>Runs <paramref name="action"/> with the store of <paramref name="space"/> (null: the active space) and the
    /// account's spaces. A change in a space this device may not change is refused (<c>memory.read_only</c>).</summary>
    internal Task<T> UseSpaceAsync<T>(Guid expectedConfigurationRevision, string? space, bool changes,
        Func<MemorySpaceSet, MemorySpace, MemoryStore, CancellationToken, Task<T>> action, CancellationToken token = default)
    {
        if (changes) Invalidate();
        return WithSpaceAsync(expectedConfigurationRevision, space,
            (set, found, store, operationToken) => action(set, found, changes ? RequireWritable(found, store) : store, operationToken),
            token, changes: changes, snapshot: !changes);
    }

    /// <summary>The live facts of the spaces recall reads besides the active one, from their snapshots (the reply's tools).</summary>
    internal IReadOnlyList<(string Space, MemoryFact Fact)> OtherFacts(MemorySpaceSet set)
    {
        var now = CurrentUtc();
        return [.. OtherSnapshots(set).SelectMany(s => s.Snapshot.Live(now).Select(fact => (s.Space, fact)))];
    }

    internal DateTimeOffset UtcNow => CurrentUtc();

    internal Task<MemoryExpiryReceipt> PurgeExpiredAsync(
        Guid expectedConfigurationRevision,
        CancellationToken token = default)
    {
        Invalidate();
        return WithStoreAsync(expectedConfigurationRevision,
            (store, operationToken) => store.PurgeExpiredAsync(operationToken), token, changes: true);
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
            result = await WithSpaceAsync(expected.ConfigurationRevision, null,
                (set, _, store, operationToken) => RecallAsync(store, OtherSnapshots(set), CurrentUtc(), query, maximum, speaker,
                    operationToken),
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
        return WithSpaceAsync(expected.ConfigurationRevision, null,
            (set, _, store, operationToken) => RecallAsync(store, OtherSnapshots(set), CurrentUtc(), query, maximum, speaker,
                operationToken), token);
    }

    /// <summary>The best lexical matches of the active store and of the <paramref name="others"/>' snapshots first (by score,
    /// then the active space), then the most recently changed facts of them all: the speaker's own and those about no one in
    /// particular before other people's. With no other space this is exactly the recall of one store.</summary>
    private static async Task<DesktopMemoryRecall> RecallAsync(MemoryStore store,
        IReadOnlyList<(string Space, MemorySnapshot Snapshot)> others, DateTimeOffset now, string query, int maximum,
        IReadOnlySet<string>? speaker, CancellationToken token)
    {
        var facts = new List<MemoryFact>(maximum);
        var included = new HashSet<Guid>();
        Dictionary<Guid, string>? spaces = null;
        void Add(MemoryFact fact, string? space)
        {
            if (!included.Add(fact.Id)) return;
            facts.Add(fact);
            if (space is not null) (spaces ??= [])[fact.Id] = space;
        }
        if (MemoryQuery.TryFromBoundedSource(query, Math.Min(maximum, MemoryLimits.MaximumResults)) is { } bounded)
        {
            IEnumerable<(MemoryRetrievalHit Hit, string? Space, int Order)> hits =
                (await store.RetrieveAsync(bounded, token).ConfigureAwait(false)).Hits.Select(hit => (hit, (string?)null, 0)).ToArray();
            if (others.Count > 0)
                hits = hits.Concat(others.SelectMany((other, index) =>
                        other.Snapshot.Search(bounded, now, token).Select(hit => (hit, (string?)other.Space, index + 1))))
                    .OrderByDescending(h => h.Hit.Score).ThenByDescending(h => h.Hit.MatchedTerms).ThenBy(h => h.Order)
                    .ThenByDescending(h => h.Hit.Fact.UpdatedAtUtc).ThenBy(h => h.Hit.Fact.Id)
                    .Take(bounded.MaximumResults);
            foreach (var (hit, space, _) in hits)
                Add(hit.Fact, space);
        }
        var inspection = await store.InspectAsync(token).ConfigureAwait(false);
        // Someone else's facts come after the speaker's own and those about no one in particular (all by recency when nobody
        // was recognized); the best matches above already include anyone's.
        var pool = inspection.Facts.Select(fact => (Fact: fact, Space: (string?)null))
            .Concat(others.SelectMany(other => other.Snapshot.Live(now).Select(fact => (fact, (string?)other.Space))));
        foreach (var (fact, space) in pool
            .OrderByDescending(f => speaker is null || f.Fact.VoiceId is null || speaker.Contains(f.Fact.VoiceId))
            .ThenByDescending(f => f.Fact.UpdatedAtUtc).ThenBy(f => f.Fact.Id))
        {
            if (facts.Count >= maximum)
                break;
            Add(fact, space);
        }
        return new(inspection.StoreRevision, facts, Spaces: spaces);
    }

    /// <summary>The loaded snapshots of <paramref name="set"/>'s spaces besides the active one, in the set's order.</summary>
    private IReadOnlyList<(string Space, MemorySnapshot Snapshot)> OtherSnapshots(MemorySpaceSet set)
    {
        IReadOnlyList<(string Space, MemorySnapshot Snapshot)> loaded;
        lock (gate) loaded = snapshots;
        if (loaded.Count == 0) return NoSnapshots;
        return [.. set.Others.Select(space => loaded.FirstOrDefault(s => s.Space == space.Id)).Where(s => s.Snapshot is not null)];
    }

    /// <summary>Applies what the model picked out of a conversation. New facts belong to the voice each operation names (the
    /// speaker's, unless the model named another voice heard). Near-duplicates of the same person's facts (or of facts about no
    /// one in particular) are skipped, updates keep whose fact it is, updates and forgets only touch the exact fact revisions
    /// that were shown to the model, and a full store makes room by dropping the oldest conversation fact (never one the user
    /// typed).</summary>
    /// <param name="person">Maps a voice ID to the voice it stands for now (<see cref="MemoryPeople.Canonical"/>), so merged
    /// voices count as one person; IDs compare as they are without it.</param>
    /// <param name="spaces">Where each shown fact from another space than the active one is (<see cref="DesktopMemoryRecall.Spaces"/>).
    /// New facts go to the active space; an update or a forget acts in the fact's own space when this device may change it, and
    /// is skipped when it may not. A near-duplicate of a fact in any space recall reads is not saved again.</param>
    internal async Task<IReadOnlyList<MemoryCaptureChange>> RememberAsync(
        Guid expectedConfigurationRevision,
        IReadOnlyList<MemoryFact> shown,
        IReadOnlyList<MemoryCaptureOperation> operations,
        Func<string?, string?>? person = null,
        CancellationToken token = default,
        IReadOnlyDictionary<Guid, string>? spaces = null)
    {
        ArgumentNullException.ThrowIfNull(shown);
        ArgumentNullException.ThrowIfNull(operations);
        person ??= id => id;
        // A fact about no one in particular already covers anyone's same fact, and an unattributed one anyone's.
        bool SamePerson(string? left, string? right) =>
            left is null || right is null || string.Equals(person(left), person(right), StringComparison.Ordinal);
        string? SpaceOf(MemoryCaptureOperation operation) => operation.Kind != MemoryCaptureKind.Remember &&
            operation.Index is { } index && index >= 1 && index <= shown.Count ? spaces?.GetValueOrDefault(shown[index - 1].Id) : null;
        var changes = new List<MemoryCaptureChange>();
        var here = operations.Where(operation => SpaceOf(operation) is null).ToArray();
        if (here.Length > 0)
            changes.AddRange(await WithSpaceAsync(expectedConfigurationRevision, null, (set, _, store, operationToken) =>
                ApplyCaptureAsync(store, shown, here, [.. OtherFacts(set).Select(f => f.Fact)], SamePerson, operationToken),
                token, changes: true).ConfigureAwait(false));
        foreach (var elsewhere in operations.Where(operation => SpaceOf(operation) is not null).GroupBy(SpaceOf))
        {
            try
            {
                changes.AddRange(await WithSpaceAsync(expectedConfigurationRevision, elsewhere.Key, (_, space, store, operationToken) =>
                    ApplyCaptureAsync(RequireWritable(space, store), shown, [.. elsewhere], [], SamePerson, operationToken),
                    token, changes: true).ConfigureAwait(false));
            }
            // Only those who may change that space change its facts.
            catch (DesktopMemoryException error) when (error.Code is "memory.read_only" or "memory.space_unknown") { }
        }
        return changes;
    }

    /// <summary>Applies <paramref name="operations"/> to one store; <paramref name="elsewhere"/> are facts of other spaces that a
    /// new or updated fact must not repeat.</summary>
    private async Task<IReadOnlyList<MemoryCaptureChange>> ApplyCaptureAsync(MemoryStore store, IReadOnlyList<MemoryFact> shown,
        IReadOnlyList<MemoryCaptureOperation> operations, IReadOnlyList<MemoryFact> elsewhere, Func<string?, string?, bool> samePerson,
        CancellationToken operationToken)
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
                    if (current.Concat(elsewhere).Any(fact => MemoryCapture.SameFact(fact.Content, content) &&
                        samePerson(fact.VoiceId, operation.VoiceId)))
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
                        current.Concat(elsewhere).Any(fact => fact.Id != target.Id && MemoryCapture.SameFact(fact.Content, content) &&
                            samePerson(fact.VoiceId, target.VoiceId)))
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
        return changes;
    }

    private static DesktopMemoryException Invalidated() => new("memory.retrieval_invalidated",
        "Memory changed while it was being read.");

    /// <summary>Runs the memory sync's local step with the store of <paramref name="space"/> (a space of the account signed in
    /// now, or of the account signed in before a switch), only while memory is on and nobody else holds that store right now
    /// (a conversation's recall or remembering never waits for it). Ran is false, with why, when it didn't run.</summary>
    internal async Task<(bool Ran, string? Why, T? Result)> TryWithFreeStoreAsync<T>(MemorySpace space,
        Func<MemoryStore, CancellationToken, Task<T>> action, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(space);
        EnsureOpen();
        var loaded = await settings.LoadAsync(token).ConfigureAwait(false);
        if (loaded.State != SettingsLoadState.Loaded || loaded.Error is not null || loaded.Settings?.Memory is not { Enabled: true } memory)
            return (false, "off", default);
        if (HasPendingCleanup) return (false, "busy", default);
        try
        {
            return (true, null, await OpenSpaceAsync(space, memory, (_, store, operationToken) => action(store, operationToken),
                token, TimeSpan.Zero, changes: true).ConfigureAwait(false));
        }
        catch (DesktopMemoryException error) when (error.Code is "memory.busy")
        {
            return (false, "busy", default);
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

    /// <summary>Runs <paramref name="action"/> with the active space's store.</summary>
    private Task<T> WithStoreAsync<T>(
        Guid expectedConfigurationRevision,
        Func<MemoryStore, CancellationToken, Task<T>> action,
        CancellationToken token,
        TimeSpan? wait = null,
        bool changes = false) =>
        WithSpaceAsync(expectedConfigurationRevision, null, (_, _, store, operationToken) => action(store, operationToken), token,
            wait, changes);

    /// <summary>Runs <paramref name="action"/> with the store of <paramref name="spaceId"/> (null: the active space), one of the
    /// spaces of the account signed in; an unknown space is <c>memory.space_unknown</c>.</summary>
    private async Task<T> WithSpaceAsync<T>(
        Guid expectedConfigurationRevision,
        string? spaceId,
        Func<MemorySpaceSet, MemorySpace, MemoryStore, CancellationToken, Task<T>> action,
        CancellationToken token,
        TimeSpan? wait = null,
        bool changes = false,
        bool snapshot = false)
    {
        EnsureOpen();
        token.ThrowIfCancellationRequested();
        var (memory, set) = await RequireEnabledAsync(expectedConfigurationRevision, token).ConfigureAwait(false);
        var space = set.Find(spaceId) ?? throw new DesktopMemoryException("memory.space_unknown",
            "That memory space isn't one this account uses. Reload and try again.");
        return await OpenSpaceAsync(space, memory, (found, store, operationToken) => action(set, found, store, operationToken),
            token, wait, changes, snapshot).ConfigureAwait(false);
    }

    /// <param name="memory">The memory settings in force (the owner's memories from before accounts move into the owner's space
    /// before that store is first opened).</param>
    /// <param name="changes">The action can add, change or delete facts: <see cref="FactsChanged"/> is raised once the store is
    /// free again, even when the action failed partway (what it committed before stays), and a space other than the active one
    /// gets a fresh snapshot.</param>
    /// <param name="snapshot">Refresh the snapshot of a space other than the active one even when nothing changed.</param>
    private async Task<T> OpenSpaceAsync<T>(
        MemorySpace space,
        MemorySettings? memory,
        Func<MemorySpace, MemoryStore, CancellationToken, Task<T>> action,
        CancellationToken token,
        TimeSpan? wait = null,
        bool changes = false,
        bool snapshot = false)
    {
        EnsureOpen();
        token.ThrowIfCancellationRequested();
        var preview = MemoryStoreActivationPreview.Create(space.Directory);
        preview.ValidateLocalScope();
        var approval = preview.Authorize(MemoryConsentDecision.Allow);
        var storeGate = StoreGate(space.Directory);
        if (!await storeGate.WaitAsync(wait ?? StoreWait, token).ConfigureAwait(false))
            throw new DesktopMemoryException("memory.busy",
                "Memory is busy. Try again in a moment.");
        MemoryAccount? owner;
        long version;
        lock (gate)
        {
            owner = account;
            version = accountVersion;
        }
        MemoryStore store;
        try
        {
            if (owner is { Owner: true } && space.Id == owner.Space && memory is not null)
                MigrateOnce(owner, memory);
            store = openStore(preview, approval, token);
        }
        catch
        {
            storeGate.Release();
            throw;
        }
        try
        {
            return await action(space, store, token).ConfigureAwait(false);
        }
        finally
        {
            // Cancellation/closed observers cannot abandon private partials or release the app effect slot.
            while (store.HasPendingCleanup)
            {
                Task retry;
                lock (gate)
                {
                    cleanupRetry ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
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
            if ((changes || snapshot) && owner is not null && space.Id != ActiveSpaceOf(owner))
            {
                try { Install(space.Id, await store.SnapshotAsync(CancellationToken.None).ConfigureAwait(false), version); }
                catch (MemoryException) { }
            }
            store.Dispose();
            storeGate.Release();
            if (changes) FactsChanged?.Invoke();
        }
    }

    private static string ActiveSpaceOf(MemoryAccount owner) =>
        owner.Character is { } character && MemorySpaceId.IsValid(character) &&
        character.StartsWith(MemorySpaceId.CharacterPrefix, StringComparison.Ordinal) ? character : owner.Space;

    /// <summary>Keeps <paramref name="snapshot"/> as the copy of <paramref name="space"/>, unless the account changed since.</summary>
    private void Install(string space, MemorySnapshot snapshot, long version)
    {
        lock (gate)
        {
            if (version != accountVersion) return;
            snapshots = [.. snapshots.Where(s => s.Space != space), (space, snapshot)];
        }
    }

    private void MigrateOnce(MemoryAccount owner, MemorySettings memory)
    {
        lock (gate) if (migrated.Contains(owner.Id)) return;
        try
        {
            if (MemorySpaces.MigrateLegacy(owner, memory) is { } outcome) ErrorLog.Info("Memory: " + outcome + ".");
            lock (gate) migrated.Add(owner.Id);
        }
        // Tried again the next time the store opens; the owner's space opens meanwhile.
        catch (Exception error) when (error is MemoryException or IOException or UnauthorizedAccessException)
        {
            ErrorLog.Warn("Memory: couldn't move this PC's memories from before accounts into the owner's space yet.", error);
        }
    }

    private SemaphoreSlim StoreGate(string directory)
    {
        var key = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        lock (gate)
        {
            if (!storeGates.TryGetValue(key, out var found))
                storeGates[key] = found = new(1, 1);
            return found;
        }
    }

    private static MemoryStore RequireWritable(MemorySpace space, MemoryStore store) => space.Writable ? store
        : throw new DesktopMemoryException("memory.read_only",
            "These memories are shared with you; only the person they belong to can change them.");

    internal void RetryCleanup()
    {
        lock (gate)
        {
            cleanupRetry?.TrySetResult();
            cleanupRetry = null;
        }
    }

    private async Task<(MemorySettings Settings, MemorySpaceSet Spaces)> RequireEnabledAsync(
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
        return (memory, MemorySpaces.Resolve(Account, memory, settings.DataDirectory));
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
