using Martlet.Core.Text;

namespace Martlet.Conversation.Guides;

/// <summary>Builds a guide's search index: BM25 over each chunk's page title, section and text (<see cref="SearchTerms"/>), with
/// relevance as the share of the question's terms a chunk has.</summary>
public static class GuideIndex
{
    public static IGuideIndex Build(IReadOnlyList<GuideChunk> chunks) => new Bm25Index(chunks ?? throw new ArgumentNullException(nameof(chunks)));

    private sealed class Bm25Index : IGuideIndex
    {
        private const double K1 = 1.2, B = 0.75;
        private readonly GuideChunk[] chunks;
        private readonly Dictionary<string, int>[] frequencies;
        private readonly int[] lengths;
        private readonly Dictionary<string, int> documentFrequency = new(StringComparer.Ordinal);
        private readonly double averageLength;

        public Bm25Index(IReadOnlyList<GuideChunk> source)
        {
            chunks = [.. source];
            frequencies = new Dictionary<string, int>[chunks.Length];
            lengths = new int[chunks.Length];
            long total = 0;
            for (var i = 0; i < chunks.Length; i++)
            {
                var terms = SearchTerms.Of(chunks[i].Page + "\n" + chunks[i].Section + "\n" + chunks[i].Text);
                var counts = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var term in terms) counts[term] = counts.GetValueOrDefault(term) + 1;
                foreach (var term in counts.Keys) documentFrequency[term] = documentFrequency.GetValueOrDefault(term) + 1;
                frequencies[i] = counts;
                lengths[i] = terms.Count;
                total += terms.Count;
            }
            averageLength = chunks.Length == 0 ? 1 : Math.Max(1, (double)total / chunks.Length);
        }

        public int Count => chunks.Length;

        public IReadOnlyList<GuideHit> Search(string query, int max)
        {
            if (max <= 0 || chunks.Length == 0) return [];
            var terms = SearchTerms.Distinct(query).Where(documentFrequency.ContainsKey).ToArray();
            var asked = SearchTerms.Distinct(query).Count;
            if (terms.Length == 0) return [];
            var hits = new List<GuideHit>();
            for (var i = 0; i < chunks.Length; i++)
            {
                double score = 0;
                var matched = 0;
                foreach (var term in terms)
                {
                    if (!frequencies[i].TryGetValue(term, out var tf)) continue;
                    matched++;
                    var df = documentFrequency[term];
                    var idf = Math.Log(1 + (chunks.Length - df + 0.5) / (df + 0.5));
                    score += idf * tf * (K1 + 1) / (tf + K1 * (1 - B + B * lengths[i] / averageLength));
                }
                if (matched > 0) hits.Add(new(chunks[i], score, (double)matched / Math.Max(1, asked)));
            }
            return hits.OrderByDescending(h => h.Score).Take(max).ToArray();
        }
    }
}
