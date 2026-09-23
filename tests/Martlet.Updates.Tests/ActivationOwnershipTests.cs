using Martlet.Core.Settings;
using static Martlet.Updates.Tests.ActivationFixture;

namespace Martlet.Updates.Tests;

public sealed class ActivationOwnershipTests(SigningKeys keys, ProductionPayloadFixture production)
    : IClassFixture<SigningKeys>, IClassFixture<ProductionPayloadFixture>
{
    [Fact]
    public async Task BoundedReadinessKeepsPointerSelectionSettingsAndEveryStagePinned()
    {
        using var f = new ActivationFixture(keys);
        f.Selection.Initialize();
        var firstSelection = f.Selection.Select(f.Selection.Stage(production: production));
        var initial = f.Engine((_, _) => ActivationProbeOutcome.Ready);
        f.Initialize(initial, firstSelection.Revision);
        var initialPlan = f.Prepare(initial, 0, firstSelection.Revision);
        initial.Activate(initialPlan, Approve(initialPlan));
        var nextSelection = f.Selection.Select(f.Selection.Stage("0.3.0.0", production: production));

        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var engine = f.Engine((request, _) =>
        {
            Assert.True(File.Exists(request.ExecutablePath));
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException();
            return ActivationProbeOutcome.Ready;
        });
        var before = f.Engine().Inspect();
        var plan = f.Prepare(engine, before.Revision, nextSelection.Revision);
        var activation = Task.Run(() => engine.Activate(plan, Approve(plan)));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(15)));
            Assert.False(activation.IsCompleted);
            Assert.Equal(ActivationFailure.Busy,
                Assert.Throws<ActivationException>(() => engine.Inspect()).Failure);
            Assert.Equal(ActivationFailure.Busy,
                Assert.Throws<ActivationException>(() => f.Engine().Inspect()).Failure);
            Assert.Equal(SelectionFailure.Busy,
                Assert.Throws<SelectionException>(() => f.Selection.Engine().Inspect()).Failure);
            Assert.Equal(StagingFailure.Busy,
                Assert.Throws<StagingException>(() => f.Selection.Staging.InspectStaged(
                    Path.Combine(f.Selection.Package.StagingRoot,
                        nextSelection.CurrentSelection!.StageName))).Failure);
            var loaded = await f.Selection.Settings.LoadAsync();
            Assert.False((await f.Selection.Settings.SaveAsync(loaded.Settings!, loaded.Revision)).Saved);

            if (OperatingSystem.IsWindows())
            {
                var paths = new List<string>
                {
                    f.Control,
                    Path.Combine(f.Selection.Root, "selection.json"),
                    f.Selection.Settings.FilePath
                };
                foreach (var selected in new[]
                    { nextSelection.CurrentSelection!, nextSelection.PreviousSelection! })
                {
                    var stage = Path.Combine(f.Selection.Package.StagingRoot, selected.StageName);
                    paths.Add(Path.Combine(stage, "candidate.json"));
                    paths.Add(Path.Combine(stage, "candidate.zip"));
                    paths.Add(Path.Combine(stage, "staged.json"));
                    paths.Add(Path.Combine(stage, "payload", "Desktop", "Martlet.Desktop.exe"));
                }
                foreach (var directory in Directory.GetDirectories(f.Root, "transaction-*"))
                    paths.AddRange(Directory.GetFiles(directory).Where(path => Path.GetFileName(path) is
                        "before.json" or "journal-v1.json" or "readiness-result.json"));
                paths.AddRange(Directory.GetFiles(f.Selection.Root, "journal-v*.json", SearchOption.AllDirectories));
                foreach (var path in paths)
                {
                    Assert.Throws<IOException>(() => File.WriteAllText(path, "UNAUTHORIZED"));
                    Assert.Throws<IOException>(() => File.Move(path, path + ".moved"));
                }
            }
            Assert.False(File.Exists(f.Selection.Package.ExecutedCanary));
            production.AssertNotExecuted();
        }
        finally
        {
            release.Set();
        }
        var result = await activation.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.False(result.IsRunnable);
        Assert.Equal(ActivationStatus.PointerPublished, result.Status);
        Assert.Equal("0.3.0.0", result.Current!.Version);
        Assert.Equal("0.2.0.0", result.Previous!.Version);
        f.Selection.Package.AssertPrivateDataUnchanged();
        production.AssertNotExecuted();
    }

    [Theory]
    [InlineData((int)ActivationIoPoint.BeforeReadiness)]
    [InlineData((int)ActivationIoPoint.BeforePublicationRename)]
    public void LiveInstallationFactsAreRecheckedThroughFinalPublication(int boundary)
    {
        using var f = ReadyWithNextSelection(keys);
        var engine = f.Engine((_, _) => ActivationProbeOutcome.Ready, (point, path, _) =>
        {
            if (point != (ActivationIoPoint)boundary ||
                point == ActivationIoPoint.BeforePublicationRename && Path.GetFileName(path) != "active.json")
                return;
            f.Selection.Package.Current = f.Selection.Package.Current with { InstallationRevision = new('b', 64) };
        });
        var plan = engine.PrepareActivation(2, f.Selection.Engine().Inspect().Revision);
        Assert.Equal(ActivationFailure.Conflict, Assert.Throws<ActivationException>(() =>
            engine.Activate(plan, Approve(plan))).Failure);
        var state = Wire.Read<ActivationControlDocument>(File.ReadAllBytes(f.Control), 65536);
        Assert.Equal(plan.TransactionId, state.Pending);
        Assert.Equal("0.2.0.0", state.Current!.Target.Version);
    }

    [Fact]
    public async Task CancellationAndTimeoutRetainOwnershipUntilTheProbeActuallyReturns()
    {
        using var f = ReadyWithNextSelection(keys);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var clock = new RestoreTestClock();
        var engine = f.Engine((_, _) =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException();
            return ActivationProbeOutcome.Ready;
        }, clock: clock, readinessLimit: TimeSpan.FromSeconds(5));
        var plan = engine.PrepareActivation(2, f.Selection.Engine().Inspect().Revision);
        var work = Task.Run(() => Record.Exception(() => engine.Activate(plan, Approve(plan), cancellation.Token)));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(15)));
            clock.Advance(TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(6));
            await cancellation.CancelAsync();
            Assert.False(work.IsCompleted);
            Assert.Equal(ActivationFailure.Busy, Assert.Throws<ActivationException>(() => f.Engine().Recover()).Failure);
            Assert.Equal(SelectionFailure.Busy, Assert.Throws<SelectionException>(() => f.Selection.Engine().Inspect()).Failure);
            var loaded = await f.Selection.Settings.LoadAsync();
            Assert.False((await f.Selection.Settings.SaveAsync(loaded.Settings!, loaded.Revision)).Saved);
        }
        finally { release.Set(); }
        Assert.Equal(ActivationFailure.Cancelled, Assert.IsType<ActivationException>(await work).Failure);
        Assert.Equal(ActivationStatus.ReadinessPending, f.Engine().Inspect().Status);
        Assert.Equal("0.2.0.0", f.Engine().Recover().Current!.Version);
    }

    [Fact]
    public void ExistingSettingsWriterPreventsActivationAdmission()
    {
        using var f = ReadyWithNextSelection(keys);
        var engine = f.Engine((_, _) => ActivationProbeOutcome.Ready);
        var plan = engine.PrepareActivation(2, f.Selection.Engine().Inspect().Revision);
        var pointer = File.ReadAllBytes(f.Control);
        using (var writer = new FileStream(f.Selection.Settings.FilePath + ".lock",
            FileMode.Open, FileAccess.Write, FileShare.None))
        {
            Assert.Throws<ActivationException>(() => engine.Activate(plan, Approve(plan)));
        }
        Assert.Equal(pointer, File.ReadAllBytes(f.Control));
        Assert.Single(Directory.GetDirectories(f.Root, "transaction-*"));
    }

    [Fact]
    public async Task BlockedCancellationCallbackCannotPermitPointerPublication()
    {
        using var f = ReadyWithNextSelection(keys);
        using var probeEntered = new ManualResetEventSlim();
        using var leaveProbe = new ManualResetEventSlim();
        using var callbackEntered = new ManualResetEventSlim();
        using var leaveCallback = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        using var registration = cancellation.Token.Register(() =>
        {
            callbackEntered.Set();
            leaveCallback.Wait();
        });
        var engine = f.Engine((_, _) =>
        {
            probeEntered.Set();
            leaveProbe.Wait();
            return ActivationProbeOutcome.Ready;
        });
        var before = f.Engine().Inspect();
        var selected = f.Selection.Engine().Inspect();
        var plan = f.Prepare(engine, before.Revision, selected.Revision);
        var activation = Task.Run(() => Record.Exception(() =>
            engine.Activate(plan, Approve(plan), cancellation.Token)));
        Task? cancellationWork = null;
        try
        {
            Assert.True(probeEntered.Wait(TimeSpan.FromSeconds(15)));
            cancellationWork = cancellation.CancelAsync();
            Assert.True(callbackEntered.Wait(TimeSpan.FromSeconds(15)));
            leaveProbe.Set();
            var error = Assert.IsType<ActivationException>(
                await activation.WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.Equal(ActivationFailure.Cancelled, error.Failure);
            Assert.False(cancellationWork.IsCompleted);
            Assert.Equal(ActivationStatus.ReadinessPending, f.Engine().Inspect().Status);
            Assert.Equal("0.2.0.0", f.Engine().Recover(plan.TransactionId).Current!.Version);
        }
        finally
        {
            leaveProbe.Set();
            leaveCallback.Set();
            if (cancellationWork is not null) await cancellationWork;
            await activation;
        }
        Assert.False(File.Exists(f.Selection.Package.ExecutedCanary));
    }

    [Theory]
    [InlineData((int)ActivationIoPoint.BeforeCreate)]
    [InlineData((int)ActivationIoPoint.AfterCreate)]
    [InlineData((int)ActivationIoPoint.BeforeWrite)]
    [InlineData((int)ActivationIoPoint.AfterWrite)]
    [InlineData((int)ActivationIoPoint.BeforeFlush)]
    [InlineData((int)ActivationIoPoint.AfterFlush)]
    [InlineData((int)ActivationIoPoint.BeforeInitializeReplace)]
    public void InterruptedInitializationNeverAdoptsOrOverwritesState(int boundary)
    {
        using var f = new ActivationFixture(keys);
        f.Selection.Initialize();
        var selected = f.Selection.Select(f.Selection.Stage());
        var engine = f.Engine(io: (point, _, _) =>
        {
            if (point == (ActivationIoPoint)boundary) throw new IOException();
        });
        Assert.Equal(ActivationFailure.Unavailable,
            Assert.Throws<ActivationException>(() => engine.Initialize(selected.Revision)).Failure);
        Assert.False(File.Exists(f.Control));
        if ((ActivationIoPoint)boundary == ActivationIoPoint.BeforeCreate)
        {
            Assert.Equal(ActivationStatus.NoActiveVersion,
                f.Engine().Initialize(selected.Revision).Status);
        }
        else
        {
            Assert.True(File.Exists(Path.Combine(f.Root, "initial.json")));
            Assert.Equal(ActivationFailure.RecoveryRequired,
                Assert.Throws<ActivationException>(() =>
                    f.Engine().Initialize(selected.Revision)).Failure);
        }
        Assert.False(File.Exists(f.Selection.Package.ExecutedCanary));
    }

    [Fact]
    public void CapacityAndUnknownOwnershipArePreservedWithoutRecursiveCleanup()
    {
        using var f = new ActivationFixture(keys);
        f.Selection.Initialize();
        var selected = f.Selection.Select(f.Selection.Stage());
        var engine = f.Engine((_, _) => ActivationProbeOutcome.Ready);
        f.Initialize(engine, selected.Revision);
        var index = 0;
        while (Directory.GetFileSystemEntries(f.Root).Length < 128)
            File.WriteAllText(Path.Combine(f.Root, $"foreign-{index++:D3}"), "PRESERVE");
        Assert.Equal(ActivationFailure.CapacityExceeded,
            Assert.Throws<ActivationException>(() =>
                engine.PrepareActivation(0, selected.Revision)).Failure);
        Assert.Equal(128, Directory.GetFileSystemEntries(f.Root).Length);
        Assert.All(Directory.GetFiles(f.Root, "foreign-*"),
            path => Assert.Equal("PRESERVE", File.ReadAllText(path)));
        Assert.Equal(ActivationStatus.NoActiveVersion, engine.Inspect().Status);
    }

    [Theory]
    [InlineData(126, true)]
    [InlineData(127, false)]
    public void InitializationReservesItsPersistentControlSlot(int foreignChildren, bool accepted)
    {
        using var f = new ActivationFixture(keys);
        f.Selection.Initialize();
        var selected = f.Selection.Select(f.Selection.Stage());
        for (var index = 0; index < foreignChildren; index++)
            File.WriteAllText(Path.Combine(f.Root, $"foreign-{index:D3}"), "PRESERVE");
        var engine = f.Engine();
        if (accepted)
        {
            Assert.Equal(ActivationStatus.NoActiveVersion,
                engine.Initialize(selected.Revision).Status);
            Assert.Equal(128, Directory.GetFileSystemEntries(f.Root).Length);
            Assert.Equal(ActivationStatus.NoActiveVersion, engine.Inspect().Status);
        }
        else
        {
            Assert.Equal(ActivationFailure.CapacityExceeded,
                Assert.Throws<ActivationException>(() =>
                    engine.Initialize(selected.Revision)).Failure);
            Assert.False(File.Exists(f.Control));
            Assert.False(File.Exists(Path.Combine(f.Root, "initial.json")));
            Assert.Equal(128, Directory.GetFileSystemEntries(f.Root).Length);
        }
        Assert.All(Directory.GetFiles(f.Root, "foreign-*"),
            path => Assert.Equal("PRESERVE", File.ReadAllText(path)));
    }

    [Fact]
    public void FirstActivationReviewsCurrentSelectionWithoutAdoptingAnUnreadyPrevious()
    {
        using var f = new ActivationFixture(keys);
        f.Selection.Initialize();
        f.Selection.Select(f.Selection.Stage());
        var second = f.Selection.Select(f.Selection.Stage("0.3.0.0"));
        var engine = f.Engine((_, _) => ActivationProbeOutcome.Ready);
        var initialized = engine.Initialize(second.Revision);
        var plan = engine.PrepareActivation(initialized.Revision, second.Revision);
        Assert.Equal("0.3.0.0", plan.Candidate.Version);
        Assert.Null(plan.PreviousAfterActivation);
        var activated = engine.Activate(plan, Approve(plan));
        Assert.Equal("0.3.0.0", activated.Current!.Version);
        Assert.Null(activated.Previous);
        Assert.False(activated.IsRunnable);
        Assert.Equal(ActivationStatus.PointerPublished, activated.Status);
    }

    [Fact]
    public void SupersededAndCrossInstancePlansCannotPublish()
    {
        using var f = new ActivationFixture(keys);
        f.Selection.Initialize();
        var selected = f.Selection.Select(f.Selection.Stage());
        var engine = f.Engine((_, _) => ActivationProbeOutcome.Ready);
        f.Initialize(engine, selected.Revision);
        var first = f.Prepare(engine, 0, selected.Revision);
        var firstApproval = Approve(first);
        var second = f.Prepare(engine, 0, selected.Revision);
        Assert.Equal(ActivationFailure.Conflict,
            Assert.Throws<ActivationException>(() =>
                engine.Activate(first, firstApproval)).Failure);
        var secondApproval = Approve(second);
        Assert.Equal(ActivationFailure.Conflict,
            Assert.Throws<ActivationException>(() =>
                f.Engine((_, _) => ActivationProbeOutcome.Ready)
                    .Activate(second, secondApproval)).Failure);
        Assert.Equal(ActivationStatus.NoActiveVersion, engine.Inspect().Status);
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
