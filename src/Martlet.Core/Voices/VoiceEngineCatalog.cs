using Martlet.Core.Contracts;

namespace Martlet.Core.Voices;

public enum VoiceEngine { F5Tts, Qwen3Tts, Chatterbox, GptSoVits, XttsV2 }
public enum VoiceAssetPurpose { Reference, TrainingMaterial }
public enum VoiceRightsBasis { OwnVoice, ExplicitPermission }

public sealed record VoiceEngineInfo(
    VoiceEngine Id, string Name, string Candidate, string License,
    string ReferenceGuidance, string TrainingGuidance, string SetupGuidance,
    bool UpstreamTrainingDocumented)
{
    public string ExecutionStatus => "Not integrated: installation, model loading, synthesis and training are unavailable in this build.";
}

public static class VoiceEngineCatalog
{
    public static IReadOnlyList<VoiceEngineInfo> All { get; } = Array.AsReadOnly(new[]
    {
        new VoiceEngineInfo(VoiceEngine.F5Tts, "F5-TTS", "F5TTS_v1_Base",
            "MIT code; official weights CC-BY-NC-4.0 (noncommercial).",
            "Use a clean short reference with its exact transcript. Upstream may trim at 12 seconds; review a matching excerpt before synthesis.",
            "Official CSV dataset preparation and Accelerate / fine-tuning tools. Importing a file does not train a model.",
            "Isolated Python worker; explicit checkpoint, vocabulary and vocoder. No automatic ASR or downloads.", true),
        new VoiceEngineInfo(VoiceEngine.Qwen3Tts, "Qwen3-TTS", "12Hz-0.6B-Base; 1.7B-Base later",
            "Inspected 0.6B Base weights: Apache-2.0. Review each exact artifact.",
            "Base models support reference cloning, advertised from 3 seconds. Supply the matching transcript for the intended in-context path.",
            "Official single-speaker fine-tuning: reviewed audio/text/reference JSONL, tokenizer preparation, then SFT.",
            "Separate Python environment and tokenizer artifacts; do not select CustomVoice or VoiceDesign for clip-based cloning.", true),
        new VoiceEngineInfo(VoiceEngine.Chatterbox, "Chatterbox", "Chatterbox-Turbo (English)",
            "Inspected Turbo weights: MIT. Preserve upstream watermarking.",
            "Turbo requires more than 5 seconds in the inspected implementation; a clean 10-second clip is a useful target.",
            "Managed fine-tuning is unavailable: no supported official recipe was verified. Reference cloning needs no training.",
            "Isolate PyTorch/transformers dependencies from other engines; use explicit local checkpoints and a supplied reference.", false),
        new VoiceEngineInfo(VoiceEngine.GptSoVits, "GPT-SoVITS", "Exact compatible GPT/SoVITS release pair pending",
            "MIT code; model and auxiliary-weight license closure still requires review.",
            "The inspected reference path accepts 3-10 seconds. Supply the matching transcript and review the language.",
            "Upstream provides dataset preparation and GPT/SoVITS fine-tuning. Its one-minute claim is not a quality guarantee.",
            "Requires matching GPT and SoVITS checkpoints plus language/auxiliary models. Worker paths are not client paths.", true),
        new VoiceEngineInfo(VoiceEngine.XttsV2, "XTTS-v2", "XTTS-v2 with a pinned Coqui-compatible runtime",
            "CPML 1.0.0: model and outputs restricted to noncommercial use; runtime code has separate terms.",
            "Supports single or multiple references; the model card advertises 6 seconds. Exact runtime capability must be qualified.",
            "Documented fine-tuning targets the GPT encoder, not every component. Audio and reviewed transcripts are required.",
            "Evaluate maintained coqui-tts in its own environment; pin config, tokenizer, weights and PyTorch separately.", true)
    });

    public static VoiceEngineInfo Get(VoiceEngine engine)
    {
        ContractRules.Defined(engine);
        return All.Single(item => item.Id == engine);
    }

    public static string DescribePreparation(VoiceEngine engine, VoiceAssetPurpose purpose, PcmWaveInfo wave)
    {
        var info = Get(engine);
        ContractRules.Defined(purpose);
        if (purpose == VoiceAssetPurpose.TrainingMaterial)
            return info.TrainingGuidance + " Dataset segmentation, review and execution are not implemented.";
        var seconds = wave.SampleCount / (double)wave.SampleRate;
        var mismatch = engine switch
        {
            VoiceEngine.F5Tts when seconds > 12 => "Choose an excerpt no longer than 12 seconds and update its transcript.",
            VoiceEngine.Qwen3Tts when seconds < 3 => "Choose at least 3 seconds for the planned reference path.",
            VoiceEngine.Chatterbox when seconds <= 5 => "Choose a reference strictly longer than 5 seconds.",
            VoiceEngine.GptSoVits when seconds is < 3 or > 10 => "Choose an excerpt between 3 and 10 seconds.",
            VoiceEngine.XttsV2 when seconds < 6 => "A reference of at least 6 seconds is recommended by the model card.",
            _ => "Duration passes the initial preparation guidance; this is not voice quality or runtime readiness."
        };
        return mismatch + " " + info.ReferenceGuidance;
    }
}
