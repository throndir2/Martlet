using System.Collections.Immutable;

namespace Martlet.LocalStt;

public enum LocalSttProcessStartStatus
{
    Started,
    Missing,
    AccessDenied,
    Unsupported,
    Failed,
    CleanupFailed
}

public enum LocalSttProcessCompletionStatus
{
    Exited,
    OutputLimit,
    PipeFailure
}

internal sealed class LocalSttProcessStartRequest
{
    public string ExecutablePath { get; }
    public string WorkingDirectory { get; }
    public ImmutableArray<string> Arguments { get; }
    public ImmutableDictionary<string, string> Environment { get; }
    public int MaximumStandardOutputBytes { get; }
    public int MaximumStandardErrorBytes { get; }
    public bool CloseStandardInput { get; }

    internal LocalSttProcessStartRequest(
        string executablePath,
        string workingDirectory,
        IEnumerable<string> arguments,
        IEnumerable<KeyValuePair<string, string>> environment,
        int maximumStandardOutputBytes,
        int maximumStandardErrorBytes,
        bool closeStandardInput)
    {
        ExecutablePath = executablePath;
        WorkingDirectory = workingDirectory;
        Arguments = [.. arguments];
        Environment = environment.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);
        MaximumStandardOutputBytes = maximumStandardOutputBytes;
        MaximumStandardErrorBytes = maximumStandardErrorBytes;
        CloseStandardInput = closeStandardInput;
    }
}

public sealed record LocalSttProcessCompletion(
    LocalSttProcessCompletionStatus Status,
    bool TreeExited,
    int? ExitCode,
    ReadOnlyMemory<byte> StandardOutput,
    ReadOnlyMemory<byte> StandardError);

internal sealed record LocalSttProcessStartResult(
    LocalSttProcessStartStatus Status,
    ILocalSttProcess? Process = null);

internal interface ILocalSttProcessRunner
{
    LocalSttProcessStartResult Start(LocalSttProcessStartRequest request);
}

internal interface ILocalSttProcess : IAsyncDisposable
{
    int Id { get; }
    Task<LocalSttProcessCompletion> Completion { get; }
    ValueTask<bool> KillTreeAsync(TimeSpan timeout);
}

internal static class WhisperCliPolicy
{
    internal static LocalSttProcessStartRequest Create(
        VerifiedLocalSttPackage package,
        ILocalSttWorkspace workspace)
    {
        var systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrWhiteSpace(systemRoot) || !Path.IsPathFullyQualified(systemRoot))
            throw new LocalSttContractException(LocalSttFailureCode.ProcessStartFailed);

        string[] arguments =
        [
            "--model", package.ModelPath,
            "--file", workspace.AudioPath,
            "--language", package.Language,
            "--threads", "4",
            "--processors", "1",
            "--no-gpu",
            "--no-timestamps",
            "--output-txt",
            "--output-file", workspace.TranscriptPrefixPath,
            "--no-prints"
        ];
        KeyValuePair<string, string>[] environment =
        [
            new("PATH", package.RuntimeDirectory),
            new("SYSTEMROOT", systemRoot),
            new("TEMP", workspace.WorkingDirectory),
            new("TMP", workspace.WorkingDirectory),
            new("LANG", "C"),
            new("LC_ALL", "C"),
            new("OMP_NUM_THREADS", "4")
        ];
        return new(
            package.ExecutablePath,
            package.RuntimeDirectory,
            arguments,
            environment,
            LocalSttPackageManifest.MaximumStandardOutputBytes,
            LocalSttPackageManifest.MaximumStandardErrorBytes,
            closeStandardInput: true);
    }
}
