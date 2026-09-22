using System.Text;

namespace Martlet.Memory;

internal readonly record struct LexicalMatch(Guid FactId, double Score, int MatchedTerms);

internal sealed class LexicalIndex
{
    private const int MaximumTokenRunes = 64;
    private readonly IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, int>> documents;
    private readonly IReadOnlyDictionary<string, int> documentFrequency;

    private LexicalIndex(
        IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, int>> documents,
        IReadOnlyDictionary<string, int> documentFrequency)
    {
        this.documents = documents;
        this.documentFrequency = documentFrequency;
    }

    internal int DocumentCount => documents.Count;
    internal int TermCount => documentFrequency.Count;
    internal bool ContainsTerm(string term) => documentFrequency.ContainsKey(term);

    internal static LexicalIndex Build(IEnumerable<MemoryFact> facts)
    {
        var documents = new Dictionary<Guid, IReadOnlyDictionary<string, int>>();
        var frequencies = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var fact in facts)
        {
            var terms = Tokenize(fact.Content, MemoryLimits.MaximumContentCharacters)
                .GroupBy(term => term, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
            documents.Add(fact.Id, terms);
            foreach (var term in terms.Keys)
                frequencies[term] = frequencies.GetValueOrDefault(term) + 1;
        }
        return new(documents, frequencies);
    }

    internal static string[] QueryTerms(string query)
    {
        MemoryGuard.Require(query is { Length: > 0 } && !string.IsNullOrWhiteSpace(query) && !query.Contains('\0'));
        MemoryGuard.Require(query.Length <= MemoryLimits.MaximumQueryCharacters, MemoryFailure.LimitExceeded);
        try
        {
            MemoryGuard.Require(new UTF8Encoding(false, true).GetByteCount(query) <= MemoryLimits.MaximumQueryUtf8Bytes,
                MemoryFailure.LimitExceeded);
        }
        catch (EncoderFallbackException)
        {
            throw new MemoryException(MemoryFailure.InvalidData);
        }
        var terms = Tokenize(query, MemoryLimits.MaximumQueryCharacters)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        MemoryGuard.Require(terms is { Length: > 0 and <= MemoryLimits.MaximumQueryTerms }, MemoryFailure.LimitExceeded);
        return terms;
    }

    internal LexicalMatch[] Search(string[] queryTerms, int maximumResults, CancellationToken token)
    {
        MemoryGuard.Require(maximumResults is >= 1 and <= MemoryLimits.MaximumResults);
        var matches = new List<LexicalMatch>();
        foreach (var document in documents)
        {
            token.ThrowIfCancellationRequested();
            var score = 0d;
            var matched = 0;
            foreach (var term in queryTerms)
            {
                if (!document.Value.TryGetValue(term, out var frequency))
                    continue;
                matched++;
                var inverseDocumentFrequency =
                    Math.Log((documents.Count + 1d) / (documentFrequency[term] + 1d)) + 1d;
                score += (1d + Math.Log(frequency)) * inverseDocumentFrequency;
            }
            if (matched > 0)
                matches.Add(new(document.Key, score, matched));
        }
        return matches
            .OrderByDescending(match => match.Score)
            .ThenByDescending(match => match.MatchedTerms)
            .ThenBy(match => match.FactId)
            .Take(maximumResults)
            .ToArray();
    }

    private static IEnumerable<string> Tokenize(string text, int maximumCharacters)
    {
        MemoryGuard.Require(text.Length <= maximumCharacters);
        var token = new StringBuilder();
        var runes = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune))
            {
                if (runes < MaximumTokenRunes)
                {
                    token.Append(Rune.ToLowerInvariant(rune));
                    runes++;
                }
                else
                {
                    token.Clear();
                    runes = MaximumTokenRunes + 1;
                }
            }
            else
            {
                if (token.Length > 0 && runes <= MaximumTokenRunes)
                    yield return token.ToString();
                token.Clear();
                runes = 0;
            }
        }
        if (token.Length > 0 && runes <= MaximumTokenRunes)
            yield return token.ToString();
    }
}
