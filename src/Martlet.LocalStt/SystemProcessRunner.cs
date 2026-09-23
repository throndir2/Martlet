using System.ComponentModel;
using System.Diagnostics;

namespace Martlet.LocalStt;

public sealed class SystemLocalSttProcessRunner : ILocalSttProcessRunner
{
    public SystemLocalSttProcessRunner()
    {
    }

    public LocalSttProcessStartResult Start(LocalSttProcessStartRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!OperatingSystem.IsWindows())
            return new(LocalSttProcessStartStatus.Unsupported);
        Process? process = null;
        WindowsProcessJob? job = null;
        var started = false;
        var assigned = false;
        try
        {
            job = WindowsProcessJob.Create();
            process = new Process { StartInfo = BuildStartInfo(request) };
            if (!process.Start())
            {
                process.Dispose();
                job.Dispose();
                return new(LocalSttProcessStartStatus.Failed);
            }
            started = true;
            job.Assign(process);
            assigned = true;
            if (request.CloseStandardInput)
                process.StandardInput.Close();
            return new(LocalSttProcessStartStatus.Started,
                new SystemLocalSttProcess(
                    process,
                    job,
                    request.MaximumStandardOutputBytes,
                    request.MaximumStandardErrorBytes));
        }
        catch (Win32Exception error)
        {
            if (!CleanupFailedStart(process, job, started, assigned))
                return new(LocalSttProcessStartStatus.CleanupFailed);
            return new(error.NativeErrorCode switch
            {
                2 or 3 => LocalSttProcessStartStatus.Missing,
                5 or 13 => LocalSttProcessStartStatus.AccessDenied,
                8 or 193 => LocalSttProcessStartStatus.Unsupported,
                _ => LocalSttProcessStartStatus.Failed
            });
        }
        catch (Exception error) when (error is InvalidOperationException or IOException)
        {
            if (!CleanupFailedStart(process, job, started, assigned))
                return new(LocalSttProcessStartStatus.CleanupFailed);
            return new(LocalSttProcessStartStatus.Failed);
        }
    }

    internal static ProcessStartInfo BuildStartInfo(LocalSttProcessStartRequest request)
    {
        if (!Path.IsPathFullyQualified(request.ExecutablePath) ||
            !Path.IsPathFullyQualified(request.WorkingDirectory) ||
            request.Arguments.IsDefaultOrEmpty ||
            request.Arguments.Length > 32 ||
            request.Arguments.Any(argument => argument.Length > 2048 || argument.Contains('\0')) ||
            request.Environment.Count is < 1 or > 8 ||
            request.MaximumStandardOutputBytes != LocalSttPackageManifest.MaximumStandardOutputBytes ||
            request.MaximumStandardErrorBytes != LocalSttPackageManifest.MaximumStandardErrorBytes ||
            !request.CloseStandardInput)
            throw new InvalidOperationException("Invalid local STT process request.");
        var info = new ProcessStartInfo
        {
            FileName = request.ExecutablePath,
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            ErrorDialog = false,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        info.Environment.Clear();
        foreach (var pair in request.Environment)
            info.Environment.Add(pair.Key, pair.Value);
        foreach (var argument in request.Arguments)
            info.ArgumentList.Add(argument);
        return info;
    }

    private static bool CleanupFailedStart(
        Process? process,
        WindowsProcessJob? job,
        bool started,
        bool assigned)
    {
        var cleaned = !started;
        try
        {
            if (started && assigned && job is not null)
                cleaned = job.TerminateAndWaitAsync(LocalSttPackageManifest.ProcessCleanupTimeout)
                    .AsTask().GetAwaiter().GetResult();
            else if (started && process is not null)
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                process.WaitForExit((int)LocalSttPackageManifest.ProcessCleanupTimeout.TotalMilliseconds);
                cleaned = process.HasExited;
            }
        }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception or
            AggregateException or NotSupportedException or IOException)
        {
            cleaned = HasExited(process);
        }
        finally
        {
            job?.Dispose();
            process?.Dispose();
        }
        return cleaned;
    }

    private static bool HasExited(Process? process)
    {
        try
        {
            return process is not null && process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}

internal sealed class SystemLocalSttProcess : ILocalSttProcess
{
    private readonly Process process;
    private readonly WindowsProcessJob job;
    private readonly int id;
    private readonly CancellationTokenSource reads = new();
    private readonly Task<byte[]> standardOutput;
    private readonly Task<byte[]> standardError;
    private readonly Task exited;
    private readonly TaskCompletionSource outputLimit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int disposed;

    public int Id => id;
    public Task<LocalSttProcessCompletion> Completion { get; }

    internal SystemLocalSttProcess(
        Process process,
        WindowsProcessJob job,
        int maximumStandardOutputBytes,
        int maximumStandardErrorBytes)
    {
        this.process = process;
        this.job = job;
        id = process.Id;
        standardOutput = ReadCappedAsync(
            process.StandardOutput.BaseStream,
            maximumStandardOutputBytes,
            outputLimit,
            reads.Token);
        standardError = ReadCappedAsync(
            process.StandardError.BaseStream,
            maximumStandardErrorBytes,
            outputLimit,
            reads.Token);
        exited = process.WaitForExitAsync(CancellationToken.None);
        Completion = ObserveAsync();
    }

    private async Task<LocalSttProcessCompletion> ObserveAsync()
    {
        try
        {
            var first = await Task.WhenAny(exited, outputLimit.Task).ConfigureAwait(false);
            if (first == outputLimit.Task)
                return new(
                    LocalSttProcessCompletionStatus.OutputLimit,
                    TreeExited(),
                    process.HasExited ? process.ExitCode : null,
                    ReadOnlyMemory<byte>.Empty,
                    ReadOnlyMemory<byte>.Empty);
            await exited.ConfigureAwait(false);
            if (!await job.WaitForEmptyAsync(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false))
                return new(
                    LocalSttProcessCompletionStatus.Exited,
                    TreeExited: false,
                    process.ExitCode,
                    ReadOnlyMemory<byte>.Empty,
                    ReadOnlyMemory<byte>.Empty);
            var pipes = Task.WhenAll(standardOutput, standardError);
            first = await Task.WhenAny(pipes, outputLimit.Task).ConfigureAwait(false);
            if (first == outputLimit.Task)
                return new(
                    LocalSttProcessCompletionStatus.OutputLimit,
                    TreeExited: true,
                    process.ExitCode,
                    ReadOnlyMemory<byte>.Empty,
                    ReadOnlyMemory<byte>.Empty);
            await pipes.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            if (outputLimit.Task.IsCompleted)
                return new(
                    LocalSttProcessCompletionStatus.OutputLimit,
                    TreeExited(),
                    process.ExitCode,
                    ReadOnlyMemory<byte>.Empty,
                    ReadOnlyMemory<byte>.Empty);
            return new(
                LocalSttProcessCompletionStatus.Exited,
                TreeExited(),
                process.ExitCode,
                standardOutput.Result,
                standardError.Result);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or TimeoutException)
        {
            return new(
                LocalSttProcessCompletionStatus.PipeFailure,
                TreeExited(),
                process.HasExited ? process.ExitCode : null,
                ReadOnlyMemory<byte>.Empty,
                ReadOnlyMemory<byte>.Empty);
        }
    }

    public async ValueTask<bool> KillTreeAsync(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout > LocalSttPackageManifest.ProcessCleanupTimeout)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        var watch = Stopwatch.StartNew();
        try
        {
            if (!await job.TerminateAndWaitAsync(timeout).ConfigureAwait(false))
                return false;
            var remaining = timeout - watch.Elapsed;
            if (remaining <= TimeSpan.Zero)
                return exited.IsCompleted && job.IsEmpty;
            await exited.WaitAsync(remaining).ConfigureAwait(false);
            await reads.CancelAsync().ConfigureAwait(false);
            try
            {
                remaining = timeout - watch.Elapsed;
                if (remaining <= TimeSpan.Zero)
                    return false;
                await Task.WhenAll(standardOutput, standardError).WaitAsync(remaining).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (reads.IsCancellationRequested)
            {
            }
            catch (IOException)
            {
            }
            return job.IsEmpty;
        }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception or
            IOException or TimeoutException or AggregateException)
        {
            try
            {
                return job.IsEmpty;
            }
            catch (Exception nested) when (nested is InvalidOperationException or Win32Exception)
            {
                return false;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        if (!TreeExited())
            await KillTreeAsync(LocalSttPackageManifest.ProcessCleanupTimeout).ConfigureAwait(false);
        await reads.CancelAsync().ConfigureAwait(false);
        reads.Dispose();
        process.Dispose();
        job.Dispose();
    }

    private static async Task<byte[]> ReadCappedAsync(
        Stream stream,
        int maximum,
        TaskCompletionSource outputLimit,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[maximum + 1];
        var count = 0;
        while (count <= maximum)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(count), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return buffer[..count];
            count += read;
            if (count > maximum)
            {
                outputLimit.TrySetResult();
                return [];
            }
        }
        throw new InvalidOperationException("Unreachable bounded process output state.");
    }

    private bool TreeExited()
    {
        try
        {
            return job.IsEmpty;
        }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }
}
