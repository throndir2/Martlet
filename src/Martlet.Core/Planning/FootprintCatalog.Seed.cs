using Martlet.Core.Installation;

namespace Martlet.Core.Planning;

// Seed footprints, derived from the constants Martlet used before the planner (SetupAdvisor, DefaultSetup,
// ListeningAdvisor, MainWindow.LocalChatModels, SpeechEngines, DeepThinkingSlots). The footprint session replaces these
// numbers with measured or sourced ones (docs/RESOURCE_FOOTPRINTS.md); keep option ids stable, callers save them.
public sealed partial class FootprintCatalog
{
    private const string Constants = "Martlet constants before the planner (estimate)";
    private static readonly string[] DockerNvidia = ["windows", "linux"];
    private static readonly string[] WindowsOnly = ["windows"];

    private static ComponentOption Ollama(string id, string model, string name, double sizeGb, int tier, int firstWordMs, bool hears,
        FootprintEvidence evidence = FootprintEvidence.Estimate, string source = Constants) => new()
    {
        Id = id, Component = PlanComponent.Thinking, DisplayName = name, ModelId = model, HostRoleKind = "ollama",
        Gpu = GpuRequirement.AnyGpu, Steady = new(sizeGb + 0.5, 1, 1, sizeGb), Peak = new(sizeGb + 0.5, 1.5, 2, sizeGb),
        QualityTier = tier, FirstWordMs = firstWordMs, HearsAudio = hears, SeesImages = true, Evidence = evidence, Source = source
    };

    private static ComponentOption Deep(string model, string name, double sizeGb, int tier) => new()
    {
        Id = "deep-thinking:" + model, Component = PlanComponent.DeepThinking, DisplayName = name, ModelId = model,
        HostRoleKind = "deep-thinking", Gpu = GpuRequirement.AnyGpu, Steady = new(sizeGb + 0.5, 1.5, 2, sizeGb),
        Peak = new(sizeGb + 0.5, 2, 4, sizeGb), ContextGb = Math.Round(DeepThinkingSlots.ContextGb(sizeGb + 0.5), 1),
        QualityTier = tier, Source = "DeepThinkingSlots context estimate (estimate)"
    };

    private static IEnumerable<ComponentOption> SeedOptions() =>
    [
        // Thinking, local (Ollama). Sizes from MainWindow.LocalChatModels plus 0.5 GB runtime (ListeningAdvisor.OllamaModelGb).
        Ollama("gemma4:e2b", "gemma4:e2b", "Gemma 4 E2B", 4.6, 1, 150, hears: true, FootprintEvidence.Measured,
            "docs/VOICE_LATENCY.md (RTX 4070, first sentence)"),
        Ollama("qwen3.5:4b", "qwen3.5:4b", "Qwen3.5 4B", 3.4, 2, 250, hears: false),
        Ollama("gemma4:e4b", "gemma4:e4b", "Gemma 4 E4B", 6.6, 2, 250, hears: true),
        Ollama("gemma4:12b", "gemma4:12b", "Gemma 4 12B", 8.0, 3, 400, hears: true),
        Ollama("gemma4:26b", "gemma4:26b", "Gemma 4 26B", 18.7, 4, 600, hears: false),
        new()
        {
            Id = "gemma4:e2b-cpu", Component = PlanComponent.Thinking, DisplayName = "Gemma 4 E2B on the processor", ModelId = "gemma4:e2b",
            HostRoleKind = "ollama", Steady = new(0, 6, 6, 4.6), Peak = new(0, 7, 8, 4.6), QualityTier = 1, FirstWordMs = 2500,
            HearsAudio = true, SeesImages = true, Source = Constants
        },
        // Thinking, hosted. The hosted-endpoint session (docs/HOSTED_THINKING.md) owns hearing and reliability facts.
        new()
        {
            Id = "hosted:nvidia-build", Component = PlanComponent.Thinking, DisplayName = "NVIDIA Build (free endpoint)",
            Hosting = OptionHosting.External, ProviderId = "nvidia-build", ModelId = Settings.ChatCompletionsEndpointCatalog.NvidiaBuildDefaultModelId,
            QualityTier = 4, FirstWordMs = 500, SeesImages = true, FreeTier = true, NeedsSignup = true,
            Reliability = OptionReliability.Medium, Evidence = FootprintEvidence.Measured,
            Source = "ChatCompletionsEndpointCatalog (checked 2026-10-01: about 0.5 s per reply; models retire often)"
        },
        new()
        {
            Id = "hosted:openrouter", Component = PlanComponent.Thinking, DisplayName = "OpenRouter", Hosting = OptionHosting.External,
            ProviderId = "openrouter", ModelId = "google/gemma-4-26b-a4b-it", QualityTier = 4, FirstWordMs = 800, SeesImages = true,
            FreeTier = true, NeedsSignup = true, Reliability = OptionReliability.Low, Source = Constants
        },
        new()
        {
            Id = "hosted:openai", Component = PlanComponent.Thinking, DisplayName = "OpenAI", Hosting = OptionHosting.External,
            ProviderId = "openai", QualityTier = 5, FirstWordMs = 700, SeesImages = true, NeedsSignup = true, Source = Constants
        },

        // Voice. Engines from SpeechEngines (MinimumGpuMemoryGb), 4 GB while speaking (DefaultSetup.VoiceGb).
        new()
        {
            Id = "chatterbox-turbo", Component = PlanComponent.Voice, DisplayName = "Chatterbox Turbo", ModelId = "chatterbox-turbo",
            HostRoleKind = "chatterbox", Gpu = GpuRequirement.Nvidia, MinGpuGb = 6, Platforms = DockerNvidia,
            Steady = new(4, 3, 2, 6), Peak = new(4, 4, 4, 6), QualityTier = 4, FirstWordMs = 400, Source = Constants
        },
        new()
        {
            Id = "xtts-v2", Component = PlanComponent.Voice, DisplayName = "XTTS-v2", ModelId = "xtts-v2", HostRoleKind = "xtts",
            Gpu = GpuRequirement.Nvidia, MinGpuGb = 4, Platforms = DockerNvidia, Steady = new(4, 3, 2, 5), Peak = new(4, 4, 4, 5),
            QualityTier = 3, FirstWordMs = 300, Source = Constants
        },
        new()
        {
            Id = "windows-speech", Component = PlanComponent.Voice, DisplayName = "Windows voices", RunsInApp = true, Platforms = WindowsOnly,
            Steady = new(0, 0.2, 1, 0), Peak = new(0, 0.3, 1, 0), QualityTier = 1, FirstWordMs = 50, Source = Constants
        },
        new()
        {
            Id = "hosted:openai-tts", Component = PlanComponent.Voice, DisplayName = "OpenAI voice", Hosting = OptionHosting.External,
            ProviderId = "openai", QualityTier = 4, FirstWordMs = 600, NeedsSignup = true, Source = Constants
        },

        // Listening. Parakeet in the app on the processor; Whisper on an NVIDIA card (ListeningAdvisor needs).
        new()
        {
            Id = "parakeet-tdt-0.6b-v3-cpu", Component = PlanComponent.Listening, DisplayName = "Parakeet on the processor",
            ModelId = "parakeet-tdt-0.6b-v3", RunsInApp = true, Steady = new(0, 2, 4, 0.7), Peak = new(0, 2.5, 4, 0.7),
            QualityTier = 3, FirstWordMs = 200, Source = Constants
        },
        new()
        {
            Id = "whisper-large-v3-turbo-cuda", Component = PlanComponent.Listening, DisplayName = "Whisper large-v3 turbo on the graphics card",
            ModelId = "large-v3-turbo", HostRoleKind = "stt", Gpu = GpuRequirement.Nvidia, MinGpuGb = 4, Steady = new(2.5, 1, 1, 1.6),
            Peak = new(2.5, 1.5, 2, 1.6), QualityTier = 4, FirstWordMs = 250, Source = Constants
        },
        new()
        {
            Id = "hosted:openai-transcribe", Component = PlanComponent.Listening, DisplayName = "OpenAI transcription",
            Hosting = OptionHosting.External, ProviderId = "openai", QualityTier = 5, FirstWordMs = 600, NeedsSignup = true, Source = Constants
        },

        // Character and lip-sync.
        new()
        {
            Id = "character-live2d-vrm", Component = PlanComponent.Character, DisplayName = "Live2D or VRM character", RunsInApp = true,
            Steady = new(0, 0.5, 1, 0.2), Peak = new(0, 0.8, 1, 0.2), QualityTier = 3, Source = Constants
        },
        new()
        {
            Id = "loudness-lipsync", Component = PlanComponent.LipSync, DisplayName = "Loudness lip-sync", RunsInApp = true,
            Steady = new(0, 0, 0.1, 0), Peak = new(0, 0, 0.1, 0), QualityTier = 1, Source = Constants
        },
        new()
        {
            Id = "audio2face-3d", Component = PlanComponent.LipSync, DisplayName = "Advanced lip-sync (Audio2Face-3D)", HostRoleKind = "audio2face",
            Gpu = GpuRequirement.Nvidia, MinGpuGb = 4, Platforms = DockerNvidia, Steady = new(4, 3, 2, 8), Peak = new(4, 4, 2, 8),
            QualityTier = 4, Source = Constants
        },

        // Deep thinking: one think's context at the host context size on top of the model (DeepThinkingSlots).
        Deep("gemma4:e4b", "Gemma 4 E4B (deep thinking)", 6.6, 2),
        Deep("gemma4:12b", "Gemma 4 12B (deep thinking)", 8.0, 3),
        Deep("gemma4:26b", "Gemma 4 26B (deep thinking)", 18.7, 4),
        new()
        {
            Id = "hosted:nvidia-build-deep", Component = PlanComponent.DeepThinking, DisplayName = "NVIDIA Build (free endpoint)",
            Hosting = OptionHosting.External, ProviderId = "nvidia-build", QualityTier = 4, FreeTier = true, NeedsSignup = true,
            Reliability = OptionReliability.Medium, Source = Constants
        },

        // Singing and pictures: large NVIDIA jobs, rough estimates until measured.
        new()
        {
            Id = "singing-acestep-soulx", Component = PlanComponent.Singing, DisplayName = "Singing (ACE-Step + SoulX)", HostRoleKind = "singing",
            Gpu = GpuRequirement.Nvidia, MinGpuGb = 8, Platforms = DockerNvidia, Steady = new(8, 8, 4, 20), Peak = new(8, 10, 6, 20),
            QualityTier = 3, Source = Constants
        },
        new()
        {
            Id = "pictures-comfyui", Component = PlanComponent.Pictures, DisplayName = "Pictures (ComfyUI)", HostRoleKind = "pictures",
            Gpu = GpuRequirement.Nvidia, MinGpuGb = 8, Platforms = DockerNvidia, Steady = new(8, 8, 4, 15), Peak = new(8, 10, 6, 15),
            QualityTier = 3, Source = Constants
        }
    ];
}
