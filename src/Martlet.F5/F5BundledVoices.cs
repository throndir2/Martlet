using System.Security.Cryptography;

namespace Martlet.F5;

/// <summary>A starter voice: a short recording Martlet adds to a new voice list, public domain (or CC0) or under CMU ARCTIC's
/// free-for-any-use terms, with its exact transcript. Once added it is an ordinary voice the owner can remove like any
/// other. Sources, modifications and notices are in BundledVoices\NOTICES.txt; scripts\Build-F5BundledVoices.py rebuilds
/// the clips.</summary>
public sealed class F5BundledVoice
{
    private readonly Lazy<byte[]> audio;

    internal F5BundledVoice(string key, string name, bool female, bool cute, string description, string licence, string transcript,
        string audioSha256)
    {
        Key = key;
        Name = name;
        Female = female;
        Cute = cute;
        Description = description;
        Licence = licence;
        Transcript = transcript;
        AudioSha256 = audioSha256;
        audio = new(Load);
    }

    /// <summary>Stable identifier, for example "arctic-slt".</summary>
    public string Key { get; }

    /// <summary>The name the voice gets in the voice list.</summary>
    public string Name { get; }

    /// <summary>Whether the speaker is a woman, so F5 copying the clip sounds feminine.</summary>
    public bool Female { get; }

    /// <summary>Whether this is one of the cute voices Martlet adds first and starts with.</summary>
    public bool Cute { get; }

    public string Description { get; }

    public string Licence { get; }

    public string Transcript { get; }

    public string AudioSha256 { get; }

    /// <summary>The clip's PCM format. Checks the clip, its name and its transcript against the rules F5's reference store
    /// applies to every voice (throws <see cref="F5Exception"/>).</summary>
    public F5ReferenceAudioFormat Check()
    {
        F5Guard.Utf8Text(Name, F5ReferenceLimits.MaximumPresetNameCharacters, F5ReferenceLimits.MaximumPresetNameUtf8Bytes,
            allowNewLines: false);
        F5Guard.Utf8Text(Transcript, F5ReferenceLimits.MaximumTranscriptCharacters, F5ReferenceLimits.MaximumTranscriptUtf8Bytes);
        return F5ReferenceAudio.Parse(audio.Value);
    }

    /// <summary>A copy of the clip's WAV bytes, verified against <see cref="AudioSha256"/>.</summary>
    public byte[] ReadAudio() => (byte[])audio.Value.Clone();

    private byte[] Load()
    {
        using var stream = typeof(F5BundledVoice).Assembly.GetManifestResourceStream($"Martlet.F5.BundledVoices.{Key}.wav") ??
            throw new F5Exception(F5Failure.SourceMissing);
        F5Guard.Require(stream.Length is > 44 and <= F5ReferenceLimits.MaximumAudioFileBytes, F5Failure.InvalidAudio);
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        F5Guard.Require(Convert.ToHexStringLower(SHA256.HashData(bytes)) == AudioSha256, F5Failure.InvalidAudio);
        return bytes;
    }
}

/// <summary>A recording an earlier Martlet shipped and this one does not (<see cref="F5BundledVoices.Retired"/>):
/// <paramref name="Key"/> is how diagnostics name it, and <paramref name="Transcript"/> (null for the F5-TTS example clip,
/// which was never in a shared voice list) gives a former starter voice's ID in a shared voice list.</summary>
public sealed record F5RetiredVoice(string Key, string Name, string AudioSha256, string? Transcript);

/// <summary>The five starter voices Martlet adds to a new voice list (<see cref="Martlet.Core.Voices.SpeakingVoiceLibrary"/>):
/// the two cute voices first, then a female narrator and a US female and a US male voice. The first is the voice Martlet
/// starts speaking with. Nothing marks them afterwards: they are shared, chosen and removed like any voice.</summary>
public static class F5BundledVoices
{
    private const string Arctic = "CMU ARCTIC speech database, Carnegie Mellon University";
    private const string ArcticLicence = "CMU ARCTIC licence: free for any use, notice kept";
    private const string ArcticTranscript =
        "I came for information more out of curiosity than anything else. The ship should be in within a week or ten days.";
    private const string AnnieAnne =
        "But am I talking too much? People are always telling me I do. Would you rather I didn't talk? If you say so, I'll stop.";

    public static IReadOnlyList<F5BundledVoice> All { get; } =
    [
        new("librivox-annie", "Annie (cute, chatty)", true, true,
            "Annie Coleman Rothenberg's chatty Anne of Green Gables for LibriVox, as read. Public domain.", "Public domain", AnnieAnne,
            "d6726dc5b0b57825fe603d03159c4a41f11288842e4364f68d312446202e8a88"),
        new("librivox-woollybee", "Bee (cute, bubbly)", true, true,
            "WoollyBee's high, bubbly Anne of Green Gables for LibriVox, as read. Public domain (CC0).", "Public domain (CC0)",
            "It isn't heavy. I've got all my worldly goods in it, but it isn't heavy. And if it isn't carried in just a certain way the handle pulls out, so I'd better keep it because I know the exact knack of it.",
            "5996d313f84e4db81b999f2ef97c7a014952f9bd0e759738d2ed7a2f1991475e"),
        new("lj-speech", "LJ (female narrator)", true, false, "Female narrator from the LJ Speech dataset, a LibriVox reading. Public domain.",
            "Public domain", "Printing, then, for our purpose, may be considered as the art of making books by means of movable types.",
            "d54f23016ad2cd288c276960b3366fe09e3bd3983a2089ede0d09e57ff3fedf4"),
        new("arctic-slt", "SLT (US female)", true, false, $"US English female voice talent, {Arctic}.", ArcticLicence, ArcticTranscript,
            "59f79c9146230358f6a6f5183d3858a8881f16340372dbc0cfb15586cb7818ca"),
        new("arctic-bdl", "BDL (US male)", false, false, $"US English male voice talent, {Arctic}.", ArcticLicence, ArcticTranscript,
            "bbde73d09e93d123600c52851d173bad474d75712737690adbf4dd68552c0002")
    ];

    /// <summary>Recordings earlier versions shipped and this one does not. The F5-TTS example clip ("F5 sample voice
    /// (English)") is a male voice whose source could not be confirmed. The two "anime" voices were the cute voices with pitch
    /// and formants raised, which sounded artificial. A voice list drops them on every computer (they never join it again),
    /// anything still speaking with one moves to the chosen (or first) voice, and this PC's copy is deleted once nothing
    /// speaks with it.</summary>
    public static IReadOnlyList<F5RetiredVoice> Retired { get; } =
    [
        new("retired-sample", "F5 sample voice (English)", "6a7c5fb9068fa23762af51544c8de76b2dbf6f54a65a44e3b2f9e2ad11899aa2", null),
        new("retired-librivox-annie-anime", "Annie (cute anime girl)", "fff92da85f9126c6887a3ed264d9490513a64b6bd0c89f3828292b8777d02380",
            AnnieAnne),
        new("retired-librivox-woollybee-anime", "Bee (cute anime girl)", "3aaf7c78e363e5c3d1a6e8f2a2c2574e1d92a6185e5326180c45be819eef0c9f",
            "You do get so attached to things like that, don't you? Is there a brook anywhere near Green Gables? I forgot to ask Mrs. Spencer that.")
    ];

    /// <summary>The voice Martlet speaks with until the owner chooses another: the first cute, female voice (Annie, cute and
    /// chatty).</summary>
    public static F5BundledVoice Default { get; } = All.First(voice => voice is { Cute: true, Female: true });

    public static F5BundledVoice? Find(string key) => All.FirstOrDefault(voice => voice.Key == key);

    /// <summary>The starter voice whose clip has <paramref name="audioSha256"/>: its recording can be read from Martlet itself
    /// instead of another computer.</summary>
    public static F5BundledVoice? ForAudio(string audioSha256) =>
        All.FirstOrDefault(voice => string.Equals(voice.AudioSha256, audioSha256, StringComparison.OrdinalIgnoreCase));

    /// <summary>The retired recording whose SHA-256 is <paramref name="audioSha256"/>, or null.</summary>
    public static F5RetiredVoice? RetiredForAudio(string audioSha256) =>
        Retired.FirstOrDefault(voice => string.Equals(voice.AudioSha256, audioSha256, StringComparison.OrdinalIgnoreCase));

    public static bool IsRetired(string audioSha256) => RetiredForAudio(audioSha256) is not null;
}
