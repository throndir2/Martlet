using System.Globalization;
using Martlet.Core.Planning;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>What Set it all up for me sets up on this PC, as the placement engine (<see cref="PlacementEngine"/>) planned it:
/// Thinking in Ollama here (<paramref name="ThinkingOnGpu"/>: on the graphics card, otherwise on the processor) unless it is
/// hosted or set up already; a voice engine on the NVIDIA card when it has room, otherwise a Windows voice; listening on the
/// Windows default microphone with Parakeet on the processor, or Whisper on the card (<paramref name="Listening"/>'s GPU
/// model) when <paramref name="ListenOnGpu"/>; and lip-sync by Audio2Face on the card when <paramref name="LipSyncOnGpu"/>,
/// otherwise by the voice's loudness.</summary>
internal sealed record DefaultSetupPlan(LocalChatModel Thinking, bool ThinkingOnGpu, SpeechEngine? Voice, bool ListenOnGpu,
    ListeningAdvice Listening, string ParakeetModel, string Gpu, bool LipSyncOnGpu = false)
{
    /// <summary>One line per job, as the confirmation shows them.</summary>
    internal string Describe(bool thinking = true, bool listening = true, bool voice = true)
    {
        var lines = new List<string>();
        if (thinking)
            lines.Add($"Thinking: {Thinking.Id} in Ollama on this PC ({Thinking.Size}{(ThinkingOnGpu ? ", on the graphics card" : ", on the processor")}). " +
                (Thinking.Hears ? "It's the fastest model that also hears your voice." : "It's the fastest model that fits."));
        if (voice)
            lines.Add(Voice is { } engine
                ? $"Voice: {engine.Name} on the graphics card. Martlet speaks with a Windows voice until it's ready."
                : "Voice: a Windows voice on the processor, since the graphics card has no room for a voice engine.");
        if (listening)
            lines.Add(ListenOnGpu
                ? $"Listening: your Windows default microphone, with Whisper ({Listening.GpuModel}) on the graphics card. Parakeet on the processor listens until it's ready."
                : $"Listening: your Windows default microphone, with Parakeet on the processor{(Voice is null ? "" : ", leaving the graphics card to the voice")}.");
        return string.Join("\n", lines);
    }
}

internal static class DefaultSetup
{
    /// <summary>The PC the user talks to, as the placement engine names it.</summary>
    internal const string ThisPc = "this-pc";

    /// <summary>What the first-run setup plans: the conversation and the character. Deep thinking, singing and pictures are
    /// added later, each from its own page.</summary>
    internal static readonly PlanComponent[] Conversation =
        [PlanComponent.Thinking, PlanComponent.Voice, PlanComponent.Listening, PlanComponent.Character, PlanComponent.LipSync];

    /// <summary>The smallest suggested model that hears your recording: the omni model Martlet starts with.</summary>
    internal static LocalChatModel SmallestHearingModel =>
        MainWindow.LocalChatModels.Where(m => m.Hears).MinBy(m => ListeningAdvisor.OllamaModelGb(m.Id))!;

    /// <summary>This PC for the placement engine: its NVIDIA cards as nvidia-smi reports them now (memory in use counts), or
    /// the card Windows reports when nvidia-smi doesn't answer, plus memory and processor threads.</summary>
    internal static MachineSpecs Specs(IReadOnlyList<GpuNow> gpus, GpuInfo? windowsGpu, double? ramGb, int threads)
    {
        IReadOnlyList<MachineGpu> cards = gpus.Count > 0
            ? [.. gpus.Select(g => new MachineGpu(g.Name, GpuVendor.Nvidia, g.TotalGb) { UsedGb = g.UsedGb })]
            : windowsGpu is { MemoryGb: > 0 } windows ? [new MachineGpu(windows.Name, MachineGpu.VendorOf(windows.Name), windows.MemoryGb.Value)]
            : [];
        return MachineSpecs.ThisPc(cards, ramGb ?? 16, threads);
    }

    internal static MachineSpecs Specs(MachineInfo machine, IReadOnlyList<GpuNow> gpus) =>
        Specs(gpus, machine.BestGpu, machine.MemoryGb, machine.Threads);

    /// <summary>Plans the default setup for this PC with the placement engine. <paramref name="thinkingGb"/> is the graphics
    /// memory Thinking takes here: null plans Thinking too; 0 means Thinking runs elsewhere (a cloud provider, another
    /// computer); more means the local model already set up keeps that much of the card.</summary>
    internal static DefaultSetupPlan Plan(IReadOnlyList<GpuNow> gpus, GpuInfo? windowsGpu, int threads, CultureInfo language,
        double? thinkingGb = null, double? ramGb = null)
    {
        var specs = Specs(gpus, windowsGpu, ramGb, threads);
        if (thinkingGb is > 0 && specs.Gpus.Count > 0)
            specs = specs with { Gpus = [specs.Gpus[0] with { UsedGb = specs.Gpus[0].UsedGb + thinkingGb.Value }, .. specs.Gpus.Skip(1)] };
        var wanted = thinkingGb is null ? Conversation : Conversation.Where(c => c != PlanComponent.Thinking).ToArray();
        var plan = PlacementEngine.Plan(new PlanRequest([specs]) { Preference = HostingPreference.PreferLocal, Wanted = wanted }, Catalog(gpus));
        return FromPlacement(plan, gpus, windowsGpu, threads, language);
    }

    /// <summary>What the first-run setup can set up by itself: the default voice engine (the others need a voice recording
    /// or a licence choice first, in Companion › Voice) or a Windows voice, Parakeet or Whisper on the graphics card for
    /// listening, and on this PC jobs that need an NVIDIA card only when nvidia-smi answers (Martlet checks the driver through
    /// it), with Whisper only on a driver for CUDA 13. <paramref name="gpus"/> null: another computer's jobs, no driver check.</summary>
    internal static FootprintCatalog Catalog(IReadOnlyList<GpuNow>? gpus)
    {
        var card = gpus?.MaxBy(g => g.TotalGb);
        var oldDriver = card?.DriverMajor is < ListeningAdvisor.MinimumDriver;
        var defaultVoice = FootprintCatalog.Default.For(PlanComponent.Voice)
            .FirstOrDefault(o => o.HostRoleKind == SpeechEngines.Default.HostRoleKind)?.Id;
        return new(FootprintCatalog.Default.Options.Where(o =>
            !(o.IsLocal && o.Component == PlanComponent.Voice && o.UsesGpu && o.Id != defaultVoice) &&
            !(o.IsLocal && o.Component == PlanComponent.Listening && o.HostRoleKind is not null && !o.UsesGpu) &&
            !(gpus is not null && o.IsLocal && o.Gpu == GpuRequirement.Nvidia && (card is null || oldDriver && o.Component == PlanComponent.Listening))));
    }

    /// <summary>The setup steps for what <paramref name="plan"/> places on this PC. Whisper on the card shares the voice
    /// engine's host service and needs an NVIDIA driver for CUDA 13, so it runs only beside a voice engine on a current driver.</summary>
    internal static DefaultSetupPlan FromPlacement(PlacementPlan plan, IReadOnlyList<GpuNow> gpus, GpuInfo? windowsGpu, int threads,
        CultureInfo language)
    {
        var thinking = plan.Primary(PlanComponent.Thinking) is { MachineId: ThisPc, Option: { } local }
            ? MainWindow.LocalChatModels.FirstOrDefault(m => m.Id == local.ModelId) is { } model ? (model, local.UsesGpu) : (SmallestHearingModel, local.UsesGpu)
            : (SmallestHearingModel, false);
        var voice = plan.Primary(PlanComponent.Voice) is { MachineId: ThisPc, Option.HostRoleKind: { } role }
            ? SpeechEngines.All.FirstOrDefault(e => e.HostRoleKind == role) : null;
        var card = gpus.MaxBy(g => g.TotalGb);
        var listening = ListeningAdvisor.Advise(gpus, windowsGpu, [], threads, null);
        var whisper = plan.Primary(PlanComponent.Listening) is { MachineId: ThisPc, Option: { UsesGpu: true } stt } ? stt : null;
        var listenOnGpu = whisper is not null && voice is not null && card is not null && card.DriverMajor is not < ListeningAdvisor.MinimumDriver;
        if (listenOnGpu) listening = listening with { UseGpu = true, GpuModel = whisper!.ModelId ?? listening.GpuModel };
        var lipSync = plan.Primary(PlanComponent.LipSync) is { MachineId: ThisPc, Option.UsesGpu: true };
        var gpu = card is not null ? $"{card.Name} ({card.TotalGb.ToString("0.#", CultureInfo.InvariantCulture)} GB)"
            : windowsGpu?.Describe() ?? "no dedicated graphics card";
        return new(thinking.Item1, thinking.Item2, voice, listenOnGpu, listening, LocalSpeechSetup.RecommendedParakeetModel(language), gpu, lipSync);
    }

    /// <summary>The hosted providers (planner provider ids: the preset ids "nvidia-build", "openrouter", "google-gemini", and
    /// "openai") with a saved key: the Thinking route's, and the If Thinking fails fallback's (its own key, or the Thinking
    /// route's when it borrows it).</summary>
    internal static IReadOnlyCollection<string> ConfiguredProviders(SetupRoute? thinking, ThinkingFallbackSettings? fallback)
    {
        var providers = new List<string>();
        if (thinking?.CredentialId is not null)
        {
            if (thinking.RouteType == SetupRouteType.OpenAi) providers.Add("openai");
            else if (ChatCompletionsEndpointCatalog.Named(thinking.Origin)?.Id is { } id) providers.Add(id);
        }
        if (fallback is not null && (fallback.CredentialId is not null || fallback.UsesThinkingKey(thinking)) &&
            ChatCompletionsEndpointCatalog.Named(fallback.Origin)?.Id is { } fallbackId)
            providers.Add(fallbackId);
        return providers.Distinct(StringComparer.Ordinal).ToArray();
    }

    /// <summary>The welcome wizard's suggestion for this PC from the placement engine, with the owner's preference: "keep
    /// everything on my computers" plans nothing hosted; "free online services are fine" lets Thinking go to NVIDIA Build so
    /// this PC's card goes to the voice and face. <paramref name="network"/>, when this PC joined a Martlet network, adds the
    /// network's machines and current setup, and the suggestion becomes what this PC should take on.</summary>
    internal static WelcomePlan Recommend(MachineSpecs specs, IReadOnlyList<GpuNow> gpus, GpuInfo? windowsGpu, CultureInfo language,
        HostingPreference preference, IReadOnlyCollection<string>? configuredProviders = null, PlanRequest? network = null)
    {
        var machines = network is null ? [specs] : (IReadOnlyList<MachineSpecs>)[.. network.Machines.Where(m => m.Id != specs.Id), specs];
        var request = (network ?? new PlanRequest(machines)) with
        {
            Machines = machines, Preference = preference, ConfiguredProviders = configuredProviders ?? [], Wanted = Conversation
        };
        // Alone, this PC plans only what it can set up; in a network the hosts' NVIDIA jobs aren't checked against this PC's driver.
        var catalog = Catalog(network is null ? gpus : null);
        var plan = PlacementEngine.Plan(request, catalog);
        var joining = network is null ? []
            : PlacementEngine.SuggestForJoiningMachine(request with { Machines = [.. machines.Where(m => m.Id != specs.Id)] }, specs, catalog);
        return new(specs, preference, plan, FromPlacement(plan, gpus, windowsGpu, specs.CpuThreads, language), joining);
    }
}

/// <summary>The welcome wizard's suggestion: the engine's plan for this PC (and its network when joining), the setup steps
/// that apply it, and, when joining, what changes because this PC joined.</summary>
internal sealed record WelcomePlan(MachineSpecs Specs, HostingPreference Preference, PlacementPlan Placement, DefaultSetupPlan Setup,
    IReadOnlyList<PlanSuggestion> Joining)
{
    /// <summary>The jobs the wizard shows, in rank order.</summary>
    internal static readonly PlanComponent[] Shown = [PlanComponent.Thinking, PlanComponent.Voice, PlanComponent.Listening, PlanComponent.LipSync];

    internal bool ThinkingOnline => Placement.Primary(PlanComponent.Thinking) is { IsExternal: true };

    internal Assignment? ThinkingHosted => Placement.Primary(PlanComponent.Thinking) is { IsExternal: true } hosted ? hosted : null;

    internal MachineUsage? Here => Placement.Usage(Specs.Id);

    /// <summary>A component's share of this PC in percent: graphics memory (of the card it is on), memory and processor.</summary>
    /// <summary>What the plan's primary parts use here (a backup Thinking model is shown but not set up).</summary>
    private IEnumerable<UsageItem> Primary(MachineUsage here) =>
        here.Items.Where(i => Placement.Primary(i.Component) is { MachineId: { } id } a && id == Specs.Id && a.Option.Id == i.OptionId);

    internal (int Vram, int Ram, int Cpu) Share(PlanComponent component)
    {
        if (Here is not { } here) return (0, 0, 0);
        var items = Primary(here).Where(i => i.Component == component).ToList();
        int Percent(double used, double capacity) => capacity <= 0 ? 0 : (int)Math.Round(used / capacity * 100);
        var vram = items.Where(i => i.GpuIndex is not null).Sum(i => Percent(i.Use.VramGb, here.Gpus[i.GpuIndex!.Value].TotalGb));
        return (vram, Percent(items.Sum(i => i.Use.RamGb), Specs.RamGb), Percent(items.Sum(i => i.Use.CpuThreads), Specs.CpuThreads));
    }

    /// <summary>Everything the plan puts on this PC, as shares of the whole card, memory and processor.</summary>
    internal (int Vram, int Ram, int Cpu) Total()
    {
        if (Here is not { } here) return (0, 0, 0);
        var card = here.Gpus.FirstOrDefault();
        int Percent(double used, double capacity) => capacity <= 0 ? 0 : (int)Math.Round(used / capacity * 100);
        var items = Primary(here).ToList();
        return (card is null ? 0 : Percent(items.Where(i => i.GpuIndex == card.Index).Sum(i => i.Use.VramGb), card.TotalGb),
            Percent(items.Sum(i => i.Use.RamGb), Specs.RamGb), Percent(items.Sum(i => i.Use.CpuThreads), Specs.CpuThreads));
    }

    internal static string Where(Assignment assignment) => assignment.MachineId switch
    {
        null => "online (free)",
        DefaultSetup.ThisPc => "this PC",
        _ => "another of your computers"
    };

    /// <summary>One line per job, as MCP and the log read it: what, where, its share of this PC and why (or why it's left out).</summary>
    internal string Describe(PlanComponent component)
    {
        var name = ComponentRanking.Name(component);
        if (Placement.Primary(component) is not { } assignment)
            return $"{name}: not set up. " + (Placement.Dropped.FirstOrDefault(d => d.Component == component)?.Why ?? "");
        var (vram, ram, cpu) = Share(component);
        var line = $"{name}: {assignment.Option.DisplayName}, {Where(assignment)}. Uses {vram}% graphics memory, {ram}% memory, {cpu}% processor. {assignment.Why}";
        if (Placement.Fallback(component) is { } fallback) line += $" If it's down: {fallback.Option.DisplayName} ({Where(fallback)}).";
        return line;
    }
}

/// <summary>The welcome wizard's answer to "where may Martlet do its thinking?".</summary>
internal static class WelcomePreferences
{
    internal static string Describe(HostingPreference preference) => preference == HostingPreference.PreferLocal
        ? "everything stays on your computers" : "free online services are fine";
}
