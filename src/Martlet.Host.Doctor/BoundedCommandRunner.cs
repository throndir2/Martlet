using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Martlet.Host.Doctor;

public enum CommandKind { PackageVersions, NvidiaQuery }
public enum ReadStatus { Success, Missing, PermissionDenied, Unsupported, Timeout, Canceled, Malformed, OutputLimit, IoError, Failed, NotRun }
public sealed record CommandResult(ReadStatus Status, string Output, int? ExitCode, DateTimeOffset StartedAt, double DurationMilliseconds);

public interface ICommandRunner
{
    Task<CommandResult> RunAsync(CommandKind command, CancellationToken cancellationToken);
}

public sealed class BoundedCommandRunner : ICommandRunner
{
    public const int MaximumStdoutBytes = 32768;
    public const int MaximumStderrBytes = 8192;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private readonly SemaphoreSlim slot = new(1, 1);

    internal static ProcessStartInfo ApprovedStartInfo(CommandKind kind)
    {
        var info = new ProcessStartInfo
        {
            FileName = kind switch
            {
                CommandKind.PackageVersions => "/usr/bin/dpkg-query",
                CommandKind.NvidiaQuery => "/usr/bin/nvidia-smi",
                _ => throw new ArgumentOutOfRangeException(nameof(kind))
            },
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            WorkingDirectory = "/"
        };
        info.Environment.Clear();
        info.Environment["PATH"] = "/usr/bin:/bin";
        info.Environment["LC_ALL"] = "C";
        info.Environment["LANG"] = "C";
        info.Environment["HOME"] = "/";
        string[] arguments = kind == CommandKind.PackageVersions
            ? ["--admindir=/var/lib/dpkg", "--show", "--showformat=${binary:Package}\\t${Version}\\t${db:Status-Status}\\n",
                "docker-ce", "docker.io", "docker-compose-plugin", "docker-compose-v2", "nvidia-container-toolkit", "nvidia-container-toolkit-base"]
            : ["--query-gpu=name,driver_version,memory.total", "--format=csv,noheader,nounits"];
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return info;
    }

    public async Task<CommandResult> RunAsync(CommandKind command, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(command)) throw new ArgumentOutOfRangeException(nameof(command));
        if (cancellationToken.IsCancellationRequested)
            return new(ReadStatus.Canceled, "", null, DateTimeOffset.UtcNow, 0);
        if (!OperatingSystem.IsLinux())
            return new(ReadStatus.Unsupported, "", null, DateTimeOffset.UtcNow, 0);
        await slot.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Neither PATH nor a report/fixture string can select the executable or arguments.
            return await RunOwnedAsync(ApprovedStartInfo(command), Timeout, cancellationToken).ConfigureAwait(false);
        }
        finally { slot.Release(); }
    }

    internal static async Task<CommandResult> RunOwnedAsync(ProcessStartInfo info, TimeSpan timeout, CancellationToken original,
        Action<int>? started = null)
    {
        if (timeout <= TimeSpan.Zero || timeout > Timeout) throw new ArgumentOutOfRangeException(nameof(timeout));
        var start = DateTimeOffset.UtcNow;
        var watch = Stopwatch.StartNew();
        CommandResult Result(ReadStatus status, string text = "", int? exit = null) =>
            new(status, text, exit, start, watch.Elapsed.TotalMilliseconds);
        if (original.IsCancellationRequested) return Result(ReadStatus.Canceled);
        using var process = new Process { StartInfo = info };
        try
        {
            if (original.IsCancellationRequested) return Result(ReadStatus.Canceled);
            if (!process.Start()) return Result(ReadStatus.Failed);
        }
        catch (Win32Exception ex)
        {
            return Result(ex.NativeErrorCode switch
            {
                2 or 3 => ReadStatus.Missing,
                5 or 13 => ReadStatus.PermissionDenied,
                8 or 193 => ReadStatus.Unsupported,
                _ => ReadStatus.Failed
            });
        }
        using var timer = new CancellationTokenSource(timeout);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(original, timer.Token);
        using var reads = new CancellationTokenSource();
        var overflow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interrupted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = stop.Token.Register(() => interrupted.TrySetResult());
        process.StandardInput.Close();
        var stdout = ReadCappedAsync(process.StandardOutput.BaseStream, MaximumStdoutBytes, overflow, reads.Token);
        var stderr = ReadCappedAsync(process.StandardError.BaseStream, MaximumStderrBytes, overflow, reads.Token);
        var exited = process.WaitForExitAsync(CancellationToken.None);
        var completed = Task.WhenAll(exited, stdout, stderr);
        async Task<CommandResult> ObserveAsync()
        {
            await Task.WhenAny(completed, interrupted.Task, overflow.Task).ConfigureAwait(false);
            var status = original.IsCancellationRequested ? ReadStatus.Canceled
                : timer.IsCancellationRequested || watch.Elapsed >= timeout ? ReadStatus.Timeout
                : overflow.Task.IsCompleted ? ReadStatus.OutputLimit : ReadStatus.Success;
            if (status != ReadStatus.Success) return Result(status);
            await completed.ConfigureAwait(false);
            // Original token and monotonic time are authoritative even if linked callbacks are delayed.
            if (original.IsCancellationRequested) return Result(ReadStatus.Canceled);
            if (timer.IsCancellationRequested || watch.Elapsed >= timeout) return Result(ReadStatus.Timeout);
            if (overflow.Task.IsCompleted) return Result(ReadStatus.OutputLimit);
            string output;
            try { output = new UTF8Encoding(false, true).GetString(stdout.Result); }
            catch (DecoderFallbackException) { return Result(ReadStatus.Malformed); }
            if (original.IsCancellationRequested) return Result(ReadStatus.Canceled);
            return Result(ReadStatus.Success, output, process.ExitCode);
        }
        CommandResult result;
        try
        {
            started?.Invoke(process.Id);
            result = await ObserveAsync().ConfigureAwait(false);
        }
        catch (IOException) { result = Result(ReadStatus.IoError); }
        finally
        {
            // Never return a timeout while our child is still running. Only this spawned tree is stopped.
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
                await exited.ConfigureAwait(false);
            }
            await reads.CancelAsync().ConfigureAwait(false);
            try { await completed.ConfigureAwait(false); }
            catch (OperationCanceledException) when (reads.IsCancellationRequested) { }
            catch (IOException) { /* Pipes may close during termination; no partial response is accepted. */ }
        }
        if (original.IsCancellationRequested) result = result with { Status = ReadStatus.Canceled, Output = "", ExitCode = null };
        return result with { DurationMilliseconds = watch.Elapsed.TotalMilliseconds };
    }

    private static async Task<byte[]> ReadCappedAsync(Stream stream, int maximum,
        TaskCompletionSource overflow, CancellationToken cancellationToken)
    {
        var buffer = new byte[maximum + 1];
        var count = 0;
        while (count <= maximum)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(count), cancellationToken).ConfigureAwait(false);
            if (read == 0) return buffer[..count];
            count += read;
            if (count > maximum)
            {
                overflow.TrySetResult();
                return [];
            }
        }
        throw new InvalidOperationException("Unreachable bounded read state.");
    }
}
