using System.Text;
using Martlet.Core.Text;

namespace Martlet.Core.Search;

/// <summary>FIXTURE (NOT AI): a tiny deterministic embedder for checks and tests of finding by meaning (docs/SEARCH.md). Each word
/// of a few everyday topics (food, a home, pets, music, work, travel, sleep, weapons) counts toward its topic's number, so texts
/// about one topic in different words come out close, as with a real embedding model; any other word counts a little toward
/// one of 32 hashed numbers. It knows nothing else and is never used for a real search.</summary>
public sealed class FixtureEmbedder : ITextEmbedder
{
    private const int Hashed = 32;
    private const float OtherWeight = 0.25f;

    private static readonly string[][] Topics =
    [
        ["cook", "cooking", "dinner", "lunch", "breakfast", "meal", "eat", "food", "recipe", "vegetarian", "vegan", "meat", "dish", "kitchen", "hungry"],
        ["live", "home", "house", "apartment", "city", "seattle", "portland", "address", "move", "moved", "neighborhood"],
        ["dog", "cat", "pet", "puppy", "kitten", "rex", "walk", "vet"],
        ["music", "guitar", "piano", "song", "band", "concert", "drum", "sing"],
        ["job", "work", "office", "boss", "career", "nurse", "hospital", "shift", "clinic", "colleague"],
        ["trip", "travel", "vacation", "flight", "holiday", "japan", "hotel", "airport", "tokyo"],
        ["sleep", "tired", "bed", "insomnia", "rest", "nap", "awake"],
        ["weapon", "sword", "stronger", "upgrade", "blacksmith", "forge", "damage", "armor", "ore"]
    ];

    private static readonly Dictionary<string, int> TopicOf = Build();

    public string Model => "fixture:concepts";

    public int MaximumBatch { get; init; } = 4;

    /// <summary>How many texts it embedded (questions and documents).</summary>
    public int Embedded { get; private set; }

    /// <summary>How many requests it answered.</summary>
    public int Requests { get; private set; }

    public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, EmbeddingUse use, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(texts);
        token.ThrowIfCancellationRequested();
        Requests++;
        Embedded += texts.Count;
        return Task.FromResult(texts.Select(Embed).ToArray());
    }

    /// <summary>The vector of <paramref name="text"/> (one number for each topic, then the hashed ones).</summary>
    public static float[] Embed(string text)
    {
        var vector = new float[Topics.Length + Hashed];
        foreach (var term in SearchTerms.Of(text ?? ""))
        {
            if (TopicOf.TryGetValue(term, out var topic)) vector[topic] += 1;
            else vector[Topics.Length + (int)(Hash(term) % Hashed)] += OtherWeight;
        }
        // An empty text still gets a vector (a real model gives one too).
        if (vector.All(v => v == 0)) vector[^1] = OtherWeight;
        return vector;
    }

    private static Dictionary<string, int> Build()
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var topic = 0; topic < Topics.Length; topic++)
            foreach (var word in Topics[topic])
                foreach (var term in SearchTerms.Of(word))
                    map.TryAdd(term, topic);
        return map;
    }

    private static uint Hash(string term)
    {
        var hash = 2166136261u;
        foreach (var b in Encoding.UTF8.GetBytes(term)) hash = (hash ^ b) * 16777619u;
        return hash;
    }
}
