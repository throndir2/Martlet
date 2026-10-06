namespace Martlet.Conversation.Tests;

/// <summary>The background broker: deterministic placement by standing, slots, holds for other duties and a first come, first
/// served line when every place is busy.</summary>
public sealed class BackgroundBrokerTests
{
    // One companion PC with three hosts: a general one, one that makes images and one that sings.
    private static readonly BackgroundPlace General = new("host:general", "general");
    private static readonly BackgroundPlace Images = new("host:images", "images") { Duties = ["image generation"] };
    private static readonly BackgroundPlace Singer = new("host:singer", "singer") { Duties = ["singing"] };
    private static readonly IReadOnlyList<BackgroundPlace> Pool = [Singer, Images, General];

    [Fact]
    public void GeneralComputerFirstThenComputersKeptForOtherWorkInOrder()
    {
        var places = new BackgroundPlaces();
        var first = places.TryAcquire(Pool, "think-1")!;
        var second = places.TryAcquire(Pool, "think-2")!;
        var third = places.TryAcquire(Pool, "think-3")!;
        Assert.Equal("general", first.Place.Name);
        Assert.Equal("singer", second.Place.Name);
        Assert.Equal("images", third.Place.Name);
        Assert.Null(places.TryAcquire(Pool, "think-4"));
        first.Dispose();
        Assert.Equal("general", places.TryAcquire(Pool, "think-5")!.Place.Name);
    }

    [Fact]
    public void ConversationSharingRanksAfterOtherDuties()
    {
        var places = new BackgroundPlaces();
        var voice = new BackgroundPlace("host:voice", "voice", Rank: 1);
        Assert.Equal("singer", places.TryAcquire([voice, Singer], "think-1")!.Place.Name);
        Assert.Equal("voice", places.TryAcquire([voice, Singer], "think-2")!.Place.Name);
    }

    [Fact]
    public void SlotsSpreadAcrossComputersBeforeDoublingUp()
    {
        var places = new BackgroundPlaces();
        var twin = new BackgroundPlace("host:twin", "twin") { Slots = 2 };
        var pool = new[] { twin, General, Singer };
        Assert.Equal("twin", places.TryAcquire(pool, "think-1")!.Place.Name);
        Assert.Equal("general", places.TryAcquire(pool, "think-2")!.Place.Name);
        // Its second slot comes before the singing computer.
        Assert.Equal("twin", places.TryAcquire(pool, "think-3")!.Place.Name);
        Assert.Equal("singer", places.TryAcquire(pool, "think-4")!.Place.Name);
    }

    [Fact]
    public void HoldKeepsWorkOffTheSingingComputer()
    {
        var places = new BackgroundPlaces();
        using var song = places.Hold(Singer, "song-1");
        var general = places.TryAcquire(Pool, "think-1")!;
        Assert.Equal("images", places.TryAcquire(Pool, "think-2")!.Place.Name);
        Assert.Null(places.TryAcquire(Pool, "think-3"));
        general.Dispose();
        Assert.Equal("general", places.TryAcquire(Pool, "think-4")!.Place.Name);
    }

    [Fact]
    public async Task WaitersAreServedFirstComeFirstServed()
    {
        var places = new BackgroundPlaces();
        var only = new[] { General };
        var running = places.TryAcquire(only, "think-1")!;
        var second = places.AcquireAsync(only, "think-2", CancellationToken.None);
        var third = places.AcquireAsync(only, "think-3", CancellationToken.None);
        Assert.Equal(["think-2", "think-3"], places.Line);
        Assert.Equal(2, places.Position("think-3"));
        running.Dispose();
        var next = await second.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("think-2", next.Holder);
        Assert.False(third.IsCompleted);
        next.Dispose();
        Assert.Equal("think-3", (await third.WaitAsync(TimeSpan.FromSeconds(5))).Holder);
        Assert.Empty(places.Line);
    }

    [Fact]
    public async Task CanceledWaiterLeavesTheLine()
    {
        var places = new BackgroundPlaces();
        var only = new[] { General };
        var running = places.TryAcquire(only, "think-1")!;
        using var cancel = new CancellationTokenSource();
        var waiting = places.AcquireAsync(only, "think-2", cancel.Token);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Empty(places.Line);
        running.Dispose();
        Assert.Equal(0, places.Load(General.Id));
    }

    [Fact]
    public async Task JobStartedInLineRunsWhenAPlaceFreesUp()
    {
        using var jobs = new BackgroundJobs();
        var kind = new BackgroundJobKind("think", 2, 10, TimeSpan.FromMinutes(1));
        var only = new[] { General };
        var release = new TaskCompletionSource();
        var first = jobs.Start(kind, "first", async (_, token) =>
        {
            await release.Task.WaitAsync(token);
            return BackgroundJobOutcome.Done("one");
        }, only, wait: true);
        Assert.Equal("general", first.Job!.Place!.Name);
        Assert.Null(first.Queued);

        var ranOn = new TaskCompletionSource<string?>();
        var second = jobs.Start(kind, "second", (job, _) =>
        {
            ranOn.TrySetResult(job.Place?.Name);
            return Task.FromResult(BackgroundJobOutcome.Done("two"));
        }, only, wait: true);
        Assert.NotNull(second.Job);
        Assert.Equal("think-1 on general", second.Queued);
        Assert.Null(second.Job!.Place);
        Assert.Equal(BackgroundJobState.Waiting, second.Job.State);

        // A third is over the kind's limit (one running, one waiting).
        Assert.Equal("busy", jobs.Start(kind, "third", (_, _) => Task.FromResult(BackgroundJobOutcome.Done("x")), only, wait: true).Refusal);

        release.SetResult();
        Assert.Equal("general", await ranOn.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task CancelingAJobInLineFreesNothingAndStopsIt()
    {
        using var jobs = new BackgroundJobs();
        var kind = new BackgroundJobKind("think", 2, 10, TimeSpan.FromMinutes(1));
        var only = new[] { General };
        var hold = jobs.Places.Hold(General, "song-1");
        var waiting = jobs.Start(kind, "waits", (_, _) => Task.FromResult(BackgroundJobOutcome.Done("never")), only, wait: true).Job!;
        await WaitUntil(() => jobs.Places.Position(waiting.Id) == 1);
        jobs.Cancel(waiting.Id, BackgroundJob.CanceledByYou);
        await WaitUntil(() => waiting.Finished);
        Assert.Equal(BackgroundJobState.Canceled, waiting.State);
        Assert.Empty(jobs.Places.Line);
        hold.Dispose();
        Assert.Equal(0, jobs.Places.Load(General.Id));
    }

    [Fact]
    public void WithoutWaitingAFullPoolIsStillRefused()
    {
        using var jobs = new BackgroundJobs();
        var kind = new BackgroundJobKind("think", 4, 10, TimeSpan.FromMinutes(1));
        using var hold = jobs.Places.Hold(General, "song-1");
        var refused = jobs.Start(kind, "x", (_, _) => Task.FromResult(BackgroundJobOutcome.Done("x")), [General]);
        Assert.Equal("busy", refused.Refusal);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }
}
