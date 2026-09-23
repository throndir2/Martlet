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
        try
        {
            Directory.CreateDirectory(root);
            pathInspector.AssertSafeExisting(root, directory: true);
            if (Directory.Exists(operationDirectory) || File.Exists(operationDirectory))
                return new(WorkspaceStatus.UnsafePath);
            Directory.CreateDirectory(operationDirectory);
            pathInspector.AssertSafeExisting(operationDirectory, directory: true);
            var transcriptPrefix = LocalPathRules.Combine(operationDirectory, "transcript");
            input = new FileStream(
                inputPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.Read,
                65_536,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await audio.WriteToAsync(input, cancellationToken).ConfigureAwait(false);
            await input.FlushAsync(cancellationToken).ConfigureAwait(false);
            input.Position = 0;
            return new(WorkspaceStatus.Ready,
                new EphemeralLocalSttWorkspace(
                    operationDirectory,
                    inputPath,
                    transcriptPrefix,
                    input,
                    pathInspector));
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

        WorkspaceCreateResult CreationFailure(WorkspaceStatus status) =>
            new(TryCleanupFailedCreation(input, inputPath, operationDirectory)
                ? status
                : WorkspaceStatus.CleanupFailed);
    }

    private static bool IsDiskFull(IOException error)
    {
        var code = error.HResult & 0xffff;
        return code is 0x27 or 0x70;
    }

    internal bool TryCleanupFailedCreation(
        FileStream? input,
        string inputPath,
        string directory)
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
                File.Delete(inputPath);
            }
            if (Directory.Exists(directory))
            {
                pathInspector.AssertSafeExisting(directory, directory: true);
                if (Directory.EnumerateFileSystemEntries(directory).Any())
                    return false;
                Directory.Delete(directory);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or LocalPathException)
        {
            return false;
        }
        return clean && !File.Exists(inputPath) && !Directory.Exists(directory);
    }
}

internal sealed class EphemeralLocalSttWorkspace : ILocalSttWorkspace
{
    private readonly string operationDirectory;
    private readonly FileStream input;
    private readonly ILocalPathInspector pathInspector;
    private int cleaned;

    public string AudioPath { get; }
    public string TranscriptPrefixPath { get; }
    public string WorkingDirectory => operationDirectory;

    internal EphemeralLocalSttWorkspace(
        string operationDirectory,
        string audioPath,
        string transcriptPrefixPath,
        FileStream input,
        ILocalPathInspector pathInspector)
    {
        this.operationDirectory = operationDirectory;
        AudioPath = audioPath;
        TranscriptPrefixPath = transcriptPrefixPath;
        this.input = input;
        this.pathInspector = pathInspector;
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
            if (stream.Length > LocalSttPackageManifest.MaximumTranscriptBytes)
                return new(WorkspaceStatus.OutputLimit);
            var bytes = new byte[LocalSttPackageManifest.MaximumTranscriptBytes + 1];
            var length = 0;
            while (length < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(length), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;
                length += read;
            }
            if (length > LocalSttPackageManifest.MaximumTranscriptBytes)
                return new(WorkspaceStatus.OutputLimit);
            return new(WorkspaceStatus.Ready, bytes[..length]);
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

    public Task<WorkspaceStatus> CleanupAsync()
    {
        if (Interlocked.Exchange(ref cleaned, 1) != 0)
            return Task.FromResult(WorkspaceStatus.Ready);
        try
        {
            input.Dispose();
            if (File.Exists(AudioPath))
                File.Delete(AudioPath);
            if (Directory.Exists(operationDirectory))
            {
                var entries = Directory.EnumerateFileSystemEntries(operationDirectory).ToArray();
                if (entries.Length > 8)
                    return Task.FromResult(WorkspaceStatus.IoFailure);
                foreach (var path in entries)
                {
                    pathInspector.AssertSafeExisting(path, directory: false);
                    File.Delete(path);
                }
                Directory.Delete(operationDirectory);
            }
            return Task.FromResult(WorkspaceStatus.Ready);
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult(WorkspaceStatus.AccessDenied);
        }
        catch (LocalPathException)
        {
            return Task.FromResult(WorkspaceStatus.UnsafePath);
        }
        catch (IOException)
        {
            return Task.FromResult(WorkspaceStatus.IoFailure);
        }
    }
}
