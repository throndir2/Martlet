namespace Martlet.LocalStt;

internal sealed class ProvisioningReadTree : IDisposable
{
    private readonly List<IDisposable> leases = [];
    private readonly List<(string Identity, string Relative)> files = [];

    internal ProvisioningReadTree(string root, int maximumEntries,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var pending = new Stack<string>();
            pending.Push(root);
            var count = 0;
            while (pending.TryPop(out var directory))
            {
                var directoryLease = new WindowsDirectoryLease(directory);
                leases.Add(directoryLease);
                foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ProvisioningGuard.Require(++count <= maximumEntries,
                        LocalSttProvisioningFailure.InvalidReceipt);
                    var attributes = File.GetAttributes(path);
                    if (PhysicalLocalPathInspector.IsUnsafe(attributes))
                        throw new LocalPathException();
                    if (attributes.HasFlag(FileAttributes.Directory))
                        pending.Push(path);
                    else
                    {
                        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                        leases.Add(stream);
                        WindowsLocalPath.Validate(stream.SafeFileHandle, path, directory: false);
                        files.Add((ProvisioningFileIdentity.Read(stream), Path.GetRelativePath(root, path)));
                    }
                }

            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal void ValidatePublished(string root)
    {
        foreach (var file in files)
        {
            var path = Path.Combine(root, file.Relative);
            using var named = ProvisioningFile.OpenRead(path);
            ProvisioningGuard.Require(ProvisioningFileIdentity.Read(named) ==
                file.Identity, LocalSttProvisioningFailure.InvalidReceipt);
        }
    }

    public void Dispose()
    {
        for (var index = leases.Count - 1; index >= 0; index--)
            leases[index].Dispose();
        leases.Clear();
    }
}
