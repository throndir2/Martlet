namespace Martlet.Core.Installation;

/// <summary>device-role.txt: whether this computer is a companion PC ("Companion") or a host PC ("Host"), in the PC folder
/// (<see cref="PcFolder"/>) and as each Windows user's copy of the last choice. It says what this computer is for, so it stays on
/// this computer (docs/CLUSTER.md, "What stays with each computer").</summary>
public static class DeviceRoleFile
{
    public const string FileName = "device-role.txt";
    public const string Companion = "Companion", Host = "Host";

    /// <summary>The file's text in <paramref name="directory"/> (at most 16 characters, not trimmed), read so that another Windows
    /// user's Martlet can replace the file meanwhile. Throws <see cref="FileNotFoundException"/> or
    /// <see cref="DirectoryNotFoundException"/> when there is none, and <see cref="IOException"/> or
    /// <see cref="UnauthorizedAccessException"/> when it can't be read now.</summary>
    public static string ReadText(string directory)
    {
        using var stream = new FileStream(Path.Combine(directory, FileName), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var buffer = new char[16];
        var count = reader.ReadBlock(buffer, 0, buffer.Length);
        return new string(buffer, 0, count);
    }

    /// <summary>"host", "companion" (any other text), null without a file, or "unreadable".</summary>
    public static string? Describe(string directory)
    {
        try { return ReadText(directory).Trim() == Host ? "host" : "companion"; }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return "unreadable"; }
    }
}
