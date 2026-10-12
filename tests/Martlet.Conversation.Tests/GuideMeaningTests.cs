using Martlet.Conversation.Guides;
using Martlet.Core.Search;

namespace Martlet.Conversation.Tests;

/// <summary>search_guide finding by meaning (docs/SEARCH.md, "Finding by meaning"): sections an embedding search found are fused with
/// the guide's lexical order through the search engine; a section's relevance is at least its meaning. The embeddings are a fixture
/// (NOT AI).</summary>
public sealed class GuideMeaningTests
{
    private static readonly GuideChunk[] Chunks =
    [
        new("https://wiki.example/Blacksmith", "Blacksmith", "Blacksmith › Upgrades", "Bring ore to Hilda at the forge to raise a sword's damage by one level."),
        new("https://wiki.example/Fishing", "Fishing", "Fishing › Spots", "Cast your line at dawn near the pier for the best catch."),
        new("https://wiki.example/Farming", "Farming", "Farming › Crops", "Plant turnips in spring and water them every morning."),
        new("https://wiki.example/Mines", "Mines", "Mines › Floors", "Every fifth floor of the mines has a ladder down and a chest."),
        new("https://wiki.example/Town", "Town", "Town › Shops", "The general store sells seeds and opens at nine.")
    ];

    private static (int, double)[] Near(string question)
    {
        var set = new VectorSet("fixture:concepts");
        for (var i = 0; i < Chunks.Length; i++) set.Put(i.ToString(), Chunks[i].Text, FixtureEmbedder.Embed(Chunks[i].Section + "\n" + Chunks[i].Text));
        return [.. set.Nearest(FixtureEmbedder.Embed(question), 20).Where(n => n.Meaning >= 0.2).Select(n => (int.Parse(n.Id), n.Meaning))];
    }

    [Fact]
    public void AQuestionInOtherWordsFindsTheSectionByMeaning()
    {
        var index = GuideIndex.Build(Chunks);
        const string question = "How do I make my weapons stronger?";
        Assert.Empty(index.Search(question, 3));
        var near = Near(question);
        var hits = index.Search(question, 3, near);
        Assert.Equal("Blacksmith", hits[0].Chunk.Page);
        Assert.Equal(near[0].Item2, hits[0].Relevance, 6);
        // Without vectors it is the same search as before, and the chunks are there to embed.
        Assert.Equal(index.Search("fishing pier", 3).Select(h => h.Chunk), index.Search("fishing pier", 3, null).Select(h => h.Chunk));
        Assert.Equal(Chunks, index.Chunks);
        // A section found by its words keeps its own relevance when it is higher.
        var both = index.Search("blacksmith forge sword damage", 3, near);
        Assert.Equal("Blacksmith", both[0].Chunk.Page);
        Assert.True(both[0].Relevance >= near[0].Item2);
        // Numbers outside the guide are left out.
        Assert.Empty(index.Search(question, 3, [(99, 0.9), (-1, 0.9)]));
    }
}
