using System.Globalization;
using System.Text;

namespace Martlet.Core.Text;

/// <summary>The words of a text as lexical search compares them (app guides, past conversations, memory): runs of Unicode letters
/// and digits, in lower case, with a possessive "'s" left out, common English grammar words (<see cref="IsStopWord"/>) left out
/// and each word reduced to a light English stem (<see cref="Stem"/>), so "swords" finds "sword" and "crafting" finds "craft".
/// Words longer than <see cref="MaximumTermLength"/> are left out. Pure and thread-safe.</summary>
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
        "any", "some", "all", "just", "very", "too", "also", "has", "have", "had", "having", "s", "t", "d", "ll", "re", "ve", "m"
    };

    /// <summary>The search terms of <paramref name="text"/> in order, repeats kept (term frequency counts them).</summary>
    public static IReadOnlyList<string> Of(string? text)
    {
        var terms = new List<string>();
        if (string.IsNullOrEmpty(text)) return terms;
        var word = new StringBuilder();
        var skipping = false;
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
                else word.Append(char.ToLowerInvariant(c));
                continue;
            }
            if (!skipping && word.Length > 0) Add(terms, word.ToString());
            word.Clear();
            skipping = false;
            // "player's" → "player": an apostrophe and the s after it end the word.
            if (c is '\'' or '\u2019' && i + 1 < text.Length && text[i + 1] is 's' or 'S' &&
                (i + 2 >= text.Length || !char.IsLetterOrDigit(text[i + 2])))
                i++;
        }
        return terms;
    }

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

    /// <summary>A light English stem of <paramref name="word"/> (lower case): plurals ("berries" → "berry", "boxes" → "box",
    /// "swords" → "sword"), then "-ing" and "-ed" with a doubled last letter made single ("running" → "run"), then a last "e"
    /// ("make" and "making" → "mak"). Short words, numbers and words in other scripts stay as they are. Stems need not be words:
    /// both sides of a comparison are stemmed the same way.</summary>
    public static string Stem(string word)
    {
        if (word.Length <= 3 || !IsAsciiLetters(word)) return word;
        var w = word;
        if (w.EndsWith("ies", StringComparison.Ordinal) && w.Length > 4) w = w[..^3] + "y";
        else if (w.EndsWith("sses", StringComparison.Ordinal)) w = w[..^2];
        else if (w.EndsWith("es", StringComparison.Ordinal) && w.Length > 4 &&
            (w[^3] is 's' or 'x' or 'z' || w.EndsWith("ches", StringComparison.Ordinal) || w.EndsWith("shes", StringComparison.Ordinal)))
            w = w[..^2];
        else if (w[^1] == 's' && w[^2] is not ('s' or 'u' or 'i')) w = w[..^1];

        if (w.EndsWith("ing", StringComparison.Ordinal) && w.Length - 3 >= 3 && HasVowel(w.AsSpan(0, w.Length - 3))) w = Undouble(w[..^3]);
        else if (w.EndsWith("ed", StringComparison.Ordinal) && w.Length - 2 >= 3 && HasVowel(w.AsSpan(0, w.Length - 2))) w = Undouble(w[..^2]);

        if (w.Length >= 4 && w[^1] == 'e') w = w[..^1];
        return w;
    }

    /// <summary>Lower case without culture rules (for comparing names).</summary>
    public static string Fold(string text) => text.ToLower(CultureInfo.InvariantCulture);

    private static void Add(List<string> terms, string word)
    {
        if (IsStopWord(word)) return;
        terms.Add(Stem(word));
    }

    private static string Undouble(string w) =>
        w.Length >= 3 && w[^1] == w[^2] && w[^1] is 'b' or 'd' or 'g' or 'm' or 'n' or 'p' or 'r' or 't' ? w[..^1] : w;

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
