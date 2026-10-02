using System.Security.Cryptography;

namespace Martlet.F5;

public sealed class F5ReferencePresetStore : IDisposable
{
    internal const string StoreFileName = ".martlet-f5-references.v1.json";
    internal const string PendingFileName = ".martlet-f5-references.v1.pending";
    internal const string LockFileName = ".martlet-f5-references.v1.lock";

    private readonly object gate = new();
    private readonly SemaphoreSlim mutationGate = new(1, 1);
    private readonly string directory;
    private readonly string storePath;
    private readonly string pendingPath;
    private readonly FileStream ownershipLock;
    private readonly TimeProvider clock;
    private F5ReferenceStoreDocument state;
    private int activeUses;
    private bool selectionMutation;
    private bool disposed;

    private F5ReferencePresetStore(
        string directory,
        FileStream ownershipLock,
        F5ReferenceStoreDocument state,
        TimeProvider clock)
    {
        this.directory = directory;
        storePath = Path.Combine(directory, StoreFileName);
        pendingPath = Path.Combine(directory, PendingFileName);
        this.ownershipLock = ownershipLock;
        this.state = state;
        this.clock = clock;
    }

    public static F5ReferencePresetStore Open(
        string absoluteDirectory,
        TimeProvider? clock = null,
        CancellationToken cancellationToken = default)
    {
        var selectedClock = clock ?? TimeProvider.System;
        var directory = F5ReferencePaths.NormalizeDirectory(absoluteDirectory, inspectFileSystem: true);
        cancellationToken.ThrowIfCancellationRequested();
        FileStream? ownership = null;
        try
        {
            Directory.CreateDirectory(directory);
            F5ReferencePaths.NormalizeDirectory(directory, inspectFileSystem: true);
            var audioDirectory = Path.Combine(directory, "audio");
            Directory.CreateDirectory(audioDirectory);
            F5ReferencePaths.CheckEntry(audioDirectory, allowMissing: false, requireFile: false);

            var lockPath = Path.Combine(directory, LockFileName);
            F5ReferencePaths.CheckEntry(lockPath, allowMissing: true, requireFile: false);
            try
            {
                ownership = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                    FileShare.None, 1, FileOptions.WriteThrough);
            }
            catch (IOException)
            {
                throw new F5Exception(F5Failure.Busy);
            }
            F5Guard.Require(ownership.Length == 0, F5Failure.CorruptStore);

            var pendingPath = Path.Combine(directory, PendingFileName);
            F5ReferencePaths.CheckEntry(pendingPath, allowMissing: true, requireFile: false);
            if (File.Exists(pendingPath))
                File.Delete(pendingPath);

            var now = selectedClock.GetUtcNow();
            F5Guard.Utc(now);
            var storePath = Path.Combine(directory, StoreFileName);
            F5ReferenceStoreDocument document;
            if (File.Exists(storePath))
            {
                F5ReferencePaths.CheckEntry(storePath, allowMissing: false, requireFile: true);
                byte[] bytes;
                using (var input = new FileStream(storePath, FileMode.Open, FileAccess.Read,
                    FileShare.Read, 16 * 1024, FileOptions.SequentialScan))
                {
                    F5Guard.Require(input.Length is > 0 and <= F5ReferenceLimits.MaximumStoreBytes,
                        F5Failure.CorruptStore);
                    bytes = new byte[checked((int)input.Length)];
                    input.ReadExactly(bytes);
                    F5Guard.Require(input.ReadByte() == -1, F5Failure.CorruptStore);
                }
                try
                {
                    document = F5ReferenceJson.Read(bytes, now);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(bytes);
                }
            }
            else
            {
                document = new()
                {
                    SchemaVersion = F5ReferenceLimits.SchemaVersion,
                    StoreRevision = 0,
                    UpdatedAtUtc = now,
                    AppliedPresetId = null,
                    AppliedReferenceRevision = null,
                    Presets = []
                };
            }

            var result = new F5ReferencePresetStore(directory, ownership, document, selectedClock);
            ownership = null;
            return result;
        }
        catch (F5Exception)
        {
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            throw new F5Exception(F5Failure.AccessDenied);
        }
        catch (IOException)
        {
            throw new F5Exception(F5Failure.IoFailure);
        }
        finally
        {
            ownership?.Dispose();
        }
    }

    public F5ReferenceStoreInspection Inspect()
    {
        lock (gate)
        {
            EnsureOpen();
            return new(
                state.StoreRevision,
                state.AppliedPresetId,
                state.AppliedReferenceRevision,
                state.Presets
                    .OrderBy(preset => preset.Name, StringComparer.Ordinal)
                    .ThenBy(preset => preset.Id)
                    .Select(preset => new F5ReferencePresetInfo(
                        preset.Id,
                        preset.Name,
                        preset.Snapshots
                            .OrderBy(snapshot => snapshot.CreatedAtUtc)
                            .ThenBy(snapshot => snapshot.ReferenceRevision, StringComparer.Ordinal)
                            .Select(snapshot => snapshot.ToPublic(preset.Name))
                            .ToArray()))
                    .ToArray());
        }
    }

    public async Task<F5ReferenceSnapshot> SnapshotAsync(
        F5ReferenceSnapshotRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = CurrentUtc();
        F5Guard.Require(request.PresetId is null || request.PresetId != Guid.Empty);
        F5Guard.Utf8Text(request.PresetName, F5ReferenceLimits.MaximumPresetNameCharacters,
            F5ReferenceLimits.MaximumPresetNameUtf8Bytes, allowNewLines: false);
        F5Guard.Utf8Text(request.Transcript, F5ReferenceLimits.MaximumTranscriptCharacters,
            F5ReferenceLimits.MaximumTranscriptUtf8Bytes);
        F5Guard.Require(request.Rights is not null, F5Failure.RightsRequired);
        request.Rights!.Validate(now);

        var audio = await F5ReferenceAudio.ReadAsync(request.AbsoluteSourcePath, cancellationToken);
        try
        {
            var transcriptRevision = F5ReferenceDigests.TranscriptRevision(request.Transcript);
            var referenceRevision = F5ReferenceDigests.ReferenceRevision(
                audio.Sha256, transcriptRevision);

            await mutationGate.WaitAsync(cancellationToken);
            try
            {
                F5ReferenceStoreDocument current;
                F5ReferencePresetDocument? existingPreset;
                lock (gate)
                {
                    EnsureOpen();
                    current = state;
                    existingPreset = request.PresetId is { } id
                        ? current.Presets.SingleOrDefault(preset => preset.Id == id)
                        : null;
                    F5Guard.Require(request.PresetId is null || existingPreset is not null,
                        F5Failure.NotFound);
                }

                if (existingPreset?.Snapshots.SingleOrDefault(snapshot =>
                    snapshot.ReferenceRevision == referenceRevision) is { } existing)
                {
                    F5Guard.Require(existing.Rights.ProcessingDestinationId ==
                            request.Rights.ProcessingDestinationId,
                        F5Failure.Conflict);
                    return existing.ToPublic(existingPreset.Name);
                }

                var presetId = existingPreset?.Id ?? Guid.NewGuid();
                var presetCount = current.Presets.Length + (existingPreset is null ? 1 : 0);
                var snapshotCount = current.Presets.Sum(preset => preset.Snapshots.Length) + 1;
                F5Guard.Require(presetCount <= F5ReferenceLimits.MaximumPresets &&
                    snapshotCount <= F5ReferenceLimits.MaximumTotalSnapshots &&
                    (existingPreset?.Snapshots.Length ?? 0) <
                        F5ReferenceLimits.MaximumSnapshotsPerPreset,
                    F5Failure.LimitExceeded);
                var relativePath = F5ReferenceJson.RelativeAudioPath(presetId, referenceRevision);
                var snapshot = new F5ReferenceSnapshotDocument
                {
                    PresetId = presetId,
                    ReferenceRevision = referenceRevision,
                    SourcePath = audio.SourcePath,
                    AudioRelativePath = relativePath,
                    AudioSha256 = audio.Sha256,
                    Transcript = request.Transcript,
                    TranscriptRevision = transcriptRevision,
                    AudioFormat = audio.Format,
                    Rights = request.Rights,
                    CreatedAtUtc = now
                };

                var createdAudio = await WriteSnapshotAudioAsync(snapshot, audio.Bytes, cancellationToken);
                try
                {
                    var presets = current.Presets.ToList();
                    if (existingPreset is null)
                    {
                        presets.Add(new()
                        {
                            Id = presetId,
                            Name = request.PresetName,
                            Snapshots = [snapshot]
                        });
                    }
                    else
                    {
                        var replacement = existingPreset with
                        {
                            Name = request.PresetName,
                            Snapshots = [.. existingPreset.Snapshots, snapshot]
                        };
                        presets[presets.FindIndex(preset => preset.Id == presetId)] = replacement;
                    }
                    var next = current with
                    {
                        StoreRevision = NextRevision(current.StoreRevision),
                        UpdatedAtUtc = now,
                        Presets = [.. presets]
                    };
                    await CommitAsync(current, next, cancellationToken);
                    return snapshot.ToPublic(request.PresetName);
                }
                catch
                {
                    if (createdAudio)
                        TryDeleteAudio(snapshot);
                    throw;
                }
            }
            finally
            {
                mutationGate.Release();
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(audio.Bytes);
        }
    }

    public Task<F5ReferenceApplyPreview> CreateApplyPreviewAsync(
        Guid presetId,
        string referenceRevision,
        CancellationToken cancellationToken = default)
    {
        F5Guard.Require(presetId != Guid.Empty);
        F5Guard.Sha256(referenceRevision);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            EnsureOpen();
            var snapshot = FindSnapshot(state, presetId, referenceRevision);
            return Task.FromResult(new F5ReferenceApplyPreview(
                Guid.NewGuid(),
                state.StoreRevision,
                presetId,
                referenceRevision,
                snapshot.Rights.ProcessingDestinationId,
                state.AppliedReferenceRevision));
        }
    }

    public async Task<F5ReferenceApplyReceipt> ApplyAsync(
        F5ReferenceApplyPreview preview,
        F5ReferenceApplyAuthorization authorization,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(authorization);
        F5Guard.Require(ReferenceEquals(authorization.Preview, preview),
            F5Failure.AuthorizationMismatch);
        F5Guard.Require(Volatile.Read(ref authorization.Used) == 0,
            F5Failure.AuthorizationConsumed);
        await mutationGate.WaitAsync(cancellationToken);
        var ownsSelectionMutation = false;
        try
        {
            F5ReferenceStoreDocument current;
            F5ReferenceSnapshotDocument snapshot;
            lock (gate)
            {
                EnsureOpen();
                F5Guard.Require(!selectionMutation && activeUses == 0, F5Failure.Busy);
                F5Guard.Require(state.StoreRevision == preview.StoreRevision,
                    F5Failure.Conflict);
                snapshot = FindSnapshot(state, preview.PresetId, preview.ReferenceRevision);
                F5Guard.Require(snapshot.Rights.ProcessingDestinationId == preview.DestinationId &&
                    state.AppliedReferenceRevision == preview.PreviousReferenceRevision,
                    F5Failure.AuthorizationMismatch);
                selectionMutation = true;
                ownsSelectionMutation = true;
                current = state;
            }

            cancellationToken.ThrowIfCancellationRequested();
            F5Guard.Require(Interlocked.CompareExchange(ref authorization.Used, 1, 0) == 0,
                F5Failure.AuthorizationConsumed);
            if (current.AppliedPresetId == preview.PresetId &&
                current.AppliedReferenceRevision == preview.ReferenceRevision)
                return new(current.StoreRevision, preview.PresetId, preview.ReferenceRevision,
                    preview.PreviousReferenceRevision);

            var now = CurrentUtc();
            var next = current with
            {
                StoreRevision = NextRevision(current.StoreRevision),
                UpdatedAtUtc = now,
                AppliedPresetId = preview.PresetId,
                AppliedReferenceRevision = preview.ReferenceRevision
            };
            await CommitAsync(current, next, cancellationToken);
            return new(next.StoreRevision, preview.PresetId, preview.ReferenceRevision,
                preview.PreviousReferenceRevision);
        }
        finally
        {
            if (ownsSelectionMutation)
            {
                lock (gate)
                    selectionMutation = false;
            }
            mutationGate.Release();
        }
    }

    public Task<F5ReferenceUseLease> AcquireAppliedAsync(
        string expectedReferenceRevision,
        CancellationToken cancellationToken = default)
    {
        F5Guard.Sha256(expectedReferenceRevision);
        Guid presetId;
        lock (gate)
        {
            EnsureOpen();
            F5Guard.Require(state.AppliedPresetId is not null &&
                state.AppliedReferenceRevision == expectedReferenceRevision,
                F5Failure.Conflict);
            presetId = state.AppliedPresetId ??
                throw new F5Exception(F5Failure.Conflict);
        }
        return AcquireAsync(presetId, expectedReferenceRevision, requireApplied: true, cancellationToken);
    }

    public Task<F5ReferenceUseLease> AcquireForPreviewAsync(
        Guid presetId,
        string referenceRevision,
        CancellationToken cancellationToken = default)
    {
        F5Guard.Require(presetId != Guid.Empty);
        F5Guard.Sha256(referenceRevision);
        return AcquireAsync(presetId, referenceRevision, requireApplied: false, cancellationToken);
    }

    /// <summary>Removes a voice: the preset, all its snapshots and their stored recordings. The applied voice cannot be
    /// removed (apply another one first), so a selection never points at a missing voice. Original files are untouched.</summary>
    public async Task DeleteAsync(Guid presetId, CancellationToken cancellationToken = default)
    {
        F5Guard.Require(presetId != Guid.Empty);
        await mutationGate.WaitAsync(cancellationToken);
        try
        {
            F5ReferenceStoreDocument current;
            F5ReferencePresetDocument preset;
            lock (gate)
            {
                EnsureOpen();
                current = state;
                preset = current.Presets.SingleOrDefault(candidate => candidate.Id == presetId) ??
                    throw new F5Exception(F5Failure.NotFound);
                F5Guard.Require(current.AppliedPresetId != presetId, F5Failure.Conflict);
            }
            var next = current with
            {
                StoreRevision = NextRevision(current.StoreRevision),
                UpdatedAtUtc = CurrentUtc(),
                Presets = [.. current.Presets.Where(candidate => candidate.Id != presetId)]
            };
            await CommitAsync(current, next, cancellationToken);
            foreach (var snapshot in preset.Snapshots)
                TryDeleteAudio(snapshot);
            try
            {
                var folder = Path.GetDirectoryName(StoredPath(preset.Snapshots[0]))!;
                if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                    Directory.Delete(folder);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // An empty generated folder is inert.
            }
        }
        finally
        {
            mutationGate.Release();
        }
    }

    internal void ReleaseUse()
    {
        lock (gate)
        {
            F5Guard.Require(activeUses > 0, F5Failure.InvalidData);
            activeUses--;
        }
    }

    private async Task<F5ReferenceUseLease> AcquireAsync(
        Guid presetId,
        string referenceRevision,
        bool requireApplied,
        CancellationToken cancellationToken)
    {
        F5ReferenceSnapshotDocument snapshot;
        lock (gate)
        {
            EnsureOpen();
            F5Guard.Require(!selectionMutation, F5Failure.Busy);
            F5Guard.Require(!requireApplied ||
                (state.AppliedPresetId == presetId &&
                    state.AppliedReferenceRevision == referenceRevision),
                F5Failure.Conflict);
            snapshot = FindSnapshot(state, presetId, referenceRevision);
            F5ReferenceDigests.Validate(snapshot);
            activeUses = checked(activeUses + 1);
        }

        var release = true;
        byte[]? storedBytes = null;
        try
        {
            var storedPath = StoredPath(snapshot);
            ValidatedReferenceAudio stored;
            try
            {
                stored = await F5ReferenceAudio.ReadAsync(storedPath, cancellationToken);
            }
            catch (F5Exception error) when (error.Failure is F5Failure.SourceMissing or
                F5Failure.SourceChanged or F5Failure.InvalidAudio or F5Failure.InvalidPath)
            {
                throw new F5Exception(F5Failure.CorruptStore);
            }
            storedBytes = stored.Bytes;
            F5Guard.Require(F5Guard.FixedTimeEquals(stored.Sha256, snapshot.AudioSha256) &&
                stored.Format == snapshot.AudioFormat, F5Failure.CorruptStore);
            var result = new F5ReferenceUseLease(this, snapshot, storedBytes);
            storedBytes = null;
            release = false;
            return result;
        }
        finally
        {
            if (storedBytes is not null)
                CryptographicOperations.ZeroMemory(storedBytes);
            if (release)
                ReleaseUse();
        }
    }

    private async Task<bool> WriteSnapshotAudioAsync(
        F5ReferenceSnapshotDocument snapshot,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        var target = StoredPath(snapshot);
        var targetDirectory = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(targetDirectory);
        F5ReferencePaths.CheckAncestors(targetDirectory);
        F5ReferencePaths.CheckEntry(targetDirectory, allowMissing: false, requireFile: false);
        if (File.Exists(target))
        {
            var existing = await F5ReferenceAudio.ReadAsync(target, cancellationToken);
            try
            {
                F5Guard.Require(F5Guard.FixedTimeEquals(existing.Sha256, snapshot.AudioSha256) &&
                    existing.Format == snapshot.AudioFormat, F5Failure.CorruptStore);
                return false;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(existing.Bytes);
            }
        }

        var temporary = Path.Combine(targetDirectory, $".{Guid.NewGuid():N}.partial");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await output.WriteAsync(bytes, cancellationToken);
                await output.FlushAsync(cancellationToken);
                output.Flush(flushToDisk: true);
            }
            F5ReferencePaths.CheckEntry(target, allowMissing: true, requireFile: true);
            File.Move(temporary, target, overwrite: false);
            return true;
        }
        catch (F5Exception)
        {
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            throw new F5Exception(F5Failure.AccessDenied);
        }
        catch (IOException)
        {
            throw new F5Exception(F5Failure.IoFailure);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // A uniquely named uncommitted file is never authoritative.
                }
            }
        }
    }

    private async Task CommitAsync(
        F5ReferenceStoreDocument expected,
        F5ReferenceStoreDocument next,
        CancellationToken cancellationToken)
    {
        lock (gate)
        {
            EnsureOpen();
            F5Guard.Require(ReferenceEquals(state, expected), F5Failure.Conflict);
        }
        var bytes = F5ReferenceJson.Write(next, CurrentUtc());
        try
        {
            F5ReferencePaths.CheckEntry(pendingPath, allowMissing: true, requireFile: true);
            F5Guard.Require(!File.Exists(pendingPath), F5Failure.Busy);
            await using (var output = new FileStream(pendingPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await output.WriteAsync(bytes, cancellationToken);
                await output.FlushAsync(cancellationToken);
                output.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            F5ReferencePaths.CheckEntry(storePath, allowMissing: true, requireFile: true);
            File.Move(pendingPath, storePath, overwrite: File.Exists(storePath));
            lock (gate)
            {
                EnsureOpen();
                F5Guard.Require(ReferenceEquals(state, expected), F5Failure.Conflict);
                state = next;
            }
        }
        catch (F5Exception)
        {
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            throw new F5Exception(F5Failure.AccessDenied);
        }
        catch (IOException)
        {
            throw new F5Exception(F5Failure.IoFailure);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (File.Exists(pendingPath))
            {
                try
                {
                    File.Delete(pendingPath);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // The owned pending name blocks later writes until a reopen can remove it.
                }
            }
        }
    }

    private static F5ReferenceSnapshotDocument FindSnapshot(
        F5ReferenceStoreDocument document,
        Guid presetId,
        string referenceRevision)
    {
        var preset = document.Presets.SingleOrDefault(candidate => candidate.Id == presetId);
        var snapshot = preset?.Snapshots.SingleOrDefault(candidate =>
            candidate.ReferenceRevision == referenceRevision);
        return snapshot ?? throw new F5Exception(F5Failure.NotFound);
    }

    private string StoredPath(F5ReferenceSnapshotDocument snapshot) =>
        Path.Combine(directory, snapshot.AudioRelativePath.Replace('/', Path.DirectorySeparatorChar));

    private static long NextRevision(long revision)
    {
        F5Guard.Require(revision < F5ReferenceLimits.MaximumStoreRevision,
            F5Failure.LimitExceeded);
        return revision + 1;
    }

    private DateTimeOffset CurrentUtc()
    {
        var now = clock.GetUtcNow();
        F5Guard.Utc(now);
        return now;
    }

    private void TryDeleteAudio(F5ReferenceSnapshotDocument snapshot)
    {
        try
        {
            File.Delete(StoredPath(snapshot));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // The manifest remains authoritative; an unreferenced generated file is inert.
        }
    }

    private void EnsureOpen() =>
        F5Guard.Require(!disposed, F5Failure.Closed);

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
                return;
            F5Guard.Require(activeUses == 0 && !selectionMutation, F5Failure.Busy);
            F5Guard.Require(mutationGate.Wait(0), F5Failure.Busy);
            try
            {
                disposed = true;
                ownershipLock.Dispose();
            }
            finally
            {
                // Queued mutations must wake and observe Closed, not a disposed semaphore.
                mutationGate.Release();
            }
        }
    }
}
