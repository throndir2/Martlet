using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Martlet.Audio;

/// <summary>
/// Local speaker-embedding inference: a pure managed port of Resemblyzer's GE2E voice encoder
/// (40-band mel frames, three 256-unit LSTM layers, linear + ReLU, L2 normalized). No native
/// runtime, network or GPU is used. Embeddings are non-negative unit vectors, so cosine
/// similarity is in [0, 1]; same-speaker clean speech is typically above 0.8.
/// </summary>
public sealed class SpeakerEncoder
{
    public const int SampleRate = 16_000;
    public const int EmbeddingSize = 256;
    public const int PartialFrames = 160;
    private const int Mels = 40, Hidden = 256, Layers = 3, Fft = 400, Hop = 160, Bins = Fft / 2 + 1;
    private const int Gates = 4 * Hidden;
    private static readonly float[] Window = HannPeriodic();
    private static readonly float[] Cos = new float[Bins * Fft], Sin = new float[Bins * Fft];
    private static readonly float[][] MelFilters = SlaneyMelFilters();
    private readonly float[][] inputWeights = new float[Layers][], recurrentWeights = new float[Layers][], biases = new float[Layers][];
    private readonly float[] linearWeights = new float[EmbeddingSize * Hidden], linearBias = new float[EmbeddingSize];

    static SpeakerEncoder()
    {
        for (var k = 0; k < Bins; k++)
            for (var n = 0; n < Fft; n++)
            {
                var angle = 2 * Math.PI * k * n / Fft;
                Cos[k * Fft + n] = (float)Math.Cos(angle);
                Sin[k * Fft + n] = (float)Math.Sin(angle);
            }
    }

    private SpeakerEncoder() { }

    /// <summary>Reads the flat "MVID" v1 weight file produced by scripts/Convert-SpeakerEncoder.py.</summary>
    public static SpeakerEncoder Load(Stream weights)
    {
        ArgumentNullException.ThrowIfNull(weights);
        var header = new byte[24];
        weights.ReadExactly(header);
        if (header[0] != 'M' || header[1] != 'V' || header[2] != 'I' || header[3] != 'D' ||
            BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4)) != 1 ||
            BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8)) != Mels ||
            BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12)) != Hidden ||
            BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(16)) != Layers ||
            BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(20)) != EmbeddingSize)
            throw new InvalidDataException("Unsupported voice encoder weight file.");
        var encoder = new SpeakerEncoder();
        for (var layer = 0; layer < Layers; layer++)
        {
            encoder.inputWeights[layer] = ReadFloats(weights, Gates * (layer == 0 ? Mels : Hidden));
            encoder.recurrentWeights[layer] = ReadFloats(weights, Gates * Hidden);
            encoder.biases[layer] = ReadFloats(weights, Gates);
        }
        ReadFloats(weights, encoder.linearWeights);
        ReadFloats(weights, encoder.linearBias);
        if (weights.ReadByte() != -1) throw new InvalidDataException("Unexpected trailing voice encoder data.");
        return encoder;
    }

    /// <summary>
    /// Embeds 16 kHz mono speech. The caller should pass speech only (silence trimmed); volume is
    /// raised to -30 dBFS like the reference preprocessing. Partials cover ~1.6 s each.
    /// </summary>
    public SpeakerEmbedding Embed(ReadOnlySpan<float> samples)
    {
        if (samples.Length < SampleRate / 4) throw new ArgumentException("At least 250 ms of speech is required.", nameof(samples));
        var (partials, paddedLength) = PartialStarts(samples.Length);
        var wave = new float[Math.Max(samples.Length, paddedLength)];
        samples.CopyTo(wave);
        NormalizeVolume(wave.AsSpan(0, samples.Length));
        var mel = MelSpectrogram(wave);
        Array.Clear(wave);
        var embeddings = new float[partials.Length][];
        Parallel.For(0, partials.Length, i => embeddings[i] = EmbedPartial(mel, partials[i]));
        var mean = new float[EmbeddingSize];
        foreach (var partial in embeddings)
            for (var j = 0; j < EmbeddingSize; j++) mean[j] += partial[j];
        Normalize(mean);
        var length = samples.Length;
        var ranges = partials.Select(start => new SpeakerPartial(start * Hop, Math.Min(length, (start + PartialFrames) * Hop)))
            .ToArray();
        return new(mean, Array.AsReadOnly(embeddings.Select(e => (IReadOnlyList<float>)Array.AsReadOnly(e)).ToArray()), ranges);
    }

    public SpeakerEmbedding EmbedPcm16(ReadOnlySpan<byte> pcm)
    {
        var samples = new float[pcm.Length / 2];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm.Slice(i * 2, 2)) / 32768f;
        try { return Embed(samples); }
        finally { Array.Clear(samples); }
    }

    public static float Similarity(IReadOnlyList<float> a, IReadOnlyList<float> b)
    {
        if (a.Count != EmbeddingSize || b.Count != EmbeddingSize) throw new ArgumentException("Embedding size mismatch.");
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < EmbeddingSize; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        return na == 0 || nb == 0 ? 0 : (float)(dot / Math.Sqrt(na * nb));
    }

    /// <summary>Averages several unit embeddings of the same speaker and re-normalizes.</summary>
    public static float[] Average(IEnumerable<IReadOnlyList<float>> embeddings)
    {
        var mean = new float[EmbeddingSize];
        var count = 0;
        foreach (var embedding in embeddings)
        {
            if (embedding.Count != EmbeddingSize) throw new ArgumentException("Embedding size mismatch.");
            for (var j = 0; j < EmbeddingSize; j++) mean[j] += embedding[j];
            count++;
        }
        if (count == 0) throw new ArgumentException("At least one embedding is required.");
        Normalize(mean);
        return mean;
    }

    // Mirrors Resemblyzer's compute_partial_slices(rate=1.3, min_coverage=0.75).
    private static (int[] Starts, int PaddedLength) PartialStarts(int samples)
    {
        var frames = (int)Math.Ceiling((samples + 1) / (double)Hop);
        var step = (int)Math.Round(SampleRate / 1.3 / Hop, MidpointRounding.ToEven);
        var starts = new List<int>();
        for (var i = 0; i < Math.Max(1, frames - PartialFrames + step + 1); i += step) starts.Add(i);
        var last = starts[^1] * Hop;
        var coverage = (samples - last) / (double)(PartialFrames * Hop);
        if (coverage < 0.75 && starts.Count > 1) starts.RemoveAt(starts.Count - 1);
        return (starts.ToArray(), (starts[^1] + PartialFrames) * Hop);
    }

    private static void NormalizeVolume(Span<float> wave)
    {
        double squares = 0;
        foreach (var sample in wave) squares += sample * (double)sample;
        var rms = Math.Sqrt(squares / wave.Length);
        if (rms <= 0) return;
        var change = -30 - 20 * Math.Log10(rms);
        if (change <= 0) return;
        var gain = (float)Math.Pow(10, change / 20);
        foreach (ref var sample in wave) sample *= gain;
    }

    // librosa.feature.melspectrogram(n_fft=400, hop=160, n_mels=40, power=2, center=True, reflect padding).
    private static float[] MelSpectrogram(float[] wave)
    {
        var frames = 1 + wave.Length / Hop;
        var mel = new float[frames * Mels];
        Parallel.For(0, frames, () => (new float[Fft], new float[Bins]), (frame, _, buffers) =>
        {
            var (segment, power) = buffers;
            var start = frame * Hop - Fft / 2;
            for (var n = 0; n < Fft; n++) segment[n] = Reflect(wave, start + n) * Window[n];
            for (var k = 0; k < Bins; k++)
            {
                var re = Dot(segment, Cos.AsSpan(k * Fft, Fft));
                var im = Dot(segment, Sin.AsSpan(k * Fft, Fft));
                power[k] = re * re + im * im;
            }
            for (var m = 0; m < Mels; m++) mel[frame * Mels + m] = Dot(MelFilters[m], power);
            return buffers;
        }, _ => { });
        return mel;
    }

    private static float Reflect(float[] wave, int index)
    {
        var length = wave.Length;
        if (length == 1) return wave[0];
        var period = 2 * (length - 1);
        index %= period;
        if (index < 0) index += period;
        return wave[index < length ? index : period - index];
    }

    private float[] EmbedPartial(float[] mel, int startFrame)
    {
        var input = mel.AsSpan(startFrame * Mels, PartialFrames * Mels).ToArray();
        var width = Mels;
        var h = new float[Hidden];
        var c = new float[Hidden];
        var gates = new float[Gates];
        for (var layer = 0; layer < Layers; layer++)
        {
            var output = new float[PartialFrames * Hidden];
            Array.Clear(h);
            Array.Clear(c);
            var wi = inputWeights[layer];
            var wh = recurrentWeights[layer];
            var bias = biases[layer];
            for (var t = 0; t < PartialFrames; t++)
            {
                var x = input.AsSpan(t * width, width);
                for (var g = 0; g < Gates; g++)
                    gates[g] = bias[g] + Dot(wi.AsSpan(g * width, width), x) + Dot(wh.AsSpan(g * Hidden, Hidden), h);
                for (var j = 0; j < Hidden; j++)
                {
                    var i = Sigmoid(gates[j]);
                    var f = Sigmoid(gates[Hidden + j]);
                    var cell = MathF.Tanh(gates[2 * Hidden + j]);
                    var o = Sigmoid(gates[3 * Hidden + j]);
                    c[j] = f * c[j] + i * cell;
                    h[j] = o * MathF.Tanh(c[j]);
                }
                h.CopyTo(output.AsSpan(t * Hidden, Hidden));
            }
            input = output;
            width = Hidden;
        }
        var embedding = new float[EmbeddingSize];
        for (var e = 0; e < EmbeddingSize; e++)
            embedding[e] = Math.Max(0, linearBias[e] + Dot(linearWeights.AsSpan(e * Hidden, Hidden), h));
        Normalize(embedding);
        return embedding;
    }

    private static float Sigmoid(float x) => 1f / (1f + MathF.Exp(-x));

    private static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var sum = Vector<float>.Zero;
        var va = MemoryMarshal.Cast<float, Vector<float>>(a);
        var vb = MemoryMarshal.Cast<float, Vector<float>>(b);
        for (var v = 0; v < va.Length; v++) sum += va[v] * vb[v];
        var i = va.Length * Vector<float>.Count;
        var result = Vector.Sum(sum);
        for (; i < a.Length; i++) result += a[i] * b[i];
        return result;
    }

    private static void Normalize(float[] vector)
    {
        double norm = 0;
        foreach (var value in vector) norm += value * (double)value;
        if (norm <= 0) return;
        var scale = (float)(1 / Math.Sqrt(norm));
        for (var i = 0; i < vector.Length; i++) vector[i] *= scale;
    }

    private static float[] HannPeriodic()
    {
        var window = new float[Fft];
        for (var n = 0; n < Fft; n++) window[n] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * n / Fft));
        return window;
    }

    // librosa.filters.mel(sr=16000, n_fft=400, n_mels=40, htk=False, norm="slaney").
    private static float[][] SlaneyMelFilters()
    {
        static double HzToMel(double hz) => hz < 1000 ? hz / (200.0 / 3) : 15 + Math.Log(hz / 1000) / (Math.Log(6.4) / 27);
        static double MelToHz(double mel) => mel < 15 ? mel * (200.0 / 3) : 1000 * Math.Exp((Math.Log(6.4) / 27) * (mel - 15));
        var points = new double[Mels + 2];
        var top = HzToMel(SampleRate / 2.0);
        for (var i = 0; i < points.Length; i++) points[i] = MelToHz(top * i / (Mels + 1));
        var filters = new float[Mels][];
        for (var m = 0; m < Mels; m++)
        {
            filters[m] = new float[Bins];
            var lowerWidth = points[m + 1] - points[m];
            var upperWidth = points[m + 2] - points[m + 1];
            var norm = 2.0 / (points[m + 2] - points[m]);
            for (var k = 0; k < Bins; k++)
            {
                var frequency = k * (SampleRate / 2.0) / (Bins - 1);
                var lower = (frequency - points[m]) / lowerWidth;
                var upper = (points[m + 2] - frequency) / upperWidth;
                filters[m][k] = (float)(Math.Max(0, Math.Min(lower, upper)) * norm);
            }
        }
        return filters;
    }

    private static float[] ReadFloats(Stream stream, int count) => ReadFloats(stream, new float[count]);

    private static float[] ReadFloats(Stream stream, float[] target)
    {
        if (!BitConverter.IsLittleEndian) throw new PlatformNotSupportedException("Voice ID weights require a little-endian CPU.");
        stream.ReadExactly(MemoryMarshal.AsBytes(target.AsSpan()));
        foreach (var value in target)
            if (!float.IsFinite(value)) throw new InvalidDataException("Voice encoder weights contain a non-finite value.");
        return target;
    }
}

public readonly record struct SpeakerPartial(int StartSample, int EndSampleExclusive);

public sealed record SpeakerEmbedding(IReadOnlyList<float> Vector, IReadOnlyList<IReadOnlyList<float>> Partials,
    IReadOnlyList<SpeakerPartial> PartialRanges);
