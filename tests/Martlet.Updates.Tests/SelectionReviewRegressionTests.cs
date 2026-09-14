using static Martlet.Updates.Tests.SelectionFixture;

namespace Martlet.Updates.Tests;

public sealed class SelectionReviewRegressionTests(SigningKeys keys) : IClassFixture<SigningKeys>
{
    [Theory]
    [InlineData("initial", false, false)]
    [InlineData("initial", true, false)]
    [InlineData("pending", false, false)]
    [InlineData("pending", true, false)]
    [InlineData("selected", false, false)]
    [InlineData("selected", true, false)]
    [InlineData("recovery", false, false)]
    [InlineData("recovery", true, false)]
    [InlineData("initial", false, true)]
    [InlineData("initial", true, true)]
    [InlineData("pending", false, true)]
    [InlineData("pending", true, true)]
    [InlineData("selected", false, true)]
    [InlineData("selected", true, true)]
    [InlineData("recovery", false, true)]
    [InlineData("recovery", true, true)]
    public void AtomicReplacementConsumesOnlyExactApprovedScratch(string phase, bool replaceName, bool afterPublication)
    {
        using var f = new SelectionFixture(keys);
        var originalSettings = File.ReadAllBytes(f.Settings.FilePath);
        if (phase != "initial") f.Initialize();
        var stage = f.Stage();
        if (phase == "recovery")
        {
            var interrupted = f.Engine((point, _, _) =>
            {
                if (point == SelectionIoPoint.AfterPendingReplace) throw new IOException();
            });
            var pendingPlan = f.Prepare(interrupted, stage);
            Assert.Throws<SelectionException>(() => interrupted.CommitSelection(pendingPlan, Approve(pendingPlan)));
        }
        var pointToAttack = phase switch
        {
            "initial" => SelectionIoPoint.BeforeInitializeReplace,
            "pending" => SelectionIoPoint.BeforePendingReplace,
            "selected" => SelectionIoPoint.BeforeSelectionReplace,
            _ => SelectionIoPoint.BeforeRecoveryReplace
        };
        if (afterPublication) pointToAttack = SelectionIoPoint.BeforePublicationRename;
        var attempted = false;
        var changed = false;
        byte[]? authorityBeforeAttack = null;
        var engine = f.Engine((point, path, _) =>
        {
            if (point != pointToAttack || afterPublication && !IsScratch(path, phase)) return;
            attempted = true;
            authorityBeforeAttack = File.Exists(f.Control) ? File.ReadAllBytes(f.Control) : null;
            var scratch = afterPublication ? path : phase switch
            {
                "initial" => path,
                "pending" => Path.Combine(path, "pending.json"),
                "selected" => Path.Combine(path, "selected.json"),
                _ => Path.Combine(path, "recovered-0.json")
            };
            try
            {
                if (replaceName) File.Move(scratch, scratch + ".retained");
                File.WriteAllText(scratch, "{}");
                changed = true;
            }
            catch (IOException) { /* Windows write denial is an acceptable ownership result. */ }
        });
        SelectionReceipt? receipt = null;
        var error = Record.Exception(() =>
        {
            if (phase == "initial") receipt = engine.Initialize(f.Package.Current);
            else if (phase == "recovery") receipt = engine.Recover();
            else
            {
                var plan = f.Prepare(engine, stage);
                receipt = engine.CommitSelection(plan, Approve(plan));
            }
        });
        Assert.True(attempted);
        if (changed)
        {
            Assert.True(error is SelectionException or StagingException,
                "Changed scratch must not produce a successful in-memory receipt.");
            Assert.Null(receipt);
            if (authorityBeforeAttack is null) Assert.False(File.Exists(f.Control));
            else Assert.Equal(authorityBeforeAttack, File.ReadAllBytes(f.Control));
        }
        else
        {
            Assert.Null(error);
            Assert.NotNull(receipt);
            Assert.Equal(receipt.Revision, f.Engine().Inspect().Revision);
            Assert.False(receipt.IsRunnable);
        }
        if (phase != "initial")
        {
            if (changed && afterPublication) AssertHistoryRejectedWithoutMutation(f);
            else
            {
                var recovered = f.Engine().Recover();
                if (changed) Assert.Null(recovered.CurrentSelection);
                Assert.Equal(recovered.Revision, f.Engine().Recover().Revision);
            }
        }
        Assert.Equal(originalSettings, File.ReadAllBytes(f.Settings.FilePath));
        f.Package.AssertPrivateDataUnchanged();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RewindingOnlyControlCannotHideNewerPublicationOrReuseApproval(bool toBootstrap)
    {
        using var f = new SelectionFixture(keys);
        f.Initialize();
        var bootstrap = File.ReadAllBytes(f.Control);
        var first = f.Stage();
        var oldEngine = f.Engine();
        var oldPlan = f.Prepare(oldEngine, first);
        var oldApproval = Approve(oldPlan);
        f.Select(first);
        var firstSelection = File.ReadAllBytes(f.Control);
        var second = f.Stage("0.3.0.0");
        var secondOldEngine = f.Engine();
        var secondOldPlan = f.Prepare(secondOldEngine, second);
        var secondOldApproval = Approve(secondOldPlan);
        f.Select(second);
        var rewound = toBootstrap ? bootstrap : firstSelection;
        File.WriteAllBytes(f.Control, rewound);
        Assert.Equal(SelectionFailure.InvalidControl, Assert.Throws<SelectionException>(() => f.Engine().Inspect()).Failure);
        Assert.Equal(SelectionFailure.InvalidControl, Assert.Throws<SelectionException>(() => f.Engine().Recover()).Failure);
        Assert.Equal(SelectionFailure.InvalidControl, Assert.Throws<SelectionException>(() => toBootstrap
            ? oldEngine.CommitSelection(oldPlan, oldApproval)
            : secondOldEngine.CommitSelection(secondOldPlan, secondOldApproval)).Failure);
        Assert.Equal(rewound, File.ReadAllBytes(f.Control));
        f.Package.AssertPrivateDataUnchanged();
    }

    [Theory]
    [InlineData((int)SelectionIoPoint.BeforePendingReplace, "current-stage")]
    [InlineData((int)SelectionIoPoint.BeforeSelectionReplace, "current-stage")]
    [InlineData((int)SelectionIoPoint.BeforePendingReplace, "current-snapshot")]
    [InlineData((int)SelectionIoPoint.BeforeSelectionReplace, "current-snapshot")]
    [InlineData((int)SelectionIoPoint.BeforePendingReplace, "previous-stage")]
    [InlineData((int)SelectionIoPoint.BeforeSelectionReplace, "previous-stage")]
    [InlineData((int)SelectionIoPoint.BeforePendingReplace, "previous-snapshot")]
    [InlineData((int)SelectionIoPoint.BeforeSelectionReplace, "previous-snapshot")]
    public void AllRetainedEvidenceStaysPinnedUntilSelectionCompletes(int boundary, string target)
    {
        using var f = new SelectionFixture(keys);
        f.Initialize();
        f.Select(f.Stage());
        var original = f.Select(f.Stage("0.3.0.0"));
        var next = f.Stage("0.4.0.0");
        var entry = target.StartsWith("current", StringComparison.Ordinal)
            ? original.CurrentSelection! : original.PreviousSelection!;
        var targetPath = target.EndsWith("stage", StringComparison.Ordinal)
            ? Path.Combine(f.Package.StagingRoot, entry.StageName, "payload", "Doctor", "Martlet.Doctor.exe")
            : Path.Combine(f.Root, entry.SnapshotRelativePath.Replace('/', Path.DirectorySeparatorChar));
        var bytes = File.ReadAllBytes(targetPath);
        var attempted = false;
        var changed = false;
        var engine = f.Engine((point, _, _) =>
        {
            if (point != (SelectionIoPoint)boundary) return;
            attempted = true;
            try { File.WriteAllText(targetPath, "corrupted retained evidence"); changed = true; }
            catch (IOException) { /* Original must stay pinned under the same staging owner. */ }
        });
        var plan = f.Prepare(engine, next);
        SelectionReceipt? receipt = null;
        var error = Record.Exception(() => receipt = engine.CommitSelection(plan, Approve(plan)));
        Assert.True(attempted);
        if (changed)
        {
            Assert.True(error is SelectionException or StagingException,
                "A changed retained version/snapshot must not be advertised as successful rollback evidence.");
            Assert.Null(receipt);
            File.WriteAllBytes(targetPath, bytes);
            Assert.Equal(original.CurrentSelection!.Version, f.Engine().Recover().CurrentSelection!.Version);
        }
        else
        {
            Assert.Null(error);
            Assert.Equal(SelectionOutcome.Committed, receipt!.Outcome);
            Assert.Equal(bytes, File.ReadAllBytes(targetPath));
            Assert.Equal("0.3.0.0", f.Engine().Inspect().PreviousSelection!.Version);
        }
        f.Package.AssertPrivateDataUnchanged();
    }

    public static IEnumerable<object[]> PublicationWrites()
    {
        foreach (var phase in new[] { "pending", "selected", "recovery" })
            foreach (var point in new[] { SelectionIoPoint.BeforeCreate, SelectionIoPoint.AfterCreate,
                SelectionIoPoint.BeforeWrite, SelectionIoPoint.AfterWrite, SelectionIoPoint.BeforeFlush, SelectionIoPoint.AfterFlush })
                yield return [phase, (int)point];
    }

    [Theory]
    [MemberData(nameof(PublicationWrites))]
    public void InterruptedPublicationReceiptNeverGuessesWhetherRenameHappened(string phase, int boundary)
    {
        using var f = new SelectionFixture(keys);
        f.Initialize();
        var stage = f.Stage();
        if (phase == "recovery") LeavePending(f, stage);
        var hit = false;
        byte[]? before = null;
        string? publication = null;
        var engine = f.Engine((point, path, _) =>
        {
            if (point != (SelectionIoPoint)boundary || !IsPublication(path, phase)) return;
            hit = true;
            before = File.ReadAllBytes(f.Control);
            publication = path;
            throw new IOException("Injected publication write interruption");
        });
        Assert.Throws<SelectionException>(() =>
        {
            if (phase == "recovery") engine.Recover();
            else
            {
                var plan = f.Prepare(engine, stage);
                engine.CommitSelection(plan, Approve(plan));
            }
        });
        Assert.True(hit);
        Assert.Equal(before, File.ReadAllBytes(f.Control));
        if ((SelectionIoPoint)boundary == SelectionIoPoint.BeforeCreate)
        {
            Assert.False(File.Exists(publication));
            var recovered = f.Engine().Recover();
            Assert.Null(recovered.CurrentSelection);
            Assert.Equal(recovered.Revision, f.Engine().Recover().Revision);
        }
        else
        {
            Assert.True(File.Exists(publication));
            AssertHistoryRejectedWithoutMutation(f);
        }
        f.Package.AssertPrivateDataUnchanged();
    }

    [Theory]
    [InlineData("initial", false)]
    [InlineData("initial", true)]
    [InlineData("pending", false)]
    [InlineData("pending", true)]
    [InlineData("selected", false)]
    [InlineData("selected", true)]
    [InlineData("recovery", false)]
    [InlineData("recovery", true)]
    public async Task OriginalCancellationAtNewRenameBoundaryNeverHidesFinalization(string phase, bool afterRename)
    {
        using var f = new SelectionFixture(keys);
        if (phase != "initial") f.Initialize();
        var stage = f.Stage();
        if (phase == "recovery") LeavePending(f, stage);
        using var cancellation = new CancellationTokenSource();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var registration = cancellation.Token.Register(() => { entered.Set(); release.Wait(); });
        Task? delivery = null;
        var engine = f.Engine((point, path, _) =>
        {
            if (point != (afterRename ? SelectionIoPoint.AfterPublicationRename : SelectionIoPoint.BeforePublicationRename) ||
                !IsScratch(path, phase)) return;
            delivery = cancellation.CancelAsync();
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        });
        SelectionReceipt? result = null;
        try
        {
            var error = Record.Exception(() =>
            {
                if (phase == "initial") result = engine.Initialize(f.Package.Current, cancellation.Token);
                else if (phase == "recovery") result = engine.Recover(token: cancellation.Token);
                else
                {
                    var plan = f.Prepare(engine, stage);
                    result = engine.CommitSelection(plan, Approve(plan), cancellation.Token);
                }
            });
            Assert.NotNull(delivery);
            Assert.False(delivery.IsCompleted);
            if (afterRename && phase != "pending")
            {
                Assert.Null(error);
                Assert.NotNull(result);
                Assert.Equal(phase == "recovery" ? SelectionOutcome.RecoveredOriginal : SelectionOutcome.Committed, result.Outcome);
                Assert.False(result.IsRunnable);
                Assert.Equal(result.Revision, f.Engine().Inspect().Revision);
                Assert.Equal(result.Revision, f.Engine().Recover().Revision);
            }
            else
            {
                Assert.Equal(SelectionFailure.Cancelled, Assert.IsType<SelectionException>(error).Failure);
                Assert.Null(result);
                if (!afterRename && phase != "initial") AssertHistoryRejectedWithoutMutation(f);
                else if (phase == "initial")
                {
                    Assert.False(File.Exists(f.Control));
                    Assert.True(File.Exists(Path.Combine(f.Root, "initial.json")));
                }
                else Assert.Equal(SelectionOutcome.RecoveredOriginal, f.Engine().Recover().Outcome);
            }
        }
        finally { release.Set(); if (delivery is not null) await delivery; }
        f.Package.AssertPrivateDataUnchanged();
    }

    [Theory]
    [InlineData("current-stage")]
    [InlineData("current-snapshot")]
    [InlineData("previous-stage")]
    [InlineData("previous-snapshot")]
    public void RecoveryRetainsAllOriginalVersionAndSnapshotPins(string target)
    {
        using var f = new SelectionFixture(keys);
        f.Initialize();
        f.Select(f.Stage());
        var original = f.Select(f.Stage("0.3.0.0"));
        LeavePending(f, f.Stage("0.4.0.0"));
        var entry = target.StartsWith("current", StringComparison.Ordinal)
            ? original.CurrentSelection! : original.PreviousSelection!;
        var path = target.EndsWith("stage", StringComparison.Ordinal)
            ? Path.Combine(f.Package.StagingRoot, entry.StageName, "candidate.zip")
            : Path.Combine(f.Root, entry.SnapshotRelativePath.Replace('/', Path.DirectorySeparatorChar));
        var bytes = File.ReadAllBytes(path);
        var called = false;
        var engine = f.Engine((point, _, _) =>
        {
            if (point != SelectionIoPoint.BeforePublicationRename) return;
            called = true;
            Assert.Throws<IOException>(() => File.WriteAllText(path, "changed during recovery"));
            Assert.Equal(StagingFailure.Busy, Assert.Throws<StagingException>(() =>
                f.Staging.InspectStaged(Path.Combine(f.Package.StagingRoot, entry.StageName))).Failure);
        });
        var recovered = engine.Recover();
        Assert.True(called);
        Assert.Equal(SelectionOutcome.RecoveredOriginal, recovered.Outcome);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(original.CurrentSelection!.Version, f.Engine().Inspect().CurrentSelection!.Version);
    }

    [Theory]
    [InlineData("missing-pending")]
    [InlineData("missing-selected")]
    [InlineData("legacy-control")]
    [InlineData("legacy-journal")]
    [InlineData("mismatched-publication")]
    [InlineData("rewound-pending")]
    [InlineData("recovered-instead-of-selected")]
    public void PublicationRequirementsCannotBeRemovedOrSubstituted(string scenario)
    {
        using var f = new SelectionFixture(keys);
        f.Initialize();
        var result = f.Select(f.Stage());
        var directory = Path.Combine(f.Root, "transaction-" + result.TransactionId!.Value.ToString("N"));
        if (scenario == "missing-pending") File.Delete(Path.Combine(directory, "pending-publication.json"));
        if (scenario == "missing-selected") File.Delete(Path.Combine(directory, "selected-publication.json"));
        if (scenario == "legacy-control")
        {
            var current = Wire.Read<ControlDocument>(File.ReadAllBytes(f.Control), 32768);
            File.WriteAllBytes(f.Control, Wire.Write(current with { FormatVersion = 1 }));
        }
        if (scenario == "legacy-journal")
            File.Move(Path.Combine(directory, "journal-v2.json"), Path.Combine(directory, "journal.json"));
        if (scenario == "mismatched-publication")
            File.Copy(Path.Combine(directory, "before.json"), Path.Combine(directory, "selected-publication.json"), true);
        if (scenario == "rewound-pending")
            File.Copy(Path.Combine(directory, "pending-publication.json"), f.Control, true);
        if (scenario == "recovered-instead-of-selected")
        {
            var journal = Wire.Read<SelectionJournal>(File.ReadAllBytes(Path.Combine(directory, "journal-v2.json")), 131072);
            File.WriteAllBytes(f.Control, Wire.Write(journal.Before with { Revision = journal.After.Revision, LastTransaction = journal.TransactionId }));
        }
        AssertHistoryRejectedWithoutMutation(f);
    }

    [Fact]
    public void UnpublishedV2OrphanIsNotHistoryButLegacyOrphanCannotBeAssumedUnpublished()
    {
        using var f = new SelectionFixture(keys);
        f.Initialize();
        var stage = f.Stage();
        var engine = f.Engine((point, _, _) =>
        {
            if (point == SelectionIoPoint.BeforePendingReplace) throw new IOException();
        });
        var plan = f.Prepare(engine, stage);
        Assert.Throws<SelectionException>(() => engine.CommitSelection(plan, Approve(plan)));
        var orphan = Path.Combine(f.Root, "transaction-" + plan.TransactionId.ToString("N"));
        Assert.Equal(0, f.Engine().Recover(plan.TransactionId).Revision);
        f.Select(stage);
        Assert.Equal(2, f.Engine().Inspect().Revision);
        Assert.True(File.Exists(Path.Combine(orphan, "journal-v2.json")));
        File.Move(Path.Combine(orphan, "journal-v2.json"), Path.Combine(orphan, "journal.json"));
        AssertHistoryRejectedWithoutMutation(f);
    }

    private static bool IsScratch(string path, string phase) => Path.GetFileName(path) == phase switch
    {
        "initial" => "initial.json", "pending" => "pending.json", "selected" => "selected.json", _ => "recovered-0.json"
    };
    private static bool IsPublication(string path, string phase) => Path.GetFileName(path) == phase switch
    {
        "pending" => "pending-publication.json", "selected" => "selected-publication.json", _ => "recovered-0.json.publication"
    };

    private static void LeavePending(SelectionFixture f, string stage)
    {
        var engine = f.Engine((point, _, _) =>
        {
            if (point == SelectionIoPoint.AfterPendingReplace) throw new IOException();
        });
        var plan = f.Prepare(engine, stage);
        Assert.Throws<SelectionException>(() => engine.CommitSelection(plan, Approve(plan)));
        Assert.Equal(SelectionStatus.RecoveryRequired, f.Engine().Inspect().Status);
    }

    private static void AssertHistoryRejectedWithoutMutation(SelectionFixture f)
    {
        var files = Directory.GetFiles(f.Root, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.ReadAllBytes, StringComparer.Ordinal);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            Assert.Equal(SelectionFailure.InvalidControl, Assert.Throws<SelectionException>(() => f.Engine().Inspect()).Failure);
            Assert.Equal(SelectionFailure.InvalidControl, Assert.Throws<SelectionException>(() => f.Engine().Recover()).Failure);
        }
        Assert.Equal(files.Count, Directory.GetFiles(f.Root, "*", SearchOption.AllDirectories).Length);
        foreach (var (path, bytes) in files) Assert.Equal(bytes, File.ReadAllBytes(path));
    }
}
