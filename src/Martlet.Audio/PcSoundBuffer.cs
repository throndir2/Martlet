using System.Buffers.Binary;

namespace Martlet.Audio;

/// <summary>The last few seconds of what this PC plays, for the sound digest (Companion › Listening › Describe PC sounds): 16 kHz
/// mono, in memory only, never saved, logged or sent anywhere but the sound judge. It fills only while <see cref="Recording"/>
/// is on (Hear what this PC plays and Describe PC sounds are both on and always listening hears the PC); turning it off clears
/// it. Thread-safe: the PC capture's worker appends while the digest reads.</summary>
public sealed class PcSoundBuffer
{
    public const int SampleRate = 16_000;
    /// <summary>How much the buffer keeps by default.</summary>
    public static TimeSpan DefaultCapacity => TimeSpan.FromSeconds(15);
    private readonly object gate = new();
    private readonly short[] ring;
    private readonly TimeProvider clock;
    private long written, lastAppendedAt;
    private bool recording;

    public PcSoundBuffer(TimeSpan? capacity = null, TimeProvider? clock = null)
    {
        var seconds = (capacity ?? DefaultCapacity).TotalSeconds;
        if (!double.IsFinite(seconds) || seconds < 1 || seconds > 60) throw new ArgumentOutOfRangeException(nameof(capacity));
        ring = new short[(int)(seconds * SampleRate)];
        this.clock = clock ?? TimeProvider.System;
    }

    /// <summary>The buffer's clock (the controller's).</summary>
    public TimeProvider Clock => clock;

    /// <summary>How long the buffer can be.</summary>
    public TimeSpan Capacity => TimeSpan.FromSeconds(ring.Length / (double)SampleRate);

    /// <summary>Whether what the PC plays is kept now. Off clears what was kept.</summary>
    public bool Recording
    {
        get { lock (gate) return recording; }
        set
        {
            lock (gate)
            {
                if (recording == value) return;
                recording = value;
                if (!value) ClearLocked();
            }
        }
    }

    /// <summary>The clock timestamp of the newest sound kept (0: none).</summary>
    public long LastAppendedAt { get { lock (gate) return lastAppendedAt; } }

    /// <summary>How much sound is kept now.</summary>
    public TimeSpan Buffered { get { lock (gate) return TimeSpan.FromSeconds(Math.Min(written, ring.Length) / (double)SampleRate); } }

    /// <summary>Adds 16 kHz mono 16-bit little-endian PCM (what <see cref="CaptureNormalizer"/> makes); ignored while not
    /// <see cref="Recording"/>.</summary>
    public void Append(ReadOnlySpan<byte> pcm)
    {
        lock (gate)
        {
            if (!recording) return;
            var samples = pcm.Length / 2;
            for (var i = 0; i < samples; i++)
                ring[(written + i) % ring.Length] = BinaryPrimitives.ReadInt16LittleEndian(pcm.Slice(i * 2, 2));
            written += samples;
            if (samples > 0) lastAppendedAt = clock.GetTimestamp();
        }
    }

    /// <summary>The newest sound kept, at most <paramref name="length"/> long and none of it from before the clock timestamp
    /// <paramref name="notBefore"/> (0: no limit), as samples from -1 to 1. Empty when nothing is kept.</summary>
    public float[] Latest(TimeSpan length, long notBefore = 0)
    {
        lock (gate)
        {
            var count = (long)Math.Min(Math.Min(written, ring.Length), Math.Max(0, length.TotalSeconds) * SampleRate);
            if (notBefore > 0 && lastAppendedAt > 0)
            {
                var since = (lastAppendedAt - notBefore) / (double)clock.TimestampFrequency;
                count = Math.Min(count, Math.Max(0, (long)(since * SampleRate)));
            }
            var clip = new float[count];
            for (var i = 0; i < count; i++) clip[i] = ring[(written - count + i) % ring.Length] / 32768f;
            return clip;
        }
    }

    /// <summary>Forgets everything kept.</summary>
    public void Clear()
    {
        lock (gate) ClearLocked();
    }

    private void ClearLocked()
    {
        Array.Clear(ring);
        written = 0;
        lastAppendedAt = 0;
    }
}
