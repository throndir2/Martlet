using Martlet.Updates;

namespace Martlet.Launcher.Tests;

[Collection(LauncherProcessCollection.Name)]
public sealed class LauncherReviewRegressionTests
{
    [Fact]
    public void EvidenceRootCannotHideInsidePayloadThroughAncestorLink()
    {
        using var fixture = Selected();
        var stage = Path.Combine(fixture.StagingRoot, "version-0.2.0.0");
        var alias = Path.Combine(fixture.Root, "alias");
        var before = Directory.GetFileSystemEntries(Path.Combine(stage, "payload", "notices"));
        Directory.CreateSymbolicLink(alias, stage);
        try
        {
            var error = Assert.Throws<LauncherException>(() =>
                new LauncherEvidenceStore(Path.Combine(alias, "payload", "notices")));
            Assert.Equal(LauncherFailure.EvidenceUnavailable, error.Failure);
            Assert.Equal(before, Directory.GetFileSystemEntries(Path.Combine(stage, "payload", "notices")));
        }
        finally { Directory.Delete(alias); }
    }

    [Theory]
    [InlineData("invalid-operation")]
    [InlineData("json")]
    [InlineData("timeout")]
    [InlineData("io")]
    public async Task PolicyReadFailureAfterReadinessIsRedactedAndRecordsRetirement(string kind)
    {
        using var fixture = Selected();
        var fail = false;
        fixture.ConfigureActivation(TimeSpan.FromSeconds(5), publisherPolicy: () =>
        {
            if (fail)
                throw kind switch
                {
                    "json" => new System.Text.Json.JsonException(fixture.Root),
                    "timeout" => new TimeoutException(fixture.Root),
                    "io" => new IOException(fixture.Root),
                    _ => new InvalidOperationException(fixture.Root)
                };
            return fixture.PublisherPolicy;
        });
        fixture.InitializeActivation();
        var active = fixture.ActivateCurrent();
        var launcher = new LocalDesktopLauncher(fixture.Activation!, fixture.LauncherRoot)
        {
            Io = (point, _, _) => { if (point == LauncherEvidencePoint.AfterReadiness) fail = true; }
        };
        var error = await Assert.ThrowsAsync<LauncherException>(() => launcher.LaunchActiveAsync(active.Revision));
        Assert.Equal(LauncherFailure.PublisherUnavailable, error.Failure);
        Assert.DoesNotContain(fixture.Root, error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(error.AttemptId);
        var terminal = Terminal(fixture, error.AttemptId!.Value);
        Assert.Contains("\"cleanupConfirmed\":true", terminal);
        Assert.Contains("\"outcome\":\"PublisherUnavailable\"", terminal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinalPrestartProviderCannotOutliveConsentOrReadiness(bool readiness)
    {
        using var fixture = Selected();
        var slow = false;
        var clock = new ManualClock();
        fixture.ConfigureActivation(TimeSpan.FromSeconds(5), publisherPolicy: () =>
        {
            if (slow)
            {
                slow = false;
                if (readiness) Thread.Sleep(400);
                else clock.Advance(TimeSpan.FromMinutes(3));
            }
            return fixture.PublisherPolicy;
        });
        fixture.InitializeActivation();
        var active = fixture.ActivateCurrent();
        var launcher = new LocalDesktopLauncher(fixture.Activation!, fixture.LauncherRoot,
            readiness ? TimeSpan.FromMilliseconds(300) : TimeSpan.FromSeconds(5))
        {
            Clock = clock,
            Io = (point, _, _) => { if (point == LauncherEvidencePoint.AfterIntent) slow = true; }
        };
        var error = await Assert.ThrowsAsync<LauncherException>(() => launcher.LaunchActiveAsync(active.Revision));
        Assert.Equal(readiness ? LauncherFailure.ReadinessTimedOut : LauncherFailure.Conflict, error.Failure);
        Assert.NotNull(error.AttemptId);
        var attempt = fixture.Attempts().Single(path => Path.GetFileName(path) == $"attempt-{error.AttemptId:N}");
        Assert.False(File.Exists(Path.Combine(attempt, "runtime", "fixture-started.pid")));
        Assert.Contains("\"processId\":null", Terminal(fixture, error.AttemptId!.Value));
    }

    [Fact]
    public void FinalPublisherSamplingPrecedesLiveInstalledFactsRecheck()
    {
        using var fixture = Selected();
        var armed = false;
        byte[]? pending = null;
        var probe = new LauncherActivationReadinessProbe(fixture.LauncherRoot, TimeSpan.FromSeconds(5), () =>
        {
            if (armed)
            {
                armed = false;
                fixture.ChangeInstalledImage();
            }
            return fixture.PublisherPolicy;
        });
        var engine = new LocalActivationEngine(fixture.ActivationRoot, fixture.Selection, probe)
        {
            Io = (point, path, _) =>
            {
                if (point == ActivationIoPoint.BeforePublicationRename && Path.GetFileName(path) == "active.json")
                {
                    pending = File.ReadAllBytes(Path.Combine(fixture.ActivationRoot, "active.json"));
                    armed = true;
                }
            }
        };
        engine.Initialize(fixture.Selection.Inspect().Revision);
        var plan = engine.PrepareActivation(0, fixture.Selection.Inspect().Revision);
        var error = Assert.Throws<ActivationException>(() => engine.Activate(plan,
            plan.Approve(plan.TransactionId, 0, plan.ExpectedSelectionRevision,
                plan.ExpectedSettingsRevision, plan.PlanDigest)));
        Assert.Equal(ActivationFailure.Conflict, error.Failure);
        Assert.NotNull(pending);
        Assert.Equal(pending, File.ReadAllBytes(Path.Combine(fixture.ActivationRoot, "active.json")));
        Assert.Single(fixture.Attempts());
    }

    [Fact]
    public async Task PublisherExpiryDuringPrestartEvidenceReadCannotExecute()
    {
        using var fixture = Selected();
        fixture.ConfigureActivation(TimeSpan.FromSeconds(10));
        fixture.InitializeActivation();
        var active = fixture.ActivateCurrent();
        var expiry = DateTimeOffset.UtcNow.AddSeconds(2);
        fixture.PublisherPolicy = new("expiring-launch", expiry,
            [fixture.Grant("0.2.0.0", LauncherExecutionPurpose.DesktopLaunch)]);
        var armed = false;
        fixture.InstalledFactsRead = () =>
        {
            if (!armed) return;
            armed = false;
            WaitPast(expiry);
        };
        var launcher = new LocalDesktopLauncher(fixture.Activation!, fixture.LauncherRoot, TimeSpan.FromSeconds(10))
        {
            Io = (point, _, _) => { if (point == LauncherEvidencePoint.AfterIntent) armed = true; }
        };
        var error = await Assert.ThrowsAsync<LauncherException>(() => launcher.LaunchActiveAsync(active.Revision));
        Assert.Equal(LauncherFailure.PublisherRejected, error.Failure);
        Assert.NotNull(error.AttemptId);
        Assert.Contains("\"processId\":null", Terminal(fixture, error.AttemptId!.Value));
    }

    [Fact]
    public void PublisherExpiryDuringFinalEvidenceReadCannotPublish()
    {
        using var fixture = Selected();
        var expiry = DateTimeOffset.UtcNow.AddSeconds(5);
        fixture.PublisherPolicy = new("expiring-activation", expiry,
            [fixture.Grant("0.2.0.0", LauncherExecutionPurpose.ActivationReadiness)]);
        var armed = false;
        byte[]? pending = null;
        fixture.InstalledFactsRead = () =>
        {
            if (!armed) return;
            armed = false;
            WaitPast(expiry);
        };
        var probe = new LauncherActivationReadinessProbe(
            fixture.LauncherRoot, TimeSpan.FromSeconds(10), () => fixture.PublisherPolicy);
        var engine = new LocalActivationEngine(fixture.ActivationRoot, fixture.Selection, probe)
        {
            Io = (point, path, _) =>
            {
                if (point != ActivationIoPoint.BeforePublicationRename || Path.GetFileName(path) != "active.json")
                    return;
                pending = File.ReadAllBytes(Path.Combine(fixture.ActivationRoot, "active.json"));
                armed = true;
            }
        };
        engine.Initialize(fixture.Selection.Inspect().Revision);
        var plan = engine.PrepareActivation(0, fixture.Selection.Inspect().Revision);
        var error = Assert.Throws<LauncherException>(() => engine.Activate(plan,
            plan.Approve(plan.TransactionId, 0, plan.ExpectedSelectionRevision,
                plan.ExpectedSettingsRevision, plan.PlanDigest)));
        Assert.Equal(LauncherFailure.PublisherRejected, error.Failure);
        Assert.NotNull(pending);
        Assert.Equal(pending, File.ReadAllBytes(Path.Combine(fixture.ActivationRoot, "active.json")));
        Assert.Single(fixture.Attempts());
    }

    private static void WaitPast(DateTimeOffset deadline)
    {
        while (DateTimeOffset.UtcNow <= deadline)
            Thread.Sleep(10);
    }

    private static string Terminal(LauncherFixture fixture, Guid attempt) =>
        File.ReadAllText(Path.Combine(fixture.Attempts().Single(path =>
            Path.GetFileName(path) == $"attempt-{attempt:N}"), "terminal-v1.json"));

    private static LauncherFixture Selected()
    {
        var fixture = new LauncherFixture();
        fixture.InitializeSelection();
        fixture.Select(fixture.Stage("0.2.0.0", launchMode: "initialized-hang"));
        return fixture;
    }

    private sealed class ManualClock : TimeProvider
    {
        private TimeSpan offset;
        private readonly DateTimeOffset start = DateTimeOffset.UtcNow;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => offset.Ticks;
        public override DateTimeOffset GetUtcNow() => start + offset;
        internal void Advance(TimeSpan amount) => offset += amount;
    }
}
