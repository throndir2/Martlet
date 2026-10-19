using System.Diagnostics;

namespace Martlet.Core.Processes;

/// <summary>What a finished child process gave back. <see cref="ExitCode"/> is -1 when <see cref="TimedOut"/> is true.</summary>
public sealed record ProcessRunResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut);

/// <summary>Runs a child process to its end without a pipe deadlock: it reads standard output and standard error at the
/// same time, stops at a time limit and then kills the whole process tree.</summary>
public static class ProcessRunner
{
    /// <summary>How long a kill waits for the pipes to close (a detached grandchild can hold them open).</summary>
    private static readonly TimeSpan DrainLimit = TimeSpan.FromSeconds(2);

    /// <summary>Starts <paramref name="info"/> (no shell, both output streams redirected and read), writes
    /// <paramref name="standardInput"/> and closes the input when it is not null, and waits at most
    /// <paramref name="timeout"/> for the exit and the output. On a timeout it kills the process tree and returns
    /// <see cref="ProcessRunResult.TimedOut"/>; when <paramref name="cancellationToken"/> is cancelled it kills the tree
    /// and throws <see cref="OperationCanceledException"/>. Start errors (for example a missing file) are not caught.</summary>
    public static async Task<ProcessRunResult> RunAsync(
        ProcessStartInfo info, TimeSpan timeout, CancellationToken cancellationToken = default, string? standardInput = null)
    {
        ArgumentNullException.ThrowIfNull(info);
        cancellationToken.ThrowIfCancellationRequested();
        info.UseShellExecute = false;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.RedirectStandardInput = standardInput is not null;
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"{info.FileName} did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            if (standardInput is not null)
            {
                try
                {
                    await process.StandardInput.WriteAsync(standardInput.AsMemory(), limit.Token).ConfigureAwait(false);
                    process.StandardInput.Close();
                }
                catch (IOException) { } // The child closed its input early; its exit code tells the rest.
            }
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            await Task.WhenAll(output, error).WaitAsync(limit.Token).ConfigureAwait(false);
            return new ProcessRunResult(process.ExitCode, output.Result, error.Result, TimedOut: false);
        }
        catch (OperationCanceledException) when (limit.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception failure) when (failure is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            try { await Task.WhenAll(output, error).WaitAsync(DrainLimit).ConfigureAwait(false); }
            catch (Exception) { Observe(output); Observe(error); }
            cancellationToken.ThrowIfCancellationRequested();
            return new ProcessRunResult(-1, Text(output), Text(error), TimedOut: true);
        }
    }

    /// <summary>The same as <see cref="RunAsync"/> for callers that cannot await.</summary>
    public static ProcessRunResult Run(ProcessStartInfo info, TimeSpan timeout, string? standardInput = null) =>
        RunAsync(info, timeout, CancellationToken.None, standardInput).GetAwaiter().GetResult();

    private static string Text(Task<string> read) => read.IsCompletedSuccessfully ? read.Result : "";

    private static void Observe(Task task) =>
        task.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
}
