using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using static Martlet.Gateway.Persistence.PersistenceFailure;

namespace Martlet.Gateway.Persistence;

// Adapted from PR #37, 18daa4acfa90d7c7dff52861c93800a2f63b0823.
[SupportedOSPlatform("windows")]
internal sealed class WindowsOwnedDirectory : IOwnedAuthorityDirectory
{
    private readonly SecurityIdentifier user;
    private readonly SafeFileHandle directoryHandle;
    private readonly FileStream ownerLock;
    internal string Path { get; }

    private WindowsOwnedDirectory(string path, SecurityIdentifier user, SafeFileHandle directoryHandle, FileStream ownerLock)
    {
        Path = path;
        this.user = user;
        this.directoryHandle = directoryHandle;
        this.ownerLock = ownerLock;
    }

    internal static WindowsOwnedDirectory Open(string path, bool create)
    {
        ValidatePath(path);
        var full = System.IO.Path.GetFullPath(path).TrimEnd('\\');
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw Error(GatewayPersistenceFailure.InsecureStorage);
        var info = new DirectoryInfo(full);
        if (create)
        {
            if (info.Exists)
                throw Error(GatewayPersistenceFailure.InsecureStorage);
            var security = new DirectorySecurity();
            security.SetOwner(user);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (var sid in new[] { user, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
                security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None, AccessControlType.Allow));
            info.Create(security);
            info.Refresh();
        }
        if (!info.Exists)
            throw Error(GatewayPersistenceFailure.StoreMissing);
        ValidatePath(full);
        var handle = CreateFile(full, 0x80000000, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw Error(GatewayPersistenceFailure.InsecureStorage);
        }
        FileStream? ownerLock = null;
        try
        {
            CheckHandle(handle);
            var finalPath = new StringBuilder(32768);
            var length = GetFinalPathNameByHandle(handle, finalPath, (uint)finalPath.Capacity, 0);
            if (length == 0 || length >= finalPath.Capacity ||
                !string.Equals(finalPath.ToString(), @"\\?\" + full, StringComparison.OrdinalIgnoreCase))
                throw Error(GatewayPersistenceFailure.InsecureStorage);
            CheckAcl(info.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access), user, directory: true);
            var lockPath = System.IO.Path.Combine(full, "owner.lock");
            if (File.Exists(lockPath) && (File.GetAttributes(lockPath) & FileAttributes.ReparsePoint) != 0)
                throw Error(GatewayPersistenceFailure.InsecureStorage);
            try { ownerLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { throw Error(GatewayPersistenceFailure.StoreBusy); }
            CheckHandle(ownerLock.SafeFileHandle);
            CheckAcl(new FileInfo(lockPath).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access), user, directory: false);
            return new(full, user, handle, ownerLock);
        }
        catch
        {
            ownerLock?.Dispose();
            handle.Dispose();
            throw;
        }
    }

    internal string FilePath(string name) => System.IO.Path.Combine(Path, name);

    public void ValidateFiles()
    {
        CheckAcl(new DirectoryInfo(Path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access), user, directory: true);
        foreach (var entry in Directory.EnumerateFileSystemEntries(Path))
        {
            var name = System.IO.Path.GetFileName(entry);
            if (name is not ("owner.lock" or "authority.bin" or "running" or "pending.bin" or "staging.bin"))
                throw Error(GatewayPersistenceFailure.RecoveryRequired);
            if (name != "owner.lock")
                CheckFile(entry, user);
        }
    }

    public byte[] Read(string name, int maximum)
    {
        var path = FilePath(name);
        CheckFile(path, user);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        CheckHandle(file.SafeFileHandle);
        CheckAcl(new FileInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access), user, false);
        if (file.Length < 0 || file.Length > maximum)
            throw Error(GatewayPersistenceFailure.InvalidState);
        var bytes = new byte[(int)file.Length];
        file.ReadExactly(bytes);
        if (file.ReadByte() != -1)
            throw Error(GatewayPersistenceFailure.InvalidState);
        return bytes;
    }

    public void WriteNew(string name, ReadOnlySpan<byte> bytes)
    {
        using var file = new FileStream(FilePath(name), FileMode.CreateNew, FileAccess.Write, FileShare.None,
            4096, FileOptions.WriteThrough);
        CheckHandle(file.SafeFileHandle);
        CheckAcl(new FileInfo(FilePath(name)).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access), user, directory: false);
        file.Write(bytes);
        file.Flush(flushToDisk: true);
    }

    public void Validate(string name) => CheckFile(FilePath(name), user);

    public bool Exists(string name)
    {
        try { _ = File.GetAttributes(FilePath(name)); return true; }
        catch (FileNotFoundException) { return false; }
    }

    public void Prepare()
    {
        File.Move(FilePath("staging.bin"), FilePath("pending.bin"), overwrite: false);
        using var file = new FileStream(FilePath("pending.bin"), FileMode.Open,
            FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        file.Flush(flushToDisk: true);
    }

    public void Promote(bool replace, Action? replaced = null)
    {
        if (replace) File.Replace(FilePath("pending.bin"), FilePath("authority.bin"), null);
        else File.Move(FilePath("pending.bin"), FilePath("authority.bin"), overwrite: false);
        replaced?.Invoke();
        Validate("authority.bin");
        using var file = new FileStream(FilePath("authority.bin"), FileMode.Open,
            FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        file.Flush(flushToDisk: true);
    }

    public void RemoveUnpreparedStage() => File.Delete(FilePath("staging.bin"));
    public void RemoveRunning() => File.Delete(FilePath("running"));

    private static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathFullyQualified(path) ||
            path.StartsWith(@"\\", StringComparison.Ordinal) || path.IndexOf(':', 2) >= 0 ||
            path.Contains('/') || path.EndsWith('\\') ||
            !string.Equals(path, System.IO.Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase) ||
            path.Split('\\').Any(p => p is "." or ".." || p.EndsWith(' ') || p.EndsWith('.')))
            throw Error(GatewayPersistenceFailure.InvalidPath);
        var root = System.IO.Path.GetPathRoot(path)!;
        var drive = new DriveInfo(root);
        if (drive.DriveType != DriveType.Fixed || drive.DriveFormat != "NTFS" || path.Length <= root.Length)
            throw Error(GatewayPersistenceFailure.InvalidPath);
        var parent = new DirectoryInfo(path).Parent;
        if (parent is null || !parent.Exists)
            throw Error(GatewayPersistenceFailure.InvalidPath);
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw Error(GatewayPersistenceFailure.InsecureStorage);
    }

    private static void CheckFile(string path, SecurityIdentifier user)
    {
        var info = new FileInfo(path);
        if (!info.Exists || (info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw Error(GatewayPersistenceFailure.InsecureStorage);
        CheckAcl(info.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access), user, directory: false);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        CheckHandle(file.SafeFileHandle);
    }

    private static void CheckHandle(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info) || info.NumberOfLinks != 1 ||
            (info.Attributes & 0x400) != 0)
            throw Error(GatewayPersistenceFailure.InsecureStorage);
    }

    private static void CheckAcl(FileSystemSecurity security, SecurityIdentifier user, bool directory)
    {
        if (!user.Equals(security.GetOwner(typeof(SecurityIdentifier))) ||
            directory && !security.AreAccessRulesProtected)
            throw Error(GatewayPersistenceFailure.InsecureStorage);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var ownFull = false;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow ||
                !rule.IdentityReference.Equals(user) && !rule.IdentityReference.Equals(system) ||
                (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0)
                throw Error(GatewayPersistenceFailure.InsecureStorage);
            ownFull |= rule.IdentityReference.Equals(user) &&
                (rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl;
        }
        if (!ownFull)
            throw Error(GatewayPersistenceFailure.InsecureStorage);
    }

    public void Dispose()
    {
        ownerLock.Dispose();
        directoryHandle.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        internal uint Attributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME Creation;
        internal System.Runtime.InteropServices.ComTypes.FILETIME Access;
        internal System.Runtime.InteropServices.ComTypes.FILETIME Write;
        internal uint Volume;
        internal uint SizeHigh;
        internal uint SizeLow;
        internal uint NumberOfLinks;
        internal uint IndexHigh;
        internal uint IndexLow;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint sharing,
        IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
}
