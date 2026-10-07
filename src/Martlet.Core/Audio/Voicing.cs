namespace Martlet.Core.Audio;

/// <summary>How much of some speech is voiced (the vocal folds buzzing) rather than breathed, as a whisper is.</summary>
public static class Voicing
{
    /// <summary>The normalized autocorrelation at which a frame counts as voiced.</summary>
    public const double VoicedCorrelation = 0.6;

    /// <summary>The share of <paramref name="samples"/> (mono 16-bit PCM at <paramref name="sampleRate"/>) that is voiced: of
    /// the 40 ms frames, every 20 ms, at least a tenth as loud as the loudest and above -50 dBFS, those whose waveform repeats
    /// at a pitch between 70 and 400 Hz (normalized autocorrelation at least <see cref="VoicedCorrelation"/>). Measured on
    /// Chatterbox Turbo's starter voices: 0.61-0.92 for ordinary sentences, at most 0.18 for whispered ones. Null without
    /// such frames.</summary>
    public static double? VoicedShare(ReadOnlySpan<short> samples, int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8_000);
        int frame = sampleRate / 25, hop = sampleRate / 50, shortest = sampleRate / 400, longest = sampleRate / 70;
        if (samples.Length < frame) return null;
        var count = (samples.Length - frame) / hop + 1;
        var levels = new double[count];
        for (var f = 0; f < count; f++)
        {
            double sum = 0;
            foreach (var sample in samples.Slice(f * hop, frame)) sum += (double)sample * sample;
            levels[f] = Math.Sqrt(sum / frame) / short.MaxValue;
        }
        var floor = Math.Max(levels.Max() * 0.1, Math.Pow(10, -50 / 20.0));
        var x = new double[frame];
        int active = 0, voiced = 0;
        for (var f = 0; f < count; f++)
        {
            if (levels[f] < floor) continue;
            active++;
            var window = samples.Slice(f * hop, frame);
            double mean = 0;
            for (var i = 0; i < frame; i++) mean += x[i] = window[i];
            mean /= frame;
            for (var i = 0; i < frame; i++) x[i] -= mean;
            var best = 0.0;
            for (var lag = shortest; lag <= longest && best < VoicedCorrelation; lag++)
            {
                double ab = 0, aa = 0, bb = 0;
                for (var i = 0; i + lag < frame; i++)
                {
                    ab += x[i] * x[i + lag];
                    aa += x[i] * x[i];
                    bb += x[i + lag] * x[i + lag];
                }
                if (aa > 0 && bb > 0) best = Math.Max(best, ab / Math.Sqrt(aa * bb));
            }
            if (best >= VoicedCorrelation) voiced++;
        }
        return active == 0 ? null : Math.Round((double)voiced / active, 3);
    }
}
