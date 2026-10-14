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

    // Each job's stages, then one total (each job reports its own as Completed).
    private static IReadOnlyList<SongStageTiming> Timings(IReadOnlyList<SongStageTiming> music, IReadOnlyList<SongStageTiming> match)
    {
        var totals = music.Concat(match).Where(t => t.Stage == SongStage.Completed).ToArray();
        IEnumerable<SongStageTiming> stages = music.Concat(match).Where(t => t.Stage != SongStage.Completed);
        return totals.Length == 0 ? [.. stages]
            : [.. stages, new SongStageTiming(SongStage.Completed, TimeSpan.FromTicks(totals.Sum(t => t.Duration.Ticks)))];
    }
}
