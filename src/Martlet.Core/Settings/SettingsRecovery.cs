using Martlet.Core.Contracts;

namespace Martlet.Core.Settings;

// A narrow internal fault/retirement seam around real IO, not a replaceable filesystem.
internal enum SettingsIoPoint { BeforeStage, AfterWrite, BeforeFlush, AfterFlush, BeforeCommit, AfterCommit, BeforeCleanup }

public sealed partial class SettingsStore
{
    internal Action<SettingsIoPoint, CancellationToken>? RecoveryIo { get; init; }

    private static async Task<byte[]> ReadBoundedAsync(string path, int maximum, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await ReadBoundedAsync(stream, maximum, token);
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximum, CancellationToken token)
    {
        var buffer = new byte[maximum + 1];
        var length = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, token);
        if (length > maximum) throw new RecoveryException(RecoveryFailure.InvalidBackup);
        return buffer.AsSpan(0, length).ToArray();
    }

    private async Task WriteAtomicAsync(byte[] bytes, string destination, bool replace, string? backup,
        CancellationToken token, bool recovery = false, Func<Task>? beforeCommit = null)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, $"settings.{Guid.NewGuid():N}.tmp");
        var owned = false;
        void Point(SettingsIoPoint point) { if (recovery) RecoveryIo?.Invoke(point, token); }
        try
        {
            Point(SettingsIoPoint.BeforeStage);
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                owned = true;
                await stream.WriteAsync(bytes, token);
                Point(SettingsIoPoint.AfterWrite);
                Point(SettingsIoPoint.BeforeFlush);
                await stream.FlushAsync(token);
                stream.Flush(flushToDisk: true);
                Point(SettingsIoPoint.AfterFlush);
            }
            Point(SettingsIoPoint.BeforeCommit);
            if (beforeCommit is not null) await beforeCommit();
            token.ThrowIfCancellationRequested();
            if (replace) File.Replace(temporary, destination, backup);
            else File.Move(temporary, destination, overwrite: false);
            owned = false;
            Point(SettingsIoPoint.AfterCommit);
        }
        finally
        {
            if (owned)
            {
                try
                {
                    Point(SettingsIoPoint.BeforeCleanup);
                    File.Delete(temporary);
                }
                catch (Exception ex) when (recovery && ex is IOException or UnauthorizedAccessException)
                { throw new RecoveryException(RecoveryFailure.CleanupPending, temporary); }
            }
        }
    }

    // Paths are validated on explicit actions only. No directory discovery or file scan on opening UI.
    internal static string RecoveryPath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
            throw new RecoveryException(RecoveryFailure.Unavailable);
        var full = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows() && (full[2..].Contains(':') ||
            new DriveInfo(Path.GetPathRoot(full)!).DriveType == DriveType.Network))
            throw new RecoveryException(RecoveryFailure.Unavailable);
        for (var current = full; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                    throw new RecoveryException(RecoveryFailure.Unavailable);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return full;
    }

    private async Task<(AppSettings Settings, string Revision, byte[] Bytes)> RecoveryCurrentAsync(CancellationToken token)
    {
        var loaded = await LoadAsync(token);
        if (loaded.Error?.Code == ErrorCode.UnsupportedVersion) throw new RecoveryException(RecoveryFailure.Incompatible);
        if (loaded.State != SettingsLoadState.Loaded) throw new RecoveryException(RecoveryFailure.Unavailable);
        var bytes = await ReadBoundedAsync(FilePath, AppSettings.MaxFileBytes, token);
        if (ConfigurationSnapshot.Hash(bytes) != loaded.Revision) throw new RecoveryException(RecoveryFailure.Conflict);
        return (loaded.Settings!, loaded.Revision!, bytes);
    }

    public async Task<ConfigurationRecoveryReceipt> CreateConfigurationSnapshotAsync(string destination, CancellationToken token = default)
    {
        try
        {
            destination = RecoveryPath(destination);
            RecoveryPath(FilePath);
            using var writeLock = new FileStream(FilePath + ".lock", FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
            var current = await RecoveryCurrentAsync(token);
            var bytes = ConfigurationSnapshot.Create(current.Bytes);
            await WriteAtomicAsync(bytes, destination, false, null, token, recovery: true, beforeCommit: async () =>
            {
                if ((await RecoveryCurrentAsync(token)).Revision != current.Revision)
                    throw new RecoveryException(RecoveryFailure.Conflict);
            });
            return new(destination, ConfigurationSnapshot.Hash(bytes));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { throw new RecoveryException(RecoveryFailure.Unavailable); }
    }

    public async Task<ConfigurationRestorePlan> PreviewConfigurationRestoreAsync(string source, CancellationToken token = default)
    {
        try
        {
            source = RecoveryPath(source);
            RecoveryPath(FilePath);
            var bytes = await ReadBoundedAsync(source, ConfigurationSnapshot.MaximumBytes, token);
            var envelope = ConfigurationSnapshot.Read(bytes);
            using var writeLock = new FileStream(FilePath + ".lock", FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
            var current = await RecoveryCurrentAsync(token);
            if (envelope.Manifest.ProfileId != current.Settings.Profile.Id)
                throw new RecoveryException(RecoveryFailure.WrongProfile);
            var imported = SettingsJson.Read(envelope.Manifest.SettingsBytes);
            var restored = RestoreCandidate(current.Settings, imported);
            return new(source, bytes, envelope, FilePath, current.Revision, current.Settings, restored);
        }
        catch (ContractException) { throw new RecoveryException(RecoveryFailure.InvalidBackup); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { throw new RecoveryException(RecoveryFailure.Unavailable); }
    }

    private static AppSettings RestoreCandidate(AppSettings current, AppSettings imported)
    {
        var restored = SetupSettings.Begin(imported);
        var pending = (current.Setup?.PendingRemovals ?? []).Concat(
            (current.Setup?.Routes ?? []).Where(route => route.CredentialId is not null)
            .Select(route => new PendingCredentialRemoval { Role = route.Role, CredentialId = route.CredentialId!.Value })).ToArray();
        if (pending.Length > 16)
            throw new RecoveryException(RecoveryFailure.CleanupCapacity);
        restored = restored with
        {
            Profile = restored.Profile with { Credentials = current.Profile.Credentials.ToArray() },
            Setup = restored.Setup! with
            {
                Checkpoint = SetupStep.Destinations,
                Routes = restored.Setup!.Routes.Select(route => route.WithCredential(null)).ToArray(),
                PendingRemovals = pending
            },
            Audio = restored.Audio is not { } audio ? null : audio with
            {
                Input = audio.Input with { ConfigurationRevision = Guid.NewGuid(), Checkpoint = null },
                Output = audio.Output with { ConfigurationRevision = Guid.NewGuid(), Checkpoint = null }
            }
        };
        restored.Validate();
        return restored;
    }

    public async Task<ConfigurationRecoveryReceipt> RestoreConfigurationAsync(ConfigurationRestorePlan plan,
        ConfigurationRestoreApproval approval, CancellationToken token = default)
    {
        approval.Consume(plan);
        if (plan.Destination != FilePath) throw new RecoveryException(RecoveryFailure.Conflict);
        try
        {
            RecoveryPath(FilePath);
            using var writeLock = new FileStream(FilePath + ".lock", FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
            RecoveryPath(plan.SourcePath);
            // Pin the approved source through commit. Windows sharing denies writes/renames;
            // digest verification still rejects changes made since preview.
            await using var sourceLock = new FileStream(plan.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            async Task ValidateCurrent()
            {
                var current = await RecoveryCurrentAsync(token);
                if (current.Revision != plan.ExpectedRevision || current.Settings.Profile.Id != plan.ProfileId)
                    throw new RecoveryException(RecoveryFailure.Conflict);
                RecoveryPath(plan.SourcePath);
                // Only compare with the frozen source; never import newly read, unseen content.
                sourceLock.Position = 0;
                var source = await ReadBoundedAsync(sourceLock, ConfigurationSnapshot.MaximumBytes, token);
                if (ConfigurationSnapshot.Hash(source) != plan.SourceFileDigest)
                    throw new RecoveryException(RecoveryFailure.Conflict);
            }
            await ValidateCurrent();
            var current = await RecoveryCurrentAsync(token);
            var candidate = plan.CandidateBytes();
            var settings = SettingsJson.Read(candidate);
            ValidateCredentialTransition(SetupSettings.Begin(current.Settings).Setup!, settings.Setup!, null);
            var original = Path.Combine(DataDirectory, $"settings.recovery.{Guid.NewGuid():N}.bak");
            // Create-only, flushed original BEFORE replacement. Failure after this leaves redundant
            // recovery evidence, never an untracked native-key transaction or an overwritten snapshot.
            await WriteAtomicAsync(current.Bytes, original, false, null, token, recovery: true);
            await WriteAtomicAsync(candidate, FilePath, true, null, token, recovery: true, beforeCommit: ValidateCurrent);
            return new(FilePath, ConfigurationSnapshot.Hash(candidate), original);
        }
        catch (ContractException) { throw new RecoveryException(RecoveryFailure.InvalidBackup); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { throw new RecoveryException(RecoveryFailure.Unavailable); }
    }

    internal void RetryRecoveryCleanup(string ownedTemporary)
    {
        try
        {
            RecoveryIo?.Invoke(SettingsIoPoint.BeforeCleanup, CancellationToken.None);
            File.Delete(ownedTemporary);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new RecoveryException(RecoveryFailure.CleanupPending, ownedTemporary); }
    }
}
