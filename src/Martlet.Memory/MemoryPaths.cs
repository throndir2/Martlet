namespace Martlet.Memory;

internal static class MemoryPaths
{
    internal static string NormalizeLocalPath(string path, bool directory, bool inspectFileSystem)
    {
        try
        {
            MemoryGuard.Require(!string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) &&
                !path.StartsWith(@"\\", StringComparison.Ordinal) &&
                !path.StartsWith("//", StringComparison.Ordinal) &&
                !path.Contains('\0'), MemoryFailure.InvalidPath);
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            MemoryGuard.Require(full != Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full)!),
                MemoryFailure.InvalidPath);
            if (OperatingSystem.IsWindows())
            {
                MemoryGuard.Require(full.Length > 3 && full[1] == ':' && !full[2..].Contains(':'),
                    MemoryFailure.InvalidPath);
            }
            if (inspectFileSystem)
            {
                CheckLocalVolume(full);
                CheckAncestors(directory ? full : Path.GetDirectoryName(full)!);
                CheckEntry(full);
            }
            return full;
        }
        catch (MemoryException)
        {
            throw;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException)
        {
            throw new MemoryException(MemoryFailure.InvalidPath);
        }
        catch (UnauthorizedAccessException)
        {
            throw new MemoryException(MemoryFailure.AccessDenied);
        }
        catch (IOException)
        {
            throw new MemoryException(MemoryFailure.IoFailure);
        }
    }

    internal static string ExportDestination(string path)
    {
        var full = NormalizeLocalPath(path, directory: false, inspectFileSystem: true);
        MemoryGuard.Require(Path.GetExtension(full).Equals(".json", StringComparison.OrdinalIgnoreCase) &&
            Directory.Exists(Path.GetDirectoryName(full)), MemoryFailure.InvalidPath);
        var name = Path.GetFileName(full);
        MemoryGuard.Require(!name.Equals(MemoryStore.StoreFileName, StringComparison.OrdinalIgnoreCase) &&
            !name.Equals(MemoryStore.LockFileName, StringComparison.OrdinalIgnoreCase) &&
            !name.Equals(MemoryStore.PendingFileName, StringComparison.OrdinalIgnoreCase),
            MemoryFailure.InvalidPath);
        return full;
    }

    internal static bool IsAcceptedDriveType(DriveType type) =>
        type is DriveType.Fixed or DriveType.Removable or DriveType.Ram;

    private static void CheckLocalVolume(string full)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var volume = DriveInfo.GetDrives()
            .Where(drive => Contains(drive.RootDirectory.FullName, full, comparison))
            .OrderByDescending(drive => drive.RootDirectory.FullName.Length)
            .FirstOrDefault();
        MemoryGuard.Require(volume is not null && IsAcceptedDriveType(volume.DriveType),
            MemoryFailure.InvalidPath);
    }

    private static bool Contains(string root, string path, StringComparison comparison)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (string.Equals(root, path, comparison))
            return true;
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ||
            root.EndsWith(Path.AltDirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, comparison);
    }

    internal static void CheckAncestors(string directory)
    {
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
            CheckEntry(current.FullName);
    }

    internal static void CheckEntry(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            MemoryGuard.Require((attributes & FileAttributes.ReparsePoint) == 0, MemoryFailure.InvalidPath);
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}
