using System.Security.Cryptography;
using static Martlet.Gateway.Persistence.LinuxFileSystem;
using static Martlet.Gateway.Persistence.PersistenceFailure;

namespace Martlet.Gateway.Persistence;

internal enum LinuxStoreStep { PreparedRenamed, PreparedDirectoryFlushed, AuthorityRenamed, AuthorityDirectoryFlushed }

internal sealed class LinuxOwnedDirectory : IOwnedAuthorityDirectory
{
    private static readonly string[] Names = ["owner.lock", "authority.bin", "running", "staging.bin", "pending.bin"];
    private readonly ILinuxFileSystem fileSystem;
    private readonly List<(LinuxDescriptor Handle, LinuxFileIdentity Identity, string Name)> ancestors;
    private readonly LinuxDescriptor directory;
    private readonly LinuxFileIdentity identity;
    private readonly LinuxDescriptor ownerLock;
    private readonly LinuxFileIdentity lockIdentity;
    private readonly Action<LinuxStoreStep>? fault;
    private bool closed;

    private LinuxOwnedDirectory(ILinuxFileSystem fileSystem,
        List<(LinuxDescriptor Handle, LinuxFileIdentity Identity, string Name)> ancestors,
        LinuxDescriptor directory, LinuxDescriptor ownerLock, Action<LinuxStoreStep>? fault)
    {
        this.fileSystem = fileSystem;
        this.ancestors = ancestors;
        this.directory = directory;
        identity = fileSystem.Stat(directory.Value);
        this.ownerLock = ownerLock;
        lockIdentity = fileSystem.Stat(ownerLock.Value);
        this.fault = fault;
    }

    internal static LinuxOwnedDirectory Open(string path, bool create, ILinuxFileSystem fileSystem,
        Action<LinuxStoreStep>? fault = null)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 4096 || path[0] != '/' ||
            path.Any(c => c == '\0' || char.IsControl(c)) ||
            path.Split('/').Skip(1).Any(p => p is "" or "." or ".."))
            throw Error(GatewayPersistenceFailure.InvalidPath);
        if (fileSystem.UserId == 0) throw Error(GatewayPersistenceFailure.InsecureStorage);
        var parts = path.Split('/').Skip(1).ToArray();
        if (parts.Length < 2) throw Error(GatewayPersistenceFailure.InvalidPath);
        var ancestors = new List<(LinuxDescriptor Handle, LinuxFileIdentity Identity, string Name)>();
        LinuxDescriptor? directory = null;
        LinuxDescriptor? ownerLock = null;
        try
        {
            var root = new LinuxDescriptor(fileSystem, fileSystem.OpenRoot());
            AddAncestor(root, "/");
            CheckDirectory(fileSystem, root.Value, false);
            foreach (var name in parts[..^1])
            {
                var next = new LinuxDescriptor(fileSystem, fileSystem.OpenAt(ancestors[^1].Handle.Value,
                    name, LinuxFileSystem.Directory | CloseOnExec | NoFollow, 0, NoLinks | Beneath));
                AddAncestor(next, name);
                CheckDirectory(fileSystem, next.Value, false);
            }
            var parent = ancestors[^1].Handle.Value;
            CheckDirectory(fileSystem, parent, true);
            fileSystem.VerifyFileSystem(parent);
            if (create)
            {
                if (fileSystem.StatAt(parent, parts[^1]) is not null)
                    throw Error(GatewayPersistenceFailure.InsecureStorage);
                fileSystem.MakeDirectory(parent, parts[^1], 0x1c0);
            }
            directory = new(fileSystem, fileSystem.OpenAt(parent, parts[^1], LinuxFileSystem.Directory | CloseOnExec | NoFollow,
                0, NoLinks | Beneath | NoMounts));
            CheckDirectory(fileSystem, directory.Value, true);
            var identity = fileSystem.Stat(directory.Value);
            RequireSameMount(identity, fileSystem.Stat(parent));
            fileSystem.VerifyFileSystem(directory.Value);
            CheckNames(fileSystem, directory.Value);
            if (create)
            {
                fileSystem.Flush(directory.Value);
                fileSystem.Flush(parent);
            }
            var previousLock = fileSystem.StatAt(directory.Value, "owner.lock");
            if (previousLock is { } known) CheckFile(fileSystem, known, identity);
            ownerLock = new(fileSystem, fileSystem.OpenAt(directory.Value, "owner.lock",
                ReadWrite | CloseOnExec | NoFollow | NonBlocking |
                (previousLock is null ? Create | Exclusive : 0), previousLock is null ? 0x180u : 0,
                NoLinks | Beneath | NoMounts));
            var actual = fileSystem.Stat(ownerLock.Value);
            CheckFile(fileSystem, actual, identity);
            if (previousLock is { } prior && !actual.SameFile(prior) || fileSystem.HasAcl(ownerLock.Value, false))
                throw Error(GatewayPersistenceFailure.InsecureStorage);
            fileSystem.Lock(ownerLock.Value);
            if (previousLock is null)
            {
                fileSystem.Flush(ownerLock.Value);
                fileSystem.Flush(directory.Value);
            }
            var result = new LinuxOwnedDirectory(fileSystem, ancestors, directory, ownerLock, fault);
            result.ValidateOwner(parts[^1]);
            result.leafName = parts[^1];
            result.ValidateFiles();
            return result;
        }
        catch
        {
            ownerLock?.Dispose();
            directory?.Dispose();
            foreach (var ancestor in ancestors.AsEnumerable().Reverse()) ancestor.Handle.Dispose();
            throw;
        }

        void AddAncestor(LinuxDescriptor handle, string name)
        {
            try { ancestors.Add((handle, fileSystem.Stat(handle.Value), name)); }
            catch { handle.Dispose(); throw; }
        }
    }

    private string leafName = "";

    private static void CheckDirectory(ILinuxFileSystem fs, int descriptor, bool isPrivate)
    {
        var value = fs.Stat(descriptor);
        if ((value.Mode & 0xf000) != 0x4000 || (value.Mode & 0xe00) != 0 ||
            (value.User != 0 && value.User != fs.UserId) ||
            (isPrivate ? value.User != fs.UserId || (value.Mode & 0x1ff) != 0x1c0 : (value.Mode & 0x12) != 0) ||
            fs.HasAcl(descriptor, true))
            throw Error(GatewayPersistenceFailure.InsecureStorage);
    }

    private static void CheckFile(ILinuxFileSystem fs, LinuxFileIdentity value, LinuxFileIdentity directory)
    {
        if ((value.Mode & 0xffff) != (0x8000 | 0x180) || value.User != fs.UserId || value.Links != 1)
            throw Error(GatewayPersistenceFailure.InsecureStorage);
        RequireSameMount(value, directory);
    }

    private static void RequireSameMount(LinuxFileIdentity value, LinuxFileIdentity directory)
    {
        if (value.Mount != directory.Mount || value.DeviceMajor != directory.DeviceMajor ||
            value.DeviceMinor != directory.DeviceMinor)
            throw Error(GatewayPersistenceFailure.InsecureStorage);
    }

    private void ValidateOwner(string? leaf = null)
    {
        if (closed) throw Error(GatewayPersistenceFailure.Closed);
        for (var i = 0; i < ancestors.Count; i++)
        {
            var ancestor = ancestors[i];
            CheckDirectory(fileSystem, ancestor.Handle.Value, i == ancestors.Count - 1);
            if (!ancestor.Identity.SameFile(fileSystem.Stat(ancestor.Handle.Value)))
                throw Error(GatewayPersistenceFailure.InsecureStorage);
            if (i > 0)
            {
                var named = fileSystem.StatAt(ancestors[i - 1].Handle.Value, ancestor.Name);
                if (named is null || !ancestor.Identity.SameFile(named.Value))
                    throw Error(GatewayPersistenceFailure.InsecureStorage);
            }
        }
        CheckDirectory(fileSystem, directory.Value, true);
        if (!identity.SameFile(fileSystem.Stat(directory.Value)) ||
            fileSystem.StatAt(ancestors[^1].Handle.Value, leaf ?? leafName) is not { } current ||
            !identity.SameFile(current))
            throw Error(GatewayPersistenceFailure.InsecureStorage);
        var actualLock = fileSystem.Stat(ownerLock.Value);
        CheckFile(fileSystem, actualLock, identity);
        if (!lockIdentity.SameFile(actualLock) ||
            fileSystem.StatAt(directory.Value, "owner.lock") is not { } namedLock ||
            !lockIdentity.SameFile(namedLock) || fileSystem.HasAcl(ownerLock.Value, false))
            throw Error(GatewayPersistenceFailure.InsecureStorage);
    }

    private static void CheckNames(ILinuxFileSystem fs, int descriptor)
    {
        if (fs.Enumerate(descriptor).Any(name => !Names.Contains(name, StringComparer.Ordinal)))
            throw Error(GatewayPersistenceFailure.RecoveryRequired);
    }

    private static void RequireName(string name)
    {
        if (!Names.Contains(name, StringComparer.Ordinal))
            throw Error(GatewayPersistenceFailure.InvalidPath);
    }

    public void ValidateFiles()
    {
        ValidateOwner();
        CheckNames(fileSystem, directory.Value);
        foreach (var name in fileSystem.Enumerate(directory.Value))
            if (name != "owner.lock") Validate(name);
    }

    public bool Exists(string name)
    {
        RequireName(name);
        ValidateOwner();
        var value = fileSystem.StatAt(directory.Value, name);
        if (value is null) return false;
        CheckFile(fileSystem, value.Value, identity);
        return true;
    }

    private LinuxDescriptor OpenFile(string name, int flags)
    {
        RequireName(name);
        ValidateOwner();
        var before = fileSystem.StatAt(directory.Value, name) ?? throw Error(GatewayPersistenceFailure.StoreMissing);
        CheckFile(fileSystem, before, identity);
        var handle = new LinuxDescriptor(fileSystem, fileSystem.OpenAt(directory.Value, name,
            flags | CloseOnExec | NoFollow | NonBlocking, 0, NoLinks | Beneath | NoMounts));
        try
        {
            CheckOpened(name, handle, before);
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    private void CheckOpened(string name, LinuxDescriptor handle, LinuxFileIdentity expected)
    {
        var actual = fileSystem.Stat(handle.Value);
        CheckFile(fileSystem, actual, identity);
        if (!actual.SameFile(expected) || fileSystem.HasAcl(handle.Value, false) ||
            fileSystem.StatAt(directory.Value, name) is not { } named || !actual.SameFile(named))
            throw Error(GatewayPersistenceFailure.InsecureStorage);
    }

    public void Validate(string name)
    {
        using var handle = OpenFile(name, 0);
    }

    public byte[] Read(string name, int maximum)
    {
        using var handle = OpenFile(name, 0);
        var initial = fileSystem.Stat(handle.Value);
        if (initial.Length < 0 || initial.Length > maximum)
            throw Error(GatewayPersistenceFailure.InvalidState);
        var bytes = new byte[(int)initial.Length];
        var extra = new byte[1];
        try
        {
            var offset = 0;
            while (offset < bytes.Length)
            {
                var read = fileSystem.Read(handle.Value, bytes, offset, bytes.Length - offset);
                if (read <= 0 || read > bytes.Length - offset) throw Error(GatewayPersistenceFailure.InvalidState);
                offset += read;
            }
            if (fileSystem.Read(handle.Value, extra, 0, 1) != 0 ||
                fileSystem.Stat(handle.Value).Length != initial.Length)
                throw Error(GatewayPersistenceFailure.InvalidState);
            CheckOpened(name, handle, initial);
            ValidateOwner();
            return bytes;
        }
        catch { CryptographicOperations.ZeroMemory(bytes); throw; }
        finally { CryptographicOperations.ZeroMemory(extra); }
    }

    public void WriteNew(string name, ReadOnlySpan<byte> bytes)
    {
        RequireName(name);
        ValidateOwner();
        if (name is not ("staging.bin" or "running") || bytes.Length is <= 0 or > StoreFormat.MaximumBytes)
            throw Error(GatewayPersistenceFailure.InvalidState);
        using var handle = new LinuxDescriptor(fileSystem, fileSystem.OpenAt(directory.Value, name,
            ReadWrite | Create | Exclusive | CloseOnExec | NoFollow | NonBlocking, 0x180, NoLinks | Beneath | NoMounts));
        var initial = fileSystem.Stat(handle.Value);
        CheckOpened(name, handle, initial);
        var ownedBytes = bytes.ToArray();
        try
        {
            var offset = 0;
            while (offset < ownedBytes.Length)
            {
                var written = fileSystem.Write(handle.Value, ownedBytes, offset, ownedBytes.Length - offset);
                if (written <= 0 || written > ownedBytes.Length - offset)
                    throw Error(GatewayPersistenceFailure.StorageFailed);
                offset += written;
            }
            fileSystem.Flush(handle.Value);
            CheckOpened(name, handle, initial);
            if (fileSystem.Stat(handle.Value).Length != bytes.Length)
                throw Error(GatewayPersistenceFailure.StorageFailed);
            fileSystem.Flush(directory.Value);
            ValidateOwner();
        }
        finally { CryptographicOperations.ZeroMemory(ownedBytes); }
    }

    public void Prepare()
    {
        using var handle = OpenFile("staging.bin", ReadWrite);
        var initial = fileSystem.Stat(handle.Value);
        if (Exists("pending.bin")) throw Error(GatewayPersistenceFailure.RecoveryRequired);
        fileSystem.Rename(directory.Value, "staging.bin", "pending.bin", false);
        fault?.Invoke(LinuxStoreStep.PreparedRenamed);
        CheckOpened("pending.bin", handle, initial);
        fileSystem.Flush(directory.Value);
        fault?.Invoke(LinuxStoreStep.PreparedDirectoryFlushed);
        ValidateOwner();
    }

    public void Promote(bool replace, Action? replaced = null)
    {
        using var handle = OpenFile("pending.bin", ReadWrite);
        var initial = fileSystem.Stat(handle.Value);
        if (replace) Validate("authority.bin");
        else if (Exists("authority.bin")) throw Error(GatewayPersistenceFailure.RecoveryRequired);
        fileSystem.Rename(directory.Value, "pending.bin", "authority.bin", replace);
        fault?.Invoke(LinuxStoreStep.AuthorityRenamed);
        replaced?.Invoke();
        CheckOpened("authority.bin", handle, initial);
        fileSystem.Flush(handle.Value);
        fileSystem.Flush(directory.Value);
        fault?.Invoke(LinuxStoreStep.AuthorityDirectoryFlushed);
        ValidateOwner();
    }

    public void RemoveUnpreparedStage() => Remove("staging.bin");
    public void RemoveRunning() => Remove("running");

    private void Remove(string name)
    {
        using var handle = OpenFile(name, ReadWrite);
        fileSystem.Unlink(directory.Value, name);
        fileSystem.Flush(directory.Value);
        ValidateOwner();
    }

    public void Dispose()
    {
        if (closed) return;
        closed = true;
        ownerLock.Dispose();
        directory.Dispose();
        foreach (var ancestor in ancestors.AsEnumerable().Reverse()) ancestor.Handle.Dispose();
    }
}
