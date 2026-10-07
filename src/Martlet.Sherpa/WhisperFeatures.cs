using System.Numerics;

namespace Martlet.Sherpa;

/// <summary>Whisper's log-mel features for Smart Turn v3 (the math of Hugging Face's <c>WhisperFeatureExtractor(chunk_length=8)</c>
/// with <c>do_normalize=True</c>, as Pipecat's own Smart Turn code computes it): the last 8 s of 16 kHz audio, zeros in front when
/// shorter, normalized to zero mean and unit variance, a centered 400-point Hann-windowed power spectrum every 160 samples, 80 Slaney
/// mel bands, log10, clamped to 8 below the loudest and scaled to about -1..1. Returns 80 x 800 values, band by band.</summary>
internal static class WhisperFeatures
{
    internal const int SampleRate = 16_000, Samples = 8 * SampleRate, Bands = 80, Frames = 800;
    private const int Fft = 400, Hop = 160, Bins = Fft / 2 + 1, Pad = Fft / 2;
    // Each frequency bin's windowed cosine and sine rows, so one frame's spectrum is two dot products per bin.
    private static readonly float[] cosines = new float[Bins * Fft], sines = new float[Bins * Fft];
    private static readonly (int First, double[] Weights)[] filters = Filters();

    static WhisperFeatures()
    {
        for (var k = 0; k < Bins; k++)
            for (var n = 0; n < Fft; n++)
            {
                var window = 0.5 - 0.5 * Math.Cos(2 * Math.PI * n / Fft);
                var angle = 2 * Math.PI * ((long)k * n % Fft) / Fft;
                cosines[k * Fft + n] = (float)(window * Math.Cos(angle));
                sines[k * Fft + n] = (float)(window * Math.Sin(angle));
            }
    }

    internal static float[] Compute(ReadOnlySpan<float> audio)
    {
        var x = new float[Samples];
        var kept = audio.Length > Samples ? audio[^Samples..] : audio;
        var lead = Samples - kept.Length;
        kept.CopyTo(x.AsSpan(lead));
        double sum = 0;
        foreach (var value in x) sum += value;
        var mean = sum / Samples;
        double squares = 0;
        foreach (var value in x) squares += (value - mean) * (value - mean);
        var scale = 1 / Math.Sqrt(squares / Samples + 1e-7);
        for (var i = 0; i < Samples; i++) x[i] = (float)((x[i] - mean) * scale);

        // Centered frames with reflected edges.
        var padded = new float[Samples + 2 * Pad];
        x.CopyTo(padded, Pad);
        for (var i = 0; i < Pad; i++)
        {
            padded[i] = x[Pad - i];
            padded[Pad + Samples + i] = x[Samples - 2 - i];
        }

        var logs = new float[Bands * Frames];
        var power = new double[Bins];
        double[]? constant = null;
        var max = double.MinValue;
        for (var frame = 0; frame < Frames; frame++)
        {
            var start = frame * Hop;
            // Frames that see only the zeros in front (all one value after normalizing) share one spectrum.
            var flat = lead > Pad && start + Fft - Pad <= lead;
            if (flat && constant is not null) constant.CopyTo(power, 0);
            else
            {
                var samples = padded.AsSpan(start, Fft);
                for (var k = 0; k < Bins; k++)
                {
                    double re = Dot(samples, cosines.AsSpan(k * Fft, Fft)), im = Dot(samples, sines.AsSpan(k * Fft, Fft));
                    power[k] = re * re + im * im;
                }
                if (flat) constant = (double[])power.Clone();
            }
            for (var band = 0; band < Bands; band++)
            {
                var (first, weights) = filters[band];
                double energy = 0;
                for (var i = 0; i < weights.Length; i++) energy += weights[i] * power[first + i];
                var log = Math.Log10(Math.Max(1e-10, energy));
                logs[band * Frames + frame] = (float)log;
                if (log > max) max = log;
            }
        }
        var floor = max - 8;
        for (var i = 0; i < logs.Length; i++) logs[i] = (float)((Math.Max(logs[i], floor) + 4) / 4);
        return logs;
    }

    private static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var width = Vector<float>.Count;
        var total = Vector<float>.Zero;
        var i = 0;
        for (; i <= a.Length - width; i += width) total += new Vector<float>(a[i..]) * new Vector<float>(b[i..]);
        var sum = Vector.Sum(total);
        for (; i < a.Length; i++) sum += a[i] * b[i];
        return sum;
    }

    // Slaney-normalized triangular mel filters from 0 to 8 kHz over the 201 bins, kept as each band's nonzero run.
    private static (int, double[])[] Filters()
    {
        static double ToMel(double hz) => hz >= 1000 ? 15 + Math.Log(hz / 1000) * (27 / Math.Log(6.4)) : 3 * hz / 200;
        static double ToHz(double mel) => mel >= 15 ? 1000 * Math.Exp(Math.Log(6.4) / 27 * (mel - 15)) : 200 * mel / 3;
        var low = ToMel(0);
        var high = ToMel(SampleRate / 2.0);
        var edges = new double[Bands + 2];
        for (var i = 0; i < edges.Length; i++) edges[i] = ToHz(low + (high - low) * i / (Bands + 1));
        var result = new (int, double[])[Bands];
        for (var band = 0; band < Bands; band++)
        {
            var weights = new double[Bins];
            for (var bin = 0; bin < Bins; bin++)
            {
                var hz = (SampleRate / 2.0) * bin / (Bins - 1);
                var down = (hz - edges[band]) / (edges[band + 1] - edges[band]);
                var up = (edges[band + 2] - hz) / (edges[band + 2] - edges[band + 1]);
                weights[bin] = Math.Max(0, Math.Min(down, up)) * 2 / (edges[band + 2] - edges[band]);
            }
            var first = Array.FindIndex(weights, w => w > 0);
            var last = Array.FindLastIndex(weights, w => w > 0);
            result[band] = first < 0 ? (0, []) : (first, weights[first..(last + 1)]);
        }
        return result;
    }
}
