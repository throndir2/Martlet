using static Martlet.Updates.Tests.ActivationFixture;

namespace Martlet.Updates.Tests;

public sealed class ActivationInterruptionTests(SigningKeys keys) : IClassFixture<SigningKeys>
{
    public static IEnumerable<object[]> DurableWrites()
    {
        foreach (var file in new[]
        {
            "before.json", "journal-v1.json", "pending.json", "pending-publication.json",
            "readiness-intent.json", "readiness-result.json", "active.json", "active-publication.json"
        })
        {
            foreach (var point in new[]
            {
                ActivationIoPoint.BeforeCreate, ActivationIoPoint.AfterCreate,
                ActivationIoPoint.BeforeWrite, ActivationIoPoint.AfterWrite,
                ActivationIoPoint.BeforeFlush, ActivationIoPoint.AfterFlush
            })
                yield return [file, (int)point];
        }
    }

    [Theory]
    [MemberData(nameof(DurableWrites))]
    public void EveryWriteInterruptionIsRecoverableOrExplicitlyAmbiguous(string file, int boundary)
    {
        using var f = ReadyWithNextSelection(keys);
        var before = f.Engine().Inspect();
        var beforeBytes = File.ReadAllBytes(f.Control);
        var hit = false;
        var engine = f.Engine((_, _) => ActivationProbeOutcome.Ready,
            (point, path, _) =>
            {
                if (!hit && point == (ActivationIoPoint)boundary &&
                    Path.GetFileName(path) == file)
                {
                    hit = true;
                    throw new IOException("PRIVATE ACTIVATION INTERRUPTION");
                }
            });
        var selected = f.Selection.Engine().Inspect();
        var plan = f.Prepare(engine, before.Revision, selected.Revision);
        var error = Assert.Throws<ActivationException>(() =>
            engine.Activate(plan, Approve(plan)));
        Assert.Equal(ActivationFailure.Unavailable, error.Failure);
        Assert.True(hit);
        Assert.DoesNotContain("PRIVATE ACTIVATION INTERRUPTION", error.ToString());

        var directory = Path.Combine(f.Root, "transaction-" + plan.TransactionId.ToString("N"));
        Assert.True(Directory.Exists(directory));
        var foreign = Path.Combine(directory, "unknown-owner");
        File.WriteAllText(foreign, "PRESERVE");
        var hasPendingIntent = File.Exists(Path.Combine(directory, "pending-publication.json"));
        var hasActiveIntent = File.Exists(Path.Combine(directory, "active-publication.json"));
        var pendingInstalled = !File.ReadAllBytes(f.Control).AsSpan().SequenceEqual(beforeBytes);
        var ambiguous = hasActiveIntent || hasPendingIntent && !pendingInstalled;

        if (ambiguous)
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                Assert.Equal(ActivationFailure.PublicationAmbiguous,
                    Assert.Throws<ActivationException>(() => f.Engine().Inspect()).Failure);
                Assert.Equal(ActivationFailure.PublicationAmbiguous,
                    Assert.Throws<ActivationException>(() => f.Engine().Recover()).Failure);
            }
        }
        else if (pendingInstalled)
        {
            var pending = engine.Inspect();
            Assert.Equal(ActivationStatus.ReadinessPending, pending.Status);
            Assert.False(pending.IsRunnable);
            var recovered = engine.Recover(plan.TransactionId);
            Assert.Equal(ActivationOutcome.RecoveredPrevious, recovered.Outcome);
            Assert.False(recovered.IsRunnable);
            Assert.Equal(ActivationStatus.PointerPublished, recovered.Status);
            Assert.Equal(before.Current!.Version, recovered.Current!.Version);
            Assert.Equal(recovered.Revision, engine.Recover().Revision);
        }
        else
        {
            Assert.Equal(beforeBytes, File.ReadAllBytes(f.Control));
            var unchanged = engine.Recover(plan.TransactionId);
            Assert.Equal(ActivationOutcome.Unchanged, unchanged.Outcome);
            Assert.Equal(before.Revision, unchanged.Revision);
            Assert.False(unchanged.IsRunnable);
            Assert.Equal(ActivationStatus.PointerPublished, unchanged.Status);
        }
        Assert.Equal("PRESERVE", File.ReadAllText(foreign));
        f.Selection.Package.AssertPrivateDataUnchanged();
    }

    [Theory]
    [InlineData((int)ActivationIoPoint.BeforePendingReplace)]
    [InlineData((int)ActivationIoPoint.AfterPendingReplace)]
    [InlineData((int)ActivationIoPoint.BeforeReadiness)]
    [InlineData((int)ActivationIoPoint.AfterReadiness)]
    [InlineData((int)ActivationIoPoint.BeforeActiveReplace)]
    [InlineData((int)ActivationIoPoint.BeforePublicationRename)]
    [InlineData((int)ActivationIoPoint.AfterActiveReplace)]
    public void OriginalCancellationNeverHidesTheFinalPointer(int boundary)
    {
        using var f = ReadyWithNextSelection(keys);
        using var cancellation = new CancellationTokenSource();
        var before = f.Engine().Inspect();
        var engine = f.Engine((_, _) => ActivationProbeOutcome.Ready,
            (point, path, _) =>
            {
                if (point != (ActivationIoPoint)boundary) return;
                if (point == ActivationIoPoint.BeforePublicationRename &&
                    Path.GetFileName(path) != "active.json") return;
                cancellation.Cancel();
            });
        var selected = f.Selection.Engine().Inspect();
        var plan = f.Prepare(engine, before.Revision, selected.Revision);
        var result = Record.Exception(() =>
            engine.Activate(plan, Approve(plan), cancellation.Token));
        if ((ActivationIoPoint)boundary == ActivationIoPoint.AfterActiveReplace)
        {
            Assert.Null(result);
            Assert.Equal("0.3.0.0", engine.Inspect().Current!.Version);
            return;
        }
        Assert.Equal(ActivationFailure.Cancelled,
            Assert.IsType<ActivationException>(result).Failure);
        var activeIntent = File.Exists(Path.Combine(f.Root,
            "transaction-" + plan.TransactionId.ToString("N"), "active-publication.json"));
        if (activeIntent)
        {
            Assert.Equal(ActivationFailure.PublicationAmbiguous,
                Assert.Throws<ActivationException>(() => f.Engine().Inspect()).Failure);
        }
        else
        {
            var observed = engine.Inspect();
            if (observed.Status == ActivationStatus.ReadinessPending)
                Assert.Equal("0.2.0.0", engine.Recover(plan.TransactionId).Current!.Version);
            else
                Assert.Equal(before.Revision, engine.Recover(plan.TransactionId).Revision);
        }
        f.Selection.Package.AssertPrivateDataUnchanged();
    }

    [Fact]
    public void TimedOutReadinessIsRecordedAndCannotPublish()
    {
        using var f = ReadyWithNextSelection(keys);
        var clock = new RestoreTestClock();
        var engine = f.Engine((_, _) =>
        {
            clock.Advance(TimeSpan.FromSeconds(5), TimeSpan.Zero);
            return ActivationProbeOutcome.Ready;
        }, clock: clock, readinessLimit: TimeSpan.FromSeconds(5));
        var before = f.Engine().Inspect();
        var selected = f.Selection.Engine().Inspect();
        var plan = f.Prepare(engine, before.Revision, selected.Revision);
        var error = Assert.Throws<ActivationException>(() =>
            engine.Activate(plan, Approve(plan)));
        Assert.Equal(ActivationFailure.ReadinessTimedOut, error.Failure);
        var directory = Path.Combine(f.Root, "transaction-" + plan.TransactionId.ToString("N"));
        var result = Wire.Read<ActivationReadinessDocument>(
            File.ReadAllBytes(Path.Combine(directory, "readiness-result.json")), 32768, canonical: true);
        Assert.Equal(ActivationReadinessOutcome.TimedOut, result.Outcome);
        Assert.False(engine.Inspect().IsRunnable);
        Assert.Equal("0.2.0.0", engine.Recover(plan.TransactionId).Current!.Version);
    }

    [Theory]
    [InlineData((int)ActivationIoPoint.BeforeReadiness, false)]
    [InlineData((int)ActivationIoPoint.BeforeReadiness, true)]
    [InlineData((int)ActivationIoPoint.BeforeActiveReplace, false)]
    [InlineData((int)ActivationIoPoint.BeforeActiveReplace, true)]
    [InlineData((int)ActivationIoPoint.BeforePublicationRename, false)]
    [InlineData((int)ActivationIoPoint.BeforePublicationRename, true)]
    public void DeadlineIncludesIoBeforeProbeAndAfterPassedResult(int boundary, bool utc)
    {
        using var f = ReadyWithNextSelection(keys);
        var clock = new RestoreTestClock();
        var calls = 0;
        var engine = f.Engine((_, _) => { calls++; return ActivationProbeOutcome.Ready; },
            (point, path, _) =>
            {
                if (point != (ActivationIoPoint)boundary ||
                    point == ActivationIoPoint.BeforePublicationRename && Path.GetFileName(path) != "active.json")
                    return;
                clock.Advance(utc ? TimeSpan.Zero : TimeSpan.FromSeconds(5),
                    utc ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(-5));
            }, clock, TimeSpan.FromSeconds(5));
        var plan = engine.PrepareActivation(2, f.Selection.Engine().Inspect().Revision);
        Assert.Equal(ActivationFailure.ReadinessTimedOut, Assert.Throws<ActivationException>(() =>
            engine.Activate(plan, Approve(plan))).Failure);
        Assert.Equal((ActivationIoPoint)boundary == ActivationIoPoint.BeforeReadiness ? 0 : 1, calls);
        var state = Wire.Read<ActivationControlDocument>(File.ReadAllBytes(f.Control), 65536);
        Assert.Equal(plan.TransactionId, state.Pending);
        Assert.Equal("0.2.0.0", state.Current!.Target.Version);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailureAfterTerminalRenameCannotMasqueradeAsCancellation(bool cancelled)
    {
        using var f = ReadyWithNextSelection(keys);
        using var cancellation = new CancellationTokenSource();
        var engine = f.Engine((_, _) => ActivationProbeOutcome.Ready, (point, path, _) =>
        {
            if (point != ActivationIoPoint.AfterPublicationRename || Path.GetFileName(path) != "active.json") return;
            if (!cancelled) throw new IOException("PRIVATE POST COMMIT");
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        });
        var plan = engine.PrepareActivation(2, f.Selection.Engine().Inspect().Revision);
        var error = Assert.Throws<ActivationException>(() => engine.Activate(plan, Approve(plan), cancellation.Token));
        Assert.Equal(ActivationFailure.PublishedOutcomeUncertain, error.Failure);
        Assert.Equal(plan.TransactionId, error.TransactionId);
        Assert.DoesNotContain("PRIVATE POST COMMIT", error.ToString());
        var observed = f.Engine().Inspect();
        Assert.Equal(ActivationStatus.PointerPublished, observed.Status);
        Assert.Equal("0.3.0.0", observed.Current!.Version);
        Assert.Equal(observed.Revision, f.Engine().Recover().Revision);
    }

    [Theory]
    [InlineData("active.json", 65536)]
    [InlineData("journal-v1.json", 262144)]
    [InlineData("readiness-result.json", 32768)]
    public void OversizedAuthoritativeDocumentsFailClosedAndRemainUntouched(string file, int maximum)
    {
        using var f = ReadyWithNextSelection(keys);
        var state = f.Engine().Inspect();
        var path = file == "active.json" ? f.Control :
            Path.Combine(f.Root, $"transaction-{state.Current!.TransitionId:N}", file);
        var bytes = Enumerable.Repeat((byte)' ', maximum + 1).ToArray();
        File.WriteAllBytes(path, bytes);
        Assert.Throws<ActivationException>(() => f.Engine().Inspect());
        Assert.Throws<ActivationException>(() => f.Engine().Recover());
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FinalPublicationIntentCannotBeRewoundOrSubstituted(bool rewindToBefore)
    {
        using var f = ReadyWithNextSelection(keys);
        byte[]? before = null;
        byte[]? pending = null;
        var engine = f.Engine((_, _) => ActivationProbeOutcome.Ready,
            (point, path, _) =>
            {
                if (point == ActivationIoPoint.BeforePendingReplace)
                    before = File.ReadAllBytes(f.Control);
                if (point == ActivationIoPoint.AfterPendingReplace)
                    pending = File.ReadAllBytes(f.Control);
            });
        var active = f.Engine().Inspect();
        var selected = f.Selection.Engine().Inspect();
        var plan = f.Prepare(engine, active.Revision, selected.Revision);
        engine.Activate(plan, Approve(plan));
        File.WriteAllBytes(f.Control, rewindToBefore ? before! : pending!);
        var retained = Directory.GetFiles(f.Root, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.ReadAllBytes);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            Assert.Equal(ActivationFailure.PublicationAmbiguous,
                Assert.Throws<ActivationException>(() => f.Engine().Inspect()).Failure);
            Assert.Equal(ActivationFailure.PublicationAmbiguous,
                Assert.Throws<ActivationException>(() => f.Engine().Recover()).Failure);
        }
        foreach (var pair in retained) Assert.Equal(pair.Value, File.ReadAllBytes(pair.Key));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExactFinalScratchCannotBeSubstituted(bool replaceName)
    {
        using var f = ReadyWithNextSelection(keys);
        var changed = false;
        var attempted = false;
        var engine = f.Engine((_, _) => ActivationProbeOutcome.Ready,
            (point, path, _) =>
            {
                if (point != ActivationIoPoint.BeforePublicationRename ||
                    Path.GetFileName(path) != "active.json") return;
                attempted = true;
                try
                {
                    if (replaceName) File.Move(path, path + ".retained");
                    File.WriteAllText(path, "{}");
                    changed = true;
                }
                catch (IOException) { }
            });
        var before = f.Engine().Inspect();
        var selected = f.Selection.Engine().Inspect();
        var plan = f.Prepare(engine, before.Revision, selected.Revision);
        var error = Record.Exception(() => engine.Activate(plan, Approve(plan)));
        Assert.True(attempted);
        if (changed)
        {
            Assert.NotNull(error);
            Assert.Equal(ActivationFailure.PublicationAmbiguous,
                Assert.Throws<ActivationException>(() => f.Engine().Inspect()).Failure);
        }
        else
        {
            Assert.Null(error);
            Assert.Equal("0.3.0.0", engine.Inspect().Current!.Version);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void FinalProviderCallbackCannotSubstituteReplacementOrTarget(bool recovery, bool target)
    {
        using var f = ReadyWithNextSelection(keys);
        if (recovery)
        {
            var failing = f.Engine((_, _) => ActivationProbeOutcome.NotReady);
            var pendingPlan = failing.PrepareActivation(2, f.Selection.Engine().Inspect().Revision);
            Assert.Equal(ActivationFailure.ReadinessFailed, Assert.Throws<ActivationException>(() =>
                failing.Activate(pendingPlan, Approve(pendingPlan))).Failure);
        }
        string? armedSource = null;
        string? changedSource = null;
        var staging = new LocalStagingEngine(f.Selection.Package.StagingRoot, f.Selection.Package.Trust, () =>
        {
            if (armedSource is { } source)
            {
                armedSource = null;
                changedSource = source;
                var path = target ? f.Control : source;
                File.Move(path, path + ".retained");
                File.WriteAllText(path, "{}");
            }
            return f.Selection.Package.Current;
        });
        var engine = f.Engine((_, _) => ActivationProbeOutcome.Ready, (point, path, _) =>
        {
            if (point == ActivationIoPoint.BeforePublicationRename &&
                (recovery ? Path.GetFileName(path).StartsWith("recovered-", StringComparison.Ordinal) :
                    Path.GetFileName(path) == "active.json"))
                armedSource = path;
        }, selection: f.Selection.Engine(staging: staging));
        var selected = f.Selection.Engine().Inspect();
        var plan = recovery ? null : engine.PrepareActivation(2, selected.Revision);
        Assert.Equal(ActivationFailure.InvalidControl, Assert.Throws<ActivationException>(() =>
        {
            if (recovery) engine.Recover();
            else engine.Activate(plan!, Approve(plan!));
        }).Failure);
        Assert.NotNull(changedSource);
        Assert.True(File.Exists(changedSource));
        if (target)
            Assert.Equal("{}", File.ReadAllText(f.Control));
        else
            Assert.NotNull(Wire.Read<ActivationControlDocument>(File.ReadAllBytes(f.Control), 65536).Pending);
    }

    [Theory]
    [InlineData("journal.json")]
    [InlineData("journal-v2.json")]
    [InlineData("journal-v99.json")]
    public void OldOrFutureJournalIsRefusedEvenWhenItHasNoPublication(string journal)
    {
        using var f = ReadyWithNextSelection(keys);
        var directory = Path.Combine(f.Root, "transaction-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, journal), "{}");
        var pointer = File.ReadAllBytes(f.Control);
        Assert.Equal(ActivationFailure.InvalidControl,
            Assert.Throws<ActivationException>(() => f.Engine().Inspect()).Failure);
        Assert.Equal(pointer, File.ReadAllBytes(f.Control));
    }

    private static ActivationFixture ReadyWithNextSelection(SigningKeys keys)
    {
        var f = new ActivationFixture(keys);
        f.Selection.Initialize();
        var firstSelection = f.Selection.Select(f.Selection.Stage());
        var activation = f.Engine((_, _) => ActivationProbeOutcome.Ready);
        f.Initialize(activation, firstSelection.Revision);
        var firstPlan = f.Prepare(activation, 0, firstSelection.Revision);
        activation.Activate(firstPlan, Approve(firstPlan));
        f.Selection.Select(f.Selection.Stage("0.3.0.0"));
        return f;
    }
}
