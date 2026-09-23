namespace Martlet.F5;

internal static class F5ReferencePaths
{
    internal static string NormalizeDirectory(string path, bool inspectFileSystem)
    {
        var full = Normalize(path);
        if (inspectFileSystem)
        {
            CheckLocalVolume(full);
            CheckAncestors(Directory.Exists(full) ? full : Path.GetDirectoryName(full)!);
            CheckEntry(full, allowMissing: true, requireFile: false);
        }
        return full;
    }

    internal static string NormalizeSource(string path, bool requireExisting)
    {
        var full = Normalize(path);
        F5Guard.Require(Path.GetExtension(full).Equals(".wav", StringComparison.OrdinalIgnoreCase),
            F5Failure.InvalidPath);
        if (requireExisting)
        {
            CheckLocalVolume(full);
            CheckAncestors(Path.GetDirectoryName(full)!);
            CheckEntry(full, allowMissing: false, requireFile: true);
        }
        return full;
    }

    internal static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    internal static void CheckAncestors(string directory)
    {
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
            CheckEntry(current.FullName, allowMissing: true, requireFile: false);
    }

    internal static void CheckEntry(string path, bool allowMissing, bool requireFile)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            F5Guard.Require((attributes & FileAttributes.ReparsePoint) == 0, F5Failure.InvalidPath);
            if (requireFile)
                F5Guard.Require((attributes & FileAttributes.Directory) == 0, F5Failure.InvalidPath);
        }
        catch (FileNotFoundException)
        {
            if (!allowMissing)
                throw new F5Exception(F5Failure.SourceMissing);
        }
        catch (DirectoryNotFoundException)
        {
            if (!allowMissing)
                throw new F5Exception(F5Failure.SourceMissing);
        }
        catch (UnauthorizedAccessException)
        {
            throw new F5Exception(F5Failure.AccessDenied);
        }
        catch (IOException)
        {
            throw new F5Exception(F5Failure.IoFailure);
        }
    }

    private static string Normalize(string path)
    {
        try
        {
            F5Guard.Require(!string.IsNullOrWhiteSpace(path) &&
                path.Length <= F5ReferenceLimits.MaximumSourcePathCharacters &&
                Path.IsPathFullyQualified(path) &&
                !path.StartsWith(@"\\", StringComparison.Ordinal) &&
                !path.StartsWith("//", StringComparison.Ordinal) &&
                !path.Contains('\0'), F5Failure.InvalidPath);
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            var root = Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full)!);
            F5Guard.Require(!string.Equals(full, root, PathComparison), F5Failure.InvalidPath);
            if (OperatingSystem.IsWindows())
            {
                F5Guard.Require(full.Length > 3 && full[1] == ':' &&
                    !full[2..].Contains(':'), F5Failure.InvalidPath);
            }
            return full;
        }
        catch (F5Exception)
        {
            throw;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new F5Exception(F5Failure.InvalidPath);
        }
    }

    private static void CheckLocalVolume(string full)
    {
        try
        {
            var volume = DriveInfo.GetDrives()
                .Where(drive => Contains(drive.RootDirectory.FullName, full))
                .OrderByDescending(drive => drive.RootDirectory.FullName.Length)
                .FirstOrDefault();
            F5Guard.Require(volume is not null &&
                volume.DriveType is DriveType.Fixed or DriveType.Removable or DriveType.Ram,
                F5Failure.InvalidPath);
        }
        catch (F5Exception)
        {
            throw;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new F5Exception(F5Failure.IoFailure);
        }
    }

    private static bool Contains(string root, string path)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (string.Equals(root, path, PathComparison))
            return true;
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ||
            root.EndsWith(Path.AltDirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, PathComparison);
    }
}
