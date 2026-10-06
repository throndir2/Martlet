using System.Globalization;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>What Set it all up for me does on a PC that isn't in a Martlet network: Thinking with the smallest model that hears
/// (Gemma 4 E2B) in Ollama here; a voice that fits (a voice engine on the NVIDIA graphics card when it has room beside the
/// Thinking model, otherwise a Windows voice on the processor); and listening on the Windows default microphone with Parakeet
/// on the processor, or Whisper on the graphics card when room is left after the voice. The voice comes before listening on
/// the graphics card. <paramref name="ThinkingOnGpu"/>: the Thinking model fits the graphics card (Ollama otherwise runs part
/// of it on the processor). <paramref name="Listening"/> is the Whisper advice used when <paramref name="ListenOnGpu"/>.</summary>
internal sealed record DefaultSetupPlan(LocalChatModel Thinking, bool ThinkingOnGpu, SpeechEngine? Voice, bool ListenOnGpu,
    ListeningAdvice Listening, string ParakeetModel, string Gpu)
{
    /// <summary>One line per job, as the tour and the confirmation show them.</summary>
    internal string Describe(bool thinking = true, bool listening = true, bool voice = true)
    {
        var lines = new List<string>();
        if (thinking)
            lines.Add($"Thinking: {Thinking.Id} in Ollama on this PC ({Thinking.Size}{(ThinkingOnGpu ? ", on the graphics card" : ", partly on the processor")}). " +
                "It's the smallest model that also hears your voice.");
        if (voice)
            lines.Add(Voice is { } engine
                ? $"Voice: {engine.Name} on the graphics card. Martlet speaks with a Windows voice until it's ready."
                : "Voice: a Windows voice on the processor, since the graphics card has no room for a voice engine beside Thinking.");
        if (listening)
            lines.Add(ListenOnGpu
                ? $"Listening: your Windows default microphone, with Whisper ({Listening.GpuModel}) on the graphics card. Parakeet on the processor listens until it's ready."
                : $"Listening: your Windows default microphone, with Parakeet on the processor{(Voice is null ? "" : ", leaving the graphics card to Thinking and the voice")}.");
        return string.Join("\n", lines);
    }
}

internal static class DefaultSetup
{
    /// <summary>Graphics memory a voice engine takes while it speaks (Chatterbox Turbo; the setup advisor's estimate).</summary>
    internal const double VoiceGb = 4;
    /// <summary>Graphics memory Audio2Face lip-sync takes on this PC's host service.</summary>
    internal const double LipSyncGb = 3;
    /// <summary>What Windows and the desktop keep on the card.</summary>
    private const double DesktopGb = 0.8;

    /// <summary>The welcome wizard's suggestion for this PC: <see cref="Plan"/>, with Thinking moved to NVIDIA Build when the
    /// owner allows free online services and Thinking would otherwise crowd this PC (partly on the processor, or taking the card
    /// a voice engine needs), and lip-sync by Audio2Face when the card has room left, otherwise by voice loudness.</summary>
    internal static WelcomePlan Recommend(WelcomeSpecs specs, IReadOnlyList<GpuNow> gpus, GpuInfo? windowsGpu, CultureInfo language,
        WelcomePreference preference)
    {
        var local = Plan(gpus, windowsGpu, specs.Threads, language);
        var online = preference == WelcomePreference.FreeOnline &&
            (!local.ThinkingOnGpu || local.Voice is null && Plan(gpus, windowsGpu, specs.Threads, language, thinkingGb: 0).Voice is not null);
        var plan = online ? Plan(gpus, windowsGpu, specs.Threads, language, thinkingGb: 0) : local;
        var parts = new List<WelcomePart>();
        var model = MainWindow.LocalChatModels.First(m => m.Id == plan.Thinking.Id);
        var modelGb = ListeningAdvisor.OllamaModelGb(model.Id);
        var cardGb = specs.VramGb ?? 0;
        var cardFree = Math.Max(0, cardGb - Math.Max(specs.VramUsedGb ?? 0, DesktopGb));
        if (online)
            parts.Add(new(WelcomeJob.Thinking, $"{ChatCompletionsEndpointCatalog.NvidiaBuildDefaultModelId} on NVIDIA Build", WelcomePlace.Online, 0, 0.2, 0.2,
                local.ThinkingOnGpu
                    ? "Online, so this PC's graphics card is left for a natural voice. Free with an NVIDIA key; what you say goes to NVIDIA."
                    : $"Online: {model.Id} doesn't fit this PC's graphics card, and on the processor replies would be slow. Free with an NVIDIA key; what you say goes to NVIDIA."));
        else if (plan.ThinkingOnGpu)
            parts.Add(new(WelcomeJob.Thinking, $"{model.Id} in Ollama", WelcomePlace.ThisPc, modelGb, 1, 1,
                "The smallest local model that also hears your voice, on the graphics card for quick replies."));
        else
        {
            var onCard = Math.Min(cardFree, modelGb);
            parts.Add(new(WelcomeJob.Thinking, $"{model.Id} in Ollama", WelcomePlace.ThisPc, onCard, modelGb - onCard + 1, Math.Max(4, specs.Threads / 2),
                cardGb > 0 ? "Partly on the processor: the graphics card has too little room, so replies are slower."
                    : "On the processor: this PC has no graphics card Martlet can use, so replies are slower." +
                      (preference == WelcomePreference.LocalOnly ? " Free online services would answer faster." : "")));
        }
        parts.Add(plan.Voice is { } engine
            ? new(WelcomeJob.Voice, engine.Name, WelcomePlace.ThisPc, VoiceGb, 2, 1, "A natural voice on the graphics card. A Windows voice speaks until it's ready.")
            : new(WelcomeJob.Voice, "Windows voice", WelcomePlace.ThisPc, 0, 0.1, 0.5,
                cardGb >= SpeechEngines.Default.MinimumGpuMemoryGb ? $"{SpeechEngines.Default.Name} doesn't fit beside Thinking on the graphics card, so a Windows voice speaks."
                    : $"{SpeechEngines.Default.Name} needs an NVIDIA graphics card with {SpeechEngines.Default.MinimumGpuMemoryGb:0} GB or more, so a Windows voice speaks."));
        parts.Add(plan.ListenOnGpu
            ? new(WelcomeJob.Listening, $"Whisper {plan.Listening.GpuModel}", WelcomePlace.ThisPc, plan.Listening.GpuModel == "large-v3-turbo" ? 2.5 : 1, 1, 1,
                "On the graphics card beside the voice, with your default microphone.")
            : new(WelcomeJob.Listening, "Parakeet", WelcomePlace.ThisPc, 0, 0.8, 2, "On the processor with your default microphone: quick and needs no graphics card."));
        var used = parts.Sum(p => p.VramGb);
        var audio2Face = gpus.MaxBy(g => g.TotalGb) is { } card && card.TotalGb >= 4 && cardFree - used >= LipSyncGb;
        parts.Add(audio2Face
            ? new(WelcomeJob.LipSync, "Audio2Face", WelcomePlace.ThisPc, LipSyncGb, 2, 1,
                "Natural mouth movement on the graphics card, in Docker. The mouth follows the voice's loudness until it's ready.")
            : new(WelcomeJob.LipSync, "Voice loudness", WelcomePlace.ThisPc, 0, 0, 0.1,
                gpus.Count == 0 ? "The mouth opens with the voice's loudness: Audio2Face needs an NVIDIA graphics card."
                    : "The mouth opens with the voice's loudness: the graphics card has no room left for Audio2Face."));
        return new(specs, preference, plan, parts);
    }

    /// <summary>This PC's hardware for planning, from Windows (<paramref name="machine"/>) and nvidia-smi (<paramref name="gpus"/>).</summary>
    internal static WelcomeSpecs Specs(MachineInfo machine, IReadOnlyList<GpuNow> gpus)
    {
        var card = gpus.MaxBy(g => g.TotalGb);
        var windows = machine.BestGpu;
        var name = card?.Name ?? windows?.Name;
        var vendor = name is null ? null
            : name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || card is not null ? "NVIDIA"
            : name.Contains("AMD", StringComparison.OrdinalIgnoreCase) || name.Contains("Radeon", StringComparison.OrdinalIgnoreCase) ? "AMD"
            : name.Contains("Intel", StringComparison.OrdinalIgnoreCase) ? "Intel" : "Other";
        return new(vendor, name, card?.TotalGb ?? windows?.MemoryGb, card?.UsedGb, machine.MemoryGb, machine.Threads, machine.Processor);
    }

    /// <summary>The smallest suggested model that hears your recording: the omni model Martlet starts with.</summary>
    internal static LocalChatModel SmallestHearingModel =>
        MainWindow.LocalChatModels.Where(m => m.Hears).MinBy(m => ListeningAdvisor.OllamaModelGb(m.Id))!;

    /// <summary>Plans the default setup from this PC's NVIDIA cards as nvidia-smi reports them now (<paramref name="gpus"/>; empty
    /// without an NVIDIA driver), the card Windows reports, the processor's threads and the display language.
    /// <paramref name="thinkingGb"/> is the graphics memory Thinking takes here: null plans the default model; 0 means Thinking
    /// runs elsewhere (a cloud provider, another computer).</summary>
    internal static DefaultSetupPlan Plan(IReadOnlyList<GpuNow> gpus, GpuInfo? windowsGpu, int threads, CultureInfo language, double? thinkingGb = null)
    {
        var thinking = SmallestHearingModel;
        var llmGb = thinkingGb ?? ListeningAdvisor.OllamaModelGb(thinking.Id);
        var card = gpus.MaxBy(g => g.TotalGb);
        var cardGb = card?.TotalGb ?? windowsGpu?.MemoryGb ?? 0;
        var thinkingOnGpu = cardGb - Math.Max(card?.UsedGb ?? 0, DesktopGb) >= llmGb;
        var engine = SpeechEngines.Default;
        // The voice engine needs an NVIDIA card big enough for it and room left on it beside Thinking (and what already runs).
        var voiceFree = card is null ? 0 : card.TotalGb - Math.Max(card.UsedGb, DesktopGb + llmGb);
        var voiceOnGpu = card is not null && card.TotalGb >= engine.MinimumGpuMemoryGb - 0.25 && voiceFree >= VoiceGb;
        var loads = new List<GpuLoad>();
        if (llmGb > 0) loads.Add(new($"Ollama {thinking.Id}", llmGb));
        if (voiceOnGpu) loads.Add(new(engine.Name, VoiceGb));
        var listening = ListeningAdvisor.Advise(gpus, windowsGpu, loads, threads, null);
        // Whisper on the card shares the voice engine's host service; without it, Parakeet on the processor needs no Docker.
        var listenOnGpu = voiceOnGpu && listening.UseGpu;
        var gpu = card is not null ? $"{card.Name} ({card.TotalGb.ToString("0.#", CultureInfo.InvariantCulture)} GB)"
            : windowsGpu?.Describe() ?? "no dedicated graphics card";
        return new(thinking, thinkingOnGpu, voiceOnGpu ? engine : null, listenOnGpu, listening,
            LocalSpeechSetup.RecommendedParakeetModel(language), gpu);
    }
}

/// <summary>The welcome wizard's answer to "where may Martlet do its thinking?".</summary>
internal enum WelcomePreference { LocalOnly, FreeOnline }

internal enum WelcomeJob { Thinking, Listening, Voice, LipSync }

internal enum WelcomePlace { ThisPc, Online, OtherComputer }

/// <summary>This PC's hardware as the welcome wizard read it: the main graphics card's vendor, name, memory and memory in use
/// (nvidia-smi; Windows reports the total only), system memory and the processor's threads.</summary>
internal sealed record WelcomeSpecs(string? GpuVendor, string? GpuName, double? VramGb, double? VramUsedGb, double? RamGb, int Threads, string? Processor)
{
    internal string Describe()
    {
        var gpu = GpuName is null ? "no dedicated graphics card"
            : VramGb is { } gb ? $"{GpuName} ({gb.ToString("0.#", CultureInfo.InvariantCulture)} GB graphics memory)" : GpuName;
        var ram = RamGb is { } r ? $"{r.ToString("0", CultureInfo.InvariantCulture)} GB memory" : "memory unknown";
        return $"{gpu} · {ram} · {Threads} processor threads" + (Processor is null ? "" : $" ({Processor})");
    }
}

/// <summary>One part of the suggestion: the job, what does it, where, and what it takes of this PC (graphics memory and memory
/// in GB, processor threads) with the reason in plain words.</summary>
internal sealed record WelcomePart(WelcomeJob Job, string What, WelcomePlace Place, double VramGb, double RamGb, double Threads, string Reason)
{
    internal static string Title(WelcomeJob job) => job switch
    {
        WelcomeJob.Thinking => "Thinking",
        WelcomeJob.Listening => "Listening",
        WelcomeJob.Voice => "Voice",
        _ => "Lip-sync"
    };

    internal string Where => Place switch
    {
        WelcomePlace.Online => "online (free)",
        WelcomePlace.OtherComputer => "another of your computers",
        _ => "this PC"
    };
}

/// <summary>The welcome wizard's suggestion: each part with its share of this PC, and the default-setup plan that applies it.</summary>
internal sealed record WelcomePlan(WelcomeSpecs Specs, WelcomePreference Preference, DefaultSetupPlan Setup, IReadOnlyList<WelcomePart> Parts)
{
    internal bool ThinkingOnline => Parts.Any(p => p.Job == WelcomeJob.Thinking && p.Place == WelcomePlace.Online);

    /// <summary>A share of this PC's resource in percent (0 when the PC lacks it).</summary>
    internal static int Percent(double used, double? total) => total is > 0 ? (int)Math.Clamp(Math.Round(used / total.Value * 100), 0, 999) : 0;

    internal (int Vram, int Ram, int Cpu) Share(WelcomePart part) =>
        (Percent(part.VramGb, Specs.VramGb), Percent(part.RamGb, Specs.RamGb), Percent(part.Threads, Specs.Threads));

    internal (int Vram, int Ram, int Cpu) Total() =>
        (Percent(Parts.Sum(p => p.VramGb), Specs.VramGb), Percent(Parts.Sum(p => p.RamGb), Specs.RamGb), Percent(Parts.Sum(p => p.Threads), Specs.Threads));

    /// <summary>One line per part, as MCP and the log read it.</summary>
    internal string Describe(WelcomePart part)
    {
        var (vram, ram, cpu) = Share(part);
        return $"{WelcomePart.Title(part.Job)}: {part.What}, {part.Where}. Uses {vram}% graphics memory, {ram}% memory, {cpu}% processor. {part.Reason}";
    }
}
