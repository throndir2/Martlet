using System.Buffers.Binary;
using System.Text;

namespace Martlet.Audio;

/// <summary>One label a sound tagger heard, with its score from 0 to 1 (AudioSet display names, such as "Music" or "Laughter").</summary>
public sealed record SoundTag(string Name, double Score);

/// <summary>Which kind of judge describes the PC's sound: the audio model of its own (Companion › Listening › Audio model), an
/// audio-capable model in the Thinking pool, or the small sound tagger on this PC's processor.</summary>
public enum SoundJudgeKind { Pool, Cpu, AudioModel }

/// <summary>What turns a clip of what this PC played (16 kHz mono, -1 to 1) into one short line about its non-speech sound, or
/// null when there is none worth saying (only speech, or silence).</summary>
public interface ISoundJudge
{
    /// <summary>Who judges, for status lines ("CPU sound tagger", or a pool model's name). Never what was heard.</summary>
    string Name { get; }
    SoundJudgeKind Kind { get; }
    Task<string?> DescribeAsync(float[] clip, CancellationToken cancellationToken);
}

/// <summary>The sound digest's words: the request an audio-capable pool model gets, how its answer is cleaned, and how a sound
/// tagger's labels become one line ("Music: pop music with singing, happy; laughter"). Speech is left out: what is said is
/// already transcribed.</summary>
public static class SoundDigest
{
    /// <summary>The longest line the digest keeps.</summary>
    public const int MaximumLine = 160;

    /// <summary>What an audio-capable pool model is asked about a clip.</summary>
    public const string Prompt =
        "This audio clip is what the user's PC is playing right now (a video, a stream, a game or music). In one short line of at " +
        "most 20 words, describe only its non-speech sound: music (genre, mood, whether there is singing), game or video sound " +
        "effects, laughter, applause, alarms or other notable sounds. Never transcribe or summarize what anyone says. If there is " +
        "only speech or silence, answer exactly: none";

    /// <summary>A model's answer as one clean line, or null for "none", an empty answer or one that only talks about speech.</summary>
    public static string? Clean(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply)) return null;
        var line = reply.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
        line = line.Trim('"', '\'', '`', '*', ' ', '\u201C', '\u201D');
        var plain = new string(line.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (plain.Length == 0 || plain.TrimEnd('.', '!').Equals("none", StringComparison.OrdinalIgnoreCase)) return null;
        return plain.Length <= MaximumLine ? plain : plain[..(MaximumLine - 1)].TrimEnd() + "…";
    }

    // AudioSet labels that say nothing about non-speech sound: speech itself, the room, and plain noise.
    private static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase)
    {
        "Speech", "Male speech, man speaking", "Female speech, woman speaking", "Child speech, kid speaking", "Conversation",
        "Narration, monologue", "Babbling", "Speech synthesizer", "Whispering", "Silence", "Inside, small room",
        "Inside, large room or hall", "Inside, public space", "Outside, urban or manmade", "Outside, rural or natural", "Noise",
        "Static", "Hum", "Mains hum", "White noise", "Pink noise", "Environmental noise", "Sound effect", "Echo", "Reverberation",
        "Distortion", "Sidetone", "Cacophony", "Throbbing", "Vibration", "Television", "Radio", "Field recording", "Music",
        // Ontology parents that only repeat a more exact label beside them ("Cat" says more than "Animal").
        "Animal", "Domestic animals, pets", "Livestock, farm animals, working animals", "Wild animals", "Human voice",
        "Human sounds", "Vehicle", "Motor vehicle (road)", "Mechanisms", "Domestic sounds, home sounds", "Tools", "Liquid",
        "Natural sounds", "Sounds of things", "Source-ambiguous sounds", "Channel, environment and background", "Sampler"
    };

    private static readonly HashSet<string> Vocals = new(StringComparer.OrdinalIgnoreCase)
    {
        "Singing", "Choir", "Yodeling", "Chant", "Mantra", "Male singing", "Female singing", "Child singing", "Synthetic singing",
        "Rapping", "Humming", "A capella", "Vocal music", "Beatboxing", "Lullaby"
    };

    private static readonly Dictionary<string, string> Moods = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Happy music"] = "happy", ["Funny music"] = "funny", ["Sad music"] = "sad", ["Tender music"] = "tender",
        ["Exciting music"] = "exciting", ["Angry music"] = "angry", ["Scary music"] = "scary"
    };

    // Genres and instruments: what a music line names (the display name, shortened where it is long).
    private static readonly Dictionary<string, string> Musical = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Pop music"] = "pop", ["Hip hop music"] = "hip hop", ["Rock music"] = "rock", ["Heavy metal"] = "heavy metal",
        ["Punk rock"] = "punk", ["Grunge"] = "grunge", ["Progressive rock"] = "progressive rock", ["Rock and roll"] = "rock and roll",
        ["Psychedelic rock"] = "psychedelic rock", ["Rhythm and blues"] = "R&B", ["Soul music"] = "soul", ["Reggae"] = "reggae",
        ["Country"] = "country", ["Swing music"] = "swing", ["Bluegrass"] = "bluegrass", ["Funk"] = "funk", ["Folk music"] = "folk",
        ["Middle Eastern music"] = "Middle Eastern", ["Jazz"] = "jazz", ["Disco"] = "disco", ["Classical music"] = "classical",
        ["Opera"] = "opera", ["Electronic music"] = "electronic", ["House music"] = "house", ["Techno"] = "techno",
        ["Dubstep"] = "dubstep", ["Drum and bass"] = "drum and bass", ["Electronica"] = "electronica",
        ["Electronic dance music"] = "EDM", ["Ambient music"] = "ambient", ["Trance music"] = "trance",
        ["Music of Latin America"] = "Latin", ["Salsa music"] = "salsa", ["Flamenco"] = "flamenco", ["Blues"] = "blues",
        ["Music for children"] = "children's music", ["New-age music"] = "new age", ["Music of Africa"] = "African",
        ["Afrobeat"] = "afrobeat", ["Christian music"] = "Christian", ["Gospel music"] = "gospel", ["Music of Asia"] = "Asian",
        ["Carnatic music"] = "Carnatic", ["Music of Bollywood"] = "Bollywood", ["Ska"] = "ska", ["Traditional music"] = "traditional",
        ["Independent music"] = "indie", ["Background music"] = "background music", ["Theme music"] = "theme music",
        ["Jingle (music)"] = "jingle", ["Soundtrack music"] = "soundtrack", ["Video game music"] = "video game music",
        ["Christmas music"] = "Christmas music", ["Dance music"] = "dance", ["Wedding music"] = "wedding music",
        ["Lofi"] = "lo-fi", ["Orchestra"] = "orchestra", ["Piano"] = "piano", ["Guitar"] = "guitar", ["Electric guitar"] = "electric guitar",
        ["Acoustic guitar"] = "acoustic guitar", ["Bass guitar"] = "bass guitar", ["Drum kit"] = "drums", ["Drum"] = "drums",
        ["Synthesizer"] = "synthesizer", ["Violin, fiddle"] = "violin", ["Brass instrument"] = "brass", ["Saxophone"] = "saxophone",
        ["Flute"] = "flute", ["Harp"] = "harp", ["Organ"] = "organ", ["Electronic organ"] = "organ", ["Cello"] = "cello",
        ["Trumpet"] = "trumpet", ["Ukulele"] = "ukulele", ["Banjo"] = "banjo", ["Accordion"] = "accordion",
        ["Keyboard (musical)"] = "keyboard", ["Electric piano"] = "electric piano", ["Harmonica"] = "harmonica",
        ["Bagpipes"] = "bagpipes", ["Musical instrument"] = "instruments", ["Drum machine"] = "drum machine"
    };

    /// <summary>One line from a sound tagger's labels (highest score first): a music part (genre or instruments, singing,
    /// mood) and up to three other sounds, or null when only speech, room tone or nothing scored at least
    /// <paramref name="threshold"/>.</summary>
    public static string? Line(IEnumerable<SoundTag> tags, double threshold = 0.15)
    {
        ArgumentNullException.ThrowIfNull(tags);
        var heard = tags.Where(t => t.Score >= threshold && !string.IsNullOrWhiteSpace(t.Name)).OrderByDescending(t => t.Score).ToList();
        var music = heard.Any(t => t.Name.Equals("Music", StringComparison.OrdinalIgnoreCase) || Musical.ContainsKey(t.Name) ||
            Moods.ContainsKey(t.Name) || Vocals.Contains(t.Name));
        var parts = new List<string>();
        if (music)
        {
            var kinds = heard.Where(t => Musical.ContainsKey(t.Name)).Select(t => Musical[t.Name]).Distinct().Take(2).ToList();
            var singing = heard.FirstOrDefault(t => Vocals.Contains(t.Name));
            var mood = heard.FirstOrDefault(t => Moods.ContainsKey(t.Name));
            var text = new StringBuilder("Music");
            if (kinds.Count > 0) text.Append(": ").Append(string.Join(" and ", kinds));
            if (singing is not null)
                text.Append(kinds.Count > 0 ? " with " : ": ").Append(singing.Name is "Singing" or "Vocal music" ? "singing" : Short(singing.Name));
            if (mood is not null) text.Append(kinds.Count > 0 || singing is not null ? ", " : ": ").Append(Moods[mood.Name]);
            parts.Add(text.ToString());
        }
        var others = heard.Where(t => !Ignored.Contains(t.Name) && !Musical.ContainsKey(t.Name) && !Moods.ContainsKey(t.Name) &&
            !Vocals.Contains(t.Name) && !t.Name.EndsWith(" music", StringComparison.OrdinalIgnoreCase))
            .Select(t => Short(t.Name)).Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToList();
        if (others.Count > 0) parts.Add(string.Join(", ", others));
        if (parts.Count == 0) return null;
        var line = string.Join("; ", parts);
        return line.Length <= MaximumLine ? line : line[..(MaximumLine - 1)].TrimEnd() + "…";
    }

    // "Gunshot, gunfire" says it twice: the first name is enough. Lower case unless it is a name.
    private static string Short(string name)
    {
        var first = name.Split(',', 2)[0].Trim();
        return first.Length > 1 && char.IsUpper(first[0]) && !char.IsUpper(first[1]) ? char.ToLowerInvariant(first[0]) + first[1..] : first;
    }

    /// <summary>The share of 100 ms windows in <paramref name="clip"/> (16 kHz) louder than about -45 dBFS: 0 for silence, near
    /// 1 when something plays all along.</summary>
    public static double ActiveShare(ReadOnlySpan<float> clip)
    {
        const int window = PcSoundBuffer.SampleRate / 10;
        const double floor = 0.0056; // about -45 dBFS
        var windows = clip.Length / window;
        if (windows == 0) return 0;
        var loud = 0;
        for (var w = 0; w < windows; w++)
        {
            double sum = 0;
            foreach (var sample in clip.Slice(w * window, window)) sum += (double)sample * sample;
            if (Math.Sqrt(sum / window) >= floor) loud++;
        }
        return loud / (double)windows;
    }

    /// <summary>A clip (16 kHz mono, -1 to 1) as a 16-bit PCM WAV file, for a model that hears.</summary>
    public static byte[] Wave(ReadOnlySpan<float> clip)
    {
        var data = clip.Length * 2;
        var wave = new byte[44 + data];
        var span = wave.AsSpan();
        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], 36 + data);
        "WAVEfmt "u8.CopyTo(span[8..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], PcSoundBuffer.SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], PcSoundBuffer.SampleRate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], 2);
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], 16);
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], data);
        for (var i = 0; i < clip.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(span[(44 + i * 2)..],
                (short)Math.Clamp(Math.Round(clip[i] * 32767.0), short.MinValue, short.MaxValue));
        return wave;
    }
}
