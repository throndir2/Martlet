namespace Martlet.Memory.Tests;

internal sealed class ManualClock : TimeProvider
{
    private readonly object gate = new();
    private DateTimeOffset utc = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
    private long timestamp;

    internal DateTimeOffset Utc
    {
        get
        {
            lock (gate)
                return utc;
        }
    }

    public override DateTimeOffset GetUtcNow() => Utc;

    public override long GetTimestamp()
    {
        lock (gate)
            return timestamp;
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    internal void Advance(TimeSpan duration)
    {
        lock (gate)
        {
            utc += duration;
            timestamp += duration.Ticks;
        }
    }
}

internal sealed class TestScope : IDisposable
{
    internal TestScope()
    {
        Root = Path.Combine(AppContext.BaseDirectory, "memory-tests", Guid.NewGuid().ToString("N"));
        StoreDirectory = Path.Combine(Root, "store");
        ExportPath = Path.Combine(Root, "memory-export.json");
        Directory.CreateDirectory(Root);
    }

    internal string Root { get; }
    internal string StoreDirectory { get; }
    internal string ExportPath { get; }
    internal string StorePath => Path.Combine(StoreDirectory, MemoryStore.StoreFileName);
    internal string PendingPath => Path.Combine(StoreDirectory, MemoryStore.PendingFileName);

    internal MemoryStore Open(ManualClock clock, MemoryTestHooks? hooks = null)
    {
        var preview = MemoryStoreActivationPreview.Create(StoreDirectory);
        return MemoryStore.Open(preview, preview.Authorize(MemoryConsentDecision.Allow), clock, hooks);
    }

    public void Dispose()
    {
        if (Directory.Exists(Root))
            Directory.Delete(Root, recursive: true);
    }
}

internal static class MemoryFixtures
{
    internal const string Canary =
        "credential=DO-NOT-LOG transcript Joanna C:\\Users\\Joanna\\private 203.0.113.7";

    internal static MemoryProvenance Provenance(ManualClock clock, MemorySourceKind kind = MemorySourceKind.UserEntry) =>
        new()
        {
            SourceKind = kind,
            ConsentId = Guid.NewGuid(),
            ObservedAtUtc = clock.Utc
        };

    internal static SaveFactRequest Save(ManualClock clock, string content,
        MemoryRetention? retention = null, MemorySourceKind kind = MemorySourceKind.UserEntry) =>
        new()
        {
            Content = content,
            Provenance = Provenance(clock, kind),
            Retention = retention ?? MemoryRetention.UntilDeleted()
        };

    internal static async Task<MemoryException> FailureAsync(MemoryFailure expected, Func<Task> action)
    {
        var error = await Assert.ThrowsAsync<MemoryException>(action);
        Assert.Equal(expected, error.Failure);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(Canary, error.ToString(), StringComparison.Ordinal);
        return error;
    }

    internal static MemoryException Failure(MemoryFailure expected, Action action)
    {
        var error = Assert.Throws<MemoryException>(action);
        Assert.Equal(expected, error.Failure);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(Canary, error.ToString(), StringComparison.Ordinal);
        return error;
    }
}
