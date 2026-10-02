using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Martlet.Desktop;

/// <summary>Runs a program on this PC without any window (no console ever appears) and streams its output, line by line,
/// to a run window. Terminal decorations (colors, carriage-return progress redraws, spinners) are cleaned up first.</summary>
internal static partial class LocalProcess
{
    [GeneratedRegex(@"\x1B\[[0-9;?]*[A-Za-z]|\x1B\][^\x07]*\x07")]
    private static partial Regex AnsiPattern();

    [GeneratedRegex(@"\A[\s\-\\|/]*\z|[█▒░■□▌▐]")]
    private static partial Regex NoisePattern();

    /// <summary>The text worth showing from one output line, without terminal codes; null for blank, spinner and
    /// progress-bar lines (the run window's status line shows progress instead).</summary>
    internal static string? Clean(string? line)
    {
        if (line is null) return null;
        var text = AnsiPattern().Replace(line, "");
        var redraw = text.TrimEnd('\r').LastIndexOf('\r');
        if (redraw >= 0) text = text[(redraw + 1)..];
        text = text.TrimEnd();
        return text.Length == 0 || NoisePattern().IsMatch(text) ? null : text;
    }

    /// <summary>Runs <paramref name="file"/> hidden and returns its exit code. <paramref name="input"/> is written to stdin
    /// first; <paramref name="moreInput"/>, when it completes before the program exits, is written next. Stdin is closed
    /// afterwards. Canceling kills the program and everything it started.</summary>
    internal static async Task<int> RunAsync(string file, IEnumerable<string> args, IProgress<string>? output, CancellationToken token,
        string? input = null, Task<string?>? moreInput = null, string? workingDirectory = null)
    {
        var start = new ProcessStartInfo(file)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        if (workingDirectory is not null) start.WorkingDirectory = workingDirectory;
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = start };
        void Line(string? text)
        {
            if (output is not null && Clean(text) is { } clean) output.Report(clean);
        }
        process.OutputDataReceived += (_, e) => Line(e.Data);
        process.ErrorDataReceived += (_, e) => Line(e.Data);
        try { process.Start(); }
        catch (System.ComponentModel.Win32Exception error)
        {
            throw new InvalidOperationException($"Could not start {Path.GetFileName(file)}: {error.Message}");
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        var feeding = FeedAsync(process.StandardInput, input, moreInput, process.WaitForExitAsync(CancellationToken.None));
        try { await process.WaitForExitAsync(token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            throw;
        }
        await feeding;
        process.WaitForExit();
        return process.ExitCode;
    }

    private static async Task FeedAsync(StreamWriter stdin, string? input, Task<string?>? more, Task exited)
    {
        try
        {
            if (input is not null) { await stdin.WriteAsync(input); await stdin.FlushAsync(); }
            if (more is not null && await Task.WhenAny(more, exited) == more && await more is { } last)
            {
                await stdin.WriteAsync(last);
                await stdin.FlushAsync();
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        finally
        {
            try { stdin.Close(); } catch (Exception error) when (error is IOException or ObjectDisposedException) { }
        }
    }
}
