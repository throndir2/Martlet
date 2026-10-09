using System.Globalization;
using System.Text;
using Martlet.Core.Contracts;

namespace Martlet.Audio;

/// <summary>What this PC plays, as a loopback (see <see cref="IEchoReference"/>: opened, read, stopped and disposed on the
/// capture's own worker thread; Read never blocks and returns nothing while nothing plays). <see cref="WithoutMartlet"/>: Martlet's
/// own sound (its voice) is left out, so listening to the PC never hears Martlet itself.</summary>
public interface IPcAudioSource : IEchoReference
{
    bool WithoutMartlet { get; }
    /// <summary>The one output it hears (what plays there, Martlet's own voice included), or null when it hears every app's
    /// sound except Martlet's.</summary>
    string? Output => null;
}

public interface IPcAudioSourceFactory
{
    IPcAudioSource Open(CancellationToken cancellationToken);
}

/// <summary>Companion › Listening › Hear what this PC plays: what the PC plays (a video, a stream, a call, a game) as a capture
/// device for always listening, beside the microphone. A loopback delivers nothing while nothing plays, so the gaps are filled
/// with silence on the clock: the stream stays continuous and voice activity hears a video pause or go quiet as a pause, which
/// ends the utterance. The audio only goes where the microphone's would (speech-to-text), never to Voice ID, voice recognition
/// or memory, and is never kept. With <paramref name="sound"/>, the last seconds of it also stay in memory while that buffer
/// <see cref="PcSoundBuffer.Keeps"/> sound: for the sound digest and for the owner's check-ins that ask for a recording of it.</summary>
public sealed class PcAudioCaptureFactory(IPcAudioSourceFactory sources, TimeProvider? clock = null, PcSoundBuffer? sound = null)
    : ICaptureDeviceFactory
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private int withoutMartlet = -1;
    private string? output;

    /// <summary>The last seconds of what the PC played, for the sound digest; null without one.</summary>
    public PcSoundBuffer? Sound => sound;

    /// <summary>Whether the last opened source left Martlet's own sound out; null until one opened.</summary>
    public bool? WithoutMartlet => Volatile.Read(ref withoutMartlet) switch { 0 => false, 1 => true, _ => null };

    /// <summary>The one output the last opened source heard, or null when it heard every app's sound except Martlet's.</summary>
    public string? Output => Volatile.Read(ref output);

    public ICaptureDevice Open(CaptureDeviceAccess access, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        access.CheckAuthorization();
        cancellationToken.ThrowIfCancellationRequested();
        var source = sources.Open(cancellationToken);
        Volatile.Write(ref output, source.Output);
        Volatile.Write(ref withoutMartlet, source.WithoutMartlet ? 1 : 0);
        return new PcAudioDevice(source, time, sound);
    }
}

/// <summary>Your own voice played back on this PC: a voice changer's or headset app's "hear myself" (Voicemod, NVIDIA
/// Broadcast), Windows' "Listen to this device" or a call that echoes you puts what the microphone hears into what the PC plays,
/// so with Hear what this PC plays on the same words would show twice, once as yours and once as the PC's, and Martlet would
/// answer them twice. A line the PC played is your own voice when most of its words (<see cref="Share"/>), in order, are among
/// the words the microphone heard you say at about the same time; it is left out and your own line is kept. Words only (case,
/// punctuation and apostrophes ignored; each Chinese, Japanese or Korean character is a word); nothing is kept.</summary>
public static class PcEcho
{
    /// <summary>At least this share of a line's words, in order, must be among what was said.</summary>
    public const double Share = 0.6;
    // Bounds the comparison: a line is at most a few hundred words, and only the newest of what was said matters.
    private const int MaximumLine = 600;
    private const int MaximumSaid = 1500;

    /// <summary>The line (what the PC played) mostly repeats, in order, the words of what was said (what the microphone heard
    /// you say lately).</summary>
    public static bool Repeats(string line, IEnumerable<string> said)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(said);
        var words = Words(line);
        if (words.Count == 0) return false;
        if (words.Count > MaximumLine) words.RemoveRange(0, words.Count - MaximumLine);
        var reference = new List<string>();
        foreach (var text in said) reference.AddRange(Words(text));
        if (reference.Count == 0) return false;
        if (reference.Count > MaximumSaid) reference.RemoveRange(0, reference.Count - MaximumSaid);
        return Common(words, reference) >= Math.Ceiling(words.Count * Share);
    }

    /// <summary>A line the microphone heard needs at least this many words to count as this PC's speakers.</summary>
    public const int SpeakersWords = 4;
    /// <summary>At least this share of what the microphone heard, in order and close together, must be in one line the PC
    /// played for it to count as the speakers (stricter than <see cref="Share"/>: leaving out your own words is worse than
    /// answering the speakers).</summary>
    public const double SpeakersShare = 0.7;
    /// <summary>How far apart in time the microphone may hear words and the PC play them (plus 15% of the PC line's length, as
    /// its words are placed in it by an even pace of speech) and still be the same sound.</summary>
    public static TimeSpan SpeakersSlack { get; } = TimeSpan.FromSeconds(1.5);
    private const int MaximumHeard = 60;

    /// <summary>The microphone heard this PC's speakers, not the user: at least <see cref="SpeakersShare"/> of the words of
    /// <paramref name="heard"/> (at least <see cref="SpeakersWords"/> of them) are, in order and close together, in a line a
    /// video, show, game or music on this PC played at that same moment. <paramref name="played"/> are the lines Hear what this
    /// PC plays heard lately, oldest first; only a line during which every app that made sound was a video, show, game or music
    /// counts (<see cref="PcHeardFrom.MediaOnly"/>: never a voice chat, a call or an app that may play the user's own voice
    /// back). <paramref name="start"/> and <paramref name="end"/> are when the microphone's voice began and its recording ended,
    /// on the same clock as the lines' (<paramref name="frequency"/> ticks a second); where the words sit in the PC's line, at
    /// an even pace, must be within <see cref="SpeakersSlack"/> of that, so a phrase the video said earlier never counts. Returns
    /// where the newest such line came from, or null (also when a time is unknown).</summary>
    public static PcHeardFrom? Speakers(string heard, long start, long end, IEnumerable<PcPlayedLine> played, long frequency)
    {
        ArgumentNullException.ThrowIfNull(heard);
        ArgumentNullException.ThrowIfNull(played);
        if (start <= 0 || end < start || frequency <= 0) return null;
        var words = Words(heard);
        if (words.Count < SpeakersWords) return null;
        if (words.Count > MaximumHeard) words.RemoveRange(0, words.Count - MaximumHeard);
        var need = (int)Math.Ceiling(words.Count * SpeakersShare);
        var slack = (long)(SpeakersSlack.TotalSeconds * frequency);
        PcHeardFrom? found = null;
        foreach (var line in played)
        {
            if (!line.From.MediaOnly || line.Start <= 0 || line.End < line.Start || start > line.End + slack || end < line.Start - slack)
                continue;
            var said = Words(line.Text);
            if (said.Count > MaximumLine) said.RemoveRange(0, said.Count - MaximumLine);
            var duration = line.End - line.Start;
            var room = slack + duration * 15 / 100;
            foreach (var at in Near(words, said, need))
            {
                var from = line.Start + duration * at / said.Count;
                var to = line.Start + duration * Math.Min(said.Count, at + words.Count) / said.Count;
                if (start > to + room || end < from - room) continue;
                found = line.From;
                break;
            }
        }
        return found;
    }

    // Where in `line` at least `need` of `words` are found in order within one stretch a few words longer than them, so common
    // words scattered through a long video never count.
    private static IEnumerable<int> Near(List<string> words, List<string> line, int need)
    {
        var span = words.Count + 3;
        var firsts = new HashSet<string>(words.Take(words.Count - need + 1), StringComparer.Ordinal);
        for (var i = 0; i < line.Count; i++)
            if (firsts.Contains(line[i]) && Common(words, line.GetRange(i, Math.Min(span, line.Count - i))) >= need) yield return i;
    }

    internal static List<string> Words(string text)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        foreach (var c in text)
        {
            if (c >= '\u2E80' && char.GetUnicodeCategory(c) == UnicodeCategory.OtherLetter)
            {
                Flush();
                words.Add(c.ToString());
            }
            else if (char.IsLetterOrDigit(c)) word.Append(char.ToLowerInvariant(c));
            else if (c is not ('\'' or '\u2019') || word.Length == 0) Flush();
        }
        Flush();
        return words;

        void Flush()
        {
            if (word.Length == 0) return;
            words.Add(word.ToString());
            word.Clear();
        }
    }

    // How many of the line's words are found in order (not necessarily together) among the said ones.
    private static int Common(List<string> line, List<string> said)
    {
        var previous = new int[said.Count + 1];
        var current = new int[said.Count + 1];
        foreach (var word in line)
        {
            for (var j = 1; j <= said.Count; j++)
                current[j] = string.Equals(word, said[j - 1], StringComparison.Ordinal)
                    ? previous[j - 1] + 1 : Math.Max(previous[j], current[j - 1]);
            (previous, current) = (current, previous);
        }
        return previous[said.Count];
    }
}

/// <summary>One continuous stream of what the PC plays: real packets as they come, and silence for any stretch the loopback
/// left empty once it is <see cref="Slack"/> overdue (a real packet never waits that long while something plays). With a sound
/// buffer that keeps sound, each packet is also normalized to 16 kHz mono and kept there (a problem there never stops listening).</summary>
internal sealed class PcAudioDevice(IPcAudioSource source, TimeProvider clock, PcSoundBuffer? sound = null) : ICaptureDevice
{
    internal static TimeSpan Slack => TimeSpan.FromMilliseconds(120);
    private long started, frames;
    private bool running;
    private readonly SoundKeeper keeper = new(sound);

    public CaptureSourceFormat Format => source.Format;

    public void Start(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        source.Start();
        started = clock.GetTimestamp();
        running = true;
    }

    public CapturePacket Read(Span<byte> destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!running) return new(0);
        var format = source.Format;
        var packet = source.Read(destination);
        if (packet.ByteCount < 0 || packet.ByteCount > destination.Length || packet.ByteCount % format.BlockAlignment != 0)
            throw new CaptureDeviceException(ErrorCode.PayloadTooLarge);
        // A gap the loopback reports is already filled with silence by time, so the stream never looks truncated.
        if (packet.ByteCount > 0)
        {
            frames += packet.ByteCount / format.BlockAlignment;
            keeper.Keep(destination[..packet.ByteCount], format);
            return new(packet.ByteCount);
        }
        var elapsed = clock.GetElapsedTime(started) - Slack;
        if (elapsed <= TimeSpan.Zero) return new(0);
        var due = (long)(elapsed.Ticks * (double)format.SampleRate / TimeSpan.TicksPerSecond);
        var missing = due - frames;
        if (missing <= 0) return new(0);
        var count = (int)Math.Min(missing, Math.Min(format.SampleRate / 10, destination.Length / format.BlockAlignment));
        var bytes = count * format.BlockAlignment;
        destination[..bytes].Clear();
        frames += count;
        keeper.Keep(destination[..bytes], format);
        return new(bytes);
    }

    public void Stop()
    {
        running = false;
        source.Stop();
        keeper.Forget();
    }

    public void Dispose()
    {
        keeper.Forget();
        source.Dispose();
    }
}

/// <summary>Keeps a copy of what a capture reads in a sound buffer while it <see cref="PcSoundBuffer.Keeps"/> sound, normalized
/// to 16 kHz mono; a problem there never stops the capture. The normalizer goes when the buffer stops keeping or the capture
/// stops, so a new one starts clean. Used on the capture's own worker only.</summary>
internal sealed class SoundKeeper(PcSoundBuffer? sound)
{
    private CaptureNormalizer? normalizer;
    private byte[]? normalized;

    internal void Keep(ReadOnlySpan<byte> packet, CaptureSourceFormat format)
    {
        if (sound is null) return;
        if (!sound.Keeps)
        {
            Forget();
            return;
        }
        try
        {
            normalizer ??= new CaptureNormalizer(format);
            var step = format.MaximumPacketBytes / format.BlockAlignment * format.BlockAlignment;
            while (!packet.IsEmpty)
            {
                var chunk = packet[..Math.Min(step, packet.Length)];
                var size = normalizer.MaximumOutputBytes(chunk.Length);
                if (normalized is null || normalized.Length < size) normalized = new byte[size];
                var written = normalizer.Convert(chunk, normalized);
                sound.Append(normalized.AsSpan(0, written));
                packet = packet[chunk.Length..];
            }
        }
        catch (Exception error) when (error is CaptureDeviceException or ArgumentException) { Forget(); }
    }

    internal void Forget()
    {
        normalizer?.Dispose();
        normalizer = null;
        if (normalized is not null) Array.Clear(normalized);
    }
}

/// <summary>A microphone that also keeps its last seconds in <see cref="Sound"/> (normalized to 16 kHz mono) while that buffer
/// <see cref="PcSoundBuffer.Keeps"/> sound: while one of the owner's check-ins that is on asks for the microphone. What the
/// devices read passes through unchanged. The kept seconds stay in memory, are never saved or logged, and go only with that
/// check-in to the Thinking pool member that takes it.</summary>
public sealed class KeptCaptureDeviceFactory(ICaptureDeviceFactory inner, PcSoundBuffer sound) : ICaptureDeviceFactory
{
    /// <summary>The microphone's last seconds.</summary>
    public PcSoundBuffer Sound => sound;
    /// <summary>The microphone itself.</summary>
    public ICaptureDeviceFactory Inner => inner;

    public ICaptureDevice Open(CaptureDeviceAccess access, CancellationToken cancellationToken) =>
        new KeptCaptureDevice(inner.Open(access, cancellationToken), sound);
}

internal sealed class KeptCaptureDevice(ICaptureDevice inner, PcSoundBuffer sound) : ICaptureDevice
{
    private readonly SoundKeeper keeper = new(sound);

    public CaptureSourceFormat Format => inner.Format;

    public void Start(CancellationToken cancellationToken) => inner.Start(cancellationToken);

    public CapturePacket Read(Span<byte> destination, CancellationToken cancellationToken)
    {
        var packet = inner.Read(destination, cancellationToken);
        if (packet.ByteCount > 0 && packet.ByteCount <= destination.Length && packet.ByteCount % inner.Format.BlockAlignment == 0)
            keeper.Keep(destination[..packet.ByteCount], inner.Format);
        return packet;
    }

    public void Stop()
    {
        inner.Stop();
        keeper.Forget();
    }

    public void Dispose()
    {
        keeper.Forget();
        inner.Dispose();
    }
}
