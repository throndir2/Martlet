using Martlet.Core.Search;
using Martlet.Core.Settings;
using Martlet.Core.Sync;

namespace Martlet.Core.Tests;

/// <summary>Finding by meaning's shared parts (docs/SEARCH.md, "Finding by meaning"): the vector sets (search, meaning, changed and
/// deleted documents, the file), filling them in batches, the fixture embedder and the shared choice (meaning-search.json).</summary>
public sealed class MeaningSearchTests
{
    private static async Task<VectorSet> Facts(params string[] texts)
    {
        var embedder = new FixtureEmbedder();
        var set = new VectorSet(embedder.Model);
        await set.FillAsync(embedder, [.. texts.Select((text, i) => (i.ToString(), text))], null, CancellationToken.None);
        return set;
    }

    [Fact]
    public async Task TheNearestDocumentInMeaningComesFirstWithoutSharingAWord()
    {
        var set = await Facts("The user is vegetarian.", "The user's dog is called Rex.", "The user lives in Seattle.",
            "The user plays the guitar.", "The user works as a nurse.", "The user likes green.", "The user has a red bicycle.",
            "The user wears glasses.", "The user reads mystery novels.");
        var near = set.Nearest(FixtureEmbedder.Embed("What should I cook tonight?"), 3);
        Assert.Equal("0", near[0].Id);
        Assert.True(near[0].Meaning > 0.5 && near[1].Meaning < 0.2, string.Join(", ", near.Select(n => $"{n.Id} {n.Meaning:0.00}")));
        Assert.Equal(near.OrderByDescending(n => n.Similarity).Select(n => n.Id), near.Select(n => n.Id));
        // A document can be left out, and one compared with its neighbors is never listed.
        Assert.DoesNotContain(set.Nearest(set.Vector("0")!, 9, except: "0"), n => n.Id == "0");
        Assert.DoesNotContain(set.Nearest(FixtureEmbedder.Embed("cook"), 9, include: id => id != "0"), n => n.Id == "0");
        // A query of another length finds nothing.
        Assert.Empty(set.Nearest([1f, 2f], 3));
    }

    [Fact]
    public async Task AChangedTextIsEmbeddedAgainAndADeletedOneIsDropped()
    {
        var embedder = new FixtureEmbedder { MaximumBatch = 2 };
        var set = new VectorSet(embedder.Model);
        var items = new[] { ("a", "The user is vegetarian."), ("b", "The user's dog is called Rex."), ("c", "The user lives in Seattle.") };
        Assert.Equal(3, await set.FillAsync(embedder, items, null, CancellationToken.None));
        Assert.Equal((2, 3), (embedder.Requests, embedder.Embedded));
        Assert.Equal(0, await set.FillAsync(embedder, items, null, CancellationToken.None));
        Assert.True(set.Has("a", "The user is vegetarian."));
        var changed = set.Missing([("a", "The user is vegan."), ("b", "The user's dog is called Rex.")]);
        Assert.Equal(["a"], changed.Select(c => c.Id));
        Assert.Equal(1, set.Keep(new HashSet<string> { "a", "b" }));
        Assert.Null(set.Vector("c"));
        Assert.Throws<ArgumentException>(() => set.Put("d", "x", [1f, 2f]));
        Assert.Throws<ArgumentException>(() => set.Put("d", "x", new float[set.Dimensions]));
        set.Clear();
        Assert.Equal((0, 0), (set.Count, set.Dimensions));
    }

    [Fact]
    public async Task FillingWaitsWhileHeldAndStopsWhenCanceled()
    {
        var embedder = new FixtureEmbedder { MaximumBatch = 1 };
        var set = new VectorSet(embedder.Model);
        var held = 2;
        var done = await set.FillAsync(embedder, [("a", "one"), ("b", "two")], () => Interlocked.Decrement(ref held) >= 0, CancellationToken.None);
        Assert.Equal(2, done);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => set.FillAsync(embedder, [("c", "three")], null, cancel.Token));
        await Assert.ThrowsAsync<ArgumentException>(() => new VectorSet("other").FillAsync(embedder, [("a", "one")], null, CancellationToken.None));
    }

    [Fact]
    public async Task ASetIsSavedAndReadBackOnlyForItsOwnModel()
    {
        var folder = Path.Combine(Path.GetTempPath(), "martlet-vectors-" + Guid.NewGuid().ToString("N"));
        try
        {
            var set = await Facts("The user is vegetarian.", "The user's dog is called Rex.");
            Assert.True(set.Changed);
            var path = Path.Combine(folder, "guide.vectors");
            set.Save(path);
            Assert.False(set.Changed);
            var loaded = VectorSet.Load(path, set.Model);
            Assert.Equal((2, set.Dimensions), (loaded.Count, loaded.Dimensions));
            Assert.Equal(set.Vector("1"), loaded.Vector("1"));
            Assert.True(loaded.Has("0", "The user is vegetarian."));
            Assert.Equal(0, VectorSet.Load(path, "ollama:another").Count);
            Assert.Equal(0, VectorSet.Load(Path.Combine(folder, "none.vectors"), set.Model).Count);
            File.WriteAllBytes(path, [1, 2, 3]);
            Assert.Equal(0, VectorSet.Load(path, set.Model).Count);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void MeaningIsRelativeToTheSetsAverageSoScalesDoNotMatter()
    {
        // Two models whose similarities have different scales give the same meaning to the same order.
        foreach (var offset in new[] { 0f, 3f })
        {
            var set = new VectorSet("scale");
            for (var i = 0; i < 10; i++)
            {
                var vector = new float[10];
                vector[i] = 1;
                vector[0] += offset;
                set.Put(i.ToString(), "", vector);
            }
            var query = new float[10];
            query[1] = 1;
            query[0] = offset;
            var near = set.Nearest(query, 2);
            Assert.Equal("1", near[0].Id);
            Assert.True(near[0].Meaning > 0.5, $"{offset}: {near[0].Meaning}");
        }
    }

    [Fact]
    public async Task TheChoiceIsSharedAndValidated()
    {
        var folder = Path.Combine(Path.GetTempPath(), "martlet-meaning-" + Guid.NewGuid().ToString("N"));
        try
        {
            var choice = MeaningSearchFile.Load(folder);
            Assert.Equal((MeaningSearchFile.ThisPc, MeaningSearchFile.DefaultModel, true), (choice.Place, choice.Model, choice.On));
            Assert.Null(MeaningSearchFile.Parse("""{"schema_version":2,"place":"this-pc","model":"x"}"""));
            Assert.Null(MeaningSearchFile.Parse("""{"schema_version":1,"place":"cloud","model":"x"}"""));
            Assert.Null(MeaningSearchFile.Parse("""{"schema_version":1,"place":"off","model":"bad name; rm"}"""));
            Assert.True(MeaningSearchFile.IsModelName("embeddinggemma:300m"));

            var section = MeaningSearchFile.Section(folder);
            var local = await section.ReadAsync(CancellationToken.None);
            Assert.True(local!.IsDefault);
            Assert.Equal(new MeaningSearchFile().Write(), section.Default);
            var followed = 0;
            section = MeaningSearchFile.Section(folder, () => followed++);
            var off = new MeaningSearchFile { Place = MeaningSearchFile.Off, Model = "embeddinggemma" };
            var setting = SharedSettings.Empty.Put(MeaningSearchFile.SharedKey, off.Write(), null, "other-pc", DateTimeOffset.UtcNow)
                .Find(MeaningSearchFile.SharedKey)!;
            Assert.True((await section.ApplyAsync(setting, null, CancellationToken.None)).Applied);
            Assert.Equal((off, 1), (MeaningSearchFile.Load(folder), followed));
            Assert.False((await section.ReadAsync(CancellationToken.None))!.IsDefault);
            var newer = SharedSettings.Empty.Put(MeaningSearchFile.SharedKey, """{"schema_version":9}""", null, "other-pc", DateTimeOffset.UtcNow)
                .Find(MeaningSearchFile.SharedKey)!;
            Assert.False((await section.ApplyAsync(newer, null, CancellationToken.None)).Applied);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }
}
