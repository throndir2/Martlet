using System.Security.Cryptography;

namespace Martlet.Memory;

internal enum MemoryIoPoint
{
    AfterStoreStageWrite,
    BeforeStoreCommit,
    AfterStoreCommit,
    AfterExportStageWrite,
    BeforeExportCommit
}

internal enum MemoryQueryPoint
{
    AfterSnapshot,
    BeforeRevisionCheck
}

internal sealed class MemoryTestHooks
{
    internal Action<MemoryIoPoint, CancellationToken>? Io { get; init; }
    internal Action<MemoryQueryPoint, CancellationToken>? Query { get; init; }
}

internal readonly record struct DerivedMemoryState(int IndexedFacts, int IndexedTerms, int CachedQueries)
{
    internal bool ContainsTerm(LexicalIndex index, string term) => index.ContainsTerm(term);
}

public sealed class MemoryStore : IDisposable
{
    internal const string StoreFileName = ".martlet-memory.v1.json";
    internal const string LockFileName = ".martlet-memory.v1.lock";
    internal const string PendingFileName = ".martlet-memory.v1.pending";

    private sealed record StoreState(
        Guid StoreId,
        long Revision,
        DateTimeOffset UpdatedAtUtc,
        IReadOnlyDictionary<Guid, MemoryFact> Facts,
        LexicalIndex Index);

    private readonly record struct CachedHit(Guid FactId, double Score, int MatchedTerms);
    private readonly record struct QueryKey(string Terms, int MaximumResults);

    private readonly object gate = new();
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly SemaphoreSlim exportGate = new(1, 1);
    private readonly Dictionary<QueryKey, CachedHit[]> queryCache = [];
    private readonly string directory;
    private readonly string storePath;
    private readonly string pendingPath;
    private readonly FileStream ownershipLock;
    private readonly TimeProvider clock;
    private readonly MemoryTestHooks? hooks;
    private StoreState state;
    private int activeOperations;
    private bool disposed;
    private string? pendingStoreCleanup;
    private string? pendingExportCleanup;

    private MemoryStore(string directory, FileStream ownershipLock, StoreState state,
        TimeProvider clock, MemoryTestHooks? hooks)
    {
        this.directory = directory;
        storePath = Path.Combine(directory, StoreFileName);
        pendingPath = Path.Combine(directory, PendingFileName);
        this.ownershipLock = ownershipLock;
        this.state = state;
        this.clock = clock;
        this.hooks = hooks;
    }

    public static MemoryStore Open(MemoryStoreActivationPreview preview, MemoryStoreAuthorization authorization,
        CancellationToken cancellationToken = default) =>
        Open(preview, authorization, TimeProvider.System, hooks: null, cancellationToken);

    public bool HasPendingCleanup
    {
        get
        {
            lock (gate)
            {
                EnsureOpen();
                return pendingStoreCleanup is not null || pendingExportCleanup is not null;
            }
        }
    }

    internal static MemoryStore Open(MemoryStoreActivationPreview preview, MemoryStoreAuthorization authorization,
        TimeProvider clock, MemoryTestHooks? hooks = null, CancellationToken cancellationToken = default)
    {
        if (preview is null || authorization is null)
            throw new MemoryException(MemoryFailure.InvalidData);
        cancellationToken.ThrowIfCancellationRequested();
        var directory = MemoryPaths.NormalizeLocalPath(preview.DirectoryPath, directory: true, inspectFileSystem: false);
        MemoryGuard.Require(authorization.PreviewId == preview.Id &&
            string.Equals(authorization.DirectoryPath, directory, StringComparison.Ordinal),
            MemoryFailure.ConsentMismatch);
        MemoryGuard.Require(Interlocked.CompareExchange(ref authorization.Used, 1, 0) == 0,
            MemoryFailure.ConsentConsumed);

        FileStream? ownership = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            MemoryPaths.NormalizeLocalPath(directory, directory: true, inspectFileSystem: true);
            Directory.CreateDirectory(directory);
            MemoryPaths.NormalizeLocalPath(directory, directory: true, inspectFileSystem: true);
            var lockPath = Path.Combine(directory, LockFileName);
            MemoryPaths.CheckEntry(lockPath);
            try
            {
                ownership = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                    1, FileOptions.WriteThrough);
            }
            catch (IOException)
            {
                throw new MemoryException(MemoryFailure.Busy);
            }
            MemoryGuard.Require(ownership.Length == 0, MemoryFailure.CorruptStore);
            cancellationToken.ThrowIfCancellationRequested();

            var pending = Path.Combine(directory, PendingFileName);
            MemoryPaths.CheckEntry(pending);
            if (File.Exists(pending))
                File.Delete(pending);

            var storePath = Path.Combine(directory, StoreFileName);
            MemoryPaths.CheckEntry(storePath);
            MemoryStoreDocument? document = null;
            if (File.Exists(storePath))
            {
                using var input = new FileStream(storePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    4096, FileOptions.SequentialScan);
                MemoryGuard.Require(input.Length is > 0 and <= MemoryLimits.MaximumStoreBytes,
                    MemoryFailure.LimitExceeded);
                var bytes = new byte[checked((int)input.Length)];
                try
                {
                    input.ReadExactly(bytes);
                    MemoryGuard.Require(input.ReadByte() == -1, MemoryFailure.CorruptStore);
                    document = MemoryJson.ReadStore(bytes);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(bytes);
                }
            }

            var now = clock.GetUtcNow();
            MemoryGuard.Utc(now);
            var facts = (document?.Facts ?? []).ToDictionary(fact => fact.Id);
            var state = new StoreState(
                document?.StoreId ?? Guid.NewGuid(),
                document?.StoreRevision ?? 0,
                document?.UpdatedAtUtc ?? now,
                facts,
                LexicalIndex.Build(facts.Values));
            var result = new MemoryStore(directory, ownership, state, clock, hooks);
            ownership = null;
            return result;
        }
        catch (MemoryException)
        {
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            throw new MemoryException(MemoryFailure.AccessDenied);
        }
        catch (IOException)
        {
            throw new MemoryException(MemoryFailure.IoFailure);
        }
        finally
        {
            ownership?.Dispose();
        }
    }

    public async Task<MemoryInspection> InspectAsync(CancellationToken cancellationToken = default)
    {
        using var operation = BeginOperation();
        await PurgeExpiredCoreAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            EnsureOpen();
            var now = clock.GetUtcNow();
            MemoryGuard.Utc(now);
            return new(state.StoreId, state.Revision, now, state.Facts.Values
                .OrderBy(fact => fact.CreatedAtUtc)
                .ThenBy(fact => fact.Id)
                .ToArray());
        }
    }

    public async Task<MemoryMutationReceipt> SaveAsync(SaveFactRequest request,
        CancellationToken cancellationToken = default)
    {
        using var operation = BeginOperation();
        await writeGate.WaitAsync(cancellationToken);
        try
        {
            await PurgeExpiredUnderWriteGateAsync(CurrentUtc(), cancellationToken);
            var now = CurrentUtc();
            ValidateWrite(request?.Content, request?.Provenance, request?.Retention, now);
            MemoryFact.ValidateVoiceId(request!.VoiceId);
            StoreState current;
            lock (gate)
            {
                EnsureWritable();
                current = state;
                MemoryGuard.Require(current.Facts.Count < MemoryLimits.MaximumFacts, MemoryFailure.LimitExceeded);
            }
            var fact = new MemoryFact
            {
                Id = Guid.NewGuid(),
                Revision = 1,
                Content = request!.Content,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                CreatedFrom = request.Provenance,
                LastModifiedBy = request.Provenance,
                Retention = request.Retention,
                VoiceId = request.VoiceId
            };
            var facts = current.Facts.Values.Append(fact).ToArray();
            var committed = await CommitFactsAsync(current, facts, now, cancellationToken);
            return new(committed.Revision, fact);
        }
        finally
        {
            writeGate.Release();
        }
    }

    public async Task<MemoryMutationReceipt> EditAsync(EditFactRequest request,
        CancellationToken cancellationToken = default)
    {
        using var operation = BeginOperation();
        MemoryGuard.Require(request is not null && request.Id != Guid.Empty && request.ExpectedRevision > 0);
        await writeGate.WaitAsync(cancellationToken);
        try
        {
            await PurgeExpiredUnderWriteGateAsync(CurrentUtc(), cancellationToken);
            var now = CurrentUtc();
            ValidateWrite(request!.Content, request.Provenance, request.Retention, now);
            MemoryFact.ValidateVoiceId(request.VoiceId);
            StoreState current;
            MemoryFact existing;
            lock (gate)
            {
                EnsureWritable();
                current = state;
                MemoryGuard.Require(current.Facts.TryGetValue(request.Id, out existing!), MemoryFailure.NotFound);
                MemoryGuard.Require(existing.Revision == request.ExpectedRevision, MemoryFailure.Conflict);
                MemoryGuard.Require(existing.Revision < MemoryLimits.MaximumRevision, MemoryFailure.LimitExceeded);
            }
            var updated = existing with
            {
                Revision = existing.Revision + 1,
                Content = request.Content,
                // A fact another computer stamped ahead of this clock stays in order.
                UpdatedAtUtc = now > existing.UpdatedAtUtc ? now : existing.UpdatedAtUtc,
                LastModifiedBy = request.Provenance,
                Retention = request.Retention,
                VoiceId = request.VoiceId
            };
            var facts = current.Facts.Values.Select(fact => fact.Id == updated.Id ? updated : fact).ToArray();
            var committed = await CommitFactsAsync(current, facts, now, cancellationToken);
            return new(committed.Revision, updated);
        }
        finally
        {
            writeGate.Release();
        }
    }

    public async Task<MemoryDeleteReceipt> DeleteAsync(DeleteFactRequest request,
        CancellationToken cancellationToken = default)
    {
        using var operation = BeginOperation();
        MemoryGuard.Require(request is not null && request.Id != Guid.Empty &&
            request.ExpectedRevision > 0 && request.ConsentId != Guid.Empty);
        await writeGate.WaitAsync(cancellationToken);
        try
        {
            await PurgeExpiredUnderWriteGateAsync(CurrentUtc(), cancellationToken);
            var now = CurrentUtc();
            StoreState current;
            MemoryFact existing;
            lock (gate)
            {
                EnsureWritable();
                current = state;
                MemoryGuard.Require(current.Facts.TryGetValue(request!.Id, out existing!), MemoryFailure.NotFound);
                MemoryGuard.Require(existing.Revision == request.ExpectedRevision, MemoryFailure.Conflict);
            }
            var facts = current.Facts.Values.Where(fact => fact.Id != request!.Id).ToArray();
            var committed = await CommitFactsAsync(current, facts, now, cancellationToken);
            return new(existing.Id, existing.Revision, committed.Revision, request!.ConsentId);
        }
        finally
        {
            writeGate.Release();
        }
    }

    public async Task<MemoryExpiryReceipt> PurgeExpiredAsync(CancellationToken cancellationToken = default)
    {
        using var operation = BeginOperation();
        return await PurgeExpiredCoreAsync(cancellationToken);
    }

    /// <summary>Makes this store match the owner's other computers in one commit: puts each fact of
    /// <paramref name="request"/> in exactly as it is (replacing this store's version of it; already expired ones are left out)
    /// and forgets the listed facts. Only the memory sync calls this, with the newest version of each fact every computer agreed
    /// on; facts keep their own IDs, revisions, times and provenance.</summary>
    public async Task<MemoryMergeReceipt> MergeAsync(MemoryMergeRequest request, CancellationToken cancellationToken = default)
    {
        using var operation = BeginOperation();
        MemoryGuard.Require(request is { Facts: not null, Forget: not null });
        var incoming = request!.Facts!.Select(MemoryJson.ReadFact).ToArray();
        MemoryGuard.Require(incoming.Select(fact => fact.Id).Distinct().Count() == incoming.Length);
        await writeGate.WaitAsync(cancellationToken);
        try
        {
            await PurgeExpiredUnderWriteGateAsync(CurrentUtc(), cancellationToken);
            var now = CurrentUtc();
            StoreState current;
            lock (gate)
            {
                EnsureWritable();
                current = state;
            }
            var facts = new Dictionary<Guid, MemoryFact>(current.Facts);
            var forgotten = request.Forget!.Count(facts.Remove);
            var saved = 0;
            var expired = 0;
            foreach (var fact in incoming)
            {
                if (fact.Retention.IsExpired(now))
                {
                    expired++;
                    continue;
                }
                facts[fact.Id] = fact;
                saved++;
            }
            if (saved == 0 && forgotten == 0)
                return new(0, 0, expired, current.Revision);
            MemoryGuard.Require(facts.Count <= MemoryLimits.MaximumFacts, MemoryFailure.LimitExceeded);
            var committed = await CommitFactsAsync(current, facts.Values.ToArray(), now, cancellationToken);
            return new(saved, forgotten, expired, committed.Revision);
        }
        finally
        {
            writeGate.Release();
        }
    }

    public async Task<MemoryRetrievalResult> RetrieveAsync(MemoryQuery query,
        CancellationToken cancellationToken = default)
    {
        using var operation = BeginOperation();
        MemoryGuard.Require(query is not null);
        var terms = LexicalIndex.QueryTerms(query!.Text);
        MemoryGuard.Require(query.MaximumResults is >= 1 and <= MemoryLimits.MaximumResults);
        await PurgeExpiredCoreAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        StoreState snapshot;
        CachedHit[]? cached;
        var key = new QueryKey(string.Join('\u001f', terms), query.MaximumResults);
        lock (gate)
        {
            EnsureOpen();
            snapshot = state;
            queryCache.TryGetValue(key, out cached);
        }
        hooks?.Query?.Invoke(MemoryQueryPoint.AfterSnapshot, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var hits = cached ?? snapshot.Index.Search(terms, query.MaximumResults, cancellationToken)
            .Select(match => new CachedHit(match.FactId, match.Score, match.MatchedTerms))
            .ToArray();
        hooks?.Query?.Invoke(MemoryQueryPoint.BeforeRevisionCheck, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        await writeGate.WaitAsync(cancellationToken);
        try
        {
            var now = CurrentUtc();
            await PurgeExpiredUnderWriteGateAsync(now, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                EnsureOpen();
                MemoryGuard.Require(state.Revision == snapshot.Revision, MemoryFailure.QueryInvalidated);
                if (cached is null)
                {
                    if (queryCache.TryGetValue(key, out var winner))
                        hits = winner;
                    else
                    {
                        if (queryCache.Count >= MemoryLimits.MaximumCachedQueries)
                            queryCache.Remove(queryCache.Keys.First());
                        queryCache.Add(key, hits);
                    }
                }
                var materialized = hits
                    .Select(hit => new MemoryRetrievalHit(snapshot.Facts[hit.FactId], hit.Score, hit.MatchedTerms))
                    .ToArray();
                return new(snapshot.Revision, now, materialized);
            }
        }
        finally
        {
            writeGate.Release();
        }
    }

    public async Task<MemoryExportPreview> CreateExportPreviewAsync(CancellationToken cancellationToken = default)
    {
        using var operation = BeginOperation();
        await PurgeExpiredCoreAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            EnsureOpen();
            var now = CurrentUtc();
            var id = Guid.NewGuid();
            var facts = state.Facts.Values.OrderBy(fact => fact.CreatedAtUtc).ThenBy(fact => fact.Id).ToArray();
            var bytes = MemoryJson.WriteExport(new()
            {
                SchemaVersion = MemoryLimits.SchemaVersion,
                ExportId = id,
                ExportedAtUtc = now,
                StoreRevision = state.Revision,
                Facts = facts
            });
            return new(id, state.StoreId, state.Revision, now, facts.Length, bytes);
        }
    }

    public async Task<MemoryExportReceipt> ExportAsync(MemoryExportPreview preview,
        MemoryExportAuthorization authorization, string absoluteDestination,
        CancellationToken cancellationToken = default)
    {
        using var operation = BeginOperation();
        if (preview is null || authorization is null)
            throw new MemoryException(MemoryFailure.InvalidData);
        var destination = MemoryPaths.ExportDestination(absoluteDestination);
        MemoryGuard.Require(authorization.PreviewId == preview.Id &&
            authorization.StoreId == preview.StoreId &&
            authorization.StoreRevision == preview.StoreRevision &&
            authorization.Digest == preview.Sha256 &&
            string.Equals(authorization.Destination, destination, StringComparison.Ordinal),
            MemoryFailure.ConsentMismatch);
        await exportGate.WaitAsync(cancellationToken);
        byte[]? bytes = null;
        string? temporary = null;
        var committed = false;
        try
        {
            lock (gate)
            {
                EnsureWritable();
                MemoryGuard.Require(pendingExportCleanup is null, MemoryFailure.CleanupPending);
                MemoryGuard.Require(state.StoreId == preview.StoreId &&
                    state.Revision == preview.StoreRevision, MemoryFailure.QueryInvalidated);
            }
            MemoryGuard.Require(Interlocked.CompareExchange(ref authorization.Used, 1, 0) == 0,
                MemoryFailure.ConsentConsumed);
            MemoryGuard.Require(!File.Exists(destination) && !Directory.Exists(destination),
                MemoryFailure.DestinationExists);
            bytes = preview.Preview();
            MemoryGuard.Require(Convert.ToHexStringLower(SHA256.HashData(bytes)) == preview.Sha256,
                MemoryFailure.ConsentMismatch);
            temporary = Path.Combine(Path.GetDirectoryName(destination)!,
                $".martlet-memory-{Guid.NewGuid():N}.partial");
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await output.WriteAsync(bytes, cancellationToken);
                await output.FlushAsync(cancellationToken);
                output.Flush(flushToDisk: true);
            }
            hooks?.Io?.Invoke(MemoryIoPoint.AfterExportStageWrite, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            await writeGate.WaitAsync(cancellationToken);
            try
            {
                hooks?.Io?.Invoke(MemoryIoPoint.BeforeExportCommit, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                MemoryPaths.CheckAncestors(Path.GetDirectoryName(destination)!);
                MemoryPaths.CheckEntry(destination);
                await PurgeExpiredUnderWriteGateAsync(CurrentUtc(), cancellationToken);
                lock (gate)
                {
                    EnsureWritable();
                    MemoryGuard.Require(state.StoreId == preview.StoreId &&
                        state.Revision == preview.StoreRevision, MemoryFailure.QueryInvalidated);
                }
                MemoryGuard.Require(!File.Exists(destination) && !Directory.Exists(destination),
                    MemoryFailure.DestinationExists);
                File.Move(temporary, destination, overwrite: false);
                committed = true;
            }
            finally
            {
                writeGate.Release();
            }
            return new(preview.Id, preview.StoreRevision, preview.Sha256, bytes.Length);
        }
        catch (MemoryException)
        {
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            throw new MemoryException(MemoryFailure.AccessDenied);
        }
        catch (IOException)
        {
            throw new MemoryException(MemoryFailure.IoFailure);
        }
        finally
        {
            if (!committed && temporary is not null && File.Exists(temporary))
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    lock (gate)
                        pendingExportCleanup = temporary;
                }
            }
            if (bytes is not null)
                CryptographicOperations.ZeroMemory(bytes);
            exportGate.Release();
        }
    }

    public async Task RetryCleanupAsync(CancellationToken cancellationToken = default)
    {
        using var operation = BeginOperation();
        await exportGate.WaitAsync(cancellationToken);
        try
        {
            await writeGate.WaitAsync(cancellationToken);
            try
            {
                string? storeCleanup;
                string? exportCleanup;
                lock (gate)
                {
                    EnsureOpen();
                    storeCleanup = pendingStoreCleanup;
                    exportCleanup = pendingExportCleanup;
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (storeCleanup is not null)
                    File.Delete(storeCleanup);
                cancellationToken.ThrowIfCancellationRequested();
                if (exportCleanup is not null)
                    File.Delete(exportCleanup);
                lock (gate)
                {
                    pendingStoreCleanup = null;
                    pendingExportCleanup = null;
                }
            }
            finally
            {
                writeGate.Release();
            }
        }
        catch (UnauthorizedAccessException)
        {
            throw new MemoryException(MemoryFailure.AccessDenied);
        }
        catch (IOException)
        {
            throw new MemoryException(MemoryFailure.CleanupPending);
        }
        finally
        {
            exportGate.Release();
        }
    }

    internal DerivedMemoryState DerivedState
    {
        get
        {
            lock (gate)
            {
                EnsureOpen();
                return new(state.Index.DocumentCount, state.Index.TermCount, queryCache.Count);
            }
        }
    }

    internal bool DerivedIndexContains(string term)
    {
        lock (gate)
        {
            EnsureOpen();
            return state.Index.ContainsTerm(term);
        }
    }

    private async Task<MemoryExpiryReceipt> PurgeExpiredCoreAsync(CancellationToken token)
    {
        await writeGate.WaitAsync(token);
        try
        {
            return await PurgeExpiredUnderWriteGateAsync(CurrentUtc(), token);
        }
        finally
        {
            writeGate.Release();
        }
    }

    private async Task<MemoryExpiryReceipt> PurgeExpiredUnderWriteGateAsync(DateTimeOffset now, CancellationToken token)
    {
        StoreState current;
        MemoryFact[] active;
        lock (gate)
        {
            EnsureWritable();
            current = state;
            active = current.Facts.Values.Where(fact => !fact.Retention.IsExpired(now)).ToArray();
        }
        var deleted = current.Facts.Count - active.Length;
        if (deleted == 0)
            return new(0, current.Revision);
        var committed = await CommitFactsAsync(current, active, now, token);
        return new(deleted, committed.Revision);
    }

    private async Task<StoreState> CommitFactsAsync(StoreState expected, MemoryFact[] facts,
        DateTimeOffset now, CancellationToken token)
    {
        MemoryGuard.Require(facts.Length <= MemoryLimits.MaximumFacts, MemoryFailure.LimitExceeded);
        MemoryGuard.Require(expected.Revision < MemoryLimits.MaximumRevision, MemoryFailure.LimitExceeded);
        var revision = expected.Revision + 1;
        // Never behind a fact's own time: a fact another computer stamped ahead of this clock keeps the document valid.
        var stamp = facts.Select(fact => fact.UpdatedAtUtc).Append(now).Max();
        var document = new MemoryStoreDocument
        {
            SchemaVersion = MemoryLimits.SchemaVersion,
            StoreId = expected.StoreId,
            StoreRevision = revision,
            UpdatedAtUtc = stamp,
            Facts = facts
        };
        var dictionary = facts.ToDictionary(fact => fact.Id);
        var index = LexicalIndex.Build(facts);
        await WriteStoreDocumentAsync(document, token);
        StoreState committed;
        lock (gate)
        {
            EnsureOpen();
            MemoryGuard.Require(state.Revision == expected.Revision, MemoryFailure.Conflict);
            committed = new(expected.StoreId, revision, stamp, dictionary, index);
            state = committed;
            queryCache.Clear();
        }
        hooks?.Io?.Invoke(MemoryIoPoint.AfterStoreCommit, token);
        return committed;
    }

    private async Task WriteStoreDocumentAsync(MemoryStoreDocument document, CancellationToken token)
    {
        lock (gate)
        {
            EnsureWritable();
            MemoryGuard.Require(pendingStoreCleanup is null, MemoryFailure.CleanupPending);
        }
        var bytes = MemoryJson.WriteStore(document);
        var staged = false;
        var committed = false;
        try
        {
            token.ThrowIfCancellationRequested();
            MemoryPaths.CheckAncestors(directory);
            MemoryPaths.CheckEntry(pendingPath);
            MemoryGuard.Require(!File.Exists(pendingPath), MemoryFailure.CleanupPending);
            await using (var output = new FileStream(pendingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                staged = true;
                await output.WriteAsync(bytes, token);
                await output.FlushAsync(token);
                output.Flush(flushToDisk: true);
            }
            hooks?.Io?.Invoke(MemoryIoPoint.AfterStoreStageWrite, token);
            token.ThrowIfCancellationRequested();
            hooks?.Io?.Invoke(MemoryIoPoint.BeforeStoreCommit, token);
            token.ThrowIfCancellationRequested();
            MemoryPaths.CheckEntry(storePath);
            File.Move(pendingPath, storePath, overwrite: File.Exists(storePath));
            committed = true;
        }
        catch (MemoryException)
        {
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            throw new MemoryException(MemoryFailure.AccessDenied);
        }
        catch (IOException)
        {
            throw new MemoryException(MemoryFailure.IoFailure);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (staged && !committed && File.Exists(pendingPath))
            {
                try
                {
                    File.Delete(pendingPath);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    lock (gate)
                        pendingStoreCleanup = pendingPath;
                }
            }
        }
    }

    private static void ValidateWrite(string? content, MemoryProvenance? provenance,
        MemoryRetention? retention, DateTimeOffset now)
    {
        MemoryGuard.Require(content is not null && provenance is not null && retention is not null);
        MemoryFact.ValidateContent(content!);
        provenance!.Validate();
        MemoryGuard.Require(provenance.ObservedAtUtc <= now + TimeSpan.FromMinutes(5));
        retention!.ValidateForWrite(now);
    }

    private DateTimeOffset CurrentUtc()
    {
        var now = clock.GetUtcNow();
        MemoryGuard.Utc(now);
        return now;
    }

    private OperationLease BeginOperation()
    {
        lock (gate)
        {
            EnsureOpen();
            activeOperations = checked(activeOperations + 1);
            return new(this);
        }
    }

    private void EndOperation()
    {
        lock (gate)
            activeOperations--;
    }

    private void EnsureOpen() =>
        MemoryGuard.Require(!disposed, MemoryFailure.Closed);

    private void EnsureWritable()
    {
        EnsureOpen();
        MemoryGuard.Require(pendingStoreCleanup is null && pendingExportCleanup is null,
            MemoryFailure.CleanupPending);
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
                return;
            MemoryGuard.Require(activeOperations == 0, MemoryFailure.Busy);
            MemoryGuard.Require(pendingStoreCleanup is null && pendingExportCleanup is null,
                MemoryFailure.CleanupPending);
            disposed = true;
            queryCache.Clear();
        }
        ownershipLock.Dispose();
        writeGate.Dispose();
        exportGate.Dispose();
    }

    private sealed class OperationLease(MemoryStore owner) : IDisposable
    {
        private MemoryStore? owner = owner;

        public void Dispose() => Interlocked.Exchange(ref owner, null)?.EndOperation();
    }
}
