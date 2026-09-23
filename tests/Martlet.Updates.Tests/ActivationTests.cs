using Martlet.Core.Settings;
using Martlet.Core.Contracts;
using static Martlet.Updates.Tests.ActivationFixture;

namespace Martlet.Updates.Tests;

public sealed class ActivationTests(SigningKeys keys, ProductionPayloadFixture production)
    : IClassFixture<SigningKeys>, IClassFixture<ProductionPayloadFixture>
{
    private static ActivationException Fails(ActivationFailure failure, Action action)
    {
        var error = Assert.Throws<ActivationException>(action);
        Assert.Equal(failure, error.Failure);
        return error;
    }

    [Fact]
    public void ProductionStagePublishesExactInternalPointerWithoutLaunchAuthority()
    {
        using var f = new ActivationFixture(keys);
        f.Selection.Initialize();
        var selected = f.Selection.Select(f.Selection.Stage(production: production));
        Assert.False(selected.IsRunnable);
        ActivationReadinessRequest? observed = null;
        var engine = f.Engine((request, _) =>
        {
            observed = request;
            Assert.Equal(f.Selection.Package.Files["Desktop/Martlet.Desktop.exe"],
                File.ReadAllBytes(request.ExecutablePath));
            return ActivationProbeOutcome.Ready;
        });
        var initialized = f.Initialize(engine, selected.Revision);
        Assert.Equal(ActivationStatus.NoActiveVersion, initialized.Status);
        Assert.False(initialized.IsRunnable);
        var plan = f.Prepare(engine, initialized.Revision, selected.Revision);
        Assert.Equal(ActivationTransitionKind.Activation, plan.Kind);
        Assert.Equal("0.2.0.0", plan.Candidate.Version);
        Assert.Equal("Desktop/Martlet.Desktop.exe", plan.Candidate.ExecutableRelativePath);
        Assert.Null(plan.PreviousAfterActivation);
        Assert.Contains("pending non-runnable", plan.PlannedEffects);
        var result = engine.Activate(plan, Approve(plan));
        Assert.False(result.IsRunnable);
        Assert.Equal(ActivationReadiness.MissingReadiness, result.Readiness);
        Assert.Equal(ActivationStatus.PointerPublished, result.Status);
        Assert.Equal(ActivationOutcome.Activated, result.Outcome);
        Assert.Equal(2, result.Revision);
        Assert.Equal("0.2.0.0", result.Current!.Version);
        Assert.Null(result.Previous);
        Assert.NotNull(observed);
        Assert.Equal(plan.TransactionId, observed.TransactionId);
        Assert.Equal(plan.ExpectedSettingsRevision, observed.SettingsRevision);
        Assert.Equal(selected.Revision, observed.SelectionRevision);
        Assert.Equal(result.Current.ExecutableSha256, observed.ExecutableSha256);
        Assert.Equal(result.Current.ReadinessResultSha256,
            Wire.Hash(File.ReadAllBytes(Path.Combine(f.Root,
                "transaction-" + plan.TransactionId.ToString("N"), "readiness-result.json"))));
        Assert.Equal(result.Revision, f.Engine().Inspect().Revision);
        Assert.False(f.Engine().Inspect().IsRunnable);
        Assert.Equal(ActivationReadiness.MissingReadiness, f.Engine().Inspect().Readiness);
        Assert.False(f.Selection.Engine().Inspect().IsRunnable);
        Assert.Equal(new[]
        {
            "active-publication.json", "before.json", "journal-v1.json",
            "pending-publication.json", "readiness-intent.json", "readiness-result.json"
        }, Directory.GetFiles(Path.Combine(f.Root, "transaction-" + plan.TransactionId.ToString("N")))
            .Select(Path.GetFileName).Order());
        f.Selection.Package.AssertPrivateDataUnchanged();
        production.AssertNotExecuted();
    }

    [Fact]
    public void DefaultCoordinatorCannotPrepareOrExecuteCandidate()
    {
        using var f = new ActivationFixture(keys);
        f.Selection.Initialize();
        var selected = f.Selection.Select(f.Selection.Stage(production: production));
        var engine = f.Engine();
        f.Initialize(engine, selected.Revision);
        var before = Directory.GetFiles(f.Root, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.ReadAllBytes);
        Fails(ActivationFailure.ReadinessUnavailable,
            () => engine.PrepareActivation(0, selected.Revision));
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(f.Root, "*", SearchOption.AllDirectories).Order());
        foreach (var pair in before) Assert.Equal(pair.Value, File.ReadAllBytes(pair.Key));
        Assert.False(File.Exists(f.Selection.Package.ExecutedCanary));
        production.AssertNotExecuted();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecordedConfigurationRestoreIsRequiredBeforeDeterministicRollback(bool interrupted)
    {
        using var f = new ActivationFixture(keys);
        f.Selection.Initialize();
        var firstSelection = f.Selection.Select(f.Selection.Stage(production: production));
        var probeCalls = 0;
        var activation = f.Engine((_, _) =>
        {
            probeCalls++;
            return ActivationProbeOutcome.Ready;
        });
        f.Initialize(activation, firstSelection.Revision);
        var firstPlan = f.Prepare(activation, 0, firstSelection.Revision);
        var first = activation.Activate(firstPlan, Approve(firstPlan));

        var secondSelection = f.Selection.Select(f.Selection.Stage("0.3.0.0", production: production));
        var secondPlan = f.Prepare(activation, first.Revision, secondSelection.Revision);
        var second = activation.Activate(secondPlan, Approve(secondPlan));
        Assert.Equal(ActivationTransitionKind.Activation, secondPlan.Kind);
        Assert.Equal("0.3.0.0", second.Current!.Version);
        Assert.Equal("0.2.0.0", second.Previous!.Version);

        f.Selection.ChangeSettings();
        var selectionEngine = f.Selection.Engine(io: (point, _, _) =>
        {
            if (interrupted && point == SelectionIoPoint.BeforeRestoreAcknowledgment) throw new IOException();
        });
        var rollbackPlan = selectionEngine.PrepareRollback(secondSelection.Revision, f.Selection.Snapshot());
        var rollbackSelection = selectionEngine.CommitSelection(rollbackPlan,
            SelectionFixture.Approve(rollbackPlan));
        Assert.Equal(SelectionStatus.AwaitingConfigurationRestore, rollbackSelection.Status);
        Fails(ActivationFailure.ConfigurationRestoreRequired,
            () => activation.PrepareActivation(second.Revision, rollbackSelection.Revision));
        Assert.Equal(2, probeCalls);

        var effects = new SetupOperationRunner();
        var restorePlan = await selectionEngine
            .PreviewRollbackConfigurationRestore(rollbackSelection.Revision, effects).Completion;
        var restored = await selectionEngine.RestoreRollbackConfiguration(restorePlan,
            RollbackRestoreTests.Approve(restorePlan), effects).Completion;
        long restoredRevision;
        if (interrupted)
        {
            Assert.NotEqual(RollbackAcknowledgment.Recorded, restored.Acknowledgment);
            Fails(ActivationFailure.ConfigurationRestoreRequired,
                () => activation.PrepareActivation(second.Revision, selectionEngine.Inspect().Revision));
            var ackPlan = await selectionEngine.PreviewRollbackConfigurationAcknowledgment(
                selectionEngine.Inspect().Revision, effects).Completion;
            var acknowledged = await selectionEngine.AcknowledgeRollbackConfiguration(ackPlan,
                RollbackAcknowledgmentTests.ApproveAcknowledgment(ackPlan), effects).Completion;
            Assert.Null(acknowledged.Failure);
            restoredRevision = acknowledged.Selection!.Revision;
        }
        else
        {
            Assert.Equal(RollbackAcknowledgment.Recorded, restored.Acknowledgment);
            restoredRevision = restored.Selection!.Revision;
        }
        var restoredBytes = File.ReadAllBytes(f.Selection.Settings.FilePath);
        Assert.False((await f.Selection.Settings.LoadAsync()).Settings!.Memory!.Enabled);
        var activationRollback = f.Prepare(activation, second.Revision, restoredRevision);
        Assert.Equal(ActivationTransitionKind.Rollback, activationRollback.Kind);
        Assert.Equal("0.2.0.0", activationRollback.Candidate.Version);
        Assert.Equal("0.3.0.0", activationRollback.PreviousAfterActivation!.Version);
        var result = activation.Activate(activationRollback, Approve(activationRollback));
        Assert.Equal(ActivationOutcome.RolledBack, result.Outcome);
        Assert.False(result.IsRunnable);
        Assert.Equal(ActivationStatus.PointerPublished, result.Status);
        Assert.Equal("0.2.0.0", result.Current!.Version);
        Assert.Equal("0.3.0.0", result.Previous!.Version);
        Assert.Equal(3, probeCalls);
        Assert.Equal(restoredBytes, File.ReadAllBytes(f.Selection.Settings.FilePath));
        f.Selection.Package.AssertPrivateDataUnchanged();
        production.AssertNotExecuted();
    }

    [Theory]
    [InlineData("settings", ActivationFailure.Conflict)]
    [InlineData("selection", ActivationFailure.Conflict)]
    [InlineData("stage", ActivationFailure.InvalidSelectedVersion)]
    [InlineData("control", ActivationFailure.InvalidControl)]
    public void FrozenPlanRejectsEveryChangedAuthority(string change, ActivationFailure expected)
    {
        using var f = new ActivationFixture(keys);
        f.Selection.Initialize();
        var selected = f.Selection.Select(f.Selection.Stage());
        var engine = f.Engine((_, _) => ActivationProbeOutcome.Ready);
        f.Initialize(engine, selected.Revision);
        var plan = f.Prepare(engine, 0, selected.Revision);
        var approval = Approve(plan);
        var control = File.ReadAllBytes(f.Control);
        switch (change)
        {
            case "settings":
                f.Selection.ChangeSettings();
                break;
            case "selection":
                f.Selection.Select(f.Selection.Stage("0.3.0.0"));
                break;
            case "stage":
                File.AppendAllText(Path.Combine(f.Selection.Package.StagingRoot,
                    selected.CurrentSelection!.StageName, "candidate.json"), " ");
                break;
            case "control":
                var state = Wire.Read<ActivationControlDocument>(control, 65536);
                File.WriteAllBytes(f.Control, Wire.Write(state with { Revision = 1 }));
                break;
        }
        Fails(expected, () => engine.Activate(plan, approval));
        Assert.Empty(Directory.GetDirectories(f.Root, "transaction-*"));
        if (change != "control") Assert.Equal(control, File.ReadAllBytes(f.Control));
        Fails(ActivationFailure.Conflict, () => engine.Activate(plan, approval));
        f.Selection.Package.AssertPrivateDataUnchanged();
    }

    [Fact]
    public void FailedReadinessIsDurableNonRunnableAndRecoveryNeverReplaysProbe()
    {
        using var f = new ActivationFixture(keys);
        f.Selection.Initialize();
        var firstSelection = f.Selection.Select(f.Selection.Stage());
        var ready = f.Engine((_, _) => ActivationProbeOutcome.Ready);
        f.Initialize(ready, firstSelection.Revision);
        var firstPlan = f.Prepare(ready, 0, firstSelection.Revision);
        var first = ready.Activate(firstPlan, Approve(firstPlan));
        var secondSelection = f.Selection.Select(f.Selection.Stage("0.3.0.0"));
        var calls = 0;
        var failing = f.Engine((_, _) =>
        {
            calls++;
            return ActivationProbeOutcome.NotReady;
        });
        var plan = f.Prepare(failing, first.Revision, secondSelection.Revision);
        var error = Fails(ActivationFailure.ReadinessFailed,
            () => failing.Activate(plan, Approve(plan)));
        Assert.Equal(plan.TransactionId, error.TransactionId);
        Assert.Equal(1, calls);
        var pending = failing.Inspect();
        Assert.Equal(ActivationStatus.ReadinessPending, pending.Status);
        Assert.False(pending.IsRunnable);
        Assert.Equal("0.2.0.0", pending.Current!.Version);
        var directory = Path.Combine(f.Root, "transaction-" + plan.TransactionId.ToString("N"));
        var evidence = Wire.Read<ActivationReadinessDocument>(
            File.ReadAllBytes(Path.Combine(directory, "readiness-result.json")), 32768, canonical: true);
        Assert.Equal(ActivationReadinessOutcome.Failed, evidence.Outcome);
        var recovered = failing.Recover(plan.TransactionId);
        Assert.Equal(ActivationOutcome.RecoveredPrevious, recovered.Outcome);
        Assert.False(recovered.IsRunnable);
        Assert.Equal(ActivationStatus.PointerPublished, recovered.Status);
        Assert.Equal("0.2.0.0", recovered.Current!.Version);
        Assert.Equal(1, calls);
        Assert.Equal(recovered.Revision, failing.Recover().Revision);
        Assert.Equal(1, calls);

        var retry = f.Engine((_, _) => ActivationProbeOutcome.Ready);
        var retryPlan = f.Prepare(retry, recovered.Revision, secondSelection.Revision);
        Assert.Equal("0.3.0.0", retry.Activate(retryPlan, Approve(retryPlan)).Current!.Version);
        f.Selection.Package.AssertPrivateDataUnchanged();
    }

    [Theory]
    [InlineData("format")]
    [InlineData("legacy-root")]
    [InlineData("result")]
    public void OldOrTamperedActivationEvidenceFailsClosedWithoutRepair(string change)
    {
        using var f = new ActivationFixture(keys);
        f.Selection.Initialize();
        var selected = f.Selection.Select(f.Selection.Stage());
        var engine = f.Engine((_, _) => ActivationProbeOutcome.Ready);
        f.Initialize(engine, selected.Revision);
        var plan = f.Prepare(engine, 0, selected.Revision);
        engine.Activate(plan, Approve(plan));
        if (change == "format")
        {
            var state = Wire.Read<ActivationControlDocument>(File.ReadAllBytes(f.Control), 65536);
            File.WriteAllBytes(f.Control, Wire.Write(state with { FormatVersion = 0 }));
        }
        if (change == "legacy-root")
            File.WriteAllText(Path.Combine(f.Root, "activation.json"), "{}");
        if (change == "result")
            File.AppendAllText(Path.Combine(f.Root, "transaction-" + plan.TransactionId.ToString("N"),
                "readiness-result.json"), " ");
        var retained = Directory.GetFiles(f.Root, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.ReadAllBytes);
        var expected = change == "result"
            ? ActivationFailure.PublicationAmbiguous : ActivationFailure.InvalidControl;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            Fails(expected, () => f.Engine().Inspect());
            Fails(expected, () => f.Engine().Recover());
        }
        foreach (var pair in retained) Assert.Equal(pair.Value, File.ReadAllBytes(pair.Key));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CurrentTrustIsReappliedToEveryPointerInspection(bool omitted)
    {
        using var f = new ActivationFixture(keys);
        f.Selection.Initialize();
        var selected = f.Selection.Select(f.Selection.Stage());
        var engine = f.Engine((_, _) => ActivationProbeOutcome.Ready);
        f.Initialize(engine, selected.Revision);
        var plan = f.Prepare(engine, 0, selected.Revision);
        engine.Activate(plan, Approve(plan));
        var pointer = File.ReadAllBytes(f.Control);
        var revokedStaging = new LocalStagingEngine(f.Selection.Package.StagingRoot,
            new UpdateTrustPolicy(omitted ? [] : [keys.Stranger.ExportSubjectPublicKeyInfo()]),
            () => f.Selection.Package.Current);
        var revokedSelection = f.Selection.Engine(staging: revokedStaging);
        var reopened = f.Engine(selection: revokedSelection);
        Fails(ActivationFailure.InvalidSelectedVersion, () => reopened.Inspect());
        Assert.Equal(pointer, File.ReadAllBytes(f.Control));
        Assert.False(f.Selection.Package.ExecutedCanary is { } canary && File.Exists(canary));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task HistoricalSettingsAndReceiptsKeepTheirExactSchemaAndBytes(int schema)
    {
        using var f = new ActivationFixture(keys);
        var loaded = (await f.Selection.Settings.LoadAsync()).Settings!;
        var historical = loaded with
        {
            SchemaVersion = schema,
            Setup = schema >= 2 ? loaded.Setup : null,
            Audio = schema >= 2 ? loaded.Audio : null,
            Companion = schema >= 3 ? loaded.Companion : null,
            Memory = schema >= 4 ? loaded.Memory!.Configure(true, MemoryStoragePolicy.AppLocalData, null) : null
        };
        historical.Validate();
        File.WriteAllBytes(f.Selection.Settings.FilePath, ContractJson.Write(historical));
        f.Selection.RefreshFacts();
        f.Selection.Initialize();
        var selected = f.Selection.Select(f.Selection.Stage(maximumReader: schema));
        var settings = File.ReadAllBytes(f.Selection.Settings.FilePath);
        var selectionHistory = RollbackRestoreTests.History(f.Selection);
        var engine = f.Engine((_, _) => ActivationProbeOutcome.Ready);
        engine.Initialize(selected.Revision);
        var plan = engine.PrepareActivation(0, selected.Revision);
        Assert.Equal(schema, plan.ExpectedSettingsSchemaVersion);
        var result = engine.Activate(plan, Approve(plan));
        Assert.Equal(schema, result.Current!.SettingsSchemaVersion);
        Assert.Equal(schema, f.Engine().Inspect().Current!.SettingsSchemaVersion);
        Assert.Equal(result.Revision, f.Engine().Recover().Revision);
        Assert.False(result.IsRunnable);
        Assert.Equal(settings, File.ReadAllBytes(f.Selection.Settings.FilePath));
        foreach (var pair in selectionHistory) Assert.Equal(pair.Value, File.ReadAllBytes(pair.Key));
        if (schema == 4)
            Assert.True((await f.Selection.Settings.LoadAsync()).Settings!.Memory!.Enabled);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("revision")]
    [InlineData("directory")]
    public void CurrentInstallationDriftInvalidatesApprovedPlanBeforeAnyTransaction(string changed)
    {
        using var f = new ActivationFixture(keys);
        f.Selection.Initialize();
        var selected = f.Selection.Select(f.Selection.Stage());
        var engine = f.Engine((_, _) => ActivationProbeOutcome.Ready);
        engine.Initialize(selected.Revision);
        var plan = engine.PrepareActivation(0, selected.Revision);
        var current = f.Selection.Package.Current;
        f.Selection.Package.Current = changed switch
        {
            "version" => current with { Version = "0.1.0.1" },
            "revision" => current with { InstallationRevision = new('a', 64) },
            _ => current with { InstallationDirectory = Path.Combine(f.Selection.Package.Root, "different-install") }
        };
        var pointer = File.ReadAllBytes(f.Control);
        Fails(ActivationFailure.Conflict, () => engine.Activate(plan, Approve(plan)));
        Assert.Equal(pointer, File.ReadAllBytes(f.Control));
        Assert.Empty(Directory.GetDirectories(f.Root, "transaction-*"));
    }

    [Theory]
    [InlineData("payload")]
    [InlineData("archive")]
    [InlineData("history")]
    [InlineData("settings")]
    public void LinkedActivationSourcesAreRejectedWithoutChangingTheirTargets(string source)
    {
        using var f = new ActivationFixture(keys);
        f.Selection.Initialize();
        var selected = f.Selection.Select(f.Selection.Stage());
        var engine = f.Engine((_, _) => ActivationProbeOutcome.Ready);
        engine.Initialize(selected.Revision);
        var plan = engine.PrepareActivation(0, selected.Revision);
        var stage = Path.Combine(f.Selection.Package.StagingRoot, selected.CurrentSelection!.StageName);
        var path = source switch
        {
            "payload" => Path.Combine(stage, "payload", "Desktop", "Martlet.Desktop.exe"),
            "archive" => Path.Combine(stage, "candidate.zip"),
            "history" => Path.Combine(f.Selection.Root, $"transaction-{selected.TransactionId:N}", "journal-v2.json"),
            _ => f.Selection.Settings.FilePath
        };
        var target = Path.Combine(f.Selection.Package.Root, "retained-link-target");
        var bytes = File.ReadAllBytes(path);
        var pointer = File.ReadAllBytes(f.Control);
        File.Move(path, target);
        File.CreateSymbolicLink(path, target);
        Assert.Throws<ActivationException>(() => engine.Activate(plan, Approve(plan)));
        Assert.Equal(pointer, File.ReadAllBytes(f.Control));
        Assert.Equal(bytes, File.ReadAllBytes(target));
        Assert.Empty(Directory.GetDirectories(f.Root, "transaction-*"));
    }

    [Fact]
    public void ApprovalIsExactOneUseAndCannotCrossCoordinatorInstances()
    {
        using var f = new ActivationFixture(keys);
        f.Selection.Initialize();
        var selected = f.Selection.Select(f.Selection.Stage());
        var engine = f.Engine((_, _) => ActivationProbeOutcome.Ready);
        f.Initialize(engine, selected.Revision);
        var plan = f.Prepare(engine, 0, selected.Revision);
        Fails(ActivationFailure.Conflict, () => plan.Approve(Guid.NewGuid(),
            plan.ExpectedActivationRevision, plan.ExpectedSelectionRevision,
            plan.ExpectedSettingsRevision, plan.PlanDigest));
        var firstApproval = Approve(plan);
        Fails(ActivationFailure.Conflict, () => Approve(plan));
        Fails(ActivationFailure.Conflict,
            () => f.Engine((_, _) => ActivationProbeOutcome.Ready).Activate(plan, firstApproval));

        plan = f.Prepare(engine, 0, selected.Revision);
        var approval = Approve(plan);
        Fails(ActivationFailure.Conflict,
            () => f.Engine((_, _) => ActivationProbeOutcome.Ready).Activate(plan, approval));
        Fails(ActivationFailure.Conflict, () => engine.Activate(plan, approval));
    }
}
