using System.Buffers.Binary;

namespace Martlet.Core.Singing;

/// <summary>A song's voice match, made apart from its music (possibly on another computer): the matched vocals (48 kHz mono,
/// as long as the music's vocals), the model that matched them and where, and how long each stage took.</summary>
public sealed record SongVoiceMatchResult(string JobId, SongTrack Vocals, string Converter, SongVoiceMatch VoiceMatch, bool Fixture,
    IReadOnlyList<SongStageTiming> StageTimings);

/// <summary>Puts a song together from its music (the music model's own singer, separated) and its voice match: the matched
/// vocals go over the backing, and the three tracks are scaled together when the mix would clip, so they stay in step.</summary>
public static class SongVoice
{
    /// <summary>The largest sample the mix may reach (of full scale).</summary>
    public const double Peak = 0.98;

    /// <summary><paramref name="music"/> sung with <paramref name="match"/>'s vocals: mix = backing + matched vocals; the
    /// timing (lines, words, beats) stays the music's, since a voice match keeps the singing's timing. The matched vocals are
    /// cut or padded with silence to the backing's length.</summary>
    public static SongResult WithVoice(SongResult music, SongVoiceMatchResult match)
    {
        ArgumentNullException.ThrowIfNull(music);
        ArgumentNullException.ThrowIfNull(match);
        if (music.Backing.Channels != 2 || match.Vocals.Channels != 1 || music.Backing.SampleRate != match.Vocals.SampleRate)
            throw new SongException(SongErrorCodes.Failed, "The voice match doesn't fit the song's music.");
        var frames = checked((int)music.Backing.Frames);
        var backing = music.Backing.Pcm16.Span;
        var vocals = match.Vocals.Pcm16.Span;
        var vocalFrames = (int)Math.Min(frames, match.Vocals.Frames);
        var peak = 0;
        for (var i = 0; i < frames; i++)
        {
            int voice = i < vocalFrames ? BinaryPrimitives.ReadInt16LittleEndian(vocals[(2 * i)..]) : 0;
            peak = Math.Max(peak, Math.Abs(BinaryPrimitives.ReadInt16LittleEndian(backing[(4 * i)..]) + voice));
            peak = Math.Max(peak, Math.Abs(BinaryPrimitives.ReadInt16LittleEndian(backing[(4 * i + 2)..]) + voice));
        }
        var scale = peak > Peak * short.MaxValue ? Peak * short.MaxValue / peak : 1.0;
        var mix = new byte[frames * 4];
        var bed = new byte[frames * 4];
        var dry = new byte[frames * 2];
        static short Sample(double value) => (short)Math.Clamp(Math.Round(value), short.MinValue, short.MaxValue);
        for (var i = 0; i < frames; i++)
        {
            var voice = i < vocalFrames ? BinaryPrimitives.ReadInt16LittleEndian(vocals[(2 * i)..]) * scale : 0;
            var left = BinaryPrimitives.ReadInt16LittleEndian(backing[(4 * i)..]) * scale;
            var right = BinaryPrimitives.ReadInt16LittleEndian(backing[(4 * i + 2)..]) * scale;
            BinaryPrimitives.WriteInt16LittleEndian(dry.AsSpan(2 * i), Sample(voice));
            BinaryPrimitives.WriteInt16LittleEndian(bed.AsSpan(4 * i), Sample(left));
            BinaryPrimitives.WriteInt16LittleEndian(bed.AsSpan(4 * i + 2), Sample(right));
            BinaryPrimitives.WriteInt16LittleEndian(mix.AsSpan(4 * i), Sample(left + voice));
            BinaryPrimitives.WriteInt16LittleEndian(mix.AsSpan(4 * i + 2), Sample(right + voice));
        }
        var rate = music.Backing.SampleRate;
        return music with
        {
            Mix = new SongTrack(SongTrackKind.Mix, rate, 2, mix),
            Vocals = new SongTrack(SongTrackKind.Vocals, rate, 1, dry),
            Backing = new SongTrack(SongTrackKind.Backing, rate, 2, bed),
            Engine = music.Engine with
            {
                Converter = match.Converter, VoiceMatch = match.VoiceMatch, Fixture = music.Engine.Fixture || match.Fixture
            },
            StageTimings = Timings(music.StageTimings, match.StageTimings)
        };
    }

    /// <summary>A song made without separating it (a cloud provider's mix, and no computer or provider to separate it): the
    /// vocals are the mix's middle (mono), the backing is what is left on each side, so backing + vocals is the mix again
    /// exactly. The mouth then follows the whole mix, and a stop fades the middle and the sides apart.</summary>
    public static (SongTrack Vocals, SongTrack Backing) Unseparated(SongTrack mix)
    {
        ArgumentNullException.ThrowIfNull(mix);
        var stereo = Stereo(mix);
        var frames = checked((int)stereo.Frames);
        var source = stereo.Pcm16.Span;
        var vocals = new byte[frames * 2];
        var backing = new byte[frames * 4];
        for (var i = 0; i < frames; i++)
        {
            int left = BinaryPrimitives.ReadInt16LittleEndian(source[(4 * i)..]), right = BinaryPrimitives.ReadInt16LittleEndian(source[(4 * i + 2)..]);
            var middle = (short)((left + right) / 2);
            BinaryPrimitives.WriteInt16LittleEndian(vocals.AsSpan(2 * i), middle);
            BinaryPrimitives.WriteInt16LittleEndian(backing.AsSpan(4 * i), (short)Math.Clamp(left - middle, short.MinValue, short.MaxValue));
            BinaryPrimitives.WriteInt16LittleEndian(backing.AsSpan(4 * i + 2), (short)Math.Clamp(right - middle, short.MinValue, short.MaxValue));
        }
        return (new SongTrack(SongTrackKind.Vocals, stereo.SampleRate, 1, vocals), new SongTrack(SongTrackKind.Backing, stereo.SampleRate, 2, backing));
    }

    /// <summary><paramref name="track"/> as two channels (a mono track is copied to both).</summary>
    public static SongTrack Stereo(SongTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (track.Channels == 2) return track;
        var source = track.Pcm16.Span;
        var stereo = new byte[source.Length * 2];
        for (var i = 0; i + 1 < source.Length; i += 2)
        {
            source.Slice(i, 2).CopyTo(stereo.AsSpan(2 * i));
            source.Slice(i, 2).CopyTo(stereo.AsSpan(2 * i + 2));
        }
        return new SongTrack(track.Kind, track.SampleRate, 2, stereo);
    }

    /// <summary>Matched vocals made by a cloud provider, made to fit the song as a singing host's are: as long as
    /// <paramref name="original"/>, silent outside the phrases the original sings (with 20 ms fades, so no conversion noise
    /// between them reaches lip sync), and levelled to the original's loudness there. Both are 48 kHz mono.</summary>
    public static SongTrack Gate(SongTrack converted, SongTrack original)
    {
        ArgumentNullException.ThrowIfNull(converted);
        ArgumentNullException.ThrowIfNull(original);
        if (converted.Channels != 1 || original.Channels != 1 || converted.SampleRate != original.SampleRate)
            throw new SongException(SongErrorCodes.Failed, "The matched vocals don't fit the song.");
        var frames = checked((int)original.Frames);
        var sung = Phrases(original.Pcm16.Span, original.SampleRate);
        var input = converted.Pcm16.Span;
        var fade = original.SampleRate / 50;
        var output = new double[frames];
        double convertedPower = 0, originalPower = 0;
        long counted = 0;
        foreach (var (start, end) in sung)
            for (var i = start; i < end && i < frames; i++)
            {
                var gain = Math.Min(1.0, Math.Min(i - start, end - 1 - i) / (double)fade);
                var sample = 2 * i + 1 < input.Length ? BinaryPrimitives.ReadInt16LittleEndian(input[(2 * i)..]) * gain : 0;
                output[i] = sample;
                double source = BinaryPrimitives.ReadInt16LittleEndian(original.Pcm16.Span[(2 * i)..]);
                convertedPower += sample * sample;
                originalPower += source * source;
                counted++;
            }
        var level = counted == 0 || convertedPower <= 0 ? 0 : Math.Sqrt(originalPower / convertedPower);
        var pcm = new byte[frames * 2];
        for (var i = 0; i < frames; i++)
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(2 * i), (short)Math.Clamp(Math.Round(output[i] * level), short.MinValue, short.MaxValue));
        return new SongTrack(SongTrackKind.Vocals, original.SampleRate, 1, pcm);
    }

    /// <summary>The sung phrases of mono <paramref name="pcm"/> (start and end frames): 50 ms windows louder than 30 dB under its
    /// loudest window, joined across gaps shorter than 350 ms, each at least 300 ms long.</summary>
    public static IReadOnlyList<(int Start, int End)> Phrases(ReadOnlySpan<byte> pcm, int sampleRate)
    {
        var frames = pcm.Length / 2;
        var window = Math.Max(1, sampleRate / 20);
        var windows = (frames + window - 1) / window;
        var loudness = new double[windows];
        for (var w = 0; w < windows; w++)
        {
            double sum = 0;
            var end = Math.Min(frames, (w + 1) * window);
            for (var i = w * window; i < end; i++)
            {
                double sample = BinaryPrimitives.ReadInt16LittleEndian(pcm[(2 * i)..]);
                sum += sample * sample;
            }
            loudness[w] = Math.Sqrt(sum / Math.Max(1, end - w * window));
        }
        var peak = loudness.Length == 0 ? 0 : loudness.Max();
        if (peak <= 1) return [];
        var threshold = peak * Math.Pow(10, -30 / 20.0);
        var phrases = new List<(int Start, int End)>();
        var gap = (int)(0.35 * sampleRate / window);
        for (var w = 0; w < windows; w++)
        {
            if (loudness[w] < threshold) continue;
            var start = w;
            while (w + 1 < windows && loudness[w + 1] >= threshold) w++;
            if (phrases.Count > 0 && start - phrases[^1].End / window <= gap) phrases[^1] = (phrases[^1].Start, Math.Min(frames, (w + 1) * window));
            else phrases.Add((start * window, Math.Min(frames, (w + 1) * window)));
        }
        return [.. phrases.Where(p => p.End - p.Start >= 0.3 * sampleRate)];
    }

    /// <summary>The lyric lines of <paramref name="lyrics"/> spread over the singing of <paramref name="vocals"/> (48 kHz mono):
    /// the lines take the sung phrases in order, each phrase's share by its length; without sung phrases, the song's middle 84%.
    /// For a song whose provider gives no lyric times and no computer timed it.</summary>
    public static IReadOnlyList<SongLyricLine> SpreadLines(string lyrics, SongTrack vocals)
    {
        ArgumentNullException.ThrowIfNull(vocals);
        var lines = SongLyrics.Parse(lyrics);
        if (lines.Count == 0) return [];
        var rate = vocals.SampleRate;
        var phrases = Phrases(vocals.Pcm16.Span, rate);
        if (phrases.Count == 0) phrases = [((int)(vocals.Frames * 0.08), (int)(vocals.Frames * 0.92))];
        var total = phrases.Sum(p => (double)(p.End - p.Start));
        // Each line gets an equal share of the singing time, laid along the phrases.
        var result = new List<SongLyricLine>(lines.Count);
        var share = total / lines.Count;
        for (var l = 0; l < lines.Count; l++)
        {
            var start = At(l * share);
            var end = At((l + 1) * share - 1);
            result.Add(new SongLyricLine(TimeSpan.FromSeconds(start / rate), TimeSpan.FromSeconds(end / rate), lines[l].Text, lines[l].Section));
        }
        return result;

        double At(double sung)
        {
            foreach (var (start, end) in phrases)
            {
                if (sung < end - start) return start + Math.Max(0, sung);
                sung -= end - start;
            }
            return phrases[^1].End;
        }
    }

    // Each job's stages, then one total (each job reports its own as Completed).
    private static IReadOnlyList<SongStageTiming> Timings(IReadOnlyList<SongStageTiming> music, IReadOnlyList<SongStageTiming> match)
    {
        var totals = music.Concat(match).Where(t => t.Stage == SongStage.Completed).ToArray();
        IEnumerable<SongStageTiming> stages = music.Concat(match).Where(t => t.Stage != SongStage.Completed);
        return totals.Length == 0 ? [.. stages]
            : [.. stages, new SongStageTiming(SongStage.Completed, TimeSpan.FromTicks(totals.Sum(t => t.Duration.Ticks)))];
    }
}
