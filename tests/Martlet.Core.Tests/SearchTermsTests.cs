using Martlet.Core.Text;

namespace Martlet.Core.Tests;

public sealed class SearchTermsTests
{
    [Fact]
    public void TermsAreLowerCaseStemmedAndLeaveOutGrammarWords()
    {
        Assert.Equal(["find", "iron", "ore", "player", "sword"], SearchTerms.Of("Where do I find the Iron ORE? The player's swords!"));
    }

    [Theory]
    [InlineData("berries", "berry")]
    [InlineData("boxes", "box")]
    [InlineData("matches", "match")]
    [InlineData("bosses", "boss")]
    [InlineData("boss", "boss")]
    [InlineData("running", "run")]
    [InlineData("crafting", "craft")]
    [InlineData("spawned", "spawn")]
    [InlineData("needed", "need")]
    [InlineData("feed", "feed")]
    [InlineData("bonus", "bonus")]
    [InlineData("gas", "gas")]
    public void StemsAreLight(string word, string stem) => Assert.Equal(stem, SearchTerms.Stem(word));

    [Fact]
    public void FormsOfAWordShareAStem()
    {
        Assert.Equal(SearchTerms.Stem("make"), SearchTerms.Stem("making"));
        Assert.Equal(SearchTerms.Stem("fire"), SearchTerms.Stem("fires"));
        Assert.Equal(SearchTerms.Stem("speed"), SearchTerms.Stem("speeds"));
    }

    [Fact]
    public void OverlongWordsAndOtherScriptsAreHandled()
    {
        Assert.Equal(["ok"], SearchTerms.Of(new string('x', 60) + " ok"));
        Assert.Equal(["日本語", "2077"], SearchTerms.Of("日本語 2077"));
        Assert.Equal(["sword", "shield"], SearchTerms.Distinct("sword shield swords", 5));
    }
}
