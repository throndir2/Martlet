using System.Text;
using Martlet.Core.Settings;

namespace Martlet.Updates.Tests;

public sealed class RollbackRestoreTests(SigningKeys keys) : IClassFixture<SigningKeys>
{
    internal static RollbackRestoreApproval Approve(RollbackRestorePlan plan) =>
        plan.Approve(plan.OperationId, plan.ExpectedSelectionRevision, plan.ExpectedSettingsRevision, plan.PlanDigest);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualV07aRestoreOfRecordedRollbackNeedsDistinctConsentAndNeverRuns(bool legacy)
    {
        using var f = new SelectionFixture(keys, legacy);
        var rollback = f.Rollback();
        var engine = f.Engine();
        var effects = new SetupOperationRunner();
        var before = File.ReadAllBytes(f.Settings.FilePath);
        var control = File.ReadAllBytes(f.Control);
        var history = History(f);
        var plan = await engine.PreviewRollbackConfigurationRestore(rollback.Revision, effects).Completion;
        Assert.Equal(before, File.ReadAllBytes(f.Settings.FilePath));
        Assert.Equal(control, File.ReadAllBytes(f.Control));
        Assert.Equal(2, Wire.Read<ControlDocument>(control, 32768).FormatVersion);
        Assert.Equal(rollback.CurrentSelection!.SnapshotRelativePath, plan.SnapshotRelativePath);
        Assert.Equal(rollback.CurrentSelection.SnapshotFileDigest, plan.Snapshot.FileDigest);
        Assert.Equal(f.Package.Current.SettingsRevision, plan.ExpectedSettingsRevision);
        Assert.Equal(f.Package.Current.SettingsRevision.ToUpperInvariant(), plan.ExpectedSettingsRevision);
        Assert.Equal(legacy ? 1 : 2, plan.CurrentSettingsSchemaVersion);
        Assert.Contains("format 3", plan.PlannedEffects);
        Assert.Contains("NOT RUNNABLE", plan.PlannedEffects);
        var result = await engine.RestoreRollbackConfiguration(plan, Approve(plan), effects).Completion;
        Assert.Null(result.Failure);
        Assert.Null(result.ConfigurationFailure);
        Assert.Null(result.PackageFailure);
        Assert.False(result.OwnershipFailed);
        Assert.Equal(RollbackSettingsProgress.VerifiedCommitted, result.SettingsProgress);
        Assert.Equal(RollbackAcknowledgment.Recorded, result.Acknowledgment);
        Assert.Equal(SelectionStatus.AwaitingReadiness, result.Selection!.Status);
        Assert.False(result.IsRunnable);
        Assert.False(result.Selection.IsRunnable);
        Assert.Equal(rollback.Revision + 2, result.Selection.Revision);
        Assert.Equal(plan.ProfileId, result.ProfileId);
        Assert.Equal(2, result.SettingsSchemaVersion);
        Assert.Equal(plan.CandidateDigest, result.ResultingSettingsRevision);
        Assert.Equal(Encoding.UTF8.GetBytes(plan.CandidateJson), File.ReadAllBytes(f.Settings.FilePath));
        Assert.Equal(before, File.ReadAllBytes(result.OriginalSnapshot!));
        var stored = await f.Settings.LoadAsync();
        Assert.Equal(plan.CandidateDigest, stored.Revision);
        Assert.Equal(ProfileKind.NotConfigured, stored.Settings!.Profile.Kind);
        Assert.Equal(legacy ? 0 : 1, stored.Settings.Setup!.PendingRemovals.Count);
        Assert.Equal(3, Wire.Read<ControlDocument>(File.ReadAllBytes(f.Control), 32768).FormatVersion);
        foreach (var pair in history.Where(pair => pair.Key != f.Control)) Assert.Equal(pair.Value, File.ReadAllBytes(pair.Key));
        var complete = History(f);
        for (var index = 0; index < 2; index++)
        {
            var inspected = f.Engine().Inspect();
            Assert.Equal(RollbackRestoreProgress.Recorded, inspected.ConfigurationRestoreProgress);
            Assert.Equal(result.Selection.Revision, inspected.Revision);
            Assert.Equal(SelectionOutcome.Unchanged, f.Engine().Recover().Outcome);
        }
        foreach (var pair in complete) Assert.Equal(pair.Value, File.ReadAllBytes(pair.Key));
        f.Package.AssertPrivateDataUnchanged();
    }

    [Fact]
    public async Task RestorePlanAndResultExposeNoMutableOrConstructibleAuthority()
    {
        using var f = new SelectionFixture(keys);
        var rollback = f.Rollback();
        var engine = f.Engine();
        var effects = new SetupOperationRunner();
        var plan = await engine.PreviewRollbackConfigurationRestore(rollback.Revision, effects).Completion;
        foreach (var type in new[] { typeof(RollbackRestorePlan), typeof(RollbackRestoreApproval),
            typeof(RollbackRestoreResult), typeof(RollbackRestoreOperation<RollbackRestorePlan>) })
        {
            Assert.Empty(type.GetConstructors());
            Assert.All(type.GetProperties(), property => Assert.Null(property.SetMethod));
        }
        Assert.DoesNotContain(f.Root, plan.ToString());
        Assert.DoesNotContain("pending_removals", plan.ToString());
        Assert.DoesNotContain(typeof(LocalSelectionEngine).GetMethods(), method =>
            method.GetParameters().Any(parameter => parameter.ParameterType == typeof(ConfigurationRecoveryReceipt) ||
                parameter.ParameterType == typeof(Func<bool>)));
        var approval = Approve(plan);
        var result = await engine.RestoreRollbackConfiguration(plan, approval, effects).Completion;
        Assert.DoesNotContain(f.Root, result.ToString());
        Assert.Equal(SelectionFailure.Conflict, Assert.Throws<SelectionException>(() =>
            engine.RestoreRollbackConfiguration(plan, approval, effects)).Failure);
    }

    [Theory]
    [InlineData("operation")]
    [InlineData("selection")]
    [InlineData("settings-case")]
    [InlineData("digest")]
    public async Task WrongConsentIssuanceConsumesItsOnlyAttempt(string kind)
    {
        using var f = new SelectionFixture(keys);
        var rollback = f.Rollback();
        var engine = f.Engine();
        var plan = await engine.PreviewRollbackConfigurationRestore(rollback.Revision, new()).Completion;
        Assert.Equal(SelectionFailure.Conflict, Assert.Throws<SelectionException>(() => plan.Approve(
            kind == "operation" ? Guid.NewGuid() : plan.OperationId,
            plan.ExpectedSelectionRevision + (kind == "selection" ? 1 : 0),
            kind == "settings-case" ? plan.ExpectedSettingsRevision.ToLowerInvariant() : plan.ExpectedSettingsRevision,
            kind == "digest" ? new string('0', 64) : plan.PlanDigest)).Failure);
        Assert.Throws<SelectionException>(() => Approve(plan));
        Assert.Equal(SelectionStatus.AwaitingConfigurationRestore, engine.Inspect().Status);
    }

    [Theory]
    [InlineData("runner")]
    [InlineData("engine")]
    [InlineData("superseded")]
    [InlineData("failed-preview")]
    [InlineData("revision")]
    [InlineData("source")]
    [InlineData("stage")]
    [InlineData("receipt")]
    public async Task ApprovalCannotEscapeExactOwningState(string change)
    {
        using var f = new SelectionFixture(keys);
        var rollback = f.Rollback();
        var engine = f.Engine();
        var effects = new SetupOperationRunner();
        var plan = await engine.PreviewRollbackConfigurationRestore(rollback.Revision, effects).Completion;
        var approval = Approve(plan);
        var control = File.ReadAllBytes(f.Control);
        if (change == "superseded") await engine.PreviewRollbackConfigurationRestore(rollback.Revision, effects).Completion;
        if (change == "failed-preview")
            await Assert.ThrowsAsync<SelectionException>(() => engine.PreviewRollbackConfigurationRestore(rollback.Revision + 1, effects).Completion);
        if (change == "revision")
        {
            var loaded = await f.Settings.LoadAsync();
            Assert.True((await f.Settings.SaveAsync(loaded.Settings! with
            { Profile = loaded.Settings!.Profile with { Kind = ProfileKind.Fixture } }, loaded.Revision)).Saved);
        }
        if (change == "source") File.AppendAllText(Path.Combine(f.Root, plan.SnapshotRelativePath.Replace('/', '\\')), " ");
        if (change is "stage" or "receipt")
            File.AppendAllText(Path.Combine(f.Package.StagingRoot, plan.Candidate.StageName,
                change == "receipt" ? "staged.json" : "candidate.json"), " ");
        var actualEngine = change == "engine" ? f.Engine() : engine;
        var actualRunner = change == "runner" ? new SetupOperationRunner() : effects;
        var original = File.ReadAllBytes(f.Settings.FilePath);
        var failure = await Record.ExceptionAsync(async () =>
            await actualEngine.RestoreRollbackConfiguration(plan, approval, actualRunner).Completion);
        Assert.True(failure is SelectionException or RecoveryException or StagingException);
        Assert.Equal(control, File.ReadAllBytes(f.Control));
        Assert.Equal(original, File.ReadAllBytes(f.Settings.FilePath));
        Assert.Throws<SelectionException>(() => engine.RestoreRollbackConfiguration(plan, approval, effects));
    }

    [Fact]
    public async Task RequiredRestoreCannotBeClearedBySelectingAnotherCandidate()
    {
        using var f = new SelectionFixture(keys);
        var rollback = f.Rollback();
        var next = f.Stage("0.4.0.0");
        var engine = f.Engine();
        var control = File.ReadAllBytes(f.Control);
        Assert.Equal(SelectionFailure.ConfigurationRestoreRequired, Assert.Throws<SelectionException>(() =>
            engine.PrepareActivation(next, rollback.Revision, f.Snapshot())).Failure);
        Assert.Equal(SelectionFailure.ConfigurationRestoreRequired, Assert.Throws<SelectionException>(() =>
            engine.PrepareRollback(rollback.Revision, f.Snapshot())).Failure);
        Assert.Equal(control, File.ReadAllBytes(f.Control));
        var effects = new SetupOperationRunner();
        var plan = await engine.PreviewRollbackConfigurationRestore(rollback.Revision, effects).Completion;
        await engine.RestoreRollbackConfiguration(plan, Approve(plan), effects).Completion;
        f.RefreshFacts();
        var stage = f.Stage("0.5.0.0");
        var selected = f.Select(stage);
        Assert.Equal(SelectionStatus.AwaitingReadiness, selected.Status);
        Assert.Equal(3, Wire.Read<ControlDocument>(File.ReadAllBytes(f.Control), 32768).FormatVersion);
        Assert.Equal(selected.Revision, f.Engine().Recover().Revision);
    }

    [Theory]
    [InlineData("bootstrap")]
    [InlineData("activation")]
    [InlineData("trust")]
    [InlineData("capacity")]
    public async Task OnlyAnAuthoritativeCompatibleRollbackCanBePreviewed(string kind)
    {
        using var f = new SelectionFixture(keys);
        SelectionReceipt selected;
        if (kind == "bootstrap") selected = f.Initialize();
        else if (kind == "activation") { f.Initialize(); selected = f.Select(f.Stage()); }
        else selected = f.Rollback();
        if (kind == "capacity")
            while (Directory.GetDirectories(f.Root, "transaction-*").Length < 32)
                Directory.CreateDirectory(Path.Combine(f.Root, "transaction-" + Guid.NewGuid().ToString("N")));
        var engine = kind == "trust"
            ? f.Engine(staging: new(f.Package.StagingRoot, new([]), () => f.Package.Current)) : f.Engine();
        var before = File.ReadAllBytes(f.Control);
        Assert.NotNull(await Record.ExceptionAsync(async () =>
            await engine.PreviewRollbackConfigurationRestore(selected.Revision, new()).Completion));
        Assert.Equal(before, File.ReadAllBytes(f.Control));
        Assert.Empty(Directory.GetFiles(f.Settings.DataDirectory, "settings.recovery.*.bak"));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task OriginalMonotonicAndUtcBudgetsAreNotRenewed(bool monotonic, bool utc)
    {
        using var f = new SelectionFixture(keys);
        var rollback = f.Rollback();
        var clock = new RestoreTestClock();
        var engine = new LocalSelectionEngine(f.Root, f.Staging, f.Settings) { RestoreClock = clock };
        var effects = new SetupOperationRunner();
        var plan = await engine.PreviewRollbackConfigurationRestore(rollback.Revision, effects).Completion;
        var approval = Approve(plan);
        clock.Advance(monotonic ? TimeSpan.FromMinutes(5) : TimeSpan.Zero,
            utc ? TimeSpan.FromMinutes(5) : TimeSpan.FromHours(-1));
        var error = await Assert.ThrowsAsync<SelectionException>(() =>
            engine.RestoreRollbackConfiguration(plan, approval, effects).Completion);
        Assert.Equal(SelectionFailure.ConsentExpired, error.Failure);
        Assert.Equal(rollback.Revision, engine.Inspect().Revision);
        Assert.Empty(Directory.GetFiles(f.Settings.DataDirectory, "settings.recovery.*.bak"));
    }

    [Fact]
    public async Task RealCurrentCredentialCleanupWinsOverHistoricalImportedBindings()
    {
        using var f = new SelectionFixture(keys);
        var loaded = await f.Settings.LoadAsync();
        var historical = SetupSettings.SelectRoute(loaded.Settings!, SetupRole.Tts, "tts-1", "alloy");
        var historicalId = Guid.NewGuid();
        historical = SetupSettings.ReplaceRoute(historical, historical.Setup!.Routes.Single().WithCredential(historicalId));
        Assert.True((await f.Settings.SaveAsync(historical, loaded.Revision)).Saved);
        f.RefreshFacts();
        f.Initialize();
        f.Select(f.Stage());
        var second = f.Select(f.Stage("0.3.0.0"));
        loaded = await f.Settings.LoadAsync();
        var currentId = Guid.NewGuid();
        var current = SetupSettings.ReplaceRoute(loaded.Settings!, loaded.Settings!.Setup!.Routes.Single().WithCredential(currentId));
        current = current with
        {
            Setup = current.Setup! with
            {
                PendingRemovals = current.Setup.PendingRemovals.Concat(
                    [new PendingCredentialRemoval { Role = SetupRole.Tts, CredentialId = historicalId }]).ToArray()
            }
        };
        Assert.True((await f.Settings.SaveAsync(current, loaded.Revision)).Saved);
        f.RefreshFacts();
        var engine = f.Engine();
        var selectionPlan = engine.PrepareRollback(second.Revision, f.Snapshot());
        var rollback = engine.CommitSelection(selectionPlan, SelectionFixture.Approve(selectionPlan));
        var effects = new SetupOperationRunner();
        var plan = await engine.PreviewRollbackConfigurationRestore(rollback.Revision, effects).Completion;
        var original = File.ReadAllBytes(f.Settings.FilePath);
        var result = await engine.RestoreRollbackConfiguration(plan, Approve(plan), effects).Completion;
        Assert.Equal(RollbackAcknowledgment.Recorded, result.Acknowledgment);
        var restored = (await f.Settings.LoadAsync()).Settings!;
        Assert.Null(restored.Setup!.Routes.Single().CredentialId);
        Assert.Null(restored.Setup.Routes.Single().Consent);
        Assert.Contains(restored.Setup.PendingRemovals, item => item.CredentialId == currentId);
        Assert.All(current.Setup!.PendingRemovals, item => Assert.Contains(item, restored.Setup.PendingRemovals));
        Assert.Equal(current.Profile.Credentials, restored.Profile.Credentials);
        Assert.Equal(original, File.ReadAllBytes(result.OriginalSnapshot!));
        f.Package.AssertPrivateDataUnchanged();
    }

    internal static Dictionary<string, byte[]> History(SelectionFixture f) =>
        Directory.GetFiles(f.Root, "*", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith(".lock", StringComparison.Ordinal))
            .ToDictionary(path => path, File.ReadAllBytes, StringComparer.Ordinal);
}

internal sealed class RestoreTestClock : TimeProvider
{
    private long timestamp;
    private DateTimeOffset utc = DateTimeOffset.UtcNow;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => timestamp;
    public override DateTimeOffset GetUtcNow() => utc;
    internal void Advance(TimeSpan elapsed, TimeSpan wall) { timestamp += elapsed.Ticks; utc += wall; }
}
