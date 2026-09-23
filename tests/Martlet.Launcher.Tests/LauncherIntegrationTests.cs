using System.Diagnostics;
using System.Text.Json;
using Martlet.Updates;

namespace Martlet.Launcher.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LauncherProcessCollection
{
    public const string Name = "Launcher process tests";
}

[Collection(LauncherProcessCollection.Name)]
public sealed class LauncherIntegrationTests
{
    [Fact]
    public async Task ExactVerifiedApphostLaunchesWithMinimalEnvironmentAndRetainedEvidence()
    {
        using var fixture = Active();
        var receipt = fixture.Activation!.Inspect();
        var launcher = new LocalDesktopLauncher(
            fixture.Activation, fixture.LauncherRoot, TimeSpan.FromSeconds(2));

        var result = await launcher.LaunchActiveAsync(receipt.Revision);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("0.2.0.0", result.Version);
        Assert.Equal(receipt.Current!.Version, result.Version);
        var attempts = fixture.Attempts();
        Assert.Equal(2, attempts.Length);
        var launch = attempts.Single(path =>
            File.ReadAllText(Path.Combine(path, "intent-v1.json"))
                .Contains("\"kind\":\"DesktopLaunch\"", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(launch, "readiness-v1.json")));
        Assert.True(File.Exists(Path.Combine(launch, "terminal-v1.json")));
        var evidence = string.Concat(Directory.GetFiles(launch, "*.json")
            .Select(File.ReadAllText));
        Assert.DoesNotContain(fixture.Root, evidence, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("nonce", evidence, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pipe", evidence, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RawActiveJsonCannotAuthorizeLaunch()
    {
        using var fixture = Active();
        var receipt = fixture.Activation!.Inspect();
        var attempts = fixture.Attempts().Length;
        File.AppendAllText(Path.Combine(fixture.ActivationRoot, "active.json"), " ");
        var launcher = new LocalDesktopLauncher(
            fixture.Activation, fixture.LauncherRoot, TimeSpan.FromSeconds(1));

        var error = await Assert.ThrowsAsync<LauncherException>(
            () => launcher.LaunchActiveAsync(receipt.Revision));

        Assert.Equal(LauncherFailure.InvalidActivation, error.Failure);
        Assert.Equal(attempts, fixture.Attempts().Length);
        Assert.DoesNotContain(fixture.Root, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("wrong-nonce", LauncherFailure.ReadinessRejected)]
    [InlineData("wrong-version", LauncherFailure.ReadinessRejected)]
    [InlineData("wrong-profile", LauncherFailure.ReadinessRejected)]
    [InlineData("wrong-payload", LauncherFailure.ReadinessRejected)]
    [InlineData("wrong-process", LauncherFailure.ReadinessRejected)]
    [InlineData("raw-format", LauncherFailure.ReadinessRejected)]
    [InlineData("raw-protocol", LauncherFailure.ReadinessRejected)]
    [InlineData("raw-purpose", LauncherFailure.ReadinessRejected)]
    [InlineData("raw-settings", LauncherFailure.ReadinessRejected)]
    [InlineData("raw-executable", LauncherFailure.ReadinessRejected)]
    [InlineData("raw-deadline", LauncherFailure.ReadinessTimedOut)]
    [InlineData("raw-state", LauncherFailure.ReadinessRejected)]
    [InlineData("raw-duplicate", LauncherFailure.ReadinessRejected)]
    [InlineData("raw-unknown", LauncherFailure.ReadinessRejected)]
    [InlineData("raw-noncanonical", LauncherFailure.ReadinessRejected)]
    [InlineData("raw-oversize", LauncherFailure.ReadinessRejected)]
    [InlineData("raw-zero", LauncherFailure.ReadinessRejected)]
    [InlineData("raw-truncated", LauncherFailure.Unavailable)]
    [InlineData("degraded", LauncherFailure.ReadinessRejected)]
    [InlineData("failed", LauncherFailure.ReadinessRejected)]
    [InlineData("crash", LauncherFailure.ProcessExitedBeforeReadiness)]
    [InlineData("late", LauncherFailure.ReadinessTimedOut)]
    public async Task InvalidOrLateHandshakeFailsClosed(
        string mode,
        LauncherFailure expected)
    {
        using var fixture = Active(mode, TimeSpan.FromMilliseconds(400));
        var receipt = fixture.Activation!.Inspect();
        var launcher = new LocalDesktopLauncher(
            fixture.Activation, fixture.LauncherRoot,
            TimeSpan.FromMilliseconds(400));

        var error = await Assert.ThrowsAsync<LauncherException>(
            () => launcher.LaunchActiveAsync(receipt.Revision));

        Assert.Equal(expected, error.Failure);
        Assert.NotNull(error.AttemptId);
        Assert.DoesNotContain(fixture.Root, error.Message, StringComparison.OrdinalIgnoreCase);
        var attempt = fixture.Attempts().Single(path =>
            Path.GetFileName(path) == "attempt-" +
            error.AttemptId!.Value.ToString("N"));
        Assert.True(File.Exists(Path.Combine(attempt, "intent-v1.json")));
        Assert.True(File.Exists(Path.Combine(attempt, "readiness-v1.json")));
        Assert.True(File.Exists(Path.Combine(attempt, "terminal-v1.json")));
    }

    [Fact]
    public async Task CancellationStopsOnlyTheOwnedTreeAndRecordsIt()
    {
        using var fixture = Active(
            "silent-hang", TimeSpan.FromSeconds(5));
        var receipt = fixture.Activation!.Inspect();
        using var cancellation = new CancellationTokenSource();
        var launcher = new LocalDesktopLauncher(
            fixture.Activation, fixture.LauncherRoot, TimeSpan.FromSeconds(5))
        {
            Io = (point, _, _) =>
            {
                if (point == LauncherEvidencePoint.AfterProcessStarted)
                    cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));
            }
        };

        var error = await Assert.ThrowsAsync<LauncherException>(
            () => launcher.LaunchActiveAsync(
                receipt.Revision, cancellation.Token));

        Assert.Equal(LauncherFailure.Cancelled, error.Failure);
        Assert.NotNull(error.AttemptId);
        var terminal = File.ReadAllText(Path.Combine(
            fixture.Attempts().Single(path =>
                Path.GetFileName(path) == "attempt-" +
                error.AttemptId!.Value.ToString("N")),
            "terminal-v1.json"));
        Assert.Contains("\"outcome\":\"Cancelled\"", terminal);
        Assert.Contains("\"cleanupConfirmed\":true", terminal);
    }

    [Fact]
    public async Task OneProfileLeaseRejectsConcurrentLaunch()
    {
        using var fixture = Active(
            "initialized-hang", TimeSpan.FromSeconds(2));
        var receipt = fixture.Activation!.Inspect();
        var launcher = new LocalDesktopLauncher(
            fixture.Activation, fixture.LauncherRoot, TimeSpan.FromSeconds(2));
        using var cancellation = new CancellationTokenSource();
        var first = launcher.LaunchActiveAsync(
            receipt.Revision, cancellation.Token);
        await WaitUntilAsync(() =>
            fixture.Attempts().Count(path =>
                File.Exists(Path.Combine(path, "readiness-v1.json"))) >= 2);

        var alternateEvidence = Path.Combine(fixture.Root, "alternate-launcher");
        Directory.CreateDirectory(alternateEvidence);
        var second = await Assert.ThrowsAsync<LauncherException>(
            () => new LocalDesktopLauncher(
                    fixture.Activation, alternateEvidence,
                    TimeSpan.FromSeconds(1))
                .LaunchActiveAsync(receipt.Revision));

        Assert.Equal(LauncherFailure.Busy, second.Failure);
        cancellation.Cancel();
        Assert.Equal(LauncherFailure.Cancelled,
            (await Assert.ThrowsAsync<LauncherException>(() => first)).Failure);
    }

    [Fact]
    public async Task VerificationOwnershipRemainsUntilActualProcessRetirement()
    {
        using var fixture = Active(
            "initialized-hang", TimeSpan.FromSeconds(2));
        var receipt = fixture.Activation!.Inspect();
        using var cancellation = new CancellationTokenSource();
        var running = new LocalDesktopLauncher(
                fixture.Activation, fixture.LauncherRoot,
                TimeSpan.FromSeconds(2))
            .LaunchActiveAsync(receipt.Revision, cancellation.Token);
        try
        {
            await WaitUntilAsync(() =>
                fixture.Attempts().Count(path =>
                    File.Exists(Path.Combine(path, "readiness-v1.json"))) >= 2);
            Assert.Equal(ActivationFailure.Busy,
                Assert.Throws<ActivationException>(() => fixture.Activation.Inspect()).Failure);
            Assert.Throws<IOException>(() =>
            {
                using var write = new FileStream(fixture.Settings.FilePath,
                    FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            });
            var dependency = fixture.PayloadFile(
                Path.Combine(fixture.StagingRoot, "version-0.2.0.0"), "Desktop/Martlet.Readiness.dll");
            Assert.Throws<IOException>(() =>
            {
                using var write = new FileStream(dependency, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            });
        }
        finally
        {
            cancellation.Cancel();
            Assert.Equal(LauncherFailure.Cancelled,
                (await Assert.ThrowsAsync<LauncherException>(() => running)).Failure);
        }
        fixture.ChangeSettings();
    }

    [Fact]
    public async Task PersistentProfileLeaseIsSharedAcrossInstances()
    {
        using var fixture = Active();
        var receipt = fixture.Activation!.Inspect();
        var profile = (await fixture.Settings.LoadAsync()).Settings!.Profile.Id;
        var store = new LauncherEvidenceStore(fixture.LauncherRoot);
        using var lease = store.Acquire(profile, fixture.ActivationRoot);

        var error = await Assert.ThrowsAsync<LauncherException>(
            () => new LocalDesktopLauncher(
                    fixture.Activation, fixture.LauncherRoot,
                    TimeSpan.FromSeconds(1))
                .LaunchActiveAsync(receipt.Revision));

        Assert.Equal(LauncherFailure.Busy, error.Failure);
    }

    [Fact]
    public async Task HandshakeBindsCurrentlyPinnedSettingsRevision()
    {
        using var fixture = Active();
        fixture.ChangeSettings();
        var current = await fixture.Settings.LoadAsync();
        var receipt = fixture.Activation!.Inspect();

        var result = await new LocalDesktopLauncher(
                fixture.Activation,
                fixture.LauncherRoot,
                TimeSpan.FromSeconds(2))
            .LaunchActiveAsync(receipt.Revision);

        var intent = File.ReadAllText(Path.Combine(
            fixture.Attempts().Single(path =>
                Path.GetFileName(path) == "attempt-" +
                result.AttemptId.ToString("N")),
            "intent-v1.json"));
        Assert.Contains(
            $"\"settingsRevision\":\"{current.Revision}\"", intent);
        Assert.DoesNotContain(
            $"\"settingsRevision\":\"{receipt.Current!.SettingsRevision}\"",
            intent);
    }

    [Fact]
    public async Task TerminalEvidenceIoFailureIsStableAndRedacted()
    {
        using var fixture = Active();
        var receipt = fixture.Activation!.Inspect();
        var launcher = new LocalDesktopLauncher(
            fixture.Activation,
            fixture.LauncherRoot,
            TimeSpan.FromSeconds(2))
        {
            Io = (point, _, _) =>
            {
                if (point == LauncherEvidencePoint.BeforeTerminal)
                    throw new UnauthorizedAccessException(fixture.Root);
            }
        };

        var error = await Assert.ThrowsAsync<LauncherException>(
            () => launcher.LaunchActiveAsync(receipt.Revision));

        Assert.Equal(LauncherFailure.EvidenceUnavailable, error.Failure);
        Assert.DoesNotContain(
            fixture.Root, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(error.AttemptId);
    }

    [Fact]
    public async Task NoncooperativeDescendantIsKilledWithItsOwnedJob()
    {
        using var fixture = Active(
            "spawn-child-silent", TimeSpan.FromMilliseconds(500));
        var receipt = fixture.Activation!.Inspect();
        var launcher = new LocalDesktopLauncher(
            fixture.Activation, fixture.LauncherRoot,
            TimeSpan.FromMilliseconds(500));

        var error = await Assert.ThrowsAsync<LauncherException>(
            () => launcher.LaunchActiveAsync(receipt.Revision));

        Assert.Equal(LauncherFailure.ReadinessTimedOut, error.Failure);
        var pidPath = Directory.GetFiles(
            fixture.LauncherRoot, "grandchild.pid", SearchOption.AllDirectories)
            .Single();
        var pid = int.Parse(File.ReadAllText(pidPath),
            System.Globalization.CultureInfo.InvariantCulture);
        await WaitUntilAsync(() => !ProcessExists(pid));
        Assert.False(ProcessExists(pid));
    }

    [Fact]
    public async Task SimulatedLauncherLossLeavesIntentKillsJobAndRestartIsFresh()
    {
        using var fixture = Active();
        var receipt = fixture.Activation!.Inspect();
        var interruptedProcess = 0;
        var interrupted = new LocalDesktopLauncher(
            fixture.Activation, fixture.LauncherRoot,
            TimeSpan.FromSeconds(2))
        {
            Io = (point, _, processId) =>
            {
                if (point == LauncherEvidencePoint.AfterProcessStarted)
                {
                    interruptedProcess = processId!.Value;
                    throw new SimulatedLauncherLossException();
                }
            }
        };

        await Assert.ThrowsAsync<SimulatedLauncherLossException>(
            () => interrupted.LaunchActiveAsync(receipt.Revision));
        await WaitUntilAsync(() => !ProcessExists(interruptedProcess));
        var incomplete = fixture.Attempts().Single(path =>
            File.Exists(Path.Combine(path, "intent-v1.json")) &&
            !File.Exists(Path.Combine(path, "readiness-v1.json")));

        var result = await new LocalDesktopLauncher(
                fixture.Activation, fixture.LauncherRoot,
                TimeSpan.FromSeconds(2))
            .LaunchActiveAsync(receipt.Revision);

        Assert.Equal(0, result.ExitCode);
        Assert.True(Directory.Exists(incomplete));
        Assert.False(File.Exists(Path.Combine(incomplete, "terminal-v1.json")));
    }

    [Fact]
    public async Task ChangedDependencyFailsBeforeAnyProcessOrFallback()
    {
        using var fixture = Active();
        var receipt = fixture.Activation!.Inspect();
        var stage = Path.Combine(
            fixture.StagingRoot, "version-" + receipt.Current!.Version);
        File.AppendAllText(
            fixture.PayloadFile(stage, "Desktop/Martlet.Readiness.dll"), "x");
        var attempts = fixture.Attempts().Length;

        var error = await Assert.ThrowsAsync<LauncherException>(
            () => new LocalDesktopLauncher(
                    fixture.Activation, fixture.LauncherRoot,
                    TimeSpan.FromSeconds(1))
                .LaunchActiveAsync(receipt.Revision));

        Assert.Equal(LauncherFailure.TamperedPayload, error.Failure);
        Assert.Equal(attempts, fixture.Attempts().Length);
    }

    [Fact]
    public async Task FailedCurrentNeverFallsBackToVerifiedPrevious()
    {
        using var fixture = new LauncherFixture();
        fixture.InitializeSelection();
        fixture.Select(fixture.Stage(
            "0.2.0.0", launchMode: "initialized-exit"));
        fixture.ConfigureActivation(TimeSpan.FromSeconds(2));
        fixture.InitializeActivation();
        fixture.ActivateCurrent();
        fixture.Select(fixture.Stage(
            "0.3.0.0", launchMode: "wrong-nonce"));
        var active = fixture.ActivateCurrent();

        var error = await Assert.ThrowsAsync<LauncherException>(
            () => new LocalDesktopLauncher(
                    fixture.Activation!, fixture.LauncherRoot,
                    TimeSpan.FromSeconds(1))
                .LaunchActiveAsync(active.Revision));

        Assert.Equal(LauncherFailure.ReadinessRejected, error.Failure);
        var intent = File.ReadAllText(Path.Combine(
            fixture.Attempts().Single(path =>
                Path.GetFileName(path) == "attempt-" +
                error.AttemptId!.Value.ToString("N")),
            "intent-v1.json"));
        Assert.Contains("\"version\":\"0.3.0.0\"", intent);
        Assert.DoesNotContain("\"version\":\"0.2.0.0\"", intent);
    }

    [Theory]
    [InlineData("wrong-nonce", ActivationFailure.ReadinessFailed)]
    [InlineData("late", ActivationFailure.ReadinessTimedOut)]
    public void ProductionActivationProbeFailsAndRetainsPendingEvidence(
        string mode,
        ActivationFailure expected)
    {
        using var fixture = new LauncherFixture();
        fixture.InitializeSelection();
        fixture.Select(fixture.Stage(
            "0.2.0.0", activationMode: mode));
        fixture.ConfigureActivation(TimeSpan.FromMilliseconds(400));
        fixture.InitializeActivation();

        var error = Assert.Throws<ActivationException>(
            fixture.ActivateCurrent);

        Assert.Equal(expected, error.Failure);
        Assert.NotNull(error.TransactionId);
        Assert.Equal(ActivationStatus.ReadinessPending,
            fixture.Activation!.Inspect().Status);
        Assert.Single(fixture.Attempts());
        var recovered = fixture.Activation.Recover(error.TransactionId);
        Assert.Equal(ActivationOutcome.RecoveredPrevious, recovered.Outcome);
        Assert.False(recovered.IsRunnable);
    }

    [Fact]
    public void UnsupportedApplicationVersionFailsProductionReadiness()
    {
        using var fixture = new LauncherFixture();
        fixture.InitializeSelection();
        fixture.Select(fixture.Stage("1.0.0.0"));
        fixture.ConfigureActivation(TimeSpan.FromSeconds(1));
        fixture.InitializeActivation();

        var error = Assert.Throws<LauncherException>(
            fixture.ActivateCurrent);

        Assert.Equal(LauncherFailure.IncompatibleVersion, error.Failure);
        Assert.Empty(fixture.Attempts());
    }

    private static LauncherFixture Active(
        string launchMode = "initialized-exit",
        TimeSpan? timeout = null)
    {
        var fixture = new LauncherFixture();
        try
        {
            fixture.InitializeSelection();
            fixture.Select(fixture.Stage(
                "0.2.0.0",
                activationMode: "initialized-exit",
                launchMode: launchMode));
            var activationTimeout = timeout is { } requested &&
                requested > TimeSpan.FromSeconds(2)
                    ? requested
                    : TimeSpan.FromSeconds(2);
            fixture.ConfigureActivation(activationTimeout);
            fixture.InitializeActivation();
            fixture.ActivateCurrent();
            return fixture;
        }
        catch
        {
            fixture.Dispose();
            throw;
        }
    }

    private static bool ProcessExists(int processId)
    {
        if (processId <= 0) return false;
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(5))
        {
            if (condition()) return;
            await Task.Delay(20);
        }
        Assert.True(condition());
    }
}
