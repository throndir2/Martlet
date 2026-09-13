using System.Security.Cryptography;
using Martlet.Core.Contracts;

namespace Martlet.Core.Settings;

public enum SettingsLoadState { FirstRun, Loaded, Invalid, Inaccessible }
public sealed record SettingsLoadResult(SettingsLoadState State, AppSettings? Settings, string? Revision, MartletError? Error);
public sealed record SettingsSaveResult(bool Saved, string? Revision, MartletError? Error,
    bool MigratedFromVersion1 = false, string? SnapshotFileName = null);

public sealed class SettingsStore
{
    public string DataDirectory { get; }
    public string FilePath => Path.Combine(DataDirectory, "settings.json");

    public SettingsStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        if (!Path.IsPathFullyQualified(dataDirectory))
            throw new ArgumentException("Use an absolute data directory.", nameof(dataDirectory));
        DataDirectory = Path.GetFullPath(dataDirectory);
    }

    public static string DefaultDataDirectory()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root))
            throw new InvalidOperationException("A per-user data directory is unavailable. Supply an absolute --data-directory.");
        return Path.Combine(root, "Martlet");
    }

    public async Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var bytes = new byte[AppSettings.MaxFileBytes + 1];
            var length = 0;
            while (length < bytes.Length)
            {
                var count = await stream.ReadAsync(bytes.AsMemory(length), cancellationToken);
                if (count == 0)
                    break;
                length += count;
            }
            var settings = SettingsJson.Read(bytes.AsMemory(0, length));
            return new(SettingsLoadState.Loaded, settings, Convert.ToHexString(SHA256.HashData(bytes.AsSpan(0, length))), null);
        }
        catch (FileNotFoundException) { return MissingFile(); }
        catch (DirectoryNotFoundException) { return MissingFile(); }
        catch (ContractException ex)
        {
            return new(SettingsLoadState.Invalid, null, null, Failure(
                ex.Code == ErrorCode.UnsupportedVersion ? ErrorCode.UnsupportedVersion : ErrorCode.SettingsMalformed,
                ex.Code == ErrorCode.UnsupportedVersion
                    ? "Settings use an unsupported version. Use a compatible Martlet build or restore a compatible backup; the file was not changed."
                    : "Settings are malformed or exceed supported limits. Back up the file, then correct it or restore a compatible backup; it was not changed.",
                "settings.restore"));
        }
        catch (UnauthorizedAccessException) { return Inaccessible(); }
        catch (IOException) { return Inaccessible(); }
    }

    // A null revision means create-only. A loaded revision prevents accidental lost updates.
    public async Task<SettingsSaveResult> SaveAsync(AppSettings settings, string? expectedRevision, CancellationToken cancellationToken = default)
        => await SaveCoreAsync(settings, expectedRevision, lockHeld: false, cancellationToken);

    private async Task<SettingsSaveResult> SaveCoreAsync(AppSettings settings, string? expectedRevision,
        bool lockHeld, CancellationToken cancellationToken, Guid? removedCredential = null)
    {
        try
        {
            var bytes = ContractJson.Write(settings, AppSettings.MaxFileBytes);
            Directory.CreateDirectory(DataDirectory);
            using var writeLock = lockHeld ? null : new FileStream(FilePath + ".lock", FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
            var existing = await LoadAsync(cancellationToken);
            if (existing.Error is not null)
                return new(false, null, existing.Error);
            if (!string.Equals(existing.Revision, expectedRevision, StringComparison.Ordinal))
                return new(false, null, Failure(ErrorCode.SettingsConflict,
                    "Settings changed since they were loaded. Reload and review the current profile before saving.", "settings.reload"));

            if (existing.Settings?.SchemaVersion == 2 && settings.SchemaVersion == 1)
                return new(false, null, Failure(ErrorCode.UnsupportedVersion,
                    "A setup profile cannot be downgraded by saving version 1. Keep its snapshot and use compatible settings.", "settings.restore"));
            if (existing.Settings is { } prior && settings.SchemaVersion == 2 &&
                (prior.Profile.Id != settings.Profile.Id ||
                 !prior.Profile.Credentials.SequenceEqual(settings.Profile.Credentials)))
                return new(false, null, Failure(ErrorCode.InvalidContract,
                    "Setup must preserve the existing profile identity and legacy credential references.", "settings.correct"));
            if (existing.Settings?.Setup is { } priorSetup && settings.Setup is { } nextSetup)
                ValidateCredentialTransition(priorSetup, nextSetup, removedCredential);
            if (existing.Settings?.Audio is { } priorAudio && settings.Audio is { } nextAudio)
            {
                ValidateAudioTransition(priorAudio.Input, nextAudio.Input);
                ValidateAudioTransition(priorAudio.Output, nextAudio.Output);
            }
            var migrated = existing.Settings?.SchemaVersion == 1 && settings.SchemaVersion == 2;
            var snapshot = migrated ? $"settings.v1.{Guid.NewGuid():N}.bak" : null;
            var temporary = Path.Combine(DataDirectory, $"settings.{Guid.NewGuid():N}.tmp");
            try
            {
                await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await stream.WriteAsync(bytes, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                    stream.Flush(flushToDisk: true);
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (existing.State == SettingsLoadState.FirstRun)
                    File.Move(temporary, FilePath, overwrite: false);
                else
                    File.Replace(temporary, FilePath, snapshot is null ? null : Path.Combine(DataDirectory, snapshot));
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
            return new(true, Convert.ToHexString(SHA256.HashData(bytes)), null, migrated, snapshot);
        }
        catch (ContractException ex)
        {
            return new(false, null, Failure(ex.Code, "Settings are invalid or unsupported. Correct the profile before saving; existing settings were preserved.", "settings.correct"));
        }
        catch (UnauthorizedAccessException) { return new(false, null, StorageError()); }
        catch (IOException) { return new(false, null, StorageError()); }
    }

    internal async Task<SetupSaveResult> ReplaceCredentialAsync(AppSettings staged, AppSettings updated, string? expectedRevision,
        SetupRole role, Guid id, SecretLease secret, ICredentialStore credentials, CancellationToken token)
    {
        staged.Validate();
        updated.Validate();
        try
        {
            Directory.CreateDirectory(DataDirectory);
            using var writeLock = new FileStream(FilePath + ".lock", FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
            // Durable metadata precedes the OS write. A crash leaves an exact owned cleanup reference,
            // not an untracked key. The same lock protects promotion and irreversible rollback.
            var prepared = await SaveCoreAsync(staged, expectedRevision, lockHeld: true, token);
            if (!prepared.Saved) return new(prepared, staged);
            var binding = CredentialBinding.For(staged, role, id);
            if (token.IsCancellationRequested)
                return new(AttachmentFailure(prepared), staged);
            var written = credentials.Write(binding, secret);
            if (written != CredentialError.None)
                return new(AttachmentFailure(prepared), staged, written);
            SettingsSaveResult committed;
            try { committed = await SaveCoreAsync(updated, prepared.Revision, lockHeld: true, token); }
            catch (OperationCanceledException) { committed = AttachmentFailure(prepared); }
            if (committed.Saved)
                return new(committed with { MigratedFromVersion1 = prepared.MigratedFromVersion1, SnapshotFileName = prepared.SnapshotFileName }, updated);
            var cleanup = credentials.Delete(binding);
            return new(AttachmentFailure(prepared), staged,
                cleanup is CredentialError.None or CredentialError.Missing ? CredentialError.None : cleanup);
        }
        catch (UnauthorizedAccessException) { return new(new(false, null, StorageError()), staged); }
        catch (IOException) { return new(new(false, null, StorageError()), staged); }
    }

    private static SettingsSaveResult AttachmentFailure(SettingsSaveResult prepared) => new(false, prepared.Revision,
        Failure(ErrorCode.SettingsConflict,
            "The metadata checkpoint was saved, but the new key was not attached. Reload Setup and review the pending owned reference; explicitly retry removal before adding another key.",
            "settings.reload"), prepared.MigratedFromVersion1, prepared.SnapshotFileName);

    private static void ValidateCredentialTransition(SetupSettings prior, SetupSettings next, Guid? removed)
    {
        foreach (var route in prior.Routes)
        {
            var replacement = next.Routes.SingleOrDefault(item => item.Role == route.Role);
            if (route.CredentialId is { } id)
                ContractRules.Require(replacement?.CredentialId == id ||
                    next.PendingRemovals.Any(item => item.Role == route.Role && item.CredentialId == id),
                    "Detach an active credential explicitly before removing its route; do not orphan an owned key.");
            if (replacement is not null && (replacement.ModelId != route.ModelId || replacement.VoiceId != route.VoiceId ||
                replacement.CredentialId != route.CredentialId || replacement.Origin != route.Origin || replacement.ProviderAlias != route.ProviderAlias))
                ContractRules.Require(replacement.ConfigurationRevision != route.ConfigurationRevision,
                    "Route changes require a fresh configuration revision and renewed destination selection.");
        }
        foreach (var pending in prior.PendingRemovals)
            ContractRules.Require(pending.CredentialId == removed || next.PendingRemovals.Contains(pending) ||
                next.Routes.Any(route => route.Role == pending.Role && route.CredentialId == pending.CredentialId),
                "Use explicit credential cleanup to remove a pending owned reference.");
    }

    private static void ValidateAudioTransition(AudioChoice prior, AudioChoice next)
    {
        if (prior.EndpointId != next.EndpointId || prior.DisplayName != next.DisplayName)
            ContractRules.Require(prior.ConfigurationRevision != next.ConfigurationRevision,
                "Changed audio selection requires a fresh configuration revision; prior qualification cannot be reused.");
    }

    internal async Task<SetupSaveResult> RemoveDetachedCredentialAsync(AppSettings settings, string? expectedRevision,
        PendingCredentialRemoval removal, ICredentialStore credentials, CancellationToken token)
    {
        try
        {
            using var writeLock = new FileStream(FilePath + ".lock", FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
            var current = await LoadAsync(token);
            if (current.Error is not null)
                return new(new(false, null, current.Error), settings);
            if (current.Revision != expectedRevision || current.Settings?.Setup is not { } setup ||
                current.Settings.Profile.Id != settings.Profile.Id ||
                !current.Settings.Profile.Credentials.SequenceEqual(settings.Profile.Credentials) ||
                !setup.PendingRemovals.Contains(removal))
                return new(new(false, null, Failure(ErrorCode.SettingsConflict,
                    "Settings changed. Reload and review detached credentials before removal.", "settings.reload")), settings);
            var updated = settings with { Setup = settings.Setup! with
            {
                PendingRemovals = settings.Setup!.PendingRemovals.Where(item => item != removal).ToArray()
            } };
            ContractJson.Write(updated, AppSettings.MaxFileBytes);
            ValidateCredentialTransition(setup, updated.Setup!, removal.CredentialId);
            token.ThrowIfCancellationRequested();
            var error = credentials.Delete(CredentialBinding.For(current.Settings, removal.Role, removal.CredentialId));
            if (error is not (CredentialError.None or CredentialError.Missing))
                return new(new(false, null, null), current.Settings, error);
            // Once deleted, finish metadata without cancellation. A failed commit retains the retry marker.
            var save = await SaveCoreAsync(updated, current.Revision, lockHeld: true, CancellationToken.None, removal.CredentialId);
            if (!save.Saved)
                save = save with { Error = Failure(save.Error!.Code,
                    "The selected key is removed or already missing, but cleanup metadata was not saved. Reload and retry this pending removal; missing is a safe cleanup result.",
                    "settings.reload") };
            return new(save, save.Saved ? updated : current.Settings);
        }
        catch (UnauthorizedAccessException) { return new(new(false, null, StorageError()), settings); }
        catch (IOException) { return new(new(false, null, StorageError()), settings); }
        catch (ContractException)
        {
            return new(new(false, null, Failure(ErrorCode.InvalidContract,
                "Review and correct the checkpoint before removing credentials. No invalid configuration can authorize removal.", "settings.correct")), settings);
        }
    }

    private static SettingsLoadResult Inaccessible() => new(SettingsLoadState.Inaccessible, null, null, StorageError());

    private SettingsLoadResult MissingFile()
    {
        // A missing profile below an existing directory is first run; an unavailable
        // drive or a file occupying an ancestor path is a storage failure.
        for (var parent = DataDirectory; parent is not null; parent = Path.GetDirectoryName(parent))
        {
            try
            {
                return File.GetAttributes(parent).HasFlag(FileAttributes.Directory)
                    ? new(SettingsLoadState.FirstRun, null, null, null)
                    : Inaccessible();
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            catch (UnauthorizedAccessException) { return Inaccessible(); }
            catch (IOException) { return Inaccessible(); }
        }
        return Inaccessible();
    }

    private static MartletError StorageError() => Failure(ErrorCode.SettingsInaccessible,
        "Settings cannot be accessed. Check the data directory permissions, free disk space and other Martlet processes, then retry. Do not run as administrator.",
        "settings.check_access");
    private static MartletError Failure(ErrorCode code, string summary, string action) =>
        new() { Code = code, Stage = Stage.Settings, Retryable = code == ErrorCode.SettingsInaccessible, Summary = summary, ActionId = action };
}
