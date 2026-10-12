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
        Assert.Equal(["日本", "本語", "2077"], SearchTerms.Of("日本語 2077"));
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
        Assert.Equal(["ガチ", "チャ"], SearchTerms.Of("ガチャ"));
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

    [Fact]
    public void ScriptsWithoutSpacesArePairsOfCharacters()
    {
        Assert.Equal(["東京", "京大", "大学"], SearchTerms.Of("東京大学"));
        // Japanese: kanji, hiragana and katakana are read apart, and particles are grammar words.
        Assert.Equal(["猫", "好", "ポケ", "ケモ", "モン"], SearchTerms.Of("猫が好き。ポケモン"));
        // Chinese grammar characters end a pair: "我的猫" is "猫", not "我的" and "的猫".
        Assert.Equal(["猫"], SearchTerms.Of("我的猫"));
        Assert.Equal(["고양", "양이"], SearchTerms.Of("고양이를").Take(2));
        Assert.Equal([new SearchWord("東京", 0, 2), new SearchWord("京大", 1, 2), new SearchWord("大学", 2, 2)], SearchTerms.Locate("東京大学"));
    }

    [Fact]
    public void CombiningMarksAndFullWidthLettersBelongToTheirWord()
    {
        Assert.Equal(["naive", "cafe"], SearchTerms.Words("nai\u0308ve cafe\u0301"));
        Assert.Equal(["abc123"], SearchTerms.Words("ＡＢＣ１２３"));
        Assert.Equal(["हिन्दी"], SearchTerms.Words("हिन्दी"));
    }

    [Theory]
    [InlineData("¿Te acuerdas de lo que hablamos sobre mi perro?", SearchLanguage.Spanish)]
    [InlineData("Tu te souviens de mon chat ?", SearchLanguage.French)]
    [InlineData("Erinnerst du dich an meine Katze?", SearchLanguage.German)]
    [InlineData("Você lembra do que eu disse sobre a guitarra?", SearchLanguage.Portuguese)]
    [InlineData("Ti ricordi il mio gatto?", SearchLanguage.Italian)]
    [InlineData("Where do I find the iron ore?", SearchLanguage.English)]
    [InlineData("Download it from https://developer.android.com/develop/os and the docs at example.de/la", SearchLanguage.English)]
    [InlineData("AI Plus Pro", SearchLanguage.English)]
    [InlineData("Me gusta", SearchLanguage.English)]
    public void ALanguageNeedsTwoOfItsOwnGrammarWords(string text, SearchLanguage language) =>
        Assert.Equal(language, SearchTerms.LanguageOf(SearchTerms.Words(text)));

    [Fact]
    public void TextInAnotherLanguageLeavesOutItsGrammarWordsAndAddsItsStems()
    {
        Assert.Equal(["perro", "gata", "hermano", "perr", "herman"], SearchTerms.Of("El perro y la gata de mi hermano"));
        var italian = SearchTerms.Of("Ho visto due gatti nel giardino");
        Assert.Contains("gatti", italian);
        Assert.Contains("gatt", italian);
        Assert.Contains(SearchTerms.Stem("gatto", SearchLanguage.Italian), SearchTerms.Of("Il mio gatto dorme sul divano"));
        // English text gets exactly the English terms.
        Assert.Equal(["sword", "gatti"], SearchTerms.Of("The swords and the gatti"));
    }

    [Theory]
    [InlineData("perros", "perra", SearchLanguage.Spanish)]
    [InlineData("ciudades", "ciudad", SearchLanguage.Spanish)]
    [InlineData("luces", "luz", SearchLanguage.Spanish)]
    [InlineData("coracoes", "coracao", SearchLanguage.Portuguese)]
    [InlineData("animais", "animal", SearchLanguage.Portuguese)]
    [InlineData("homens", "homem", SearchLanguage.Portuguese)]
    [InlineData("chevaux", "cheval", SearchLanguage.French)]
    [InlineData("dernieres", "dernier", SearchLanguage.French)]
    [InlineData("mangee", "manger", SearchLanguage.French)]
    [InlineData("drachen", "drache", SearchLanguage.German)]
    [InlineData("kindern", "kind", SearchLanguage.German)]
    [InlineData("hauser", "haus", SearchLanguage.German)]
    [InlineData("strassen", "straße", SearchLanguage.German)]
    [InlineData("gatti", "gatto", SearchLanguage.Italian)]
    [InlineData("amiche", "amico", SearchLanguage.Italian)]
    [InlineData("valigie", "valigia", SearchLanguage.Italian)]
    public void LightStemsOfOtherLanguagesJoinWordForms(string form, string word, SearchLanguage language) =>
        Assert.Equal(SearchTerms.Stem(word, language), SearchTerms.Stem(form, language));
}
