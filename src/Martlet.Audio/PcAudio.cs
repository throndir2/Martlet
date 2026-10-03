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

    /// <summary>Whether the last opened source left Martlet's own sound out; null until one opened.</summary>
    public bool? WithoutMartlet => Volatile.Read(ref withoutMartlet) switch { 0 => false, 1 => true, _ => null };

    public ICaptureDevice Open(CaptureDeviceAccess access, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        access.CheckAuthorization();
        cancellationToken.ThrowIfCancellationRequested();
        var source = sources.Open(cancellationToken);
        Volatile.Write(ref withoutMartlet, source.WithoutMartlet ? 1 : 0);
        return new PcAudioDevice(source, time);
    }
}

/// <summary>Whether a line the microphone heard is only the speakers playing what the PC played. Echo reduction leaves a trace
/// of a loud video in the room's sound and speech-to-text can still make words of it, so with Hear what this PC plays on the
/// same words would show twice: once as the PC's and once as the user's. It is an echo when most of its words
/// (<see cref="Share"/>), in order, are among the words the PC listener heard at about the same time. Words only (case,
/// punctuation and apostrophes ignored; each Chinese, Japanese or Korean character is a word); nothing is kept.</summary>
public static class PcEcho
{
    public const double Share = 0.6;
    private const int MaximumHeard = 200;
    private const int MaximumPlayed = 1500;

    public static bool Of(string heard, IEnumerable<string> played)
    {
        ArgumentNullException.ThrowIfNull(heard);
        ArgumentNullException.ThrowIfNull(played);
        var words = Words(heard);
        if (words.Count == 0 || words.Count > MaximumHeard) return false;
        var reference = new List<string>();
        foreach (var line in played) reference.AddRange(Words(line));
        if (reference.Count == 0) return false;
        if (reference.Count > MaximumPlayed) reference.RemoveRange(0, reference.Count - MaximumPlayed);
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

    // The longest run of the heard words found in order (not necessarily together) among the played ones.
    private static int Common(List<string> heard, List<string> played)
    {
        var previous = new int[played.Count + 1];
        var current = new int[played.Count + 1];
        foreach (var word in heard)
        {
            for (var j = 1; j <= played.Count; j++)
                current[j] = string.Equals(word, played[j - 1], StringComparison.Ordinal)
                    ? previous[j - 1] + 1 : Math.Max(previous[j], current[j - 1]);
            (previous, current) = (current, previous);
        }
        return previous[played.Count];
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
