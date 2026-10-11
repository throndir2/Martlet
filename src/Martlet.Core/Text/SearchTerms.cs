using System.Globalization;
using System.Text;

namespace Martlet.Core.Text;

/// <summary>The words of a text as lexical search compares them (app guides, past conversations, memory): runs of Unicode letters
/// and digits, in lower case, with accents on Latin letters left out ("Pokémon" is "pokemon"), with a possessive "'s" left out,
/// common English grammar words (<see cref="IsStopWord"/>) left out and each word reduced to a light English stem
/// (<see cref="Stem"/>), so "swords" finds "sword", "crafting" finds "craft" and "found" finds "find". Words longer than
/// <see cref="MaximumTermLength"/> are left out. Pure and thread-safe.</summary>
public static class SearchTerms
{
    public const int MaximumTermLength = 40;

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "and", "or", "but", "nor", "of", "to", "in", "on", "at", "for", "with", "by", "from", "as", "into", "onto",
        "about", "than", "then", "so", "if", "is", "are", "was", "were", "be", "been", "being", "am", "it", "its", "this", "that",
        "these", "those", "i", "me", "my", "you", "your", "yours", "he", "him", "his", "she", "her", "hers", "we", "us",
        "our", "they", "them", "their", "do", "does", "did", "doing", "done", "how", "what", "where", "when", "why", "who", "whom",
        "which", "can", "could", "would", "should", "will", "shall", "may", "might", "must", "there", "here", "not", "no", "yes",
        "any", "some", "all", "just", "very", "too", "also", "has", "have", "had", "having", "s", "t", "d", "ll", "re", "ve", "m",
        // What is left of a contraction after its apostrophe ends the word ("don't" → "don" and "t"), and contractions typed
        // without one ("dont", "im").
        "don", "doesn", "didn", "isn", "aren", "wasn", "weren", "wouldn", "shouldn", "couldn", "haven", "hasn", "hadn", "ain", "mustn",
        "dont", "doesnt", "didnt", "isnt", "arent", "wasnt", "werent", "wouldnt", "shouldnt", "couldnt", "havent", "hasnt", "hadnt",
        "cant", "im", "ive", "youre", "youve", "theyre", "thats", "theres", "whats", "hes", "shes", "whos",
        "while", "because", "although", "unless", "whether", "whose"
    };

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

    /// <summary>The search terms of <paramref name="text"/> in order, repeats kept (term frequency counts them).</summary>
    public static IReadOnlyList<string> Of(string? text)
    {
        var terms = new List<string>();
        foreach (var word in Words(text))
            if (Term(word) is { } term) terms.Add(term);
        return terms;
    }

    /// <summary>The words of <paramref name="text"/> in order before grammar words are left out and before stemming: runs of
    /// letters and digits in lower case, accents on Latin letters left out, a possessive "'s" left out and words longer than
    /// <see cref="MaximumTermLength"/> left out. <see cref="Term"/> makes a word's search term.</summary>
    public static IReadOnlyList<string> Words(string? text)
    {
        var words = new List<string>();
        if (string.IsNullOrEmpty(text)) return words;
        var word = new StringBuilder();
        var skipping = false;
        var accented = false;
        for (var i = 0; i <= text.Length; i++)
        {
            var c = i < text.Length ? text[i] : ' ';
            if (char.IsLetterOrDigit(c) || char.IsSurrogate(c))
            {
                if (skipping) continue;
                if (word.Length >= MaximumTermLength)
                {
                    word.Clear();
                    skipping = true;
                }
                else
                {
                    word.Append(char.ToLowerInvariant(c));
                    accented |= c > '\u007f';
                }
                continue;
            }
            if (!skipping && word.Length > 0) words.Add(accented ? WithoutAccents(word.ToString()) : word.ToString());
            word.Clear();
            skipping = false;
            accented = false;
            // "player's" → "player": an apostrophe and the s after it end the word.
            if (c is '\'' or '\u2019' && i + 1 < text.Length && text[i + 1] is 's' or 'S' &&
                (i + 2 >= text.Length || !char.IsLetterOrDigit(text[i + 2])))
                i++;
        }
        return words;
    }

    /// <summary>The search term of one word from <see cref="Words"/>: its stem (<see cref="Stem"/>), or null for a grammar word
    /// (<see cref="IsStopWord"/>).</summary>
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

    /// <summary>Whether <paramref name="word"/> (lower case) is a common English grammar word that search leaves out.</summary>
    public static bool IsStopWord(string word) => StopWords.Contains(word);

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

    /// <summary>Lower case without culture rules (for comparing names).</summary>
    public static string Fold(string text) => text.ToLower(CultureInfo.InvariantCulture);

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
}
