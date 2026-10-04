using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Core.Audio;
using Martlet.Core.Creations;
using Martlet.Core.Singing;

namespace Martlet.Conversation;

/// <summary>One sung line of a song: where its singing starts (the vocal onset, in seconds from the top), where it ends when
/// known, its words and its section ("verse", "chorus 2"; empty without tags).</summary>
public sealed record StoredSongLine(double Start, double? End, string Text, string Section = "");

/// <summary>One sung word of a song and when it is sung (seconds from the top).</summary>
public sealed record StoredSongWord(double Start, double End, string Text);

/// <summary>A song Martlet made, as it plays one: a song creation (<see cref="SongCreations"/>) with its map (lines and words
/// with sections and times, and the beat grid), how it was made and where its mouth track came from. <see cref="Id"/> is the
/// creation's short key, which Martlet's tools use. Times are seconds from the top.</summary>
public sealed record StoredSong
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    /// <summary>What the user asked for, in a few words.</summary>
    public string About { get; init; } = "";
    public required string Lyrics { get; init; }
    public required string Style { get; init; }
    public required string VoiceId { get; init; }
    public double DurationSeconds { get; init; }
    public double? Bpm { get; init; }
    public string? Key { get; init; }
    public int BeatsPerBar { get; init; } = 4;
    public IReadOnlyList<double> Beats { get; init; } = [];
    public IReadOnlyList<double> Downbeats { get; init; } = [];
    public IReadOnlyList<StoredSongLine> Lines { get; init; } = [];
    /// <summary>The sung words with their times: the song maker's, or (<see cref="WordsEstimated"/>) spread over each line's
    /// singing.</summary>
    public IReadOnlyList<StoredSongWord> Words { get; init; } = [];
    public bool WordsEstimated { get; init; }
    /// <summary>Where the mouth track came from (<see cref="SongMouthSource"/>) and what made it.</summary>
    public string? MouthSource { get; init; }
    public string? MouthNote { get; init; }
    public string Generator { get; init; } = "";
    public string Converter { get; init; } = "";
    public string Quality { get; init; } = "";
    public string VoiceMatch { get; init; } = "";
    /// <summary>Made by the FIXTURE - NOT AI song maker.</summary>
    public bool Fixture { get; init; }
    public long Seed { get; init; }
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>The song's map for playback planning.</summary>
    public SongMap Map() => new(TimeSpan.FromSeconds(DurationSeconds), Bpm, BeatsPerBar,
        [.. Beats.Select(TimeSpan.FromSeconds)], [.. Downbeats.Select(TimeSpan.FromSeconds)],
        [.. Lines.Select(line => new SongLyricLine(TimeSpan.FromSeconds(line.Start), line.End is { } end ? TimeSpan.FromSeconds(end) : null,
            line.Text, line.Section))]);

    /// <summary>The sung words with their times.</summary>
    public IReadOnlyList<SongWordTime> WordTimes() =>
        [.. Words.Select(word => new SongWordTime(TimeSpan.FromSeconds(word.Start), TimeSpan.FromSeconds(word.End), word.Text))];

    public override string ToString() => $"{nameof(StoredSong)} {Id}";
}

/// <summary>A song's backing (48 kHz stereo, interleaved) and vocals (48 kHz mono) as 16-bit samples, sample-aligned.</summary>
public sealed class SongAudio
{
    public SongAudio(short[] backing, short[] vocals, int sampleRate = SongTrack.SampleRateHz)
    {
        ArgumentNullException.ThrowIfNull(backing);
        ArgumentNullException.ThrowIfNull(vocals);
        if (backing.Length % 2 != 0) throw new ArgumentException("The backing must be interleaved stereo.");
        Backing = backing;
        Vocals = vocals;
        SampleRate = sampleRate;
        Frames = Math.Min(backing.Length / 2, vocals.Length);
    }

    public short[] Backing { get; }
    public short[] Vocals { get; }
    public int SampleRate { get; }
    public long Frames { get; }
    public TimeSpan Duration => TimeSpan.FromSeconds((double)Frames / SampleRate);

    /// <summary>The backing and vocals of a song the song maker returned.</summary>
    public static SongAudio From(SongResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new(Samples(result.Backing.Pcm16.Span, result.Backing.Channels, 2), Samples(result.Vocals.Pcm16.Span, result.Vocals.Channels, 1),
            result.Backing.SampleRate);
    }

    /// <summary>A track as mono samples (the vocals, for the mouth track).</summary>
    public static short[] Mono(SongTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        return Samples(track.Pcm16.Span, track.Channels, 1);
    }

    /// <summary>Interleaved 16-bit PCM with <paramref name="from"/> channels as <paramref name="to"/> channels (mono is the
    /// average of stereo; stereo repeats mono).</summary>
    public static short[] Samples(ReadOnlySpan<byte> pcm, int from, int to)
    {
        var frames = pcm.Length / (2 * from);
        var samples = new short[frames * to];
        for (var frame = 0; frame < frames; frame++)
        {
            var at = frame * from * 2;
            var first = BinaryPrimitives.ReadInt16LittleEndian(pcm[at..]);
            var second = from == 2 ? BinaryPrimitives.ReadInt16LittleEndian(pcm[(at + 2)..]) : first;
            if (to == 1) samples[frame] = (short)((first + second) / 2);
            else
            {
                samples[frame * 2] = first;
                samples[frame * 2 + 1] = second;
            }
        }
        return samples;
    }

    public override string ToString() => $"{nameof(SongAudio)} ({Duration.TotalSeconds:0.0} s)";
}

/// <summary>"0:22", "1:05".</summary>
public static class SongClock
{
    public static string Of(TimeSpan time) =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)Math.Max(0, time.TotalMinutes)}:{Math.Max(0, time.Seconds):00}");
}

/// <summary>Songs as creations (Martlet's shared Creations library, <see cref="CreationStore"/>): the <c>song</c> kind, its
/// assets (the mix, vocals and backing as FLAC, its map and its mouth track as JSON) and the song Martlet plays from one. A
/// song made on one computer is copied to every paired Martlet computer, so any of them can sing it.</summary>
public static class SongCreations
{
    public const string KindName = "song";
    public const string Mix = "mix", Vocals = "vocals", Backing = "backing", MapAsset = "map", Mouth = "mouth";
    private const long Audio = 64L * 1024 * 1024;

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>The song kind: sung by Martlet in conversation (play_song, or perform_creation with the same from).</summary>
    public static CreationKind Kind { get; } = new()
    {
        Name = KindName, Noun = "song", Plural = "songs", Verb = "sing", Glyph = "\uEC4F",
        Assets =
        [
            new(Mix, [FlacCodec.MediaType], Audio), new(Vocals, [FlacCodec.MediaType], Audio), new(Backing, [FlacCodec.MediaType], Audio),
            new(MapAsset, ["application/json"], 1024 * 1024), new(Mouth, ["application/json"], 16L * 1024 * 1024, Required: false)
        ],
        MaximumBytes = 3 * Audio + 17L * 1024 * 1024,
        AutoCleanup = true,
        OptionsHint = "For a song: {\"from\": \"start\"}, or resume, a section (chorus), line:N or a time like 1:05.",
        Describe = creation => $"a {SongClock.Of(creation.Duration ?? TimeSpan.Zero)} song" +
            (Metadata(creation) is { About.Length: > 0 } about ? $" ({about.About})" : ""),
        Details = creation => Sections(creation.Text ?? "")
    };

    /// <summary>The song's metadata (small; the map is an asset).</summary>
    public sealed record SongMetadata
    {
        public int Version { get; init; } = 1;
        public string About { get; init; } = "";
        public string Style { get; init; } = "";
        public string VoiceId { get; init; } = "";
        public double? Bpm { get; init; }
        public string? Key { get; init; }
        public int Lines { get; init; }
        public int Words { get; init; }
        public bool WordsEstimated { get; init; }
        public string? WordTimingSource { get; init; }
        public string? MouthSource { get; init; }
        public string? MouthNote { get; init; }
        public string Generator { get; init; } = "";
        public string Converter { get; init; } = "";
        public string Quality { get; init; } = "";
        public string VoiceMatch { get; init; } = "";
        public bool Fixture { get; init; }
        public long Seed { get; init; }
    }

    /// <summary>The song's map asset: its lines and words with their times, and the beat grid.</summary>
    public sealed record SongMapDocument
    {
        public int Version { get; init; } = 1;
        public int BeatsPerBar { get; init; } = 4;
        public IReadOnlyList<double> Beats { get; init; } = [];
        public IReadOnlyList<double> Downbeats { get; init; } = [];
        public IReadOnlyList<StoredSongLine> Lines { get; init; } = [];
        public IReadOnlyList<StoredSongWord> Words { get; init; } = [];
    }

    public static SongMetadata? Metadata(Creation creation)
    {
        if (creation.Metadata is not { ValueKind: JsonValueKind.Object } element) return null;
        try { return element.Deserialize<SongMetadata>(Json); }
        catch (JsonException) { return null; }
    }

    /// <summary>The song's lyrics in their sections, for the Creations page.</summary>
    public static IReadOnlyList<CreationSection> Sections(string lyrics)
    {
        var sections = new List<CreationSection>();
        var heading = "";
        var text = new StringBuilder();
        void Flush()
        {
            if (text.Length > 0) sections.Add(new(heading, text.ToString().Trim()));
            text.Clear();
        }
        foreach (var raw in lyrics.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length >= 2 && line[0] == '[' && line[^1] == ']')
            {
                Flush();
                heading = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(line[1..^1].Trim().ToLowerInvariant());
                continue;
            }
            if (line.Length > 0) text.Append(line).Append('\n');
        }
        Flush();
        return sections;
    }

    /// <summary>A new song creation for <paramref name="result"/>: its title, lyrics and style, its sung words, its mouth
    /// track and who made it.</summary>
    public static CreationDraft Draft(SongResult result, string title, string about, string lyrics, string style,
        IReadOnlyList<SongWordTime> words, bool wordsEstimated, SongMouthTrack mouth, CreationAuthor author)
    {
        ArgumentNullException.ThrowIfNull(result);
        static double Round(double seconds) => Math.Round(seconds, 3);
        var map = new SongMapDocument
        {
            BeatsPerBar = result.BeatsPerBar,
            Beats = [.. result.Beats.Select(beat => Round(beat.TotalSeconds))],
            Downbeats = [.. result.Downbeats.Select(beat => Round(beat.TotalSeconds))],
            Lines = [.. result.LyricTimestamps.Select(line => new StoredSongLine(Round(line.Start.TotalSeconds),
                line.End is { } end ? Round(end.TotalSeconds) : null, line.Text, line.Section))],
            Words = [.. words.Select(word => new StoredSongWord(Round(word.Start.TotalSeconds), Round(word.End.TotalSeconds), word.Text))]
        };
        var metadata = new SongMetadata
        {
            About = Clean(about, 200), Style = Clean(style, 512), VoiceId = result.VoiceId, Bpm = result.Bpm is { } bpm ? Round(bpm) : null,
            Key = result.Key, Lines = map.Lines.Count, Words = map.Words.Count, WordsEstimated = wordsEstimated,
            WordTimingSource = wordsEstimated ? "spread over each line's singing" : result.WordTimingSource,
            MouthSource = mouth.Source.ToString(), MouthNote = mouth.Note, Generator = result.Engine.Generator,
            Converter = result.Engine.Converter, Quality = result.Engine.Quality.ToString(), VoiceMatch = result.Engine.VoiceMatch.ToString(),
            Fixture = result.Engine.Fixture, Seed = result.Seed
        };
        return new()
        {
            Kind = KindName, Title = Clean(title, CreationLibrary.MaximumTitleLength), Text = lyrics.Trim(),
            Summary = Clean(result.Engine.Fixture ? $"FIXTURE - NOT AI: {about}" : about, CreationLibrary.MaximumSummaryLength),
            Duration = result.Duration, Metadata = JsonSerializer.SerializeToElement(metadata, Json), CreatedBy = author,
            Assets =
            [
                new(Mix, FlacCodec.MediaType, FlacCodec.Encode(result.Mix.Pcm16.Span, result.Mix.SampleRate, result.Mix.Channels)),
                new(Vocals, FlacCodec.MediaType, FlacCodec.Encode(result.Vocals.Pcm16.Span, result.Vocals.SampleRate, result.Vocals.Channels)),
                new(Backing, FlacCodec.MediaType, FlacCodec.Encode(result.Backing.Pcm16.Span, result.Backing.SampleRate, result.Backing.Channels)),
                new(MapAsset, "application/json", JsonSerializer.SerializeToUtf8Bytes(map, Json)),
                new(Mouth, "application/json", Encoding.UTF8.GetBytes(mouth.ToJson()))
            ]
        };
    }

    /// <summary>The song creations kept on this computer, newest first.</summary>
    public static IReadOnlyList<Creation> List(string dataDirectory) =>
        [.. CreationStore.View(dataDirectory).Live.Where(creation => creation.Kind == KindName)];

    /// <summary>The song creation <paramref name="reference"/> names (its key or ID), or null.</summary>
    public static Creation? Find(string dataDirectory, string? reference) =>
        CreationStore.View(dataDirectory).Resolve(reference) is { Kind: KindName } creation ? creation : null;

    /// <summary>A song creation's song: its record (with its map), audio and mouth track, or what's missing.</summary>
    public static async Task<(StoredSong? Song, SongAudio? Audio, SongMouthTrack? Mouth, string? Problem)> LoadAsync(Creation creation,
        ICreationAssets assets, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(creation);
        ArgumentNullException.ThrowIfNull(assets);
        var mapBytes = await assets.ReadAsync(MapAsset, token).ConfigureAwait(false);
        var vocals = await assets.ReadAsync(Vocals, token).ConfigureAwait(false);
        var backing = await assets.ReadAsync(Backing, token).ConfigureAwait(false);
        if (mapBytes is null || vocals is null || backing is null)
            return (null, null, null, "Its audio hasn't reached this computer yet.");
        SongMapDocument? map;
        try { map = JsonSerializer.Deserialize<SongMapDocument>(mapBytes, Json); }
        catch (JsonException) { map = null; }
        if (map is null) return (null, null, null, "Its map couldn't be read.");
        FlacAudio voice, band;
        try
        {
            voice = FlacCodec.Decode(vocals);
            band = FlacCodec.Decode(backing);
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException) { return (null, null, null, "Its audio couldn't be read."); }
        var mouthBytes = await assets.ReadAsync(Mouth, token).ConfigureAwait(false);
        var mouth = mouthBytes is null ? null : SongMouthTrack.FromJson(Encoding.UTF8.GetString(mouthBytes));
        var metadata = Metadata(creation) ?? new SongMetadata();
        var song = new StoredSong
        {
            Id = creation.Key, Title = creation.Title ?? "a song", About = metadata.About, Lyrics = creation.Text ?? "", Style = metadata.Style,
            VoiceId = metadata.VoiceId, DurationSeconds = (creation.Duration ?? TimeSpan.FromSeconds((double)voice.Frames / voice.SampleRate)).TotalSeconds,
            Bpm = metadata.Bpm, Key = metadata.Key, BeatsPerBar = map.BeatsPerBar, Beats = map.Beats, Downbeats = map.Downbeats,
            Lines = map.Lines, Words = map.Words, WordsEstimated = metadata.WordsEstimated, MouthSource = mouth?.Source.ToString() ?? metadata.MouthSource,
            MouthNote = mouth?.Note ?? metadata.MouthNote, Generator = metadata.Generator, Converter = metadata.Converter, Quality = metadata.Quality,
            VoiceMatch = metadata.VoiceMatch, Fixture = metadata.Fixture, Seed = metadata.Seed, CreatedAt = creation.CreatedAt
        };
        var audio = new SongAudio(SongAudio.Samples(band.Pcm16, band.Channels, 2), SongAudio.Samples(voice.Pcm16, voice.Channels, 1), voice.SampleRate);
        return (song, audio, mouth, null);
    }

    /// <summary>The song <paramref name="reference"/> names in this computer's creations.</summary>
    public static async Task<(StoredSong? Song, SongAudio? Audio, SongMouthTrack? Mouth, string? Problem)> LoadAsync(string dataDirectory,
        string? reference, CancellationToken token) =>
        Find(dataDirectory, reference) is { } creation
            ? await LoadAsync(creation, CreationStore.Assets(dataDirectory, creation), token).ConfigureAwait(false)
            : (null, null, null, $"There's no song {reference}.");

    private static string Clean(string? text, int limit)
    {
        var line = string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(word => new string([.. word.Where(c => !char.IsControl(c))])));
        return line.Length <= limit ? line : line[..limit].TrimEnd();
    }
}
