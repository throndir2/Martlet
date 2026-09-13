namespace Martlet.Updates;

internal static class LocalPaths
{
    internal static string Canonical(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
            path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
            throw new StagingException(StagingFailure.AccessDenied);
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (OperatingSystem.IsWindows() && (full[2..].Contains(':') ||
            new DriveInfo(Path.GetPathRoot(full)!).DriveType == DriveType.Network))
            throw new StagingException(StagingFailure.AccessDenied);
        return full;
    }

    internal static void NoReparse(string path)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new StagingException(StagingFailure.AccessDenied);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    internal static bool Within(string path, string root) =>
        path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    internal static bool Exists(string path)
    {
        try { File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    internal static void Entry(string path)
    {
        if (path.Length is 0 or > 240) throw new StagingException(StagingFailure.CapacityExceeded);
        var parts = path.Split('/');
        if (parts.Length > 16) throw new StagingException(StagingFailure.CapacityExceeded);
        foreach (var part in parts)
        {
            if (part.Length is 0 or > 100 || part is "." or ".." || part.EndsWith('.') ||
                part.Any(c => !(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-')))
                throw new StagingException(StagingFailure.UnsafeEntry);
            var stem = part.Split('.')[0].ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" ||
                stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) ||
                    stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '0' and <= '9')
                throw new StagingException(StagingFailure.UnsafeEntry);
        }
        if (path is "manifest.json" or "SHA256SUMS.txt") return;
        if (parts.Length < 2 || parts[0] is not ("Desktop" or "Doctor" or "help" or "notices") ||
            parts.Any(p => p.Equals("data", StringComparison.OrdinalIgnoreCase) ||
                p.Equals("logs", StringComparison.OrdinalIgnoreCase) || p.Equals("models", StringComparison.OrdinalIgnoreCase) ||
                p.Equals("recordings", StringComparison.OrdinalIgnoreCase) || p.Equals("profiles", StringComparison.OrdinalIgnoreCase) ||
                p.EndsWith(".lock", StringComparison.OrdinalIgnoreCase) ||
                p.StartsWith("settings", StringComparison.OrdinalIgnoreCase) && p.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
            throw new StagingException(StagingFailure.UnsafeEntry);
    }

    internal static int Inventory(PayloadFile[] files, StagingLimits limits)
    {
        if (files.Length < 3 || files.Length > limits.MaximumEntries)
            throw new StagingException(StagingFailure.CapacityExceeded);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? previous = null;
        foreach (var file in files)
        {
            if (file is null || file.Path is null || !Wire.IsHash(file.Sha256))
                throw new StagingException(StagingFailure.InvalidManifest);
            Entry(file.Path);
            if (!paths.Add(file.Path)) throw new StagingException(StagingFailure.UnsafeEntry);
            if (previous is not null && StringComparer.Ordinal.Compare(previous, file.Path) >= 0)
                throw new StagingException(StagingFailure.InvalidManifest);
            previous = file.Path;
            for (var slash = file.Path.IndexOf('/'); slash >= 0; slash = file.Path.IndexOf('/', slash + 1))
            {
                var directory = file.Path[..slash];
                if (directories.TryGetValue(directory, out var exact) && exact != directory)
                    throw new StagingException(StagingFailure.UnsafeEntry);
                directories[directory] = directory;
                if (directories.Count > limits.MaximumDirectories)
                    throw new StagingException(StagingFailure.CapacityExceeded);
            }
            if (file.Bytes < 0 || file.Bytes > limits.MaximumFileBytes)
                throw new StagingException(StagingFailure.CapacityExceeded);
        }
        if (paths.Overlaps(directories.Keys)) throw new StagingException(StagingFailure.UnsafeEntry);
        if (!paths.Contains("manifest.json") || !paths.Contains("SHA256SUMS.txt"))
            throw new StagingException(StagingFailure.InvalidManifest);
        return directories.Count;
    }
}
