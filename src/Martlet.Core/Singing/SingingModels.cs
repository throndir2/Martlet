namespace Martlet.Core.Singing;

/// <summary>A model the owner adds to the singing host role on one computer (<see cref="SingingModels.AddOns"/>): a music model
/// (<see cref="Music"/>) or a voice match (<see cref="Match"/>). <see cref="Id"/> is its name on the wire (a job's
/// <c>model</c> or <c>voice_match</c>, the service status's <c>music_models</c> or <c>voice_matches</c>); <see cref="Choice"/>
/// is the role choice that sets it up (<see cref="On"/>) or leaves it out (<see cref="Off"/>).</summary>
public sealed record SingingAddOn(string Id, string Name, string Button, SongMusicModel? Music, SongVoiceMatch? Match, string Choice,
    string On, string Off, string Download, string Licence, bool NonCommercial, string Label, string About)
{
    /// <summary>What adding it downloads and agrees to, for its confirmation (role.conf's terms for it, in short).</summary>
    public string Terms { get; init; } = "";
}

/// <summary>Every model the singing host role can run, by its name on the wire, and the ones the owner adds on a computer.
/// ACE-Step 1.5 and SoulX-Singer are always set up where Singing is; each <see cref="AddOns"/> entry only where the owner adds
/// it (Companion › Singing, *Add ... here*), after a confirmation naming its download and licence.</summary>
public static class SingingModels
{
    public static IReadOnlyList<SingingAddOn> AddOns { get; } =
    [
        new("ace-step-v15-xl", "ACE-Step 1.5 XL", "Xl", SongMusicModel.AceStep15Xl, null, "SINGING_MUSIC_MODELS", "standard-xl", "standard",
            "about 20 GB", "MIT", false, "ACE-Step 1.5 XL (the highest quality; about 20 GB more, a 12 GB+ card)",
            "ACE-Step's 4B music model (its SFT model, 50 steps): clearer words and richer music than ACE-Step 1.5.")
        {
            Terms = "ACE-Step 1.5 XL downloads about 20 GB more: its SFT model (huggingface.co/ACE-Step/acestep-v15-xl-sft, MIT). " +
                "It is ACE-Step's largest music model: the clearest singing and music, but slower and it needs a 12 GB+ NVIDIA " +
                "graphics card (16 GB+ is better beside the voice)."
        },
        new("heartmula", "HeartMuLa", "HeartMuLa", SongMusicModel.HeartMuLa, null, "SINGING_HEARTMULA", "yes", "no",
            "about 22 GB", "Apache-2.0", false, "HeartMuLa (a 3B song model; about 22 GB more, a 16 GB+ card)",
            "HeartMuLa's 3B song model with its HeartCodec: full songs from lyrics and style tags.")
        {
            Terms = "HeartMuLa adds HeartMuLa's heartlib (github.com/HeartMuLa/heartlib, Apache-2.0) to the singing image and " +
                "downloads about 22 GB: HeartMuLa-oss-3B, HeartCodec and their tokenizer (huggingface.co/HeartMuLa, Apache-2.0). " +
                "It needs a 16 GB+ NVIDIA graphics card. Martlet has not measured it yet."
        },
        new("diffrhythm", "DiffRhythm", "DiffRhythm", SongMusicModel.DiffRhythm, null, "SINGING_DIFFRHYTHM", "yes", "no",
            "about 9 GB", "Apache-2.0; its VAE Stability AI Community; MuQ-MuLan CC-BY-NC-4.0", true,
            "DiffRhythm (fast; about 9 GB more, an 8 GB+ card; non-commercial use only)",
            "DiffRhythm 1.2: a diffusion song model that writes a whole song in seconds, up to 4 minutes 45 seconds.")
        {
            Terms = "DiffRhythm adds DiffRhythm (github.com/ASLP-lab/DiffRhythm, Apache-2.0) and espeak-ng to the singing image and " +
                "downloads about 9 GB: DiffRhythm 1.2 full (Apache-2.0), its VAE (Stability AI Community License), MuQ-MuLan and " +
                "MuQ (CC-BY-NC-4.0: non-commercial use only) and XLM-RoBERTa base (MIT). It needs an 8 GB+ NVIDIA graphics card. " +
                "Martlet has not measured it yet."
        },
        new("yue", "YuE", "YuE", SongMusicModel.YuE, null, "SINGING_YUE", "yes", "no",
            "about 8 GB", "CC-BY-NC-4.0", true, "YuE (strong singing, slow; non-commercial use only; about 8 GB more, a 16 GB+ card)",
            "YuE2: a lyrics-to-song model with strong singing. It is slow, and the lyrics set the song's length.")
        {
            Terms = "YuE adds YuE2's inference package to the singing image and downloads about 8 GB: YuE2-3B and its VAE " +
                "(huggingface.co/m-a-p, CC-BY-NC-4.0: non-commercial use only; the licensors let individual creators use what " +
                "they make). It is slow and needs a 16 GB+ NVIDIA graphics card (24 GB for full precision). Martlet has not " +
                "measured it yet."
        },
        new("vevosing", "VevoSing", "Vevo", null, SongVoiceMatch.VevoSing, "SINGING_VOICE_MATCHES", "soulx-vevosing", "soulx",
            "about 4.5 GB", "CC-BY-NC-ND-4.0", true, "VevoSing (closer to the voice; personal, non-commercial use only)",
            "Amphion's Vevo1.5: a little closer to the voice and cleaner, but may drift off-key.")
        {
            Terms = "VevoSing downloads about 4.5 GB more: Amphion's Vevo1.5 models (huggingface.co/amphion/Vevo1.5), licensed " +
                "CC-BY-NC-ND-4.0: personal, non-commercial use only, and nothing made from them may be shared as a derivative; " +
                "and OpenAI Whisper medium (MIT). Choose it only for your own non-commercial use."
        },
        new("vevo2", "Vevo2", "Vevo2", null, SongVoiceMatch.Vevo2, "SINGING_VEVO2", "yes", "no",
            "about 3 GB", "CC-BY-NC-ND-4.0", true, "Vevo2 (Amphion's newest; personal, non-commercial use only)",
            "Amphion's Vevo2: speech and singing voice conversion from a short recording, keeping the melody.")
        {
            Terms = "Vevo2 downloads about 3 GB more: Amphion's Vevo2 models (huggingface.co/RMSnow/Vevo2), licensed " +
                "CC-BY-NC-ND-4.0: personal, non-commercial use only, and nothing made from them may be shared as a derivative; " +
                "and OpenAI Whisper medium (MIT). Martlet has not measured it yet."
        },
        new("seedvc", "Seed-VC", "SeedVc", null, SongVoiceMatch.SeedVc, "SINGING_SEEDVC", "yes", "no",
            "about 2.5 GB", "GPL-3.0", false, "Seed-VC (singing voice conversion with pitch; GPL-3.0)",
            "Seed-VC's singing model (44.1 kHz, with pitch): zero-shot singing voice conversion from a short recording.")
        {
            Terms = "Seed-VC adds Seed-VC (github.com/Plachtaa/seed-vc, GPL-3.0, no longer maintained) to the singing image and " +
                "downloads about 2.5 GB: its singing model (GPL-3.0), RMVPE (MIT), CAMPPlus (Apache-2.0), NVIDIA BigVGAN v2 " +
                "(MIT) and OpenAI Whisper small (Apache-2.0). Martlet has not measured it yet."
        },
    ];

    /// <summary>The add-on that sets <paramref name="model"/> up, or null for ACE-Step 1.5 (always set up).</summary>
    public static SingingAddOn? AddOn(SongMusicModel model) => AddOns.FirstOrDefault(a => a.Music == model);

    /// <summary>The add-on that sets <paramref name="match"/> up, or null for SoulX-Singer (always set up) and the cloud ones.</summary>
    public static SingingAddOn? AddOn(SongVoiceMatch match) => AddOns.FirstOrDefault(a => a.Match == match);

    /// <summary>The host's music models, in the order the app offers them.</summary>
    public static IReadOnlyList<SongMusicModel> HostMusic { get; } =
        [SongMusicModel.AceStep15, SongMusicModel.AceStep15Xl, SongMusicModel.HeartMuLa, SongMusicModel.DiffRhythm, SongMusicModel.YuE];

    /// <summary>The host's voice matches, in the order the app offers them.</summary>
    public static IReadOnlyList<SongVoiceMatch> HostMatches { get; } =
        [SongVoiceMatch.SoulX, SongVoiceMatch.VevoSing, SongVoiceMatch.Vevo2, SongVoiceMatch.SeedVc];

    /// <summary>The music model's name on the wire.</summary>
    public static string Id(SongMusicModel model) => AddOn(model)?.Id ?? "ace-step-v15";

    /// <summary>The voice match's name on the wire.</summary>
    public static string Id(SongVoiceMatch match) => match switch
    {
        SongVoiceMatch.ElevenLabs => "elevenlabs",
        SongVoiceMatch.Mureka => "mureka",
        _ => AddOn(match)?.Id ?? "soulx"
    };

    /// <summary>The host music model named <paramref name="id"/>, or null for a name this Martlet doesn't know.</summary>
    public static SongMusicModel? Music(string? id) => id == "ace-step-v15" ? SongMusicModel.AceStep15
        : AddOns.FirstOrDefault(a => a.Music is not null && a.Id == id)?.Music;

    /// <summary>The host voice match named <paramref name="id"/>, or null for a name this Martlet doesn't know.</summary>
    public static SongVoiceMatch? Match(string? id) => id == "soulx" ? SongVoiceMatch.SoulX
        : AddOns.FirstOrDefault(a => a.Match is not null && a.Id == id)?.Match;

    /// <summary>The music model's name in the app.</summary>
    public static string Name(SongMusicModel model) => AddOn(model)?.Name ?? "ACE-Step 1.5";

    /// <summary>The voice match's name in the app.</summary>
    public static string Name(SongVoiceMatch match) => match switch
    {
        SongVoiceMatch.ElevenLabs => "ElevenLabs Voice Changer",
        SongVoiceMatch.Mureka => "Mureka",
        _ => AddOn(match)?.Name ?? "SoulX-Singer"
    };

    /// <summary>The role choices that keep what a computer has (<paramref name="has"/>) and add <paramref name="adding"/>.</summary>
    public static IReadOnlyDictionary<string, string> Choices(Func<SingingAddOn, bool> has, SingingAddOn? adding)
    {
        ArgumentNullException.ThrowIfNull(has);
        var choices = new Dictionary<string, string>(StringComparer.Ordinal) { ["SINGING_MODEL"] = "ace-step-v15-soulx-svc" };
        foreach (var addOn in AddOns) choices[addOn.Choice] = addOn == adding || has(addOn) ? addOn.On : addOn.Off;
        return choices;
    }
}
