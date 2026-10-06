namespace Martlet.Core.Planning;

// Footprints of every local option, one per model or engine: what docs/RESOURCE_FOOTPRINTS.md measured, sourced or
// estimated (each option's Evidence and Source say which, and the doc says where and how). GB are 10^9 bytes as tools
// report them; CPU is hardware threads kept busy. Thinking's VRAM is measured or estimated at Martlet's 8,192-token
// context, so its ContextGb is 0; Deep thinking's ContextGb is the extra KV cache and buffers of one 32,768-token think.
// Keep option ids stable: settings and callers save them.
public sealed partial class FootprintCatalog
{
    private const string Doc = "docs/RESOURCE_FOOTPRINTS.md";
    // Properties, not fields: Default (in the other part of the class) builds the seed before field initializers here run.
    private static string[] DockerNvidia => ["windows", "linux"];
    private static string[] WindowsOnly => ["windows"];

    /// <summary>A Thinking model in Ollama on a graphics card (8,192-token context, the KV cache included in VRAM).</summary>
    private static ComponentOption Ollama(string id, string name, ResourceUse steady, ResourceUse peak, int tier, int firstWordMs,
        bool hears, FootprintEvidence evidence, string source) => new()
    {
        Id = id, Component = PlanComponent.Thinking, DisplayName = name, ModelId = id, HostRoleKind = "ollama",
        Gpu = GpuRequirement.AnyGpu, Steady = steady, Peak = peak, QualityTier = tier, FirstWordMs = firstWordMs, HearsAudio = hears,
        SeesImages = true, Evidence = evidence, Source = source
    };

    /// <summary>A Deep thinking model in its own Ollama server: the same model as Thinking plus one think's 32,768-token
    /// context (<see cref="ComponentOption.ContextGb"/>, from the model's config.json; see the doc).</summary>
    private static ComponentOption Deep(string model, string name, double vramGb, double ramGb, double diskGb, double contextGb, int tier,
        FootprintEvidence evidence, string source) => new()
    {
        Id = "deep-thinking:" + model, Component = PlanComponent.DeepThinking, DisplayName = name, ModelId = model,
        HostRoleKind = "deep-thinking", Gpu = GpuRequirement.AnyGpu, Steady = new(vramGb, ramGb, 1, diskGb),
        Peak = new(vramGb, ramGb + 0.5, 2, diskGb), ContextGb = contextGb, QualityTier = tier, Evidence = evidence, Source = source
    };

    private static IEnumerable<ComponentOption> SeedOptions() =>
    [
        // Thinking, local (Ollama on a graphics card). VRAM: the model's runner at 8,192 tokens. Disk: ollama.com's tag size
        // with its speculative-decoding draft. RAM: what Ollama keeps off the card (Gemma 4 E-models' per-layer embeddings).
        Ollama("gemma4:e2b", "Gemma 4 E2B", new(3.3, 1.5, 1, 7.5), new(3.3, 2, 2, 7.5), 1, 154, hears: true, FootprintEvidence.Measured,
            $"VRAM measured: RTX 4070, Ollama 0.35.1, 8,192 tokens (docs/VOICE_LATENCY.md); disk ollama.com; RAM estimate ({Doc})"),
        Ollama("qwen3.5:4b", "Qwen3.5 4B", new(4.1, 0.5, 1, 4.0), new(4.1, 1, 2, 4.0), 2, 328, hears: false, FootprintEvidence.Measured,
            $"VRAM measured: RTX 4070, Ollama 0.35.1, 8,192 tokens (docs/VOICE_LATENCY.md); disk ollama.com; RAM estimate ({Doc})"),
        Ollama("gemma4:e4b", "Gemma 4 E4B", new(4.9, 2.5, 1, 9.5), new(4.9, 3, 2, 9.5), 2, 208, hears: true, FootprintEvidence.Measured,
            $"VRAM measured: RTX 4070, Ollama 0.35.1, 8,192 tokens (docs/VOICE_LATENCY.md); disk ollama.com; RAM estimate ({Doc})"),
        Ollama("gemma4:12b", "Gemma 4 12B", new(9.0, 1, 1, 8.0), new(9.0, 1.5, 2, 8.0), 3, 400, hears: true, FootprintEvidence.Estimate,
            $"Estimate: ollama.com download plus KV cache from config.json at 8,192 tokens and buffers ({Doc})"),
        Ollama("gemma4:26b", "Gemma 4 26B", new(18.5, 1.5, 1, 19), new(18.5, 2, 2, 19), 4, 600, hears: false, FootprintEvidence.Estimate,
            $"Estimate: ollama.com download plus KV cache from config.json at 8,192 tokens and buffers ({Doc})"),
        new()
        {
            Id = "gemma4:e2b-cpu", Component = PlanComponent.Thinking, DisplayName = "Gemma 4 E2B on the processor", ModelId = "gemma4:e2b",
            HostRoleKind = "ollama", Steady = new(0, 6, 6, 7.5), Peak = new(0, 7.5, 8, 7.5), QualityTier = 1, FirstWordMs = 2500,
            HearsAudio = true, SeesImages = true,
            Source = $"Estimate: whole model in system memory, Ollama's threads on the physical cores; disk ollama.com ({Doc})"
        },
        // Thinking, hosted. The hosted-endpoint session (docs/HOSTED_THINKING.md) owns hearing and reliability facts.
        new()
        {
            Id = "hosted:nvidia-build", Component = PlanComponent.Thinking, DisplayName = "NVIDIA Build (free endpoint)",
            Hosting = OptionHosting.External, ProviderId = "nvidia-build", ModelId = Settings.ChatCompletionsEndpointCatalog.NvidiaBuildDefaultModelId,
            QualityTier = 4, FirstWordMs = 500, SeesImages = true, FreeTier = true, NeedsSignup = true,
            Reliability = OptionReliability.Medium, Evidence = FootprintEvidence.Measured,
            Source = "docs/HOSTED_THINKING.md and ChatCompletionsEndpointCatalog (checked 2026-10-01: about 0.5 s per reply; up to 40 " +
                "requests a minute; models retire often; does not hear)"
        },
        new()
        {
            Id = "hosted:gemini", Component = PlanComponent.Thinking, DisplayName = "Google Gemini (free tier)", Hosting = OptionHosting.External,
            ProviderId = "google-ai-studio", ModelId = "gemini-3.5-flash-lite", QualityTier = 3, HearsAudio = true, SeesImages = true,
            FreeTier = true, NeedsSignup = true, Reliability = OptionReliability.High, Evidence = FootprintEvidence.Sourced,
            Source = "docs/HOSTED_THINKING.md (OpenAI-compatible at generativelanguage.googleapis.com/v1beta/openai; 99.5-100% uptime)"
        },
        new()
        {
            Id = "hosted:nvidia-build-omni", Component = PlanComponent.Thinking, DisplayName = "NVIDIA Build Nemotron omni (free endpoint)",
            Hosting = OptionHosting.External, ProviderId = "nvidia-build", ModelId = "nvidia/nemotron-3-nano-omni-30b-a3b-reasoning",
            QualityTier = 3, HearsAudio = true, SeesImages = true, FreeTier = true, NeedsSignup = true, Reliability = OptionReliability.Low,
            Evidence = FootprintEvidence.Sourced, Source = "docs/HOSTED_THINKING.md (72.6% 1-day uptime on OpenRouter; latency not measured)"
        },
        new()
        {
            Id = "hosted:openrouter", Component = PlanComponent.Thinking, DisplayName = "OpenRouter", Hosting = OptionHosting.External,
            ProviderId = "openrouter", ModelId = "google/gemma-4-26b-a4b-it", QualityTier = 4, FirstWordMs = 800, SeesImages = true,
            NeedsSignup = true, Reliability = OptionReliability.Medium, Evidence = FootprintEvidence.Sourced,
            Source = "docs/HOSTED_THINKING.md (:free variants allow 50 requests a day until credits are bought)"
        },
        new()
        {
            Id = "hosted:openai", Component = PlanComponent.Thinking, DisplayName = "OpenAI", Hosting = OptionHosting.External,
            ProviderId = "openai", QualityTier = 5, FirstWordMs = 700, SeesImages = true, NeedsSignup = true, Source = "Hosted: uses no local resources"
        },

        // Voice. Host roles in Docker on an NVIDIA card (SpeechEngines); disk is the weights plus the role's image.
        new()
        {
            Id = "chatterbox-turbo", Component = PlanComponent.Voice, DisplayName = "Chatterbox Turbo", ModelId = "chatterbox-turbo",
            HostRoleKind = "chatterbox", Gpu = GpuRequirement.Nvidia, MinGpuGb = 6, Platforms = DockerNvidia,
            Steady = new(3.7, 2.5, 1, 11), Peak = new(4.2, 3, 1.5, 11), QualityTier = 4, FirstWordMs = 450, Evidence = FootprintEvidence.Measured,
            Source = $"VRAM measured: RTX 4070, 3.7 GB loaded, 4.2 GB peak (docs/VOICE_LATENCY.md); weights 3.0 GB pinned; RAM, CPU, image estimate ({Doc})"
        },
        new()
        {
            Id = "f5-tts", Component = PlanComponent.Voice, DisplayName = "F5-TTS", ModelId = "f5tts-v1-base", HostRoleKind = "f5",
            Gpu = GpuRequirement.Nvidia, MinGpuGb = 6, Platforms = DockerNvidia, Steady = new(0.9, 2.5, 1, 9.5), Peak = new(2, 3, 1.5, 9.5),
            QualityTier = 3, FirstWordMs = 1400, Source = $"VRAM about 0.9 GB measured beside other roles (docs/VOICE_LATENCY.md); peak, RAM, image estimate ({Doc})"
        },
        new()
        {
            Id = "xtts-v2", Component = PlanComponent.Voice, DisplayName = "XTTS-v2", ModelId = "xtts-v2", HostRoleKind = "xtts",
            Gpu = GpuRequirement.Nvidia, MinGpuGb = 4, Platforms = DockerNvidia, Steady = new(2.2, 2.5, 1, 10), Peak = new(3, 3, 1.5, 10),
            QualityTier = 3, FirstWordMs = 300, Evidence = FootprintEvidence.Sourced,
            Source = $"VRAM sourced: about 2-2.2 GB in fp16 (nexgpu.net/en/models/xtts); peak, RAM, image estimate ({Doc})"
        },
        new()
        {
            Id = "gpt-sovits", Component = PlanComponent.Voice, DisplayName = "GPT-SoVITS", ModelId = "gpt-sovits-v2pro", HostRoleKind = "gpt-sovits",
            Gpu = GpuRequirement.Nvidia, MinGpuGb = 4, Platforms = DockerNvidia, Steady = new(3, 2.5, 1, 8.1), Peak = new(4, 3, 2, 8.1),
            QualityTier = 2, FirstWordMs = 1300, Source = $"Image 6.7 GB measured (docs/GPT_SOVITS_VOICE.md); VRAM estimate from the 4 GB minimum card ({Doc})"
        },
        new()
        {
            Id = "dia", Component = PlanComponent.Voice, DisplayName = "Dia", ModelId = "dia-1.6b-0626", HostRoleKind = "dia",
            Gpu = GpuRequirement.Nvidia, MinGpuGb = 8, Platforms = DockerNvidia, Steady = new(4.4, 3, 1, 15), Peak = new(9.8, 4, 1, 15),
            QualityTier = 2, FirstWordMs = 17000, Evidence = FootprintEvidence.Measured,
            Source = $"Peak VRAM measured: 9.8 GB alone on an RTX 4070 (docs/DIA_VOICE.md); steady 4.4 GB from Dia's README; disk estimate ({Doc})"
        },
        new()
        {
            Id = "windows-speech", Component = PlanComponent.Voice, DisplayName = "Windows voices", RunsInApp = true, Platforms = WindowsOnly,
            Steady = new(0, 0.1, 0.9, 0), Peak = new(0, 0.2, 1, 0), QualityTier = 1, FirstWordMs = 50, Evidence = FootprintEvidence.Measured,
            Source = $"Measured: System.Speech on an i7-13700K, 0.17 GB with its PowerShell host, 0.9 threads while speaking ({Doc})"
        },
        new()
        {
            Id = "macos-speech", Component = PlanComponent.Voice, DisplayName = "macOS system voices", RunsInApp = true, Platforms = ["macos"],
            Steady = new(0, 0.1, 0.9, 0), Peak = new(0, 0.2, 1, 0), QualityTier = 1, FirstWordMs = 50,
            Source = $"Estimate: like Windows voices (AVSpeechSynthesizer) ({Doc})"
        },
        new()
        {
            Id = "hosted:openai-tts", Component = PlanComponent.Voice, DisplayName = "OpenAI voice", Hosting = OptionHosting.External,
            ProviderId = "openai", QualityTier = 4, FirstWordMs = 600, NeedsSignup = true, Source = "Hosted: uses no local resources"
        },

        // Listening. Parakeet runs in the app on the processor (sherpa-onnx, clamp(logical processors / 4, 2, 4) threads:
        // steady is 2 threads' busy count, peak 4 threads'); Whisper is the stt host role (whisper.cpp).
        new()
        {
            Id = "parakeet-tdt-0.6b-v3-cpu", Component = PlanComponent.Listening, DisplayName = "Parakeet on the processor",
            ModelId = "parakeet-tdt-0.6b-v3", RunsInApp = true, Steady = new(0, 0.9, 2.6, 0.67), Peak = new(0, 0.9, 8.4, 0.67),
            QualityTier = 4, FirstWordMs = 250, Evidence = FootprintEvidence.Measured,
            Source = $"Measured: sherpa-onnx 1.13.8 on an i7-13700K; disk is the pinned download (ParakeetModels) ({Doc})"
        },
        new()
        {
            Id = "parakeet-tdt-110m-en-cpu", Component = PlanComponent.Listening, DisplayName = "Parakeet 110M (English) on the processor",
            ModelId = "parakeet-tdt-110m-en", RunsInApp = true, Steady = new(0, 0.7, 4, 0.48), Peak = new(0, 0.7, 9.9, 0.48),
            QualityTier = 3, FirstWordMs = 90, Evidence = FootprintEvidence.Measured,
            Source = $"Measured: sherpa-onnx 1.13.8 on an i7-13700K; disk is the pinned download (ParakeetModels) ({Doc})"
        },
        new()
        {
            Id = "parakeet-tdt-0.6b-v2-cpu", Component = PlanComponent.Listening, DisplayName = "Parakeet 0.6B v2 (English) on the processor",
            ModelId = "parakeet-tdt-0.6b-v2-int8", RunsInApp = true, Steady = new(0, 0.9, 2.6, 0.66), Peak = new(0, 0.9, 8.6, 0.66),
            QualityTier = 3, FirstWordMs = 225, Evidence = FootprintEvidence.Measured,
            Source = $"Measured: sherpa-onnx 1.13.8 on an i7-13700K; disk is the pinned download (ParakeetModels) ({Doc})"
        },
        new()
        {
            Id = "whisper-large-v3-turbo-cuda", Component = PlanComponent.Listening, DisplayName = "Whisper large-v3 turbo on the graphics card",
            ModelId = "large-v3-turbo", HostRoleKind = "stt", Gpu = GpuRequirement.Nvidia, MinGpuGb = 4, Platforms = DockerNvidia,
            Steady = new(2, 0.5, 1, 4), Peak = new(2.5, 1, 1, 4), QualityTier = 4, FirstWordMs = 210, Evidence = FootprintEvidence.Measured,
            Source = $"VRAM measured: removing the role frees about 2 GB on an RTX 4070 (docs/VOICE_LATENCY.md); model 1.6 GB; image, RAM estimate ({Doc})"
        },
        new()
        {
            Id = "whisper-small-cuda", Component = PlanComponent.Listening, DisplayName = "Whisper small on the graphics card",
            ModelId = "small", HostRoleKind = "stt", Gpu = GpuRequirement.Nvidia, MinGpuGb = 2, Platforms = DockerNvidia,
            Steady = new(0.9, 0.5, 1, 2.5), Peak = new(1.2, 1, 1, 2.5), QualityTier = 3, FirstWordMs = 150, Evidence = FootprintEvidence.Sourced,
            Source = $"Sourced: whisper.cpp README (small 466 MiB, about 852 MB memory); faster-whisper small.en 0.8 GB measured; image estimate ({Doc})"
        },
        new()
        {
            Id = "whisper-small-cpu", Component = PlanComponent.Listening, DisplayName = "Whisper small on the processor",
            ModelId = "small", HostRoleKind = "stt", Platforms = DockerNvidia, Steady = new(0, 0.9, 4, 1.5), Peak = new(0, 1, 4, 1.5),
            QualityTier = 2, FirstWordMs = 1500, Evidence = FootprintEvidence.Sourced,
            Source = $"Sourced: whisper.cpp README (about 852 MB memory, 4 threads); speed and image estimate ({Doc})"
        },
        new()
        {
            Id = "hosted:openai-transcribe", Component = PlanComponent.Listening, DisplayName = "OpenAI transcription",
            Hosting = OptionHosting.External, ProviderId = "openai", QualityTier = 5, FirstWordMs = 600, NeedsSignup = true,
            Source = "Hosted: uses no local resources"
        },

        // Character and lip-sync.
        new()
        {
            Id = "character-live2d-vrm", Component = PlanComponent.Character, DisplayName = "Live2D or VRM character", RunsInApp = true,
            Steady = new(0, 0.4, 0.5, 0.1), Peak = new(0, 0.8, 1, 0.1), QualityTier = 3,
            Source = $"Estimate: WebView2 renderer on any graphics card, within the {PlacementEngine.GpuReserveGb} GB kept for the desktop ({Doc})"
        },
        new()
        {
            Id = "loudness-lipsync", Component = PlanComponent.LipSync, DisplayName = "Loudness lip-sync", RunsInApp = true,
            Steady = new(0, 0, 0.05, 0), Peak = new(0, 0, 0.1, 0), QualityTier = 1,
            Source = $"Estimate: an RMS of the audio Martlet already plays ({Doc})"
        },
        new()
        {
            Id = "audio2face-3d", Component = PlanComponent.LipSync, DisplayName = "Advanced lip-sync (Audio2Face-3D)", HostRoleKind = "audio2face",
            Gpu = GpuRequirement.Nvidia, MinGpuGb = 4, Platforms = DockerNvidia, Steady = new(1.2, 1.5, 1, 8.3), Peak = new(1.5, 2, 1, 8.3),
            QualityTier = 4,
            Source = $"Estimate: the RTX 4070's 7 GB of resident roles less Chatterbox and Whisper (docs/SINGING.md); 4 GB minimum card from NVIDIA; models.lock 0.3 GB ({Doc})"
        },

        // Deep thinking: the model again in its own server plus one think's 32,768-token context.
        Deep("gemma4:e4b", "Gemma 4 E4B (deep thinking)", 4.9, 2.5, 9.5, 0.7, 2, FootprintEvidence.Measured,
            $"Model VRAM measured as Thinking (docs/VOICE_LATENCY.md); context from config.json ({Doc})"),
        Deep("gemma4:12b", "Gemma 4 12B (deep thinking)", 9.0, 1, 8.0, 0.7, 3, FootprintEvidence.Estimate,
            $"Estimate: as Thinking plus the context from config.json ({Doc})"),
        Deep("gemma4:26b", "Gemma 4 26B (deep thinking)", 18.5, 1.5, 19, 0.9, 4, FootprintEvidence.Estimate,
            $"Estimate: as Thinking plus the context from config.json ({Doc})"),
        new()
        {
            Id = "hosted:nvidia-build-deep", Component = PlanComponent.DeepThinking, DisplayName = "NVIDIA Build (free endpoint)",
            Hosting = OptionHosting.External, ProviderId = "nvidia-build", QualityTier = 4, FreeTier = true, NeedsSignup = true,
            Reliability = OptionReliability.Medium, Source = "Hosted: uses no local resources"
        },

        // Singing and pictures: big NVIDIA jobs that free the card a few minutes after their last song or picture.
        new()
        {
            Id = "singing-acestep-soulx", Component = PlanComponent.Singing, DisplayName = "Singing (ACE-Step + SoulX)", HostRoleKind = "singing",
            Gpu = GpuRequirement.Nvidia, MinGpuGb = 6, Platforms = DockerNvidia, Steady = new(5.1, 8, 2, 30.6), Peak = new(7.2, 12, 4, 30.6),
            QualityTier = 3, Evidence = FootprintEvidence.Measured,
            Source = $"Measured: RTX 4070, worker peak 5.1-7.2 GB, models 15.9 GB and image 14.7 GB (docs/SINGING.md); RAM, CPU estimate ({Doc})"
        },
        new()
        {
            Id = "singing-acestep-vevosing", Component = PlanComponent.Singing, DisplayName = "Singing (ACE-Step + VevoSing)", HostRoleKind = "singing",
            Gpu = GpuRequirement.Nvidia, MinGpuGb = 8, Platforms = DockerNvidia, Steady = new(6.8, 8, 2, 35), Peak = new(6.8, 12, 4, 35),
            QualityTier = 3, Evidence = FootprintEvidence.Measured,
            Source = $"Measured: RTX 4070, worker peak 6.8 GB, models 20.3 GB and image 14.7 GB (docs/SINGING.md); RAM, CPU estimate ({Doc})"
        },
        new()
        {
            Id = "pictures-comfyui", Component = PlanComponent.Pictures, DisplayName = "Pictures (ComfyUI)", HostRoleKind = "pictures",
            Gpu = GpuRequirement.Nvidia, MinGpuGb = 8, Platforms = DockerNvidia, Steady = new(7, 12, 2, 31), Peak = new(8, 20, 4, 31),
            QualityTier = 3,
            Source = $"Disk: 20.7 GB of pinned models (docs/PICTURES_HOST.md) plus image estimate; VRAM and RAM estimate for bf16 with --lowvram ({Doc})"
        },
        new()
        {
            Id = "hosted:nvidia-build-pictures", Component = PlanComponent.Pictures, DisplayName = "NVIDIA Build FLUX (free endpoint)",
            Hosting = OptionHosting.External, ProviderId = "nvidia-build", ModelId = Pictures.PicturesSettings.NvidiaDefaultModel,
            QualityTier = 3, FreeTier = true, NeedsSignup = true, Reliability = OptionReliability.Medium, Evidence = FootprintEvidence.Sourced,
            Source = "docs/HOSTED_THINKING.md (the same nvapi- key covers NVIDIA's FLUX models)"
        }
    ];
}
