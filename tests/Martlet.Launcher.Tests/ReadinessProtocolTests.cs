using Martlet.Readiness;
using Martlet.Updates;

namespace Martlet.Launcher.Tests;

[Collection(LauncherProcessCollection.Name)]
public sealed class ReadinessProtocolTests
{
    [Fact]
    public void ReporterExposesOnlyFixedOneUseOutcomes()
    {
        var type = typeof(DesktopReadinessReporter);

        Assert.Empty(type.GetConstructors());
        Assert.All(type.GetProperties(), property => Assert.Null(property.SetMethod));
        var reports = type.GetMethods()
            .Where(method => method.DeclaringType == type &&
                method.Name.StartsWith("Report", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(
            new[]
            {
                "ReportDegradedAsync",
                "ReportFailedAsync",
                "ReportInitializedAsync"
            },
            reports.Select(method => method.Name).Order());
        Assert.All(reports, method => Assert.DoesNotContain(
            method.GetParameters(),
            parameter => parameter.ParameterType is { } parameterType &&
                (parameterType == typeof(bool) ||
                 parameterType == typeof(string) ||
                 parameterType.IsEnum)));
        Assert.DoesNotContain(
            typeof(LocalActivationEngine).GetConstructors(),
            constructor => constructor.GetParameters().Any(parameter =>
                parameter.ParameterType.Name.Contains(
                    "ReadinessProbe", StringComparison.Ordinal)));
        Assert.DoesNotContain(
            typeof(LocalSelectionEngine).GetMethods(),
            method => method.Name == "PrepareRetainedActivationRollback");
        Assert.NotNull(typeof(LauncherActivationComposition).GetMethod("Create"));
    }

    [Fact]
    public async Task OperatingSystemProcessPathMustMatchExpectedApphost()
    {
        var root = Directory.CreateTempSubdirectory(
            "Martlet.Readiness.Tests-").FullName;
        var actual = Path.Combine(root, "Martlet.Desktop.exe");
        try
        {
            CopyFixture(root);
            File.WriteAllText(Path.Combine(root, "fixture-mode.txt"),
                "activation=initialized-exit\nlaunch=initialized-exit\n");
            var wrong = Path.Combine(root, "Wrong.Desktop.exe");
            File.Copy(actual, wrong);
            var binding = new PrivateReadinessBinding(
                DesktopReadinessPurpose.DesktopLaunch,
                "0.2.0.0",
                Guid.NewGuid(),
                new string('a', 64),
                new string('b', 64),
                new string('c', 64),
                wrong);
            await using var server = new PrivateReadinessServer(
                binding, TimeSpan.FromSeconds(2));
            using var process = OwnedWindowsProcess.Start(
                actual,
                root,
                MinimalProcessEnvironment.Create(root, server.Environment));

            var result = await server.WaitAsync(
                process.ProcessId,
                process.ExitToken,
                process.ReadExitCode,
                CancellationToken.None);

            Assert.Equal(
                PrivateReadinessFailure.WrongProcessPath, result.Failure);
            Assert.True(await process.TerminateAndWaitAsync(
                LauncherSupport.ProcessCleanupTimeout));
        }
        finally
        {
            await WaitUntilAsync(() => AllFilesOpenExclusive(root));
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("win-arm64", "0.2.0.0", 3, LauncherFailure.IncompatibleRid)]
    [InlineData("win-x64", "1.0.0.0", 3, LauncherFailure.IncompatibleVersion)]
    [InlineData("win-x64", "0.2.0.0", 99, LauncherFailure.IncompatibleSettings)]
    public void LauncherEnforcesRidVersionAndSettingsBounds(
        string rid,
        string version,
        int settingsSchema,
        LauncherFailure expected)
    {
        var root = Path.GetFullPath(
            Path.Combine(Path.GetTempPath(), "martlet-launcher-bounds"));
        var target = new LaunchTarget(
            2,
            Guid.NewGuid(),
            null,
            Guid.NewGuid(),
            root,
            root,
            Path.Combine(root, "payload"),
            Path.Combine(root, "payload", "Desktop", "Martlet.Desktop.exe"),
            "Desktop/Martlet.Desktop.exe",
            version,
            rid,
            new string('a', 64),
            new string('b', 64),
            new string('c', 64),
            1,
            2,
            new string('d', 64),
            settingsSchema,
            1,
            3,
            null);

        var error = Assert.Throws<LauncherException>(
            () => LauncherSupport.Validate(target));

        Assert.Equal(expected, error.Failure);
    }

    [Fact]
    public async Task FailureBeforeJobAssignmentTerminatesSuspendedApphost()
    {
        var root = Directory.CreateTempSubdirectory(
            "Martlet.ProcessStart.Tests-").FullName;
        var executable = Path.Combine(root, "Martlet.Desktop.exe");
        var processId = 0;
        try
        {
            CopyFixture(root);
            File.WriteAllText(Path.Combine(root, "fixture-mode.txt"),
                "activation=silent-hang\nlaunch=silent-hang\n");

            var error = Assert.Throws<LauncherException>(() =>
                OwnedWindowsProcess.Start(
                    executable,
                    root,
                    MinimalProcessEnvironment.Create(
                        root, new Dictionary<string, string>()),
                    (_, id) =>
                    {
                        processId = id;
                        Thread.Sleep(100);
                        Assert.False(File.Exists(Path.Combine(root, "fixture-started.pid")));
                        throw new IOException("Controlled pre-assignment failure");
                    }));

            Assert.Equal(LauncherFailure.ProcessStartFailed, error.Failure);
            await WaitUntilAsync(() => !ProcessExists(processId));
            Assert.True(CanOpenExclusive(executable));
        }
        finally
        {
            await WaitUntilAsync(() => AllFilesOpenExclusive(root));
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FinalPreResumeFenceRetiresSuspendedChildWithoutExecuting(bool cancelled)
    {
        var root = Directory.CreateTempSubdirectory("Martlet.Resume.Tests-").FullName;
        using var cancellation = new CancellationTokenSource();
        var checks = 0;
        var pid = 0;
        try
        {
            CopyFixture(root);
            var error = Record.Exception(() => OwnedWindowsProcess.Start(
                Path.Combine(root, "Martlet.Desktop.exe"), root,
                MinimalProcessEnvironment.Create(root, new Dictionary<string, string>()),
                io: (_, id) => { pid = id; },
                requireAuthorization: () =>
                {
                    if (++checks != 2) return;
                    if (!cancelled) throw new LauncherException(LauncherFailure.Conflict);
                    cancellation.Cancel();
                    cancellation.Token.ThrowIfCancellationRequested();
                }));
            Assert.NotNull(error);
            if (cancelled) Assert.IsType<OperationCanceledException>(error);
            else Assert.IsType<LauncherException>(error);
            Assert.Equal(2, checks);
            Assert.True(pid > 0);
            Assert.False(ProcessExists(pid));
            Assert.False(File.Exists(Path.Combine(root, "fixture-started.pid")));
            Assert.True(AllFilesOpenExclusive(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void CopyFixture(string destination)
    {
        var source = Path.Combine(AppContext.BaseDirectory, "fixture-child");
        foreach (var file in Directory.GetFiles(source))
        {
            if (file.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase))
                continue;
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }
    }

    private static bool CanOpenExclusive(string path)
    {
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static bool AllFilesOpenExclusive(string directory)
    {
        if (!Directory.Exists(directory)) return true;
        foreach (var path in Directory.GetFiles(
                     directory, "*", SearchOption.AllDirectories))
        {
            try
            {
                using var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.None);
            }
            catch (Exception error) when (
                error is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
        return true;
    }

    private static bool ProcessExists(int processId)
    {
        if (processId <= 0) return false;
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeout = System.Diagnostics.Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(5))
        {
            if (condition()) return;
            await Task.Delay(20);
        }
        Assert.True(condition());
    }
}
