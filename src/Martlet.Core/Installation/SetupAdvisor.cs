namespace Martlet.Core.Installation;

public enum AdvisorGoal { Balanced, Smartest, Fastest, Private }
/// <summary>GPU answer for one computer. <see cref="Unknown"/> means "has a dedicated GPU, not sure which"; the
/// advisor plans for an 8 GB NVIDIA card and says so.</summary>
public enum AdvisorGpu { None, Unknown, Nvidia4, Nvidia8, Nvidia12, Nvidia16, Nvidia24, Nvidia32Plus, OtherVendor }

/// <summary>Another computer that can run Martlet roles. <paramref name="Name"/> is the paired host ID or the
/// user's label; <paramref name="Detected"/> describes hardware the host reported, when it did.</summary>
public sealed record AdvisorComputer(AdvisorGpu Gpu, string? Name = null, string? Detected = null);
public enum AdvisorAvailability { Available, BeingBuilt, Planned }
public enum AdvisorNextStep { Setup, AudioSetup, Hosts, Character, VoiceLibrary, Prerequisites }
/// <summary>A Windows prerequisite the plan runs on this PC; the Desktop app offers to install the missing ones.</summary>
public enum AdvisorInstall { WindowsSpeech, Ollama, DockerDesktop }

public sealed record AdvisorAnswers
{
    public AdvisorGoal Goal { get; init; } = AdvisorGoal.Balanced;
    public bool VoiceInput { get; init; } = true;
    public bool SpokenReplies { get; init; } = true;
    public bool Character { get; init; }
    public bool CustomVoice { get; init; }
    public AdvisorGpu ThisPcGpu { get; init; } = AdvisorGpu.None;
    /// <summary>The graphics card Martlet read from this PC, shown in the plan while the answer still matches it.</summary>
    public string? ThisPcDetected { get; init; }
    public bool GamesOnThisPc { get; init; }
    /// <summary>Each other computer that can run Martlet roles, in order (Computer 2, Computer 3, ...).
    /// Only the first <see cref="SetupAdvisor.MaxOtherComputers"/> are planned.</summary>
    public IReadOnlyList<AdvisorComputer> OtherComputers { get; init; } = [];
}

public sealed record AdvisorRole(
    string Role, string Choice, string Where, string WhatItDoes, string Why,
    AdvisorAvailability Availability, string? UntilThen, string Data, string? HowTo = null)
{
    public string Status => Availability switch
    {
        AdvisorAvailability.Available => "Available now",
        AdvisorAvailability.BeingBuilt => "Being built",
        _ => "Planned"
    };
}

public sealed record AdvisorMachine(string Name, string Hardware, IReadOnlyList<string> Runs);

public sealed record SetupAdvice(
    string Title, string Summary, IReadOnlyList<AdvisorRole> Roles, IReadOnlyList<AdvisorMachine> Machines,
    IReadOnlyList<string> Notes, IReadOnlyList<AdvisorNextStep> NextSteps)
{
    /// <summary>Windows prerequisites this plan runs on this PC, in install order.</summary>
    public IReadOnlyList<AdvisorInstall> ThisPcInstalls { get; init; } = [];
}

/// <summary>The setup advisor's goal-and-GPU questionnaire, answered by the placement engine
/// (<see cref="Planning.PlacementEngine"/>, the single source of truth for what runs where; docs/RECOMMENDATIONS.md). This
/// class only turns answers into machines and a preference, and the engine's plan into the advisor's words. It saves,
/// probes and contacts nothing; sizes are planning estimates (docs/RESOURCE_FOOTPRINTS.md).</summary>
public static class SetupAdvisor
{
    private const string LlmWhat = "Writes each reply. Where it runs affects speed, privacy and cost.";
    private const string SttWhat = "Turns your speech into text.";
    private const string TtsWhat = "Reads replies aloud.";
    private const string FaceWhat = "Moves the character's mouth and face with the voice.";
    private const string CharacterWhat = "Shows your desktop character.";
    private const string Local = "Stays on your computers.";
    private const string LocalLlmHow = "Install Ollama or LM Studio, download a model, then choose it in Companion > Thinking.";
    private const string HostedLlmHow = "Choose an online provider in Companion > Thinking, pick a model and save its API key.";
    private const string OpenAiHow = "Choose OpenAI in Setup and save your API key.";
    private const string ThisPcId = "this-pc";
    /// <summary>The advisor asks about graphics cards only; it plans memory and processors as a typical desktop.</summary>
    private const double AssumedRamGb = 32;
    private const int AssumedThreads = 16;

    public const int MaxOtherComputers = 5;

    /// <summary>The engine's request for these answers: this PC and each other computer by its GPU answer, the goal as a
    /// hosting preference (Private keeps everything local, Smartest is happy with hosted endpoints, Fastest puts local
    /// Thinking first) and the parts the user wants.</summary>
    public static Planning.PlanRequest ToPlanRequest(AdvisorAnswers answers)
    {
        ArgumentNullException.ThrowIfNull(answers);
        var machines = new List<Planning.MachineSpecs>
        {
            new(ThisPcId, "This PC")
            {
                Gpus = Gpus(answers.ThisPcGpu), RamGb = AssumedRamGb, CpuThreads = AssumedThreads, IsPrimary = true,
                KeepGpuForGames = answers.GamesOnThisPc
            }
        };
        machines.AddRange((answers.OtherComputers ?? []).Take(MaxOtherComputers).Select((computer, i) =>
            new Planning.MachineSpecs($"computer-{i + 2}", ComputerName(computer, i))
            {
                Gpus = Gpus(computer.Gpu), RamGb = AssumedRamGb, CpuThreads = AssumedThreads, Platform = "linux"
            }));
        var wanted = new List<Planning.PlanComponent> { Planning.PlanComponent.Thinking };
        if (answers.SpokenReplies) wanted.Add(Planning.PlanComponent.Voice);
        if (answers.VoiceInput) wanted.Add(Planning.PlanComponent.Listening);
        if (answers.Character) wanted.Add(Planning.PlanComponent.Character);
        if (answers.Character && answers.SpokenReplies) wanted.Add(Planning.PlanComponent.LipSync);
        return new(machines)
        {
            Preference = answers.Goal switch
            {
                AdvisorGoal.Private => Planning.HostingPreference.PreferLocal,
                AdvisorGoal.Smartest => Planning.HostingPreference.PreferHosted,
                _ => Planning.HostingPreference.Balanced
            },
            ThinkingFirst = answers.Goal == AdvisorGoal.Fastest,
            Wanted = wanted
        };
    }

    public static SetupAdvice Recommend(AdvisorAnswers answers)
    {
        ArgumentNullException.ThrowIfNull(answers);
        var goal = answers.Goal;
        var request = ToPlanRequest(answers);
        var plan = Planning.PlacementEngine.Plan(request);
        var others = (answers.OtherComputers ?? []).Take(MaxOtherComputers).ToList();
        var names = request.Machines.ToDictionary(m => m.Id, m => m.Name);
        var runs = request.Machines.ToDictionary(m => m.Id, _ => new List<string>());
        var roles = new List<AdvisorRole>();
        var notes = new List<string>();
        var online = new SortedSet<string>(StringComparer.Ordinal);

        string Where(Planning.Assignment a) => a.MachineId is null ? "Online"
            : $"{names[a.MachineId]} ({(a.GpuIndex is null ? "CPU" : "GPU")})";
        bool OnPc(Planning.Assignment? a) => a?.MachineId == ThisPcId;

        // Thinking.
        var thinking = plan.Primary(Planning.PlanComponent.Thinking);
        var fallback = plan.Fallback(Planning.PlanComponent.Thinking);
        if (thinking is { IsExternal: false } local)
        {
            var size = Size(local.Option);
            runs[local.MachineId!].Add($"Thinking model ({size}{(local.GpuIndex is null ? ", CPU" : "")})");
            var why = goal == AdvisorGoal.Fastest
                ? $"A small local model starts replies soonest ({local.Option.DisplayName}: about " +
                  $"{Planning.PlacementEngine.Seconds(local.Option.FirstWordMs ?? 0)} to its first sentence)" +
                  (local.Option.HearsAudio ? " and hears your voice" : "") + ". Bigger models are smarter but slower."
                : local.GpuIndex is null
                    ? "Keeps everything local, but replies will be slow without a GPU."
                    : "Your conversation stays on your computers. Local models are usually less capable than large online ones.";
            roles.Add(new("Thinking model", $"A {size} local model ({local.Option.DisplayName})", Where(local), LlmWhat, why,
                AdvisorAvailability.Available, null, Local,
                OnPc(local) ? LocalLlmHow : $"In Devices, add the Thinking role to {names[local.MachineId!]}, then choose it in Companion > Thinking."));
            if (local.GpuIndex is null)
                notes.Add("Without a GPU, a fully private setup is slow. A GPU on this PC or another computer makes local replies practical.");
        }
        else if (thinking is not null)
        {
            online.Add(thinking.Option.DisplayName);
            var why = goal switch
            {
                AdvisorGoal.Smartest => "Large online models give the best answers without needing a home GPU.",
                AdvisorGoal.Fastest when answers.GamesOnThisPc && answers.ThisPcGpu != AdvisorGpu.None =>
                    "Your GPU is kept for games. Another computer with a GPU could start local replies sooner.",
                AdvisorGoal.Fastest => "No GPU has room for a local model. An online model is the fastest option for now.",
                _ => thinking.Why
            };
            roles.Add(new("Thinking model", $"{thinking.Option.DisplayName} (a large online model)", "Online", LlmWhat, why,
                AdvisorAvailability.Available, null,
                "Your messages and recent conversation go to the provider." + (thinking.Option.FreeTier ? " It's free, with limits." : " Each request may cost money."),
                HostedLlmHow));
        }
        if (fallback is not null)
        {
            if (fallback.MachineId is { } id) runs[id].Add($"Backup thinking model ({Size(fallback.Option)}{(fallback.GpuIndex is null ? ", CPU" : "")})");
            notes.Add(fallback.Why);
        }

        // Listening.
        var listening = plan.Primary(Planning.PlanComponent.Listening);
        if (listening is not null)
        {
            if (listening.IsExternal)
            {
                online.Add("OpenAI");
                roles.Add(new("Speech-to-text", "OpenAI transcription", "Online", SttWhat, "The most accurate option Martlet supports.",
                    AdvisorAvailability.Available, null, "Your recorded push-to-talk audio goes to OpenAI. Each request may cost money.", OpenAiHow));
            }
            else if (listening.GpuIndex is not null)
            {
                runs[listening.MachineId!].Add("Speech-to-text (GPU)");
                roles.Add(new("Speech-to-text", "Whisper speech recognition on a GPU", Where(listening), SttWhat,
                    "A graphics card had room, and it takes the work off the processor. Your audio stays on your network.",
                    AdvisorAvailability.Available, null, Local,
                    OnPc(listening) ? "In Companion > Listening, choose Whisper on the graphics card."
                        : $"In Devices, add the Listening role to {names[listening.MachineId!]}."));
            }
            else
            {
                runs[listening.MachineId!].Add("Speech-to-text (CPU)");
                roles.Add(new("Speech-to-text", "Parakeet speech recognition", Where(listening), SttWhat,
                    goal == AdvisorGoal.Fastest
                        ? "Recognizes a short sentence in about 0.1-0.3 s on the processor, with no internet delay, and leaves the GPU to the " +
                          "thinking model and the voice."
                        : "Accurate, runs on the processor, and keeps your microphone audio on this PC.",
                    AdvisorAvailability.Available, null, "Stays on this PC.",
                    "In Companion > Listening, choose Parakeet in Martlet. It downloads once."));
            }
        }

        // Voice.
        var voice = plan.Primary(Planning.PlanComponent.Voice);
        var windowsSpeech = false;
        var gpuVoice = voice is { IsExternal: false, GpuIndex: not null };
        if (voice is not null)
        {
            var engine = Settings.SpeechEngines.All.FirstOrDefault(e => e.HostRoleKind == voice.Option.HostRoleKind) ?? Settings.SpeechEngines.Default;
            if (gpuVoice)
            {
                runs[voice.MachineId!].Add(answers.CustomVoice ? "Voice (your custom voice)" : "Voice (natural local voice)");
                roles.Add(new("Voice (text-to-speech)",
                    answers.CustomVoice
                        ? $"Your own voice with {engine.Name} (other engines selectable)"
                        : $"A natural local voice with {engine.Name}",
                    Where(voice), TtsWhat,
                    answers.CustomVoice ? "Cloning a voice needs a self-hosted GPU engine."
                        : goal == AdvisorGoal.Fastest ? $"It streams its first audio in about {Planning.PlacementEngine.Seconds(voice.Option.FirstWordMs ?? 400)}, in a cloned voice."
                        : "Natural speech without sending reply text anywhere. Only a local voice can't be offloaded for free, so it gets the GPU first.",
                    AdvisorAvailability.Available, null, Local,
                    (OnPc(voice)
                        ? $"In Companion > Voice > Voice engine, choose {engine.Name} on this PC. It sets up in Docker Desktop."
                        : $"Add {names[voice.MachineId!]} in Devices, then choose {engine.Name} for it in Companion > Voice > Voice engine.") +
                    (answers.CustomVoice ? " Add voice recordings and transcripts in Companion > Voice > Voices." : "")));
            }
            else if (voice.IsExternal)
            {
                online.Add("OpenAI");
                roles.Add(new("Voice (text-to-speech)", "OpenAI voice", "Online", TtsWhat, "A natural voice with no local GPU needed.",
                    AdvisorAvailability.Available, null, "Reply text goes to OpenAI. Each request may cost money.", OpenAiHow));
            }
            else
            {
                runs[voice.MachineId!].Add("Voice (Windows voices)");
                windowsSpeech = true;
                roles.Add(new("Voice (text-to-speech)", "Windows installed voices", Where(voice), TtsWhat,
                    goal == AdvisorGoal.Fastest
                        ? "Starts speaking almost instantly but sounds robotic. An NVIDIA GPU with room would allow a natural local voice."
                        : "Free and offline, but sounds robotic. An NVIDIA GPU with more free memory would allow a natural local voice.",
                    AdvisorAvailability.Available, null, "Stays on this PC.",
                    "In Companion > Voice > Voice engine, choose Windows voices on this PC."));
            }
        }
        if (answers.SpokenReplies && answers.CustomVoice && !gpuVoice)
            notes.Add("A custom voice needs an NVIDIA GPU with room for a voice engine during conversations. Until then, use a built-in voice.");

        // Character and lip-sync.
        var face = plan.Primary(Planning.PlanComponent.LipSync);
        if (answers.Character)
        {
            runs[ThisPcId].Add("Character (Live2D/VRM)");
            if (face is { GpuIndex: not null })
            {
                runs[face.MachineId!].Add("Advanced lip-sync");
                roles.Add(new("Lip-sync", "Advanced face animation", Where(face), FaceWhat,
                    "Rich mouth and face animation. It runs beside playback, so it never delays replies.",
                    AdvisorAvailability.Available, null,
                    OnPc(face) ? Local : "Martlet's generated voice goes to that computer only.",
                    OnPc(face) ? "In Martlet hosts, choose This PC, then add the lip-sync role."
                        : $"In Martlet hosts, set up {names[face.MachineId!]}, add the lip-sync role and pair this PC."));
                notes.Add("Advanced lip-sync needs a free NVIDIA account and has not been verified on a real GPU yet.");
            }
            else if (face is not null)
            {
                runs[ThisPcId].Add("Lip-sync (loudness)");
                var reason = answers.GamesOnThisPc && others.All(o => o.Gpu == AdvisorGpu.None) ? "Your GPU is kept for games."
                    : "No NVIDIA GPU has room for advanced lip-sync.";
                roles.Add(new("Lip-sync", "Loudness lip-sync", "This PC (CPU)", FaceWhat,
                    $"{reason} Loudness moves the mouth with the voice's volume; advanced lip-sync is more expressive.",
                    AdvisorAvailability.Available, null, "Stays on this PC.",
                    "In Character settings, leave lip-sync on Auto or choose Loudness."));
            }
            roles.Add(new("Character", "Live2D or VRM character", "This PC", CharacterWhat,
                "It is drawn where you see it and needs very little GPU.", AdvisorAvailability.Available, null, "Stays on this PC.",
                "Press Show character on the home screen, or pick your own model in Character settings."));
        }

        // Machines and spare capacity.
        var pcRuns = runs[ThisPcId];
        pcRuns.Insert(0, "Martlet app" + (answers.VoiceInput || answers.SpokenReplies ? ", microphone and speakers" : ""));
        var machines = new List<AdvisorMachine>
        {
            new("This PC", (answers.ThisPcDetected ?? GpuName(answers.ThisPcGpu)) +
                (answers.GamesOnThisPc && answers.ThisPcGpu != AdvisorGpu.None ? " (kept for games)" : ""), pcRuns.ToArray())
        };
        var spare = 0;
        for (var i = 0; i < others.Count; i++)
        {
            var id = $"computer-{i + 2}";
            var hostRuns = runs[id];
            if (hostRuns.Count == 0 && others[i].Gpu == AdvisorGpu.None)
                hostRuns.Add("Not needed for now: without a GPU it adds little over this PC.");
            else if (hostRuns.Count == 0)
            {
                spare++;
                hostRuns.Add(spare == 1 ? "Spare: later, deep thinking, screen understanding (vision) or memory"
                    : "Spare: later, singing, pictures or Voice Studio training");
            }
            machines.Add(new(names[id], others[i].Detected ?? GpuName(others[i].Gpu), hostRuns.ToArray()));
        }

        // Notes: the engine's (minus its size disclaimer), its upgrade ideas, then the advisor's own.
        notes.InsertRange(0, plan.Notes.Where(n => !n.StartsWith("Sizes are planning estimates", StringComparison.Ordinal)));
        notes.AddRange(plan.Suggestions.Where(s => s.Kind == Planning.SuggestionKind.Upgrade && s.Component == Planning.PlanComponent.Thinking)
            .Select(s => s.Why));
        notes.AddRange(plan.Suggestions.Where(s => s.Kind == Planning.SuggestionKind.SignUp).Select(s => s.Why));
        var gpuHosts = others.Count(o => o.Gpu != AdvisorGpu.None);
        if (others.Count > 0 && gpuHosts == 0)
            notes.Add("Your other computers have no GPU, so they add little. Martlet keeps its work on this PC or online.");
        var unsure = new List<string>();
        if (answers.ThisPcGpu == AdvisorGpu.Unknown) unsure.Add("this PC");
        unsure.AddRange(others.Select((o, i) => (o, i)).Where(p => p.o.Gpu == AdvisorGpu.Unknown).Select(p => ComputerName(p.o, p.i)));
        if (unsure.Count > 0)
            notes.Add($"You were not sure which GPU is in {JoinAnd(unsure)}, so Martlet planned for an 8 GB NVIDIA card. " +
                "Check the exact GPU later for a better fit.");
        if (answers.GamesOnThisPc && !notes.Any(n => n.Contains("slow games", StringComparison.Ordinal)))
            notes.Add("Your games keep this PC's GPU. Martlet only runs the app, audio and character here.");
        notes.Add(online.Count == 0
            ? "Nothing leaves your computers."
            : $"Online services: {string.Join(", ", online)}. The data listed for each role goes to them, and paid ones may cost money.");
        notes.Add("Model sizes and GPU memory are rough estimates.");

        var installs = new List<AdvisorInstall>();
        if (windowsSpeech && goal == AdvisorGoal.Private) installs.Add(AdvisorInstall.WindowsSpeech);
        if (plan.On(ThisPcId).Any(a => a.Component == Planning.PlanComponent.Thinking)) installs.Add(AdvisorInstall.Ollama);
        var dockerHere = plan.On(ThisPcId).Any(a => a.GpuIndex is not null && a.Component != Planning.PlanComponent.Thinking);
        if (dockerHere) installs.Add(AdvisorInstall.DockerDesktop);

        var steps = new List<AdvisorNextStep>();
        if (installs.Count > 0) steps.Add(AdvisorNextStep.Prerequisites);
        steps.Add(AdvisorNextStep.Setup);
        if (answers.VoiceInput || answers.SpokenReplies) steps.Add(AdvisorNextStep.AudioSetup);
        if (plan.Assignments.Any(a => a.MachineId is { } m && m != ThisPcId) || dockerHere) steps.Add(AdvisorNextStep.Hosts);
        if (answers.CustomVoice) steps.Add(AdvisorNextStep.VoiceLibrary);
        if (answers.Character) steps.Add(AdvisorNextStep.Character);

        var computers = 1 + gpuHosts;
        var title = $"{GoalName(goal)}: {computers} computer{(computers == 1 ? "" : "s")}";
        var summary = goal switch
        {
            AdvisorGoal.Smartest => "The largest online model for the best answers. Local hardware goes to voice and face.",
            AdvisorGoal.Fastest => "A small thinking model that answers quickly, a natural voice on the GPU when it fits and speech recognized on the processor.",
            AdvisorGoal.Private => "Everything runs on your own computers.",
            _ => "Your GPU goes first to the voice and face, then to a local thinking model; a free online model fills in."
        };
        return new(title, summary, roles.ToArray(), machines.ToArray(), notes.Distinct().ToArray(), steps.ToArray()) { ThisPcInstalls = installs.ToArray() };
    }

    public static string GoalName(AdvisorGoal goal) => goal switch
    {
        AdvisorGoal.Smartest => "Smartest answers",
        AdvisorGoal.Fastest => "Fastest replies",
        AdvisorGoal.Private => "Private and offline",
        _ => "Balanced"
    };

    private static string Size(Planning.ComponentOption option) => option.QualityTier switch
    {
        <= 1 => "small",
        <= 3 => "medium",
        _ => "large"
    };

    /// <summary>The card an answer stands for: Unknown plans as an 8 GB NVIDIA card, AMD/Intel as 8 GB.</summary>
    private static IReadOnlyList<Planning.MachineGpu> Gpus(AdvisorGpu gpu) => gpu switch
    {
        AdvisorGpu.None => [],
        AdvisorGpu.OtherVendor => [new(GpuName(gpu), Planning.GpuVendor.Amd, 8)],
        _ => [new(GpuName(gpu), Planning.GpuVendor.Nvidia, Vram(gpu))]
    };

    private static string ComputerName(AdvisorComputer computer, int index) =>
        string.IsNullOrWhiteSpace(computer.Name) ? $"Computer {index + 2}" : computer.Name.Trim();

    public static string GpuName(AdvisorGpu gpu) => gpu switch
    {
        AdvisorGpu.Unknown => "Has a GPU, not sure which",
        AdvisorGpu.Nvidia4 => "NVIDIA, under 8 GB",
        AdvisorGpu.Nvidia8 => "NVIDIA, 8 GB",
        AdvisorGpu.Nvidia12 => "NVIDIA, 12 GB",
        AdvisorGpu.Nvidia16 => "NVIDIA, 16 GB",
        AdvisorGpu.Nvidia24 => "NVIDIA, 24 GB",
        AdvisorGpu.Nvidia32Plus => "NVIDIA, 32 GB or more",
        AdvisorGpu.OtherVendor => "AMD or Intel GPU",
        _ => "No dedicated GPU (or not sure)"
    };

    /// <summary>Maps a detected graphics card (vendor or adapter name, and dedicated memory) to the closest advisor
    /// answer, rounding memory down so plans stay safe. AMD/Intel adapters with under 2.5 GB of dedicated memory are
    /// integrated graphics and count as no dedicated GPU.</summary>
    public static AdvisorGpu Classify(string? vendorOrName, double? memoryGb)
    {
        if (string.IsNullOrWhiteSpace(vendorOrName)) return AdvisorGpu.None;
        if (!vendorOrName.Contains("nvidia", StringComparison.OrdinalIgnoreCase))
            return memoryGb >= 2.5 ? AdvisorGpu.OtherVendor : AdvisorGpu.None;
        return memoryGb switch
        {
            null or <= 0 => AdvisorGpu.Unknown,
            >= 31 => AdvisorGpu.Nvidia32Plus,
            >= 23 => AdvisorGpu.Nvidia24,
            >= 15 => AdvisorGpu.Nvidia16,
            >= 11.5 => AdvisorGpu.Nvidia12,
            >= 7.5 => AdvisorGpu.Nvidia8,
            _ => AdvisorGpu.Nvidia4
        };
    }

    private static string JoinAnd(IReadOnlyList<string> items) =>
        items.Count == 1 ? items[0] : string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1];

    private static int Vram(AdvisorGpu gpu) => gpu switch
    {
        AdvisorGpu.Nvidia4 => 4,
        AdvisorGpu.Unknown or AdvisorGpu.Nvidia8 or AdvisorGpu.OtherVendor => 8,
        AdvisorGpu.Nvidia12 => 12,
        AdvisorGpu.Nvidia16 => 16,
        AdvisorGpu.Nvidia24 => 24,
        AdvisorGpu.Nvidia32Plus => 32,
        _ => 0
    };
}
