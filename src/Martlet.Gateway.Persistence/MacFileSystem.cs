using System.Runtime.InteropServices;
using System.Text;
using static Martlet.Gateway.Persistence.PersistenceFailure;

namespace Martlet.Gateway.Persistence;

/// <summary>The native custody file system of this computer: Linux (ext4) or macOS (APFS). Both keep the same
/// <see cref="ILinuxFileSystem"/> contract, so the service-permissions backend runs unchanged on either.</summary>
internal static class PosixFileSystem
{
    internal static bool Supported => OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    internal static ILinuxFileSystem Create() => OperatingSystem.IsMacOS() ? new MacFileSystem() : new LinuxFileSystem();
}

/// <summary>macOS custody with the Linux backend's safety contract. Every open is one name relative to a held directory
/// descriptor ("." is the directory itself; never a path or ".."), refuses symbolic links anywhere (O_NOFOLLOW_ANY) and,
/// where the caller forbids mount crossings, checks the opened file is on the directory's own volume. Identities come
/// from fstat; ACLs that grant access are refused (macOS's own deny-only entries, such as "everyone deny delete" on the
/// home folder, grant nothing and are allowed); storage must be a local read-write APFS volume; flushes use F_FULLFSYNC
/// (through the drive's cache); a rename that must not replace uses renameatx_np with RENAME_EXCL; the boot identity is
/// kern.bootsessionuuid.</summary>
internal sealed class MacFileSystem : ILinuxFileSystem
{
    private const int ONonBlock = 0x4, ONoFollow = 0x100, OCreate = 0x200, OExclusive = 0x800,
        ODirectory = 0x100000, OCloseOnExec = 0x1000000, ONoFollowAny = 0x20000000;
    private const int AtSymlinkNoFollow = 0x20, FullFileSync = 51, LockExclusiveNonBlocking = 2 | 4;
    private const uint RenameExclusive = 0x4;
    private const int AclTypeExtended = 0x100, AclFirstEntry = 0, AclNextEntry = -1, AclExtendedAllow = 1;
    private const uint MountReadOnly = 0x1, MountLocal = 0x1000;
    private const int Interrupted = 4, NoEntry = 2, WouldBlock = 35;
    private const string LibSystem = "/usr/lib/libSystem.B.dylib";
    private static readonly bool Intel = RuntimeInformation.ProcessArchitecture == Architecture.X64;

    internal MacFileSystem()
    {
        if (!OperatingSystem.IsMacOS() || RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
            throw Error(GatewayPersistenceFailure.UnsupportedPlatform);
        UserId = GetEffectiveUser();
        if (UserId == 0 || UserId != GetUser() || GetEffectiveGroup() != GetGroup())
            throw Error(GatewayPersistenceFailure.InsecureStorage);
    }

    public uint UserId { get; }
    public uint GroupId => GetGroup();

    /// <summary>The macOS open(2) flags for the Linux flags the custody code passes; anything else is refused.</summary>
    internal static int TranslateFlags(int linux)
    {
        const int known = LinuxFileSystem.Directory | LinuxFileSystem.NoFollow | LinuxFileSystem.CloseOnExec |
            LinuxFileSystem.NonBlocking | LinuxFileSystem.ReadWrite | LinuxFileSystem.Create | LinuxFileSystem.Exclusive | 1;
        if ((linux & ~known) != 0 || (linux & 3) == 3) throw Error(GatewayPersistenceFailure.UnsupportedPlatform);
        var flags = linux & 3;
        if ((linux & LinuxFileSystem.Directory) != 0) flags |= ODirectory;
        if ((linux & LinuxFileSystem.NoFollow) != 0) flags |= ONoFollow;
        if ((linux & LinuxFileSystem.CloseOnExec) != 0) flags |= OCloseOnExec;
        if ((linux & LinuxFileSystem.NonBlocking) != 0) flags |= ONonBlock;
        if ((linux & LinuxFileSystem.Create) != 0) flags |= OCreate;
        if ((linux & LinuxFileSystem.Exclusive) != 0) flags |= OExclusive;
        return flags;
    }

    /// <summary>One entry name inside a held directory: "." or a name without a separator; never "..".</summary>
    internal static bool SingleName(string name) =>
        name is { Length: > 0 and <= 255 } && name != ".." && !name.Contains('/') && !name.Contains('\0');

    public int OpenRoot() => Check(NativeOpen("/", ODirectory | ONoFollow | OCloseOnExec));

    public int OpenAt(int directory, string name, int flags, uint mode, ulong resolve)
    {
        const ulong knownResolve = LinuxFileSystem.NoLinks | LinuxFileSystem.Beneath | LinuxFileSystem.NoMounts;
        if ((resolve & ~knownResolve) != 0) throw Error(GatewayPersistenceFailure.UnsupportedPlatform);
        if (!SingleName(name)) throw Error(GatewayPersistenceFailure.InsecureStorage);
        var native = TranslateFlags(flags);
        if ((resolve & LinuxFileSystem.NoLinks) != 0) native |= ONoFollow | ONoFollowAny;
        int result;
        do
        {
            // openat is variadic: Apple's arm64 ABI passes the mode on the stack, so it goes after eight register slots.
            result = Intel ? NativeOpenAtIntel(directory, name, native, mode)
                : NativeOpenAtArm(directory, name, native, 0, 0, 0, 0, 0, mode);
        } while (result < 0 && Marshal.GetLastPInvokeError() == Interrupted);
        var descriptor = Check(result);
        try
        {
            if ((native & OCreate) != 0) Check(NativeChangeMode(descriptor, (ushort)mode));
            if ((resolve & LinuxFileSystem.NoMounts) != 0 && !SameVolume(Stat(descriptor), Stat(directory)))
                throw Error(GatewayPersistenceFailure.InsecureStorage);
            return descriptor;
        }
        catch
        {
            Close(descriptor);
            throw;
        }
    }

    private static bool SameVolume(LinuxFileIdentity a, LinuxFileIdentity b) =>
        a.Mount == b.Mount && a.DeviceMajor == b.DeviceMajor && a.DeviceMinor == b.DeviceMinor;

    public void Close(int descriptor) => _ = NativeClose(descriptor);

    public LinuxFileIdentity Stat(int descriptor)
    {
        var buffer = new byte[StatBytes];
        Check(Intel ? NativeStatIntel(descriptor, buffer) : NativeStatArm(descriptor, buffer));
        return Identity(buffer);
    }

    public LinuxFileIdentity? StatAt(int directory, string name)
    {
        if (!SingleName(name)) throw Error(GatewayPersistenceFailure.InsecureStorage);
        var buffer = new byte[StatBytes];
        var status = Intel ? NativeStatAtIntel(directory, name, buffer, AtSymlinkNoFollow)
            : NativeStatAtArm(directory, name, buffer, AtSymlinkNoFollow);
        if (status < 0 && Marshal.GetLastPInvokeError() == NoEntry) return null;
        Check(status);
        return Identity(buffer);
    }

    // struct stat with 64-bit inodes (arm64, and the $INODE64 entry points on x86_64).
    private const int StatBytes = 144;

    internal static LinuxFileIdentity Identity(ReadOnlySpan<byte> stat)
    {
        var device = BitConverter.ToUInt32(stat[..4]);
        var mode = BitConverter.ToUInt16(stat[4..6]);
        var links = BitConverter.ToUInt16(stat[6..8]);
        var inode = BitConverter.ToUInt64(stat[8..16]);
        var user = BitConverter.ToUInt32(stat[16..20]);
        var size = BitConverter.ToInt64(stat[96..104]);
        if (size < 0) throw Error(GatewayPersistenceFailure.UnsupportedPlatform);
        return new(inode, device, (device >> 24) & 0xff, device & 0xffffff, user, mode, links, size);
    }

    public bool HasAcl(int descriptor, bool directory)
    {
        var acl = GetAcl(descriptor, AclTypeExtended);
        if (acl == IntPtr.Zero)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == NoEntry) return false;
            throw NativeFailure(error);
        }
        try
        {
            var id = AclFirstEntry;
            while (GetAclEntry(acl, id, out var entry) == 0)
            {
                id = AclNextEntry;
                if (GetAclTag(entry, out var tag) != 0 || tag == AclExtendedAllow) return true;
            }
            return false;
        }
        finally { _ = FreeAcl(acl); }
    }

    // struct statfs with 64-bit inodes: f_flags at 64, f_fstypename[16] at 72; 2168 bytes in all.
    private const int StatFsBytes = 2168;

    public void VerifyFileSystem(int descriptor)
    {
        var buffer = new byte[StatFsBytes];
        Check(Intel ? NativeFileSystemIntel(descriptor, buffer) : NativeFileSystemArm(descriptor, buffer));
        if (!AdmittedVolume(buffer)) throw Error(GatewayPersistenceFailure.UnsupportedPlatform);
    }

    /// <summary>A local, read-write APFS volume (not a network, FUSE, FAT/exFAT or HFS+ volume).</summary>
    internal static bool AdmittedVolume(ReadOnlySpan<byte> statfs)
    {
        var flags = BitConverter.ToUInt32(statfs[64..68]);
        var type = statfs.Slice(72, 16);
        var end = type.IndexOf((byte)0);
        return end > 0 && Encoding.ASCII.GetString(type[..end]) == "apfs" &&
            (flags & MountLocal) != 0 && (flags & MountReadOnly) == 0;
    }

    public void MakeDirectory(int parent, string name, uint mode)
    {
        if (!SingleName(name) || name == ".") throw Error(GatewayPersistenceFailure.InsecureStorage);
        Check(NativeMakeDirectory(parent, name, (ushort)mode));
    }

    public void Lock(int descriptor)
    {
        var result = NativeLock(descriptor, LockExclusiveNonBlocking);
        if (result < 0 && Marshal.GetLastPInvokeError() == WouldBlock)
            throw Error(GatewayPersistenceFailure.StoreBusy);
        Check(result);
    }

    public string[] Enumerate(int directory)
    {
        var duplicate = OpenAt(directory, ".", LinuxFileSystem.Directory | LinuxFileSystem.CloseOnExec | LinuxFileSystem.NoFollow,
            0, LinuxFileSystem.NoLinks | LinuxFileSystem.Beneath | LinuxFileSystem.NoMounts);
        var stream = Intel ? OpenDirectoryStreamIntel(duplicate) : OpenDirectoryStreamArm(duplicate);
        if (stream == IntPtr.Zero)
        {
            var error = Marshal.GetLastPInvokeError();
            Close(duplicate);
            throw NativeFailure(error);
        }
        try
        {
            var result = new List<string>();
            while (true)
            {
                // readdir uses errno to distinguish end-of-directory from error.
                Marshal.WriteInt32(ErrnoLocation(), 0);
                var entry = Intel ? ReadDirectoryIntel(stream) : ReadDirectoryArm(stream);
                if (entry == IntPtr.Zero)
                {
                    var error = Marshal.ReadInt32(ErrnoLocation());
                    if (error != 0) throw NativeFailure(error);
                    return result.ToArray();
                }
                // struct dirent with 64-bit inodes: d_namlen at 18, d_name at 21.
                var length = (ushort)Marshal.ReadInt16(entry, 18);
                if (length is < 1 or > 255) throw Error(GatewayPersistenceFailure.InsecureStorage);
                var name = Marshal.PtrToStringUTF8(entry + 21, length);
                if (name.Contains('\0')) throw Error(GatewayPersistenceFailure.InsecureStorage);
                if (name is "." or "..") continue;
                result.Add(name);
                if (result.Count > 5) throw Error(GatewayPersistenceFailure.RecoveryRequired);
            }
        }
        finally { _ = CloseDirectoryStream(stream); }
    }

    public int Read(int descriptor, byte[] bytes, int offset, int count) => Transfer(descriptor, bytes, offset, count, false);
    public int Write(int descriptor, byte[] bytes, int offset, int count) => Transfer(descriptor, bytes, offset, count, true);

    private static int Transfer(int descriptor, byte[] bytes, int offset, int count, bool write)
    {
        var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            long result;
            do
            {
                var address = pinned.AddrOfPinnedObject() + offset;
                result = write ? NativeWrite(descriptor, address, (nuint)count) : NativeRead(descriptor, address, (nuint)count);
            } while (result < 0 && Marshal.GetLastPInvokeError() == Interrupted);
            return checked((int)Check(result));
        }
        finally { pinned.Free(); }
    }

    public void Flush(int descriptor)
    {
        // fsync on macOS leaves the data in the drive's cache; F_FULLFSYNC asks the drive to write it out.
        int result;
        do { result = NativeControl(descriptor, FullFileSync); }
        while (result < 0 && Marshal.GetLastPInvokeError() == Interrupted);
        Check(result);
    }

    public void Rename(int directory, string source, string target, bool replace)
    {
        if (!SingleName(source) || !SingleName(target) || source == "." || target == ".")
            throw Error(GatewayPersistenceFailure.InsecureStorage);
        Check(NativeRename(directory, source, directory, target, replace ? 0u : RenameExclusive));
    }

    public void Unlink(int directory, string name)
    {
        if (!SingleName(name) || name == ".") throw Error(GatewayPersistenceFailure.InsecureStorage);
        Check(NativeUnlink(directory, name, 0));
    }

    public Guid BootIdentity()
    {
        var buffer = new byte[64];
        var length = (nuint)buffer.Length;
        if (SystemControl("kern.bootsessionuuid", buffer, ref length, IntPtr.Zero, 0) != 0 || length is < 37 or > 64)
            throw Error(GatewayPersistenceFailure.ClockUnavailable);
        var text = Encoding.ASCII.GetString(buffer, 0, (int)length).TrimEnd('\0');
        if (!Guid.TryParseExact(text, "D", out var id) || id == Guid.Empty)
            throw Error(GatewayPersistenceFailure.ClockUnavailable);
        return id;
    }

    private static int Check(int value) => (int)Check((long)value);
    private static long Check(long value)
    {
        if (value < 0) throw NativeFailure(Marshal.GetLastPInvokeError());
        return value;
    }

    /// <summary>macOS errno values mapped to the same failures as the Linux backend.</summary>
    internal static GatewayPersistenceException NativeFailure(int error) => Error(error switch
    {
        2 => GatewayPersistenceFailure.StoreMissing,
        1 or 13 or 18 or 20 or 62 => GatewayPersistenceFailure.InsecureStorage,
        17 => GatewayPersistenceFailure.RecoveryRequired,
        22 or 45 or 78 or 102 => GatewayPersistenceFailure.UnsupportedPlatform,
        _ => GatewayPersistenceFailure.StorageFailed
    });

    [DllImport(LibSystem, EntryPoint = "geteuid")] private static extern uint GetEffectiveUser();
    [DllImport(LibSystem, EntryPoint = "getuid")] private static extern uint GetUser();
    [DllImport(LibSystem, EntryPoint = "getegid")] private static extern uint GetEffectiveGroup();
    [DllImport(LibSystem, EntryPoint = "getgid")] private static extern uint GetGroup();
    [DllImport(LibSystem, EntryPoint = "open", SetLastError = true)] private static extern int NativeOpen(string path, int flags);
    [DllImport(LibSystem, EntryPoint = "openat", SetLastError = true)]
    private static extern int NativeOpenAtIntel(int directory, string name, int flags, uint mode);
    [DllImport(LibSystem, EntryPoint = "openat", SetLastError = true)]
    private static extern int NativeOpenAtArm(int directory, string name, int flags,
        nint unused3, nint unused4, nint unused5, nint unused6, nint unused7, nuint mode);
    [DllImport(LibSystem, EntryPoint = "fchmod", SetLastError = true)] private static extern int NativeChangeMode(int descriptor, ushort mode);
    [DllImport(LibSystem, EntryPoint = "fstat$INODE64", SetLastError = true)] private static extern int NativeStatIntel(int descriptor, byte[] result);
    [DllImport(LibSystem, EntryPoint = "fstat", SetLastError = true)] private static extern int NativeStatArm(int descriptor, byte[] result);
    [DllImport(LibSystem, EntryPoint = "fstatat$INODE64", SetLastError = true)]
    private static extern int NativeStatAtIntel(int directory, string name, byte[] result, int flags);
    [DllImport(LibSystem, EntryPoint = "fstatat", SetLastError = true)]
    private static extern int NativeStatAtArm(int directory, string name, byte[] result, int flags);
    [DllImport(LibSystem, EntryPoint = "fstatfs$INODE64", SetLastError = true)] private static extern int NativeFileSystemIntel(int descriptor, byte[] result);
    [DllImport(LibSystem, EntryPoint = "fstatfs", SetLastError = true)] private static extern int NativeFileSystemArm(int descriptor, byte[] result);
    [DllImport(LibSystem, EntryPoint = "acl_get_fd_np", SetLastError = true)] private static extern IntPtr GetAcl(int descriptor, int type);
    [DllImport(LibSystem, EntryPoint = "acl_get_entry", SetLastError = true)] private static extern int GetAclEntry(IntPtr acl, int id, out IntPtr entry);
    [DllImport(LibSystem, EntryPoint = "acl_get_tag_type", SetLastError = true)] private static extern int GetAclTag(IntPtr entry, out int tag);
    [DllImport(LibSystem, EntryPoint = "acl_free", SetLastError = true)] private static extern int FreeAcl(IntPtr acl);
    [DllImport(LibSystem, EntryPoint = "mkdirat", SetLastError = true)] private static extern int NativeMakeDirectory(int parent, string name, ushort mode);
    [DllImport(LibSystem, EntryPoint = "flock", SetLastError = true)] private static extern int NativeLock(int descriptor, int operation);
    [DllImport(LibSystem, EntryPoint = "read", SetLastError = true)] private static extern long NativeRead(int descriptor, IntPtr bytes, nuint size);
    [DllImport(LibSystem, EntryPoint = "write", SetLastError = true)] private static extern long NativeWrite(int descriptor, IntPtr bytes, nuint size);
    [DllImport(LibSystem, EntryPoint = "fcntl", SetLastError = true)] private static extern int NativeControl(int descriptor, int command);
    [DllImport(LibSystem, EntryPoint = "renameatx_np", SetLastError = true)]
    private static extern int NativeRename(int sourceDirectory, string source, int targetDirectory, string target, uint flags);
    [DllImport(LibSystem, EntryPoint = "unlinkat", SetLastError = true)] private static extern int NativeUnlink(int directory, string name, int flags);
    [DllImport(LibSystem, EntryPoint = "close", SetLastError = true)] private static extern int NativeClose(int descriptor);
    [DllImport(LibSystem, EntryPoint = "fdopendir$INODE64", SetLastError = true)] private static extern IntPtr OpenDirectoryStreamIntel(int descriptor);
    [DllImport(LibSystem, EntryPoint = "fdopendir", SetLastError = true)] private static extern IntPtr OpenDirectoryStreamArm(int descriptor);
    [DllImport(LibSystem, EntryPoint = "readdir$INODE64", SetLastError = true)] private static extern IntPtr ReadDirectoryIntel(IntPtr directory);
    [DllImport(LibSystem, EntryPoint = "readdir", SetLastError = true)] private static extern IntPtr ReadDirectoryArm(IntPtr directory);
    [DllImport(LibSystem, EntryPoint = "closedir", SetLastError = true)] private static extern int CloseDirectoryStream(IntPtr directory);
    [DllImport(LibSystem, EntryPoint = "__error")] private static extern IntPtr ErrnoLocation();
    [DllImport(LibSystem, EntryPoint = "sysctlbyname", SetLastError = true)]
    private static extern int SystemControl(string name, byte[] value, ref nuint length, IntPtr newValue, nuint newLength);
}
