using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Martlet.Conversation;

/// <summary>Where a song's mouth track came from: Audio2Face run once over the vocals, visemes timed from the sung words, or
/// the vocals' loudness (the last resort).</summary>
public enum SongMouthSource { Audio2Face, Visemes, Loudness }

/// <summary>One sung word and when it is sung (song time).</summary>
public sealed record SongWordTime(TimeSpan Start, TimeSpan End, string Text);

/// <summary>How well a mouth track follows the singing: for each vocal onset (the vocals rising from silence), how far the
/// mouth's opening is from it (positive: the mouth opens late).</summary>
public sealed record SongMouthTiming(int Onsets, int Matched, double MedianOffsetMs, double MeanAbsoluteOffsetMs, double P90AbsoluteOffsetMs)
{
    public bool Good => Onsets > 0 && Matched >= Onsets * 0.8 && P90AbsoluteOffsetMs <= 60;
}

/// <summary>A song's mouth, computed once when the song is made from its vocals stem (never the mix) and kept with it: ARKit
/// mouth blendshapes (the names Martlet's mouth mappings read: jawOpen, mouthFunnel, mouthPucker, mouthSmile..., mouthClose)
/// and an overall opening (for characters that only open and close their mouth), every 20 ms of song time. Playback looks it
/// up at the song time being heard, so it stays on the sung words after a lead-in, a vamp or a resume from any line.</summary>
public sealed class SongMouthTrack
{
    public const int FramesPerSecond = 50;
    public static readonly IReadOnlyList<string> VisemeChannels =
        ["jawOpen", "mouthClose", "mouthFunnel", "mouthPucker", "mouthSmileLeft", "mouthSmileRight", "mouthStretchLeft", "mouthStretchRight"];
    private readonly byte[] values, levels;

    public SongMouthTrack(SongMouthSource source, IReadOnlyList<string> channels, byte[] values, byte[] levels, string note)
    {
        ArgumentNullException.ThrowIfNull(channels);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(levels);
        if (channels.Count > 64 || values.Length != levels.Length * channels.Count)
            throw new ArgumentException("The mouth track's frames don't match its channels.");
        Source = source;
        Channels = channels;
        this.values = values;
        this.levels = levels;
        Note = note;
    }

    public SongMouthSource Source { get; }
    public IReadOnlyList<string> Channels { get; }
    /// <summary>What made it, in a few words ("Audio2Face at 127.0.0.1:52000", "visemes from 84 timed words").</summary>
    public string Note { get; }
    public int Frames => levels.Length;
    public TimeSpan Duration => TimeSpan.FromSeconds((double)Frames / FramesPerSecond);

    private int Index(TimeSpan time) => Math.Clamp((int)(time.TotalSeconds * FramesPerSecond), 0, Math.Max(0, Frames - 1));

    /// <summary>The mouth's overall opening (0 closed to 1 open) at <paramref name="time"/>.</summary>
    public double LevelAt(TimeSpan time) => Frames == 0 ? 0 : levels[Index(time)] / 255.0;

    /// <summary>Every channel's weight (0 to 1) at <paramref name="time"/>.</summary>
    public IReadOnlyDictionary<string, double> At(TimeSpan time)
    {
        var frame = Index(time);
        var weights = new Dictionary<string, double>(Channels.Count, StringComparer.Ordinal);
        for (var c = 0; c < Channels.Count; c++) weights[Channels[c]] = Frames == 0 ? 0 : values[frame * Channels.Count + c] / 255.0;
        return weights;
    }

    // ---------- making one ----------

    /// <summary>The vocals' loudness as the opening alone (jawOpen), the last resort.</summary>
    public static SongMouthTrack FromLoudness(VocalEnvelope vocals, TimeSpan duration)
    {
        var frames = Count(duration);
        var levels = new byte[frames];
        for (var i = 0; i < frames; i++) levels[i] = Byte(Opening(vocals, Time(i)));
        return new(SongMouthSource.Loudness, ["jawOpen"], (byte[])levels.Clone(), levels, "the vocals' loudness");
    }

    /// <summary>Audio2Face's blendshape frames for the vocals (song time and weights), kept for the mouth and jaw channels and
    /// resampled to 20 ms.</summary>
    public static SongMouthTrack FromFrames(IReadOnlyList<(TimeSpan At, IReadOnlyDictionary<string, double> Weights)> faces, TimeSpan duration,
        string note)
    {
        ArgumentNullException.ThrowIfNull(faces);
        var ordered = faces.OrderBy(face => face.At).ToArray();
        var channels = ordered.SelectMany(face => face.Weights.Keys)
            .Where(name => name.StartsWith("mouth", StringComparison.Ordinal) || name.StartsWith("jaw", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(64).ToArray();
        var frames = Count(duration);
        var values = new byte[frames * channels.Length];
        var levels = new byte[frames];
        var next = 0;
        for (var i = 0; i < frames; i++)
        {
            var at = Time(i);
            while (next + 1 < ordered.Length && ordered[next + 1].At <= at) next++;
            if (ordered.Length == 0 || ordered[next].At > at + TimeSpan.FromMilliseconds(100)) continue;
            var face = ordered[next].Weights;
            for (var c = 0; c < channels.Length; c++) values[i * channels.Length + c] = Byte(face.GetValueOrDefault(channels[c]));
            levels[i] = Byte(Level(c => face.GetValueOrDefault(channels[c]), channels));
        }
        return new(SongMouthSource.Audio2Face, channels, values, levels, note);
    }

    /// <summary>Visemes timed from the sung words (or, without word timings, the words of each line spread over where its
    /// vocals sound), each word turned into mouth shapes from its letters (aa, ih, ee, oh, ou, closed lips for m/b/p), opened as
    /// far as the vocals are loud, and blended from shape to shape.</summary>
    public static SongMouthTrack FromVisemes(SongMap map, VocalEnvelope vocals, IReadOnlyList<SongWordTime>? words, bool estimated = false)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(vocals);
        var timed = words is { Count: > 0 } ? words : Spread(map, vocals);
        // Word times spread over where the vocals sound start at the vowel's sound, so the consonants before it come first.
        var anticipate = estimated || words is not { Count: > 0 };
        var frames = Count(map.Duration);
        var channels = VisemeChannels;
        var raw = new double[frames, channels.Count];
        foreach (var word in timed)
        {
            var units = Visemes.Of(word.Text);
            if (units.Count == 0) continue;
            var start = word.Start.TotalSeconds;
            var length = Math.Max(0.05, (word.End - word.Start).TotalSeconds);
            // Consonants are quick; the vowels share what's left (sung vowels are held).
            var consonants = units.Count(u => !u.Vowel);
            var vowels = Math.Max(1, units.Count - consonants);
            var quick = Math.Min(0.07, length * 0.5 / Math.Max(1, consonants));
            var leading = units.TakeWhile(u => !u.Vowel).Count();
            var held = Math.Max(0.02, (length - quick * (anticipate ? consonants - leading : consonants)) / vowels);
            // Spread words start at the vowel's sound, so the consonants before it come first; timed words start at the word's
            // first sound, where a sung consonant is short, so half of them come just before it.
            var at = start - quick * leading * (anticipate ? 1 : 0.5);
            foreach (var unit in units)
            {
                var span = unit.Vowel ? held : quick;
                for (var i = Math.Max(0, (int)(at * FramesPerSecond)); i < Math.Min(frames, (int)Math.Ceiling((at + span) * FramesPerSecond)); i++)
                {
                    var open = Opening(vocals, Time(i));
                    var shape = unit.Shape;
                    for (var c = 0; c < channels.Count; c++)
                    {
                        var weight = shape.GetValueOrDefault(channels[c]);
                        // Closed lips close whatever the loudness; everything else opens with it.
                        raw[i, c] = channels[c] == "mouthClose" ? weight : weight * Math.Min(1, open * 1.15);
                    }
                }
                at += span;
            }
        }
        // Blend from shape to shape (about 25 ms), as lips move between sounds, and lead the sound by one frame (20 ms): lips
        // shape a vowel just before it sounds.
        var values = new byte[frames * channels.Count];
        var levels = new byte[frames];
        var state = new double[channels.Count];
        var follow = 1 - Math.Exp(-1.0 / (0.025 * FramesPerSecond));
        const int lead = 1;
        for (var i = 0; i < frames; i++)
        {
            for (var c = 0; c < channels.Count; c++)
            {
                state[c] += (raw[i, c] - state[c]) * follow;
                if (i >= lead) values[(i - lead) * channels.Count + c] = Byte(state[c]);
            }
            if (i >= lead) levels[i - lead] = Byte(Level(c => state[c], channels));
        }
        return new(SongMouthSource.Visemes, channels, values, levels,
            words is { Count: > 0 } && !estimated ? $"visemes from {timed.Count} timed words" : $"visemes from {timed.Count} words spread over their lines' singing");
    }

    // Without word timings: each line's words over the stretches where its vocals sound, by syllables.
    /// <summary>The sung words when the song maker gave no word timings: each line's words spread over the stretches where its
    /// vocals sound, by syllables (each word starting where its vowel sounds).</summary>
    public static IReadOnlyList<SongWordTime> Spread(SongMap map, VocalEnvelope vocals)
    {
        var words = new List<SongWordTime>();
        for (var l = 0; l < map.Lines.Count; l++)
        {
            var line = map.Lines[l];
            var end = line.End ?? (l + 1 < map.Lines.Count ? map.Lines[l + 1].Start : map.Duration);
            var sung = line.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (sung.Length == 0 || end <= line.Start) continue;
            // Where the line's vocals sound (10 ms steps above the threshold).
            var voiced = new List<double>();
            for (var t = line.Start; t < end; t += TimeSpan.FromMilliseconds(VocalEnvelope.StepMilliseconds))
                if (vocals.At(t) >= vocals.Threshold) voiced.Add(t.TotalSeconds);
            var syllables = sung.Select(word => Math.Max(1, Visemes.Of(word).Count(u => u.Vowel))).ToArray();
            var total = syllables.Sum();
            var done = 0;
            for (var w = 0; w < sung.Length; w++)
            {
                double Place(int count) => voiced.Count > 0
                    ? voiced[Math.Min(voiced.Count - 1, (int)((double)count / total * voiced.Count))]
                    : line.Start.TotalSeconds + (end - line.Start).TotalSeconds * count / total;
                var from = Place(done);
                done += syllables[w];
                var to = done >= total ? (voiced.Count > 0 ? voiced[^1] + 0.01 : end.TotalSeconds) : Place(done);
                words.Add(new(TimeSpan.FromSeconds(from), TimeSpan.FromSeconds(Math.Max(from + 0.03, to)), sung[w]));
            }
        }
        return words;
    }

    // How far the mouth opens for the vocals' loudness at a moment (the character's loudness curve).
    private static double Opening(VocalEnvelope vocals, TimeSpan time)
    {
        var rms = vocals.At(time);
        return rms < vocals.Threshold * 0.5 ? 0 : VocalEnvelope.Mouth(rms);
    }

    // The mouth's overall opening for one frame of shapes: the jaw, plus half of the strongest lip shape (rounded or spread lips
    // are open too), for characters that only open and close their mouth.
    private static double Level(Func<int, double> weight, IReadOnlyList<string> channels)
    {
        double jaw = 0, lips = 0;
        for (var c = 0; c < channels.Count; c++)
        {
            var name = channels[c];
            if (name == "jawOpen") jaw = weight(c);
            else if (name is "mouthFunnel" or "mouthPucker" or "mouthSmileLeft" or "mouthSmileRight" or "mouthStretchLeft" or "mouthStretchRight")
                lips = Math.Max(lips, weight(c));
        }
        return Math.Clamp(jaw + lips * 0.5, 0, 1);
    }

    private static int Count(TimeSpan duration) => Math.Max(1, (int)Math.Ceiling(duration.TotalSeconds * FramesPerSecond));
    private static TimeSpan Time(int frame) => TimeSpan.FromSeconds((double)frame / FramesPerSecond);
    private static byte Byte(double value) => (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);

    // ---------- how well it follows the singing ----------

    /// <summary>For every vocal onset (the vocals rising above their threshold after at least 120 ms below it), how far the
    /// mouth's opening (crossing 0.25) is from it, matched within 250 ms.</summary>
    public SongMouthTiming Measure(VocalEnvelope vocals)
    {
        var onsets = new List<double>();
        var quiet = 0;
        for (var t = TimeSpan.Zero; t < Duration; t += TimeSpan.FromMilliseconds(VocalEnvelope.StepMilliseconds))
        {
            if (vocals.At(t) < vocals.Threshold) { quiet++; continue; }
            if (quiet >= 12) onsets.Add(t.TotalSeconds);
            quiet = 0;
        }
        var offsets = new List<double>();
        foreach (var onset in onsets)
        {
            double? opened = null;
            for (var t = onset - 0.25; t <= onset + 0.25; t += 1.0 / FramesPerSecond)
            {
                if (t < 0) continue;
                var before = LevelAt(TimeSpan.FromSeconds(Math.Max(0, t - 1.0 / FramesPerSecond)));
                if (LevelAt(TimeSpan.FromSeconds(t)) >= 0.25 && before < 0.25) { opened = t; break; }
            }
            if (opened is { } at) offsets.Add((at - onset) * 1000);
        }
        if (offsets.Count == 0) return new(onsets.Count, 0, 0, 0, 0);
        var absolute = offsets.Select(Math.Abs).Order().ToArray();
        var sorted = offsets.Order().ToArray();
        return new(onsets.Count, offsets.Count, Math.Round(sorted[sorted.Length / 2], 1), Math.Round(absolute.Average(), 1),
            Math.Round(absolute[Math.Min(absolute.Length - 1, (int)(absolute.Length * 0.9))], 1));
    }

    // ---------- kept with the song ----------

    private sealed record Document(int Version, string Source, string Note, int FramesPerSecond, string[] Channels, string Values, string Levels);

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = false };

    public string ToJson() => JsonSerializer.Serialize(new Document(1, Source.ToString(), Note, FramesPerSecond, [.. Channels],
        Convert.ToBase64String(values), Convert.ToBase64String(levels)), Json);

    public static SongMouthTrack? FromJson(string json)
    {
        try
        {
            var document = JsonSerializer.Deserialize<Document>(json, Json);
            if (document is not { Version: 1, FramesPerSecond: FramesPerSecond } || !Enum.TryParse<SongMouthSource>(document.Source, out var source))
                return null;
            return new(source, document.Channels, Convert.FromBase64String(document.Values), Convert.FromBase64String(document.Levels),
                document.Note);
        }
        catch (Exception error) when (error is JsonException or FormatException or ArgumentException or NotSupportedException) { return null; }
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{nameof(SongMouthTrack)} ({Source}, {Frames} frames)");
}

/// <summary>Mouth shapes from a word's spelling: a small rule set, not a dictionary. Vowel groups become aa, ih, ee, oh or ou;
/// m, b and p close the lips; f and v nearly close them; w rounds them; other consonants leave the mouth a little open.</summary>
public static class Visemes
{
    public sealed record Unit(string Name, bool Vowel, IReadOnlyDictionary<string, double> Shape);

    private static readonly Dictionary<string, IReadOnlyDictionary<string, double>> Shapes = new(StringComparer.Ordinal)
    {
        ["aa"] = new Dictionary<string, double> { ["jawOpen"] = 0.9 },
        ["ih"] = new Dictionary<string, double> { ["jawOpen"] = 0.4, ["mouthStretchLeft"] = 0.5, ["mouthStretchRight"] = 0.5, ["mouthSmileLeft"] = 0.2, ["mouthSmileRight"] = 0.2 },
        ["ee"] = new Dictionary<string, double> { ["jawOpen"] = 0.25, ["mouthSmileLeft"] = 0.7, ["mouthSmileRight"] = 0.7, ["mouthStretchLeft"] = 0.3, ["mouthStretchRight"] = 0.3 },
        ["oh"] = new Dictionary<string, double> { ["jawOpen"] = 0.55, ["mouthFunnel"] = 0.75 },
        ["ou"] = new Dictionary<string, double> { ["jawOpen"] = 0.25, ["mouthPucker"] = 0.85 },
        ["closed"] = new Dictionary<string, double> { ["mouthClose"] = 0.8 },
        ["fv"] = new Dictionary<string, double> { ["jawOpen"] = 0.1, ["mouthClose"] = 0.3 },
        ["w"] = new Dictionary<string, double> { ["jawOpen"] = 0.15, ["mouthPucker"] = 0.7 },
        ["open"] = new Dictionary<string, double> { ["jawOpen"] = 0.2 }
    };

    private static readonly (string Letters, string Viseme)[] VowelGroups =
    [
        ("oo", "ou"), ("ou", "ou"), ("ew", "ou"), ("ue", "ou"), ("ui", "ou"),
        ("ee", "ee"), ("ea", "ee"), ("ie", "ee"), ("ei", "ee"), ("ey", "ee"), ("ay", "ee"), ("ai", "ee"),
        ("oa", "oh"), ("oe", "oh"), ("ow", "oh"), ("au", "oh"), ("aw", "oh"), ("oi", "oh"), ("oy", "oh")
    ];

    /// <summary>The mouth shapes of one word, in order.</summary>
    public static IReadOnlyList<Unit> Of(string word)
    {
        var letters = new string([.. (word ?? "").ToLowerInvariant().Where(char.IsLetter)]);
        var units = new List<Unit>();
        for (var i = 0; i < letters.Length;)
        {
            var c = letters[i];
            if (IsVowel(letters, i))
            {
                var pair = i + 1 < letters.Length ? letters.Substring(i, 2) : "";
                var group = VowelGroups.FirstOrDefault(g => g.Letters == pair);
                string name;
                if (group.Letters is not null) { name = group.Viseme; i += 2; }
                else
                {
                    name = c switch
                    {
                        'a' => "aa", 'o' => "oh", 'i' => i == letters.Length - 1 ? "ee" : "ih",
                        // A final e is silent after a consonant in a longer word ("make", "love"), sung in a short one ("me").
                        'e' => i == letters.Length - 1 ? (letters.Length >= 4 && !IsVowel(letters, i - 1) ? "" : "ee") : "ih",
                        'y' => i == letters.Length - 1 ? "ee" : "ih",
                        _ => "aa"
                    };
                    i++;
                }
                // A silent final e adds nothing.
                if (name.Length == 0) continue;
                if (units.Count > 0 && units[^1].Vowel && units[^1].Name == name) continue;
                units.Add(new(name, true, Shapes[name]));
                continue;
            }
            var consonant = c switch
            {
                'm' or 'b' or 'p' => "closed",
                'f' or 'v' => "fv",
                'w' => "w",
                _ => "open"
            };
            if (!(units.Count > 0 && !units[^1].Vowel && units[^1].Name == consonant)) units.Add(new(consonant, false, Shapes[consonant]));
            i++;
        }
        if (units.Count > 0 && units.All(u => !u.Vowel)) units.Add(new("aa", true, Shapes["aa"]));
        return units;
    }

    private static bool IsVowel(string letters, int i) =>
        letters[i] is 'a' or 'e' or 'i' or 'o' or 'u' || letters[i] == 'y' && i > 0 && !IsVowel(letters, i - 1);
}
