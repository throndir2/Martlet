using System.Text;
using Martlet.Conversation.Guides;

namespace Martlet.Conversation.Tests;

public sealed class AppGuideStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "martlet-guide-store-" + Guid.NewGuid().ToString("N"));

    public AppGuideStoreTests() => Directory.CreateDirectory(directory);

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(directory)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(directory, recursive: true);
    }

    private string LibraryPath => Path.Combine(directory, FileAppGuideStore.LibraryFile);

    private static AppGuide Guide(string name, int chunks = 2, int characters = 100) =>
        new(AppGuideKeys.Of(name), name, ["wiki.example"], DateTimeOffset.UnixEpoch, [new("https://wiki.example/a", "A", 10)],
            [.. Enumerable.Range(0, chunks).Select(i => new GuideChunk("https://wiki.example/a", "A", "A › " + i, new string('x', characters)))]);

    [Fact]
    public async Task FilesSayTheirFormatAndRoundTrip()
    {
        var store = new FileAppGuideStore(directory);
        await store.SaveGuideAsync(Guide("Elden Ring"), default);
        Assert.StartsWith("{\"formatVersion\":1,", await File.ReadAllTextAsync(LibraryPath));
        Assert.StartsWith("{\"formatVersion\":1,", await File.ReadAllTextAsync(Path.Combine(directory, "elden-ring.guide.json")));
        Assert.Equal(2, (await store.LoadGuideAsync("elden-ring", default))!.Chunks.Count);
        Assert.Empty(Directory.EnumerateFiles(directory, "*.pending"));
    }

    [Fact]
    public async Task AnUnreadableLibraryIsRefusedAndNeverReset()
    {
        await File.WriteAllTextAsync(LibraryPath, "{\"apps\": [ {\"key\": \"elden-ring\", ");
        var store = new FileAppGuideStore(directory);

        var error = await Assert.ThrowsAsync<AppGuideFileException>(() => store.LoadLibraryAsync(default));
        Assert.False(error.IsNewer);
        Assert.Equal(LibraryPath, error.File);
        Assert.Contains("library.json", error.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<AppGuideFileException>(() => store.SaveGuideAsync(Guide("Elden Ring"), default));
        await Assert.ThrowsAsync<AppGuideFileException>(() => store.DeleteGuideAsync("elden-ring", default));
        Assert.Equal("{\"apps\": [ {\"key\": \"elden-ring\", ", await File.ReadAllTextAsync(LibraryPath));
        Assert.False(File.Exists(Path.Combine(directory, "elden-ring.guide.json")));

        // Saving a whole new library (the owner's choice) keeps a copy of the unreadable one.
        await store.SaveLibraryAsync(new AppGuideLibrary { On = true }, default);
        Assert.True((await store.LoadLibraryAsync(default)).On);
        Assert.Equal("{\"apps\": [ {\"key\": \"elden-ring\", ",
            await File.ReadAllTextAsync(LibraryPath + FileAppGuideStore.UnreadableSuffix));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"apps\": null}")]
    [InlineData("{\"apps\": [{\"key\": \"Not A Key\", \"name\": \"x\"}]}")]
    [InlineData("{\"apps\": [{\"name\": \"no key\"}]}")]
    [InlineData("{\"formatVersion\": \"one\"}")]
    public async Task MalformedLibrariesAreRefused(string json)
    {
        await File.WriteAllTextAsync(LibraryPath, json);
        await Assert.ThrowsAsync<AppGuideFileException>(() => new FileAppGuideStore(directory).LoadLibraryAsync(default));
    }

    [Fact]
    public async Task FilesFromANewerMartletAreRefusedAndNeverReplaced()
    {
        const string newer = "{\"formatVersion\": 2, \"on\": true, \"apps\": [], \"somethingNew\": 1}";
        await File.WriteAllTextAsync(LibraryPath, newer);
        var store = new FileAppGuideStore(directory);

        Assert.True((await Assert.ThrowsAsync<AppGuideFileException>(() => store.LoadLibraryAsync(default))).IsNewer);
        Assert.True((await Assert.ThrowsAsync<AppGuideFileException>(() => store.SaveLibraryAsync(new(), default))).IsNewer);
        await Assert.ThrowsAsync<AppGuideFileException>(() => store.SaveGuideAsync(Guide("Elden Ring"), default));
        Assert.Equal(newer, await File.ReadAllTextAsync(LibraryPath));

        File.Delete(LibraryPath);
        await File.WriteAllTextAsync(Path.Combine(directory, "elden-ring.guide.json"), "{\"formatVersion\": 3}");
        Assert.True((await Assert.ThrowsAsync<AppGuideFileException>(() => store.LoadGuideAsync("elden-ring", default))).IsNewer);
    }

    [Fact]
    public async Task AGuideFileHoldingAnotherAppsGuideIsRefused()
    {
        var store = new FileAppGuideStore(directory);
        await store.SaveGuideAsync(Guide("Elden Ring"), default);
        File.Copy(Path.Combine(directory, "elden-ring.guide.json"), Path.Combine(directory, "skyrim.guide.json"));
        await Assert.ThrowsAsync<AppGuideFileException>(() => store.LoadGuideAsync("skyrim", default));
    }

    [Fact]
    public async Task TheBoundsAreKept()
    {
        var store = new FileAppGuideStore(directory);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.SaveGuideAsync(Guide("Too Long", characters: FileAppGuideStore.MaximumChunkCharacters + 1), default));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveGuideAsync(Guide("Too Many", chunks: FileAppGuideStore.MaximumChunks + 1, characters: 1), default));

        var apps = Enumerable.Range(0, FileAppGuideStore.MaximumApps).Select(i => new AppGuideEntry { Key = "app-" + i, Name = "App " + i }).ToArray();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.SaveLibraryAsync(new() { Apps = [.. apps, new AppGuideEntry { Key = "one-more", Name = "One more" }] }, default));
        await store.SaveLibraryAsync(new() { Apps = apps }, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveGuideAsync(Guide("One More"), default));
        await store.SaveGuideAsync(Guide("App 7"), default);
        Assert.Equal(FileAppGuideStore.MaximumApps, (await store.LoadLibraryAsync(default)).Apps.Count);

        await using (var big = File.Create(Path.Combine(directory, "huge.guide.json")))
            big.SetLength(FileAppGuideStore.MaximumGuideBytes + 1);
        Assert.Contains("larger than", (await Assert.ThrowsAsync<AppGuideFileException>(() => store.LoadGuideAsync("huge", default))).Message);
        Assert.Empty(Directory.EnumerateFiles(directory, "*.pending"));
    }

    [Fact]
    public async Task AFailedWriteLeavesTheEarlierFile()
    {
        var store = new FileAppGuideStore(directory);
        await store.SaveGuideAsync(Guide("Elden Ring"), default);
        var before = await File.ReadAllTextAsync(LibraryPath);
        File.SetAttributes(LibraryPath, FileAttributes.ReadOnly);

        await Assert.ThrowsAnyAsync<Exception>(() => store.SaveGuideAsync(Guide("Skyrim"), default));
        File.SetAttributes(LibraryPath, FileAttributes.Normal);
        Assert.Equal(before, await File.ReadAllTextAsync(LibraryPath));
        Assert.Single((await store.LoadLibraryAsync(default)).Apps);
        Assert.Empty(Directory.EnumerateFiles(directory, "*.pending"));
    }

    [Fact]
    public async Task StoresOnOneFolderSaveOneAtATime()
    {
        var first = new FileAppGuideStore(directory);
        var second = new FileAppGuideStore(directory + Path.DirectorySeparatorChar);
        var names = Enumerable.Range(0, 16).Select(i => "Game " + i).ToArray();
        await Task.WhenAll(names.SelectMany(name => new[]
        {
            Task.Run(() => first.SaveGuideAsync(Guide(name), default)),
            Task.Run(() => second.SaveGuideAsync(Guide(name, chunks: 3), default)),
            Task.Run(() => first.LoadLibraryAsync(default))
        }));

        var library = await second.LoadLibraryAsync(default);
        Assert.Equal(names.Select(AppGuideKeys.Of).Order(), library.Apps.Select(a => a.Key).Order());
        foreach (var name in names) Assert.NotNull(await first.LoadGuideAsync(AppGuideKeys.Of(name), default));
        Assert.Empty(Directory.EnumerateFiles(directory, "*.pending"));
        Assert.All(Directory.EnumerateFiles(directory, "*.json"), f => Assert.StartsWith("{\"formatVersion\":1,", File.ReadAllText(f, Encoding.UTF8)));
    }
}
