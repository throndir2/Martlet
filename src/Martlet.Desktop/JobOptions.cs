using System.Globalization;
using Martlet.Core.Planning;
using Martlet.Core.Settings;
using Martlet.Providers;
using Martlet.Sherpa;

namespace Martlet.Desktop;

/// <summary>The option lists of the job pages (Companion › Thinking, Voice, Listening and Lip-sync) for the option picker
/// (MainWindow.OptionPicker.cs). Pure: each turns models, engines, apps or providers into <see cref="PickerOption"/>s with
/// their facts (<see cref="OptionFacts"/>), badge, where each stands and why one can't run here. The page then adds each
/// option's own lines and button.</summary>
internal static class JobOptions
{
    /// <summary>Thinking › This PC › Ollama: the option for typing any Ollama model.</summary>
    internal const string OtherOllamaModel = "Other";
    /// <summary>Thinking › This PC › Model app: Ollama, and a model app typed by its address.</summary>
    internal const string OllamaApp = "Ollama", AddressApp = "Address";
    internal const string Recommended = "recommended", InUse = "in use";

    /// <summary><paramref name="facts"/> with <paramref name="more"/> added before "evidence" (how the numbers are known),
    /// which stays last.</summary>
    internal static IReadOnlyList<OptionFact> With(IEnumerable<OptionFact> facts, params OptionFact[] more)
    {
        var list = facts.ToList();
        var at = list.FindIndex(f => f.Key == "evidence");
        list.InsertRange(at < 0 ? list.Count : at, more);
        return list;
    }

    private static readonly OptionFact OllamaRunsOn =
        new("runs-on", "Runs on", "a graphics card (NVIDIA, AMD, Intel or Apple), or the processor without one", "GPU");

    // ---------- Thinking › This PC ----------

    /// <summary>Ollama's models: the suggestions (fastest first; the smallest is recommended and the largest that fits this
    /// PC's card is the smartest choice), then models Ollama already has (and the one Thinking uses) that aren't suggestions,
    /// then <see cref="OtherOllamaModel"/> for typing any name. <paramref name="downloaded"/> is null until Ollama was asked.</summary>
    internal static IReadOnlyList<PickerOption> OllamaModels(IReadOnlyList<LocalChatModel> suggestions, IReadOnlyList<string>? downloaded,
        string? inUse, double? cardGb)
    {
        var recommended = suggestions[0];
        var smartest = suggestions.Where(m => m.MinimumVramGb <= (cardGb ?? 0) + 0.5).OrderBy(m => m.MinimumVramGb).LastOrDefault() ?? recommended;
        var options = new List<PickerOption>();
        foreach (var model in suggestions)
        {
            var footprint = FootprintCatalog.Default.Find(model.Id);
            List<OptionFact> facts = footprint is null ? [OllamaRunsOn] : [.. OptionFacts.ThinkingModel(footprint, cardGb, model.MinimumVramGb, callsTools: true)];
            if (Here(downloaded, model.Id, footprint) is { } here) facts.Insert(Math.Min(1, facts.Count), here);
            var summary = model == recommended
                ? "Fastest: replies start soonest, and it hears your voice."
                : "Smarter but slower" + (model == smartest ? ": the largest that fits this PC's graphics card" : "") + ". " +
                  (model.Hears ? "It hears your voice." : "It gets the transcript of what you say.");
            options.Add(new(model.Id, footprint?.DisplayName ?? model.Id, summary)
            {
                Badge = model.Id == inUse ? InUse : model == recommended ? Recommended : model == smartest ? "smartest that fits" : null,
                InUse = model.Id == inUse, Facts = OptionFacts.Lead(facts, "fits", "here", "vram", "speed", "hears")
            });
        }
        var others = (downloaded ?? []).Append(inUse).OfType<string>().Distinct(StringComparer.Ordinal)
            .Where(id => suggestions.All(m => m.Id != id));
        foreach (var id in others)
            options.Add(new(id, id, "Martlet has no numbers for this model. Test model shows how it does on this PC.")
            {
                Badge = id == inUse ? InUse : null, InUse = id == inUse,
                Facts = [OllamaRunsOn, .. Here(downloaded, id, null) is { } here ? [here] : Array.Empty<OptionFact>()]
            });
        options.Add(new(OtherOllamaModel, "Another Ollama model",
            "Any model from ollama.com/library, a Hugging Face GGUF as hf.co/<user>/<repo>:<quant>, or one you made with ollama create.")
        {
            Facts = [OllamaRunsOn, new("download", "Download", "Download model gets it once")]
        });
        return options;
    }

    private static OptionFact? Here(IReadOnlyList<string>? downloaded, string id, ComponentOption? footprint) =>
        downloaded is null ? null
        : downloaded.Contains(id, StringComparer.Ordinal) ? new("here", "On this PC", "downloaded in Ollama", "downloaded")
        : new("here", "On this PC", "not downloaded yet" + (footprint is { Peak.DiskGb: > 0 } f
            ? $": about {f.Peak.DiskGb.ToString("0.#", CultureInfo.InvariantCulture)} GB" : ""));

    /// <summary>The model apps that can run Thinking's model on this PC: Ollama, each other app found here, and one typed by
    /// its address. <paramref name="inUseBaseUrl"/> is the address Thinking uses when it uses another app here.</summary>
    internal static IReadOnlyList<PickerOption> LocalApps(IReadOnlyList<LocalModelServer> found, bool looking, bool ollamaInstalled,
        bool ollamaInUse, string? inUseBaseUrl)
    {
        static OptionFact Data(string app) => new("data", "Your data", $"stays on this PC; {app} decides what it keeps", null);
        var options = new List<PickerOption>
        {
            new(OllamaApp, "Ollama",
                "Martlet installs Ollama, downloads a model that fits this PC and keeps it ready. Any Ollama model works, including your own.")
            {
                Badge = ollamaInUse ? InUse : Recommended, InUse = ollamaInUse,
                Facts =
                [
                    OllamaRunsOn,
                    new("setup", "Set up by", "Martlet: one click installs it and a model", "Martlet sets it up"),
                    new("here", "On this PC", ollamaInstalled ? "installed" : "not installed yet", ollamaInstalled ? "installed" : null),
                    new("cost", "Cost", "free: no per-request cost", "free"),
                    new("data", "Your data", "stays on this PC", null)
                ]
            }
        };
        foreach (var server in found)
        {
            var used = server.ChatCompletionsBaseUrl == inUseBaseUrl;
            var models = server.NeedsKey ? "asks for an API key first" : server.Models.Count == 1 ? "1 model" : $"{server.Models.Count} models";
            options.Add(new(AppKey(server), server.Name, $"Your {server.Name} at {server.ChatCompletionsBaseUrl}. Martlet uses the models it serves.")
            {
                Badge = used ? InUse : null, InUse = used,
                Facts =
                [
                    new("runs-on", "Runs on", $"what {server.Name} uses: a graphics card or the processor", null),
                    new("models", "Models", models, models),
                    new("setup", "Set up by", $"you, in {server.Name}", "you set it up"),
                    new("address", "Address", server.ChatCompletionsBaseUrl, null),
                    new("cost", "Cost", "free: no per-request cost", null),
                    Data(server.Name)
                ]
            });
        }
        var typed = inUseBaseUrl is not null && found.All(s => s.ChatCompletionsBaseUrl != inUseBaseUrl);
        options.Add(new(AddressApp, "Another app, by its address",
            "LM Studio, llama.cpp, KoboldCpp, Jan, vLLM, Lemonade, GPT4All, Docker Model Runner or any app with an OpenAI-compatible " +
            "server on this PC. " + (looking ? "Looking for them on this PC..." : found.Count == 0 ? "None answers on this PC now." : "Type the address it shows."))
        {
            Badge = typed ? InUse : null, InUse = typed,
            Facts =
            [
                new("runs-on", "Runs on", "what the app uses: a graphics card or the processor", null),
                new("setup", "Set up by", "you, in the app; start its local server", "you set it up"),
                new("cost", "Cost", "free: no per-request cost", null),
                Data("the app")
            ]
        });
        return options;
    }

    /// <summary>A found app's option key: its id and port ("lm-studio-1234"), so two copies of one app stay apart.</summary>
    internal static string AppKey(LocalModelServer server) =>
        server.Id + "-" + (Uri.TryCreate(server.ChatCompletionsBaseUrl, UriKind.Absolute, out var uri) ? uri.Port.ToString(CultureInfo.InvariantCulture) : "0");

    // ---------- cloud providers ----------

    /// <summary>A provider's option key: "openai", "nvidia-build", "openrouter", "google-gemini", "ollama" or "custom".</summary>
    internal static string ProviderKey(MainWindow.CloudProvider provider) =>
        !provider.Chat || provider.BaseUrl == "https://api.openai.com/v1" ? "openai"
        : provider.BaseUrl == MainWindow.LocalOllamaBaseUrl ? "ollama"
        : ChatCompletionsEndpointCatalog.Named(provider.BaseUrl)?.Id ?? "custom";

    /// <summary>The providers a job can use online (and, for If Thinking fails, Ollama on this PC), with what each costs, what
    /// it needs and what leaves this PC. <paramref name="component"/> picks the catalog numbers (Thinking, Voice or Listening);
    /// <paramref name="sent"/> is what goes there ("your messages and recent conversation").</summary>
    internal static IReadOnlyList<PickerOption> Providers(IReadOnlyList<MainWindow.CloudProvider> providers, PlanComponent component,
        string? inUseKey, string sent, string? recommendedKey = null)
    {
        var options = new List<PickerOption>();
        foreach (var provider in providers)
        {
            var key = ProviderKey(provider);
            var catalog = FootprintCatalog.Default;
            var option = (key, component) switch
            {
                ("openai", PlanComponent.Voice) => catalog.Find(FootprintCatalog.OpenAiVoiceId),
                ("openai", PlanComponent.Listening) => catalog.Find("hosted:openai-transcribe"),
                ("ollama", _) => catalog.Find(MainWindow.LocalChatModels[0].Id),
                _ => catalog.Find("hosted:" + key)
            };
            IReadOnlyList<OptionFact> facts = key switch
            {
                "ollama" =>
                [
                    .. option is null ? [OllamaRunsOn] : OptionFacts.Of(option).Where(f => f.Key is "runs-on" or "vram" or "speed" or "hears" or "sees"),
                    new("cost", "Cost", "free: no per-request cost", "free"),
                    new("key", "API key", "none", null),
                    new("data", "Your data", "stays on this PC", "stays here")
                ],
                "custom" => OptionFacts.Hosted("the server", null, null, keyNeeded: false, sent),
                "google-gemini" => OptionFacts.Hosted(provider.Name, null, true, true, sent, hears: true, sees: true),
                _ => OptionFacts.Hosted(provider.Name, option, key is "nvidia-build" or "openrouter" ? true : option?.FreeTier == true ? true : false,
                    provider.NeedsKey, sent)
            };
            var model = provider.DefaultModel ?? component switch
            {
                PlanComponent.Voice => OpenAiSpeechSynthesisCatalog.DefaultModelId,
                PlanComponent.Listening => OpenAiTranscriptionCatalog.DefaultModelId,
                _ => OpenAiTextGenerationCatalog.DefaultModelId
            };
            var summary = key switch
            {
                "openai" => $"OpenAI's own models; recommended: {model}. Each request costs money.",
                "nvidia-build" => $"Free keys from NVIDIA; recommended: {model}. NVIDIA retires models often.",
                "openrouter" => $"Hundreds of models behind one key, some free; recommended: {model}.",
                "google-gemini" => $"Free on Google's free tier; recommended: {model}. It sees your screen and can hear you.",
                "ollama" => $"A model in Ollama on this PC, such as {model}. Nothing leaves this PC.",
                "custom" => "Any OpenAI-compatible server, by its HTTPS address and the exact model ID.",
                _ => $"Recommended: {model}."
            };
            options.Add(new(key, provider.Name, summary)
            {
                Badge = key == inUseKey ? InUse : key == recommendedKey ? Recommended : null, InUse = key == inUseKey, Facts = facts
            });
        }
        return options;
    }

    // ---------- Voice ----------

    /// <summary>The voice engines on the shown computer, with where each stands there (<see cref="MainWindow.EngineSpot"/>):
    /// the one that speaks, the <paramref name="recommended"/> one, and why one can't run there.</summary>
    internal static IReadOnlyList<PickerOption> VoiceEngines(IEnumerable<(SpeechEngine Engine, MainWindow.EngineSpot Spot)> spots,
        SpeechEngine? recommended) =>
        [.. spots.Select(s => new PickerOption(s.Engine.Key, s.Engine.Name, s.Engine.Summary)
        {
            Badge = s.Spot.InUse ? InUse : s.Spot.Pending ? "setting up" : s.Engine == recommended ? Recommended : null,
            InUse = s.Spot.InUse, Facts = OptionFacts.VoiceEngine(s.Engine),
            State = s.Spot.Cannot is null ? s.Spot.State : null, Unavailable = s.Spot.Cannot
        })];

    /// <summary>The cloud voices: OpenAI's built-in voices and ElevenLabs with a voice cloned from yours.</summary>
    internal static IReadOnlyList<PickerOption> CloudVoices(bool openAiInUse, bool elevenLabsInUse) =>
    [
        new("openai", "OpenAI voice", "OpenAI's built-in voices, such as alloy. Quick and clear, but not your voice.")
        {
            Badge = openAiInUse ? InUse : null, InUse = openAiInUse,
            Facts = OptionFacts.Voice(FootprintCatalog.Default.Find(FootprintCatalog.OpenAiVoiceId), VoiceAbilities.OpenAiVoice,
                "many languages: it reads the reply's language", streams: true, samples: null, "OpenAI terms (paid)", nonCommercial: false, cloud: "OpenAI")
        },
        new(SpeechEngines.ElevenLabs.Key, SpeechEngines.ElevenLabs.Name, SpeechEngines.ElevenLabs.Summary)
        {
            Badge = elevenLabsInUse ? InUse : null, InUse = elevenLabsInUse, Facts = OptionFacts.VoiceEngine(SpeechEngines.ElevenLabs)
        }
    ];

    // ---------- Listening › This PC ----------

    internal const string WhisperGpu = "whisper-gpu", WhisperCpu = "whisper-cpu";

    /// <summary>Where a speech recognizer on this PC stands.</summary>
    internal sealed record RecognizerState(string? ParakeetInUse, string RecommendedParakeet, Func<string, bool> Installed, string? Downloading,
        string? Progress, bool RuntimeReady, ListeningAdvice? Advice, bool GpuInUse, bool CpuInUse, int Threads);

    /// <summary>The speech recognizers on this PC: the three Parakeet models inside Martlet (no Docker), then Whisper on the
    /// graphics card and on the processor in Martlet's host service.</summary>
    internal static IReadOnlyList<PickerOption> Recognizers(RecognizerState state)
    {
        var options = new List<PickerOption>();
        foreach (var model in ParakeetModels.All)
        {
            var (footprintId, summary, accuracy) = model.Id switch
            {
                ParakeetModels.Tdt110mEnglishId => ("parakeet-tdt-110m-en-cpu",
                    "Fastest in English: a short sentence becomes text in about 0.1 s. Less accurate with background noise or a distant microphone.",
                    "good; less accurate with background noise or a distant microphone"),
                ParakeetModels.V2EnglishId => ("parakeet-tdt-0.6b-v2-cpu",
                    "Most accurate in English: it hears noisy rooms and distant microphones best, about 0.2 s slower.",
                    "best in English, also in noisy rooms and with distant microphones"),
                _ => ("parakeet-tdt-0.6b-v3-cpu", "English and 24 other European languages, which it recognizes by itself.",
                    "very good in 25 languages")
            };
            var used = state.ParakeetInUse == model.Id;
            var installed = state.Installed(model.Id);
            var size = SherpaComponents.Megabytes(model.DownloadBytes);
            var footprint = FootprintCatalog.Default.Find(footprintId)!;
            var languages = model.EnglishOnly ? "English" : "25 languages";
            options.Add(new(model.Id, model.Name, summary)
            {
                Badge = used ? InUse : state.Downloading == model.Id ? "downloading" : model.Id == state.RecommendedParakeet ? Recommended : null,
                InUse = used,
                Facts = With(OptionFacts.SpeechRecognizer(footprint, model.Languages, accuracy, streams: false)
                        .Select(f => f.Key == "languages" ? f with { Short = languages } : f),
                    new("here", "On this PC", installed ? "downloaded" : $"downloads once: {size}", installed ? "downloaded" : null),
                    new("setup", "Set up by", "Martlet, inside the app: no Docker", null)),
                State = state.Downloading == model.Id ? state.Progress ?? "Downloading..." : installed ? "Downloaded." : $"Downloads once: {size}.",
                Unavailable = state.RuntimeReady ? null : "Martlet's speech runtime (sherpa-onnx) isn't on this PC. Install Martlet again to add it."
            });
        }
        var advice = state.Advice;
        var gpuFootprint = FootprintCatalog.Default.Find(advice?.GpuModel == "small" ? "whisper-small-cuda" : "whisper-large-v3-turbo-cuda")!;
        const string whisperLanguages = "English and 98 other languages";
        options.Add(new(WhisperGpu, "Whisper on the graphics card",
            "Fast and accurate in many languages. It runs in Martlet's host service in Docker on this PC.")
        {
            Badge = state.GpuInUse ? InUse : null, InUse = state.GpuInUse,
            Facts = With(OptionFacts.SpeechRecognizer(gpuFootprint, whisperLanguages, "very good, also in noisy rooms", streams: false)
                    .Select(f => f.Key == "languages" ? f with { Short = "99 languages" } : f),
                new("model", "Model", advice?.GpuModel ?? "large-v3-turbo, or small when the card is busy", null),
                new("setup", "Set up by", "Martlet, in Docker on this PC", "Docker")),
            State = advice is null ? "Checking this PC's graphics card..." : null,
            Unavailable = advice?.GpuBlocked
        });
        var cpuModel = advice?.CpuModel ?? (state.Threads >= 6 ? "small" : "base");
        options.Add(new(WhisperCpu, "Whisper on the processor", $"Works on any PC and keeps the graphics card free, but slower. Uses the {cpuModel} model.")
        {
            Badge = state.CpuInUse ? InUse : null, InUse = state.CpuInUse,
            Facts = With(OptionFacts.SpeechRecognizer(FootprintCatalog.Default.Find("whisper-small-cpu")!, whisperLanguages, "good", streams: false)
                    .Select(f => f.Key == "languages" ? f with { Short = "99 languages" } : f),
                new("model", "Model", cpuModel, null),
                new("setup", "Set up by", "Martlet, in Docker on this PC", "Docker"))
        });
        return options;
    }

    // ---------- Lip-sync › This PC ----------

    internal const string Audio2Face = "Audio2Face", Loudness = "Loudness", OwnService = "Own";

    /// <summary>The ways the mouth moves on this PC: Audio2Face in Docker here, voice loudness (no setup), and an Audio2Face
    /// service the owner runs. <paramref name="cannot"/>: why this PC can't run Audio2Face at all (an ARM processor);
    /// <paramref name="fits"/>: it has an NVIDIA card of 4 GB or more.</summary>
    internal static IReadOnlyList<PickerOption> LipSyncWays(bool audio2FaceInUse, bool notInstalled, bool loudnessInUse, bool ownInUse,
        bool fits, string? cannot, string endpoint)
    {
        var catalog = FootprintCatalog.Default;
        return
        [
            new(Audio2Face, "Audio2Face on this PC",
                "NVIDIA Audio2Face moves the mouth naturally with Martlet's voice. It runs in Docker with NVIDIA's open-source engine: no NVIDIA account or key.")
            {
                Badge = notInstalled ? "chosen, not installed yet" : audio2FaceInUse ? InUse : fits && cannot is null ? Recommended : null,
                InUse = audio2FaceInUse,
                Facts = With(OptionFacts.Of(catalog.Find("audio2face-3d")!),
                    new("setup", "Set up by", "Martlet, in Docker on this PC", "Docker"),
                    new("data", "Your data", "Martlet's voice stays on this PC", null)),
                Unavailable = cannot
            },
            new(Loudness, "Voice loudness",
                "Advanced lip-sync off: the mouth opens and closes with Martlet's voice. Any character and graphics card, nothing to install.")
            {
                Badge = loudnessInUse ? InUse : !fits || cannot is not null ? Recommended : null, InUse = loudnessInUse,
                Facts = With(OptionFacts.Of(catalog.Find("loudness-lipsync")!), new OptionFact("setup", "Set up by", "nothing: it is built in", "built in"))
            },
            new(OwnService, "Your own Audio2Face service",
                $"Advanced: an Audio2Face service you already run at {endpoint}. The mouth follows voice loudness whenever it doesn't answer.")
            {
                Badge = ownInUse ? InUse : null, InUse = ownInUse,
                Facts =
                [
                    new("runs-on", "Runs on", $"your own service at {endpoint}", "your service"),
                    new("setup", "Set up by", "you, outside Martlet", "you set it up")
                ]
            }
        ];
    }
}
