using System.Buffers.Binary;

namespace Martlet.Audio;

/// <summary>The last few seconds of what this PC plays (for the sound digest, Companion › Listening › Describe PC sounds, and for
/// the owner's check-ins that ask for a recording) or of the microphone (for those check-ins only): 16 kHz mono, in memory only,
/// never saved or logged, and sent nowhere but the sound judge or the Thinking pool member that takes such a check-in. It fills
/// only while it <see cref="Keeps"/> sound: <see cref="Recording"/> (the digest's) or <see cref="Wanted"/> (a check-in's) is on;
/// turning both off clears it. Thread-safe: the capture's worker appends while the digest or a check-in reads.</summary>
public sealed class PcSoundBuffer
{
    public const int SampleRate = 16_000;
    /// <summary>How much the buffer keeps by default.</summary>
    public static TimeSpan DefaultCapacity => TimeSpan.FromSeconds(15);
    /// <summary>A stretch without new sound longer than this is a pause: <see cref="Recent"/> never reaches back past it.</summary>
    public static TimeSpan Pause => TimeSpan.FromSeconds(2);
    private readonly object gate = new();
    private readonly short[] ring;
    private readonly TimeProvider clock;
    private long written, lastAppendedAt, heardSince;
    private bool recording, wanted;

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

    /// <summary>Whether the sound digest keeps sound now (Hear what this PC plays and Describe PC sounds are both on and always
    /// listening hears the PC). Off clears what was kept, unless <see cref="Wanted"/>.</summary>
    public bool Recording
    {
        get { lock (gate) return recording; }
        set
        {
            lock (gate)
            {
                if (recording == value) return;
                recording = value;
                if (!value && !wanted) ClearLocked();
            }
        }
    }

    /// <summary>Whether one of the owner's check-ins that is on asks for this sound. Off clears what was kept, unless
    /// <see cref="Recording"/>.</summary>
    public bool Wanted
    {
        get { lock (gate) return wanted; }
        set
        {
            lock (gate)
            {
                if (wanted == value) return;
                wanted = value;
                if (!value && !recording) ClearLocked();
            }
        }
    }

    /// <summary>Whether sound is kept now: <see cref="Recording"/> or <see cref="Wanted"/>.</summary>
    public bool Keeps { get { lock (gate) return recording || wanted; } }

    /// <summary>The clock timestamp of the newest sound kept (0: none).</summary>
    public long LastAppendedAt { get { lock (gate) return lastAppendedAt; } }

    /// <summary>How much sound is kept now.</summary>
    public TimeSpan Buffered { get { lock (gate) return TimeSpan.FromSeconds(Math.Min(written, ring.Length) / (double)SampleRate); } }

    /// <summary>Adds 16 kHz mono 16-bit little-endian PCM (what <see cref="CaptureNormalizer"/> makes); ignored while it doesn't
    /// <see cref="Keeps"/> sound.</summary>
    public void Append(ReadOnlySpan<byte> pcm)
    {
        lock (gate)
        {
            if (!recording && !wanted) return;
            var samples = pcm.Length / 2;
            for (var i = 0; i < samples; i++)
                ring[(written + i) % ring.Length] = BinaryPrimitives.ReadInt16LittleEndian(pcm.Slice(i * 2, 2));
            written += samples;
            if (samples == 0) return;
            var now = clock.GetTimestamp();
            if (lastAppendedAt == 0 || now - lastAppendedAt > Pause.TotalSeconds * clock.TimestampFrequency)
                heardSince = now - (long)(samples / (double)SampleRate * clock.TimestampFrequency);
            lastAppendedAt = now;
        }
    }

    /// <summary>The newest sound kept, at most <paramref name="length"/> long and none of it from before the clock timestamp
    /// <paramref name="notBefore"/> (0: no limit), as samples from -1 to 1. Empty when nothing is kept.</summary>
    public float[] Latest(TimeSpan length, long notBefore = 0)
    {
        lock (gate)
        {
            var count = CountLocked(length, notBefore);
            var clip = new float[count];
            for (var i = 0; i < count; i++) clip[i] = ring[(written - count + i) % ring.Length] / 32768f;
            return clip;
        }
    }

    /// <summary>The newest sound kept, at most <paramref name="length"/> long and never from before a <see cref="Pause"/>, as
    /// 16 kHz mono 16-bit little-endian PCM. Empty when nothing new was kept within <paramref name="fresh"/> (the capture
    /// stopped), so a check-in never gets old sound.</summary>
    public byte[] Recent(TimeSpan length, TimeSpan fresh)
    {
        lock (gate)
        {
            if (lastAppendedAt == 0 || clock.GetElapsedTime(lastAppendedAt) > fresh) return [];
            var count = CountLocked(length, heardSince);
            var pcm = new byte[count * 2];
            for (var i = 0; i < count; i++)
                BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2, 2), ring[(written - count + i) % ring.Length]);
            return pcm;
        }
    }

    /// <summary>Whether new sound was kept within <paramref name="fresh"/>.</summary>
    public bool Hears(TimeSpan fresh)
    {
        lock (gate) return lastAppendedAt != 0 && clock.GetElapsedTime(lastAppendedAt) <= fresh;
    }

    private long CountLocked(TimeSpan length, long notBefore)
    {
        var count = (long)Math.Min(Math.Min(written, ring.Length), Math.Max(0, length.TotalSeconds) * SampleRate);
        if (notBefore > 0 && lastAppendedAt > 0)
        {
            var since = (lastAppendedAt - notBefore) / (double)clock.TimestampFrequency;
            count = Math.Min(count, Math.Max(0, (long)(since * SampleRate)));
        }
        return count;
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
        heardSince = 0;
    }
}
