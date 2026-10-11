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

    [Theory]
    [InlineData("found", "find")]
    [InlineData("bought", "buy")]
    [InlineData("knives", "knife")]
    [InlineData("wolves", "wolf")]
    [InlineData("made", "making")]
    [InlineData("used", "use")]
    [InlineData("using", "uses")]
    [InlineData("died", "die")]
    [InlineData("tried", "try")]
    [InlineData("manually", "manual")]
    [InlineData("automatically", "automatic")]
    [InlineData("adding", "add")]
    [InlineData("added", "add")]
    [InlineData("running", "run")]
    public void IrregularAndDerivedFormsShareAStem(string form, string word) => Assert.Equal(SearchTerms.Stem(word), SearchTerms.Stem(form));

    [Fact]
    public void AccentsOnLatinLettersAreLeftOutAndOtherScriptsKeepTheirs()
    {
        Assert.Equal(SearchTerms.Of("pokemon okami"), SearchTerms.Of("Pokémon Ōkami"));
        Assert.Equal(["ガチャ"], SearchTerms.Of("ガチャ"));
    }

    [Fact]
    public void ContractionsWithOrWithoutTheApostropheAreGrammarWords()
    {
        Assert.Equal(SearchTerms.Of("worry"), SearchTerms.Of("don't worry"));
        Assert.Equal(SearchTerms.Of("worry"), SearchTerms.Of("dont worry, im here while it is"));
        Assert.Empty(SearchTerms.Of("I'm here and you're there, it's that"));
    }

    [Fact]
    public void WordsKeepGrammarWordsAndTermMakesEachOnesTerm()
    {
        Assert.Equal(["the", "player", "swords"], SearchTerms.Words("The player's Swords!"));
        Assert.Null(SearchTerms.Term("the"));
        Assert.Equal("sword", SearchTerms.Term("swords"));
        Assert.Equal(SearchTerms.Of("The player's Swords!"), SearchTerms.Words("The player's Swords!").Select(SearchTerms.Term).OfType<string>());
    }
}
