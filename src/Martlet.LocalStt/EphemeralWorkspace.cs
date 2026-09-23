namespace Martlet.LocalStt;

internal enum WorkspaceStatus
{
    Ready,
    MissingOutput,
    OutputLimit,
    DiskFull,
    AccessDenied,
    UnsafePath,
    IoFailure,
    CleanupFailed
}

internal sealed record WorkspaceCreateResult(
    WorkspaceStatus Status,
    ILocalSttWorkspace? Workspace = null);

internal sealed record WorkspaceReadResult(
    WorkspaceStatus Status,
    byte[]? Bytes = null);

internal interface ILocalSttWorkspaceFactory
{
    Task<WorkspaceCreateResult> CreateAsync(
        Guid operationId,
        CanonicalWaveAudio audio,
        CancellationToken cancellationToken);
}

internal interface ILocalSttWorkspace
{
    string AudioPath { get; }
    string TranscriptPrefixPath { get; }
    string WorkingDirectory { get; }
    Task<WorkspaceReadResult> ReadTranscriptAsync(CancellationToken cancellationToken);
    Task<WorkspaceStatus> CleanupAsync();
}

internal sealed class EphemeralLocalSttWorkspaceFactory : ILocalSttWorkspaceFactory
{
    private readonly string root;
    private readonly ILocalPathInspector pathInspector;

    internal EphemeralLocalSttWorkspaceFactory()
        : this(
            Path.Combine(Path.GetTempPath(), "Martlet.LocalStt"),
            new PhysicalLocalPathInspector())
    {
    }

    internal EphemeralLocalSttWorkspaceFactory(string root, ILocalPathInspector pathInspector)
    {
        this.root = LocalPathRules.NormalizeRoot(root);
        this.pathInspector = pathInspector;
    }

    public async Task<WorkspaceCreateResult> CreateAsync(
        Guid operationId,
        CanonicalWaveAudio audio,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return new(WorkspaceStatus.IoFailure);
        var operationDirectory = LocalPathRules.Combine(root, operationId.ToString("N"));
        var inputPath = LocalPathRules.Combine(operationDirectory, "input.wav");
        FileStream? input = null;
        WindowsDirectoryLease? rootLease = null;
        WindowsDirectoryLease? operationLease = null;
        var created = false;
        try
        {
            Directory.CreateDirectory(root);
            pathInspector.AssertSafeExisting(root, directory: true);
            rootLease = new WindowsDirectoryLease(root);
            if (Directory.Exists(operationDirectory) || File.Exists(operationDirectory))
                return new(WorkspaceStatus.UnsafePath);
            WindowsLocalPath.CreateOwnedDirectory(operationDirectory);
            created = true;
            pathInspector.AssertSafeExisting(operationDirectory, directory: true);
            operationLease = new WindowsDirectoryLease(operationDirectory, ownsDirectory: true);
            var transcriptPrefix = LocalPathRules.Combine(operationDirectory, "transcript");
            input = new FileStream(
                inputPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.Read,
                65_536,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            WindowsLocalPath.Validate(input.SafeFileHandle, inputPath, directory: false);
            await audio.WriteToAsync(input, cancellationToken).ConfigureAwait(false);
            await input.FlushAsync(cancellationToken).ConfigureAwait(false);
            input.Position = 0;
            return new(WorkspaceStatus.Ready,
                new EphemeralLocalSttWorkspace(
                    operationDirectory,
                    inputPath,
                    transcriptPrefix,
                    input,
                    pathInspector,
                    operationLease));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return CreationFailure(WorkspaceStatus.IoFailure);
        }
        catch (UnauthorizedAccessException)
        {
            return CreationFailure(WorkspaceStatus.AccessDenied);
        }
        catch (LocalPathException)
        {
            return CreationFailure(WorkspaceStatus.UnsafePath);
        }
        catch (IOException error) when (IsDiskFull(error))
        {
            return CreationFailure(WorkspaceStatus.DiskFull);
        }
        catch (IOException)
        {
            return CreationFailure(WorkspaceStatus.IoFailure);
        }
        finally
        {
            rootLease?.Dispose();
        }

        WorkspaceCreateResult CreationFailure(WorkspaceStatus status)
        {
            return new(!created || TryCleanupFailedCreation(input, inputPath, operationDirectory, operationLease)
                ? status
                : WorkspaceStatus.CleanupFailed);
        }
    }

    private static bool IsDiskFull(IOException error)
    {
        var code = error.HResult & 0xffff;
        return code is 0x27 or 0x70;
    }

    internal bool TryCleanupFailedCreation(
        FileStream? input,
        string inputPath,
        string directory,
        WindowsDirectoryLease? directoryLease = null)
    {
        var clean = true;
        try
        {
            input?.Dispose();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            clean = false;
        }
        try
        {
            if (File.Exists(inputPath))
            {
                pathInspector.AssertSafeExisting(inputPath, directory: false);
                WindowsLocalPath.DeleteOwnedFile(inputPath);
            }
            if (Directory.Exists(directory))
            {
                pathInspector.AssertSafeExisting(directory, directory: true);
                if (Directory.EnumerateFileSystemEntries(directory).Any())
                    return false;
                if (directoryLease is not null)
                    directoryLease.DeleteOwnedDirectory();
                else
                    Directory.Delete(directory);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or LocalPathException)
        {
            return false;
        }
        finally
        {
            directoryLease?.Dispose();
        }
        return clean && !File.Exists(inputPath) && !Directory.Exists(directory);
    }
}

internal sealed class EphemeralLocalSttWorkspace : ILocalSttWorkspace
{
    private readonly string operationDirectory;
    private readonly FileStream input;
    private readonly ILocalPathInspector pathInspector;
    private readonly WindowsDirectoryLease directoryLease;
    private WorkspaceStatus? cleanupStatus;

    public string AudioPath { get; }
    public string TranscriptPrefixPath { get; }
    public string WorkingDirectory => operationDirectory;

    internal EphemeralLocalSttWorkspace(
        string operationDirectory,
        string audioPath,
        string transcriptPrefixPath,
        FileStream input,
        ILocalPathInspector pathInspector,
        WindowsDirectoryLease directoryLease)
    {
        this.operationDirectory = operationDirectory;
        AudioPath = audioPath;
        TranscriptPrefixPath = transcriptPrefixPath;
        this.input = input;
        this.pathInspector = pathInspector;
        this.directoryLease = directoryLease;
    }

    public async Task<WorkspaceReadResult> ReadTranscriptAsync(CancellationToken cancellationToken)
    {
        var path = TranscriptPrefixPath + ".txt";
        try
        {
            pathInspector.AssertSafeExisting(path, directory: false);
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            WindowsLocalPath.Validate(stream.SafeFileHandle, path, directory: false);
            return await ReadBoundedTranscriptAsync(stream,
                new byte[LocalSttPackageManifest.MaximumTranscriptBytes + 1], cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return new(WorkspaceStatus.MissingOutput);
        }
        catch (DirectoryNotFoundException)
        {
            return new(WorkspaceStatus.MissingOutput);
        }
        catch (UnauthorizedAccessException)
        {
            return new(WorkspaceStatus.AccessDenied);
        }
        catch (LocalPathException)
        {
            return new(WorkspaceStatus.UnsafePath);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(WorkspaceStatus.IoFailure);
        }
        catch (IOException)
        {
            return new(WorkspaceStatus.IoFailure);
        }
    }

    internal static async Task<WorkspaceReadResult> ReadBoundedTranscriptAsync(
        Stream stream, byte[] scratch, CancellationToken cancellationToken)
    {
        if (scratch.Length != LocalSttPackageManifest.MaximumTranscriptBytes + 1)
            throw new ArgumentException("Invalid transcript scratch size.", nameof(scratch));
        try
        {
            if (stream.Length > LocalSttPackageManifest.MaximumTranscriptBytes)
                return new(WorkspaceStatus.OutputLimit);
            var length = 0;
            while (length < scratch.Length)
            {
                var read = await stream.ReadAsync(scratch.AsMemory(length), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;
                length += read;
            }
            return length > LocalSttPackageManifest.MaximumTranscriptBytes
                ? new(WorkspaceStatus.OutputLimit)
                : new(WorkspaceStatus.Ready, scratch[..length]);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(scratch);
        }
    }

    public Task<WorkspaceStatus> CleanupAsync()
    {
        if (cleanupStatus is { } previous)
            return Task.FromResult(previous);
        try
        {
            input.Dispose();
            if (File.Exists(AudioPath))
                WindowsLocalPath.DeleteOwnedFile(AudioPath);
            if (Directory.Exists(operationDirectory))
            {
                var entries = Directory.EnumerateFileSystemEntries(operationDirectory).Take(9).ToArray();
                if (entries.Length > 8)
                    return Result(WorkspaceStatus.IoFailure);
                foreach (var path in entries)
                {
                    pathInspector.AssertSafeExisting(path, directory: false);
                    WindowsLocalPath.DeleteOwnedFile(path);
                }
                directoryLease.DeleteOwnedDirectory();
            }
            return Result(WorkspaceStatus.Ready);
        }
        catch (UnauthorizedAccessException)
        {
            return Result(WorkspaceStatus.AccessDenied);
        }
        catch (LocalPathException)
        {
            return Result(WorkspaceStatus.UnsafePath);
        }
        catch (IOException)
        {
            return Result(WorkspaceStatus.IoFailure);
        }
        finally
        {
            directoryLease.Dispose();
        }

        Task<WorkspaceStatus> Result(WorkspaceStatus status)
        {
            cleanupStatus = status;
            return Task.FromResult(status);
        }
    }
}
