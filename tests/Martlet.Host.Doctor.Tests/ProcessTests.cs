using System.Diagnostics;
using Martlet.Host.Doctor;

namespace Martlet.Host.Doctor.Tests;

[Collection("Owned process fixtures")]
public sealed class ProcessTests
{
    internal static ProcessStartInfo Fixture(string mode)
    {
        var root = Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? throw new InvalidOperationException("Tests require their pinned DOTNET_ROOT.");
        var info = new ProcessStartInfo
        {
            FileName = Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"),
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        info.ArgumentList.Add(typeof(ProcessFixture).Assembly.Location);
        info.ArgumentList.Add("--process-fixture");
        info.ArgumentList.Add(mode);
        return info;
    }

    [Fact]
    public async Task Real_wrapper_collects_bounded_output_and_exit_without_exposing_stderr()
    {
        var success = await BoundedCommandRunner.RunOwnedAsync(Fixture("echo"), TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal(ReadStatus.Success, success.Status);
        Assert.Equal("synthetic response\n", success.Output);
        Assert.Equal(0, success.ExitCode);
        var nonzero = await BoundedCommandRunner.RunOwnedAsync(Fixture("nonzero"), TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal(37, nonzero.ExitCode);
        Assert.DoesNotContain("SECRET_CANARY", nonzero.ToString());
    }

    [Theory]
    [InlineData("flood-stdout", ReadStatus.OutputLimit)]
    [InlineData("flood-stderr", ReadStatus.OutputLimit)]
    [InlineData("invalid-utf8", ReadStatus.Malformed)]
    public async Task Limits_apply_while_reading_both_pipes_and_discard_partial_output(string fixture, ReadStatus status)
    {
        var watch = Stopwatch.StartNew();
        var result = await BoundedCommandRunner.RunOwnedAsync(Fixture(fixture), TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal(status, result.Status);
        Assert.Empty(result.Output);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Timeout_and_cancel_stop_only_their_own_process_before_returning()
    {
        using var unrelated = Process.Start(Fixture("sleep"))!;
        var unrelatedPid = int.Parse(await ReadPid(unrelated), System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            var ownedPid = 0;
            var timed = await BoundedCommandRunner.RunOwnedAsync(Fixture("sleep"), TimeSpan.FromMilliseconds(400), CancellationToken.None, pid => ownedPid = pid);
            Assert.Equal(ReadStatus.Timeout, timed.Status);
            Assert.Empty(timed.Output);
            AssertTerminated(ownedPid);
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
            var canceled = await BoundedCommandRunner.RunOwnedAsync(Fixture("sleep"), TimeSpan.FromSeconds(5), cancel.Token, pid => ownedPid = pid);
            Assert.Equal(ReadStatus.Canceled, canceled.Status);
            Assert.Empty(canceled.Output);
            AssertTerminated(ownedPid);
            Assert.False(unrelated.HasExited);
            Assert.Equal(unrelated.Id, unrelatedPid);
        }
        finally
        {
            if (!unrelated.HasExited) unrelated.Kill(entireProcessTree: true);
            await unrelated.WaitForExitAsync();
        }
    }

    [Fact]
    public async Task Pre_canceled_original_token_never_spawns_even_if_callback_delivery_is_blocked()
    {
        using var cancel = new CancellationTokenSource();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancel.Token.Register(() => { entered.TrySetResult(); release.Wait(); });
        var cancellation = Task.Factory.StartNew(cancel.Cancel, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var info = Fixture("echo");
            info.FileName = Path.Combine(Path.GetTempPath(), "martlet-nonexistent-executable");
            var result = await BoundedCommandRunner.RunOwnedAsync(info, TimeSpan.FromSeconds(5), cancel.Token);
            Assert.Equal(ReadStatus.Canceled, result.Status);
            Assert.Null(result.ExitCode);
        }
        finally { release.Set(); await cancellation; }
    }

    [Fact]
    public async Task Cancellation_wins_over_a_late_success_when_linked_callback_is_blocked()
    {
        for (var i = 0; i < 8; i++)
        {
            using var cancel = new CancellationTokenSource();
            var pending = BoundedCommandRunner.RunOwnedAsync(Fixture("delayed-echo"), TimeSpan.FromSeconds(5), cancel.Token);
            using var release = new ManualResetEventSlim();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = cancel.Token.Register(() => { entered.TrySetResult(); release.Wait(); });
            var cancellation = Task.Factory.StartNew(cancel.Cancel, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var result = await pending;
                Assert.Equal(ReadStatus.Canceled, result.Status);
                Assert.Empty(result.Output);
            }
            finally { release.Set(); await cancellation; }
        }
    }

    [Fact]
    public async Task Missing_binary_has_its_own_safe_status()
    {
        var info = Fixture("echo");
        info.FileName = Path.Combine(Path.GetTempPath(), "martlet-nonexistent-executable");
        var result = await BoundedCommandRunner.RunOwnedAsync(info, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal(ReadStatus.Missing, result.Status);
        Assert.Empty(result.Output);
    }

    private static async Task<string> ReadPid(Process process)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var chars = new char[16];
        var count = await process.StandardOutput.ReadAsync(chars, timeout.Token);
        return new string(chars, 0, count);
    }

    private static void AssertTerminated(int pid)
    {
        Assert.True(pid > 0);
        try
        {
            using var process = Process.GetProcessById(pid);
            Assert.True(process.HasExited);
        }
        catch (ArgumentException) { /* Reaped processes no longer have an OS process record. */ }
    }
}
