using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using static Martlet.Gateway.Persistence.PersistenceFailure;

namespace Martlet.Gateway.Persistence;

internal readonly record struct LinuxFileIdentity(
    ulong Inode, ulong Mount, uint DeviceMajor, uint DeviceMinor, uint User,
    ushort Mode, uint Links, long Length)
{
    internal bool SameFile(LinuxFileIdentity other) =>
        Inode == other.Inode && Mount == other.Mount &&
        DeviceMajor == other.DeviceMajor && DeviceMinor == other.DeviceMinor;
}

internal interface ILinuxFileSystem
{
    uint UserId { get; }
    uint GroupId { get; }
    int OpenRoot();
    int OpenAt(int directory, string name, int flags, uint mode, ulong resolve);
    void Close(int descriptor);
    LinuxFileIdentity Stat(int descriptor);
    LinuxFileIdentity? StatAt(int directory, string name);
    bool HasAcl(int descriptor, bool directory);
    void VerifyFileSystem(int descriptor);
    void MakeDirectory(int parent, string name, uint mode);
    void Lock(int descriptor);
    string[] Enumerate(int directory);
    int Read(int descriptor, byte[] bytes, int offset, int count);
    int Write(int descriptor, byte[] bytes, int offset, int count);
    void Flush(int descriptor);
    void Rename(int directory, string source, string target, bool replace);
    void Unlink(int directory, string name);
    Guid BootIdentity();
}

internal sealed class LinuxDescriptor(ILinuxFileSystem fileSystem, int value) : IDisposable
{
    internal int Value { get; } = value;
    private bool disposed;
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        fileSystem.Close(Value);
    }
}

internal sealed class LinuxFileSystem : ILinuxFileSystem
{
    internal const int Directory = 0x10000, NoFollow = 0x20000, CloseOnExec = 0x80000,
        NonBlocking = 0x800, ReadWrite = 2, Create = 0x40, Exclusive = 0x80;
    internal const ulong NoLinks = 0x6, Beneath = 0x8, NoMounts = 0x1;
    private const uint RequiredStat = 0x17ff;

    internal LinuxFileSystem()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
            throw Error(GatewayPersistenceFailure.UnsupportedPlatform);
        if (GetGlibcVersion() == IntPtr.Zero)
            throw Error(GatewayPersistenceFailure.UnsupportedPlatform);
        UserId = GetEffectiveUser();
        if (UserId == 0 || UserId != GetUser() || GetEffectiveGroup() != GetGroup())
            throw Error(GatewayPersistenceFailure.InsecureStorage);
    }

    public uint UserId { get; }
    public uint GroupId => GetGroup();

    public int OpenRoot()
    {
        var result = NativeOpen("/", NativeFlags(Directory | NoFollow | CloseOnExec));
        return Check(result);
    }

    /// <summary>The flags above are x86_64's; arm64 Linux numbers O_DIRECTORY and O_NOFOLLOW differently (0x4000 and
    /// 0x8000, where x86_64's 0x4000 is O_DIRECT and 0x20000 is O_LARGEFILE).</summary>
    internal static int NativeFlags(int flags, bool arm64 = false)
    {
        if (!arm64 && RuntimeInformation.ProcessArchitecture != Architecture.Arm64) return flags;
        var native = flags & ~(Directory | NoFollow);
        if ((flags & Directory) != 0) native |= 0x4000;
        if ((flags & NoFollow) != 0) native |= 0x8000;
        return native;
    }

    public int OpenAt(int directory, string name, int flags, uint mode, ulong resolve)
    {
        var how = new OpenHow { Flags = (ulong)NativeFlags(flags), Mode = mode, Resolve = resolve };
        long result;
        do { result = OpenAt2(437, directory, name, ref how, 24); }
        while (result < 0 && Marshal.GetLastPInvokeError() == 4);
        return checked((int)Check(result));
    }

    public void Close(int descriptor)
    {
        // Linux releases the descriptor even when close reports EINTR; retry could close a reused FD.
        _ = NativeClose(descriptor);
    }

    public LinuxFileIdentity Stat(int descriptor)
    {
        Check(NativeStat(descriptor, "", 0x1000, RequiredStat, out var result));
        return Identity(result);
    }

    public LinuxFileIdentity? StatAt(int directory, string name)
    {
        var status = NativeStat(directory, name, 0x100 | 0x800, RequiredStat, out var result);
        if (status < 0 && Marshal.GetLastPInvokeError() == 2) return null;
        Check(status);
        return Identity(result);
    }

    private static LinuxFileIdentity Identity(Statx value)
    {
        if ((value.Mask & RequiredStat) != RequiredStat || value.Size > long.MaxValue)
            throw Error(GatewayPersistenceFailure.UnsupportedPlatform);
        return new(value.Inode, value.Mount, value.DeviceMajor, value.DeviceMinor, value.User,
            value.Mode, value.Links, (long)value.Size);
    }

    public bool HasAcl(int descriptor, bool directory) =>
        HasAttribute(descriptor, "system.posix_acl_access") ||
        directory && HasAttribute(descriptor, "system.posix_acl_default");

    private static bool HasAttribute(int descriptor, string name)
    {
        var result = GetAttribute(descriptor, name, IntPtr.Zero, 0);
        if (result >= 0) return true;
        if (Marshal.GetLastPInvokeError() == 61) return false;
        Check(result);
        return false;
    }

    public void VerifyFileSystem(int descriptor)
    {
        if (FileSystemType(descriptor) != 0xef53)
            throw Error(GatewayPersistenceFailure.UnsupportedPlatform);
        var identity = Stat(descriptor);
        var text = ReadProc($"{GetProcess()}/mountinfo", 1024 * 1024);
        try
        {
            var entries = Encoding.UTF8.GetString(text).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var matching = entries.Where(line =>
                line.StartsWith(identity.Mount.ToString(System.Globalization.CultureInfo.InvariantCulture) + " ",
                    StringComparison.Ordinal)).ToArray();
            if (matching.Length != 1) throw Error(GatewayPersistenceFailure.UnsupportedPlatform);
            var halves = matching[0].Split(" - ", StringSplitOptions.None);
            var fields = halves[0].Split(' ');
            if (halves.Length != 2 || halves[1].Split(' ')[0] != "ext4" || fields.Length < 6 ||
                fields[2] != $"{identity.DeviceMajor}:{identity.DeviceMinor}" ||
                !fields[5].Split(',').Contains("rw"))
                throw Error(GatewayPersistenceFailure.UnsupportedPlatform);
        }
        finally { CryptographicOperations.ZeroMemory(text); }
    }

    private static long FileSystemType(int descriptor)
    {
        var memory = Marshal.AllocHGlobal(120);
        try
        {
            Check(NativeFileSystem(descriptor, memory));
            return Marshal.ReadInt64(memory);
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    public void MakeDirectory(int parent, string name, uint mode) =>
        Check(NativeMakeDirectory(parent, name, mode));

    public void Lock(int descriptor)
    {
        var result = NativeLock(descriptor, 2 | 4);
        if (result < 0 && Marshal.GetLastPInvokeError() == 11)
            throw Error(GatewayPersistenceFailure.StoreBusy);
        Check(result);
    }

    public string[] Enumerate(int directory)
    {
        var duplicate = OpenAt(directory, ".", Directory | CloseOnExec | NoFollow, 0, NoLinks | Beneath | NoMounts);
        var stream = OpenDirectoryStream(duplicate);
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
                Marshal.SetLastPInvokeError(0);
                // readdir uses errno to distinguish end-of-directory from error.
                Marshal.WriteInt32(ErrnoLocation(), 0);
                var entry = ReadDirectory(stream);
                if (entry == IntPtr.Zero)
                {
                    var error = Marshal.ReadInt32(ErrnoLocation());
                    if (error != 0) throw NativeFailure(error);
                    return result.ToArray();
                }
                var length = (ushort)Marshal.ReadInt16(entry, 16);
                if (length is < 20 or > 280) throw Error(GatewayPersistenceFailure.InsecureStorage);
                var name = Marshal.PtrToStringUTF8(entry + 19, length - 19)!;
                var end = name.IndexOf('\0');
                if (end < 0) throw Error(GatewayPersistenceFailure.InsecureStorage);
                name = name[..end];
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
            } while (result < 0 && Marshal.GetLastPInvokeError() == 4);
            return checked((int)Check(result));
        }
        finally { pinned.Free(); }
    }

    public void Flush(int descriptor)
    {
        int result;
        do { result = NativeFlush(descriptor); }
        while (result < 0 && Marshal.GetLastPInvokeError() == 4);
        Check(result);
    }

    public void Rename(int directory, string source, string target, bool replace) =>
        Check(NativeRename(directory, source, directory, target, replace ? 0u : 1u));

    public void Unlink(int directory, string name) => Check(NativeUnlink(directory, name, 0));

    public Guid BootIdentity()
    {
        var bytes = ReadProc("sys/kernel/random/boot_id", 37);
        try
        {
            if (bytes.Length != 37 || bytes[^1] != '\n' ||
                !Guid.TryParseExact(Encoding.ASCII.GetString(bytes, 0, 36), "D", out var id) || id == Guid.Empty)
                throw Error(GatewayPersistenceFailure.ClockUnavailable);
            return id;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private byte[] ReadProc(string relative, int maximum)
    {
        using var root = new LinuxDescriptor(this, OpenRoot());
        using var proc = new LinuxDescriptor(this, OpenAt(root.Value, "proc", Directory | CloseOnExec | NoFollow, 0, NoLinks | Beneath));
        if (FileSystemType(proc.Value) != 0x9fa0 || Stat(proc.Value).User != 0)
            throw Error(GatewayPersistenceFailure.UnsupportedPlatform);
        // Containers bind-mount /proc/sys read-only inside procfs, so mount crossings are allowed here;
        // the file must still be a procfs regular file.
        using var file = new LinuxDescriptor(this, OpenAt(proc.Value, relative, CloseOnExec | NoFollow | NonBlocking, 0,
            NoLinks | Beneath));
        if (FileSystemType(file.Value) != 0x9fa0 || (Stat(file.Value).Mode & 0xf000) != 0x8000)
            throw Error(GatewayPersistenceFailure.UnsupportedPlatform);
        var buffer = new byte[maximum + 1];
        try
        {
            var length = 0;
            while (length < buffer.Length)
            {
                var read = Read(file.Value, buffer, length, buffer.Length - length);
                if (read == 0) return buffer.AsSpan(0, length).ToArray();
                length += read;
            }
            throw Error(GatewayPersistenceFailure.UnsupportedPlatform);
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }

    private static int Check(int value) => (int)Check((long)value);
    private static long Check(long value)
    {
        if (value < 0) throw NativeFailure(Marshal.GetLastPInvokeError());
        return value;
    }

    private static GatewayPersistenceException NativeFailure(int error) => Error(error switch
    {
        2 => GatewayPersistenceFailure.StoreMissing,
        1 or 13 or 18 or 20 or 40 => GatewayPersistenceFailure.InsecureStorage,
        17 => GatewayPersistenceFailure.RecoveryRequired,
        22 or 38 or 95 => GatewayPersistenceFailure.UnsupportedPlatform,
        _ => GatewayPersistenceFailure.StorageFailed
    });

    [StructLayout(LayoutKind.Sequential)]
    private struct OpenHow { internal ulong Flags, Mode, Resolve; }

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct Statx
    {
        [FieldOffset(0)] internal uint Mask;
        [FieldOffset(16)] internal uint Links;
        [FieldOffset(20)] internal uint User;
        [FieldOffset(28)] internal ushort Mode;
        [FieldOffset(32)] internal ulong Inode;
        [FieldOffset(40)] internal ulong Size;
        [FieldOffset(136)] internal uint DeviceMajor;
        [FieldOffset(140)] internal uint DeviceMinor;
        [FieldOffset(144)] internal ulong Mount;
    }

    [DllImport("libc", EntryPoint = "geteuid")] private static extern uint GetEffectiveUser();
    [DllImport("libc", EntryPoint = "gnu_get_libc_version")] private static extern IntPtr GetGlibcVersion();
    [DllImport("libc", EntryPoint = "getuid")] private static extern uint GetUser();
    [DllImport("libc", EntryPoint = "getegid")] private static extern uint GetEffectiveGroup();
    [DllImport("libc", EntryPoint = "getgid")] private static extern uint GetGroup();
    [DllImport("libc", EntryPoint = "getpid")] private static extern int GetProcess();
    [DllImport("libc", EntryPoint = "open", SetLastError = true)] private static extern int NativeOpen(string path, int flags);
    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)] private static extern long OpenAt2(long number, int directory, string name, ref OpenHow how, nuint size);
    [DllImport("libc", EntryPoint = "statx", SetLastError = true)] private static extern int NativeStat(int directory, string name, int flags, uint mask, out Statx result);
    [DllImport("libc", EntryPoint = "fstatfs", SetLastError = true)] private static extern int NativeFileSystem(int descriptor, IntPtr result);
    [DllImport("libc", EntryPoint = "fgetxattr", SetLastError = true)] private static extern long GetAttribute(int descriptor, string name, IntPtr value, nuint size);
    [DllImport("libc", EntryPoint = "mkdirat", SetLastError = true)] private static extern int NativeMakeDirectory(int parent, string name, uint mode);
    [DllImport("libc", EntryPoint = "flock", SetLastError = true)] private static extern int NativeLock(int descriptor, int operation);
    [DllImport("libc", EntryPoint = "read", SetLastError = true)] private static extern long NativeRead(int descriptor, IntPtr bytes, nuint size);
    [DllImport("libc", EntryPoint = "write", SetLastError = true)] private static extern long NativeWrite(int descriptor, IntPtr bytes, nuint size);
    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)] private static extern int NativeFlush(int descriptor);
    [DllImport("libc", EntryPoint = "renameat2", SetLastError = true)] private static extern int NativeRename(int sourceDirectory, string source, int targetDirectory, string target, uint flags);
    [DllImport("libc", EntryPoint = "unlinkat", SetLastError = true)] private static extern int NativeUnlink(int directory, string name, int flags);
    [DllImport("libc", EntryPoint = "close", SetLastError = true)] private static extern int NativeClose(int descriptor);
    [DllImport("libc", EntryPoint = "fdopendir", SetLastError = true)] private static extern IntPtr OpenDirectoryStream(int descriptor);
    [DllImport("libc", EntryPoint = "readdir", SetLastError = true)] private static extern IntPtr ReadDirectory(IntPtr directory);
    [DllImport("libc", EntryPoint = "closedir", SetLastError = true)] private static extern int CloseDirectoryStream(IntPtr directory);
    [DllImport("libc", EntryPoint = "__errno_location")] private static extern IntPtr ErrnoLocation();
}
