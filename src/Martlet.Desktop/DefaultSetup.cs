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
    /// <summary>What Windows and the desktop keep on the card.</summary>
    private const double DesktopGb = 0.8;

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
