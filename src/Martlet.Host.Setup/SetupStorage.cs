using System.Security.Cryptography;
using System.Security;

namespace Martlet.Host.Setup;

public sealed class SetupFileSnapshot
{
    private readonly byte[] content;
    public ReadOnlyMemory<byte> Content => content;
    public string Version { get; }

    public SetupFileSnapshot(ReadOnlyMemory<byte> content, string version)
    {
        SetupGuard.Fingerprint(version, SetupFailure.JournalIoFailure);
        this.content = content.ToArray();
        Version = version;
    }
}

public interface ISetupFileSystem
{
    string JournalPath { get; }
    ValueTask<SetupFileSnapshot?> ReadAsync(int maximumBytes, CancellationToken cancellationToken);
    ValueTask<SetupFileSnapshot> WriteAtomicAsync(
        string? expectedVersion,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken);
}

public sealed class LocalSetupFileSystem : ISetupFileSystem
{
    private readonly string pendingPath;
    private readonly string parentDirectory;
    private readonly ISetupDirectoryCommitter directoryCommitter;
    public string JournalPath { get; }

    public LocalSetupFileSystem(
        string absoluteJournalPath,
        ISetupDirectoryCommitter? directoryCommitter = null)
    {
        SetupGuard.Text(absoluteJournalPath, 1024, SetupFailure.InvalidConfiguration);
        if (!Path.IsPathFullyQualified(absoluteJournalPath) ||
            !string.Equals(Path.GetExtension(absoluteJournalPath), ".json", StringComparison.OrdinalIgnoreCase))
            throw new SetupException(SetupFailure.InvalidConfiguration);
        try
        {
            JournalPath = Path.GetFullPath(absoluteJournalPath);
            if (OperatingSystem.IsWindows())
            {
                ValidateWindowsPath(absoluteJournalPath);
                if (new DriveInfo(Path.GetPathRoot(JournalPath)!).DriveType == DriveType.Network)
                    throw new SetupException(SetupFailure.InvalidConfiguration);
            }
            pendingPath = JournalPath + ".pending";
            var parent = Path.GetDirectoryName(JournalPath);
            if (parent is null || !Directory.Exists(parent))
                throw new SetupException(SetupFailure.InvalidConfiguration);
            parentDirectory = parent;
            if (directoryCommitter is null && !OperatingSystem.IsLinux())
                throw new SetupException(SetupFailure.InvalidConfiguration);
            this.directoryCommitter = directoryCommitter ?? new PlatformSetupDirectoryCommitter();
            ValidateDirectory();
        }
        catch (ArgumentException)
        {
            throw new SetupException(SetupFailure.InvalidConfiguration);
        }
        catch (NotSupportedException)
        {
            throw new SetupException(SetupFailure.InvalidConfiguration);
        }
        catch (UnauthorizedAccessException)
        {
            throw new SetupException(SetupFailure.JournalIoFailure);
        }
        catch (IOException)
        {
            throw new SetupException(SetupFailure.JournalIoFailure);
        }
        catch (SecurityException)
        {
            throw new SetupException(SetupFailure.JournalIoFailure);
        }
    }

    public async ValueTask<SetupFileSnapshot?> ReadAsync(int maximumBytes, CancellationToken cancellationToken)
    {
        SetupGuard.Require(maximumBytes is > 0 and <= SetupJournalCodec.MaximumBytes,
            SetupFailure.InvalidConfiguration);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            ValidateDirectory();
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(JournalPath);
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                throw new SetupException(SetupFailure.JournalIoFailure);
            await using var stream = new FileStream(JournalPath, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            });
            if (stream.Length > maximumBytes) throw new SetupException(SetupFailure.JournalTooLarge);
            var bytes = new byte[checked((int)stream.Length)];
            var offset = 0;
            while (offset < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(offset), cancellationToken).ConfigureAwait(false);
                if (read == 0) throw new SetupException(SetupFailure.JournalIoFailure);
                offset += read;
            }
            if (stream.ReadByte() != -1) throw new SetupException(SetupFailure.JournalTooLarge);
            return new(bytes, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }
        catch (UnauthorizedAccessException)
        {
            throw new SetupException(SetupFailure.JournalIoFailure);
        }
        catch (IOException)
        {
            throw new SetupException(SetupFailure.JournalIoFailure);
        }
        catch (SecurityException)
        {
            throw new SetupException(SetupFailure.JournalIoFailure);
        }
    }

    public async ValueTask<SetupFileSnapshot> WriteAtomicAsync(
        string? expectedVersion,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        if (content.Length > SetupJournalCodec.MaximumBytes)
            throw new SetupException(SetupFailure.JournalTooLarge);
        if (expectedVersion is not null)
            SetupGuard.Fingerprint(expectedVersion, SetupFailure.JournalConcurrentChange);
        cancellationToken.ThrowIfCancellationRequested();
        var ownedContent = content.ToArray();
        var pendingCreated = false;
        try
        {
            ValidateDirectory();
            try
            {
                _ = File.GetAttributes(pendingPath);
                throw new SetupException(SetupFailure.JournalConcurrentChange);
            }
            catch (FileNotFoundException) { }
            var current = await ReadAsync(SetupJournalCodec.MaximumBytes, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(current?.Version, expectedVersion, StringComparison.Ordinal))
                throw new SetupException(SetupFailure.JournalConcurrentChange);

            await using (var stream = new FileStream(pendingPath, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough
            }))
            {
                pendingCreated = true;
                await stream.WriteAsync(ownedContent, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            ValidateDirectory();
            var latest = await ReadAsync(SetupJournalCodec.MaximumBytes, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(latest?.Version, expectedVersion, StringComparison.Ordinal))
            {
                DeletePending();
                pendingCreated = false;
                throw new SetupException(SetupFailure.JournalConcurrentChange);
            }
            File.Move(pendingPath, JournalPath, overwrite: latest is not null);
            pendingCreated = false;
            directoryCommitter.Commit(parentDirectory);
            var committed = await ReadAsync(SetupJournalCodec.MaximumBytes, CancellationToken.None).ConfigureAwait(false)
                ?? throw new SetupException(SetupFailure.JournalIoFailure);
            var intended = Convert.ToHexStringLower(SHA256.HashData(ownedContent));
            if (!string.Equals(committed.Version, intended, StringComparison.Ordinal))
                throw new SetupException(SetupFailure.JournalIoFailure);
            return committed;
        }
        catch (OperationCanceledException) when (pendingCreated)
        {
            DeletePending();
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            throw new SetupException(SetupFailure.JournalIoFailure);
        }
        catch (IOException)
        {
            throw new SetupException(SetupFailure.JournalIoFailure);
        }
        catch (SecurityException)
        {
            throw new SetupException(SetupFailure.JournalIoFailure);
        }
    }

    private void ValidateDirectory()
    {
        for (var directory = new DirectoryInfo(parentDirectory); directory is not null; directory = directory.Parent)
        {
            var attributes = File.GetAttributes(directory.FullName);
            if ((attributes & FileAttributes.Directory) == 0 ||
                (attributes & FileAttributes.ReparsePoint) != 0)
                throw new SetupException(SetupFailure.JournalIoFailure);
        }
    }

    private static void ValidateWindowsPath(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal) ||
            path[2..].Contains(':'))
            throw new SetupException(SetupFailure.InvalidConfiguration);
        foreach (var part in path[3..].Split(['\\', '/']))
        {
            if (part.Length == 0 || part.EndsWith('.') || part.EndsWith(' ') ||
                part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new SetupException(SetupFailure.InvalidConfiguration);
            var stem = part.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CLOCK$" or "CONIN$" or "CONOUT$" ||
                stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) ||
                    stem.StartsWith("LPT", StringComparison.Ordinal)) && "0123456789\u00b9\u00b2\u00b3".Contains(stem[3]))
                throw new SetupException(SetupFailure.InvalidConfiguration);
        }
    }

    private void DeletePending()
    {
        try
        {
            File.Delete(pendingPath);
        }
        catch (UnauthorizedAccessException)
        {
            throw new SetupException(SetupFailure.JournalIoFailure);
        }
        catch (IOException)
        {
            throw new SetupException(SetupFailure.JournalIoFailure);
        }
        catch (SecurityException)
        {
            throw new SetupException(SetupFailure.JournalIoFailure);
        }
    }
}
