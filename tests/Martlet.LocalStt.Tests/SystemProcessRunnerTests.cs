using System.Diagnostics;
using Martlet.LocalStt;

namespace Martlet.LocalStt.Tests;

[Collection("Local STT process fixtures")]
public sealed class SystemProcessRunnerTests : IDisposable
{
    private readonly string root = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "Martlet.LocalStt.ProcessTests", Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public async Task Real_runner_collects_bounded_inert_fixture_output_without_a_shell()
    {
        var runner = new SystemLocalSttProcessRunner();
        var request = FixtureRequest("echo");
        var info = SystemLocalSttProcessRunner.BuildStartInfo(request);
        Assert.False(info.UseShellExecute);
        Assert.True(info.RedirectStandardInput);
        Assert.True(info.RedirectStandardOutput);
        Assert.True(info.RedirectStandardError);
        Assert.Empty(info.Arguments);
        Assert.Equal(request.Arguments, info.ArgumentList.ToArray());
        Assert.Equal(request.Environment.Keys.Order(StringComparer.Ordinal),
            info.Environment.Keys.Order(StringComparer.Ordinal));
        foreach (var pair in request.Environment)
            Assert.Equal(pair.Value, info.Environment[pair.Key]);

        var started = runner.Start(request);
        var process = Assert.IsAssignableFrom<ILocalSttProcess>(started.Process);
        var completed = await process.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(LocalSttProcessCompletionStatus.Exited, completed.Status);
        Assert.True(completed.TreeExited);
        Assert.Equal(0, completed.ExitCode);
        Assert.Equal("synthetic stdout", System.Text.Encoding.UTF8.GetString(completed.StandardOutput.Span));
        Assert.Equal("synthetic stderr", System.Text.Encoding.UTF8.GetString(completed.StandardError.Span));
        await process.DisposeAsync();
        Assert.All(completed.StandardOutput.ToArray(), value => Assert.Equal(0, value));
        Assert.All(completed.StandardError.ToArray(), value => Assert.Equal(0, value));
    }

    [Theory]
    [InlineData("flood-stdout")]
    [InlineData("fast-flood-stdout")]
    [InlineData("flood-stderr")]
    public async Task Pipe_overflow_is_bounded_and_owned_tree_is_stopped(string mode)
    {
        var started = new SystemLocalSttProcessRunner().Start(FixtureRequest(mode));
        var process = Assert.IsAssignableFrom<ILocalSttProcess>(started.Process);

        var completed = await process.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(LocalSttProcessCompletionStatus.OutputLimit, completed.Status);
        Assert.Empty(completed.StandardOutput.ToArray());
        Assert.Empty(completed.StandardError.ToArray());
        Assert.True(await process.KillTreeAsync(TimeSpan.FromSeconds(5)));
        await process.DisposeAsync();
        AssertTerminated(process.Id);
    }

    [Fact]
    public async Task Tree_kill_stops_parent_and_child_but_not_an_unrelated_fixture()
    {
        var pidFile = Path.Combine(root, "child.txt");
        var runner = new SystemLocalSttProcessRunner();
        var owned = Assert.IsAssignableFrom<ILocalSttProcess>(
            runner.Start(FixtureRequest("tree", pidFile)).Process);
        var unrelated = Assert.IsAssignableFrom<ILocalSttProcess>(
            runner.Start(FixtureRequest("sleep")).Process);
        try
        {
            var childPid = await ReadPidFileAsync(pidFile);
            Assert.True(await owned.KillTreeAsync(TimeSpan.FromSeconds(5)));
            AssertTerminated(owned.Id);
            AssertTerminated(childPid);
            Assert.False(unrelated.Completion.IsCompleted);
        }
        finally
        {
            await owned.DisposeAsync();
            await unrelated.KillTreeAsync(TimeSpan.FromSeconds(5));
            await unrelated.DisposeAsync();
            if (File.Exists(pidFile))
                File.Delete(pidFile);
        }
    }

    [Fact]
    public async Task Root_exit_is_not_reported_as_tree_exit_while_an_owned_child_survives()
    {
        var pidFile = Path.Combine(root, "orphan.txt");
        var process = Assert.IsAssignableFrom<ILocalSttProcess>(
            new SystemLocalSttProcessRunner().Start(FixtureRequest("orphan", pidFile)).Process);
        try
        {
            var childPid = await ReadPidFileAsync(pidFile);
            var completed = await process.Completion.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(LocalSttProcessCompletionStatus.Exited, completed.Status);
            Assert.False(completed.TreeExited);
            Assert.True(await process.KillTreeAsync(TimeSpan.FromSeconds(5)));
            AssertTerminated(childPid);
        }
        finally
        {
            await process.DisposeAsync();
            if (File.Exists(pidFile))
                File.Delete(pidFile);
        }
    }

    [Fact]
    public void Assignment_failure_never_runs_the_suspended_fixture_and_cleans_the_root()
    {
        var marker = Path.Combine(root, "never-written.txt");
        var pid = 0;
        var runner = new SystemLocalSttProcessRunner(process =>
        {
            pid = process.Id;
            Assert.False(File.Exists(marker));
            throw new InvalidOperationException("Injected assignment boundary failure.");
        });
        var started = runner.Start(FixtureRequest("marker", marker));
        Assert.Equal(LocalSttProcessStartStatus.Failed, started.Status);
        Assert.Null(started.Process);
        Assert.False(File.Exists(marker));
        AssertTerminated(pid);
    }

    [Fact]
    public async Task Exact_pipe_limits_and_immediate_root_exit_are_drained_without_false_overflow()
    {
        for (var iteration = 0; iteration < 20; iteration++)
        {
            var process = Assert.IsAssignableFrom<ILocalSttProcess>(
                new SystemLocalSttProcessRunner().Start(FixtureRequest("exact-limits")).Process);
            await using (process)
            {
                var completed = await process.Completion.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(LocalSttProcessCompletionStatus.Exited, completed.Status);
                Assert.True(completed.TreeExited);
                Assert.Equal(LocalSttPackageManifest.MaximumStandardOutputBytes, completed.StandardOutput.Length);
                Assert.Equal(LocalSttPackageManifest.MaximumStandardErrorBytes, completed.StandardError.Length);
            }
        }
    }

    [Theory]
    [InlineData("spaces and a trailing slash\\")]
    [InlineData("literal \"quote\" and \\\\\"quoted\"")]
    public async Task Native_launch_preserves_each_argument_exactly(string value)
    {
        var process = Assert.IsAssignableFrom<ILocalSttProcess>(
            new SystemLocalSttProcessRunner().Start(FixtureRequest("arguments", value)).Process);
        await using (process)
        {
            var result = await process.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(value, System.Text.Encoding.UTF8.GetString(result.StandardOutput.Span));
        }
    }

    private LocalSttProcessStartRequest FixtureRequest(string mode, string? argument = null)
    {
        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT")
            ?? throw new InvalidOperationException("Tests require the pinned DOTNET_ROOT.");
        var executable = Path.Combine(dotnetRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        var arguments = new List<string>
        {
            typeof(ProcessFixture).Assembly.Location,
            "--process-fixture",
            mode
        };
        if (argument is not null)
            arguments.Add(argument);
        return new(
            executable,
            root,
            arguments,
            new[]
            {
                new KeyValuePair<string, string>(
                    "SYSTEMROOT",
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows)),
                new KeyValuePair<string, string>("DOTNET_ROOT", dotnetRoot),
                new KeyValuePair<string, string>("TEMP", root),
                new KeyValuePair<string, string>("TMP", root)
            },
            LocalSttPackageManifest.MaximumStandardOutputBytes,
            LocalSttPackageManifest.MaximumStandardErrorBytes,
            closeStandardInput: true);
    }

    private static async Task<int> ReadPidFileAsync(string path)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (File.Exists(path))
                {
                    var text = await File.ReadAllTextAsync(path);
                    if (int.TryParse(text, System.Globalization.NumberStyles.None,
                            System.Globalization.CultureInfo.InvariantCulture, out var pid))
                        return pid;
                }
            }
            catch (IOException)
            {
            }
            await Task.Delay(10);
        }
        throw new TimeoutException("The inert child fixture did not report its PID.");
    }

    private static void AssertTerminated(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            Assert.True(process.HasExited);
        }
        catch (ArgumentException)
        {
        }
    }
}

[CollectionDefinition("Local STT process fixtures", DisableParallelization = true)]
public sealed class ProcessCollection;
