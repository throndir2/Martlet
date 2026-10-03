namespace Martlet.Audio;

/// <summary>What made the microphone's sound in one 10 ms frame of an echo-reduced capture.</summary>
public enum HeardSource : byte
{
    /// <summary>Not known: no echo reduction, or the frame isn't cleaned yet.</summary>
    Unknown,
    /// <summary>The speakers played nothing: only the room (and whoever is in it) was heard.</summary>
    Room,
    /// <summary>The speakers played, but the echo canceller kept most of the sound: a voice the speakers don't explain.</summary>
    User,
    /// <summary>The echo canceller removed most of the sound: what this PC plays (Martlet's voice, a video), never the user.</summary>
    Speakers
}

/// <summary>Per 10 ms frame of one echo-reduced capture (<see cref="EchoReducer.For(string?, EchoTimeline?)"/>), what made the
/// microphone's sound: written on the capture's worker thread as each frame is cleaned and read by voice activity at the same
/// sample positions as the capture's 16 kHz PCM (frame k covers samples 160k to 160k+159). Metadata only, never audio.</summary>
public sealed class EchoTimeline
{
    /// <summary>A frame the canceller took more than this much away from (dB) was the speakers' sound. The canceller also turns
    /// the user's voice down a few dB while both talk, and the echo adds to what it heard, so the user's voice over the speakers
    /// stays well under this.</summary>
    public const double SpeakersRemovedDb = 10;
    private readonly byte[] frames;
    private int written;

    public EchoTimeline(TimeSpan duration)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero);
        frames = new byte[(int)Math.Ceiling(duration.TotalSeconds * 100) + 1];
    }

    /// <summary>Frames written so far.</summary>
    public int Frames => Volatile.Read(ref written);

    public HeardSource this[int frame] =>
        frame >= 0 && frame < Math.Min(Frames, frames.Length) ? (HeardSource)Volatile.Read(ref frames[frame]) : HeardSource.Unknown;

    /// <summary>Whether what this PC played explains these samples: every frame of them is <see cref="HeardSource.Speakers"/>
    /// (the canceller turns the user's voice down in some frames while both talk, so one such frame isn't enough).</summary>
    public bool Speakers(long startSample, int samples)
    {
        if (samples <= 0 || startSample < 0) return false;
        var first = (int)Math.Min(int.MaxValue, startSample / EchoReduction.FrameSamples);
        var last = (int)Math.Min(int.MaxValue, (startSample + samples - 1) / EchoReduction.FrameSamples);
        for (var frame = first; frame <= last; frame++)
            if (this[frame] != HeardSource.Speakers) return false;
        return true;
    }

    /// <summary>One cleaned frame: whether the speakers played within the echo's reach, and the microphone's energy before and
    /// after cleaning.</summary>
    internal void Add(bool speakersPlayed, double inputEnergy, double cleanedEnergy)
    {
        var source = !speakersPlayed ? HeardSource.Room
            : 10 * Math.Log10((inputEnergy + 1e-12) / (cleanedEnergy + 1e-12)) > SpeakersRemovedDb ? HeardSource.Speakers
            : HeardSource.User;
        var index = written;
        if (index < frames.Length) Volatile.Write(ref frames[index], (byte)source);
        Volatile.Write(ref written, index + 1);
    }
}

/// <summary>Whether the user is talking over Martlet, from the microphone alone and only once it is clearly a voice: at least
/// <see cref="Required"/> of loud frames, where a quiet stretch longer than <see cref="Gap"/> starts the count again. A
/// cough, a click, a short "mm-hmm" or a word of the room's chatter never gets there, and frames that are what this PC plays
/// (<see cref="HeardSource.Speakers"/>) never count. Fed one 20 ms voice-activity frame at a time.</summary>
public sealed class TalkOverDetector
{
    /// <summary>How much voice talking over Martlet takes before it stops.</summary>
    public static TimeSpan Required => TimeSpan.FromSeconds(1);
    /// <summary>A pause longer than this starts the count again.</summary>
    public static TimeSpan Gap => TimeSpan.FromMilliseconds(500);
    private const int FrameMilliseconds = 20;
    private static readonly int RequiredFrames = (int)(Required.TotalMilliseconds / FrameMilliseconds);
    private static readonly int GapFrames = (int)(Gap.TotalMilliseconds / FrameMilliseconds);
    private int voiced, quiet, processed;

    /// <summary>The user has talked over Martlet (stays true).</summary>
    public bool Sustained { get; private set; }
    /// <summary>Loud frames the speakers don't explain, and loud frames that were what this PC plays.</summary>
    public int UserFrames { get; private set; }
    public int SpeakerFrames { get; private set; }
    /// <summary>Of the current stretch, how much was a voice.</summary>
    public TimeSpan Voice => TimeSpan.FromMilliseconds(voiced * FrameMilliseconds);
    /// <summary>The frame (counted from the first one processed) where the current stretch of voice began, or -1.</summary>
    public int StretchStartFrame { get; private set; } = -1;

    /// <summary>One 20 ms frame: whether it was loud enough for a voice, and whether what this PC plays explains it.
    /// Returns <see cref="Sustained"/>.</summary>
    public bool Process(bool loud, bool speakers)
    {
        if (loud && speakers) SpeakerFrames++;
        if (loud && !speakers)
        {
            UserFrames++;
            if (voiced == 0) StretchStartFrame = processed;
            voiced++;
            quiet = 0;
        }
        else if (++quiet > GapFrames)
        {
            voiced = 0;
            StretchStartFrame = -1;
        }
        processed++;
        if (voiced >= RequiredFrames) Sustained = true;
        return Sustained;
    }
}
