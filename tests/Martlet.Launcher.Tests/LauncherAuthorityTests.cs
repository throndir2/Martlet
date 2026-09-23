using Martlet.Updates;

namespace Martlet.Launcher.Tests;

[Collection(LauncherProcessCollection.Name)]
public sealed class LauncherAuthorityTests
{
    [Fact]
    public void StagingTrustDoesNotConfigurePublisherExecution()
    {
        using var fixture = Selected();
        var engine = LauncherActivationComposition.Create(
            fixture.ActivationRoot, fixture.Selection, fixture.LauncherRoot);
        engine.Initialize(fixture.Selection.Inspect().Revision);
        Assert.Equal(LauncherFailure.PublisherUnconfigured, Assert.Throws<LauncherException>(() =>
            engine.PrepareActivation(0, fixture.Selection.Inspect().Revision)).Failure);
        Assert.Empty(fixture.Attempts());
        Assert.False(engine.Inspect().IsRunnable);
    }

    [Theory]
    [InlineData("signer")]
    [InlineData("source")]
    [InlineData("dirty")]
    [InlineData("version")]
    [InlineData("archive")]
    [InlineData("manifest")]
    [InlineData("executable")]
    [InlineData("purpose")]
    [InlineData("expiry")]
    public void ExactPublisherScopeIsRequiredBeforeActivationProcess(string change)
    {
        using var fixture = Selected();
        var grant = fixture.Grant("0.2.0.0", LauncherExecutionPurpose.ActivationReadiness);
        grant = change switch
        {
            "signer" => grant with { SignerId = new string('f', 64) },
            "source" => grant with { SourceCommit = new string('c', 40) },
            "dirty" => grant with { SourceDirty = true },
            "version" => grant with { Version = "0.9.0.0" },
            "archive" => grant with { ArchiveSha256 = new string('f', 64) },
            "manifest" => grant with { ManifestSha256 = new string('f', 64) },
            "executable" => grant with { ExecutableSha256 = new string('f', 64) },
            "purpose" => grant with { Purpose = LauncherExecutionPurpose.DesktopLaunch },
            _ => grant
        };
        fixture.PublisherPolicy = new("scope-test",
            change == "expiry" ? DateTimeOffset.UtcNow.AddSeconds(-1) : DateTimeOffset.UtcNow.AddHours(1), [grant]);
        var engine = fixture.ConfigureActivation();
        fixture.InitializeActivation();
        Assert.Equal(LauncherFailure.PublisherRejected, Assert.Throws<LauncherException>(() =>
            engine.PrepareActivation(0, fixture.Selection.Inspect().Revision)).Failure);
        Assert.Empty(fixture.Attempts());
    }

    [Fact]
    public void RevocationBetweenActivationApprovalAndUseDoesNotStartChild()
    {
        using var fixture = Selected();
        var engine = fixture.ConfigureActivation();
        fixture.InitializeActivation();
        var plan = engine.PrepareActivation(0, fixture.Selection.Inspect().Revision);
        var approval = plan.Approve(plan.TransactionId, plan.ExpectedActivationRevision,
            plan.ExpectedSelectionRevision, plan.ExpectedSettingsRevision, plan.PlanDigest);
        fixture.PublisherPolicy = new("revoked", DateTimeOffset.UtcNow.AddHours(1), []);
        Assert.Equal(LauncherFailure.PublisherUnconfigured,
            Assert.Throws<LauncherException>(() => engine.Activate(plan, approval)).Failure);
        Assert.Empty(fixture.Attempts());
        Assert.False(engine.Inspect().IsRunnable);
    }

    [Fact]
    public void PolicyRevocationAtFinalPublicationCannotUseRememberedProbeResult()
    {
        using var fixture = Selected();
        var probe = new LauncherActivationReadinessProbe(
            fixture.LauncherRoot, TimeSpan.FromSeconds(5), () => fixture.PublisherPolicy);
        var engine = new LocalActivationEngine(fixture.ActivationRoot, fixture.Selection, probe)
        {
            Io = (point, _, _) =>
            {
                if (point == ActivationIoPoint.BeforeActiveReplace)
                    fixture.PublisherPolicy = new("revoked", DateTimeOffset.UtcNow.AddHours(1), []);
            }
        };
        engine.Initialize(fixture.Selection.Inspect().Revision);
        var plan = engine.PrepareActivation(0, fixture.Selection.Inspect().Revision);
        Assert.Equal(LauncherFailure.PublisherUnconfigured, Assert.Throws<LauncherException>(() =>
            engine.Activate(plan, plan.Approve(plan.TransactionId, 0, plan.ExpectedSelectionRevision,
                plan.ExpectedSettingsRevision, plan.PlanDigest))).Failure);
        Assert.Equal(ActivationFailure.PublicationAmbiguous,
            Assert.Throws<ActivationException>(() => engine.Inspect()).Failure);
        Assert.Single(fixture.Attempts());
        Assert.Equal(ActivationFailure.PublicationAmbiguous,
            Assert.Throws<ActivationException>(() => engine.Recover(plan.TransactionId)).Failure);
    }

    [Fact]
    public async Task LaunchRequiresFreshSingleUseEngineBoundConsent()
    {
        using var fixture = Activated();
        var engine = fixture.Activation!;
        var launcher = new LocalDesktopLauncher(engine, fixture.LauncherRoot);
        var plan = launcher.PrepareLaunch(engine.Inspect().Revision);
        Assert.Throws<LauncherException>(() => plan.Approve(Guid.NewGuid(), plan.PlanDigest));
        var approval = plan.Approve(plan.OperationId, plan.PlanDigest);
        Assert.Throws<LauncherException>(() => plan.Approve(plan.OperationId, plan.PlanDigest));
        var result = await launcher.LaunchActiveAsync(plan, approval);
        Assert.Equal(0, result.ExitCode);
        await Assert.ThrowsAsync<LauncherException>(() => launcher.LaunchActiveAsync(plan, approval));

        var crossOwner = launcher.PrepareLaunch(engine.Inspect().Revision);
        await Assert.ThrowsAsync<LauncherException>(() =>
            new LocalDesktopLauncher(engine, fixture.LauncherRoot).LaunchActiveAsync(crossOwner,
                crossOwner.Approve(crossOwner.OperationId, crossOwner.PlanDigest)));
        Assert.Equal(2, fixture.Attempts().Length);
    }

    [Theory]
    [InlineData("settings")]
    [InlineData("revocation")]
    [InlineData("new-plan")]
    [InlineData("policy-revision")]
    public async Task StaleLaunchScopeNeverExecutes(string change)
    {
        using var fixture = Activated();
        var engine = fixture.Activation!;
        var launcher = new LocalDesktopLauncher(engine, fixture.LauncherRoot);
        var plan = launcher.PrepareLaunch(engine.Inspect().Revision);
        var approval = plan.Approve(plan.OperationId, plan.PlanDigest);
        switch (change)
        {
            case "settings": fixture.ChangeSettings(); break;
            case "revocation":
                fixture.PublisherPolicy = new("revoked", DateTimeOffset.UtcNow.AddHours(1), []);
                break;
            case "policy-revision":
                fixture.PublisherPolicy = new("reprovisioned", DateTimeOffset.UtcNow.AddHours(1),
                    [fixture.Grant("0.2.0.0", LauncherExecutionPurpose.DesktopLaunch)]);
                break;
            default: launcher.PrepareLaunch(engine.Inspect().Revision); break;
        }
        await Assert.ThrowsAsync<LauncherException>(() => launcher.LaunchActiveAsync(plan, approval));
        Assert.Single(fixture.Attempts());
    }

    [Fact]
    public void HistoricalPointerReopenDoesNotAcquireExecutableAuthority()
    {
        using var fixture = Activated();
        var engine = new LocalActivationEngine(fixture.ActivationRoot, fixture.Selection);
        var receipt = engine.Inspect();
        Assert.Equal(ActivationReadiness.MissingReadiness, receipt.Readiness);
        Assert.False(receipt.IsRunnable);
        Assert.Equal(LauncherFailure.PublisherUnconfigured, Assert.Throws<LauncherException>(() =>
            new LocalDesktopLauncher(engine, fixture.LauncherRoot).PrepareLaunch(receipt.Revision)).Failure);
        Assert.Single(fixture.Attempts());
    }

    [Fact]
    public void PublicLaunchApiCannotAcceptReceiptRevisionOrCallerReadiness()
    {
        var methods = typeof(LocalDesktopLauncher).GetMethods()
            .Where(method => method.Name == "LaunchActiveAsync").ToArray();
        var method = Assert.Single(methods);
        Assert.Equal(new[] { typeof(DesktopLaunchPlan), typeof(DesktopLaunchApproval), typeof(CancellationToken) },
            method.GetParameters().Select(parameter => parameter.ParameterType));
        Assert.Empty(typeof(DesktopLaunchPlan).GetConstructors());
        Assert.Empty(typeof(DesktopLaunchApproval).GetConstructors());
        Assert.DoesNotContain(typeof(LocalActivationEngine).GetMethods(), item => item.Name == "WithVerifiedActive");
    }

    private static LauncherFixture Selected()
    {
        var fixture = new LauncherFixture();
        fixture.InitializeSelection();
        fixture.Select(fixture.Stage("0.2.0.0"));
        return fixture;
    }

    private static LauncherFixture Activated()
    {
        var fixture = Selected();
        fixture.ConfigureActivation(TimeSpan.FromSeconds(5));
        fixture.InitializeActivation();
        fixture.ActivateCurrent();
        return fixture;
    }
}
