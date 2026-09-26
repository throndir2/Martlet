using Martlet.Core.Contracts;

namespace Martlet.Core.Settings;

// A narrow internal fault/retirement seam around real IO, not a replaceable filesystem.
internal enum SettingsIoPoint { BeforeStage, AfterWrite, BeforeFlush, AfterFlush, BeforeCommit, AfterCommit, BeforeCleanup, BeforeRead, AfterRead }

public sealed partial class SettingsStore
{
    internal Action<SettingsIoPoint, CancellationToken>? RecoveryIo { get; init; }
    private ConfigurationRestoreCleanup? standaloneRestoreCleanup;

    private static async Task<byte[]> ReadBoundedAsync(string path, int maximum, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await ReadBoundedAsync(stream, maximum, token);
    }

    internal static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximum, CancellationToken token)
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
        var importedCompanion = imported.Companion;
        var importedMemory = imported.Memory;
        var restored = SetupSettings.Begin(imported);
        var currentPairings = (current.Setup?.RetainedGatewayCredentials ?? []).Concat(
            (current.Setup?.Routes ?? []).Where(route =>
                route.CredentialId is not null &&
                route.RouteType is SetupRouteType.GatewayOllama or SetupRouteType.GatewayF5)
            .Select(RetainedGatewayCredential.From)).ToArray();
        var routes = restored.Setup!.Routes.Select(route =>
        {
            var disabled = route.DisableForRestore();
            var matches = currentPairings.Where(item => MatchesDestination(disabled, item)).ToArray();
            return matches.Length == 1 ? disabled with
            {
                CredentialId = matches[0].CredentialId,
                GatewayDeviceId = matches[0].Scope.DeviceId
            } : disabled;
        }).ToArray();
        var attached = routes.Where(route => route.CredentialId is not null)
            .Select(route => route.CredentialId!.Value).ToHashSet();
        var retained = currentPairings.Where(item => !attached.Contains(item.CredentialId)).ToArray();
        if (retained.Length > SetupSettings.MaximumRetainedGatewayCredentials)
            throw new RecoveryException(RecoveryFailure.CleanupCapacity);
        var pending = (current.Setup?.PendingRemovals ?? []).Concat(
            (current.Setup?.Routes ?? []).Where(route => route.CredentialId is not null &&
                route.RouteType is null or SetupRouteType.OpenAi or SetupRouteType.ChatCompletions)
            .Select(route => new PendingCredentialRemoval
            {
                Role = route.Role, CredentialId = route.CredentialId!.Value,
                Scope = route.RouteType == SetupRouteType.ChatCompletions ? CredentialScopeSettings.From(route) : null
            })).ToArray();
        if (pending.Length > 16)
            throw new RecoveryException(RecoveryFailure.CleanupCapacity);
        restored = restored with
        {
            Profile = restored.Profile with { Credentials = current.Profile.Credentials.ToArray() },
            Setup = restored.Setup! with
            {
                Checkpoint = SetupStep.Destinations,
                Routes = routes,
                PendingRemovals = pending,
                RetainedGatewayCredentials = retained
            },
            Audio = restored.Audio is not { } audio ? null : audio with
            {
                Input = audio.Input with { ConfigurationRevision = Guid.NewGuid(), Checkpoint = null },
                Output = audio.Output with { ConfigurationRevision = Guid.NewGuid(), Checkpoint = null }
            },
            Companion = importedCompanion ?? current.Companion ?? CompanionSettings.Create(),
            Memory = (importedMemory ?? current.Memory ?? MemorySettings.Create()).DisableForRestore()
        };
        restored.Validate();
        return restored;
    }

    private static bool MatchesDestination(SetupRoute route, RetainedGatewayCredential pairing) =>
        route.Role == pairing.Role && route.RouteType == pairing.Scope.RouteType &&
        route.ProviderAlias == pairing.Scope.ProviderAlias && route.Origin == pairing.Scope.Origin &&
        route.Gateway?.HostId == pairing.Scope.HostId &&
        route.Gateway?.SpkiFingerprint == pairing.Scope.SpkiFingerprint &&
        route.Gateway?.DeviceRole == pairing.Scope.DeviceRole;

    public async Task<ConfigurationRecoveryReceipt> RestoreConfigurationAsync(ConfigurationRestorePlan plan,
        ConfigurationRestoreApproval approval, CancellationToken token = default)
    {
        await using var scope = await OpenConfigurationRestoreAsync(plan, approval, token);
        try
        {
            await scope.CommitAsync(static () => { }, static () => { });
            var evidence = await scope.VerifyCommittedAsync();
            return new(evidence.Path, evidence.Revision, evidence.OriginalSnapshot);
        }
        finally
        {
            if (scope.Cleanup is { } pending)
            {
                Volatile.Write(ref standaloneRestoreCleanup, pending);
            }
        }
    }

    public async Task<ConfigurationRestoreScope> OpenConfigurationRestoreAsync(ConfigurationRestorePlan plan,
        ConfigurationRestoreApproval approval, CancellationToken token = default)
    {
        approval.Consume(plan);
        if (plan.Destination != FilePath) throw new RecoveryException(RecoveryFailure.Conflict);
        FileStream? writeLock = null, sourceLock = null, currentLock = null;
        try
        {
            token.ThrowIfCancellationRequested();
            RecoveryPath(FilePath);
            token.ThrowIfCancellationRequested();
            writeLock = new FileStream(FilePath + ".lock", FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
            token.ThrowIfCancellationRequested();
            if (Volatile.Read(ref standaloneRestoreCleanup) is { IsPending: true } pending)
                throw new RecoveryException(RecoveryFailure.CleanupPending, pending.Path);
            RecoveryPath(plan.SourcePath);
            token.ThrowIfCancellationRequested();
            sourceLock = ConfigurationRestoreScope.Pin(plan.SourcePath);
            token.ThrowIfCancellationRequested();
            currentLock = ConfigurationRestoreScope.Pin(FilePath);
            token.ThrowIfCancellationRequested();
            var current = await RecoveryCurrentAsync(token);
            token.ThrowIfCancellationRequested();
            if (current.Revision != plan.ExpectedRevision || current.Settings.Profile.Id != plan.ProfileId)
                throw new RecoveryException(RecoveryFailure.Conflict);
            await ConfigurationRestoreScope.CheckBytesAsync(sourceLock, plan.SourcePath,
                plan.SourceFileDigest, ConfigurationSnapshot.MaximumBytes, token, token.ThrowIfCancellationRequested);
            await ConfigurationRestoreScope.CheckBytesAsync(currentLock, FilePath,
                plan.ExpectedRevision, AppSettings.MaxFileBytes, token, token.ThrowIfCancellationRequested);
            var candidate = plan.CandidateBytes();
            ValidateRestoreCandidate(current.Settings, candidate, plan);
            token.ThrowIfCancellationRequested();
            var original = Path.Combine(DataDirectory, $"settings.recovery.{Guid.NewGuid():N}.bak");
            var scope = new ConfigurationRestoreScope(this, plan, token, writeLock, sourceLock, currentLock,
                current.Bytes, candidate, original);
            writeLock = sourceLock = currentLock = null;
            return scope;
        }
        catch (ContractException) { throw new RecoveryException(RecoveryFailure.InvalidBackup); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { throw new RecoveryException(RecoveryFailure.Unavailable); }
        finally
        {
            currentLock?.Dispose();
            sourceLock?.Dispose();
            writeLock?.Dispose();
        }
    }

    public async Task<ConfigurationCurrentReadScope> OpenCurrentConfigurationReadAsync(CancellationToken token = default)
    {
        FileStream? writeLock = null, currentLock = null, currentNameLock = null;
        void Point(SettingsIoPoint point)
        {
            token.ThrowIfCancellationRequested();
            RecoveryIo?.Invoke(point, token);
            token.ThrowIfCancellationRequested();
        }
        try
        {
            token.ThrowIfCancellationRequested();
            RecoveryPath(DataDirectory);
            token.ThrowIfCancellationRequested();
            RecoveryPath(FilePath);
            token.ThrowIfCancellationRequested();
            if (!Directory.Exists(DataDirectory)) throw new RecoveryException(RecoveryFailure.Unavailable);
            token.ThrowIfCancellationRequested();
            writeLock = new FileStream(FilePath + ".lock", FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
            token.ThrowIfCancellationRequested();
            if (Volatile.Read(ref standaloneRestoreCleanup) is { IsPending: true } pending)
                throw new RecoveryException(RecoveryFailure.CleanupPending, pending.Path);
            currentLock = ConfigurationRestoreScope.Pin(FilePath);
            token.ThrowIfCancellationRequested();
            currentNameLock = ConfigurationRestoreScope.Pin(FilePath);
            Point(SettingsIoPoint.BeforeRead);
            var current = await RecoveryCurrentAsync(token);
            Point(SettingsIoPoint.AfterRead);
            var inspection = new ConfigurationCurrentInspection(FilePath, System.Text.Encoding.UTF8.GetString(current.Bytes),
                current.Revision, current.Settings.Profile.Id, current.Settings.SchemaVersion);
            var scope = new ConfigurationCurrentReadScope(this, token, writeLock, currentLock, currentNameLock, inspection);
            await scope.VerifyAsync();
            token.ThrowIfCancellationRequested();
            writeLock = currentLock = currentNameLock = null;
            return scope;
        }
        catch (ContractException) { throw new RecoveryException(RecoveryFailure.InvalidBackup); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { throw new RecoveryException(RecoveryFailure.Unavailable); }
        finally
        {
            currentNameLock?.Dispose();
            currentLock?.Dispose();
            writeLock?.Dispose();
        }
    }

    internal static void ValidateRestoreCandidate(AppSettings current, byte[] candidate, ConfigurationRestorePlan plan)
    {
        var settings = SettingsJson.Read(candidate);
        if (settings.SchemaVersion != AppSettings.CurrentSchemaVersion || settings.Profile.Id != plan.ProfileId ||
            ConfigurationSnapshot.Hash(candidate) != plan.CandidateDigest ||
            !current.Profile.Credentials.SequenceEqual(settings.Profile.Credentials))
            throw new RecoveryException(RecoveryFailure.Conflict);
        ValidateCredentialTransition(SetupSettings.Begin(current).Setup!, settings.Setup!, null);
        if (settings.Memory?.Enabled != false)
            throw new RecoveryException(RecoveryFailure.Conflict);
        var owned = (current.Setup?.RetainedGatewayCredentials ?? []).Concat(
            (current.Setup?.Routes ?? []).Where(route => route.CredentialId is not null &&
                route.RouteType is SetupRouteType.GatewayOllama or SetupRouteType.GatewayF5)
            .Select(RetainedGatewayCredential.From)).ToArray();
        foreach (var route in settings.Setup!.Routes.Where(route =>
            route.RouteType is SetupRouteType.GatewayOllama or SetupRouteType.GatewayF5 or SetupRouteType.LocalWhisper or
                SetupRouteType.ChatCompletions or SetupRouteType.LocalWindowsStt or SetupRouteType.LocalWindowsTts))
        {
            if (route.Enabled != false || route.Consent is not null || route.GatewaySnapshot is not null ||
                route.Reference is not null || route.LocalStt is not null ||
                route.CredentialId is not null && (route.RouteType == SetupRouteType.ChatCompletions ||
                    !owned.Contains(RetainedGatewayCredential.From(route))))
                throw new RecoveryException(RecoveryFailure.Conflict);
        }
        if ((settings.Setup.RetainedGatewayCredentials ?? []).Any(item => !owned.Contains(item)))
            throw new RecoveryException(RecoveryFailure.Conflict);
    }

    internal void RetryRecoveryCleanup(string ownedTemporary)
    {
        var pending = Volatile.Read(ref standaloneRestoreCleanup);
        if (pending?.Path == ownedTemporary)
        {
            Task.Run(() => pending.RetryAsync()).GetAwaiter().GetResult();
            Interlocked.CompareExchange(ref standaloneRestoreCleanup, null, pending);
            return;
        }
        try
        {
            RecoveryIo?.Invoke(SettingsIoPoint.BeforeCleanup, CancellationToken.None);
            File.Delete(ownedTemporary);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new RecoveryException(RecoveryFailure.CleanupPending, ownedTemporary); }
    }
}
