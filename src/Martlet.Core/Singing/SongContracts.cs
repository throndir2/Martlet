using System.Buffers.Binary;
using System.Globalization;

namespace Martlet.Core.Singing;

/// <summary>
/// Makes a song sung in a voice from Martlet's shared voice library: the music is written from the lyrics and style, then the
/// singing is matched to the chosen voice's recording. The production implementation is the desktop's client for the
/// <c>singing</c> host role (route <c>martlet.gateway.song.v1</c>); <see cref="FixtureSongMaker"/> is a deterministic
/// FIXTURE - NOT AI stand-in for tests. A song takes from tens of seconds to several minutes, so callers run it as a
/// background job and report <see cref="SongProgress"/>.
/// </summary>
public interface ISongMaker
{
    /// <summary>Whether a song can be made now, where, and with which choices. Never throws for "not set up": it returns
    /// <see cref="SongMakerAvailability.Available"/> false with a reason the owner can act on.</summary>
    Task<SongMakerAvailability> GetAvailabilityAsync(CancellationToken cancellationToken);

    /// <summary>Makes one song. <paramref name="progress"/> receives each stage (queued, loading, writing music, separating,
    /// matching voice, mixing, delivering) with the overall fraction; cancelling <paramref name="cancellationToken"/> cancels
    /// the job where it runs and throws <see cref="OperationCanceledException"/>. Other failures throw
    /// <see cref="SongException"/> with a stable <see cref="SongException.Code"/>.</summary>
    Task<SongResult> GenerateAsync(SongRequest request, IProgress<SongProgress>? progress, CancellationToken cancellationToken);
}

/// <summary>How the music is written: <see cref="Fast"/> is ACE-Step 1.5 turbo (8 steps, the default);
/// <see cref="HighQuality"/> is ACE-Step 1.5 SFT (50 steps), clearer lyrics for a few more seconds.</summary>
public enum SongQuality
{
    Fast,
    HighQuality
}

/// <summary>How the singing is matched to the voice: <see cref="SoulX"/> is SoulX-Singer-SVC (Apache-2.0, keeps the tune and
/// words, the default); <see cref="VevoSing"/> is Amphion's Vevo1.5 (a bit closer to the voice, cleaner, may drift off-key;
/// CC-BY-NC-ND-4.0 weights, personal non-commercial use only, set up only when the owner chooses it).</summary>
public enum SongVoiceMatch
{
    SoulX,
    VevoSing
}

/// <summary>One song to make. <see cref="Lyrics"/> use ACE-Step's section tags (<c>[verse]</c>, <c>[chorus]</c>,
/// <c>[bridge]</c>...), one sung line per line; <see cref="Style"/> is the caption (genre, instruments, mood, vocal gender).
/// <see cref="VoiceId"/> names a voice of the shared speaking-voice library (its <c>SpeakingVoice.Id</c>); the host resolves
/// its recording, so no path or URL is ever sent.</summary>
public sealed record SongRequest
{
    public const int MinimumDurationSeconds = 15;
    public const int MaximumDurationSeconds = 180;
    public const int DefaultDurationSeconds = 60;
    public const int MaximumLyricsCharacters = 4_096;
    public const int MaximumStyleCharacters = 512;
    public const int MinimumBpm = 40;
    public const int MaximumBpm = 240;

    public required string Lyrics { get; init; }
    public required string Style { get; init; }
    public required string VoiceId { get; init; }
    public int DurationSeconds { get; init; } = DefaultDurationSeconds;
    /// <summary>The sung language as an ISO 639-1 code ("en", "ja"...).</summary>
    public string Language { get; init; } = "en";
    /// <summary>Optional tempo; the music model picks one when null.</summary>
    public int? Bpm { get; init; }
    /// <summary>Optional key such as "C major" or "F# minor"; the music model picks one when null.</summary>
    public string? Key { get; init; }
    /// <summary>Optional seed for a repeatable song; a random one is chosen (and returned in the result) when null.</summary>
    public long? Seed { get; init; }
    public SongQuality Quality { get; init; } = SongQuality.Fast;
    public SongVoiceMatch VoiceMatch { get; init; } = SongVoiceMatch.SoulX;

    /// <summary>Throws <see cref="SongException"/> (<c>request.invalid</c>) when a field is outside its bounds.</summary>
    public void Validate()
    {
        static void Require(bool condition, string message)
        {
            if (!condition) throw new SongException(SongErrorCodes.RequestInvalid, message);
        }

        Require(!string.IsNullOrWhiteSpace(Lyrics) && Lyrics.Length <= MaximumLyricsCharacters,
            $"Lyrics must have 1 to {MaximumLyricsCharacters} characters.");
        Require(!string.IsNullOrWhiteSpace(Style) && Style.Length <= MaximumStyleCharacters,
            $"The style must have 1 to {MaximumStyleCharacters} characters.");
        Require(!Lyrics.Any(c => char.IsControl(c) && c is not '\n' and not '\r' and not '\t') && !Style.Any(char.IsControl),
            "Lyrics and style must be plain text.");
        Require(VoiceId is { Length: > 0 and <= 128 } &&
            VoiceId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or ':' or '.'),
            "Choose a voice from the voice library.");
        Require(DurationSeconds is >= MinimumDurationSeconds and <= MaximumDurationSeconds,
            $"A song lasts {MinimumDurationSeconds} to {MaximumDurationSeconds} seconds.");
        Require(Language is { Length: 2 } && Language.All(char.IsAsciiLetterLower), "Use a two-letter language code such as en.");
        Require(Bpm is null or (>= MinimumBpm and <= MaximumBpm), $"The tempo must be {MinimumBpm} to {MaximumBpm} BPM.");
        Require(Key is null || (Key.Length is > 0 and <= 16 &&
            Key.All(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '#' or '♯' or '♭')),
            "Write the key like C major or F# minor.");
        Require(Seed is null or (>= 0 and <= uint.MaxValue), "The seed must be 0 to 4294967295.");
        Require(Enum.IsDefined(Quality) && Enum.IsDefined(VoiceMatch), "Choose a quality and a voice match.");
    }
}

/// <summary>Where a song job is. Stages run in this order; <see cref="Loading"/> appears only while models load.</summary>
public enum SongStage
{
    Queued,
    Loading,
    WritingMusic,
    Separating,
    MatchingVoice,
    Mixing,
    Delivering,
    Completed
}

/// <summary>Progress of one song: the current stage, the overall fraction (0 to 1) and, while queued, how many jobs are
/// ahead.</summary>
public sealed record SongProgress(SongStage Stage, double Fraction, int? QueuePosition = null)
{
    /// <summary>A short owner-facing description such as "Writing the music".</summary>
    public string Describe() => Stage switch
    {
        SongStage.Queued => QueuePosition is > 0 ? $"Waiting for {QueuePosition} other song(s)" : "Waiting to start",
        SongStage.Loading => "Loading the singing models",
        SongStage.WritingMusic => "Writing the music",
        SongStage.Separating => "Separating the voice from the music",
        SongStage.MatchingVoice => "Matching the singing to the voice",
        SongStage.Mixing => "Mixing",
        SongStage.Delivering => "Fetching the song",
        _ => "Done"
    };
}

public enum SongTrackKind
{
    Mix,
    Vocals,
    Backing
}

/// <summary>One track of a song as interleaved signed 16-bit little-endian PCM. The mix and backing are 48 kHz stereo; the
/// vocals are 48 kHz mono. They start together and have the same length, so the vocals and backing can be played,
/// ducked or stopped separately in step with each other.</summary>
public sealed class SongTrack
{
    public const int SampleRateHz = 48_000;

    public SongTrack(SongTrackKind kind, int sampleRate, int channels, ReadOnlyMemory<byte> pcm16)
    {
        if (!Enum.IsDefined(kind) || sampleRate is < 8_000 or > 96_000 || channels is not (1 or 2) ||
            pcm16.Length % (2 * channels) != 0)
            throw new ArgumentException("The track is not interleaved 16-bit PCM.");
        Kind = kind;
        SampleRate = sampleRate;
        Channels = channels;
        Pcm16 = pcm16;
    }

    public SongTrackKind Kind { get; }
    public int SampleRate { get; }
    public int Channels { get; }
    public ReadOnlyMemory<byte> Pcm16 { get; }
    public long Frames => Pcm16.Length / (2L * Channels);
    public TimeSpan Duration => TimeSpan.FromSeconds((double)Frames / SampleRate);

    /// <summary>The track as a canonical PCM WAV file.</summary>
    public byte[] ToWave()
    {
        var wave = new byte[44 + Pcm16.Length];
        "RIFF"u8.CopyTo(wave);
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(4), checked((uint)(wave.Length - 8)));
        "WAVEfmt "u8.CopyTo(wave.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(22), (ushort)Channels);
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(24), (uint)SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(28), (uint)(SampleRate * Channels * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(32), (ushort)(Channels * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(34), 16);
        "data"u8.CopyTo(wave.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(40), (uint)Pcm16.Length);
        Pcm16.Span.CopyTo(wave.AsSpan(44));
        return wave;
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Kind} {SampleRate} Hz x{Channels}, {Duration.TotalSeconds:0.0} s");
}

/// <summary>When a lyric line starts (and ends, when known) in the song, from ACE-Step's LRC lyric timestamps.</summary>
public sealed record SongLyricLine(TimeSpan Start, TimeSpan? End, string Text);

/// <summary>How long one stage took on the host (loading models counts as <see cref="SongStage.Loading"/>).</summary>
public sealed record SongStageTiming(SongStage Stage, TimeSpan Duration);

/// <summary>What made the song: the models, whether it was the FIXTURE - NOT AI engine, and the choices used.</summary>
public sealed record SongEngineIdentity(string Generator, string Separator, string Converter, SongQuality Quality,
    SongVoiceMatch VoiceMatch, bool Fixture);

/// <summary>A finished song: the mix to play, the separate vocals and backing (for ducking or stopping the voice), lyric
/// timestamps when the music model provided them, and how long each stage took.</summary>
public sealed record SongResult
{
    public required string JobId { get; init; }
    public required string VoiceId { get; init; }
    public required SongTrack Mix { get; init; }
    public required SongTrack Vocals { get; init; }
    public required SongTrack Backing { get; init; }
    public required SongEngineIdentity Engine { get; init; }
    public required long Seed { get; init; }
    public int? Bpm { get; init; }
    public string? Key { get; init; }
    public IReadOnlyList<SongLyricLine> LyricTimestamps { get; init; } = [];
    public IReadOnlyList<SongStageTiming> StageTimings { get; init; } = [];
    public TimeSpan Duration => Mix.Duration;

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"Song {JobId}: {Duration.TotalSeconds:0.0} s, {Engine.Generator} + {Engine.Converter}");
}

/// <summary>Whether songs can be made now. <see cref="Host"/> names the computer that sings (null for a fixture);
/// <see cref="Qualities"/> and <see cref="VoiceMatches"/> list the choices set up there.</summary>
public sealed record SongMakerAvailability(bool Available, string? Reason, string? Host,
    IReadOnlyList<SongQuality> Qualities, IReadOnlyList<SongVoiceMatch> VoiceMatches, bool Fixture)
{
    public static SongMakerAvailability Unavailable(string reason) => new(false, reason, null, [], [], false);
}

/// <summary>Stable failure codes of <see cref="SongException"/>.</summary>
public static class SongErrorCodes
{
    /// <summary>No computer has the singing role set up, or it is not reachable or not ready.</summary>
    public const string Unavailable = "singing.unavailable";
    /// <summary>The singing queue is full; try again later.</summary>
    public const string Busy = "singing.busy";
    /// <summary>The voice is not in the shared voice library, or its recording cannot be used.</summary>
    public const string VoiceMissing = "voice.missing";
    /// <summary>The voice match chosen is not set up on the singing computer.</summary>
    public const string VoiceMatchUnavailable = "singing.voice_match_unavailable";
    /// <summary>A request field is outside its bounds.</summary>
    public const string RequestInvalid = "request.invalid";
    /// <summary>A stage failed on the host (the message says which).</summary>
    public const string Failed = "song.failed";
    /// <summary>The job took longer than its bound.</summary>
    public const string TimedOut = "song.timeout";
}

public sealed class SongException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
}
