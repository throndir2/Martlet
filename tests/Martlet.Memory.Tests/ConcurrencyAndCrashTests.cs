namespace Martlet.Memory.Tests;

public sealed class ConcurrencyAndCrashTests
{
    [Fact]
    public async Task DeleteInvalidatesInFlightRetrievalAndRemovesDerivedCopies()
    {
        using var scope = new TestScope();
        var clock = new ManualClock();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var hooks = new MemoryTestHooks
        {
            Query = (point, _) =>
            {
                if (point != MemoryQueryPoint.BeforeRevisionCheck)
                    return;
                entered.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            }
        };
        using var store = scope.Open(clock, hooks);
        var fact = await store.SaveAsync(MemoryFixtures.Save(clock, "inflight cometmarker"));

        var querying = Task.Run(() => store.RetrieveAsync(new() { Text = "cometmarker" }));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        await store.DeleteAsync(new()
        {
            Id = fact.Fact.Id,
            ExpectedRevision = fact.Fact.Revision,
            ConsentId = Guid.NewGuid()
        });
        release.Set();

        await MemoryFixtures.FailureAsync(MemoryFailure.QueryInvalidated, async () => await querying);
        Assert.False(store.DerivedIndexContains("cometmarker"));
        Assert.Equal(0, store.DerivedState.CachedQueries);
    }

    [Fact]
    public async Task CancellationBeforeAtomicCommitPreservesOriginalAndCleansStage()
    {
        using var scope = new TestScope();
        var clock = new ManualClock();
        using var canceled = new CancellationTokenSource();
        var cancelAtStage = false;
        var hooks = new MemoryTestHooks
        {
            Io = (point, _) =>
            {
                if (cancelAtStage && point == MemoryIoPoint.AfterStoreStageWrite)
                    canceled.Cancel();
            }
        };
        using var store = scope.Open(clock, hooks);
        var saved = await store.SaveAsync(MemoryFixtures.Save(clock, "original stable fact"));
        cancelAtStage = true;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await store.EditAsync(new()
            {
                Id = saved.Fact.Id,
                ExpectedRevision = saved.Fact.Revision,
                Content = "must not commit",
                Provenance = MemoryFixtures.Provenance(clock),
                Retention = MemoryRetention.UntilDeleted()
            }, canceled.Token));
        Assert.Equal("original stable fact", Assert.Single((await store.InspectAsync()).Facts).Content);
        Assert.False(File.Exists(scope.PendingPath));
        Assert.DoesNotContain("must not commit", await File.ReadAllTextAsync(scope.StorePath),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task InterruptedWriteBeforeCommitPreservesOriginal()
    {
        using var scope = new TestScope();
        var clock = new ManualClock();
        var fail = false;
        var hooks = new MemoryTestHooks
        {
            Io = (point, _) =>
            {
                if (fail && point == MemoryIoPoint.BeforeStoreCommit)
                    throw new IOException(MemoryFixtures.Canary);
            }
        };
        using var store = scope.Open(clock, hooks);
        var saved = await store.SaveAsync(MemoryFixtures.Save(clock, "atomic original"));
        fail = true;

        await MemoryFixtures.FailureAsync(MemoryFailure.IoFailure, async () =>
            await store.EditAsync(new()
            {
                Id = saved.Fact.Id,
                ExpectedRevision = saved.Fact.Revision,
                Content = "interrupted replacement",
                Provenance = MemoryFixtures.Provenance(clock),
                Retention = MemoryRetention.UntilDeleted()
            }));
        Assert.Equal("atomic original", Assert.Single((await store.InspectAsync()).Facts).Content);
        Assert.DoesNotContain("interrupted replacement", await File.ReadAllTextAsync(scope.StorePath),
            StringComparison.Ordinal);
        Assert.False(File.Exists(scope.PendingPath));
    }

    [Fact]
    public async Task CancellationArrivingAfterCommitDoesNotTurnSuccessIntoFailure()
    {
        using var scope = new TestScope();
        var clock = new ManualClock();
        using var canceled = new CancellationTokenSource();
        var hooks = new MemoryTestHooks
        {
            Io = (point, _) =>
            {
                if (point == MemoryIoPoint.AfterStoreCommit)
                    canceled.Cancel();
            }
        };
        using var store = scope.Open(clock, hooks);

        var receipt = await store.SaveAsync(MemoryFixtures.Save(clock, "committed boundary"), canceled.Token);
        Assert.True(canceled.IsCancellationRequested);
        Assert.Equal(receipt.Fact.Id, Assert.Single((await store.InspectAsync()).Facts).Id);
    }

    [Fact]
    public async Task ReopenDiscardsOnlyOwnedUncommittedCrashStage()
    {
        using var scope = new TestScope();
        var clock = new ManualClock();
        Guid factId;
        using (var store = scope.Open(clock))
            factId = (await store.SaveAsync(MemoryFixtures.Save(clock, "authoritative current"))).Fact.Id;

        await File.WriteAllTextAsync(scope.PendingPath, "{\"partial\":");
        using var reopened = scope.Open(clock);
        Assert.Equal(factId, Assert.Single((await reopened.InspectAsync()).Facts).Id);
        Assert.False(File.Exists(scope.PendingPath));
        Assert.Contains("authoritative current", await File.ReadAllTextAsync(scope.StorePath),
            StringComparison.Ordinal);
    }
}
