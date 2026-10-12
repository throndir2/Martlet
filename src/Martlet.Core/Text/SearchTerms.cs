using System.Buffers;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Martlet.Core.Text;

/// <summary>A language whose grammar words and word endings <see cref="SearchTerms"/> knows besides English. A text gets one
/// (<see cref="SearchTerms.LanguageOf"/>) when its grammar words show it clearly; any other text, also one in a language not
/// listed here, is read with the English rules alone.</summary>
public enum SearchLanguage { English, Spanish, French, German, Portuguese, Italian }

/// <summary>One word of a text as <see cref="SearchTerms.Words"/> gives it, with the place in the text it was read from.</summary>
public readonly record struct SearchWord(string Word, int Start, int Length);

/// <summary>The words of a text as lexical search compares them (app guides, past conversations, memory, lorebook word forms):
/// runs of Unicode letters and digits (with their combining marks), in lower case, with accents on Latin letters left out
/// ("Pokémon" is "pokemon") and a possessive "'s" left out. Scripts written without spaces (Chinese, Japanese, Korean, Thai, Lao,
/// Khmer, Myanmar) have no word boundaries to read, so each pair of neighboring characters is a word ("東京都" is "東京" and
/// "京都") and a character alone is a word too; Chinese grammar characters (的, 了, 我...) end such a pair. Common grammar words
/// (<see cref="IsStopWord(string)"/>) are left out and each word is reduced to a light English stem (<see cref="Stem(string)"/>),
/// so "swords" finds "sword", "crafting" finds "craft" and "found" finds "find". A text that is clearly Spanish, French,
/// German, Portuguese or Italian (<see cref="LanguageOf"/>) also leaves out that language's grammar words and adds each word's
/// stem by that language's light rules (<see cref="Stem(string, SearchLanguage)"/>: "gatti" and "gatto", "Drachen" and "Drache"
/// find each other). English text gets exactly the terms it always got. Words longer than <see cref="MaximumTermLength"/> are
/// left out. Pure and thread-safe.</summary>
public static class SearchTerms
{
    public const int MaximumTermLength = 40;

    // Which languages a grammar word belongs to: one bit per SearchLanguage, and Always for grammar words of the scripts written
    // without spaces, which no Latin word can be, so they are left out of every text and tell nothing about its language.
    private const int English = 1, Always = 1 << 6, Silent = 1 << 7;
    private static readonly Dictionary<string, int> Grammar = GrammarWords();

    // Irregular forms a suffix rule can't reach, mapped to the word whose stem they share ("found" → "find", "knives" → "knife").
    private static readonly Dictionary<string, string> Irregular = new(StringComparer.Ordinal)
    {
        ["found"] = "find", ["got"] = "get", ["gotten"] = "get", ["bought"] = "buy", ["made"] = "make", ["ran"] = "run",
        ["fought"] = "fight", ["caught"] = "catch", ["taught"] = "teach", ["brought"] = "bring", ["thought"] = "think",
        ["kept"] = "keep", ["sold"] = "sell", ["told"] = "tell", ["held"] = "hold", ["built"] = "build", ["sent"] = "send",
        ["spent"] = "spend", ["won"] = "win", ["lost"] = "lose", ["stole"] = "steal", ["stolen"] = "steal", ["broke"] = "break",
        ["broken"] = "break", ["chose"] = "choose", ["chosen"] = "choose", ["wrote"] = "write", ["written"] = "write",
        ["rode"] = "ride", ["ridden"] = "ride", ["ate"] = "eat", ["eaten"] = "eat", ["took"] = "take", ["taken"] = "take",
        ["gave"] = "give", ["given"] = "give", ["seen"] = "see", ["drew"] = "draw", ["drawn"] = "draw", ["flew"] = "fly",
        ["flown"] = "fly", ["grew"] = "grow", ["grown"] = "grow", ["threw"] = "throw", ["thrown"] = "throw", ["knew"] = "know",
        ["known"] = "know", ["began"] = "begin", ["begun"] = "begin", ["swam"] = "swim", ["dug"] = "dig", ["hid"] = "hide",
        ["hidden"] = "hide", ["beaten"] = "beat", ["slain"] = "slay", ["slew"] = "slay", ["forgot"] = "forget",
        ["forgotten"] = "forget", ["froze"] = "freeze", ["frozen"] = "freeze", ["shot"] = "shoot", ["struck"] = "strike",
        ["stuck"] = "stick", ["went"] = "go", ["gone"] = "go", ["goes"] = "go", ["going"] = "go", ["used"] = "use",
        ["using"] = "use", ["died"] = "die", ["dying"] = "die", ["lying"] = "lie", ["tying"] = "tie", ["fed"] = "feed",
        ["led"] = "lead", ["met"] = "meet", ["paid"] = "pay", ["said"] = "say", ["laid"] = "lay", ["sought"] = "seek",
        ["knives"] = "knife", ["wolves"] = "wolf", ["elves"] = "elf", ["dwarves"] = "dwarf", ["thieves"] = "thief",
        ["leaves"] = "leaf", ["lives"] = "life", ["halves"] = "half", ["shelves"] = "shelf", ["loaves"] = "loaf",
        ["calves"] = "calf", ["scarves"] = "scarf", ["hooves"] = "hoof", ["wives"] = "wife", ["staves"] = "staff",
        ["men"] = "man", ["women"] = "woman", ["children"] = "child", ["mice"] = "mouse", ["geese"] = "goose",
        ["feet"] = "foot", ["teeth"] = "tooth", ["oxen"] = "ox"
    };

    /// <summary>The search terms of <paramref name="text"/>: the English stem of each word that is not a grammar word, in order,
    /// repeats kept (term frequency counts them); for a text in another language (<see cref="LanguageOf"/>), then each word's
    /// stem by that language's rules where it differs, in order.</summary>
    public static IReadOnlyList<string> Of(string? text)
    {
        var list = new List<string>();
        if (string.IsNullOrEmpty(text)) return list;
        Split(text, list, null);
        var words = CollectionsMarshal.AsSpan(list);
        var terms = new List<string>(words.Length);
        Span<int> votes = stackalloc int[Languages * 4];
        votes[Languages..(2 * Languages)].Fill(-1);
        var foreign = false;
        // English terms in one pass; a text in another language (few) is read again below.
        for (var i = 0; i < words.Length; i++)
        {
            var mask = Mask(words[i]);
            if (mask != 0)
            {
                foreign |= Vote(votes, words, i, mask);
                if ((mask & (English | Always)) != 0) continue;
            }
            terms.Add(Stem(words[i]));
        }
        if (!foreign || Decide(votes, words.Length) is not { } language) return terms;
        terms.Clear();
        List<string>? forms = null;
        var leftOut = English | Always | Bit(language);
        foreach (var word in words)
        {
            if ((Mask(word) & leftOut) != 0) continue;
            var stem = Stem(word);
            terms.Add(stem);
            if (Stem(word, language) is var form && form != stem) (forms ??= []).Add(form);
        }
        if (forms is not null) terms.AddRange(forms);
        return terms;
    }
    /// <summary>The words of <paramref name="text"/> in order before grammar words are left out and before stemming: runs of
    /// letters and digits in lower case, accents on Latin letters left out, a possessive "'s" left out, words longer than
    /// <see cref="MaximumTermLength"/> left out, and in scripts written without spaces each pair of neighboring characters.
    /// <see cref="Term"/> makes a word's search term.</summary>
    public static IReadOnlyList<string> Words(string? text)
    {
        var words = new List<string>();
        if (!string.IsNullOrEmpty(text)) Split(text, words, null);
        return words;
    }

    /// <summary>The words of <paramref name="text"/> as <see cref="Words"/> gives them, each with the place in the text it was
    /// read from (a pair of characters: both).</summary>
    public static IReadOnlyList<SearchWord> Locate(string? text)
    {
        var located = new List<SearchWord>();
        if (!string.IsNullOrEmpty(text)) Split(text, null, located);
        return located;
    }

    /// <summary>The search term of one word from <see cref="Words"/>: its English stem (<see cref="Stem(string)"/>), or null for
    /// a grammar word (<see cref="IsStopWord(string)"/>).</summary>
    public static string? Term(string word) => IsStopWord(word) ? null : Stem(word);

    /// <summary>The distinct search terms of <paramref name="text"/>, at most <paramref name="maximum"/>, first seen first.</summary>
    public static IReadOnlyList<string> Distinct(string? text, int maximum = 32)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var distinct = new List<string>();
        foreach (var term in Of(text))
        {
            if (distinct.Count >= maximum) break;
            if (seen.Add(term)) distinct.Add(term);
        }
        return distinct;
    }

    /// <summary>Whether <paramref name="word"/> (lower case) is a common English grammar word, or a grammar word of a script
    /// written without spaces, that search leaves out of every text.</summary>
    public static bool IsStopWord(string word) => (Grammar.GetValueOrDefault(word) & (English | Always)) != 0;

    /// <summary>Whether search leaves <paramref name="word"/> (lower case) out of a text in <paramref name="language"/>: a grammar
    /// word of that language, or one <see cref="IsStopWord(string)"/> leaves out of every text.</summary>
    public static bool IsStopWord(string word, SearchLanguage language) =>
        (Grammar.GetValueOrDefault(word) & (English | Always | Bit(language))) != 0;

    /// <summary>The language <paramref name="words"/> (from <see cref="Words"/>) are in: the one other than English with the most
    /// of its grammar words among them, when that is at least two and more than English has; otherwise English.</summary>
    public static SearchLanguage LanguageOf(IReadOnlyList<string> words)
    {
        ArgumentNullException.ThrowIfNull(words);
        var array = words as string[] ?? [.. words];
        Span<int> votes = stackalloc int[Languages * 4];
        votes[Languages..(2 * Languages)].Fill(-1);
        var foreign = false;
        for (var i = 0; i < array.Length; i++)
            if (Mask(array[i]) is var mask and not 0) foreign |= Vote(votes, array, i, mask);
        return foreign && Decide(votes, array.Length) is { } language ? language : SearchLanguage.English;
    }
    /// <summary>A light English stem of <paramref name="word"/> (lower case): irregular forms first ("found" → "find", "knives"
    /// → "knife", "made" → "make"), then plurals ("berries" → "berry", "boxes" → "box", "swords" → "sword"), then "-ing", "-ied",
    /// "-ed" and "-ly" with a doubled last letter made single ("running" → "run", "tried" → "try", "manually" → "manual"), then a
    /// last "e" ("make" and "making" → "mak"). Short words, numbers and words in other scripts stay as they are. Stems need not be
    /// words: both sides of a comparison are stemmed the same way.</summary>
    public static string Stem(string word)
    {
        if (Irregular.TryGetValue(word, out var regular)) word = regular;
        if (word.Length <= 3 || !IsAsciiLetters(word)) return word;
        var w = word;
        if (w.EndsWith("ies", StringComparison.Ordinal) && w.Length > 4) w = w[..^3] + "y";
        else if (w.EndsWith("sses", StringComparison.Ordinal)) w = w[..^2];
        else if (w.EndsWith("es", StringComparison.Ordinal) && w.Length > 4 &&
            (w[^3] is 's' or 'x' or 'z' || w.EndsWith("ches", StringComparison.Ordinal) || w.EndsWith("shes", StringComparison.Ordinal)))
            w = w[..^2];
        else if (w[^1] == 's' && w[^2] is not ('s' or 'u' or 'i')) w = w[..^1];

        if (w.EndsWith("ing", StringComparison.Ordinal) && w.Length - 3 >= 3 && HasVowel(w.AsSpan(0, w.Length - 3))) w = Undouble(w[..^3]);
        else if (w.EndsWith("ied", StringComparison.Ordinal) && w.Length > 4) w = w[..^3] + "y";
        else if (w.EndsWith("ed", StringComparison.Ordinal) && w.Length - 2 >= 3 && HasVowel(w.AsSpan(0, w.Length - 2))) w = Undouble(w[..^2]);
        else if (w.EndsWith("ically", StringComparison.Ordinal) && w.Length > 8) w = w[..^4];
        else if (w.EndsWith("ly", StringComparison.Ordinal) && w.Length - 2 >= 4) w = w[..^2];

        if (w.Length >= 4 && w[^1] == 'e') w = w[..^1];
        return w;
    }

    /// <summary>The stem of <paramref name="word"/> (lower case, without accents) by <paramref name="language"/>'s light rules,
    /// which reach the endings of number and gender: Spanish and Portuguese plurals and a last vowel ("perros" and "perra" →
    /// "perr", "corações" → "coraca"), French plurals, feminine "-e" and "-er" ("dernières" and "dernier" → "derni"), German "-e",
    /// "-en", "-er", "-ern" and "-s" ("Drachen" and "Drache" → "drach", "Kindern" → "kind") and Italian last vowels ("gatti" and
    /// "gatto" → "gatt", "amiche" → "amic"). English is <see cref="Stem(string)"/>. Short words and words in other scripts stay
    /// as they are.</summary>
    public static string Stem(string word, SearchLanguage language) => language switch
    {
        SearchLanguage.Spanish => SpanishStem(word),
        SearchLanguage.French => FrenchStem(word),
        SearchLanguage.German => GermanStem(word),
        SearchLanguage.Portuguese => PortugueseStem(word),
        SearchLanguage.Italian => ItalianStem(word),
        _ => Stem(word)
    };

    /// <summary>Lower case without culture rules (for comparing names).</summary>
    public static string Fold(string text) => text.ToLower(CultureInfo.InvariantCulture);

    // ---------- splitting ----------

    private enum Script : byte { None, Spaced, Mark, Han, Hiragana, Katakana, Hangul, Thai, Lao, Khmer, Myanmar }

    private static void Split(string text, List<string>? words, List<SearchWord>? located)
    {
        var word = ArrayPool<char>.Shared.Rent(MaximumTermLength + 2);
        try
        {
            var state = new Splitter(text, words, located, word);
            state.Run();
        }
        finally { ArrayPool<char>.Shared.Return(word); }
    }

    private struct Splitter(string text, List<string>? words, List<SearchWord>? located, char[] word)
    {
        private int length;
        private int start = -1;
        private bool skipping;
        private bool accented;
        // The run of characters of one script written without spaces, as the start of each character in the text.
        private Script run;
        private List<int>? starts;
        private bool? japanese;

        internal void Run()
        {
            var i = 0;
            while (i < text.Length)
            {
                var c = text[i];
                if (c < 0x80)
                {
                    if (IsAsciiLetterOrDigit(c))
                    {
                        if (run != Script.None) EndRun(i);
                        if (start < 0) start = i;
                        var j = i;
                        // A run of ASCII letters and digits at once: the common case.
                        while (j < text.Length && text[j] < 0x80 && IsAsciiLetterOrDigit(text[j])) j++;
                        if (!skipping)
                        {
                            if (length + (j - i) > MaximumTermLength)
                            {
                                length = 0;
                                skipping = true;
                            }
                            else
                                for (var k = i; k < j; k++)
                                {
                                    var a = text[k];
                                    word[length++] = a is >= 'A' and <= 'Z' ? (char)(a | 0x20) : a;
                                }
                        }
                        i = j;
                        continue;
                    }
                    Separator(c, ref i);
                    i++;
                    continue;
                }

                var width = 1;
                Script script;
                if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    width = 2;
                    var rune = new Rune(c, text[i + 1]);
                    script = Rune.IsLetter(rune) ? Classify(rune.Value) : IsMark(Rune.GetUnicodeCategory(rune)) ? Script.Mark : Script.Spaced;
                }
                else if (char.IsSurrogate(c)) script = Script.Spaced;
                else if (char.IsLetter(c)) script = Classify(c);
                else if (char.IsDigit(c)) script = Script.Spaced;
                else script = IsMark(CharUnicodeInfo.GetUnicodeCategory(c)) ? Script.Mark : Script.None;

                switch (script)
                {
                    case Script.Spaced:
                        if (run != Script.None) EndRun(i);
                        if (start < 0) start = i;
                        if (width == 2) Append(c, text[i + 1]);
                        else
                        {
                            // Full-width letters and digits ("ＡＢＣ１２３", typed with Japanese and Chinese keyboards) as ASCII.
                            if (c is >= '\uFF10' and <= '\uFF5A') c = (char)(c - 0xFEE0);
                            Append(c < 0x80 ? (c is >= 'A' and <= 'Z' ? (char)(c | 0x20) : c) : char.ToLowerInvariant(c), '\0');
                        }
                        break;
                    case Script.Mark:
                        // A combining mark belongs to the letter before it ("e" + "◌́" is "é"; Hindi and Thai vowel signs).
                        if (run == Script.None && (skipping || length > 0)) Append(c, width == 2 ? text[i + 1] : '\0');
                        break;
                    case Script.None:
                        Separator(c, ref i);
                        break;
                    default:
                        EndWord(i);
                        if (run != script) EndRun(i);
                        if (script == Script.Han && width == 1 && ChineseGrammar.Contains(c) &&
                            (japanese ??= text.AsSpan().IndexOfAnyInRange('\u3040', '\u30FF') >= 0) == false)
                        {
                            EndRun(i);
                            break;
                        }
                        run = script;
                        (starts ??= []).Add(i);
                        break;
                }
                i += width;
            }
            EndWord(text.Length);
            EndRun(text.Length);
        }

        private void Append(char c, char low)
        {
            if (skipping) return;
            var width = low == '\0' ? 1 : 2;
            if (length + width > MaximumTermLength)
            {
                length = 0;
                skipping = true;
                return;
            }
            word[length++] = c;
            if (width == 2) word[length++] = low;
            accented |= c > '\u007f';
        }

        private void Separator(char c, ref int i)
        {
            EndWord(i);
            EndRun(i);
            // "player's" → "player": an apostrophe and the s after it end the word.
            if (c is '\'' or '\u2019' && i + 1 < text.Length && text[i + 1] is 's' or 'S' &&
                (i + 2 >= text.Length || !char.IsLetterOrDigit(text[i + 2])))
                i++;
        }

        private void EndWord(int end)
        {
            if (start < 0) return;
            if (!skipping && length > 0)
            {
                var plain = new string(word, 0, length);
                if (accented) plain = WithoutAccents(plain);
                words?.Add(plain);
                located?.Add(new(plain, start, end - start));
            }
            length = 0;
            start = -1;
            skipping = false;
            accented = false;
        }

        private void EndRun(int end)
        {
            if (run == Script.None) return;
            run = Script.None;
            var count = starts!.Count;
            if (count == 1) Add(starts[0], end);
            else
                for (var k = 0; k + 1 < count; k++)
                    Add(starts[k], k + 2 < count ? starts[k + 2] : end);
            starts.Clear();
        }

        private void Add(int from, int to)
        {
            var pair = text.Substring(from, to - from);
            words?.Add(pair);
            located?.Add(new(pair, from, to - from));
        }
    }

    private static bool IsAsciiLetterOrDigit(char c) => (uint)((c | 0x20) - 'a') <= 'z' - 'a' || (uint)(c - '0') <= 9;
    private static bool IsMark(UnicodeCategory category) =>
        category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;

    private static Script Classify(int c) => c switch
    {
        < 0x0E00 => Script.Spaced,
        <= 0x0E7F => Script.Thai,
        <= 0x0EFF => Script.Lao,
        >= 0x1000 and <= 0x109F => Script.Myanmar,
        >= 0x1100 and <= 0x11FF => Script.Hangul,
        >= 0x1780 and <= 0x17FF => Script.Khmer,
        < 0x3005 => Script.Spaced,
        0x3005 or 0x3006 or 0x3007 or 0x303B => Script.Han,
        >= 0x3040 and <= 0x309F => Script.Hiragana,
        >= 0x30A0 and <= 0x30FF or >= 0x31F0 and <= 0x31FF or >= 0xFF66 and <= 0xFF9F => Script.Katakana,
        >= 0x3130 and <= 0x318F or >= 0xA960 and <= 0xA97F or >= 0xAC00 and <= 0xD7FF => Script.Hangul,
        >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF or >= 0xF900 and <= 0xFAFF or >= 0x20000 and <= 0x3134F => Script.Han,
        _ => Script.Spaced
    };

    // Chinese grammar characters (particles, pronouns, "is", "this", "that"...): they end a pair of Han characters, so "我的猫"
    // is "猫" rather than "我的" and "的猫". In Japanese text (with kana) they are parts of words (存在, 京都) and stay.
    private static readonly HashSet<char> ChineseGrammar =
    [
        '的', '了', '吗', '嗎', '呢', '吧', '啊', '呀', '嘛', '哦', '我', '你', '您', '他', '她', '它', '们', '們', '是', '这', '這', '那',
        '在', '和', '也', '就', '很', '还', '還', '又', '个', '個', '把', '被', '给', '給'
    ];

    // "pokémon" → "pokemon": combining marks after Latin letters are left out; other scripts keep theirs ("ガ" stays "ガ").
    private static string WithoutAccents(string word)
    {
        var decomposed = word.Normalize(NormalizationForm.FormD);
        var plain = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark && plain.Length > 0 &&
                plain[^1] is >= 'a' and <= 'z')
                continue;
            plain.Append(c);
        }
        return plain.ToString().Normalize(NormalizationForm.FormC);
    }

    // ---------- languages ----------

    private static int Bit(SearchLanguage language) => language == SearchLanguage.English ? English : 1 << (int)language;

    private const int Languages = 6, OtherLanguages = (1 << Languages) - 2;
    private static readonly int LongestGrammarWord = Grammar.Keys.Max(word => word.Length);

    private static int Mask(string word) => word.Length <= LongestGrammarWord ? Grammar.GetValueOrDefault(word) : 0;

    // A text is in a language other than English when it has at least two different grammar words of that language that are not
    // English grammar words too, more of them than English-only grammar words, and they are at least an eighth of its words (prose
    // has many; a list of links or file names with "de" and "com" in it has few). Single letters ("c" of "c'est", "-c Release")
    // and words common in English text too (Silent: "com", "ai", "plus", "die", "man") never count. Between languages with as
    // many, the one with more words of its own wins ("você" is only Portuguese, "que" is Spanish, French and Portuguese). votes
    // holds, for each language, the count, the first word counted, whether a different word was counted too, and the count of
    // words of only that language. Returns whether the word counts for a language other than English.
    private static bool Vote(Span<int> votes, ReadOnlySpan<string> words, int at, int mask)
    {
        if ((mask & English) != 0)
        {
            // A grammar word of English and of another language ("a", "do", "me") says nothing about which one it is.
            if ((mask & OtherLanguages) == 0) votes[0]++;
            return false;
        }
        var word = words[at];
        if ((mask & Silent) != 0 || word.Length < 2) return false;
        var counted = false;
        var own = System.Numerics.BitOperations.PopCount((uint)(mask & OtherLanguages)) == 1;
        for (var language = 1; language < Languages; language++)
        {
            if ((mask & (1 << language)) == 0) continue;
            counted = true;
            votes[language]++;
            if (own) votes[3 * Languages + language]++;
            ref var first = ref votes[Languages + language];
            if (first < 0) first = at;
            else if (!string.Equals(words[first], word, StringComparison.Ordinal)) votes[2 * Languages + language] = 1;
        }
        return counted;
    }

    private static SearchLanguage? Decide(ReadOnlySpan<int> votes, int words)
    {
        var best = 0;
        for (var language = 1; language < Languages; language++)
            if (votes[2 * Languages + language] == 1 && (best == 0 || votes[language] > votes[best] ||
                votes[language] == votes[best] && votes[3 * Languages + language] > votes[3 * Languages + best]))
                best = language;
        return best != 0 && votes[best] > votes[0] && votes[best] * 8 >= words ? (SearchLanguage)best : null;
    }
    private static string SpanishStem(string w)
    {
        if (w.Length < 5 || !IsAsciiLetters(w)) return w;
        if (w.EndsWith("ces", StringComparison.Ordinal) && IsVowel(w[^4])) w = w[..^3] + "z";
        else if (w.EndsWith("es", StringComparison.Ordinal) && !IsVowel(w[^3])) w = w[..^2];
        else if (w[^1] == 's') w = w[..^1];
        if (w.Length >= 5 && w[^1] is 'a' or 'e' or 'o') w = w[..^1];
        return w;
    }

    private static string PortugueseStem(string w)
    {
        if (w.Length < 4 || !IsAsciiLetters(w)) return w;
        if (w.Length >= 5 && w.EndsWith("oes", StringComparison.Ordinal)) w = w[..^3] + "ao";
        else if (w.Length >= 5 && w.EndsWith("ais", StringComparison.Ordinal)) w = w[..^3] + "al";
        else if (w.Length >= 5 && w.EndsWith("eis", StringComparison.Ordinal)) w = w[..^3] + "el";
        else if (w.Length >= 5 && w.EndsWith("ois", StringComparison.Ordinal)) w = w[..^3] + "ol";
        else if (w.EndsWith("ns", StringComparison.Ordinal)) w = w[..^2] + "m";
        else if (w.Length >= 5 && w.EndsWith("es", StringComparison.Ordinal) && w[^3] is 'r' or 's' or 'z' or 'l') w = w[..^2];
        else if (w[^1] == 's') w = w[..^1];
        if (w.Length >= 5 && w[^1] is 'a' or 'e' or 'o') w = w[..^1];
        return w;
    }

    private static string FrenchStem(string w)
    {
        if (w.Length < 4 || !IsAsciiLetters(w)) return w;
        if (w.Length >= 5 && w.EndsWith("aux", StringComparison.Ordinal)) w = w[..^3] + "al";
        else if (w[^1] is 's' or 'x') w = w[..^1];
        if (w.Length >= 5 && w.EndsWith("ee", StringComparison.Ordinal)) w = w[..^2];
        else if (w.Length >= 4 && w[^1] == 'e') w = w[..^1];
        if (w.Length >= 5 && w.EndsWith("er", StringComparison.Ordinal)) w = w[..^2];
        if (w.Length >= 4 && w[^1] == w[^2] && !IsVowel(w[^1])) w = w[..^1];
        return w;
    }

    private static string GermanStem(string w)
    {
        if (w.Contains('ß')) w = w.Replace("ß", "ss", StringComparison.Ordinal);
        if (w.Length < 4 || !IsAsciiLetters(w)) return w;
        if (w.Length > 5 && w.EndsWith("ern", StringComparison.Ordinal)) w = w[..^3];
        else if (w.Length > 4 && w[^2] == 'e' && w[^1] is 'm' or 'n' or 'r' or 's') w = w[..^2];
        else if (w[^1] == 'e') w = w[..^1];
        else if (w[^1] == 's' && IsGermanEnding(w[^2])) w = w[..^1];
        if (w.Length > 5 && w.EndsWith("est", StringComparison.Ordinal)) w = w[..^3];
        else if (w.Length > 4 && w[^2] == 'e' && w[^1] is 'r' or 'n') w = w[..^2];
        else if (w.Length > 4 && w.EndsWith("st", StringComparison.Ordinal) && w[^3] != 'r' && IsGermanEnding(w[^3])) w = w[..^2];
        return w;
    }

    private static string ItalianStem(string w)
    {
        if (w.Length < 4 || !IsAsciiLetters(w)) return w;
        return w[^1] switch
        {
            'e' or 'i' => w[^2] == 'h' || w[^2] == 'i' && w.Length >= 5 ? w[..^2] : w[..^1],
            'a' or 'o' => w[^2] == 'i' && w.Length >= 5 ? w[..^2] : w[..^1],
            _ => w
        };
    }

    private static bool IsGermanEnding(char c) => c is 'b' or 'd' or 'f' or 'g' or 'h' or 'k' or 'l' or 'm' or 'n' or 'r' or 't';

    private static bool IsVowel(char c) => c is 'a' or 'e' or 'i' or 'o' or 'u';

    private static string Undouble(string w) =>
        w.Length >= 4 && w[^1] == w[^2] && w[^1] is 'b' or 'd' or 'g' or 'm' or 'n' or 'p' or 'r' or 't' ? w[..^1] : w;

    private static bool HasVowel(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
            if (c is 'a' or 'e' or 'i' or 'o' or 'u' or 'y') return true;
        return false;
    }

    private static bool IsAsciiLetters(string word)
    {
        foreach (var c in word)
            if (c is < 'a' or > 'z') return false;
        return true;
    }

    private static Dictionary<string, int> GrammarWords()
    {
        var grammar = new Dictionary<string, int>(StringComparer.Ordinal);
        void Add(int bit, string words)
        {
            foreach (var word in words.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                grammar[word] = grammar.GetValueOrDefault(word) | bit;
        }
        Add(English,
            "a an the and or but nor of to in on at for with by from as into onto about than then so if is are was were be been being " +
            "am it its this that these those i me my you your yours he him his she her hers we us our they them their do does did " +
            "doing done how what where when why who whom which can could would should will shall may might must there here not no " +
            "yes any some all just very too also has have had having s t d ll re ve m " +
            // What is left of a contraction after its apostrophe ends the word ("don't" → "don" and "t"), and contractions typed
            // without one ("dont", "im").
            "don doesn didn isn aren wasn weren wouldn shouldn couldn haven hasn hadn ain mustn dont doesnt didnt isnt arent wasnt " +
            "werent wouldnt shouldnt couldnt havent hasnt hadnt cant im ive youre youve theyre thats theres whats hes shes whos " +
            "while because although unless whether whose");
        // Words are compared without accents: "más" is "mas", "für" is "fur", "não" is "nao".
        Add(1 << (int)SearchLanguage.Spanish,
            "a al algo algun alguna algunas alguno algunos ante aqui asi aun cada como con contra cual cuales cuando cuanto de del " +
            "desde donde el ella ellas ello ellos en entre era eran eres es esa esas ese eso esos esta estaba estaban estamos estan " +
            "estar estas este esto estos estoy fue fueron fui ha habia han has hay he hemos la las le les lo los mas me mi mia mio " +
            "mis muy nada ni no nos nosotros nuestra nuestras nuestro nuestros o os otra otro para pero por porque que quien se sea " +
            "ser si sin sobre son soy su sus tambien tan te tenia tengo ti tiene tienen toda todas todo todos tu tus un una unas uno " +
            "unos usted ustedes vosotros y ya yo hemos habia habiamos habian estuve estuvo fui fuimos sido cual cuales tus " +
            "nuestras alla aqui ahi");
        Add(1 << (int)SearchLanguage.French,
            "a ai as au aux avait avec avez avoir avons c ca ce cela celle celui ces cet cette chez comme comment d dans de des donc " +
            "du elle elles en encore es est et etait etais ete etre eu il ils j je l la le les leur leurs lui m ma mais me meme mes " +
            "moi mon n ne ni nos notre nous on ont ou par pas plus pour qu quand que quel quelle qui quoi s sa sans se ses si son " +
            "sont suis sur ta te tes toi ton tous tout toute tres tu un une vos votre vous y alors aussi deja ici avons avez " +
            "sommes etes avais avaient ou pourquoi oui non");
        Add(1 << (int)SearchLanguage.German,
            "aber alle allem allen aller alles als also am an auch auf aus bei bin bis bist da damit dann das dass daß dein deine " +
            "dem den denn der des dich die dir doch dort du durch ein eine einem einen einer eines er es etwas euch euer fur gegen " +
            "hab habe haben hast hat hatte hatten hier ich ihm ihn ihnen ihr ihre im in ist ja jetzt kann kein keine man mein meine " +
            "meinem meinen meiner mich mir mit nach nein nicht noch nur ob oder ohne schon sehr sein seine sich sie sind so uber um " +
            "und uns unser unsere unter vom von vor war waren warst was weil wenn wer wie wir wird wo zu zum zur mal habt seid " +
            "wurde wurden worden werden muss soll wann warum welche welcher welches");
        Add(1 << (int)SearchLanguage.Portuguese,
            "a ao aos as com como da das de dela dele deles do dos e ela elas ele eles em entre era essa esse isso esta estava este " +
            "estou estao eu foi ha isto ja lhe mais mas me meu meus minha minhas muito na nao nas nem no nos nossa nosso num numa o " +
            "os ou para pela pelo por pra pro porque quando que quem se sem ser seu seus sua suas sao so tambem te tem tenho teu tua " +
            "um uma voce voces vc ta temos tinha fui somos sou estava onde qual sobre todo toda todos todas tudo mesmo ate nada aqui " +
            "ali depois cada entao ainda sim outro outra");
        Add(1 << (int)SearchLanguage.Italian,
            "a ad ai al alla alle allo anche che chi ci come con cosa da dal dalla dei del della delle dello di e ed era gli ha hai " +
            "hanno ho i il in io la le lei li lo loro lui ma me mi mia mie miei mio ne nei nel nella noi non o per perche piu " +
            "quando quella quello questa questo se si sia siamo sei sono su sua sue suo sul sulla ti tu tua tue tuo un una uno vi " +
            "voi gia molto poi cosi ancora cui abbiamo avete avevo aveva avevamo essere stato stata sto stai sta stiamo siete " +
            "tutto tutti tutta qui qua nostro nostra vostro vostra quale quali dove ogni senza tra fra");
        // Grammar words of other languages that are common in English text too (words, names and abbreviations): left out of a
        // text in that language, but never a sign that a text is in it.
        Add(Silent,
            "ante con del era ha hay le lo mi sea sin son su tan un nada ni os ti yo ai ca ci est eu ma par plus sans on " +
            "die war hat man bin mit um er es da den ob mal com pro pra vc do no na ao sem ad li si come io al non dove");
        // Grammar words of the scripts written without spaces, as they come out of Words: Japanese particles and endings in
        // hiragana, Korean particles and endings, and common Chinese words made of grammar.
        Add(Always,
            "の は が を に で と も へ や か ね よ な だ た て し い る ん っ " +
            "です ます した して って てい いる ある ない こと もの それ これ あれ この その あの から まで けど でも よね かな ので " +
            "のは のが には では とか など ても たら だっ った じゃ でし まし ませ せん しょ ょう " +
            "이 가 은 는 을 를 에 의 도 와 과 로 나 너 저 그 것 수 더 좀 잘 안 못 다 또 " +
            "에서 에게 한테 으로 까지 부터 처럼 보다 하고 이랑 그리 리고 하지 지만 그래 래서 그런 런데 있어 있는 있다 했어 하는 해요 " +
            "합니 니다 습니 입니 이에 에요 예요 우리 저는 나는 너는 그냥 정말 아니 그거 이거 저거 " +
            "不 有 会 會 能 要 去 来 來 对 對 " +
            "什么 什麼 怎么 怎麼 一个 一個 没有 沒有 可以 就是 但是 因为 因為 所以 如果 不是 已经 已經 自己 知道 觉得 覺得 一下 " +
            "一些 有点 有點 然后 然後 其实 其實 真的 不过 不過 而且 或者 应该 應該");
        // A hiragana character alone is a particle or a verb ending ("が", "き" of "好き"), never a word to look for.
        for (var c = '\u3041'; c <= '\u3096'; c++) grammar[c.ToString()] = grammar.GetValueOrDefault(c.ToString()) | Always;
        return grammar;
    }
}
