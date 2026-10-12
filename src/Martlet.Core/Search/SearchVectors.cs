using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Martlet.Core.Search;

/// <summary>What a text is embedded for: a search's question or a document to be found. Many embedding models want a different
/// start for each (<c>search_query: </c> and <c>search_document: </c>).</summary>
public enum EmbeddingUse
{
    Query,
    Document
}

/// <summary>An embedding model (docs/SEARCH.md, "Finding by meaning"): it turns each text into a list of numbers that shows what
/// the text means, so two texts that mean the same thing with different words get close vectors. Martlet uses it only away from
/// the reply's first word.</summary>
public interface ITextEmbedder
{
    /// <summary>The model and where it runs, as one name (<c>ollama:nomic-embed-text</c>): vectors of two models never mix.</summary>
    string Model { get; }

    /// <summary>The most texts one request embeds.</summary>
    int MaximumBatch { get; }

    /// <summary>One vector for each of <paramref name="texts"/>, in order, all of one length.</summary>
    Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, EmbeddingUse use, CancellationToken token);
}

/// <summary>A document found by meaning: its ID, the cosine similarity of its vector to the query's, and <see cref="Meaning"/>:
/// how much closer it is than the set's average document, from 0 (no closer) to 1 (the same). The average is the "unrelated"
/// level of this model and this query, so the same threshold works for models whose similarities have different scales.</summary>
public readonly record struct SearchNeighbor(string Id, double Similarity, double Meaning);

/// <summary>The vectors of one source's documents (memory facts, a guide's sections, the research reports) for one embedding
/// model: derived data, never a second copy of the documents. Each vector is kept with a digest of the text it was made from, so
/// a changed document is embedded again, and <see cref="Keep"/> drops the vectors of documents that are gone, so a deleted fact
/// or report doesn't survive here. A search (<see cref="Nearest"/>) compares the query's vector with every vector, in this process,
/// in about a millisecond for 10,000 documents. Thread-safe.</summary>
public sealed class VectorSet
{
    /// <summary>A set smaller than this has no reliable average similarity, so <see cref="SearchNeighbor.Meaning"/> uses at most
    /// <see cref="SmallSetBaseline"/> as its unrelated level.</summary>
    public const int SmallSet = 8;
    public const double SmallSetBaseline = 0.3;

    private const uint Magic = 0x3153564D; // "MVS1"
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private (string Id, float[] Vector)[]? snapshot;

    private sealed record Entry(byte[] Digest, float[] Vector);

    public VectorSet(string model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        Model = model;
    }

    /// <summary>The embedding model's name (<see cref="ITextEmbedder.Model"/>).</summary>
    public string Model { get; }

    /// <summary>The length of each vector (0 while the set is empty).</summary>
    public int Dimensions { get; private set; }

    public int Count { get { lock (gate) return entries.Count; } }

    /// <summary>Whether the set changed since it was last saved or loaded.</summary>
    public bool Changed { get; private set; }

    /// <summary>The digest a vector is kept with: SHA-256 of the text.</summary>
    public static byte[] Digest(string text) => SHA256.HashData(Encoding.UTF8.GetBytes(text ?? ""));

    /// <summary>Whether <paramref name="id"/> has a vector of exactly <paramref name="text"/>.</summary>
    public bool Has(string id, string text)
    {
        lock (gate) return entries.TryGetValue(id, out var entry) && entry.Digest.AsSpan().SequenceEqual(Digest(text));
    }

    /// <summary><paramref name="id"/>'s vector (unit length), or null.</summary>
    public float[]? Vector(string id)
    {
        lock (gate) return entries.TryGetValue(id, out var entry) ? entry.Vector : null;
    }

    /// <summary>The documents of <paramref name="items"/> without a vector of their current text, in order.</summary>
    public IReadOnlyList<(string Id, string Text)> Missing(IEnumerable<(string Id, string Text)> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var missing = new List<(string, string)>();
        lock (gate)
            foreach (var (id, text) in items)
                if (!entries.TryGetValue(id, out var entry) || !entry.Digest.AsSpan().SequenceEqual(Digest(text)))
                    missing.Add((id, text));
        return missing;
    }

    /// <summary>Keeps <paramref name="vector"/> (made unit length) as <paramref name="id"/>'s for <paramref name="text"/>. All
    /// vectors of a set have one length; a vector of another length, or with a number that isn't finite, is refused.</summary>
    public void Put(string id, string text, float[] vector)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(vector);
        var unit = Normalize(vector) ?? throw new ArgumentException("The vector is empty, zero or not finite.", nameof(vector));
        lock (gate)
        {
            if (entries.Count > 0 && unit.Length != Dimensions)
                throw new ArgumentException($"The vector has {unit.Length} numbers; this set's have {Dimensions}.", nameof(vector));
            Dimensions = unit.Length;
            entries[id] = new(Digest(text), unit);
            snapshot = null;
            Changed = true;
        }
    }

    /// <summary>Drops every vector whose ID isn't in <paramref name="ids"/>; returns how many were dropped.</summary>
    public int Keep(IEnumerable<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var live = ids as IReadOnlySet<string> ?? ids.ToHashSet(StringComparer.Ordinal);
        lock (gate)
        {
            var gone = entries.Keys.Where(id => !live.Contains(id)).ToArray();
            foreach (var id in gone) entries.Remove(id);
            if (gone.Length > 0)
            {
                snapshot = null;
                Changed = true;
            }
            if (entries.Count == 0) Dimensions = 0;
            return gone.Length;
        }
    }

    /// <summary>Drops every vector.</summary>
    public void Clear() => Keep([]);

    /// <summary>The <paramref name="want"/> documents nearest to <paramref name="query"/> (cosine similarity), best first, with
    /// their meaning; <paramref name="include"/> leaves documents out and <paramref name="except"/> is never listed (a document
    /// compared with its neighbors). Empty when the query's length isn't the set's.</summary>
    public SearchNeighbor[] Nearest(float[] query, int want, Func<string, bool>? include = null, string? except = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (want <= 0) return [];
        (string Id, float[] Vector)[] all;
        lock (gate) all = snapshot ??= [.. entries.Select(e => (e.Key, e.Value.Vector))];
        if (all.Length == 0 || query.Length != all[0].Vector.Length || Normalize(query) is not { } unit) return [];
        var similar = new double[all.Length];
        var keep = new bool[all.Length];
        double sum = 0;
        var counted = 0;
        for (var i = 0; i < all.Length; i++)
        {
            if (all[i].Id == except || include?.Invoke(all[i].Id) == false) continue;
            similar[i] = Dot(unit, all[i].Vector);
            keep[i] = true;
            sum += similar[i];
            counted++;
        }
        if (counted == 0) return [];
        var mean = sum / counted;
        var baseline = counted >= SmallSet ? mean : Math.Min(mean, SmallSetBaseline);
        var order = Enumerable.Range(0, all.Length).Where(i => keep[i])
            .OrderByDescending(i => similar[i]).ThenBy(i => all[i].Id, StringComparer.Ordinal).Take(want);
        return [.. order.Select(i => new SearchNeighbor(all[i].Id, similar[i],
            baseline >= 1 ? 0 : Math.Clamp((similar[i] - baseline) / (1 - baseline), 0, 1)))];
    }

    /// <summary>The dot product of two vectors of one length (their cosine similarity when both have unit length).</summary>
    public static double Dot(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        if (left.Length != right.Length) throw new ArgumentException("The vectors have different lengths.");
        var total = 0f;
        var i = 0;
        if (System.Numerics.Vector.IsHardwareAccelerated && left.Length >= Vector<float>.Count)
        {
            var sum = Vector<float>.Zero;
            var a = MemoryMarshal.Cast<float, Vector<float>>(left);
            var b = MemoryMarshal.Cast<float, Vector<float>>(right);
            for (var v = 0; v < a.Length; v++) sum += a[v] * b[v];
            total = System.Numerics.Vector.Sum(sum);
            i = a.Length * Vector<float>.Count;
        }
        for (; i < left.Length; i++) total += left[i] * right[i];
        return total;
    }

    /// <summary><paramref name="vector"/> scaled to unit length, or null when it is empty, all zero or not finite.</summary>
    public static float[]? Normalize(float[] vector)
    {
        if (vector is not { Length: > 0 }) return null;
        double length = 0;
        foreach (var value in vector)
        {
            if (!float.IsFinite(value)) return null;
            length += (double)value * value;
        }
        if (length <= 0) return null;
        var scale = 1 / Math.Sqrt(length);
        var unit = new float[vector.Length];
        for (var i = 0; i < unit.Length; i++) unit[i] = (float)(vector[i] * scale);
        return unit;
    }

    /// <summary>Embeds the documents of <paramref name="items"/> that have no vector of their current text, in batches, and keeps
    /// their vectors. While <paramref name="hold"/> says so (a reply runs) it waits between batches. Returns how many it embedded.</summary>
    public async Task<int> FillAsync(ITextEmbedder embedder, IReadOnlyList<(string Id, string Text)> items, Func<bool>? hold,
        CancellationToken token, Action<int, int>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(embedder);
        if (embedder.Model != Model) throw new ArgumentException("The embedder's model isn't this set's.", nameof(embedder));
        var missing = Missing(items);
        var done = 0;
        var batch = Math.Max(1, embedder.MaximumBatch);
        for (var start = 0; start < missing.Count; start += batch)
        {
            while (hold?.Invoke() == true) await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var part = missing.Skip(start).Take(batch).ToArray();
            var vectors = await embedder.EmbedAsync([.. part.Select(p => p.Text)], EmbeddingUse.Document, token).ConfigureAwait(false);
            if (vectors.Length != part.Length) throw new InvalidOperationException("The embedding model gave a different number of vectors.");
            for (var i = 0; i < part.Length; i++) Put(part[i].Id, part[i].Text, vectors[i]);
            done += part.Length;
            progress?.Invoke(done, missing.Count);
        }
        return done;
    }

    /// <summary>Writes the set to <paramref name="path"/> in one step (a temporary file, then a rename).</summary>
    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        KeyValuePair<string, Entry>[] all;
        int dimensions;
        lock (gate)
        {
            all = [.. entries];
            dimensions = Dimensions;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = File.Create(temporary))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(Magic);
                writer.Write(Model);
                writer.Write(dimensions);
                writer.Write(all.Length);
                foreach (var (id, entry) in all)
                {
                    writer.Write(id);
                    writer.Write(entry.Digest);
                    writer.Write(MemoryMarshal.AsBytes(entry.Vector.AsSpan()));
                }
            }
            File.Move(temporary, path, overwrite: true);
            lock (gate) Changed = false;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>The set kept at <paramref name="path"/> for <paramref name="model"/>, or an empty one when there is none, it was
    /// made by another model or it can't be read.</summary>
    public static VectorSet Load(string path, string model)
    {
        var set = new VectorSet(model);
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream, Encoding.UTF8);
            if (reader.ReadUInt32() != Magic || reader.ReadString() != model) return set;
            var dimensions = reader.ReadInt32();
            var count = reader.ReadInt32();
            if (dimensions is < 1 or > 16_384 || count is < 0 or > 1_000_000) return set;
            var loaded = new Dictionary<string, Entry>(count, StringComparer.Ordinal);
            for (var i = 0; i < count; i++)
            {
                var id = reader.ReadString();
                var digest = reader.ReadBytes(32);
                var bytes = reader.ReadBytes(dimensions * sizeof(float));
                if (digest.Length != 32 || bytes.Length != dimensions * sizeof(float)) return set;
                loaded[id] = new(digest, MemoryMarshal.Cast<byte, float>(bytes).ToArray());
            }
            lock (set.gate)
            {
                foreach (var (id, entry) in loaded) set.entries[id] = entry;
                set.Dimensions = loaded.Count > 0 ? dimensions : 0;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or EndOfStreamException or ArgumentException)
        {
            return new VectorSet(model);
        }
        return set;
    }

    public override string ToString() => $"VectorSet {{ Model = {Model}, Count = {Count}, Dimensions = {Dimensions} }}";
}
