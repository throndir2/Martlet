using System.Text;
using System.Text.Json.Nodes;

namespace Martlet.Host.Setup.Tests;

public sealed class ReviewJournalTests
{
    [Fact]
    public async Task Blocked_production_plan_can_only_record_review_and_reopens_as_history()
    {
        using var store = new ReviewStore();
        var preview = await store.Coordinator.PreviewReviewAsync(store.Plan);
        Assert.True(preview.IsLocalReview);
        Assert.True(preview.CanApprove);
        Assert.False(preview.ExecutionAuthorized);
        Assert.False(preview.Approve().IsApproved);
        Assert.Equal(new[] { SetupConsentScope.LocalJournal }, preview.RequiredConsentScopes);
        Assert.False(File.Exists(store.Path));
        Assert.Equal(SetupPlanDisposition.Blocked, store.Plan.Disposition);
        Assert.False(preview.Approve(SetupApprovalDecision.Approve,
            [SetupConsentScope.LocalJournal], SetupPrivilege.Administrator).IsApproved);

        var approval = Approve(preview);
        var result = await store.Coordinator.RecordReviewAsync(store.Plan, approval);
        Assert.True(result.ReviewRecorded);
        Assert.False(result.ExternalActionsCompleted);
        Assert.False(result.ExecutionAuthorized);
        Assert.Equal(store.Plan.Steps.Select(s => s.Id), result.ReviewedSteps);
        Assert.Equal(SetupFailure.ConsentConsumed,
            (await store.Coordinator.RecordReviewAsync(store.Plan, approval)).Failure);

        var before = File.ReadAllBytes(store.Path);
        var reopened = await store.NewCoordinator().PreviewReviewAsync(store.Plan);
        Assert.Equal(SetupPreviewState.ReviewRecorded, reopened.State);
        Assert.False(reopened.CanApprove);
        Assert.All(reopened.Steps, step =>
        {
            Assert.True(step.JournaledReview);
            Assert.False(step.JournaledComplete);
            Assert.Equal(SetupObservationState.Unknown, step.Observation);
        });
        Assert.Equal(before, File.ReadAllBytes(store.Path));
        var journal = SetupJournalCodec.Read(before);
        Assert.Equal(2, journal.FormatVersion);
        Assert.Equal(SetupJournalPurpose.LocalReview, journal.Purpose);
        Assert.All(journal.Steps, s =>
        {
            Assert.Equal(0, s.Attempts);
            Assert.Null(s.CompletedAtUtc);
            Assert.Null(s.ObservationFingerprint);
            Assert.Equal(SetupCompletionOrigin.None, s.CompletionOrigin);
        });
    }

    [Fact]
    public async Task Review_never_invokes_supplied_probe_or_executor_or_grants_internal_execution()
    {
        using var store = new ReviewStore();
        var probe = new FakeStateProbe();
        foreach (var step in store.Plan.Steps) probe.SetSatisfied(step.Id);
        var executor = new FakeStepExecutor(probe);
        var coordinator = new SetupCoordinator(store.FileSystem, probe, executor, store.Clock);
        var approval = Approve(await coordinator.PreviewReviewAsync(store.Plan));
        Assert.Equal(SetupFailure.ConsentScopeMismatch, (await coordinator.RunAsync(store.Plan, approval)).Failure);
        Assert.True((await coordinator.RecordReviewAsync(store.Plan, approval)).ReviewRecorded);
        Assert.Empty(probe.Calls);
        Assert.Empty(executor.Calls);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(9)]
    public async Task Failed_atomic_write_resumes_from_actual_committed_review_records(int failedWrite)
    {
        using var store = new ReviewStore();
        var fault = new InterceptingStore(store.FileSystem) { FailBeforeWrite = failedWrite };
        var coordinator = new SetupCoordinator(fault, store.Clock);
        var approval = Approve(await coordinator.PreviewReviewAsync(store.Plan));
        var error = await Assert.ThrowsAsync<SetupException>(
            async () => await coordinator.RecordReviewAsync(store.Plan, approval));
        Assert.Equal(SetupFailure.JournalIoFailure, error.Failure);
        var prior = File.Exists(store.Path) ? SetupJournalCodec.Read(File.ReadAllBytes(store.Path)) : null;
        var reviewed = prior?.Steps.Where(s => s.Status == SetupJournalStepStatus.Reviewed)
            .ToDictionary(s => s.Id, s => s.ReviewedAtUtc) ?? [];
        store.Clock.UtcNow += TimeSpan.FromSeconds(1);
        var restarted = store.NewCoordinator();
        var result = await restarted.RecordReviewAsync(store.Plan,
            Approve(await restarted.PreviewReviewAsync(store.Plan)));
        Assert.True(result.ReviewRecorded);
        var after = SetupJournalCodec.Read(File.ReadAllBytes(store.Path));
        foreach (var step in after.Steps.Where(s => reviewed.ContainsKey(s.Id)))
            Assert.Equal(reviewed[step.Id], step.ReviewedAtUtc);
        Assert.Equal("preserve-user-data", File.ReadAllText(store.UnrelatedPath));
    }

    [Fact]
    public async Task Cancellation_after_committed_step_is_resumable_without_fake_completion()
    {
        using var store = new ReviewStore();
        using var cancel = new CancellationTokenSource();
        var fault = new InterceptingStore(store.FileSystem)
        {
            AfterWrite = count => { if (count == 3) cancel.Cancel(); }
        };
        var coordinator = new SetupCoordinator(fault, store.Clock);
        var approval = Approve(await coordinator.PreviewReviewAsync(store.Plan));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await coordinator.RecordReviewAsync(store.Plan, approval, cancel.Token));
        var preview = await store.NewCoordinator().PreviewReviewAsync(store.Plan);
        Assert.Equal(SetupPreviewState.Interrupted, preview.State);
        Assert.Single(preview.Steps.Where(s => s.JournaledReview));
        Assert.DoesNotContain(preview.Steps, s => s.JournaledComplete);
        Assert.True((await store.NewCoordinator().RecordReviewAsync(store.Plan, Approve(preview))).ReviewRecorded);
    }

    [Fact]
    public async Task Expiry_mid_review_preserves_partial_state_and_requires_fresh_exact_review()
    {
        using var store = new ReviewStore();
        var fault = new InterceptingStore(store.FileSystem)
        {
            AfterWrite = count => { if (count == 3) store.Clock.UtcNow = store.Plan.ExpiresAtUtc; }
        };
        var coordinator = new SetupCoordinator(fault, store.Clock);
        var approval = Approve(await coordinator.PreviewReviewAsync(store.Plan));
        var result = await coordinator.RecordReviewAsync(store.Plan, approval);
        Assert.False(result.ReviewRecorded);
        Assert.Equal(SetupFailure.PlanStale, result.Failure);
        Assert.Single(result.ReviewedSteps);
        Assert.False((await store.NewCoordinator().PreviewReviewAsync(store.Plan)).CanApprove);

        var host = SetupTestData.HostReport();
        var refreshed = new SetupPlanBuilder(store.Clock).Build(
            new SetupConfiguration("review-test", [SetupRole.Llm]), host, SetupTestData.ArtifactReport("ollama-llm"));
        var preview = await store.NewCoordinator().PreviewReviewAsync(refreshed);
        Assert.True(preview.RequiresJournalRollForward);
        Assert.DoesNotContain(preview.Steps, s => s.JournaledReview);
        Assert.True((await store.NewCoordinator().RecordReviewAsync(refreshed, Approve(preview))).ReviewRecorded);
        var journal = SetupJournalCodec.Read(File.ReadAllBytes(store.Path));
        Assert.Single(journal.Supersessions);
        Assert.All(journal.Steps, s => Assert.Equal(
            FingerprintBuilder.Create(refreshed.Fingerprint, s.Id), s.ReviewFingerprint));
    }

    [Fact]
    public async Task Two_previews_use_CAS_and_stale_approval_cannot_overwrite()
    {
        using var store = new ReviewStore();
        var first = Approve(await store.Coordinator.PreviewReviewAsync(store.Plan));
        var secondCoordinator = store.NewCoordinator();
        var second = Approve(await secondCoordinator.PreviewReviewAsync(store.Plan));
        Assert.True((await store.Coordinator.RecordReviewAsync(store.Plan, first)).ReviewRecorded);
        var bytes = File.ReadAllBytes(store.Path);
        Assert.Equal(SetupFailure.JournalConcurrentChange,
            (await secondCoordinator.RecordReviewAsync(store.Plan, second)).Failure);
        Assert.Equal(bytes, File.ReadAllBytes(store.Path));
    }

    [Fact]
    public async Task Review_and_internal_execution_journals_reject_each_other_without_writes()
    {
        using var store = new ReviewStore();
        Assert.True((await store.Coordinator.RecordReviewAsync(store.Plan,
            Approve(await store.Coordinator.PreviewReviewAsync(store.Plan)))).ReviewRecorded);
        var bytes = File.ReadAllBytes(store.Path);
        var error = await Assert.ThrowsAsync<SetupException>(
            async () => await store.Coordinator.PreviewAsync(store.Plan));
        Assert.Equal(SetupFailure.JournalPurposeMismatch, error.Failure);
        Assert.Equal(bytes, File.ReadAllBytes(store.Path));

        var executionPlan = SetupTestData.ReviewedPlan();
        var probe = new FakeStateProbe();
        var memory = new FakeSetupFileSystem();
        var execution = new SetupCoordinator(memory, probe, new FakeStepExecutor(probe),
            new MutableTimeProvider(SetupTestData.Now));
        var executionApproval = await SetupTestData.ApprovalAsync(execution, executionPlan);
        Assert.Equal(SetupRunState.Completed, (await execution.RunAsync(executionPlan, executionApproval)).State);
        File.WriteAllBytes(store.Path, memory.Content!);
        error = await Assert.ThrowsAsync<SetupException>(
            async () => await store.Coordinator.PreviewReviewAsync(executionPlan));
        Assert.Equal(SetupFailure.JournalPurposeMismatch, error.Failure);
        Assert.Equal(memory.Content, File.ReadAllBytes(store.Path));

        var v1 = JsonNode.Parse(memory.Content!)!.AsObject();
        v1["formatVersion"] = 1;
        v1.Remove("purpose");
        foreach (var step in v1["steps"]!.AsArray())
        {
            step!.AsObject().Remove("reviewFingerprint");
            step.AsObject().Remove("reviewedAtUtc");
        }
        // Seal the actual old serializer shape; rejection must be version/purpose, not a bad hash.
        v1["integritySha256"] = "";
        v1["integritySha256"] = FingerprintBuilder.Bytes(Encoding.UTF8.GetBytes(v1.ToJsonString()));
        var oldBytes = Encoding.UTF8.GetBytes(v1.ToJsonString());
        File.WriteAllBytes(store.Path, oldBytes);
        error = await Assert.ThrowsAsync<SetupException>(
            async () => await store.Coordinator.PreviewReviewAsync(executionPlan));
        Assert.Equal(SetupFailure.JournalCorrupt, error.Failure);
        Assert.Equal(oldBytes, File.ReadAllBytes(store.Path));
    }

    [Theory]
    [InlineData("configuration", SetupFailure.ConfigurationChanged)]
    [InlineData("host", SetupFailure.HostFactsChanged)]
    [InlineData("artifact", SetupFailure.ArtifactFactsChanged)]
    public async Task Review_approval_binds_exact_source_facts(string changed, SetupFailure expected)
    {
        using var store = new ReviewStore();
        var original = SetupTestData.ReviewedPlan();
        store.Clock.UtcNow = SetupTestData.Now;
        var approval = Approve(await store.Coordinator.PreviewReviewAsync(original));
        var changedPlan = SetupTestData.ReviewedPlan(
            configurationRevision: changed == "configuration" ? "config-2" : "config-1",
            hostRevision: changed == "host" ? "host-2" : "host-1",
            artifactRevision: changed == "artifact" ? "artifact-2" : "artifact-1");
        Assert.Equal(expected, (await store.Coordinator.RecordReviewAsync(changedPlan, approval)).Failure);
        Assert.False(File.Exists(store.Path));
    }

    [Fact]
    public void Execution_composition_is_not_public()
    {
        Assert.Null(typeof(SetupCoordinator).GetMethod("RunAsync"));
        Assert.Null(typeof(SetupCoordinator).GetMethod("PreviewAsync"));
        Assert.Single(typeof(SetupCoordinator).GetConstructors());
    }

    internal static SetupApproval Approve(SetupPreview preview) =>
        preview.Approve(SetupApprovalDecision.Approve, [SetupConsentScope.LocalJournal]);
}

internal sealed class ReviewStore : IDisposable
{
    private readonly string directory = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "Martlet.H05a.Review." + Guid.NewGuid().ToString("N"));
    internal string Path => System.IO.Path.Combine(directory, "review.json");
    internal string UnrelatedPath => System.IO.Path.Combine(directory, "user-data.txt");
    internal LocalSetupFileSystem FileSystem { get; }
    internal MutableTimeProvider Clock { get; }
    internal SetupPlan Plan { get; }
    internal SetupCoordinator Coordinator { get; }

    internal ReviewStore()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(UnrelatedPath, "preserve-user-data");
        FileSystem = new(Path, new RecordingDirectoryCommitter());
        var host = SetupTestData.HostReport();
        Clock = new(host.CreatedAt.AddMinutes(1));
        Plan = new SetupPlanBuilder(Clock).Build(
            new SetupConfiguration("review-test", [SetupRole.Llm]), host, SetupTestData.ArtifactReport("ollama-llm"));
        Coordinator = NewCoordinator();
    }

    internal SetupCoordinator NewCoordinator() =>
        new(new LocalSetupFileSystem(Path, new RecordingDirectoryCommitter()), Clock);

    public void Dispose() => Directory.Delete(directory, recursive: true);
}

internal sealed class InterceptingStore(ISetupFileSystem inner) : ISetupFileSystem
{
    private int writes;
    internal int? FailBeforeWrite { get; init; }
    internal Action<int>? AfterWrite { get; init; }
    public string JournalPath => inner.JournalPath;
    public ValueTask<SetupFileSnapshot?> ReadAsync(int maximumBytes, CancellationToken cancellationToken) =>
        inner.ReadAsync(maximumBytes, cancellationToken);
    public async ValueTask<SetupFileSnapshot> WriteAtomicAsync(
        string? expectedVersion, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        writes++;
        if (writes == FailBeforeWrite) throw new SetupException(SetupFailure.JournalIoFailure);
        var result = await inner.WriteAtomicAsync(expectedVersion, content, cancellationToken);
        AfterWrite?.Invoke(writes);
        return result;
    }
}
