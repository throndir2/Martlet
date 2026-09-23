using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Martlet.LocalStt;

internal sealed class WindowsDirectoryLease : IDisposable
{
    private readonly List<SafeFileHandle> handles = [];

    internal WindowsDirectoryLease(string path, bool ownsDirectory = false)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException();
        try
        {
            var directories = new Stack<string>();
            for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
                directories.Push(current.FullName);
            while (directories.TryPop(out var directory))
            {
                var handle = WindowsLocalPath.Open(directory, directory: true,
                    delete: ownsDirectory && directories.Count == 0);
                handles.Add(handle);
                WindowsLocalPath.Validate(handle, directory, directory: true);
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal void DeleteOwnedDirectory()
    {
        WindowsLocalPath.MarkForDeletion(handles[^1]);
        Dispose();
    }

    public void Dispose()
    {
        for (var index = handles.Count - 1; index >= 0; index--)
            handles[index].Dispose();
        handles.Clear();
    }
}

internal static class WindowsLocalPath
{
    internal static SafeFileHandle Open(string path, bool directory, bool delete)
    {
        // LIST_DIRECTORY participates in sharing checks; attributes-only directory handles do not.
        var handle = CreateFileW(path, 0x80u | (directory ? 1u : 0) | (delete ? 0x10000u : 0),
            3, IntPtr.Zero, 3, 0x00200000u | (directory ? 0x02000000u : 0), IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException("An owned local path could not be locked.", new Win32Exception(error));
        }
        return handle;
    }

    internal static void Validate(SafeFileHandle handle, string expected, bool directory)
    {
        if (!GetFileInformationByHandle(handle, out var information))
            throw new IOException("An owned local handle could not be inspected.");
        var attributes = (FileAttributes)information.Attributes;
        if (PhysicalLocalPathInspector.IsUnsafe(attributes) ||
            attributes.HasFlag(FileAttributes.Directory) != directory ||
            (!directory && information.Links != 1))
            throw new LocalPathException();
        var path = new StringBuilder(32768);
        var length = GetFinalPathNameByHandleW(handle, path, (uint)path.Capacity, 0);
        if (length == 0 || length >= path.Capacity)
            throw new LocalPathException();
        var actual = path.ToString();
        if (!actual.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            !string.Equals(Path.TrimEndingDirectorySeparator(actual[4..]),
                Path.TrimEndingDirectorySeparator(expected), StringComparison.OrdinalIgnoreCase))
            throw new LocalPathException();
    }

    internal static void CreateOwnedDirectory(string path)
    {
        if (!CreateDirectoryW(path, IntPtr.Zero))
            throw new IOException("The private operation directory could not be created.",
                new Win32Exception(Marshal.GetLastWin32Error()));
    }

    internal static void DeleteOwnedFile(string path)
    {
        using var handle = Open(path, directory: false, delete: true);
        Validate(handle, path, directory: false);
        MarkForDeletion(handle);
    }

    internal static void MarkForDeletion(SafeFileHandle handle)
    {
        var delete = new FileDispositionInfo { Delete = true };
        if (!SetFileInformationByHandle(handle, 4, ref delete, (uint)Marshal.SizeOf<FileDispositionInfo>()))
            throw new IOException("An owned local path could not be removed.",
                new Win32Exception(Marshal.GetLastWin32Error()));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        internal uint Attributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInfo { [MarshalAs(UnmanagedType.Bool)] internal bool Delete; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryW(string path, IntPtr security);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass,
        ref FileDispositionInfo information, uint length);
}
