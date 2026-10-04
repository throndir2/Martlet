using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using Martlet.Audio;
using Martlet.Core.Audio;

namespace Martlet.Desktop;

/// <summary>Fixture audio devices for verifying conversations through Martlet's MCP server on a disposable data directory, where
/// no microphone may be recorded and nothing may be played. FIXTURE, never a real device: <c>MARTLET_SIMULATE_MICROPHONE</c>
/// (a WAV file, or a folder of WAV files taken in name order; 16-bit PCM mono at 16, 24 or 48 kHz) makes conversations hear
/// each clip once, in real time, after a short lead-in, one per recording and at least <c>MARTLET_SIMULATE_MICROPHONE_GAP</c>
/// seconds (default 8) after the previous clip ended, and silence otherwise; <c>MARTLET_SIMULATE_SPEAKERS=1</c> makes replies play
/// into a silent sink that takes audio at real-time pace. Echo reduction and hearing what this PC plays are off while the
/// microphone is simulated (both would read the real speakers). Both are read once when Martlet starts and said in the log.</summary>
internal static class SimulatedAudio
{
    internal const string MicrophoneVariable = "MARTLET_SIMULATE_MICROPHONE";
    internal const string GapVariable = "MARTLET_SIMULATE_MICROPHONE_GAP";
    internal const string SpeakersVariable = "MARTLET_SIMULATE_SPEAKERS";

    /// <summary>The fixture microphone, or null when <c>MARTLET_SIMULATE_MICROPHONE</c> isn't set or has no usable clip.</summary>
    internal static SimulatedMicrophone? Microphone()
    {
        if (Environment.GetEnvironmentVariable(MicrophoneVariable) is not { Length: > 0 } path) return null;
        try
        {
            var files = Directory.Exists(path) ? Directory.GetFiles(path, "*.wav").Order(StringComparer.OrdinalIgnoreCase).ToArray() : [path];
            var clips = files.Select(Read).ToArray();
            if (clips.Length == 0 || clips.Any(clip => clip.Rate != clips[0].Rate))
            {
                ErrorLog.Warn($"{MicrophoneVariable}: no clips, or clips at different rates; the real microphone is used.");
                return null;
            }
            var gap = double.TryParse(Environment.GetEnvironmentVariable(GapVariable), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var seconds) && seconds is >= 0 and <= 600 ? seconds : 8;
            ErrorLog.Warn($"Simulated microphone ({MicrophoneVariable}, FIXTURE): conversations hear {clips.Length} recorded clip(s) " +
                $"({clips.Sum(clip => clip.Samples.Length) / (double)clips[0].Rate:0.0} s in all, {gap:0.#} s apart), never the microphone. " +
                "Echo reduction and hearing what this PC plays are off.");
            return new(clips, clips[0].Rate, TimeSpan.FromSeconds(gap));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            ErrorLog.Warn($"{MicrophoneVariable}: the clips can't be read ({error.Message}); the real microphone is used.");
            return null;
        }
    }

    /// <summary>The silent fixture speakers, or null unless <c>MARTLET_SIMULATE_SPEAKERS=1</c>.</summary>
    internal static SimulatedSpeakers? Speakers()
    {
        if (Environment.GetEnvironmentVariable(SpeakersVariable) != "1") return null;
        ErrorLog.Warn($"Simulated speakers ({SpeakersVariable}, FIXTURE): replies play into a silent sink at real-time pace; nothing is heard.");
        return new();
    }

    private static (short[] Samples, int Rate) Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 44 || !bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !bytes.AsSpan(8, 4).SequenceEqual("WAVE"u8))
            throw new InvalidDataException("not a WAV file");
        int at = 12, rate = 0, channels = 0, bits = 0;
        while (at + 8 <= bytes.Length)
        {
            var id = bytes.AsSpan(at, 4);
            var size = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at + 4));
            var body = at + 8;
            if (id.SequenceEqual("fmt "u8))
            {
                if (BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(body)) != 1) throw new InvalidDataException("not PCM");
                channels = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(body + 2));
                rate = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(body + 4));
                bits = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(body + 14));
            }
            else if (id.SequenceEqual("data"u8))
            {
                if (channels != 1 || bits != 16 || rate is not (16_000 or 24_000 or 48_000))
                    throw new InvalidDataException("not 16-bit PCM mono at 16, 24 or 48 kHz");
                var length = Math.Min(size, bytes.Length - body) / 2;
                var samples = new short[length];
                for (var i = 0; i < length; i++) samples[i] = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(body + i * 2));
                return (samples, rate);
            }
            at = body + size + (size & 1);
        }
        throw new InvalidDataException("no audio");
    }
}

/// <summary>FIXTURE microphone (<see cref="SimulatedAudio"/>): each recording opened hears at most one clip.</summary>
internal sealed class SimulatedMicrophone((short[] Samples, int Rate)[] clips, int rate, TimeSpan gap) : ICaptureDeviceFactory
{
    private static readonly TimeSpan LeadIn = TimeSpan.FromMilliseconds(400);
    private readonly object gate = new();
    private int next;
    private long dueAt;
    private int Rate => rate;

    public ICaptureDevice Open(CaptureDeviceAccess access, CancellationToken cancellationToken)
    {
        access.CheckAuthorization();
        return new Device(this);
    }

    // The next clip when it is due, else null; the one after it is due a gap after this one ends.
    private (short[] Clip, int Index)? Take()
    {
        lock (gate)
        {
            var now = Stopwatch.GetTimestamp();
            if (next >= clips.Length || now < dueAt) return null;
            var index = next++;
            var clip = clips[index].Samples;
            dueAt = now + (long)((clip.Length / (double)rate + gap.TotalSeconds) * Stopwatch.Frequency);
            ErrorLog.Info($"Simulated microphone (FIXTURE): clip {next} of {clips.Length} plays now ({clip.Length / (double)rate:0.00} s).");
            return (clip, index);
        }
    }

    // A recording that ended before its clip's voice was heard (listening restarted): the clip plays again on the next one.
    private void GiveBack(int index)
    {
        lock (gate)
        {
            if (next != index + 1) return;
            next = index;
            dueAt = 0;
        }
    }

    private sealed class Device(SimulatedMicrophone owner) : ICaptureDevice
    {
        private readonly int packet = owner.Rate / 100;
        private long started, position, clipAt;
        private short[]? clip;
        private int clipIndex;

        public CaptureSourceFormat Format => new(owner.Rate, 1, 16, DeviceSampleEncoding.IntegerPcm);
        public void Start(CancellationToken cancellationToken) => started = Stopwatch.GetTimestamp();
        public void Stop() { }

        public void Dispose()
        {
            // Listening restarted before the clip's voice could be heard (a listener's idle restart): it plays again on the next
            // recording. Once its voice was under way, what wasn't heard is lost, as with a real microphone.
            if (clip is not null && position - clipAt < owner.Rate * 3 / 10) owner.GiveBack(clipIndex);
            clip = null;
        }

        // 10 ms at a time, in real time, like a microphone.
        public CapturePacket Read(Span<byte> destination, CancellationToken cancellationToken)
        {
            var due = started + (long)((position + packet) / (double)owner.Rate * Stopwatch.Frequency);
            var wait = due - Stopwatch.GetTimestamp();
            if (wait > 0) cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(wait / (double)Stopwatch.Frequency));
            cancellationToken.ThrowIfCancellationRequested();
            if (clip is null && position >= owner.Rate * LeadIn.TotalSeconds && owner.Take() is { } taken)
            {
                (clip, clipIndex) = taken;
                clipAt = position;
            }
            for (var i = 0; i < packet; i++)
            {
                var index = position + i - clipAt;
                var sample = clip is not null && index >= 0 && index < clip.Length ? clip[index] : (short)0;
                BinaryPrimitives.WriteInt16LittleEndian(destination[(i * 2)..], sample);
            }
            position += packet;
            return new(packet * 2);
        }
    }
}

/// <summary>FIXTURE speakers (<see cref="SimulatedAudio"/>): a silent sink that takes audio at real-time pace and plays nothing.</summary>
internal sealed class SimulatedSpeakers : IPlaybackDeviceFactory
{
    public IPlaybackDevice Open(OutputSelection selection, PcmFormat format, CancellationToken cancellationToken) => new Sink(format);

    private sealed class Sink(PcmFormat format) : IPlaybackDevice
    {
        private readonly int capacity = format.SampleRate / 5;
        private long written, started;

        public PlaybackDeviceInfo Info => new(format.SampleRate, format.Channels, 16, DeviceSampleEncoding.IntegerPcm, capacity, false);

        // What was written and not yet "played" since the sink started.
        public int GetPadding(CancellationToken cancellationToken)
        {
            if (started == 0) return (int)Math.Min(written, capacity);
            var played = (long)((Stopwatch.GetTimestamp() - started) / (double)Stopwatch.Frequency * format.SampleRate);
            return (int)Math.Clamp(written - played, 0, capacity);
        }

        public int Write(ReadOnlySpan<byte> pcm, CancellationToken cancellationToken)
        {
            var room = capacity - GetPadding(cancellationToken);
            var frames = Math.Min(room, pcm.Length / format.BlockAlignment);
            if (frames <= 0) return 0;
            if (started != 0)
            {
                // Played out already: what is written now starts from the present.
                var played = (long)((Stopwatch.GetTimestamp() - started) / (double)Stopwatch.Frequency * format.SampleRate);
                if (written < played) written = played;
            }
            written += frames;
            return frames;
        }

        public void Start(CancellationToken cancellationToken) => started = Stopwatch.GetTimestamp();
        public void StopAndReset() { written = 0; started = 0; }
        public void Dispose() { }
    }
}
