using Martlet.Core.Settings;
using Martlet.Core.Sync;

namespace Martlet.Core.Tests;

/// <summary>The memory brief is one for all the owner's computers: memory-brief.json travels as the account setting
/// <c>memory-brief</c>, a computer that takes another's brief says so (so it isn't written again there at once), and turning it
/// off on one computer turns it off on the other.</summary>
public sealed class MemoryBriefShareTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "martlet-memory-brief-share-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task Two_computers_keep_one_memory_brief()
    {
        Assert.True(SettingScopes.IsAccountKey(MemoryBriefFile.SharedKey));
        var a = Path.Combine(root, "a");
        var b = Path.Combine(root, "b");
        var followedOnB = 0;
        var nodeA = new SharedSettingsNode(a, "desktop-a", [MemoryBriefFile.Section(a)]);
        var nodeB = new SharedSettingsNode(b, "desktop-b", [MemoryBriefFile.Section(b, () => followedOnB++)]);
        var start = DateTimeOffset.UtcNow;

        // Neither has a brief yet: nothing is followed, and a default never overrides a brief written elsewhere.
        await nodeB.SyncAsync([], true, start, CancellationToken.None);
        MemoryBriefFile.Save(a, new MemoryBriefFile
        {
            Text = "The user loves tea.\n[Alex] Plays the cello.", By = MemoryBriefFile.ByThinking, WrittenAt = start, Facts = 2,
            Basis = [Guid.NewGuid().ToString("N") + ":1", Guid.NewGuid().ToString("N") + ":3", "~v2=Alex"]
        });
        var recorded = await nodeA.SyncAsync([nodeB.Document], true, start.AddSeconds(15), CancellationToken.None);
        Assert.Contains(MemoryBriefFile.SharedKey, recorded.Recorded);

        var followed = await nodeB.SyncAsync([nodeA.Document], true, start.AddSeconds(30), CancellationToken.None);
        Assert.Contains(followed.Applied, change => change.Key == MemoryBriefFile.SharedKey && change.By == "desktop-a");
        Assert.Equal(1, followedOnB);
        var onB = MemoryBriefFile.Load(b);
        Assert.Equal(MemoryBriefFile.Load(a).Write(), onB.Write());
        Assert.Equal(3, onB.Basis.Count);

        // Turned off on B: A follows, and the text stays for when it is turned on again.
        MemoryBriefFile.Save(b, onB with { On = false });
        await nodeB.SyncAsync([nodeA.Document], true, start.AddSeconds(45), CancellationToken.None);
        await nodeA.SyncAsync([nodeB.Document], true, start.AddSeconds(60), CancellationToken.None);
        var onA = MemoryBriefFile.Load(a);
        Assert.False(onA.On);
        Assert.Equal("The user loves tea.\n[Alex] Plays the cello.", onA.Text);

        // A brief from a newer Martlet waits on this one, which keeps its own.
        var newer = nodeA.Document.Find(MemoryBriefFile.SharedKey)!;
        Assert.Null(MemoryBriefFile.Parse(newer.Value.Replace("\"schema_version\":1", "\"schema_version\":2", StringComparison.Ordinal)));
    }

    [Fact]
    public void The_file_keeps_its_bounds()
    {
        var directory = Path.Combine(root, "bounds");
        Assert.Null(MemoryBriefFile.TryLoad(directory));
        Assert.True(MemoryBriefFile.Load(directory).On);
        Assert.ThrowsAny<Exception>(() => MemoryBriefFile.Save(directory, new MemoryBriefFile { Text = new string('a', MemoryBriefFile.MaximumCharacters + 1) }));
        Assert.ThrowsAny<Exception>(() => MemoryBriefFile.Save(directory, new MemoryBriefFile { Text = "bell\a" }));
        Assert.ThrowsAny<Exception>(() => MemoryBriefFile.Save(directory, new MemoryBriefFile { By = "someone" }));
        Directory.CreateDirectory(directory);
        File.WriteAllText(MemoryBriefFile.PathOf(directory), "not json");
        Assert.Null(MemoryBriefFile.TryLoad(directory));
        Assert.Null(MemoryBriefFile.Load(directory).Text);
    }
}
