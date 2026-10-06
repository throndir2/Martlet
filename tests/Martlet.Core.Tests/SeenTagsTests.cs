using Martlet.Core.Settings;

namespace Martlet.Core.Tests;

public sealed class SeenTagsTests
{
    [Fact]
    public void The_last_seen_tag_gives_the_description_tidied()
    {
        Assert.Equal("a racing game, last lap", SeenTags.Description(["[seen: a game]", "[chattiness:quiet]", "[Seen:  a racing\tgame, last lap. ]"]));
        Assert.Null(SeenTags.Description(["[chattiness:quiet]"]));
        Assert.Null(SeenTags.Description(["[seen:   ]"]));
        Assert.Null(SeenTags.Description([]));
    }

    [Fact]
    public void A_description_is_one_bounded_line_without_brackets()
    {
        Assert.Equal("a menu x y", SeenTags.Clean("  a menu {x}\n[y]  "));
        var cleaned = SeenTags.Clean(string.Join(' ', Enumerable.Repeat("word", 60)))!;
        Assert.True(cleaned.Length <= SeenTags.MaximumDescription + 1);
        Assert.EndsWith("word…", cleaned);
        Assert.Null(SeenTags.Clean(" [] . "));
    }

    [Theory]
    [InlineData("Nice! [seen: a game] More.", "Nice! More.")]
    [InlineData("[pass] [seen: a code editor]", "[pass]")]
    [InlineData("No tag here.", "No tag here.")]
    [InlineData("Broken [seen: a\nb] tag", "Broken [seen: a\nb] tag")]
    [InlineData("Two [seen: a] and [SEEN: b]", "Two and")]
    public void Without_removes_only_whole_seen_tags(string reply, string kept) => Assert.Equal(kept, SeenTags.Without(reply));

    [Fact]
    public void The_prompt_is_in_the_catalog_never_changes_and_can_be_emptied()
    {
        var text = SeenTags.Instructions(null, "pass")!;
        Assert.Contains("[seen:", text);
        Assert.Contains("[pass]", text);
        Assert.Equal(text, SeenTags.Instructions(null, "pass"));
        Assert.Equal(PromptCatalog.VisionGroup, PromptCatalog.All.Single(p => p.Id == PromptCatalog.SeenTag).Group);
        Assert.Null(SeenTags.Instructions(new PromptSettings { Overrides = new Dictionary<string, string> { [PromptCatalog.SeenTag] = "" } }, "pass"));
        Assert.True(VoiceTags.IsOpen(SeenTags.Tag));
        Assert.False(VoiceTags.IsOpen("[chattiness:quiet]"));
    }
}
