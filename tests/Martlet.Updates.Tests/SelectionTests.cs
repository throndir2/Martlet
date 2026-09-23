using System.Text;
using System.Text.Json.Nodes;
using Martlet.Core.Settings;
using static Martlet.Updates.Tests.SelectionFixture;

namespace Martlet.Updates.Tests;

public sealed class SelectionTests(SigningKeys keys) : IClassFixture<SigningKeys>
{
    private static SelectionException Fails(SelectionFailure failure, Action action)
    {
        var error = Assert.Throws<SelectionException>(action);
        Assert.Equal(failure, error.Failure);
        return error;
    }

    [Fact]
    public void ExplicitBootstrapAndSignedSelectionNeverBecomeRunnable()
    {
        using var f = new SelectionFixture(keys);
        var before = Directory.GetFileSystemEntries(f.Root);
        var engine = f.Engine();
        Assert.Equal(before, Directory.GetFileSystemEntries(f.Root));
        Fails(SelectionFailure.Uninitialized, () => engine.Inspect());
        var initialized = engine.Initialize(f.Package.Current);
        Assert.Equal(SelectionStatus.BootstrapUnqualified, initialized.Status);
        Assert.Null(initialized.CurrentSelection);
        Assert.Equal(0, initialized.Revision);
        var original = File.ReadAllBytes(f.Settings.FilePath);
        var stage = f.Stage();
        var plan = f.Prepare(engine, stage);
        Assert.Equal(0, engine.Inspect().Revision);
        Assert.Equal(f.Package.Current.SettingsRevision, plan.Snapshot.SourceRevision);
        Assert.Equal(f.Package.Current.SettingsRevision.ToUpperInvariant(), plan.Snapshot.SourceRevision);
        Assert.Equal(stage, plan.StageDirectory);
        Assert.Equal(f.Settings.FilePath, plan.SettingsPath);
        Assert.Equal(plan.Snapshot.FileDigest, ConfigurationSnapshot.Inspect(File.ReadAllBytes(plan.ConfigurationSnapshotPath)).FileDigest);
        Assert.Equal(1, plan.Candidate.SettingsMinimumReader);
        Assert.Equal(AppSettings.CurrentSchemaVersion, plan.Candidate.SettingsMaximumReader);
        Assert.Equal("win-x64", plan.Candidate.Rid);
        Assert.Contains("No installation or settings mutation", plan.PlannedEffects);
        var result = engine.CommitSelection(plan, Approve(plan));
        Assert.Equal(SelectionOutcome.Committed, result.Outcome);
        Assert.Equal(SelectionStatus.AwaitingReadiness, result.Status);
        Assert.False(result.IsRunnable);
        Assert.Equal(2, result.Revision);
        Assert.Equal("0.2.0.0", result.CurrentSelection!.Version);
        Assert.Null(result.PreviousSelection);
        Assert.Equal(result.CurrentSelection.ReceiptSha256, f.Staging.InspectStaged(stage).ReceiptSha256);
        var reopened = f.Engine().Inspect();
        Assert.Equal(result.Revision, reopened.Revision);
        Assert.Equal(result.CurrentSelection.ArchiveSha256, reopened.CurrentSelection!.ArchiveSha256);
        Assert.Equal(original, File.ReadAllBytes(f.Settings.FilePath));
        Assert.Empty(typeof(SelectionReceipt).GetConstructors());
        Assert.Empty(typeof(SelectionApproval).GetConstructors());
        Assert.DoesNotContain(f.Root, plan.ToString());
        Assert.DoesNotContain("PendingRemovals", plan.ToString());
        Assert.False(File.Exists(Path.Combine(f.Root, "active.json")));
        f.Package.AssertPrivateDataUnchanged();
    }

    [Fact]
    public async Task RollbackOnlySelectsRecordedPreviousAndKeepsPendingV07aRestore()
    {
        using var f = new SelectionFixture(keys);
        f.Initialize();
        var firstStage = f.Stage();
        f.Select(firstStage);
        f.ChangeSettings();
        var secondStage = f.Stage("0.3.0.0");
        var beforeBytes = File.ReadAllBytes(f.Settings.FilePath);
        var second = f.Select(secondStage);
        Assert.Equal("0.2.0.0", second.PreviousSelection!.Version);
        var engine = f.Engine();
        var plan = engine.PrepareRollback(second.Revision, f.Snapshot());
        Assert.Equal(SelectionKind.Rollback, plan.Kind);
        Assert.True(plan.Candidate.ConfigurationRestoreRequired);
        var result = engine.CommitSelection(plan, Approve(plan));
        Assert.Equal(SelectionStatus.AwaitingConfigurationRestore, result.Status);
        Assert.False(result.IsRunnable);
        Assert.Equal("0.2.0.0", result.CurrentSelection!.Version);
        Assert.Equal("0.3.0.0", result.PreviousSelection!.Version);
        Assert.Equal(beforeBytes, File.ReadAllBytes(f.Settings.FilePath));
        Assert.Equal(result.CurrentSelection.SnapshotFileDigest,
            ConfigurationSnapshot.Inspect(File.ReadAllBytes(Path.Combine(f.Root,
                result.CurrentSelection.SnapshotRelativePath.Replace('/', Path.DirectorySeparatorChar)))).FileDigest);
        var restore = await f.Settings.PreviewConfigurationRestoreAsync(Path.Combine(f.Root,
            result.CurrentSelection.SnapshotRelativePath.Replace('/', Path.DirectorySeparatorChar)));
        Assert.Equal(f.Package.Current.SettingsRevision, restore.ExpectedRevision);
        Assert.Equal(beforeBytes, File.ReadAllBytes(f.Settings.FilePath));
        Assert.Equal(SelectionStatus.AwaitingConfigurationRestore, f.Engine().Recover().Status);
        f.Package.AssertPrivateDataUnchanged();
    }

    [Fact]
    public void UnqualifiedBootstrapCannotBeRollbackTarget()
    {
        using var f = new SelectionFixture(keys);
        f.Initialize();
        var current = f.Select(f.Stage());
        Fails(SelectionFailure.NoVerifiedPrevious, () => f.Engine().PrepareRollback(current.Revision, f.Snapshot()));
    }

    [Theory]
    [InlineData("settings")]
    [InlineData("case")]
    [InlineData("snapshot")]
    [InlineData("stage-file")]
    [InlineData("stage-envelope")]
    [InlineData("stage-receipt")]
    [InlineData("revision")]
    public void ChangedApprovalSourcesNeverSelect(string source)
    {
        using var f = new SelectionFixture(keys);
        f.Initialize();
        var stage = f.Stage();
        var engine = f.Engine();
        var plan = f.Prepare(engine, stage);
        var approval = Approve(plan);
        var control = File.ReadAllBytes(f.Control);
        switch (source)
        {
            case "settings": f.ChangeSettings(); break;
            case "case":
                f.Package.Current = f.Package.Current with { SettingsRevision = f.Package.Current.SettingsRevision.ToLowerInvariant() };
                break;
            case "snapshot": File.AppendAllText(plan.SourcePath, " "); break;
            case "stage-file": File.AppendAllText(Path.Combine(stage, "payload", "Doctor", "Martlet.Doctor.exe"), "changed"); break;
            case "stage-envelope": File.AppendAllText(Path.Combine(stage, "candidate.json"), " "); break;
            case "stage-receipt": File.AppendAllText(Path.Combine(stage, "staged.json"), " "); break;
            case "revision":
                var state = Wire.Read<ControlDocument>(control, 32768);
                File.WriteAllBytes(f.Control, Wire.Write(state with { Revision = state.Revision + 1 }));
                break;
        }
        var expectedControl = File.ReadAllBytes(f.Control);
        var error = Record.Exception(() => engine.CommitSelection(plan, approval));
        Assert.True(error is SelectionException or StagingException);
        Assert.Equal(expectedControl, File.ReadAllBytes(f.Control));
        Fails(SelectionFailure.Conflict, () => engine.CommitSelection(plan, approval));
        f.Package.AssertPrivateDataUnchanged();
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("stale")]
    [InlineData("tamper")]
    [InlineData("future")]
    [InlineData("origin")]
    [InlineData("store")]
    public async Task WrongSnapshotOrInstallationOriginRefusesPreview(string kind)
    {
        using var f = new SelectionFixture(keys);
        f.Initialize();
        var snapshot = f.Snapshot();
        var engine = f.Engine();
        if (kind == "stale") f.ChangeSettings();
        if (kind == "origin") f.Package.Current = f.Package.Current with { InstallationRevision = new string('a', 64) };
        var stage = f.Stage();
        if (kind == "profile")
        {
            var foreign = new SettingsStore(Path.Combine(f.Package.Root, "foreign-data"));
            Assert.True((await foreign.SaveAsync(SetupSettings.Begin(null), null)).Saved);
            snapshot = Path.Combine(f.Root, "foreign.martlet-config");
            await foreign.CreateConfigurationSnapshotAsync(snapshot);
        }
        if (kind is "tamper" or "future")
        {
            var json = JsonNode.Parse(File.ReadAllBytes(snapshot))!;
            if (kind == "tamper") json["sha256"] = new string('0', 64);
            if (kind == "future") json["manifest"]!["format_version"] = 9;
            File.WriteAllText(snapshot, json.ToJsonString());
        }
        if (kind == "store")
            engine = new LocalSelectionEngine(f.Root, f.Staging, new SettingsStore(Path.Combine(f.Package.Root, "foreign")));
        var original = File.ReadAllBytes(f.Control);
        var error = Record.Exception(() => engine.PrepareActivation(stage, 0, snapshot));
        Assert.IsType<SelectionException>(error);
        Assert.Equal(original, File.ReadAllBytes(f.Control));
    }

    [Fact]
    public void ApprovalBindsExactPlanAndIsOneUseAcrossInstancesAndFailedPreviews()
    {
        using var f = new SelectionFixture(keys);
        f.Initialize();
        var stage = f.Stage();
        var engine = f.Engine();
        var plan = f.Prepare(engine, stage);
        Fails(SelectionFailure.Conflict, () => plan.Approve(Guid.NewGuid(), plan.ExpectedRevision, plan.PlanDigest));
        var approval = Approve(plan);
        Fails(SelectionFailure.Conflict, () => f.Engine().CommitSelection(plan, approval));
        Fails(SelectionFailure.Conflict, () => engine.CommitSelection(plan, approval));
        var next = f.Prepare(engine, stage);
        var nextApproval = Approve(next);
        Fails(SelectionFailure.Conflict, () => engine.PrepareActivation(stage, 1, f.Snapshot()));
        Fails(SelectionFailure.Conflict, () => engine.CommitSelection(next, nextApproval));
        var first = f.Prepare(engine, stage);
        var second = f.Prepare(engine, stage);
        var firstApproval = Approve(first);
        Fails(SelectionFailure.Conflict, () => engine.CommitSelection(second, firstApproval));
    }

    [Theory]
    [InlineData("signature")]
    [InlineData("trust")]
    [InlineData("snapshot")]
    [InlineData("entry")]
    public void PreviouslySelectedJsonCannotReplacePackageOrSnapshotProof(string tamper)
    {
        using var f = new SelectionFixture(keys);
        f.Initialize();
        var older = f.Stage();
        f.Select(older);
        var latest = f.Select(f.Stage("0.3.0.0"));
        var engine = f.Engine();
        if (tamper == "signature") File.WriteAllText(Path.Combine(older, "candidate.json"), "{}");
        if (tamper == "snapshot")
            File.AppendAllText(Path.Combine(f.Root, latest.PreviousSelection!.SnapshotRelativePath.Replace('/', Path.DirectorySeparatorChar)), "X");
        if (tamper == "entry")
        {
            var state = Wire.Read<ControlDocument>(File.ReadAllBytes(f.Control), 32768);
            File.WriteAllBytes(f.Control, Wire.Write(state with { Previous = state.Previous! with { StageName = "not-recorded" } }));
        }
        if (tamper == "trust")
            engine = f.Engine(staging: new(f.Package.StagingRoot, new([keys.Stranger.ExportSubjectPublicKeyInfo()]), () => f.Package.Current));
        var control = File.ReadAllBytes(f.Control);
        Assert.NotNull(Record.Exception(() => engine.PrepareRollback(latest.Revision, f.Snapshot())));
        Assert.Equal(control, File.ReadAllBytes(f.Control));
    }

    [Fact]
    public void ForwardStagerRemainsStrictlyNewerEvenForRetainedSignedVersion()
    {
        using var f = new SelectionFixture(keys);
        f.Initialize();
        var stage = f.Stage();
        f.Select(stage);
        f.Package.Current = f.Package.Current with { Version = "0.2.0.0" };
        Assert.Equal(StagingFailure.InvalidVersion, Assert.Throws<StagingException>(() => f.Staging.InspectStaged(stage)).Failure);
        Assert.Equal(StagingFailure.InvalidVersion, Assert.Throws<StagingException>(() =>
            f.Staging.Preview(f.Package.Archive, f.Package.Envelope, Path.Combine(f.Package.StagingRoot, "equal"))).Failure);
        Assert.Equal("0.2.0.0", f.Engine().Inspect().CurrentSelection!.Version);
    }

    [Fact]
    public void CompetingPreparedPlansLoseOnAuthoritativeRevision()
    {
        using var f = new SelectionFixture(keys);
        f.Initialize();
        var stage = f.Stage();
        var a = f.Engine(); var b = f.Engine();
        var pa = f.Prepare(a, stage); var pb = f.Prepare(b, stage);
        a.CommitSelection(pa, Approve(pa));
        Fails(SelectionFailure.Conflict, () => b.CommitSelection(pb, Approve(pb)));
        Assert.Equal(2, b.Inspect().Revision);
    }

    [Fact]
    public void PrivateControlAndAllOriginalSourceHandlesRemainOwnedAtFinalBoundary()
    {
        using var f = new SelectionFixture(keys);
        f.Initialize();
        var stage = f.Stage();
        var called = false;
        SelectionPlan? plan = null;
        var engine = f.Engine((point, _, _) =>
        {
            if (point != SelectionIoPoint.BeforeSelectionReplace) return;
            called = true;
            Fails(SelectionFailure.Busy, () => f.Engine().Inspect());
            Fails(SelectionFailure.Busy, () => f.Engine().Recover());
            Assert.Equal(StagingFailure.Busy, Assert.Throws<StagingException>(() => f.Staging.InspectStaged(stage)).Failure);
            foreach (var path in new[] { f.Settings.FilePath, plan!.SourcePath, Path.Combine(stage, "candidate.zip"),
                Path.Combine(stage, "candidate.json"), Path.Combine(stage, "payload", "Doctor", "Martlet.Doctor.exe") })
                Assert.Throws<IOException>(() => File.WriteAllText(path, "noncooperative"));
            var loaded = f.Settings.LoadAsync().GetAwaiter().GetResult();
            Assert.False(f.Settings.SaveAsync(loaded.Settings!, loaded.Revision).GetAwaiter().GetResult().Saved);
        });
        plan = f.Prepare(engine, stage);
        Assert.Equal(SelectionOutcome.Committed, engine.CommitSelection(plan, Approve(plan)).Outcome);
        Assert.True(called);
        f.Package.AssertPrivateDataUnchanged();
    }

    [Theory]
    [InlineData("corrupt")]
    [InlineData("future")]
    [InlineData("missing")]
    [InlineData("escape")]
    public void InvalidControlNeverSilentlyReinitializes(string kind)
    {
        using var f = new SelectionFixture(keys);
        f.Initialize();
        f.Select(f.Stage());
        if (kind == "missing") File.Delete(f.Control);
        else if (kind == "corrupt") File.WriteAllText(f.Control, "{");
        else
        {
            var state = Wire.Read<ControlDocument>(File.ReadAllBytes(f.Control), 32768);
            state = kind == "future" ? state with { FormatVersion = 99 } :
                state with { Current = state.Current! with { StageName = "../external" } };
            File.WriteAllBytes(f.Control, Wire.Write(state));
        }
        Assert.NotNull(Record.Exception(() => f.Engine().Inspect()));
        Assert.NotNull(Record.Exception(() => f.Engine().Recover()));
        Fails(SelectionFailure.RecoveryRequired, () => f.Initialize());
        f.Package.AssertPrivateDataUnchanged();
    }

    [Theory]
    [InlineData("external")]
    [InlineData("unc")]
    [InlineData("overflow")]
    public void SnapshotPathsAndStoreHistoryAreBounded(string kind)
    {
        using var f = new SelectionFixture(keys);
        f.Initialize();
        var stage = f.Stage();
        var snapshot = f.Snapshot();
        if (kind == "external")
        {
            var target = Path.Combine(f.Package.Root, "external.martlet-config");
            File.Copy(snapshot, target);
            snapshot = target;
        }
        if (kind == "unc") snapshot = @"\\server\share\snapshot.martlet-config";
        if (kind == "overflow")
            for (var i = 0; i < 32; i++) Directory.CreateDirectory(Path.Combine(f.Root, "transaction-" + Guid.NewGuid().ToString("N")));
        Assert.NotNull(Record.Exception(() => f.Engine().PrepareActivation(stage, 0, snapshot)));
        Assert.Equal(0, f.Engine().Inspect().Revision);
    }

    [Fact]
    public async Task NewerSchemaRetainsLastCompatiblePreviousSnapshotRatherThanCorruptingRecord()
    {
        using var f = new SelectionFixture(keys, legacy: true);
        f.Initialize();
        var first = f.Select(f.Stage(maximumReader: 1));
        var loaded = await f.Settings.LoadAsync();
        Assert.True((await f.Settings.SaveAsync(SetupSettings.Begin(loaded.Settings), loaded.Revision)).Saved);
        f.RefreshFacts();
        var second = f.Select(f.Stage("0.3.0.0"));
        Assert.Equal(first.CurrentSelection!.SnapshotFileDigest, second.PreviousSelection!.SnapshotFileDigest);
        Assert.Equal(SelectionStatus.AwaitingReadiness, f.Engine().Inspect().Status);
        Fails(SelectionFailure.IncompatibleSettings, () => f.Engine().PrepareRollback(second.Revision, f.Snapshot()));
        f.Package.AssertPrivateDataUnchanged();
    }

    [Fact]
    public void ForgedControlSelectionCannotBypassItsRecordedJournalLineage()
    {
        using var f = new SelectionFixture(keys);
        f.Initialize();
        f.Select(f.Stage());
        var state = Wire.Read<ControlDocument>(File.ReadAllBytes(f.Control), 32768);
        File.WriteAllBytes(f.Control, Wire.Write(state with { Current = state.Current! with { RestoreRequired = true } }));
        Fails(SelectionFailure.InvalidControl, () => f.Engine().Inspect());
        Fails(SelectionFailure.InvalidControl, () => f.Engine().Recover());
    }

    [Theory]
    [InlineData("snapshot")]
    [InlineData("control")]
    [InlineData("stage")]
    [InlineData("payload")]
    public void ActualFilesystemLinksAreRefusedWithoutFollowingTheirTargets(string target)
    {
        using var f = new SelectionFixture(keys);
        f.Initialize();
        var stage = f.Stage();
        var snapshot = f.Snapshot();
        if (target == "control")
        {
            var alias = Path.Combine(f.Package.Root, "control-alias");
            Directory.CreateSymbolicLink(alias, f.Root);
            Assert.NotNull(Record.Exception(() => new LocalSelectionEngine(alias, f.Staging, f.Settings)));
            return;
        }
        if (target == "snapshot")
        {
            var alias = Path.Combine(f.Root, "linked.martlet-config");
            File.CreateSymbolicLink(alias, snapshot);
            snapshot = alias;
        }
        if (target == "stage")
        {
            var moved = Path.Combine(f.Package.StagingRoot, "retained");
            Directory.Move(stage, moved);
            Directory.CreateSymbolicLink(stage, moved);
        }
        if (target == "payload")
        {
            var payload = Path.Combine(stage, "payload", "Doctor", "Martlet.Doctor.exe");
            var moved = Path.Combine(f.Package.Root, "inert-retained-file");
            File.Move(payload, moved);
            File.CreateSymbolicLink(payload, moved);
        }
        Assert.NotNull(Record.Exception(() => f.Engine().PrepareActivation(stage, 0, snapshot)));
        Assert.Equal(0, f.Engine().Inspect().Revision);
        f.Package.AssertPrivateDataUnchanged();
    }
}
