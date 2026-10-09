using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Martlet.Core.Settings;

namespace Martlet.Conversation;

/// <summary>Companion › Voice › Quick sounds while Martlet thinks (off by default): when a confirmed reply has no audio of its
/// own after <see cref="Delay"/>, or its Thinking model thinks before it answers (hidden reasoning), Martlet plays one short
/// clip in its own voice ("Mm,", "Hmm...") and the reply follows it. At most one every <see cref="Cooldown"/>, never twice in a
/// reply, never on a fast reply. ElevenLabs Agents' soft timeout and OpenAI's preambles are the same idea.</summary>
public sealed record QuickSoundOptions
{
    public bool Enabled { get; init; }
    /// <summary>How long a confirmed reply may go without its first audio before a quick sound plays (700 ms by default).</summary>
    public TimeSpan Delay { get; init; } = DefaultDelay;
    /// <summary>The least time from one quick sound to the next (20 s).</summary>
    public TimeSpan Cooldown { get; init; } = DefaultCooldown;
    /// <summary>How soon a quick sound may play when the Thinking model thinks before it answers.</summary>
    public TimeSpan ReasoningDelay { get; init; } = DefaultReasoningDelay;

    public static TimeSpan DefaultDelay { get; } = TimeSpan.FromMilliseconds(700);
    public static TimeSpan DefaultCooldown { get; } = TimeSpan.FromSeconds(20);
    public static TimeSpan DefaultReasoningDelay { get; } = TimeSpan.FromMilliseconds(300);
    /// <summary>The delays Companion › Voice offers, in milliseconds.</summary>
    public static IReadOnlyList<int> DelayChoices { get; } = [500, 700, 1000, 1500];
    public static QuickSoundOptions Off { get; } = new();

    /// <summary>The options with the delay the owner chose (one of <see cref="DelayChoices"/>, else the default).</summary>
    public static QuickSoundOptions Of(bool enabled, int delayMilliseconds) => new()
    {
        Enabled = enabled,
        Delay = DelayChoices.Contains(delayMilliseconds) ? TimeSpan.FromMilliseconds(delayMilliseconds) : DefaultDelay
    };
}

/// <summary>One quick sound: what it says and the clip in Martlet's own voice (24 kHz mono 16-bit PCM, at most
/// <see cref="QuickSoundAudio.MaximumBytes"/>).</summary>
public sealed record QuickSoundClip(string Text, ReadOnlyMemory<byte> Pcm)
{
    public TimeSpan Duration => TimeSpan.FromSeconds(Pcm.Length / 2.0 / QuickSoundAudio.SampleRate);
}

/// <summary>The quick sounds made for one voice and character: <see cref="Key"/> names them on disk
/// (<see cref="QuickSoundLibrary"/>), <see cref="Voice"/> says which voice in words (no secrets).</summary>
public sealed record QuickSoundSet(string Key, string Voice, DateTimeOffset MadeAt, IReadOnlyList<QuickSoundClip> Clips);

/// <summary>A reply as the quick sound rules see it at one moment: whether it is spoken, confirmed (taken as the reply, never
/// one still held while the user may go on talking) and for how long, whether its own first audio is ready, whether its Thinking
/// model thinks first, whether it was paused because the user talked over it, and whether it ended.</summary>
public readonly record struct QuickSoundMoment(bool Spoken, bool Confirmed, TimeSpan SinceConfirmed, bool AudioReady, bool Reasoning,
    bool Paused, bool Ended);

public enum QuickSoundVerdict { Wait, Play, Never }

public readonly record struct QuickSoundDecision(QuickSoundVerdict Verdict, string Why);

/// <summary>The quick sound rules for one conversation (<see cref="QuickSoundOptions"/>). <see cref="Decide"/> says, for one
/// moment of a reply, whether to wait, play now or never play in this reply; after a quick sound played, <see cref="Played"/>
/// starts the cooldown and the next clip comes next time (<see cref="Next"/>). Thread-safe.</summary>
public sealed class QuickSoundGate(TimeProvider clock)
{
    private readonly object gate = new();
    private QuickSoundOptions options = QuickSoundOptions.Off;
    private long? lastPlayedAt;
    private int next;

    public QuickSoundOptions Options
    {
        get { lock (gate) return options; }
        set { lock (gate) options = value ?? QuickSoundOptions.Off; }
    }

    /// <summary>How many quick sounds played in this conversation.</summary>
    public int Count { get { lock (gate) return next; } }

    public QuickSoundDecision Decide(QuickSoundMoment moment)
    {
        QuickSoundOptions now;
        long? last;
        lock (gate) (now, last) = (options, lastPlayedAt);
        if (!now.Enabled) return new(QuickSoundVerdict.Never, "quick sounds are off");
        if (!moment.Spoken) return new(QuickSoundVerdict.Never, "the reply isn't spoken");
        if (moment.AudioReady) return new(QuickSoundVerdict.Never, "its own first audio was ready in time");
        if (moment.Ended) return new(QuickSoundVerdict.Never, "the reply ended");
        if (moment.Paused) return new(QuickSoundVerdict.Never, "it was paused because you talked over it");
        if (!moment.Confirmed) return new(QuickSoundVerdict.Wait, "the reply isn't confirmed yet");
        var wait = moment.Reasoning && now.ReasoningDelay < now.Delay ? now.ReasoningDelay : now.Delay;
        if (moment.SinceConfirmed < wait) return new(QuickSoundVerdict.Wait, "waiting for its first audio");
        if (last is { } at && clock.GetElapsedTime(at) is var since && since < now.Cooldown)
            return new(QuickSoundVerdict.Never, $"one played {since.TotalSeconds:0} s ago");
        return new(QuickSoundVerdict.Play, moment.Reasoning ? "its Thinking model thinks before it answers"
            : $"it had no audio {wait.TotalMilliseconds:0} ms after it was confirmed");
    }

    /// <summary>Which of <paramref name="count"/> clips plays next: they take turns. -1 without clips.</summary>
    public int Next(int count)
    {
        if (count <= 0) return -1;
        lock (gate) return next % count;
    }

    /// <summary>A quick sound played now: the cooldown starts and the next clip comes next time.</summary>
    public void Played()
    {
        lock (gate)
        {
            lastPlayedAt = clock.GetTimestamp();
            next++;
        }
    }
}

/// <summary>What became of the quick sound for one reply: whether one played, why (or why not), which and how long after the
/// reply was confirmed.</summary>
public sealed record QuickSoundOutcome(bool Played, string Why, string? Clip = null, TimeSpan? AfterConfirmed = null);

/// <summary>Watches one reply and plays a quick sound when the rules say so (<see cref="QuickSoundGate"/>): it looks at the
/// reply every <see cref="Poll"/> from its start until its own audio is ready, it ends, or a quick sound played. Its work is a
/// few snapshot reads beside the reply, never on the reply's own path, so the reply is never later for it.</summary>
public static class QuickSoundWatcher
{
    public static TimeSpan Poll { get; } = TimeSpan.FromMilliseconds(25);

    /// <param name="confirmed">Whether the reply is confirmed now (taken as the reply); the delay counts from the first moment it
    /// is. A reply started early stays unconfirmed while it is held.</param>
    /// <param name="reasoning">The Thinking model is set to think before it answers (Thinking steps on, where the route uses
    /// it). Hidden reasoning that streams before any words counts too.</param>
    public static async Task<QuickSoundOutcome> WatchAsync(ConversationTurn turn, QuickSoundGate gate, IReadOnlyList<QuickSoundClip> clips,
        Func<bool> confirmed, bool reasoning, TimeProvider clock, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(turn);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(clips);
        ArgumentNullException.ThrowIfNull(confirmed);
        long? confirmedAt = null;
        try
        {
            while (true)
            {
                var snapshot = turn.Snapshot;
                if (confirmedAt is null && confirmed()) confirmedAt = clock.GetTimestamp();
                var timings = snapshot.Timings;
                var since = confirmedAt is { } at ? clock.GetElapsedTime(at) : TimeSpan.Zero;
                var decision = gate.Decide(new(turn.Spoken, confirmedAt is not null, since,
                    AudioReady: timings?.FirstSpeechAudioAfter is not null || timings?.PlaybackStartedAfter is not null || snapshot.FirstAudioAfter is not null,
                    Reasoning: reasoning || timings?.FirstReasoningAfter is not null && snapshot.FirstTextAfter is null,
                    Paused: turn.Paused || timings?.PausesForYou > 0,
                    Ended: turn.Completion.IsCompleted || snapshot.SpeechFailed || snapshot.VoiceMuted ||
                        snapshot.State is ConversationState.Canceled or ConversationState.Failed));
                switch (decision.Verdict)
                {
                    case QuickSoundVerdict.Wait:
                        await Task.Delay(Poll, clock, cancellationToken).ConfigureAwait(false);
                        continue;
                    case QuickSoundVerdict.Never:
                        return new(false, decision.Why);
                }
                var index = gate.Next(clips.Count);
                if (index < 0) return new(false, "no quick sounds are made for this voice yet");
                if (!turn.PlayQuickSound(clips[index].Pcm)) return new(false, "its own audio started first");
                gate.Played();
                return new(true, decision.Why, clips[index].Text, since);
            }
        }
        catch (OperationCanceledException) { return new(false, "the reply was stopped"); }
    }
}

/// <summary>What the quick sounds say: short sounds a person makes while they think, written with a comma or dots so the voice
/// keeps them open, as if more follows. A voice engine with a breath-in tag (Dia's (inhales)) gets a soft breath too.</summary>
public static class QuickSoundPhrases
{
    public static IReadOnlyList<string> Base { get; } = ["Mm,", "Hmm...", "Oh,", "Ah,"];

    public static IReadOnlyList<string> For(SpeechEngine? engine) =>
        engine?.Tags.FirstOrDefault(tag => tag.Kind == VoiceTagKind.Sound && tag.Text.Contains("inhale", StringComparison.OrdinalIgnoreCase))
            is { } breath ? [.. Base, breath.Text] : Base;
}

/// <summary>Makes a clip fit to play as a quick sound: the silence around the voice is cut (keeping 30 ms), it is at most
/// <see cref="MaximumLength"/> long and fades in and out over 10 ms, so it never clicks.</summary>
public static class QuickSoundAudio
{
    public const int SampleRate = 24_000;
    public static TimeSpan MaximumLength { get; } = TimeSpan.FromMilliseconds(1_200);
    public static int MaximumBytes => (int)(SampleRate * MaximumLength.TotalSeconds) * 2;
    private const short Quiet = 500;
    private const int Margin = SampleRate * 30 / 1000, Fade = SampleRate * 10 / 1000;

    /// <summary>The clip to keep from <paramref name="pcm"/> (24 kHz mono 16-bit PCM), or empty when it holds no voice.</summary>
    public static byte[] Prepare(ReadOnlySpan<byte> pcm) => Prepare(pcm, MaximumLength);

    /// <summary>The clip to keep from <paramref name="pcm"/>, at most <paramref name="maximum"/> long (a voice sound's
    /// <see cref="VoiceSoundLibrary.MaximumLength"/>), or empty when it holds no voice.</summary>
    public static byte[] Prepare(ReadOnlySpan<byte> pcm, TimeSpan maximum)
    {
        var maximumSamples = (int)(SampleRate * maximum.TotalSeconds);
        var samples = new short[pcm.Length / 2];
        for (var i = 0; i < samples.Length; i++) samples[i] = (short)(pcm[2 * i] | pcm[2 * i + 1] << 8);
        int first = Array.FindIndex(samples, s => Math.Abs((int)s) > Quiet), last = Array.FindLastIndex(samples, s => Math.Abs((int)s) > Quiet);
        if (first < 0) return [];
        var start = Math.Max(0, first - Margin);
        var end = Math.Min(samples.Length, Math.Min(last + 1 + Margin, start + maximumSamples));
        var length = end - start;
        var clip = new byte[length * 2];
        for (var i = 0; i < length; i++)
        {
            var gain = Math.Min(1.0, Math.Min((i + 1) / (double)Fade, (length - i) / (double)Fade));
            var value = (short)Math.Round(samples[start + i] * gain);
            clip[2 * i] = (byte)value;
            clip[2 * i + 1] = (byte)(value >> 8);
        }
        return clip;
    }
}

/// <summary>Quick sounds kept on this PC, per voice and character: quick-sounds\&lt;key&gt;\clips.json and one .pcm file per
/// clip in the data folder. Made once with the voice (never automatically with a paid cloud voice) and read again at start.</summary>
public static class QuickSoundLibrary
{
    public const string Folder = "quick-sounds";
    public const int MaximumClips = 8;
    private const string Manifest = "clips.json";

    /// <summary>The key of the quick sounds made with <paramref name="voice"/> (the voice's exact identity: route kind, model, voice
    /// or reference and its revision) for <paramref name="character"/> (the persona's ID).</summary>
    public static string Key(string voice, string character) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(voice + "\n" + character)))[..16];

    /// <summary>The character part of a <see cref="Key"/>: the active persona's ID, or "none".</summary>
    public static string Character(Guid? persona) => persona is { } id && id != Guid.Empty ? id.ToString("N") : "none";

    /// <summary>The voice a Voice route speaks with, as quick sounds know it: its exact identity (for <see cref="Key"/>), the voice
    /// in words (no secrets) and whether it is a paid cloud voice (OpenAI's or ElevenLabs'); null for a route Martlet can't make them with. A
    /// paired host's voice is its engine and model with the applied reference voice and revision, so a new recording makes them
    /// again.</summary>
    public static (string Identity, string Words, bool Paid)? Voice(SetupRoute? tts) => tts switch
    {
        { RouteType: SetupRouteType.GatewayF5, Gateway: { } gateway, Reference: { } reference } =>
            ($"host|{gateway.HostId}|{tts.GatewaySnapshot?.RouteId}|{tts.ModelId}|{reference.PresetId:N}|{reference.ReferenceRevision}",
                $"{tts.ModelId} on {gateway.HostId}", false),
        { RouteType: null or SetupRouteType.OpenAi, VoiceId: { Length: > 0 } openAi } =>
            ($"openai|{tts.ModelId}|{openAi}", $"OpenAI {tts.ModelId} ({openAi})", true),
        { RouteType: SetupRouteType.ElevenLabs, VoiceId: { Length: > 0 } cloned } =>
            ($"elevenlabs|{tts.ModelId}|{cloned}", $"ElevenLabs {tts.ModelId} (your cloned voice)", true),
        _ => null
    };

    public static string Directory(string dataDirectory, string key) => Path.Combine(dataDirectory, Folder, key);

    /// <summary>The quick sounds kept for <paramref name="key"/>, or null when none are (or they can't be read).</summary>
    public static QuickSoundSet? Load(string dataDirectory, string key)
    {
        try
        {
            var folder = Directory(dataDirectory, key);
            var path = Path.Combine(folder, Manifest);
            if (!File.Exists(path) || new FileInfo(path).Length > 16 * 1024) return null;
            var saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(path));
            if (saved is not { SchemaVersion: 1 } || saved.Key != key || saved.Clips is not { Count: > 0 and <= MaximumClips }) return null;
            List<QuickSoundClip> clips = [];
            foreach (var clip in saved.Clips)
            {
                if (clip.Text is not { Length: > 0 and <= 64 } || clip.File is not { } name || name != Path.GetFileName(name) ||
                    !name.EndsWith(".pcm", StringComparison.Ordinal)) return null;
                var file = Path.Combine(folder, name);
                if (!File.Exists(file) || new FileInfo(file).Length is var length && (length == 0 || length > QuickSoundAudio.MaximumBytes || length % 2 != 0))
                    return null;
                clips.Add(new(clip.Text, File.ReadAllBytes(file)));
            }
            return new(key, saved.Voice ?? "", saved.MadeAt, clips);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Keeps <paramref name="set"/> (replacing what was kept for its key); false when the folder can't be written.</summary>
    public static bool Save(string dataDirectory, QuickSoundSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        if (set.Clips.Count is 0 or > MaximumClips || set.Clips.Any(c => c.Pcm.Length is 0 || c.Pcm.Length > QuickSoundAudio.MaximumBytes)) return false;
        try
        {
            var folder = Directory(dataDirectory, set.Key);
            System.IO.Directory.CreateDirectory(folder);
            var files = set.Clips.Select((clip, i) => (clip, file: $"{i}.pcm")).ToArray();
            foreach (var (clip, file) in files) File.WriteAllBytes(Path.Combine(folder, file), clip.Pcm.ToArray());
            var manifest = JsonSerializer.Serialize(new Saved(1, set.Key, set.Voice, set.MadeAt,
                [.. files.Select(f => new SavedClip(f.clip.Text, f.file, (int)f.clip.Duration.TotalMilliseconds))]),
                new JsonSerializerOptions { WriteIndented = true });
            var temporary = Path.Combine(folder, $"clips.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(temporary, manifest);
            File.Move(temporary, Path.Combine(folder, Manifest), overwrite: true);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Every kept set (key, voice, when made, clip count and their words), for MCP's quick_sounds_status.</summary>
    public static IReadOnlyList<QuickSoundSet> All(string dataDirectory)
    {
        var root = Path.Combine(dataDirectory, Folder);
        if (!System.IO.Directory.Exists(root)) return [];
        try
        {
            return [.. System.IO.Directory.GetDirectories(root).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal)
                .Select(key => Load(dataDirectory, key)).OfType<QuickSoundSet>()];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return []; }
    }

    private sealed record Saved(int SchemaVersion, string Key, string? Voice, DateTimeOffset MadeAt, IReadOnlyList<SavedClip>? Clips);
    private sealed record SavedClip(string? Text, string? File, int Milliseconds);
}
