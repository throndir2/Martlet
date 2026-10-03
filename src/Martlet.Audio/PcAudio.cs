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
/// or memory, and is never kept.</summary>
public sealed class PcAudioCaptureFactory(IPcAudioSourceFactory sources, TimeProvider? clock = null) : ICaptureDeviceFactory
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private int withoutMartlet = -1;
    private string? output;

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
        return new PcAudioDevice(source, time);
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
/// left empty once it is <see cref="Slack"/> overdue (a real packet never waits that long while something plays).</summary>
internal sealed class PcAudioDevice(IPcAudioSource source, TimeProvider clock) : ICaptureDevice
{
    internal static TimeSpan Slack => TimeSpan.FromMilliseconds(120);
    private long started, frames;
    private bool running;

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
        return new(bytes);
    }

    public void Stop()
    {
        running = false;
        source.Stop();
    }

    public void Dispose() => source.Dispose();
}
