namespace Martlet.Discord;

/// <summary>Puts one speaker's RTP packets back in order. Discord voice is UDP: packets can arrive late, out of order or not at
/// all. Frames are released in sequence; a frame still missing once <see cref="Depth"/> later ones have arrived is given up as
/// lost (released as null so the decoder conceals it). Sequence numbers wrap at 65536.</summary>
public sealed class DiscordPacketOrder(int depth = 2)
{
    private const int MaximumConcealed = 5;
    private readonly SortedDictionary<int, byte[]> waiting = [];
    private ushort next;
    private bool started;

    public int Depth { get; } = depth;
    public int Lost { get; private set; }

    /// <summary>Adds a packet and appends every frame now ready, in order, to <paramref name="ready"/> (null for a lost one).</summary>
    public void Push(ushort sequence, byte[] frame, List<byte[]?> ready)
    {
        if (!started) { next = sequence; started = true; }
        var ahead = (short)(sequence - next);
        if (ahead < 0) return; // late or duplicate: already played past it
        waiting.TryAdd(ahead, frame);
        Release(ready);
        if (waiting.Count <= Depth) return;
        // A gap that later packets have overtaken: conceal a short loss, skip a long one.
        var gap = waiting.Keys.First();
        if (gap <= MaximumConcealed)
            for (var i = 0; i < gap; i++) { ready.Add(null); Lost++; }
        Advance(gap);
        Release(ready);
    }

    /// <summary>Releases everything still waiting (the speaker stopped), concealing nothing.</summary>
    public void Flush(List<byte[]?> ready)
    {
        while (waiting.Count > 0)
        {
            Advance(waiting.Keys.First());
            Release(ready);
        }
    }

    private void Release(List<byte[]?> ready)
    {
        while (waiting.Remove(0, out var frame))
        {
            ready.Add(frame);
            Advance(1);
        }
    }

    private void Advance(int count)
    {
        if (count == 0) return;
        next = unchecked((ushort)(next + count));
        var shifted = waiting.ToArray();
        waiting.Clear();
        foreach (var (key, value) in shifted) waiting[key - count] = value;
    }
}

public sealed record DiscordEndpointOptions
{
    /// <summary>Mean amplitude (PCM16 RMS) above which a 20 ms frame counts as voice. Discord clients already suppress noise
    /// and stop sending in silence, so this only separates speech from their comfort noise and breaths.</summary>
    public double VoiceLevel { get; init; } = 350;
    /// <summary>Silence (or no packets) that ends an utterance.</summary>
    public TimeSpan EndSilence { get; init; } = TimeSpan.FromMilliseconds(700);
    /// <summary>Less voice than this is a cough or a click, not words.</summary>
    public TimeSpan MinimumVoice { get; init; } = TimeSpan.FromMilliseconds(250);
    /// <summary>Longest utterance kept for speech-to-text (Martlet's capture bound).</summary>
    public TimeSpan MaximumUtterance { get; init; } = TimeSpan.FromSeconds(25);
    /// <summary>Audio kept from before the first voiced frame so the first syllable isn't clipped.</summary>
    public TimeSpan PreRoll { get; init; } = TimeSpan.FromMilliseconds(200);
    /// <summary>Voice that counts as talking over Martlet (real words, not a short sound).</summary>
    public TimeSpan BargeInVoice { get; init; } = TimeSpan.FromMilliseconds(400);
}

/// <summary>Finds one speaker's utterances in their 16 kHz mono audio: it starts at the first voiced frame (with a short
/// pre-roll) and ends after <see cref="DiscordEndpointOptions.EndSilence"/> of silence or of no packets at all (Discord clients
/// stop sending when their own voice detection hears nothing).</summary>
public sealed class DiscordSpeakerEndpointer(DiscordEndpointOptions options)
{
    private const int Frame = DiscordVoiceAudio.SpeechFrameSamples;
    private readonly List<short> speech = [];
    private readonly Queue<short[]> preRoll = new();
    private int silentFrames, voicedFrames;
    private DateTimeOffset lastAudio;

    public bool Active { get; private set; }
    /// <summary>Voice heard so far in the current utterance.</summary>
    public TimeSpan Voiced => TimeSpan.FromMilliseconds(voicedFrames * DiscordVoiceAudio.FrameMilliseconds);
    /// <summary>When this speaker's last voiced frame arrived (for barge-in: still talking now, not a minute ago).</summary>
    public DateTimeOffset LastVoice { get; private set; }

    /// <summary>Adds 16 kHz mono samples heard at <paramref name="now"/>; returns a finished utterance, or null.</summary>
    public short[]? Append(ReadOnlySpan<short> samples, DateTimeOffset now)
    {
        lastAudio = now;
        short[]? finished = null;
        for (var offset = 0; offset < samples.Length; offset += Frame)
        {
            var frame = samples.Slice(offset, Math.Min(Frame, samples.Length - offset));
            var voiced = Level(frame) >= options.VoiceLevel;
            if (!Active)
            {
                if (!voiced)
                {
                    preRoll.Enqueue(frame.ToArray());
                    while (preRoll.Count * DiscordVoiceAudio.FrameMilliseconds > options.PreRoll.TotalMilliseconds) preRoll.Dequeue();
                    continue;
                }
                Active = true;
                foreach (var kept in preRoll) speech.AddRange(kept);
                preRoll.Clear();
            }
            speech.AddRange(frame);
            if (voiced) { voicedFrames++; silentFrames = 0; LastVoice = now; }
            else silentFrames++;
            if (silentFrames * DiscordVoiceAudio.FrameMilliseconds >= options.EndSilence.TotalMilliseconds ||
                speech.Count >= options.MaximumUtterance.TotalSeconds * DiscordVoiceAudio.SpeechRate)
                finished ??= End();
        }
        return finished;
    }

    /// <summary>Ends an utterance whose speaker stopped sending packets; returns it, or null.</summary>
    public short[]? Tick(DateTimeOffset now) =>
        Active && now - lastAudio >= options.EndSilence ? End() : null;

    /// <summary>Ends whatever is being heard now (the speaker left).</summary>
    public short[]? Flush() => Active ? End() : null;

    private short[]? End()
    {
        var enough = Voiced >= options.MinimumVoice;
        var utterance = enough ? speech.ToArray() : null;
        speech.Clear();
        preRoll.Clear();
        Active = false;
        silentFrames = voicedFrames = 0;
        return utterance;
    }

    internal static double Level(ReadOnlySpan<short> frame)
    {
        if (frame.IsEmpty) return 0;
        double sum = 0;
        foreach (var sample in frame) sum += sample * (double)sample;
        return Math.Sqrt(sum / frame.Length);
    }
}

/// <summary>One finished utterance: who said it and their 16 kHz mono PCM16.</summary>
public sealed record DiscordHeard(ulong UserId, short[] Speech)
{
    public TimeSpan Duration => TimeSpan.FromSeconds(Speech.Length / (double)DiscordVoiceAudio.SpeechRate);
}

/// <summary>Everyone Martlet hears in one voice channel: per SSRC it orders packets, decodes Opus (concealing lost frames),
/// converts to 16 kHz mono and finds utterances. Discord identifies a sender only by SSRC; <paramref name="userOf"/> maps it to
/// a user (from the voice connection's Speaking events). Not thread-safe: one caller at a time.</summary>
public sealed class DiscordVoiceListener(Func<uint, ulong?> userOf, DiscordEndpointOptions? options = null)
{
    private readonly DiscordEndpointOptions options = options ?? new();
    private readonly Dictionary<uint, Speaker> speakers = [];
    private readonly List<byte[]?> ready = [];

    /// <summary>Distinct users heard speaking since this connection began.</summary>
    public IReadOnlyCollection<ulong> Heard => heard;
    private readonly HashSet<ulong> heard = [];
    public int LostFrames => speakers.Values.Sum(speaker => speaker.Order.Lost);
    /// <summary>Someone is mid-utterance (not yet ended by silence).</summary>
    public bool Hearing => speakers.Values.Any(speaker => speaker.Endpointer.Active);

    /// <summary>Takes one received Opus packet; returns any utterances it finished.</summary>
    public IReadOnlyList<DiscordHeard> Receive(uint ssrc, ushort sequence, ReadOnlySpan<byte> opus, DateTimeOffset now)
    {
        if (!speakers.TryGetValue(ssrc, out var speaker)) speakers[ssrc] = speaker = new(options);
        ready.Clear();
        speaker.Order.Push(sequence, opus.ToArray(), ready);
        List<DiscordHeard>? finished = null;
        foreach (var frame in ready)
        {
            var pcm = speaker.Downsampler.Convert(speaker.Decoder.Decode(frame));
            if (speaker.Endpointer.Append(pcm, now) is { } utterance) Add(ref finished, ssrc, utterance);
        }
        return finished ?? (IReadOnlyList<DiscordHeard>)[];
    }

    /// <summary>Ends utterances of speakers who stopped sending; call every ~100 ms.</summary>
    public IReadOnlyList<DiscordHeard> Tick(DateTimeOffset now)
    {
        List<DiscordHeard>? finished = null;
        foreach (var (ssrc, speaker) in speakers)
            if (speaker.Endpointer.Tick(now) is { } utterance) Add(ref finished, ssrc, utterance);
        return finished ?? (IReadOnlyList<DiscordHeard>)[];
    }

    /// <summary>Forgets a speaker who left, returning what they were saying.</summary>
    public DiscordHeard? Remove(uint ssrc)
    {
        if (!speakers.Remove(ssrc, out var speaker)) return null;
        List<DiscordHeard>? finished = null;
        if (speaker.Endpointer.Flush() is { } utterance) Add(ref finished, ssrc, utterance);
        return finished?[0];
    }

    /// <summary>Users talking right now with enough voice to count as talking over Martlet.</summary>
    public IReadOnlyList<ulong> TalkingOver(DateTimeOffset now) =>
    [
        .. speakers.Where(pair => pair.Value.Endpointer.Active && pair.Value.Endpointer.Voiced >= options.BargeInVoice &&
                now - pair.Value.Endpointer.LastVoice < options.EndSilence)
            .Select(pair => userOf(pair.Key)).OfType<ulong>().Distinct()
    ];

    private void Add(ref List<DiscordHeard>? finished, uint ssrc, short[] utterance)
    {
        if (userOf(ssrc) is not { } user) return;
        heard.Add(user);
        (finished ??= []).Add(new(user, utterance));
    }

    private sealed class Speaker(DiscordEndpointOptions options)
    {
        public DiscordPacketOrder Order { get; } = new();
        public DiscordOpusDecoder Decoder { get; } = new();
        public DiscordDownsampler Downsampler { get; } = new();
        public DiscordSpeakerEndpointer Endpointer { get; } = new(options);
    }
}
