using System.Security.Cryptography;
using Martlet.Core.Contracts;

namespace Martlet.Core.Settings;

public enum SettingsLoadState { FirstRun, Loaded, Invalid, Inaccessible }
public sealed record SettingsLoadResult(SettingsLoadState State, AppSettings? Settings, string? Revision, MartletError? Error);
public sealed record SettingsSaveResult(bool Saved, string? Revision, MartletError? Error,
    int? MigratedFromSchemaVersion = null, string? SnapshotFileName = null)
{
    public bool MigratedFromVersion1 => MigratedFromSchemaVersion == 1;
}

public sealed partial class SettingsStore
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
                    ? "Settings were saved by a newer Martlet. Update Martlet or restore a compatible backup."
                    : "Settings are damaged or too large. Restore a compatible backup, or fix the settings file.",
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
                    "Settings changed since you opened them. Reload, review and save again.", "settings.reload"));

            if (existing.Settings is { } existingSettings && existingSettings.SchemaVersion > settings.SchemaVersion)
                return new(false, null, Failure(ErrorCode.UnsupportedVersion,
                    "Settings cannot be saved by this older Martlet. Update Martlet or restore a compatible backup.", "settings.restore"));
            if (existing.Settings is { } prior && settings.SchemaVersion >= 2 &&
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
            if (existing.Settings?.Companion is { } priorCompanion && settings.Companion is { } nextCompanion)
                ValidateCompanionTransition(priorCompanion, nextCompanion);
            if (existing.Settings?.Memory is { } priorMemory && settings.Memory is { } nextMemory)
                ValidateMemoryTransition(priorMemory, nextMemory);
            int? migratedFrom = existing.Settings is { } old && old.SchemaVersion < settings.SchemaVersion
                ? old.SchemaVersion
                : null;
            var snapshot = migratedFrom is { } version ? $"settings.v{version}.{Guid.NewGuid():N}.bak" : null;
            await WriteAtomicAsync(bytes, FilePath, existing.State != SettingsLoadState.FirstRun,
                snapshot is null ? null : Path.Combine(DataDirectory, snapshot), cancellationToken);
            return new(true, Convert.ToHexString(SHA256.HashData(bytes)), null, migratedFrom, snapshot);
        }
        catch (ContractException ex)
        {
            return new(false, null, Failure(ex.Code, "Settings are invalid. Correct them and try saving again.", "settings.correct"));
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
                return new(committed with { MigratedFromSchemaVersion = prepared.MigratedFromSchemaVersion, SnapshotFileName = prepared.SnapshotFileName }, updated);
            var cleanup = credentials.Delete(binding);
            return new(AttachmentFailure(prepared), staged,
                cleanup is CredentialError.None or CredentialError.Missing ? CredentialError.None : cleanup);
        }
        catch (UnauthorizedAccessException) { return new(new(false, null, StorageError()), staged); }
        catch (IOException) { return new(new(false, null, StorageError()), staged); }
    }

    private static SettingsSaveResult AttachmentFailure(SettingsSaveResult prepared) => new(false, prepared.Revision,
        Failure(ErrorCode.SettingsConflict,
            "The key was not saved. Reload Setup and remove the pending key before adding another.",
            "settings.reload"), prepared.MigratedFromSchemaVersion, prepared.SnapshotFileName);

    private static void ValidateCredentialTransition(SetupSettings prior, SetupSettings next, Guid? removed)
    {
        foreach (var route in prior.Routes)
        {
            var replacement = next.Routes.SingleOrDefault(item => item.Role == route.Role);
            if (route.CredentialId is { } id)
                ContractRules.Require(
                    replacement is { CredentialId: var replacementId } &&
                        replacementId == id && SameCredentialScope(route, replacement) ||
                    // A gateway route's credential is its host pairing's device credential, owned by that pairing
                    // (kept until the host is forgotten); handing the role to another computer does not orphan it.
                    // It never moves to a different host or device.
                    SelfHostSetup.IsGateway(route.RouteType) &&
                        next.Routes.All(item => item.CredentialId != id || SetupSettings.SamePairing(route, item)) ||
                    next.PendingRemovals.Any(item => RemovalMatches(route, item, id)) ||
                    (next.RetainedGatewayCredentials ?? []).Any(item =>
                        item.CredentialId == id && item.Role == route.Role &&
                        item.Scope == CredentialScopeSettings.From(route)),
                    "Detach an active credential explicitly before removing its route; do not orphan an owned key.");
            var priorMaterial = route with { Consent = null, ConfigurationRevision = Guid.Empty };
            var replacementMaterial = replacement is null
                ? null
                : replacement with { Consent = null, ConfigurationRevision = Guid.Empty };
            var migratedLegacyMaterial = route.RouteType is null
                ? route.UpgradeLegacy() with { Consent = null, ConfigurationRevision = Guid.Empty }
                : null;
            if (replacement is not null && replacementMaterial != priorMaterial &&
                replacementMaterial != migratedLegacyMaterial)
                ContractRules.Require(replacement.ConfigurationRevision != route.ConfigurationRevision,
                    "Route changes require a fresh configuration revision and renewed destination selection.");
        }
        foreach (var pending in prior.PendingRemovals)
            ContractRules.Require(pending.CredentialId == removed || next.PendingRemovals.Contains(pending) ||
                next.Routes.Any(route => route.Role == pending.Role && route.CredentialId == pending.CredentialId &&
                    RemovalMatches(route, pending, pending.CredentialId)),
                "Use explicit credential cleanup to remove a pending owned reference.");
        foreach (var retained in prior.RetainedGatewayCredentials ?? [])
            ContractRules.Require((next.RetainedGatewayCredentials ?? []).Contains(retained) ||
                next.Routes.Any(route => route.Role == retained.Role && route.CredentialId == retained.CredentialId &&
                    CredentialScopeSettings.From(route) == retained.Scope) ||
                next.PendingRemovals.Any(item => item.Role == retained.Role &&
                    item.CredentialId == retained.CredentialId && item.Scope == retained.Scope),
                "Retain the exact saved pairing or explicitly detach it; ordinary saves cannot forget trust.");
        foreach (var retained in next.RetainedGatewayCredentials ?? [])
            ContractRules.Require((prior.RetainedGatewayCredentials ?? []).Contains(retained) ||
                prior.Routes.Any(route => route.Role == retained.Role && route.CredentialId == retained.CredentialId &&
                    CredentialScopeSettings.From(route) == retained.Scope),
                "Only a current owned pairing can become retained; imported pairings are not authority.");
    }

    private static bool SameCredentialScope(SetupRoute prior, SetupRoute replacement) =>
        CredentialScopeSettings.From(prior) == CredentialScopeSettings.From(replacement);

    private static bool RemovalMatches(
        SetupRoute prior,
        PendingCredentialRemoval removal,
        Guid credentialId)
    {
        if (removal.Role != prior.Role || removal.CredentialId != credentialId)
            return false;
        var expected = CredentialScopeSettings.From(prior);
        return removal.Scope is null
            ? expected.RouteType == SetupRouteType.OpenAi
            : removal.Scope == expected;
    }

    private static void ValidateAudioTransition(AudioChoice prior, AudioChoice next)
    {
        if (prior.EndpointId != next.EndpointId || prior.DisplayName != next.DisplayName)
            ContractRules.Require(prior.ConfigurationRevision != next.ConfigurationRevision,
                "Changed audio selection requires a fresh configuration revision; prior qualification cannot be reused.");
    }

    private static void ValidateCompanionTransition(CompanionSettings prior, CompanionSettings next)
    {
        foreach (var persona in prior.Personas)
        {
            var replacement = next.Personas.SingleOrDefault(item => item.Id == persona.Id);
            if (replacement is not null &&
                (replacement.Name != persona.Name || replacement.Text != persona.Text || replacement.Styles != persona.Styles ||
                 replacement.Breaks != persona.Breaks))
                ContractRules.Require(replacement.ConfigurationRevision != persona.ConfigurationRevision,
                    "Changed persona content, response styles or speech breaks require a fresh configuration revision.");
        }
    }

    private static void ValidateMemoryTransition(MemorySettings prior, MemorySettings next)
    {
        if (prior.Enabled != next.Enabled || prior.StoragePolicy != next.StoragePolicy ||
            !string.Equals(prior.CustomDirectory, next.CustomDirectory, StringComparison.Ordinal))
            ContractRules.Require(prior.ConfigurationRevision != next.ConfigurationRevision,
                "Changed memory enablement or storage selection requires a fresh configuration revision.");
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
            var error = credentials.Delete(CredentialBinding.For(current.Settings, removal));
            if (error is not (CredentialError.None or CredentialError.Missing))
                return new(new(false, null, null), current.Settings, error);
            // Once deleted, finish metadata without cancellation. A failed commit retains the retry marker.
            var save = await SaveCoreAsync(updated, current.Revision, lockHeld: true, CancellationToken.None, removal.CredentialId);
            if (!save.Saved)
                save = save with { Error = Failure(save.Error!.Code,
                    "The key was removed, but Martlet could not save the cleanup. Reload and retry the pending removal.",
                    "settings.reload") };
            return new(save, save.Saved ? updated : current.Settings);
        }
        catch (UnauthorizedAccessException) { return new(new(false, null, StorageError()), settings); }
        catch (IOException) { return new(new(false, null, StorageError()), settings); }
        catch (ContractException)
        {
            return new(new(false, null, Failure(ErrorCode.InvalidContract,
                "Review and correct Setup before removing credentials.", "settings.correct")), settings);
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
        "Settings cannot be accessed. Check your data folder permissions, free space and other Martlet windows.",
        "settings.check_access");
    private static MartletError Failure(ErrorCode code, string summary, string action) =>
        new() { Code = code, Stage = Stage.Settings, Retryable = code == ErrorCode.SettingsInaccessible, Summary = summary, ActionId = action };
}
