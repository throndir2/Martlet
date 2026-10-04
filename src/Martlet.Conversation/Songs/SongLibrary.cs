using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Core.Singing;

namespace Martlet.Conversation;

/// <summary>One sung line of a stored song: where its singing starts (the vocal onset, in seconds from the top), where it ends
/// when known, its words and its section ("verse", "chorus 2"; empty without tags).</summary>
public sealed record StoredSongLine(double Start, double? End, string Text, string Section = "");

/// <summary>A finished song kept in the song library (song.json beside its three WAV tracks): what it is, its lyrics, its map
/// (lines with sections and times, and the beat grid) and how it was made. Times are seconds from the top.</summary>
public sealed record StoredSong
{
    public const int SchemaVersion1 = 1;
    public int SchemaVersion { get; init; } = SchemaVersion1;
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
        return new(Samples(result.Backing, 2), Samples(result.Vocals, 1), result.Backing.SampleRate);
    }

    private static short[] Samples(SongTrack track, int channels)
    {
        var span = track.Pcm16.Span;
        var frames = (int)track.Frames;
        var samples = new short[frames * channels];
        for (var frame = 0; frame < frames; frame++)
        {
            var at = frame * track.Channels * 2;
            var first = BinaryPrimitives.ReadInt16LittleEndian(span[at..]);
            var second = track.Channels == 2 ? BinaryPrimitives.ReadInt16LittleEndian(span[(at + 2)..]) : first;
            if (channels == 1) samples[frame] = (short)((first + second) / 2);
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

/// <summary>Martlet's finished songs, kept in the data directory's songs folder: one folder per song with song.json and its
/// mix, vocals and backing as WAV files. Only the newest <see cref="Kept"/> stay; the oldest go first.</summary>
public sealed class SongLibrary
{
    public const int Kept = 20;
    public const string FolderName = "songs";
    public const string SongFile = "song.json", MixFile = "mix.wav", VocalsFile = "vocals.wav", BackingFile = "backing.wav";
    private readonly object gate = new();

    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public SongLibrary(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory = directory;
    }

    /// <summary>The library in a data directory.</summary>
    public static SongLibrary In(string dataDirectory) => new(Path.Combine(dataDirectory, FolderName));

    public string Directory { get; }

    /// <summary>Whether <paramref name="id"/> is a song ID ("song-" and 6 to 16 lowercase hex digits).</summary>
    public static bool IsId(string? id) => id is { Length: >= 11 and <= 21 } && id.StartsWith("song-", StringComparison.Ordinal) &&
        id[5..].All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>Saves a finished song with its three tracks and returns its record; the oldest songs beyond <see cref="Kept"/>
    /// are removed.</summary>
    public StoredSong Save(SongResult result, string title, string about, string lyrics, string style, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(result);
        var id = "song-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(3));
        var song = new StoredSong
        {
            Id = id, Title = Clean(title, 80), About = Clean(about, 200), Lyrics = lyrics.Trim(), Style = Clean(style, 512),
            VoiceId = result.VoiceId, DurationSeconds = Round(result.Duration.TotalSeconds), Bpm = result.Bpm is { } bpm ? Round(bpm) : null,
            Key = result.Key, BeatsPerBar = result.BeatsPerBar,
            Beats = [.. result.Beats.Select(beat => Round(beat.TotalSeconds))],
            Downbeats = [.. result.Downbeats.Select(beat => Round(beat.TotalSeconds))],
            Lines = [.. result.LyricTimestamps.Select(line => new StoredSongLine(Round(line.Start.TotalSeconds),
                line.End is { } end ? Round(end.TotalSeconds) : null, line.Text, line.Section))],
            Generator = result.Engine.Generator, Converter = result.Engine.Converter, Quality = result.Engine.Quality.ToString(),
            VoiceMatch = result.Engine.VoiceMatch.ToString(), Fixture = result.Engine.Fixture, Seed = result.Seed, CreatedAt = now
        };
        lock (gate)
        {
            System.IO.Directory.CreateDirectory(Directory);
            var staging = Path.Combine(Directory, "." + id + ".tmp");
            System.IO.Directory.CreateDirectory(staging);
            try
            {
                WriteWave(Path.Combine(staging, MixFile), result.Mix);
                WriteWave(Path.Combine(staging, VocalsFile), result.Vocals);
                WriteWave(Path.Combine(staging, BackingFile), result.Backing);
                File.WriteAllText(Path.Combine(staging, SongFile), JsonSerializer.Serialize(song, Json));
                System.IO.Directory.Move(staging, Path.Combine(Directory, id));
            }
            catch
            {
                try { System.IO.Directory.Delete(staging, recursive: true); }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
                throw;
            }
            Trim();
        }
        return song;
    }

    /// <summary>Every stored song, newest first (unreadable folders are skipped).</summary>
    public IReadOnlyList<StoredSong> List()
    {
        if (!System.IO.Directory.Exists(Directory)) return [];
        var songs = new List<StoredSong>();
        try
        {
            foreach (var folder in System.IO.Directory.EnumerateDirectories(Directory))
                if (IsId(Path.GetFileName(folder)) && Read(folder) is { } song) songs.Add(song);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return [.. songs.OrderByDescending(song => song.CreatedAt).ThenByDescending(song => song.Id, StringComparer.Ordinal)];
    }

    public StoredSong? Find(string? id) => IsId(id) ? Read(Path.Combine(Directory, id!)) : null;

    /// <summary>The song's backing and vocals, or null when its tracks can't be read.</summary>
    public SongAudio? LoadAudio(StoredSong song)
    {
        ArgumentNullException.ThrowIfNull(song);
        if (!IsId(song.Id)) return null;
        try
        {
            var folder = Path.Combine(Directory, song.Id);
            var backing = ReadWave(Path.Combine(folder, BackingFile), 2);
            var vocals = ReadWave(Path.Combine(folder, VocalsFile), 1);
            return backing is null || vocals is null ? null : new SongAudio(backing, vocals);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    private static StoredSong? Read(string folder)
    {
        try
        {
            var path = Path.Combine(folder, SongFile);
            if (!File.Exists(path) || new FileInfo(path).Length > 1_048_576) return null;
            var song = JsonSerializer.Deserialize<StoredSong>(File.ReadAllText(path), Json);
            return song is not null && song.Id == Path.GetFileName(folder) && IsId(song.Id) ? song : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return null; }
    }

    // The track as a canonical PCM WAV (like SongTrack.ToWave), written straight from its samples without another copy.
    private static void WriteWave(string path, SongTrack track)
    {
        Span<byte> header = stackalloc byte[44];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], checked((uint)(36 + track.Pcm16.Length)));
        "WAVEfmt "u8.CopyTo(header[8..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(header[20..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header[22..], (ushort)track.Channels);
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..], (uint)track.SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(header[28..], (uint)(track.SampleRate * track.Channels * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(header[32..], (ushort)(track.Channels * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(header[34..], 16);
        "data"u8.CopyTo(header[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[40..], (uint)track.Pcm16.Length);
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65_536);
        file.Write(header);
        file.Write(track.Pcm16.Span);
    }

    // A 48 kHz 16-bit PCM WAV with the expected channel count, as SongTrack.ToWave writes it, read straight into samples.
    private static short[]? ReadWave(string path, int channels)
    {
        if (!File.Exists(path)) return null;
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65_536);
        Span<byte> chunk = stackalloc byte[8];
        Span<byte> riff = stackalloc byte[12];
        Span<byte> format = stackalloc byte[64];
        if (file.ReadAtLeast(riff, 12, throwOnEndOfStream: false) < 12 || !riff[..4].SequenceEqual("RIFF"u8) || !riff[8..].SequenceEqual("WAVE"u8))
            return null;
        int? rate = null, count = null, bits = null;
        while (file.ReadAtLeast(chunk, 8, throwOnEndOfStream: false) == 8)
        {
            var size = (long)BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
            if (chunk[..4].SequenceEqual("fmt "u8) && size is >= 16 and <= 64)
            {
                var fields = format[..(int)size];
                if (file.ReadAtLeast(fields, fields.Length, throwOnEndOfStream: false) < fields.Length) return null;
                count = BinaryPrimitives.ReadUInt16LittleEndian(fields[2..]);
                rate = (int)BinaryPrimitives.ReadUInt32LittleEndian(fields[4..]);
                bits = BinaryPrimitives.ReadUInt16LittleEndian(fields[14..]);
                if ((size & 1) == 1) file.Seek(1, SeekOrigin.Current);
            }
            else if (chunk[..4].SequenceEqual("data"u8))
            {
                if (rate != SongTrack.SampleRateHz || count != channels || bits != 16) return null;
                size = Math.Min(size, file.Length - file.Position);
                var samples = new short[size / 2];
                var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(samples.AsSpan());
                if (file.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false) < bytes.Length) return null;
                if (!BitConverter.IsLittleEndian)
                    for (var i = 0; i < samples.Length; i++) samples[i] = BinaryPrimitives.ReverseEndianness(samples[i]);
                return samples;
            }
            else file.Seek(size + (size & 1), SeekOrigin.Current);
        }
        return null;
    }

    private void Trim()
    {
        foreach (var old in List().Skip(Kept))
            try { System.IO.Directory.Delete(Path.Combine(Directory, old.Id), recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static double Round(double seconds) => Math.Round(seconds, 3);

    private static string Clean(string? text, int limit)
    {
        var line = string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(word => new string([.. word.Where(c => !char.IsControl(c))])));
        return line.Length <= limit ? line : line[..limit].TrimEnd();
    }

    /// <summary>"0:22", "1:05".</summary>
    public static string Clock(TimeSpan time) =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)Math.Max(0, time.TotalMinutes)}:{Math.Max(0, time.Seconds):00}");

    public override string ToString() => nameof(SongLibrary);
}
