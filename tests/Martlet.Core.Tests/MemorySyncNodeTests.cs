using System.Diagnostics;
using Martlet.Core.Sync;

namespace Martlet.Core.Tests;

/// <summary>The memory sync handles a large store in one pass, and when every computer's facts together don't fit one store it
/// forgets the oldest facts picked out of conversations and says how many didn't fit.</summary>
public sealed class MemorySyncNodeTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "martlet-memory-sync-node-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private static LocalMemories Store(Guid storeId, int count, int offset, DateTimeOffset start) => new(storeId,
        [.. Enumerable.Range(offset, count).Select(i => new LocalMemory(Guid.NewGuid(), 1, start.AddSeconds(i), $$"""{"n":{{i}}}"""))]);

    private static MemoryFactInfo Untyped(string fact) => new(false, false);

    [Fact]
    public async Task A_large_store_syncs_in_one_pass_and_nothing_is_recorded_twice()
    {
        var start = DateTimeOffset.UtcNow.AddDays(-1);
        var local = Store(Guid.NewGuid(), 20_000, 0, start);
        var node = new MemorySyncNode(Path.Combine(root, "a"), "desktop-a");
        var timer = Stopwatch.StartNew();
        var first = await node.SyncAsync([], _ => Task.FromResult(local), (_, _) => Task.CompletedTask, Untyped,
            DateTimeOffset.UtcNow, CancellationToken.None);
        timer.Stop();
        Assert.Equal(20_000, first.Document.Live.Count());
        Assert.Equal(20_000, first.Recorded);
        Assert.Equal(0, first.Full);
        // One fact at a time made this quadratic (minutes); one pass takes seconds at most.
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(30), $"{timer.Elapsed}");

        var second = await node.SyncAsync([], _ => Task.FromResult(local), (_, _) => Task.CompletedTask, Untyped,
            DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Equal(0, second.Recorded);
        Assert.Equal(first.Document.Digest(), second.Document.Digest());
    }

    [Fact]
    public async Task Facts_that_do_not_fit_one_store_are_counted_and_the_oldest_are_forgotten()
    {
        var start = DateTimeOffset.UtcNow.AddDays(-1);
        var other = new MemorySyncNode(Path.Combine(root, "other"), "desktop-b", 300);
        var theirs = (await other.SyncAsync([], _ => Task.FromResult(Store(Guid.NewGuid(), 200, 0, start)),
            (_, _) => Task.CompletedTask, Untyped, DateTimeOffset.UtcNow, CancellationToken.None)).Document;

        MemoryChanges? applied = null;
        var node = new MemorySyncNode(Path.Combine(root, "a"), "desktop-a", 300);
        var result = await node.SyncAsync([theirs], _ => Task.FromResult(Store(Guid.NewGuid(), 200, 200, start)),
            (changes, _) => { applied = changes; return Task.CompletedTask; }, Untyped, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(100, result.Full);
        Assert.Equal(300, result.Document.Live.Count());
        Assert.Equal(100, result.Document.Forgotten.Count());
        // The oldest (the other computer's first 100) are the ones forgotten; this store takes the 100 newest of theirs.
        Assert.Equal(100, applied!.Facts.Count);
        Assert.All(result.Document.Live, fact => Assert.True(fact.UpdatedAt >= start.AddSeconds(100)));
    }
}
