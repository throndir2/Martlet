using System.Globalization;
using Martlet.Core.Planning;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>What Set it all up for me sets up on this PC, as the placement engine (<see cref="PlacementEngine"/>) planned it:
/// Thinking in Ollama here (<paramref name="ThinkingOnGpu"/>: on the graphics card, otherwise on the processor) unless it is
/// hosted or set up already; a voice engine on the NVIDIA card when it has room, otherwise Chatterbox Nano on the processor
/// (null <paramref name="Voice"/>: neither fits, and Martlet can't speak until a voice is set up); listening on the
/// Windows default microphone with Parakeet on the processor, or Whisper on the card (<paramref name="Listening"/>'s GPU
/// model) when <paramref name="ListenOnGpu"/>; and lip-sync by Audio2Face on the card when <paramref name="LipSyncOnGpu"/>,
/// otherwise by the voice's loudness.</summary>
internal sealed record DefaultSetupPlan(LocalChatModel Thinking, bool ThinkingOnGpu, SpeechEngine? Voice, bool ListenOnGpu,
    ListeningAdvice Listening, string ParakeetModel, string Gpu, bool LipSyncOnGpu = false, bool VoiceOnGpu = true)
{
    /// <summary>What the confirmation and the status say when no voice fits this PC.</summary>
    internal const string NoVoice = "Voice: not set up. This PC has no room for a voice engine: Chatterbox Nano needs an NVIDIA " +
        "graphics card with 4 GB or more, or about 8 free processor threads and 4.5 GB of free memory. Free some room, or set up " +
        "a voice on another computer in Companion › Voice.";

    /// <summary>A chat model one of the owner's model apps already serves on this PC, which Thinking uses instead of
    /// <see cref="Thinking"/> (Use models your apps already run); null: Thinking uses Martlet's Ollama model.</summary>
    internal ServedModel? Served { get; init; }

    /// <summary>One line per job, as the confirmation shows them.</summary>
    internal string Describe(bool thinking = true, bool listening = true, bool voice = true)
    {
        var lines = new List<string>();
        if (thinking && Served is { } served)
            lines.Add($"Thinking: {served.ModelId} in {served.AppName} on this PC (about " +
                $"{ServedModels.Gb(served)?.ToString("0.#", CultureInfo.InvariantCulture) ?? "?"} GB on the graphics card). " +
                "You already run it there, so nothing downloads. Its first word may come later than with a small model.");
        else if (thinking)
            lines.Add($"Thinking: {Thinking.Id} in Ollama on this PC ({Thinking.Size}{(ThinkingOnGpu ? ", on the graphics card" : ", on the processor")}). " +
                (Thinking.Hears ? "It's the fastest model that also hears your voice." : "It's the fastest model that fits."));
        if (voice)
            lines.Add(Voice is not { } engine ? NoVoice
                : VoiceOnGpu ? $"Voice: {engine.Name} on the graphics card. Martlet speaks once it's ready."
                : $"Voice: {engine.Name} on the processor, since the graphics card has no room for a voice engine. " +
                  "Its first word comes about 1.5 seconds after Thinking's. Martlet speaks once it's ready.");
        if (listening)
            lines.Add(ListenOnGpu
                ? $"Listening: your Windows default microphone, with Whisper ({Listening.GpuModel}) on the graphics card. Parakeet on the processor listens until it's ready."
                : $"Listening: your Windows default microphone, with Parakeet on the processor{(Voice is null || !VoiceOnGpu ? "" : ", leaving the graphics card to the voice")}.");
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
        double? thinkingGb = null, double? ramGb = null, RecommendationPreferences? preferences = null, bool games = false)
    {
        var specs = Specs(gpus, windowsGpu, ramGb, threads) with { KeepGpuForGames = games };
        if (thinkingGb is > 0 && specs.Gpus.Count > 0)
            specs = specs with { Gpus = [specs.Gpus[0] with { UsedGb = specs.Gpus[0].UsedGb + thinkingGb.Value }, .. specs.Gpus.Skip(1)] };
        var wanted = thinkingGb is null ? Conversation : Conversation.Where(c => c != PlanComponent.Thinking).ToArray();
        var plan = PlacementEngine.Plan(new PlanRequest([specs])
        {
            Preference = HostingPreference.PreferLocal, Wanted = wanted, Quality = preferences?.Quality, PreferHearing = preferences?.PreferHearing ?? true
        }, Catalog(gpus));
        return FromPlacement(plan, gpus, windowsGpu, threads, language);
    }

    /// <summary>Use models your apps already run, on a PC alone: Thinking uses the best chat model in <paramref name="served"/>
    /// (the biggest first) that fits the graphics card beside the voice <paramref name="plan"/> places, so the voice doesn't move
    /// off the card or go away. Martlet's own Ollama models stay its own; with no graphics card, or none that fits,
    /// <paramref name="plan"/> stays as it is.</summary>
    internal static DefaultSetupPlan WithServed(DefaultSetupPlan plan, IEnumerable<ServedModel>? served, IReadOnlyList<GpuNow> gpus,
        GpuInfo? windowsGpu, int threads, CultureInfo language, double? ramGb = null)
    {
        var card = Specs(gpus, windowsGpu, ramGb, threads).Gpus.FirstOrDefault();
        if (card is null) return plan;
        var candidates = ServedModels.Usable((served ?? []).Select(m => m with { MachineId = ThisPc }), PlanningCatalog.Current)
            .Select(m => (Model: m, Option: ServedModels.Option(m, PlanningCatalog.Models)))
            .Where(x => x.Option.UsesGpu && x.Option.GpuGb > 0 && x.Option.GpuGb <= card.VramGb - card.UsedGb)
            .OrderByDescending(x => x.Option.QualityTier).ThenByDescending(x => x.Option.GpuGb).ThenBy(x => x.Model.ModelId, StringComparer.Ordinal);
        foreach (var (model, option) in candidates)
        {
            var withIt = Plan(gpus, windowsGpu, threads, language, option.GpuGb, ramGb);
            if (plan.Voice is not null && (withIt.Voice is null || plan.VoiceOnGpu && !withIt.VoiceOnGpu)) continue;
            return withIt with { Thinking = plan.Thinking, ThinkingOnGpu = true, Served = model };
        }
        return plan;
    }

    /// <summary>What the first-run setup can set up by itself: the default voice engine (the others need a voice recording
    /// or a licence choice first, in Companion › Voice) or Chatterbox Nano, on the card or the processor, Parakeet or Whisper on the graphics card for
    /// listening, and on this PC jobs that need an NVIDIA card only when nvidia-smi answers (Martlet checks the driver through
    /// it), with Whisper only on a driver for CUDA 13. <paramref name="gpus"/> null: another computer's jobs, no driver check.</summary>
    internal static FootprintCatalog Catalog(IReadOnlyList<GpuNow>? gpus)
    {
        var card = gpus?.MaxBy(g => g.TotalGb);
        var oldDriver = card?.DriverMajor is < ListeningAdvisor.MinimumDriver;
        // Of the voice engines only the default and the fallback (Chatterbox Nano, on the card or the processor) are set up here.
        return new(PlanningCatalog.Current.Options.Where(o =>
            !(o.IsLocal && o.Component == PlanComponent.Voice && o.HostRoleKind is not null &&
              o.HostRoleKind != SpeechEngines.Default.HostRoleKind && o.HostRoleKind != FootprintCatalog.FallbackVoiceKind) &&
            !(o.IsLocal && o.Component == PlanComponent.Listening && o.HostRoleKind is not null && !o.UsesGpu) &&
            !(gpus is not null && o.IsLocal && o.Gpu == GpuRequirement.Nvidia && (card is null || oldDriver && o.Component == PlanComponent.Listening))));
    }

    /// <summary>The Ollama model Set it all up for me installs for Thinking's <paramref name="option"/>: one of Martlet's suggestions,
    /// or a model from the model catalog by its install name (an Ollama tag or hf.co/{repo}:{quant}) with its download size;
    /// else the smallest model that hears.</summary>
    internal static LocalChatModel ChatModel(ComponentOption option)
    {
        ArgumentNullException.ThrowIfNull(option);
        if (MainWindow.LocalChatModels.FirstOrDefault(m => m.Id == option.ModelId) is { } suggested) return suggested;
        if (option.Origin != OptionOrigin.LocalFacts || option.ModelId is not { Length: > 0 } model) return SmallestHearingModel;
        var card = Math.Ceiling(option.GpuGb + 2);
        return new(model, $"{option.Peak.DiskGb.ToString("0.#", CultureInfo.InvariantCulture)} GB",
            $"a graphics card with {card.ToString("0", CultureInfo.InvariantCulture)} GB or more", card, option.HearsAudio);
    }

    /// <summary>The setup steps for what <paramref name="plan"/> places on this PC. Whisper on the card shares the voice
    /// engine's host service and needs an NVIDIA driver for CUDA 13, so it runs only beside a voice engine on a current driver.</summary>
    internal static DefaultSetupPlan FromPlacement(PlacementPlan plan, IReadOnlyList<GpuNow> gpus, GpuInfo? windowsGpu, int threads,
        CultureInfo language)
    {
        var thinking = plan.Primary(PlanComponent.Thinking) is { MachineId: ThisPc, Option: { } local }
            ? (ChatModel(local), local.UsesGpu)
            : (SmallestHearingModel, false);
        var voiceAssignment = plan.Primary(PlanComponent.Voice) is { MachineId: ThisPc, Option.HostRoleKind: not null } placed ? placed : null;
        var voice = voiceAssignment is { Option.HostRoleKind: { } role } ? SpeechEngines.All.FirstOrDefault(e => e.HostRoleKind == role) : null;
        var card = gpus.MaxBy(g => g.TotalGb);
        var listening = ListeningAdvisor.Advise(gpus, windowsGpu, [], threads, null);
        var whisper = plan.Primary(PlanComponent.Listening) is { MachineId: ThisPc, Option: { UsesGpu: true } stt } ? stt : null;
        // Whisper shares the host service the voice engine sets up; Nano on the processor sets it up too.
        var listenOnGpu = whisper is not null && voice is not null && card is not null && card.DriverMajor is not < ListeningAdvisor.MinimumDriver;
        if (listenOnGpu) listening = listening with { UseGpu = true, GpuModel = whisper!.ModelId ?? listening.GpuModel };
        var lipSync = plan.Primary(PlanComponent.LipSync) is { MachineId: ThisPc, Option.UsesGpu: true };
        var gpu = card is not null ? $"{card.Name} ({card.TotalGb.ToString("0.#", CultureInfo.InvariantCulture)} GB)"
            : windowsGpu?.Describe() ?? "no dedicated graphics card";
        return new(thinking.Item1, thinking.Item2, voice, listenOnGpu, listening, LocalSpeechSetup.RecommendedParakeetModel(language), gpu, lipSync,
            voiceAssignment?.Option.UsesGpu ?? true);
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
        HostingPreference preference, IReadOnlyCollection<string>? configuredProviders = null, PlanRequest? network = null,
        RecommendationPreferences? preferences = null, bool games = false)
    {
        specs = specs with { KeepGpuForGames = games };
        var machines = network is null ? [specs] : (IReadOnlyList<MachineSpecs>)[.. network.Machines.Where(m => m.Id != specs.Id), specs];
        var request = (network ?? new PlanRequest(machines)) with
        {
            Machines = machines, Preference = preference, ConfiguredProviders = configuredProviders ?? [], Wanted = Conversation,
            Quality = preferences?.Quality, PreferHearing = preferences?.PreferHearing ?? true,
            HostGpuShare = preferences?.HostGpuFraction ?? PlacementEngine.DefaultGpuShare
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

    /// <summary>The graphics memory a component usually holds, in percent of its card; <see cref="Share"/> is the most it takes
    /// while it works hardest.</summary>
    internal int UsualVram(PlanComponent component)
    {
        if (Here is not { } here) return 0;
        int Percent(double used, double capacity) => capacity <= 0 ? 0 : (int)Math.Round(used / capacity * 100);
        return Primary(here).Where(i => i.Component == component && i.GpuIndex is not null)
            .Sum(i => Percent(Math.Min(i.Usual.VramGb, i.Use.VramGb), here.Gpus[i.GpuIndex!.Value].TotalGb));
    }

    /// <summary>"31-35%" when a component grows while it works, else "35%".</summary>
    internal static string Percents(int usual, int most) => usual < most ? $"{usual}-{most}%" : $"{most}%";

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
        var line = $"{name}: {assignment.Option.DisplayName}, {Where(assignment)}. Uses {Percents(UsualVram(component), vram)} graphics memory, " +
                   $"{ram}% memory, {cpu}% processor. {assignment.Why}";
        if (Placement.Fallback(component) is { } fallback) line += $" If it's down: {fallback.Option.DisplayName} ({Where(fallback)}).";
        return line;
    }
}

/// <summary>The welcome wizard's three answers in words.</summary>
internal static class WelcomePreferences
{
    internal static string Describe(RecommendationPreferences preferences, bool games) =>
        $"{(games ? "you play games on this PC" : "no games on this PC")}, online services {RecommendationPreferences.Words(preferences.Online).ToLowerInvariant()}, " +
        $"{RecommendationPreferences.Words(preferences.Quality).ToLowerInvariant()}" +
        (preferences.Quality == ReplyQuality.Balanced ? " replies" : "");
}
