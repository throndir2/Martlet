namespace Martlet.Conversation;

/// <summary>Renders a song from its separate backing and vocals as 48 kHz stereo, following a <see cref="SongStartPlan"/>
/// (the backing's fade-in on a downbeat, the vocals muted until the line, the band vamping on a bar while Martlet still talks),
/// a <see cref="SongStopPlan"/> (the vocals finishing their word, the backing ringing to the next beat and fading) and ducking
/// (-12 dB while Martlet talks over it). Only its player's thread renders; <see cref="Hold"/> and <see cref="Ducked"/> may be
/// set from any thread.</summary>
public sealed class SongMixer
{
    /// <summary>At most this many times the band repeats the lead-in bar while Martlet is still talking; then it sings anyway
    /// (turned down under the speech).</summary>
    public const int MaximumVamps = 4;
    /// <summary>-12 dB.</summary>
    public const double DuckGain = 0.2512;
    public static TimeSpan DuckTime => TimeSpan.FromMilliseconds(60);
    public static TimeSpan VocalRamp => TimeSpan.FromMilliseconds(20);
    public static TimeSpan LoopCrossfade => TimeSpan.FromMilliseconds(10);
    private readonly object gate = new();
    private readonly SongAudio audio;
    private readonly SongMap map;
    private readonly VocalEnvelope? envelope;
    private readonly int rate;
    private readonly long entry, fade, vocalsFrom, commitAt, vampStart, vampEnd, ramp, crossfade;
    private readonly bool top;
    private readonly double duckStep;
    private readonly List<(long Output, long Source)> segments = [];
    private long cursor, rendered, crossfadeSource;
    private int crossfadeLeft;
    private bool committed, vampPending, faded, finished;
    private long vocalsEnd = long.MaxValue, vocalsFade, backingFrom = long.MaxValue, backingFade, silent = long.MaxValue;
    private SongStopPlan? stop;
    private double duck = 1;
    private volatile bool hold, ducked;

    public SongMixer(SongAudio audio, SongMap map, VocalEnvelope? envelope, SongStartPlan plan)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(plan);
        this.audio = audio;
        this.map = map;
        this.envelope = envelope;
        Plan = plan;
        rate = audio.SampleRate;
        top = plan.Top;
        entry = Frame(plan.Entry);
        fade = Math.Max(1, Frame(plan.FadeIn));
        vocalsFrom = Frame(plan.VocalsFrom);
        commitAt = Frame(plan.CommitAt);
        vampStart = Frame(plan.VampStart);
        vampEnd = Frame(plan.VampEnd);
        ramp = Math.Max(1, Frame(VocalRamp));
        crossfade = Math.Max(1, Frame(LoopCrossfade));
        duckStep = 1 - Math.Exp(-1.0 / Math.Max(1, DuckTime.TotalSeconds * rate));
        cursor = Math.Clamp(entry, 0, audio.Frames);
        committed = top;
        faded = top;
        segments.Add((0, cursor));
    }

    public SongStartPlan Plan { get; }
    public int SampleRate => rate;
    /// <summary>Martlet is still talking: at the lead-in's last bar the band vamps instead of going into the line.</summary>
    public bool Hold { get => hold; set => hold = value; }
    /// <summary>Martlet is talking: the song is turned down by 12 dB (smoothly) until it stops.</summary>
    public bool Ducked { get => ducked; set => ducked = value; }
    public int Vamps { get; private set; }
    /// <summary>The vocals are on: the song started from the top, or its lead-in went into the line.</summary>
    public bool Singing { get { lock (gate) return committed; } }
    public bool Finished { get { lock (gate) return finished; } }
    public SongStopPlan? Stopping { get { lock (gate) return stop; } }
    /// <summary>How many frames were rendered so far.</summary>
    public long Rendered { get { lock (gate) return rendered; } }
    /// <summary>The song time rendered up to (what is written ahead of what is heard).</summary>
    public TimeSpan Written { get { lock (gate) return Time(cursor); } }
    /// <summary>The vocals' current gain (0 muted, 1 full), for the character's mouth.</summary>
    public double VocalGain { get; private set; }
    public double CurrentDuck { get; private set; } = 1;

    /// <summary>The song time that rendered frame <paramref name="output"/> came from (vamps repeat a bar, so it isn't linear).</summary>
    public TimeSpan SourceAt(long output)
    {
        lock (gate)
        {
            var source = segments[0].Source + output;
            foreach (var (from, start) in segments)
            {
                if (from > output) break;
                source = start + (output - from);
            }
            return Time(Math.Min(source, audio.Frames));
        }
    }

    /// <summary>Renders up to <paramref name="frames"/> stereo frames into <paramref name="stereo"/>; returns how many (fewer once
    /// the song has ended or stopped).</summary>
    public int Render(Span<short> stereo, int frames)
    {
        lock (gate)
        {
            var count = 0;
            var backing = audio.Backing;
            var vocals = audio.Vocals;
            for (; count < frames && count * 2 + 1 < stereo.Length; count++)
            {
                if (finished || cursor >= audio.Frames || cursor >= silent)
                {
                    finished = true;
                    break;
                }
                if (!committed && stop is null)
                {
                    if (cursor >= commitAt && !vampPending)
                    {
                        if (hold && Vamps < MaximumVamps) vampPending = true;
                        else committed = true;
                    }
                    if (vampPending && cursor >= vampEnd)
                    {
                        crossfadeSource = cursor;
                        crossfadeLeft = (int)crossfade;
                        cursor = vampStart;
                        Vamps++;
                        vampPending = false;
                        segments.Add((rendered, cursor));
                        if (segments.Count > 64) segments.RemoveAt(1);
                    }
                }
                var gain = 1.0;
                if (!faded)
                {
                    if (cursor < entry + fade) gain = Math.Sin(Math.PI / 2 * Math.Max(0, cursor - entry) / fade);
                    else faded = true;
                }
                gain *= Fade(cursor, backingFrom, backingFade);
                var voice = committed ? Ramp(cursor) : 0;
                voice *= Fade(cursor, vocalsEnd, vocalsFade);
                duck += ((ducked ? DuckGain : 1) - duck) * duckStep;
                double left = backing[cursor * 2], right = backing[cursor * 2 + 1];
                if (crossfadeLeft > 0)
                {
                    // The bar repeats: the old bar's tail fades out under the new one's start, so the jump doesn't click.
                    var angle = Math.PI / 2 * (1 - (double)crossfadeLeft / crossfade);
                    var old = Math.Min(crossfadeSource, audio.Frames - 1);
                    left = left * Math.Sin(angle) + backing[old * 2] * Math.Cos(angle);
                    right = right * Math.Sin(angle) + backing[old * 2 + 1] * Math.Cos(angle);
                    crossfadeSource++;
                    crossfadeLeft--;
                }
                double sung = vocals[cursor];
                stereo[count * 2] = Sample((left * gain + sung * voice) * duck);
                stereo[count * 2 + 1] = Sample((right * gain + sung * voice) * duck);
                VocalGain = voice;
                cursor++;
                rendered++;
            }
            CurrentDuck = duck;
            return count;
        }
    }

    /// <summary>Stops the song: a musical stop when <paramref name="musical"/> (the vocals finish the word heard at
    /// <paramref name="heard"/>, the backing rings to the next beat and fades over one), otherwise a quick 300 ms fade. A quick
    /// stop replaces a musical one that would end later; otherwise the first stop stands.</summary>
    public SongStopPlan Stop(TimeSpan heard, bool musical)
    {
        lock (gate)
        {
            var plan = SongTransport.PlanStop(map, envelope, heard, Time(cursor), musical, committed);
            if (stop is not null && (musical || stop.SilentAt <= plan.SilentAt)) return stop;
            stop = plan;
            vampPending = false;
            vocalsEnd = Frame(plan.VocalsEnd);
            vocalsFade = Frame(plan.VocalsFade);
            backingFrom = Frame(plan.BackingFrom);
            backingFade = Frame(plan.BackingFade);
            silent = Frame(plan.SilentAt);
            return plan;
        }
    }

    // The vocals open over 20 ms ending where the line's pre-roll starts (from the top they are simply on).
    private double Ramp(long at)
    {
        if (top || at >= vocalsFrom) return 1;
        var from = vocalsFrom - ramp;
        return at <= from ? 0 : Math.Sin(Math.PI / 2 * (at - from) / ramp);
    }

    // An equal-power fade-out over length frames from start (1 before it, 0 after).
    private static double Fade(long at, long start, long length)
    {
        if (at < start) return 1;
        if (length <= 0 || at >= start + length) return 0;
        return Math.Cos(Math.PI / 2 * (at - start) / length);
    }

    private static short Sample(double value) => (short)Math.Clamp(Math.Round(value), short.MinValue, short.MaxValue);
    private long Frame(TimeSpan time) => (long)Math.Round(time.TotalSeconds * rate);
    private TimeSpan Time(long frame) => TimeSpan.FromSeconds((double)frame / rate);

    public override string ToString() => nameof(SongMixer);
}
