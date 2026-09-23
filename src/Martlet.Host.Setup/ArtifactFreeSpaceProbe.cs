using System.Runtime.InteropServices;

namespace Martlet.Host.Setup;

public interface IArtifactFreeSpaceProbe
{
    long GetAvailableBytes(string absoluteDirectory);
}

public sealed class PlatformArtifactFreeSpaceProbe : IArtifactFreeSpaceProbe
{
    public long GetAvailableBytes(string absoluteDirectory)
    {
        AcquisitionGuard.Text(
            absoluteDirectory,
            1024,
            ArtifactAcquisitionFailure.DestinationInvalid);
        if (!Path.IsPathFullyQualified(absoluteDirectory))
            throw new ArtifactAcquisitionException(
                ArtifactAcquisitionFailure.DestinationInvalid);
        LocalArtifactAcquisitionStorage.ValidateDirectory(absoluteDirectory);
        if (OperatingSystem.IsWindows())
            return GetWindowsAvailableBytes(absoluteDirectory);
        if (OperatingSystem.IsLinux())
            return GetLinuxAvailableBytes(absoluteDirectory);
        throw new ArtifactAcquisitionException(
            ArtifactAcquisitionFailure.DestinationInvalid);
    }

    private static long GetWindowsAvailableBytes(string directory)
    {
        if (!GetDiskFreeSpaceEx(
            directory,
            out var available,
            out _,
            out _))
            throw NativeFailure(Marshal.GetLastPInvokeError(), 5);
        return available <= long.MaxValue
            ? (long)available
            : long.MaxValue;
    }

    private static long GetLinuxAvailableBytes(string directory)
    {
        if (!Environment.Is64BitProcess)
            throw new ArtifactAcquisitionException(
                ArtifactAcquisitionFailure.DestinationInvalid);
        if (StatVfs(directory, out var value) != 0)
            throw NativeFailure(Marshal.GetLastPInvokeError(), 13);
        var blockSize = value.FragmentSize == 0
            ? value.BlockSize
            : value.FragmentSize;
        try
        {
            var available = checked(value.BlocksAvailable * blockSize);
            return available <= long.MaxValue
                ? (long)available
                : long.MaxValue;
        }
        catch (OverflowException error)
        {
            throw new ArtifactAcquisitionException(
                ArtifactAcquisitionFailure.StorageFailed,
                error);
        }
    }

    private static ArtifactAcquisitionException NativeFailure(
        int error,
        int accessDeniedCode) =>
        new(
            error == accessDeniedCode
                ? ArtifactAcquisitionFailure.AccessDenied
                : ArtifactAcquisitionFailure.StorageFailed);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetDiskFreeSpaceExW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(
        string directoryName,
        out ulong freeBytesAvailable,
        out ulong totalNumberOfBytes,
        out ulong totalNumberOfFreeBytes);

    [DllImport("libc", EntryPoint = "statvfs", SetLastError = true)]
    private static extern int StatVfs(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        out LinuxStatVfs value);

    // The first five unsigned-long fields are stable on 64-bit Linux
    // target. Extra native fields fit inside the deliberately oversized buffer.
    [StructLayout(LayoutKind.Sequential, Size = 256)]
    private struct LinuxStatVfs
    {
        public ulong BlockSize;
        public ulong FragmentSize;
        public ulong Blocks;
        public ulong BlocksFree;
        public ulong BlocksAvailable;
    }
}
