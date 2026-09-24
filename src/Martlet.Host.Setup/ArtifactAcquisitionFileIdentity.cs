using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Martlet.Host.Setup;

internal readonly record struct ArtifactAcquisitionFileMetadata(string Identity, long Bytes);

internal static partial class ArtifactAcquisitionFileIdentity
{
    internal static FileStream OpenRead(string path)
    {
        if (!OperatingSystem.IsLinux())
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        // O_NOFOLLOW and O_NONBLOCK prevent a visible link or FIFO from being followed
        // or blocking before handle metadata can reject it. The directory is private.
        var descriptor = Open(path, 0x20000 | 0x800 | 0x80000);
        if (descriptor < 0)
            throw NativeFailure();
        var handle = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        try
        {
            _ = Read(handle);
            return new FileStream(handle, FileAccess.Read);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal static ArtifactAcquisitionFileMetadata Read(SafeFileHandle handle)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!GetFileInformationByHandle(handle, out var information) ||
                !GetFileInformationByHandleEx(handle, 18, out var id, 24))
                throw NativeFailure();
            Require(information.NumberOfLinks == 1 &&
                (information.FileAttributes & (uint)(FileAttributes.Directory |
                    FileAttributes.ReparsePoint | FileAttributes.Device)) == 0);
            return new(
                $"win:{id.VolumeSerialNumber:x16}:{id.Low:x16}{id.High:x16}",
                checked((long)(((ulong)information.FileSizeHigh << 32) |
                    information.FileSizeLow)));
        }
        if (OperatingSystem.IsLinux() && Environment.Is64BitProcess)
        {
            if (Statx(handle, "", 0x1000 | 0x100, 0x7ff | 0x800, out var value) != 0)
                throw NativeFailure();
            // Birth time prevents adopting a recycled inode after an interrupted
            // operation. Filesystems lacking this metadata fail closed.
            Require((value.Mask & 0xb07) == 0xb07 &&
                (value.Mode & 0xf000) == 0x8000 && value.LinkCount == 1);
            RequireLocalLinuxFileSystem(handle);
            return new(
                $"linux:{value.DeviceMajor:x8}:{value.DeviceMinor:x8}:{value.Inode:x16}:" +
                $"{value.BirthSeconds:x16}:{value.BirthNanoseconds:x8}",
                checked((long)value.Size));
        }
        throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.DestinationInvalid);
    }

    internal static void RequireLocalLinuxDirectory(string path)
    {
        if (!OperatingSystem.IsLinux())
            return;
        Require(Environment.Is64BitProcess);
        var descriptor = Open(path, 0x10000 | 0x20000 | 0x80000);
        if (descriptor < 0)
            throw NativeFailure();
        using var handle = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        RequireLocalLinuxFileSystem(handle);
    }

    private static void RequireLocalLinuxFileSystem(SafeFileHandle handle)
    {
        if (FStatFs(handle, out var value) != 0)
            throw NativeFailure();
        // Fail closed for network, FUSE and unknown filesystems. This is not a
        // claim to establish trust in mounts or defend hostile mount replacement.
        Require(value.Type is 0xef53 or 0x58465342 or 0x9123683e or
            0x01021994 or 0x794c7630 or 0x2fc12fc1);
    }

    private static void Require(bool condition)
    {
        if (!condition)
            throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.DestinationInvalid);
    }

    private static ArtifactAcquisitionException NativeFailure() =>
        new(Marshal.GetLastPInvokeError() is 5 or 13
            ? ArtifactAcquisitionFailure.AccessDenied
            : ArtifactAcquisitionFailure.StorageFailed);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle handle, out WindowsFileInformation information);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle handle, int informationClass, out WindowsFileId information, uint size);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsFileId
    {
        public ulong VolumeSerialNumber;
        public ulong Low;
        public ulong High;
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(SafeFileHandle descriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags, uint mask, out LinuxStatx value);

    // statx has a fixed-width, architecture-independent Linux ABI.
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxStatx
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(16)] public uint LinkCount;
        [FieldOffset(28)] public ushort Mode;
        [FieldOffset(32)] public ulong Inode;
        [FieldOffset(40)] public ulong Size;
        [FieldOffset(80)] public long BirthSeconds;
        [FieldOffset(88)] public uint BirthNanoseconds;
        [FieldOffset(136)] public uint DeviceMajor;
        [FieldOffset(140)] public uint DeviceMinor;
    }

    [DllImport("libc", EntryPoint = "fstatfs", SetLastError = true)]
    private static extern int FStatFs(SafeFileHandle descriptor, out LinuxStatFs value);

    [StructLayout(LayoutKind.Sequential, Size = 256)]
    private struct LinuxStatFs
    {
        public long Type;
    }
}
