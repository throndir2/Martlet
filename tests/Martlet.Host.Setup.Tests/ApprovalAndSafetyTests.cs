namespace Martlet.Host.Setup.Tests;

public sealed class ApprovalAndSafetyTests
{
    [Fact]
    public async Task Preview_and_default_no_are_read_only()
    {
        var plan = SetupTestData.ReviewedPlan();
        var fileSystem = new FakeSetupFileSystem();
        var probe = new FakeStateProbe();
        var executor = new FakeStepExecutor(probe);
        var coordinator = new SetupCoordinator(fileSystem, probe, executor,
            new MutableTimeProvider(SetupTestData.Now));

        var preview = await coordinator.PreviewAsync(plan);
        var approval = preview.Approve();

        Assert.True(preview.CanApprove);
        Assert.False(approval.IsApproved);
        Assert.Equal(SetupFailure.ConsentRequired, approval.Failure);
        Assert.Equal(0, fileSystem.WriteAttempts);
        Assert.Empty(fileSystem.TouchedPaths);
        Assert.Empty(executor.Calls);
        Assert.False(preview.RollbackAvailable);
    }

    [Fact]
    public async Task Exact_scopes_are_required_without_blanket_or_partial_consent()
    {
        var plan = SetupTestData.ReviewedPlan();
        var coordinator = new SetupCoordinator(new FakeSetupFileSystem(), new FakeStateProbe(),
            clock: new MutableTimeProvider(SetupTestData.Now));
        var preview = await coordinator.PreviewAsync(plan);

        var missing = preview.Approve(SetupApprovalDecision.Approve,
            [SetupConsentScope.LocalJournal, SetupConsentScope.HostFilesystem],
            SetupPrivilege.Administrator);
        var extra = preview.Approve(SetupApprovalDecision.Approve,
            [.. preview.RequiredConsentScopes, SetupConsentScope.ArtifactDownload],
            SetupPrivilege.Administrator);
        var exact = preview.Approve(SetupApprovalDecision.Approve,
            preview.RequiredConsentScopes, SetupPrivilege.Administrator);

        Assert.Equal(SetupFailure.ConsentScopeMismatch, missing.Failure);
        Assert.Equal(SetupFailure.ConsentScopeMismatch, extra.Failure);
        Assert.True(exact.IsApproved);
    }

    [Fact]
    public async Task Current_production_proposal_cannot_be_approved_or_journaled()
    {
        var host = SetupTestData.HostReport();
        var plan = new SetupPlanBuilder(new MutableTimeProvider(host.CreatedAt.AddMinutes(1))).Build(
            new SetupConfiguration("candidate-1", [SetupRole.Llm, SetupRole.Tts]),
            host, SetupTestData.ArtifactReport());
        var fileSystem = new FakeSetupFileSystem();
        var coordinator = new SetupCoordinator(fileSystem, clock: new MutableTimeProvider(host.CreatedAt.AddMinutes(1)));

        var preview = await coordinator.PreviewAsync(plan);
        var approval = preview.Approve(SetupApprovalDecision.Approve,
            preview.RequiredConsentScopes, SetupPrivilege.Administrator);
        var result = await coordinator.RunAsync(plan, approval);

        Assert.False(preview.CanApprove);
        Assert.Equal(SetupFailure.PlanBlocked, preview.Failure);
        Assert.Equal(SetupFailure.PlanBlocked, approval.Failure);
        Assert.Equal(SetupRunState.Refused, result.State);
        Assert.Equal(0, fileSystem.WriteAttempts);
    }

    [Fact]
    public async Task Approval_is_one_use()
    {
        var plan = SetupTestData.ReviewedPlan();
        var fileSystem = new FakeSetupFileSystem();
        var probe = new FakeStateProbe();
        var executor = new FakeStepExecutor(probe);
        var coordinator = new SetupCoordinator(fileSystem, probe, executor,
            new MutableTimeProvider(SetupTestData.Now));
        var approval = await SetupTestData.ApprovalAsync(coordinator, plan);

        var first = await coordinator.RunAsync(plan, approval);
        var second = await coordinator.RunAsync(plan, approval);

        Assert.Equal(SetupRunState.Completed, first.State);
        Assert.Equal(SetupRunState.Refused, second.State);
        Assert.Equal(SetupFailure.ConsentConsumed, second.Failure);
    }

    [Fact]
    public async Task Expired_approval_refuses_before_any_write_or_execution()
    {
        var clock = new MutableTimeProvider(SetupTestData.Now);
        var plan = SetupTestData.ReviewedPlan();
        var fileSystem = new FakeSetupFileSystem();
        var probe = new FakeStateProbe();
        var executor = new FakeStepExecutor(probe);
        var coordinator = new SetupCoordinator(fileSystem, probe, executor, clock);
        var approval = await SetupTestData.ApprovalAsync(coordinator, plan);
        clock.UtcNow = plan.ExpiresAtUtc;

        var result = await coordinator.RunAsync(plan, approval);

        Assert.Equal(SetupFailure.PlanStale, result.Failure);
        Assert.Equal(0, fileSystem.WriteAttempts);
        Assert.Empty(executor.Calls);
    }

    [Fact]
    public async Task Approval_is_bound_to_the_exact_journal_destination()
    {
        var plan = SetupTestData.ReviewedPlan();
        var probe = new FakeStateProbe();
        var first = new SetupCoordinator(
            new FakeSetupFileSystem(@"C:\inert-h05a\first.json"), probe,
            new FakeStepExecutor(probe), new MutableTimeProvider(SetupTestData.Now));
        var approval = await SetupTestData.ApprovalAsync(first, plan);
        var secondFileSystem = new FakeSetupFileSystem(@"C:\inert-h05a\second.json");
        var secondExecutor = new FakeStepExecutor(probe);
        var second = new SetupCoordinator(secondFileSystem, probe, secondExecutor,
            new MutableTimeProvider(SetupTestData.Now));

        var result = await second.RunAsync(plan, approval);

        Assert.Equal(SetupFailure.JournalDestinationChanged, result.Failure);
        Assert.Equal(0, secondFileSystem.WriteAttempts);
        Assert.Empty(secondExecutor.Calls);
    }

    [Fact]
    public async Task Privilege_boundary_stops_before_administrator_step()
    {
        var plan = SetupTestData.ReviewedPlan();
        var fileSystem = new FakeSetupFileSystem();
        var probe = new FakeStateProbe();
        var executor = new FakeStepExecutor(probe);
        var coordinator = new SetupCoordinator(fileSystem, probe, executor,
            new MutableTimeProvider(SetupTestData.Now));
        var approval = await SetupTestData.ApprovalAsync(coordinator, plan, SetupPrivilege.Operator);

        var result = await coordinator.RunAsync(plan, approval);

        Assert.Equal(SetupRunState.Blocked, result.State);
        Assert.Equal(SetupFailure.PrivilegeRequired, result.Failure);
        Assert.Equal("step-two", result.StepId);
        Assert.Equal(["/test/step-one"], executor.Calls);
        Assert.False(result.RollbackAvailable);
        Assert.False(result.RollbackPerformed);
    }

    [Fact]
    public async Task Production_default_executor_refuses_without_process_fallback()
    {
        var plan = SetupTestData.ReviewedPlan(secondPrivilege: SetupPrivilege.Operator);
        var fileSystem = new FakeSetupFileSystem();
        var probe = new FakeStateProbe();
        var coordinator = new SetupCoordinator(fileSystem, probe, clock: new MutableTimeProvider(SetupTestData.Now));
        var approval = await SetupTestData.ApprovalAsync(coordinator, plan, SetupPrivilege.Operator);

        var result = await coordinator.RunAsync(plan, approval);

        Assert.Equal(SetupFailure.ExecutorRefused, result.Failure);
        Assert.Equal("step-one", result.StepId);
        Assert.False(result.RollbackAvailable);
        Assert.All(fileSystem.TouchedPaths, path => Assert.Equal(fileSystem.JournalPath, path));
    }

    [Fact]
    public async Task Fake_execution_mutates_only_exact_expected_state_and_journal()
    {
        var plan = SetupTestData.ReviewedPlan();
        var fileSystem = new FakeSetupFileSystem();
        var probe = new FakeStateProbe();
        var executor = new FakeStepExecutor(probe);
        var coordinator = new SetupCoordinator(fileSystem, probe, executor,
            new MutableTimeProvider(SetupTestData.Now));
        var approval = await SetupTestData.ApprovalAsync(coordinator, plan);

        var result = await coordinator.RunAsync(plan, approval);

        Assert.Equal(SetupRunState.Completed, result.State);
        Assert.Equal(["/test/step-one", "/test/step-two"], executor.Calls);
        Assert.All(fileSystem.TouchedPaths, path => Assert.Equal(fileSystem.JournalPath, path));
        Assert.Equal(new[] { "step-one", "step-two" }, result.ExecutedSteps.ToArray());
        Assert.Equal(new[] { "step-one", "step-two" }, result.CompletedSteps.ToArray());
        Assert.False(result.RollbackAvailable);
        Assert.False(result.RollbackPerformed);
    }
}
