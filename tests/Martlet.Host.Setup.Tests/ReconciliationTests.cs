namespace Martlet.Host.Setup.Tests;

public sealed class ReconciliationTests
{
    [Fact]
    public async Task Partial_completion_resumes_without_reexecuting_completed_step()
    {
        var plan = SetupTestData.ReviewedPlan();
        var fileSystem = new FakeSetupFileSystem();
        var probe = new FakeStateProbe();
        var executor = new FakeStepExecutor(probe);
        executor.Enqueue("/test/step-two",
            SetupExecutionResult.Canceled("operator-canceled"),
            SetupExecutionResult.Completed());
        var clock = new MutableTimeProvider(SetupTestData.Now);
        var coordinator = new SetupCoordinator(fileSystem, probe, executor, clock);

        var firstApproval = await SetupTestData.ApprovalAsync(coordinator, plan);
        var first = await coordinator.RunAsync(plan, firstApproval);
        var resumePreview = await coordinator.PreviewAsync(plan);
        var secondApproval = resumePreview.Approve(SetupApprovalDecision.Approve,
            resumePreview.RequiredConsentScopes, SetupPrivilege.Administrator);
        var second = await coordinator.RunAsync(plan, secondApproval);

        Assert.Equal(SetupRunState.Interrupted, first.State);
        Assert.Equal(SetupFailure.Interrupted, first.Failure);
        Assert.Equal(SetupPreviewState.Interrupted, resumePreview.State);
        Assert.Equal(SetupRunState.Completed, second.State);
        Assert.Equal(1, executor.Calls.Count(call => call == "/test/step-one"));
        Assert.Equal(2, executor.Calls.Count(call => call == "/test/step-two"));
        Assert.Equal(new[] { "step-one", "step-two" }, second.CompletedSteps.ToArray());
    }

    [Fact]
    public async Task Already_satisfied_external_state_is_journaled_without_execution()
    {
        var plan = SetupTestData.ReviewedPlan();
        var fileSystem = new FakeSetupFileSystem();
        var probe = new FakeStateProbe();
        probe.SetSatisfied("step-one");
        probe.SetSatisfied("step-two");
        var executor = new FakeStepExecutor(probe);
        var coordinator = new SetupCoordinator(fileSystem, probe, executor,
            new MutableTimeProvider(SetupTestData.Now));
        var approval = await SetupTestData.ApprovalAsync(coordinator, plan);

        var result = await coordinator.RunAsync(plan, approval);

        Assert.Equal(SetupRunState.Completed, result.State);
        Assert.Empty(executor.Calls);
        Assert.Equal(new[] { "step-one", "step-two" }, result.ReconciledSteps.ToArray());
        Assert.Equal(new[] { "step-one", "step-two" }, result.CompletedSteps.ToArray());
    }

    [Fact]
    public async Task Command_completed_before_journal_failure_is_recognized_on_resume()
    {
        var plan = SetupTestData.ReviewedPlan();
        var fileSystem = new FakeSetupFileSystem { FailOnWriteAttempt = 4 };
        var probe = new FakeStateProbe();
        var executor = new FakeStepExecutor(probe);
        var clock = new MutableTimeProvider(SetupTestData.Now);
        var coordinator = new SetupCoordinator(fileSystem, probe, executor, clock);
        var approval = await SetupTestData.ApprovalAsync(coordinator, plan);

        var failure = await Assert.ThrowsAsync<SetupException>(
            async () => await coordinator.RunAsync(plan, approval));
        Assert.Equal(SetupFailure.JournalIoFailure, failure.Failure);
        Assert.Equal(["/test/step-one"], executor.Calls);
        fileSystem.FailOnWriteAttempt = null;

        var resume = await coordinator.PreviewAsync(plan);
        var resumedApproval = resume.Approve(SetupApprovalDecision.Approve,
            resume.RequiredConsentScopes, SetupPrivilege.Administrator);
        var result = await coordinator.RunAsync(plan, resumedApproval);

        Assert.Equal(SetupRunState.Completed, result.State);
        Assert.Contains("step-one", result.ReconciledSteps);
        Assert.Equal(1, executor.Calls.Count(call => call == "/test/step-one"));
        Assert.Equal(1, executor.Calls.Count(call => call == "/test/step-two"));
    }

    [Theory]
    [InlineData("configuration", SetupFailure.ConfigurationChanged)]
    [InlineData("artifact", SetupFailure.ArtifactFactsChanged)]
    public async Task Changed_configuration_or_artifacts_refuse_existing_journal_without_write(
        string changed,
        SetupFailure expected)
    {
        var original = SetupTestData.ReviewedPlan();
        var fileSystem = new FakeSetupFileSystem();
        var probe = new FakeStateProbe();
        var executor = new FakeStepExecutor(probe);
        executor.Enqueue("/test/step-one", SetupExecutionResult.Canceled());
        var coordinator = new SetupCoordinator(fileSystem, probe, executor,
            new MutableTimeProvider(SetupTestData.Now));
        var approval = await SetupTestData.ApprovalAsync(coordinator, original);
        _ = await coordinator.RunAsync(original, approval);
        var writesBefore = fileSystem.WriteAttempts;
        var changedPlan = SetupTestData.ReviewedPlan(
            configurationRevision: changed == "configuration" ? "config-2" : "config-1",
            hostRevision: "host-1",
            artifactRevision: changed == "artifact" ? "artifact-2" : "artifact-1");

        var error = await Assert.ThrowsAsync<SetupException>(
            async () => await coordinator.PreviewAsync(changedPlan));

        Assert.Equal(expected, error.Failure);
        Assert.Equal(writesBefore, fileSystem.WriteAttempts);
    }

    [Fact]
    public async Task Fresh_host_facts_roll_forward_expired_interrupted_journal()
    {
        var original = SetupTestData.ReviewedPlan();
        var fileSystem = new FakeSetupFileSystem();
        var probe = new FakeStateProbe();
        var executor = new FakeStepExecutor(probe);
        executor.Enqueue("/test/step-one", SetupExecutionResult.Canceled());
        var clock = new MutableTimeProvider(SetupTestData.Now);
        var coordinator = new SetupCoordinator(fileSystem, probe, executor, clock);
        var approval = await SetupTestData.ApprovalAsync(coordinator, original);
        Assert.Equal(SetupRunState.Interrupted, (await coordinator.RunAsync(original, approval)).State);
        clock.UtcNow = original.ExpiresAtUtc.AddMinutes(1);

        var stalePreview = await coordinator.PreviewAsync(original);
        Assert.False(stalePreview.CanApprove);
        Assert.Equal(SetupFailure.PlanStale, stalePreview.Failure);
        var refreshed = SetupTestData.ReviewedPlan(
            createdAt: clock.UtcNow, hostRevision: "host-2");
        var writesBefore = fileSystem.WriteAttempts;
        var refreshedPreview = await coordinator.PreviewAsync(refreshed);

        Assert.True(refreshedPreview.CanApprove);
        Assert.True(refreshedPreview.RequiresJournalRollForward);
        Assert.Equal(writesBefore, fileSystem.WriteAttempts);
        var refreshedApproval = refreshedPreview.Approve(SetupApprovalDecision.Approve,
            refreshedPreview.RequiredConsentScopes, SetupPrivilege.Administrator);
        var result = await coordinator.RunAsync(refreshed, refreshedApproval);

        Assert.Equal(SetupRunState.Completed, result.State);
        var journal = SetupJournalCodec.Read(fileSystem.Content!);
        Assert.Single(journal.Supersessions);
        Assert.Equal(original.Fingerprint, journal.Supersessions[0].PlanFingerprint);
        Assert.Equal(refreshed.Fingerprint, journal.PlanFingerprint);
        Assert.Equal(refreshed.HostFactsFingerprint, journal.HostFactsFingerprint);
    }

    [Fact]
    public async Task Old_approval_refuses_changed_host_facts()
    {
        var original = SetupTestData.ReviewedPlan();
        var fileSystem = new FakeSetupFileSystem();
        var probe = new FakeStateProbe();
        var executor = new FakeStepExecutor(probe);
        var coordinator = new SetupCoordinator(fileSystem, probe, executor,
            new MutableTimeProvider(SetupTestData.Now));
        var approval = await SetupTestData.ApprovalAsync(coordinator, original);
        var changed = SetupTestData.ReviewedPlan(hostRevision: "host-2");

        var result = await coordinator.RunAsync(changed, approval);

        Assert.Equal(SetupFailure.HostFactsChanged, result.Failure);
        Assert.Equal(0, fileSystem.WriteAttempts);
        Assert.Empty(executor.Calls);
    }

    [Fact]
    public async Task External_conflict_after_preview_blocks_without_executor()
    {
        var plan = SetupTestData.ReviewedPlan();
        var fileSystem = new FakeSetupFileSystem();
        var probe = new FakeStateProbe();
        var executor = new FakeStepExecutor(probe);
        var coordinator = new SetupCoordinator(fileSystem, probe, executor,
            new MutableTimeProvider(SetupTestData.Now));
        var approval = await SetupTestData.ApprovalAsync(coordinator, plan);
        probe.SetConflict("step-one");

        var result = await coordinator.RunAsync(plan, approval);

        Assert.Equal(SetupRunState.Blocked, result.State);
        Assert.Equal(SetupFailure.ExternalStateConflict, result.Failure);
        Assert.Empty(executor.Calls);
        Assert.False(result.RollbackAvailable);
    }

    [Fact]
    public async Task Unknown_external_state_blocks_without_treating_it_as_pending()
    {
        var plan = SetupTestData.ReviewedPlan();
        var fileSystem = new FakeSetupFileSystem();
        var probe = new FakeStateProbe();
        var executor = new FakeStepExecutor(probe);
        var coordinator = new SetupCoordinator(fileSystem, probe, executor,
            new MutableTimeProvider(SetupTestData.Now));
        var approval = await SetupTestData.ApprovalAsync(coordinator, plan);
        probe.SetUnknown("step-one");

        var result = await coordinator.RunAsync(plan, approval);

        Assert.Equal(SetupFailure.ExternalStateUnknown, result.Failure);
        Assert.Empty(executor.Calls);
    }

    [Fact]
    public async Task Missing_state_for_journaled_completed_step_is_a_conflict()
    {
        var plan = SetupTestData.ReviewedPlan();
        var fileSystem = new FakeSetupFileSystem();
        var probe = new FakeStateProbe();
        var executor = new FakeStepExecutor(probe);
        var coordinator = new SetupCoordinator(fileSystem, probe, executor,
            new MutableTimeProvider(SetupTestData.Now));
        var approval = await SetupTestData.ApprovalAsync(coordinator, plan);
        Assert.Equal(SetupRunState.Completed, (await coordinator.RunAsync(plan, approval)).State);
        probe.SetPending("step-one");

        var preview = await coordinator.PreviewAsync(plan);

        Assert.False(preview.CanApprove);
        Assert.Equal(SetupFailure.ExternalStateConflict, preview.Failure);
        Assert.Equal(2, executor.Calls.Count);
    }

    [Fact]
    public async Task Changed_satisfied_fingerprint_is_blocked_not_completed()
    {
        var plan = SetupTestData.ReviewedPlan();
        var fileSystem = new FakeSetupFileSystem();
        var probe = new FakeStateProbe();
        var executor = new FakeStepExecutor(probe);
        var coordinator = new SetupCoordinator(fileSystem, probe, executor,
            new MutableTimeProvider(SetupTestData.Now));
        var approval = await SetupTestData.ApprovalAsync(coordinator, plan);
        Assert.Equal(SetupRunState.Completed, (await coordinator.RunAsync(plan, approval)).State);
        probe.SetSatisfied("step-one", "changed-revision");

        var preview = await coordinator.PreviewAsync(plan);

        Assert.Equal(SetupPreviewState.Blocked, preview.State);
        Assert.Equal(SetupFailure.ExternalStateConflict, preview.Failure);
        Assert.False(preview.CanApprove);
    }

    [Fact]
    public async Task Executor_failure_is_truthful_and_never_claims_rollback()
    {
        var plan = SetupTestData.ReviewedPlan();
        var fileSystem = new FakeSetupFileSystem();
        var probe = new FakeStateProbe();
        var executor = new FakeStepExecutor(probe);
        executor.Enqueue("/test/step-one", SetupExecutionResult.Failed("inert-failure"));
        var coordinator = new SetupCoordinator(fileSystem, probe, executor,
            new MutableTimeProvider(SetupTestData.Now));
        var approval = await SetupTestData.ApprovalAsync(coordinator, plan);

        var result = await coordinator.RunAsync(plan, approval);

        Assert.Equal(SetupFailure.StepFailed, result.Failure);
        Assert.False(result.RollbackAvailable);
        Assert.False(result.RollbackPerformed);
        Assert.Contains("No automatic rollback", result.Remedy!.Instruction);
    }

    [Fact]
    public async Task Original_token_interruption_leaves_resumable_running_journal()
    {
        var plan = SetupTestData.ReviewedPlan();
        var fileSystem = new FakeSetupFileSystem();
        var probe = new FakeStateProbe();
        using var source = new CancellationTokenSource();
        var executor = new CancelingStepExecutor(source);
        var coordinator = new SetupCoordinator(fileSystem, probe, executor,
            new MutableTimeProvider(SetupTestData.Now));
        var approval = await SetupTestData.ApprovalAsync(coordinator, plan);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await coordinator.RunAsync(plan, approval, source.Token));
        var preview = await coordinator.PreviewAsync(plan);

        Assert.Equal(1, executor.Calls);
        Assert.Equal(SetupPreviewState.Interrupted, preview.State);
        Assert.True(preview.CanApprove);
        Assert.NotNull(fileSystem.Content);
    }

    [Fact]
    public async Task Expiry_is_rechecked_before_each_new_command()
    {
        var plan = SetupTestData.ReviewedPlan();
        var fileSystem = new FakeSetupFileSystem();
        var probe = new FakeStateProbe();
        var clock = new MutableTimeProvider(SetupTestData.Now);
        var executor = new ExpiringStepExecutor(probe, clock, plan.ExpiresAtUtc);
        var coordinator = new SetupCoordinator(fileSystem, probe, executor, clock);
        var approval = await SetupTestData.ApprovalAsync(coordinator, plan);

        var result = await coordinator.RunAsync(plan, approval);

        Assert.Equal(SetupRunState.Blocked, result.State);
        Assert.Equal(SetupFailure.PlanStale, result.Failure);
        Assert.Equal("step-two", result.StepId);
        Assert.Equal(["/test/step-one"], executor.Calls);
    }
}
