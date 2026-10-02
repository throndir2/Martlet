using System.Security.Cryptography;

namespace Martlet.F5;

/// <summary>A reference voice Martlet ships for F5: a short recording that is public domain or under CMU ARCTIC's
/// free-for-any-use terms, with its exact transcript. Sources, modifications and notices are in
/// BundledVoices\NOTICES.txt; scripts\Build-F5BundledVoices.py rebuilds the clips.</summary>
public sealed class F5BundledVoice
{
    private readonly Lazy<byte[]> audio;

    internal F5BundledVoice(string key, string name, bool female, string description, string licence, string transcript,
        string audioSha256)
    {
        Key = key;
        Name = name;
        Female = female;
        Description = description;
        Licence = licence;
        Transcript = transcript;
        AudioSha256 = audioSha256;
        audio = new(Load);
    }

    /// <summary>Stable identifier, for example "arctic-slt".</summary>
    public string Key { get; }

    /// <summary>The name shown in the voice list and kept as the preset name once the voice is used.</summary>
    public string Name { get; }

    /// <summary>Whether the speaker is a woman, so F5 copying the clip sounds feminine.</summary>
    public bool Female { get; }

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

/// <summary>The ten reference voices Martlet bundles for F5. The first is the voice F5 starts with, and it is female.</summary>
public static class F5BundledVoices
{
    private const string Arctic = "CMU ARCTIC speech database, Carnegie Mellon University";
    private const string ArcticLicence = "CMU ARCTIC licence: free for any use, notice kept";
    private const string ArcticTranscript =
        "I came for information more out of curiosity than anything else. The ship should be in within a week or ten days.";

    /// <summary>SHA-256 of the F5-TTS example clip ("F5 sample voice (English)") that earlier versions bundled and stored
    /// in the voice list. It is a male voice and is no longer shipped because where its recording comes from could not be
    /// confirmed; the desktop moves anything still speaking with it to <see cref="Default"/>.</summary>
    public const string RetiredSampleSha256 = "6a7c5fb9068fa23762af51544c8de76b2dbf6f54a65a44e3b2f9e2ad11899aa2";

    public static IReadOnlyList<F5BundledVoice> All { get; } =
    [
        new("lj-speech", "LJ (female narrator)", true, "Female narrator from the LJ Speech dataset, a LibriVox reading. Public domain.",
            "Public domain", "Printing, then, for our purpose, may be considered as the art of making books by means of movable types.",
            "d54f23016ad2cd288c276960b3366fe09e3bd3983a2089ede0d09e57ff3fedf4"),
        new("librivox-cori-samuel", "Cori (female narrator)", true, "Cori Samuel reading Frankenstein for LibriVox. Public domain.",
            "Public domain",
            "I have thus endeavoured to preserve the truth of the elementary principles of human nature, while I have not scrupled to innovate upon their combinations.",
            "932a39325bec674d2562a2b0ff2ce5ad6988687643b9b134e445a698525a1ca5"),
        new("librivox-helen-taylor", "Helen (female narrator)", true, "Helen Taylor reading Love at Second Sight for LibriVox. Public domain.",
            "Public domain", "She was a slim, fair, pretty woman, with more vividness and character than usually goes with her type.",
            "360ded8f517990dd1d98675d512c9990a6cad8b371a15e91ee20ea29cd806676"),
        new("arctic-slt", "SLT (US female)", true, $"US English female voice talent, {Arctic}.", ArcticLicence, ArcticTranscript,
            "59f79c9146230358f6a6f5183d3858a8881f16340372dbc0cfb15586cb7818ca"),
        new("arctic-clb", "CLB (US female)", true, $"US English female, {Arctic}.", ArcticLicence, ArcticTranscript,
            "651d51a985851459ac01d1213c5ba001cca7b03140fd3ecb8c1e2fd5060af033"),
        new("arctic-bdl", "BDL (US male)", false, $"US English male voice talent, {Arctic}.", ArcticLicence, ArcticTranscript,
            "bbde73d09e93d123600c52851d173bad474d75712737690adbf4dd68552c0002"),
        new("arctic-rms", "RMS (US male)", false, $"US English male, {Arctic}.", ArcticLicence, ArcticTranscript,
            "8a60d4e8adb5045f5c452a4fd9fdb140689e146eb584f4270748c3a03c99fae1"),
        new("arctic-awb", "AWB (Scottish male)", false, $"Scottish English male, {Arctic}.", ArcticLicence, ArcticTranscript,
            "9f900857b028a33151939f3c6d93f49b0e4db7c9fdf860dfe3287c2aa728f9e3"),
        new("arctic-jmk", "JMK (Canadian male)", false, $"Canadian English male, {Arctic}.", ArcticLicence, ArcticTranscript,
            "facd727e0a14808624a70d7e794951efbc4e260be331b99e51728156f3121998"),
        new("arctic-ksp", "KSP (Indian English male)", false, $"Indian English male, {Arctic}.", ArcticLicence, ArcticTranscript,
            "5453f3e6a8b335dac1eff29d91f2981d7064cbb97a6f236e790f574caa584695")
    ];

    /// <summary>The voice F5 speaks with until the owner chooses another: the first female voice (LJ), so F5's default
    /// sounds feminine.</summary>
    public static F5BundledVoice Default { get; } = All.First(voice => voice.Female);

    public static F5BundledVoice? Find(string key) => All.FirstOrDefault(voice => voice.Key == key);

    /// <summary>The bundled voice whose clip has <paramref name="audioSha256"/>, for recognizing it in the voice list.</summary>
    public static F5BundledVoice? ForAudio(string audioSha256) =>
        All.FirstOrDefault(voice => string.Equals(voice.AudioSha256, audioSha256, StringComparison.OrdinalIgnoreCase));

    public static bool IsRetiredSample(string audioSha256) =>
        string.Equals(audioSha256, RetiredSampleSha256, StringComparison.OrdinalIgnoreCase);
}
