using Martlet.Conversation.Guides;

namespace Martlet.Conversation.Tests;

public sealed class GuideChunkerTests
{
    [Fact]
    public void ShortSectionsJoinANeighbourInsteadOfBeingLost()
    {
        var page = new GuidePage("https://w/Iron_Ore", "Iron Ore", string.Join('\n',
            "# Iron Ore", "Wiki stub.",
            "## Overview", "Iron Ore is a material used to forge weapons and armor at any anvil in the game.",
            "## Price", "7 gold.",
            "## Locations", "Iron Ore can be mined in the Northern Caves and bought from the blacksmith in Riverwood."), 300);
        var chunks = GuideChunker.Chunk(page);

        Assert.Equal(["Iron Ore › Overview", "Iron Ore › Locations"], chunks.Select(c => c.Section));
        Assert.StartsWith("Iron Ore: Wiki stub.\n", chunks[0].Text, StringComparison.Ordinal);
        Assert.EndsWith("\nPrice: 7 gold.", chunks[0].Text, StringComparison.Ordinal);
        Assert.Contains(GuideChunker.Chunk(page).SelectMany(c => c.Text.Split('\n')), l => l.Contains("Northern Caves"));
    }

    [Fact]
    public void ALongSectionIsSplitWithinTheLimitAndTheNextChunkRepeatsTheLastSentence()
    {
        var sentences = Enumerable.Range(1, 40).Select(i => $"Sentence number {i} tells how the river path bends near camp {i}.").ToArray();
        var lines = sentences.Chunk(3).Select(group => string.Join(' ', group));
        var page = new GuidePage("https://w/Path", "River Path", "# River Path\n## Route\n" + string.Join('\n', lines), 3_000);
        var chunks = GuideChunker.Chunk(page);

        Assert.True(chunks.Count >= 2);
        Assert.All(chunks, c => Assert.True(c.Text.Length <= GuideChunker.MaximumCharacters, $"{c.Text.Length} characters"));
        Assert.All(chunks, c => Assert.Equal("River Path › Route", c.Section));
        Assert.All(sentences, s => Assert.Contains(chunks, c => c.Text.Contains(s, StringComparison.Ordinal)));
        for (var i = 1; i < chunks.Count; i++)
        {
            var previousLast = chunks[i - 1].Text.Split('\n')[^1].Split(". ")[^1];
            Assert.StartsWith(previousLast, chunks[i].Text, StringComparison.Ordinal);
            Assert.True(previousLast.Length <= GuideChunker.OverlapCharacters);
        }
    }

    [Fact]
    public void TableRowsStayWholeAndAContinuedTableRepeatsItsHeader()
    {
        const string header = "| Item | Location | Price |";
        var rows = Enumerable.Range(1, 60).Select(i => $"| Gem {i} | Crystal cave {i} | {i * 10} gold |").ToArray();
        var page = new GuidePage("https://w/Gems", "Gems", "# Gems\n## Prices\n" + header + "\n|---|:---:|---|\n" + string.Join('\n', rows), 3_000);
        var chunks = GuideChunker.Chunk(page);

        Assert.True(chunks.Count >= 2);
        Assert.All(chunks, c => Assert.Equal(header, c.Text.Split('\n')[0]));
        Assert.DoesNotContain(chunks, c => c.Text.Contains("---", StringComparison.Ordinal));
        var lines = chunks.SelectMany(c => c.Text.Split('\n')).Where(l => l != header).ToArray();
        Assert.Equal(rows, lines);
    }

    [Fact]
    public void ABlockLongerThanAChunkIsCutAtSentenceEndsAndEveryLimitHolds()
    {
        var text = string.Concat(Enumerable.Range(1, 80).Select(i => $"Step {i} of the long walkthrough says to climb the tower. "));
        var page = new GuidePage("https://w/Walkthrough", "Walkthrough", "# Walkthrough\n" + text, 6_000);
        foreach (var maximum in new[] { GuideChunker.MaximumCharacters, 300 })
        {
            var chunks = GuideChunker.Chunk(page, maximum);
            Assert.All(chunks, c => Assert.True(c.Text.Length <= maximum, $"{c.Text.Length} > {maximum}"));
            Assert.All(chunks, c => Assert.EndsWith(".", c.Text, StringComparison.Ordinal));
            Assert.Contains(chunks, c => c.Text.Contains("Step 80 of", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void ListItemsAreNeverCut()
    {
        var items = Enumerable.Range(1, 80).Select(i => $"- Quest {i}: talk to the keeper of lighthouse {i}").ToArray();
        var page = new GuidePage("https://w/Quests", "Quests", "# Quests\n" + string.Join('\n', items), 4_000);
        var lines = GuideChunker.Chunk(page).SelectMany(c => c.Text.Split('\n')).ToHashSet();
        Assert.All(items, item => Assert.Contains(item, lines));
    }
}
