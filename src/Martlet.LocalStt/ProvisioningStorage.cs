using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32.SafeHandles;

namespace Martlet.LocalStt;

public static class LocalSttOfflinePackageInput
{
    public static byte[] ReadBoundedLocalFile(
        string path,
        int maximumBytes)
    {
        if (path is null)
            throw new LocalSttProvisioningException(
                LocalSttProvisioningFailure.InvalidRequest);
        ProvisioningGuard.Require(
            maximumBytes is > 0 and <= 1_048_576,
            LocalSttProvisioningFailure.InvalidRequest);
        try
        {
            ProvisioningHost.RequireSupported();
            var normalized = ProvisioningPath.Normalize(path);
            new PhysicalLocalPathInspector().AssertSafeExisting(
                normalized,
                directory: false);
            using var input = ProvisioningFile.OpenRead(normalized);
            ProvisioningGuard.Require(
                input.Length is > 0 &&
                input.Length <= maximumBytes,
                LocalSttProvisioningFailure.InvalidRequest);
            var bytes = new byte[checked((int)input.Length)];
            input.ReadExactly(bytes);
            return bytes;
        }
        catch (LocalSttProvisioningException)
        {
            throw;
        }
        catch (LocalPathException)
        {
            throw new LocalSttProvisioningException(
                LocalSttProvisioningFailure.SourceUnsafe);
        }
        catch (FileNotFoundException)
        {
            throw new LocalSttProvisioningException(
                LocalSttProvisioningFailure.SourceMissing);
        }
        catch (DirectoryNotFoundException)
        {
            throw new LocalSttProvisioningException(
                LocalSttProvisioningFailure.SourceMissing);
        }
        catch (UnauthorizedAccessException)
        {
            throw new LocalSttProvisioningException(
                LocalSttProvisioningFailure.AccessDenied);
        }
        catch (SecurityException)
        {
            throw new LocalSttProvisioningException(
                LocalSttProvisioningFailure.AccessDenied);
        }
        catch (Exception error) when (
            error is ArgumentException or
                IOException or
                NotSupportedException)
        {
            throw new LocalSttProvisioningException(
                LocalSttProvisioningFailure.StorageFailure);
        }
    }
}

internal static partial class CreateOnlyDirectory
{
    private const int ErrorPathNotFound = 3;
    private const int ErrorAccessDenied = 5;
    private const int ErrorFileExists = 80;
    private const int ErrorAlreadyExists = 183;
    private const int ErrorDiskFull = 112;

    internal static void Create(string path)
    {
        if (!OperatingSystem.IsWindows())
            throw new LocalSttProvisioningException(LocalSttProvisioningFailure.UnsupportedHost);

        if (CreateDirectoryW(path, IntPtr.Zero))
            return;
        var error = Marshal.GetLastWin32Error();
        throw error switch
        {
            ErrorFileExists or ErrorAlreadyExists =>
                new LocalSttProvisioningException(
                LocalSttProvisioningFailure.StagingConflict),
            ErrorAccessDenied => new LocalSttProvisioningException(
                LocalSttProvisioningFailure.AccessDenied),
            ErrorDiskFull => new LocalSttProvisioningException(
                LocalSttProvisioningFailure.InsufficientDisk),
            ErrorPathNotFound => new LocalSttProvisioningException(
                LocalSttProvisioningFailure.StorageFailure),
            _ => new LocalSttProvisioningException(
                LocalSttProvisioningFailure.StorageFailure)
        };
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryW(string path, IntPtr securityAttributes);
}

internal static class ProvisioningHost
{
    internal static void RequireSupported()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new LocalSttProvisioningException(LocalSttProvisioningFailure.UnsupportedHost);
    }

}

internal sealed class ProvisioningTransactionLease : IDisposable
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> Active = new();
    private readonly Mutex mutex;
    private readonly string key;

    internal ProvisioningTransactionLease(string path)
    {
        key = ProvisioningGuard.Fingerprint(path.ToUpperInvariant());
        if (!Active.TryAdd(key, 0))
            throw new LocalSttProvisioningException(LocalSttProvisioningFailure.Busy);
        try { mutex = new Mutex(false, @"Local\MartletLocalStt-" + key); }
        catch { Active.TryRemove(key, out _); throw; }
        bool acquired;
        try { acquired = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired)
        {
            mutex.Dispose();
            Active.TryRemove(key, out _);
            throw new LocalSttProvisioningException(LocalSttProvisioningFailure.Busy);
        }
    }

    public void Dispose()
    {
        mutex.ReleaseMutex();
        mutex.Dispose();
        Active.TryRemove(key, out _);
    }
}
internal static class ProvisioningPath
{
    internal static string Normalize(string path)
    {
        var result = LocalPathRules.NormalizeRoot(path);
        foreach (var segment in result[3..].Split('\\'))
            ValidateSegment(segment);
        return result;
    }

    internal static void ValidateSegment(string segment)
    {
        var stem = segment.Split('.')[0];
        if (segment.Length == 0 || segment.EndsWith('.') || segment.EndsWith(' ') ||
            segment.Any(c => char.IsControl(c) || c is ':' or '/' or '\\' or '"' or '<' or '>' or '|' or '?' or '*') ||
            stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("CLOCK$", StringComparison.OrdinalIgnoreCase) ||
            (stem.Length == 4 &&
             (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
              stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
             stem[3] is >= '1' and <= '9' or '\u00b9' or '\u00b2' or '\u00b3'))
            throw new LocalPathException();
    }
}

internal sealed class ProvisioningFile : FileStream
{
    private readonly WindowsDirectoryLease directory;

    private ProvisioningFile(SafeFileHandle handle, FileAccess access, WindowsDirectoryLease directory)
        : base(handle, access, 65_536, isAsync: false) => this.directory = directory;

    internal static ProvisioningFile OpenRead(string path) => Open(path, create: false);
    internal static ProvisioningFile Create(string path) => Open(path, create: true);

    private static ProvisioningFile Open(string path, bool create)
    {
        var directory = new WindowsDirectoryLease(Path.GetDirectoryName(path)!);
        SafeFileHandle? handle = null;
        try
        {
            handle = File.OpenHandle(path, create ? FileMode.CreateNew : FileMode.Open,
                create ? FileAccess.Write : FileAccess.Read,
                create ? FileShare.None : FileShare.Read,
                create ? FileOptions.WriteThrough : FileOptions.SequentialScan);
            WindowsLocalPath.Validate(handle, path, directory: false);
            return new(handle, create ? FileAccess.Write : FileAccess.Read, directory);
        }
        catch
        {
            handle?.Dispose();
            directory.Dispose();
            throw;
        }
    }

    protected override void Dispose(bool disposing)
    {
        try { base.Dispose(disposing); }
        finally { if (disposing) directory.Dispose(); }
    }
}

internal static class ProvisioningFileIdentity
{
    internal static string Read(FileStream stream) => Read(stream.SafeFileHandle);

    internal static string Read(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info))
            throw new IOException("The package file identity could not be read.");
        return $"{info.Volume:x8}:{info.IndexHigh:x8}{info.IndexLow:x8}";
    }

    internal static string ReadDirectory(string path)
    {
        using var handle = WindowsLocalPath.Open(path, directory: true, delete: false);
        WindowsLocalPath.Validate(handle, path, directory: true);
        return Read(handle);
    }

    internal static void DeleteFile(string path, string expected) => Delete(path, expected, directory: false);
    internal static void DeleteDirectory(string path, string expected) => Delete(path, expected, directory: true);

    private static void Delete(string path, string expected, bool directory)
    {
        using var handle = WindowsLocalPath.Open(path, directory, delete: true);
        WindowsLocalPath.Validate(handle, path, directory);
        ProvisioningGuard.Require(Read(handle) == expected, LocalSttProvisioningFailure.StagingConflict);
        WindowsLocalPath.MarkForDeletion(handle);
    }

    internal static void MoveDirectory(string source, string destination, string expectedIdentity)
    {
        using var handle = WindowsLocalPath.Open(source, directory: true, delete: true);
        WindowsLocalPath.Validate(handle, source, directory: true);
        ProvisioningGuard.Require(Read(handle) == expectedIdentity,
            LocalSttProvisioningFailure.StagingConflict);
        var name = System.Text.Encoding.Unicode.GetBytes(destination);
        // FILE_RENAME_INFO has an aligned HANDLE followed by length and UTF-16 name.
        var buffer = new byte[20 + name.Length];
        BitConverter.GetBytes(name.Length).CopyTo(buffer, 16);
        name.CopyTo(buffer, 20);
        if (!SetFileInformationByHandle(handle, 3, buffer, (uint)buffer.Length))
            throw new IOException("The create-only package rename failed.",
                new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        internal uint Attributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int kind, byte[] information, uint length);
}
