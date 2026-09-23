using Martlet.Core.Contracts;

namespace Martlet.Core.Settings;

public enum ConfigurationRestoreCommitState { NotStarted, NotCommitted, OutcomeUnknown, ReplacementReturned, VerifiedCommitted }

public sealed class ConfigurationRestoreEvidence
{
    public string Path { get; }
    public string Revision { get; }
    public Guid ProfileId { get; }
    public int SchemaVersion { get; }
    public string OriginalSnapshot { get; }
    public string OriginalRevision { get; }

    internal ConfigurationRestoreEvidence(string path, string revision, Guid profileId, int schemaVersion,
        string originalSnapshot, string originalRevision)
    {
        Path = path;
        Revision = revision;
        ProfileId = profileId;
        SchemaVersion = schemaVersion;
        OriginalSnapshot = originalSnapshot;
        OriginalRevision = originalRevision;
    }
}

public sealed class ConfigurationRestoreScope : IAsyncDisposable
{
    private readonly SettingsStore store;
    private readonly ConfigurationRestorePlan plan;
    private readonly CancellationToken token;
    private readonly FileStream writer;
    private readonly FileStream source;
    private readonly byte[] originalBytes;
    private readonly byte[] candidate;
    private readonly object gate = new();
    private FileStream? current, original, originalName, result, resultName;
    private Task? active, retirement;
    private bool retiring, attempted;
    private int commitState;
    private ConfigurationRestoreCleanup? cleanup;

    public string Destination { get; }
    public Guid ProfileId { get; }
    public string ExpectedRevision { get; }
    public string SourceFileDigest { get; }
    public string CandidateDigest { get; }
    public string OriginalSnapshotPath { get; }
    public ConfigurationRestoreCommitState CommitState => (ConfigurationRestoreCommitState)Volatile.Read(ref commitState);
    public ConfigurationRestoreCleanup? Cleanup => Volatile.Read(ref cleanup);

    internal ConfigurationRestoreScope(SettingsStore store, ConfigurationRestorePlan plan, CancellationToken token,
        FileStream writer, FileStream source, FileStream current, byte[] originalBytes, byte[] candidate, string originalPath)
    {
        this.store = store;
        this.plan = plan;
        this.token = token;
        this.writer = writer;
        this.source = source;
        this.current = current;
        this.originalBytes = originalBytes;
        this.candidate = candidate;
        Destination = plan.Destination;
        ProfileId = plan.ProfileId;
        ExpectedRevision = plan.ExpectedRevision;
        SourceFileDigest = plan.SourceFileDigest;
        CandidateDigest = plan.CandidateDigest;
        OriginalSnapshotPath = originalPath;
    }

    public Task CommitAsync(Action validateOperation, Action beforeReplacement) => RunAsync(async () =>
    {
        ArgumentNullException.ThrowIfNull(validateOperation);
        ArgumentNullException.ThrowIfNull(beforeReplacement);
        void Check()
        {
            token.ThrowIfCancellationRequested();
            validateOperation();
            token.ThrowIfCancellationRequested();
        }
        Check();
        await ValidateBindingsAsync(Check);
        Check();
        SettingsStore.ValidateRestoreCandidate(SettingsJson.Read(originalBytes), candidate, plan);
        Check();
        await PublishAsync(originalBytes, OriginalSnapshotPath, replace: false, Check, beforeReplacement);
        Check();
        await PublishAsync(candidate, Destination, replace: true, Check, beforeReplacement);
        return true;
    }, commit: true);

    public Task<ConfigurationRestoreEvidence> VerifyCommittedAsync() => RunAsync(async () =>
    {
        if (CommitState is not (ConfigurationRestoreCommitState.ReplacementReturned or ConfigurationRestoreCommitState.VerifiedCommitted) ||
            result is null || original is null)
            throw new RecoveryException(RecoveryFailure.Conflict);
        // Accounting is bounded and noncancelable after the actual replacement. It grants no further effects.
        resultName ??= Pin(Destination);
        var bytes = await ReadPinnedAsync(result, Destination, CandidateDigest, AppSettings.MaxFileBytes,
            static () => { }, accounting: true);
        await ReadPinnedAsync(resultName, Destination, CandidateDigest, AppSettings.MaxFileBytes,
            static () => { }, accounting: true);
        await ReadPinnedAsync(original, OriginalSnapshotPath, ExpectedRevision, AppSettings.MaxFileBytes,
            static () => { }, accounting: true);
        await ReadPinnedAsync(source, plan.SourcePath, SourceFileDigest, ConfigurationSnapshot.MaximumBytes,
            static () => { }, accounting: true);
        var settings = SettingsJson.Read(bytes);
        if (settings.SchemaVersion != AppSettings.CurrentSchemaVersion || settings.Profile.Id != ProfileId)
            throw new RecoveryException(RecoveryFailure.Conflict);
        Volatile.Write(ref commitState, (int)ConfigurationRestoreCommitState.VerifiedCommitted);
        return new ConfigurationRestoreEvidence(Destination, CandidateDigest, ProfileId, settings.SchemaVersion,
            OriginalSnapshotPath, ExpectedRevision);
    }, commit: false);

    private Task<T> RunAsync<T>(Func<Task<T>> operation, bool commit)
    {
        TaskCompletionSource finished;
        lock (gate)
        {
            if (retiring || active is not null || (commit && attempted))
                throw new RecoveryException(RecoveryFailure.Conflict);
            if (commit)
            {
                attempted = true;
                Volatile.Write(ref commitState, (int)ConfigurationRestoreCommitState.NotCommitted);
            }
            finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
            active = finished.Task;
        }
        return CompleteAsync();

        async Task<T> CompleteAsync()
        {
            try { return await operation(); }
            catch (ContractException) { throw new RecoveryException(RecoveryFailure.InvalidBackup); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            { throw new RecoveryException(RecoveryFailure.Unavailable); }
            finally
            {
                lock (gate)
                {
                    active = null;
                    finished.SetResult();
                }
            }
        }
    }

    private async Task ValidateBindingsAsync(Action check)
    {
        check();
        await ReadPinnedAsync(current!, Destination, ExpectedRevision, AppSettings.MaxFileBytes, check);
        check();
        await ReadPinnedAsync(source, plan.SourcePath, SourceFileDigest, ConfigurationSnapshot.MaximumBytes, check);
        check();
        if (original is not null)
        {
            await ReadPinnedAsync(original, OriginalSnapshotPath, ExpectedRevision, AppSettings.MaxFileBytes, check);
            check();
        }
    }

    private async Task PublishAsync(byte[] bytes, string destination, bool replace, Action check, Action beforeReplacement)
    {
        var temporary = System.IO.Path.Combine(store.DataDirectory, $"settings.{Guid.NewGuid():N}.tmp");
        FileStream? scratch = null;
        var owned = false;
        void Point(SettingsIoPoint point)
        {
            check();
            store.RecoveryIo?.Invoke(point, token);
            check();
        }
        try
        {
            Point(SettingsIoPoint.BeforeStage);
            SettingsStore.RecoveryPath(temporary);
            check();
            scratch = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
            owned = true;
            check();
            await scratch.WriteAsync(bytes, token);
            Point(SettingsIoPoint.AfterWrite);
            Point(SettingsIoPoint.BeforeFlush);
            await scratch.FlushAsync(token);
            check();
            scratch.Flush(flushToDisk: true);
            check();
            // Retain the same file while exchanging the write handle for its write-denying read pin.
            var reader = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            scratch.Dispose();
            scratch = reader;
            check();
            var sealedReader = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            reader.Dispose();
            scratch = sealedReader;
            Point(SettingsIoPoint.AfterFlush);
            Point(SettingsIoPoint.BeforeCommit);
            await ValidateBindingsAsync(check);
            check();
            await ReadPinnedAsync(scratch, temporary, ConfigurationSnapshot.Hash(bytes), AppSettings.MaxFileBytes, check);
            check();
            if (replace)
            {
                beforeReplacement();
                check();
                await CheckBytesAsync(scratch, temporary, CandidateDigest, AppSettings.MaxFileBytes, token, check);
                check();
                // Only the checked old target is released. Source and byte-exact original stay owned.
                current!.Dispose();
                current = null;
                Volatile.Write(ref commitState, (int)ConfigurationRestoreCommitState.OutcomeUnknown);
                File.Move(temporary, destination, overwrite: true);
                Volatile.Write(ref commitState, (int)ConfigurationRestoreCommitState.ReplacementReturned);
                owned = false;
                result = scratch;
                scratch = null;
                resultName = Pin(destination);
                await ReadPinnedAsync(result, destination, CandidateDigest, AppSettings.MaxFileBytes,
                    static () => { }, accounting: true);
                await ReadPinnedAsync(resultName, destination, CandidateDigest, AppSettings.MaxFileBytes,
                    static () => { }, accounting: true);
                // Preserve the OS-returned fact even when this observer fails or requests cancellation.
                store.RecoveryIo?.Invoke(SettingsIoPoint.AfterCommit, token);
            }
            else
            {
                File.Move(temporary, destination, overwrite: false);
                owned = false;
                original = scratch;
                scratch = null;
                originalName = Pin(destination);
                await ReadPinnedAsync(original, destination, ExpectedRevision, AppSettings.MaxFileBytes, check);
                Point(SettingsIoPoint.AfterCommit);
            }
        }
        finally
        {
            if (owned && scratch is not null)
            {
                var pending = new ConfigurationRestoreCleanup(temporary, scratch, store.RecoveryIo);
                scratch = null;
                var cleaned = false;
                try
                {
                    // Retiring exact owned scratch is not another restore effect.
                    await pending.RetryOwnedAsync(CancellationToken.None, static () => { });
                    cleaned = true;
                }
                finally { if (!cleaned) Volatile.Write(ref cleanup, pending); }
            }
            scratch?.Dispose();
        }
    }

    internal static FileStream Pin(string path)
    {
        SettingsStore.RecoveryPath(path);
        return new(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    private async Task<byte[]> ReadPinnedAsync(FileStream retained, string path, string digest, int maximum,
        Action check, bool accounting = false)
    {
        check();
        store.RecoveryIo?.Invoke(SettingsIoPoint.BeforeRead, token);
        check();
        var bytes = await CheckBytesAsync(retained, path, digest, maximum,
            accounting ? CancellationToken.None : token, check);
        check();
        store.RecoveryIo?.Invoke(SettingsIoPoint.AfterRead, token);
        check();
        return bytes;
    }

    internal static async Task<byte[]> CheckBytesAsync(FileStream retained, string path, string digest, int maximum,
        CancellationToken token, Action check)
    {
        check();
        SettingsStore.RecoveryPath(path);
        check();
        retained.Position = 0;
        check();
        var bytes = await SettingsStore.ReadBoundedAsync(retained, maximum, token);
        check();
        if (!string.Equals(ConfigurationSnapshot.Hash(bytes), digest, StringComparison.Ordinal))
            throw new RecoveryException(RecoveryFailure.Conflict);
        await using var named = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        check();
        var namedBytes = await SettingsStore.ReadBoundedAsync(named, maximum, token);
        check();
        if (!bytes.AsSpan().SequenceEqual(namedBytes))
            throw new RecoveryException(RecoveryFailure.Conflict);
        return bytes;
    }

    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            retiring = true;
            retirement ??= RetireAsync(active);
            return new(retirement);
        }
    }

    private async Task RetireAsync(Task? inFlight)
    {
        if (inFlight is not null) await inFlight;
        resultName?.Dispose();
        result?.Dispose();
        originalName?.Dispose();
        original?.Dispose();
        current?.Dispose();
        source.Dispose();
        writer.Dispose();
    }
}

public sealed class ConfigurationRestoreCleanup
{
    private readonly SemaphoreSlim gate = new(1);
    private readonly Action<SettingsIoPoint, CancellationToken>? observer;
    private FileStream? retained;
    public string Path { get; }
    public bool IsPending => Volatile.Read(ref retained) is not null;

    internal ConfigurationRestoreCleanup(string path, FileStream retained, Action<SettingsIoPoint, CancellationToken>? observer)
    {
        Path = path;
        this.retained = retained;
        this.observer = observer;
    }

    public Task RetryAsync(CancellationToken token = default) => RetryOwnedAsync(token, token.ThrowIfCancellationRequested);

    internal async Task RetryOwnedAsync(CancellationToken token, Action check)
    {
        await gate.WaitAsync(token);
        try
        {
            if (retained is null) return;
            check();
            observer?.Invoke(SettingsIoPoint.BeforeCleanup, token);
            check();
            SettingsStore.RecoveryPath(Path);
            check();
            retained.Position = 0;
            var bytes = await SettingsStore.ReadBoundedAsync(retained, AppSettings.MaxFileBytes, token);
            check();
            try
            {
                await ConfigurationRestoreScope.CheckBytesAsync(retained, Path, ConfigurationSnapshot.Hash(bytes),
                    AppSettings.MaxFileBytes, token, check);
            }
            catch (FileNotFoundException)
            {
                retained.Dispose();
                Volatile.Write(ref retained, null);
                return;
            }
            check();
            File.Delete(Path);
            retained.Dispose();
            Volatile.Write(ref retained, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or RecoveryException)
        { throw new RecoveryException(RecoveryFailure.CleanupPending, Path); }
        finally { gate.Release(); }
    }
}
