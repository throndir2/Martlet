using System.Diagnostics;
using Martlet.Core.Processes;

namespace Martlet.Core.Tests;

public sealed class ProcessRunnerTests
{
    private static ProcessStartInfo Shell(string windows, string unix)
    {
        var start = OperatingSystem.IsWindows() ? new ProcessStartInfo("powershell.exe") : new ProcessStartInfo("/bin/sh");
        if (OperatingSystem.IsWindows())
        {
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-Command");
            start.ArgumentList.Add(windows);
        }
        else
        {
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(unix);
        }
        return start;
    }

    [Fact]
    public async Task AChildThatFillsStandardErrorFinishesAndBothStreamsAreRead()
    {
        var start = Shell(
            "[Console]::Error.Write('x' * 2000000); [Console]::Out.Write('done'); exit 3",
            "head -c 2000000 /dev/zero | tr '\\0' x >&2; printf done; exit 3");

        var result = await ProcessRunner.RunAsync(start, TimeSpan.FromSeconds(60));

        Assert.False(result.TimedOut);
        Assert.Equal(3, result.ExitCode);
        Assert.Equal("done", result.StandardOutput.Trim());
        Assert.Equal(2_000_000, result.StandardError.Count(c => c == 'x'));
    }

    [Fact]
    public async Task ALongChildIsKilledAtTheTimeout()
    {
        var start = Shell("[Console]::Out.WriteLine('started'); Start-Sleep -Seconds 60", "echo started; sleep 60");
        var clock = Stopwatch.StartNew();

        var result = await ProcessRunner.RunAsync(start, TimeSpan.FromSeconds(3));

        Assert.True(result.TimedOut);
        Assert.Equal(-1, result.ExitCode);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"took {clock.Elapsed}");
    }

    [Fact]
    public async Task CancellingKillsTheChildAndThrows()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var start = Shell("Start-Sleep -Seconds 60", "sleep 60");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProcessRunner.RunAsync(start, TimeSpan.FromSeconds(60), cancel.Token));
    }

    [Fact]
    public void StandardInputIsWrittenAndClosed()
    {
        var start = Shell("[Console]::In.ReadToEnd().ToUpperInvariant()", "tr a-z A-Z");

        var result = ProcessRunner.Run(start, TimeSpan.FromSeconds(60), "secret header\n");

        Assert.False(result.TimedOut);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("SECRET HEADER", result.StandardOutput.Trim());
    }
}
