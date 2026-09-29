using static Martlet.Gateway.Persistence.LinuxFileSystem;
using static Martlet.Gateway.Persistence.PersistenceFailure;

namespace Martlet.Gateway.Persistence;

// Separate from the authority directory: reading configuration never creates a lock or running marker.
internal sealed class LinuxControlDirectory : IDisposable
{
    private readonly ILinuxFileSystem fs;
    private readonly List<(LinuxDescriptor Handle, LinuxFileIdentity Identity, string Name)> chain = [];
    private int DirectoryFd => chain[^1].Handle.Value;
    internal const string Config = "host.json", Approval = "service-approval.json", Machine = "machine.json";
    internal const string Staging = "service-approval.staging";
    internal uint UserId => fs.UserId;
    internal uint GroupId => fs.GroupId;

    internal LinuxControlDirectory(string configPath, ILinuxFileSystem fs)
    {
        this.fs = fs;
        if (fs.UserId == 0 || !ValidPath(configPath) || !configPath.EndsWith("/" + Config, StringComparison.Ordinal))
            throw Error(GatewayPersistenceFailure.InvalidPath);
        try
        {
            var root = new LinuxDescriptor(fs, fs.OpenRoot());
            try { chain.Add((root, fs.Stat(root.Value), "/")); }
            catch { root.Dispose(); throw; }
            var parts = configPath.Split('/').Skip(1).SkipLast(1).ToArray();
            foreach (var part in parts)
            {
                LinuxOwnedDirectory.CheckDirectory(fs, DirectoryFd, false);
                var handle = new LinuxDescriptor(fs, fs.OpenAt(DirectoryFd, part,
                    LinuxFileSystem.Directory | NoFollow | CloseOnExec, 0, NoLinks | Beneath));
                try { chain.Add((handle, fs.Stat(handle.Value), part)); }
                catch { handle.Dispose(); throw; }
            }
            Validate();
            fs.VerifyFileSystem(DirectoryFd);
        }
        catch { Dispose(); throw; }
    }

    internal static bool ValidPath(string path) =>
        path is { Length: > 1 and <= 4096 } && path[0] == '/' &&
        path.All(c => c is >= ' ' and <= '~' && c != '\\') &&
        path.Split('/').Skip(1).All(p => p is not ("" or "." or ".."));

    private void Validate()
    {
        if (chain.Count < 2) throw Error(GatewayPersistenceFailure.InvalidPath);
        for (var i = 0; i < chain.Count; i++)
        {
            var entry = chain[i];
            LinuxOwnedDirectory.CheckDirectory(fs, entry.Handle.Value, i == chain.Count - 1);
            if (!entry.Identity.SameFile(fs.Stat(entry.Handle.Value)) ||
                i > 0 && (fs.StatAt(chain[i - 1].Handle.Value, entry.Name) is not { } named ||
                    !entry.Identity.SameFile(named)))
                throw Error(GatewayPersistenceFailure.InsecureStorage);
        }
    }

    private void Check(string name, LinuxDescriptor handle, LinuxFileIdentity expected)
    {
        Validate();
        var actual = fs.Stat(handle.Value);
        LinuxOwnedDirectory.CheckFile(fs, actual, chain[^1].Identity);
        if (!actual.SameFile(expected) || fs.HasAcl(handle.Value, false) ||
            fs.StatAt(DirectoryFd, name) is not { } named || !actual.SameFile(named))
            throw Error(GatewayPersistenceFailure.InsecureStorage);
    }

    internal byte[]? Read(string name, int maximum)
    {
        if (name is not (Config or Approval or Machine)) throw Error(GatewayPersistenceFailure.InvalidPath);
        Validate();
        var before = fs.StatAt(DirectoryFd, name);
        if (before is null) return null;
        LinuxOwnedDirectory.CheckFile(fs, before.Value, chain[^1].Identity);
        using var handle = new LinuxDescriptor(fs, fs.OpenAt(DirectoryFd, name,
            NoFollow | CloseOnExec | NonBlocking, 0, NoLinks | Beneath | NoMounts));
        Check(name, handle, before.Value);
        var length = fs.Stat(handle.Value).Length;
        if (length is < 1 || length > maximum) throw Error(GatewayPersistenceFailure.InvalidState);
        var bytes = new byte[(int)length];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var count = fs.Read(handle.Value, bytes, offset, bytes.Length - offset);
            if (count <= 0 || count > bytes.Length - offset) throw Error(GatewayPersistenceFailure.StorageFailed);
            offset += count;
        }
        if (fs.Read(handle.Value, new byte[1], 0, 1) != 0 || fs.Stat(handle.Value).Length != length)
            throw Error(GatewayPersistenceFailure.InvalidState);
        Check(name, handle, before.Value);
        return bytes;
    }

    internal void WriteApproval(byte[] bytes)
    {
        if (bytes.Length is < 1 or > 8192) throw Error(GatewayPersistenceFailure.InvalidState);
        _ = Read(Approval, 8192);
        Validate();
        using (var handle = new LinuxDescriptor(fs, fs.OpenAt(DirectoryFd, Staging,
            ReadWrite | Create | Exclusive | NoFollow | CloseOnExec | NonBlocking,
            0x180, NoLinks | Beneath | NoMounts)))
        {
            var initial = fs.Stat(handle.Value);
            Check(Staging, handle, initial);
            var offset = 0;
            while (offset < bytes.Length)
            {
                var count = fs.Write(handle.Value, bytes, offset, bytes.Length - offset);
                if (count <= 0 || count > bytes.Length - offset) throw Error(GatewayPersistenceFailure.StorageFailed);
                offset += count;
            }
            fs.Flush(handle.Value);
            Check(Staging, handle, initial);
        }
        _ = Read(Approval, 8192);
        Validate();
        fs.Rename(DirectoryFd, Staging, Approval, replace: true);
        fs.Flush(DirectoryFd);
    }

    internal void RemoveApproval()
    {
        if (Read(Approval, 8192) is null) return;
        Validate();
        fs.Unlink(DirectoryFd, Approval);
        fs.Flush(DirectoryFd);
    }

    public void Dispose()
    {
        foreach (var entry in chain.AsEnumerable().Reverse()) entry.Handle.Dispose();
        chain.Clear();
    }
}
