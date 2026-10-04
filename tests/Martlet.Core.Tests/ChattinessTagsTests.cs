using Martlet.Core.Settings;

namespace Martlet.Core.Tests;

public sealed class ChattinessTagsTests
{
    [Theory]
    [InlineData("[chattiness:quiet]", Chattiness.Quiet)]
    [InlineData("[Chattiness: Chatty]", Chattiness.Chatty)]
    [InlineData("[CHATTINESS:NORMAL]", Chattiness.Normal)]
    [InlineData("[chattiness:loud]", null)]
    [InlineData("[quiet]", null)]
    [InlineData(null, null)]
    public void A_tag_names_its_level_in_either_spelling(string? tag, Chattiness? level) =>
        Assert.Equal(level, ChattinessTags.Of(tag));

    [Fact]
    public void The_last_tag_wins_and_the_choice_resolves_to_a_level()
    {
        Assert.Equal(Chattiness.Chatty, ChattinessTags.Last(["[chattiness:quiet]", "[blush]", "[chattiness: chatty]"]));
        Assert.Null(ChattinessTags.Last([]));
        Assert.Equal(6, ChattinessTags.All.Count);
        Assert.All(ChattinessTags.All, tag => Assert.NotNull(ChattinessTags.Of(tag)));
        Assert.Equal(ChattinessChoice.MartletDecides, ChattinessTags.Choice(3));
        Assert.Equal(ChattinessChoice.Normal, ChattinessTags.Choice(4));
        Assert.Equal(Chattiness.Quiet, ChattinessTags.Level(ChattinessChoice.MartletDecides, Chattiness.Quiet));
        Assert.Equal(Chattiness.Chatty, ChattinessTags.Level(ChattinessChoice.Chatty, Chattiness.Quiet));
        Assert.Equal("Martlet decides", ChattinessTags.Label(ChattinessChoice.MartletDecides));
    }

    [Fact]
    public void Martlet_decides_tells_the_tags_and_follows_prompt_edits()
    {
        var told = ChattinessTags.Instructions(null, "pass")!;
        Assert.Contains("[chattiness:quiet], [chattiness:normal] or [chattiness:chatty]", told);
        Assert.Contains("at the very end of your reply", told);
        Assert.Contains("[pass]", told);
        Assert.DoesNotContain("{", told);
        Assert.Equal("Your chattiness right now: quiet.", ChattinessTags.Note(null, Chattiness.Quiet));
        var edited = new PromptSettings
        {
            Overrides = new Dictionary<string, string>
            {
                [PromptCatalog.ChattinessDecides] = "Switch with {quiet} or {chatty}.",
                [PromptCatalog.ChattinessNow] = ""
            }
        };
        edited.Validate();
        Assert.Equal("Switch with [chattiness:quiet] or [chattiness:chatty].", ChattinessTags.Instructions(edited, "pass"));
        Assert.Null(ChattinessTags.Note(edited, Chattiness.Normal));
    }
}
