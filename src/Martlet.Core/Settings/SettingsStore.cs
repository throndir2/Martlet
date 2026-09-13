using System.Security.Cryptography;
using Martlet.Core.Contracts;

namespace Martlet.Core.Settings;

public enum SettingsLoadState { FirstRun, Loaded, Invalid, Inaccessible }
public sealed record SettingsLoadResult(SettingsLoadState State, AppSettings? Settings, string? Revision, MartletError? Error);
public sealed record SettingsSaveResult(bool Saved, string? Revision, MartletError? Error);

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
    {
        try
        {
            var bytes = ContractJson.Write(settings, AppSettings.MaxFileBytes);
            Directory.CreateDirectory(DataDirectory);
            using var writeLock = new FileStream(FilePath + ".lock", FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
            var existing = await LoadAsync(cancellationToken);
            if (existing.Error is not null)
                return new(false, null, existing.Error);
            if (!string.Equals(existing.Revision, expectedRevision, StringComparison.Ordinal))
                return new(false, null, Failure(ErrorCode.SettingsConflict,
                    "Settings changed since they were loaded. Reload and review the current profile before saving.", "settings.reload"));

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
                    File.Replace(temporary, FilePath, destinationBackupFileName: null);
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
            return new(true, Convert.ToHexString(SHA256.HashData(bytes)), null);
        }
        catch (ContractException ex)
        {
            return new(false, null, Failure(ex.Code, "Settings are invalid or unsupported. Correct the profile before saving; existing settings were preserved.", "settings.correct"));
        }
        catch (UnauthorizedAccessException) { return new(false, null, StorageError()); }
        catch (IOException) { return new(false, null, StorageError()); }
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
