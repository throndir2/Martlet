namespace Martlet.Core.Installation;

/// <summary>A models folder Ollama is told to use, and whether it reaches its models through a link: <paramref name="LinkPath"/>
/// is the first part of <paramref name="Path"/> that is a junction or symbolic link (often the models folder itself, moved to
/// another drive and replaced with a junction), <paramref name="LinkTarget"/> where that link points, and
/// <paramref name="RealPath"/> the same folder with every link resolved. <paramref name="RealPathUsable"/>: the real folder is
/// there, isn't a link and holds Ollama's <c>blobs</c> or <c>manifests</c>.</summary>
public sealed record OllamaModelsFolder(string Path, string? LinkPath, string? LinkTarget, string? RealPath, bool RealPathUsable)
{
    public bool Linked => LinkPath is not null;

    /// <summary>Walks <paramref name="path"/> from its root and resolves each junction or symbolic link on the way. Reads only.</summary>
    public static OllamaModelsFolder Inspect(string path)
    {
        string full;
        try { full = System.IO.Path.GetFullPath(path); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return new(path, null, null, null, false);
        }
        string? linkPath = null, linkTarget = null;
        var current = full;
        // Each pass resolves the first link left in the path; a target can itself sit behind another link.
        for (var pass = 0; pass < 8; pass++)
        {
            var link = FirstLink(current);
            if (link is null)
                return new(full, linkPath, linkTarget, linkPath is null ? full : current, Usable(current));
            if (link.Value.Final is null) return new(full, linkPath ?? link.Value.Prefix, linkTarget ?? link.Value.Target, null, false);
            linkPath ??= link.Value.Prefix;
            linkTarget ??= link.Value.Target;
            current = System.IO.Path.Join(link.Value.Final, current[link.Value.Prefix.Length..].TrimStart('\\', '/'));
        }
        return new(full, linkPath, linkTarget, null, false);
    }

    /// <summary>Whether <paramref name="a"/> and <paramref name="b"/> name the same folder (case and a trailing separator aside
    /// on Windows).</summary>
    public static bool SamePath(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        try
        {
            static string Normal(string value) => System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(value.Trim()));
            return string.Equals(Normal(a), Normal(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return false;
        }
    }

    private static (string Prefix, string Target, string? Final)? FirstLink(string full)
    {
        var root = System.IO.Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root)) return null;
        var current = root;
        foreach (var part in full[root.Length..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
        {
            current = System.IO.Path.Join(current, part);
            try
            {
                var info = new DirectoryInfo(current);
                if (info.LinkTarget is { } target)
                {
                    string? final = null;
                    try { final = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName; }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                    if (final is not null && !System.IO.Path.IsPathRooted(final))
                        final = System.IO.Path.GetFullPath(final, System.IO.Path.GetDirectoryName(current)!);
                    return (current, target, final);
                }
                if (!info.Exists) return null;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return null;
            }
        }
        return null;
    }

    private static bool Usable(string real)
    {
        try
        {
            var info = new DirectoryInfo(real);
            return info.Exists && info.LinkTarget is null &&
                (Directory.Exists(System.IO.Path.Combine(real, "blobs")) || Directory.Exists(System.IO.Path.Combine(real, "manifests")));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
