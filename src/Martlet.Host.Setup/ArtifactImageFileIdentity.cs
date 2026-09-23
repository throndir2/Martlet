using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace Martlet.Host.Setup;

internal static partial class ArtifactAcquisitionFileIdentity
{
    internal static SafeFileHandle OpenDirectory(string path)
    {
        SafeFileHandle handle;
        if (OperatingSystem.IsWindows())
            handle = CreateDirectoryHandle(path, 0x0001 | 0x0080, 1 | 2, IntPtr.Zero, 3,
                0x02000000 | 0x00200000, IntPtr.Zero);
        else
            handle = new SafeFileHandle((IntPtr)Open(path, 0x10000 | 0x20000 | 0x80000), ownsHandle: true);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw NativeFailure();
        }
        try { _ = ReadDirectory(handle); return handle; }
        catch { handle.Dispose(); throw; }
    }

    internal static string ReadDirectory(SafeFileHandle handle)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!GetFileInformationByHandle(handle, out var information) ||
                !GetFileInformationByHandleEx(handle, 18, out var id, 24))
                throw NativeFailure();
            Require((information.FileAttributes & (uint)(FileAttributes.Directory |
                FileAttributes.ReparsePoint | FileAttributes.Device)) == (uint)FileAttributes.Directory);
            return $"win:{id.VolumeSerialNumber:x16}:{id.Low:x16}{id.High:x16}";
        }
        if (OperatingSystem.IsLinux() && Environment.Is64BitProcess)
        {
            if (Statx(handle, "", 0x1000 | 0x100, 0x7ff | 0x800, out var value) != 0)
                throw NativeFailure();
            Require((value.Mask & 0xb07) == 0xb07 && (value.Mode & 0xf000) == 0x4000);
            RequireLocalLinuxFileSystem(handle);
            return $"linux:{value.DeviceMajor:x8}:{value.DeviceMinor:x8}:{value.Inode:x16}:" +
                $"{value.BirthSeconds:x16}:{value.BirthNanoseconds:x8}";
        }
        throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.DestinationInvalid);
    }

    internal static string DirectoryIdentity(string path)
    {
        using var handle = OpenDirectory(path);
        return ReadDirectory(handle);
    }

    internal static void CreateImageDirectoryExclusive(string path)
    {
        var created = OperatingSystem.IsWindows()
            ? CreateImageDirectory(path, IntPtr.Zero)
            : Mkdir(path, 0x1c0) == 0;
        if (!created) throw NativeFailure();
    }

    internal static FileStream OpenImageLease(string path, bool create)
    {
        if (OperatingSystem.IsWindows())
            return new FileStream(path, create ? FileMode.CreateNew : FileMode.Open,
                FileAccess.ReadWrite, FileShare.ReadWrite);
        var descriptor = OpenWithMode(path, 2 | 0x20000 | 0x80000 | (create ? 0x40 | 0x80 : 0), 0x180);
        if (descriptor < 0) throw NativeFailure();
        var handle = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        try { _ = Read(handle); return new FileStream(handle, FileAccess.ReadWrite); }
        catch { handle.Dispose(); throw; }
    }

    internal static bool TryLockImageLease(SafeFileHandle handle)
    {
        bool acquired;
        if (OperatingSystem.IsWindows())
        {
            var overlapped = new ImageLockOverlapped();
            acquired = LockFileEx(handle, 3, 0, 1, 0, ref overlapped);
        }
        else acquired = Flock(handle, 2 | 4) == 0;
        if (acquired) return true;
        var error = Marshal.GetLastPInvokeError();
        if (OperatingSystem.IsWindows() ? error is 32 or 33 : error == 11) return false;
        throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.ImageLeaseUnavailable);
    }

    internal static void UnlockImageLease(SafeFileHandle handle)
    {
        bool released;
        if (OperatingSystem.IsWindows())
        {
            var overlapped = new ImageLockOverlapped();
            released = UnlockFileEx(handle, 0, 1, 0, ref overlapped);
        }
        else released = Flock(handle, 8) == 0;
        if (!released)
            throw new ArtifactAcquisitionException(ArtifactAcquisitionFailure.ImageLeaseUnavailable);
    }

    internal static async ValueTask<string> HashOwnedFileAsync(string path,
        ArtifactAcquisitionFileMetadata expected, Func<CancellationToken, ValueTask> checkCurrent,
        ArtifactAcquisitionFailure mismatch, CancellationToken cancellationToken, Action<int>? bytesRead = null)
    {
        await checkCurrent(cancellationToken).ConfigureAwait(false);
        await using var stream = OpenRead(path);
        if (Read(stream.SafeFileHandle) != expected)
            throw new ArtifactAcquisitionException(mismatch);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65_536];
        long total = 0;
        while (total < expected.Bytes)
        {
            await checkCurrent(cancellationToken).ConfigureAwait(false);
            if (Read(stream.SafeFileHandle) != expected)
                throw new ArtifactAcquisitionException(mismatch);
            var count = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, expected.Bytes - total)),
                cancellationToken).ConfigureAwait(false);
            if (count == 0) throw new ArtifactAcquisitionException(mismatch);
            bytesRead?.Invoke(count);
            total = checked(total + count);
            if (total > expected.Bytes) throw new ArtifactAcquisitionException(mismatch);
            hash.AppendData(buffer, 0, count);
        }
        await checkCurrent(cancellationToken).ConfigureAwait(false);
        using var current = OpenRead(path);
        if (total != expected.Bytes || Read(stream.SafeFileHandle) != expected ||
            Read(current.SafeFileHandle) != expected)
            throw new ArtifactAcquisitionException(mismatch);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateDirectoryHandle(string path, uint access, uint share,
        IntPtr securityAttributes, uint creationDisposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LockFileEx(SafeFileHandle handle, uint flags, uint reserved,
        uint lengthLow, uint lengthHigh, ref ImageLockOverlapped overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnlockFileEx(SafeFileHandle handle, uint reserved,
        uint lengthLow, uint lengthHigh, ref ImageLockOverlapped overlapped);

    [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static extern int Flock(SafeFileHandle handle, int operation);

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenWithMode([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, int mode);

    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateImageDirectory(string path, IntPtr securityAttributes);

    [DllImport("libc", EntryPoint = "mkdir", SetLastError = true)]
    private static extern int Mkdir([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int mode);

    [StructLayout(LayoutKind.Sequential)]
    private struct ImageLockOverlapped
    {
        public IntPtr Internal;
        public IntPtr InternalHigh;
        public uint Offset;
        public uint OffsetHigh;
        public IntPtr Event;
    }
}
