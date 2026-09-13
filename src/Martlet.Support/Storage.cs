using System.Text.Json;
using Martlet.Core.Contracts;

namespace Martlet.Support;

// The same seam used by all production IO; tests inject failures here, not in alternative algorithms.
internal class SupportFileSystem
{
    internal virtual FileStream Open(string path, FileMode mode, FileAccess access, FileShare share) =>
        new(path, mode, access, share, 4096, FileOptions.None);
    internal virtual void Write(Stream stream, ReadOnlySpan<byte> bytes) => stream.Write(bytes);
    internal virtual void Flush(FileStream stream) => stream.Flush(flushToDisk: true);
    internal virtual void Close(Stream stream) => stream.Dispose();
    internal virtual void Move(string source, string destination, bool overwrite) => File.Move(source, destination, overwrite);
    internal virtual void Delete(string path) => File.Delete(path);
    internal virtual void Truncate(FileStream stream, long length) => stream.SetLength(length);
}

internal static class Storage
{
    internal static T Run<T>(Func<T> action)
    {
        try { return action(); }
        catch (UnauthorizedAccessException) { throw new SupportException(SupportFailure.AccessDenied); }
        catch (IOException) { throw new SupportException(SupportFailure.IoFailure); }
    }

    internal static void Run(Action action) => Run(() => { action(); return true; });

    internal static string LocalPath(string path, bool directory)
    {
        try
        {
            Guard.Require(!string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) &&
                !path.StartsWith(@"\\", StringComparison.Ordinal) && !path.StartsWith("//", StringComparison.Ordinal) &&
                !path.Contains('\0'), SupportFailure.InvalidPath);
            if (OperatingSystem.IsWindows())
                Guard.Require(path.Length > 3 && path[1] == ':' && !path[2..].Contains(':'),
                    SupportFailure.InvalidPath);
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            Guard.Require(full != Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full)!), SupportFailure.InvalidPath);
            if (OperatingSystem.IsWindows())
                Guard.Require(new DriveInfo(Path.GetPathRoot(full)!).DriveType != DriveType.Network, SupportFailure.InvalidPath);
            CheckAncestors(directory ? full : Path.GetDirectoryName(full)!);
            CheckFile(full);
            return full;
        }
        catch (ArgumentException) { throw new SupportException(SupportFailure.InvalidPath); }
        catch (NotSupportedException) { throw new SupportException(SupportFailure.InvalidPath); }
    }

    internal static void CheckAncestors(string directory)
    {
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
            CheckFile(current.FullName);
    }

    internal static void CheckFile(string path)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        Guard.Require((attributes & FileAttributes.ReparsePoint) == 0, SupportFailure.InvalidPath);
    }

    internal static byte[] ReadBounded(SupportFileSystem fs, string path, int maximum)
    {
        CheckFile(path);
        using var stream = fs.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Guard.Require(stream.Length <= maximum, SupportFailure.LimitExceeded);
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        Guard.Require(stream.ReadByte() == -1, SupportFailure.CorruptJournal);
        return bytes;
    }

    internal static byte[] Compact<T>(T value, int maximum) where T : IContract
    {
        var bytes = SupportJson.Write(value, maximum);
        using var document = JsonDocument.Parse(bytes);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) document.RootElement.WriteTo(writer);
        stream.WriteByte((byte)'\n');
        Guard.Require(stream.Length <= maximum, SupportFailure.LimitExceeded);
        return stream.ToArray();
    }

    internal static void WriteFile(SupportFileSystem fs, string path, byte[] bytes, FileMode mode, Operation operation)
    {
        operation.Check();
        CheckAncestors(Path.GetDirectoryName(path)!);
        CheckFile(path);
        Guard.Require(!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReadOnly) == 0,
            SupportFailure.AccessDenied);
        var stream = fs.Open(path, mode, FileAccess.Write, FileShare.None);
        try
        {
            operation.Check();
            fs.Write(stream, bytes);
            operation.Check();
            fs.Flush(stream);
            operation.Check();
        }
        finally
        {
            // Retain the close error, but always dispose this concrete FileStream before abandoning the local owner.
            try { fs.Close(stream); }
            finally { stream.Dispose(); }
        }
        operation.Check();
    }
}
