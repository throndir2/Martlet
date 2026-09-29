namespace Martlet.Core.Installation;

public enum AdvisorGoal { Balanced, Smartest, Fastest, Private }
/// <summary>GPU answer for one computer. <see cref="Unknown"/> means "has a dedicated GPU, not sure which"; the
/// advisor plans for an 8 GB NVIDIA card and says so.</summary>
public enum AdvisorGpu { None, Unknown, Nvidia4, Nvidia8, Nvidia12, Nvidia16, Nvidia24, Nvidia32Plus, OtherVendor }

/// <summary>Another computer that can run Martlet roles. <paramref name="Name"/> is the paired host ID or the
/// user's label; <paramref name="Detected"/> describes hardware the host reported, when it did.</summary>
public sealed record AdvisorComputer(AdvisorGpu Gpu, string? Name = null, string? Detected = null);
public enum AdvisorAvailability { Available, BeingBuilt, Planned }
public enum AdvisorNextStep { Setup, AudioSetup, Hosts, Character, VoiceLibrary }

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
    IReadOnlyList<string> Notes, IReadOnlyList<AdvisorNextStep> NextSteps);

/// <summary>Pure goal/hardware-to-placement recommendation for the setup advisor. It saves, probes and
/// contacts nothing; sizes are rough planning estimates, not measurements (see docs/RECOMMENDED_SETUPS.md).</summary>
public static class SetupAdvisor
{
    private const int SpeechVram = 4, WhisperVram = 3, FaceVram = 4;

    private const string LlmWhat = "Thinks of each reply. Its size decides how smart answers are; where it runs decides how soon the first sentence starts.";
    private const string SttWhat = "Turns your voice into text when you hold push-to-talk or talk hands-free.";
    private const string TtsWhat = "Reads the reply aloud, one sentence at a time, while the rest is still being written.";
    private const string FaceWhat = "Moves the character's mouth and face to match the voice.";
    private const string CharacterWhat = "Draws your Live2D or VRM companion on the desktop.";
    private const string Local = "Stays on your computers.";
    private const string LocalLlmHow = "Install Ollama (Start > Martlet prerequisites installs it and offers a model sized to your GPU) or LM Studio, and download the model. In Setup / resume > Destinations, choose an OpenAI-compatible LLM endpoint at http://127.0.0.1:11434/v1 (Ollama) or http://127.0.0.1:1234/v1 (LM Studio) and enter the model ID. No API key is needed.";
    private const string HostedLlmHow = "In Setup / resume > Destinations, choose OpenRouter or NVIDIA Build as the LLM endpoint, enter a model ID from its catalog and store its API key.";
    private const string OpenAiHow = "In Setup / resume, choose OpenAI for this role and store your OpenAI API key.";

    public const int MaxOtherComputers = 5;

    private sealed class Machine(string name, AdvisorGpu gpu, int vram, bool isHost, string hardware)
    {
        public string Name { get; } = name;
        public AdvisorGpu Gpu { get; } = gpu;
        public string Hardware { get; } = hardware;
        public int Vram { get; } = vram;
        public bool Nvidia { get; } = vram > 0 && IsNvidia(gpu);
        public bool IsHost { get; } = isHost;
        public bool HasLlm { get; set; }
        public int Used { get; set; }
        public List<string> Runs { get; } = [];
        public bool Idle => !HasLlm && Used == 0;
        public int Free => Usable(Vram) - Used - (HasLlm ? LlmReserve(Vram) : 0);
    }

    public static SetupAdvice Recommend(AdvisorAnswers answers)
    {
        ArgumentNullException.ThrowIfNull(answers);
        var goal = answers.Goal;
        var hosts = (answers.OtherComputers ?? []).Take(MaxOtherComputers)
            .Select((computer, i) => new Machine(ComputerName(computer, i), computer.Gpu, Vram(computer.Gpu), true,
                computer.Detected ?? GpuName(computer.Gpu))).ToList();
        var gpuHosts = hosts.Where(h => h.Vram > 0).ToList();
        var pcGpuFree = answers.ThisPcGpu != AdvisorGpu.None && !answers.GamesOnThisPc;
        var pc = new Machine("This PC", answers.ThisPcGpu, pcGpuFree ? Vram(answers.ThisPcGpu) : 0, false,
            (answers.ThisPcDetected ?? GpuName(answers.ThisPcGpu)) +
            (answers.GamesOnThisPc && answers.ThisPcGpu != AdvisorGpu.None ? " (kept for games)" : ""));
        var notes = new List<string>();
        var roles = new List<AdvisorRole>();
        var online = new SortedSet<string>(StringComparer.Ordinal);

        bool CanShare(Machine m, int need, bool needsNvidia) =>
            (!needsNvidia || m.Nvidia) && m.Free >= need &&
            !(m.HasLlm && goal == AdvisorGoal.Fastest && !m.IsHost);
        // Prefer a free host, then a machine already doing speech work, then this PC, and the LLM's GPU last;
        // among equals, the smallest GPU that fits so bigger ones stay free.
        int Rank(Machine m) => m.HasLlm ? 3 : m.Idle ? (m.IsHost ? 0 : 2) : 1;
        Machine? Place(int need, bool needsNvidia, bool hostsOnly = false, bool avoidLlm = false)
        {
            var target = hosts.Append(pc).Where(m => (!hostsOnly || m.IsHost) && !(avoidLlm && m.HasLlm) && CanShare(m, need, needsNvidia))
                .OrderBy(Rank).ThenBy(m => m.Free).FirstOrDefault();
            if (target is not null) target.Used += need;
            return target;
        }

        // Voice engines, GPU Whisper and Audio2Face need NVIDIA; the conversation model runs on any vendor.
        var nvidiaWork = answers.SpokenReplies && (answers.CustomVoice || answers.Character || goal is AdvisorGoal.Private or AdvisorGoal.Fastest)
            || answers.VoiceInput && goal == AdvisorGoal.Private;
        // The conversation model gets the largest free GPU (a dedicated host on a tie). When NVIDIA-only parts are
        // wanted, it avoids taking the only NVIDIA GPU if another GPU can hold it.
        Machine? PickLlmMachine()
        {
            var candidates = gpuHosts.Append(pc).Where(m => m.Vram > 0)
                .OrderByDescending(m => m.Vram).ThenBy(m => m.IsHost ? 0 : 1).ToList();
            if (!nvidiaWork) return candidates.FirstOrDefault();
            return candidates.FirstOrDefault(c => candidates.Any(o => o != c && o.Nvidia)) ?? candidates.FirstOrDefault();
        }

        // 1. Conversation model: the goal decides whether it is local.
        Machine? llmMachine = null;
        var llmVram = 0;
        var llmOnCpu = false;
        if (goal is AdvisorGoal.Fastest or AdvisorGoal.Private)
        {
            llmMachine = PickLlmMachine();
            llmVram = llmMachine?.Vram ?? 0;
            if (llmMachine is null && goal == AdvisorGoal.Private)
            {
                if (answers.ThisPcGpu != AdvisorGpu.None)
                {
                    llmMachine = pc;
                    llmVram = Vram(answers.ThisPcGpu);
                    notes.Add("The local model shares this PC's GPU with your games and may lower frame rates. Another computer with a GPU avoids that.");
                }
                else llmOnCpu = true;
            }
            if (llmMachine is not null) llmMachine.HasLlm = true;
        }

        // 2. Voice and speech recognition placement (GPU work only where it helps the goal).
        var wantGpuVoice = answers.SpokenReplies &&
            (answers.CustomVoice || goal == AdvisorGoal.Private ||
             goal == AdvisorGoal.Fastest && (hosts.Any(h => h.Idle && h.Nvidia) || llmMachine is { IsHost: true } && pc.Nvidia));
        var wantHostWhisper = answers.VoiceInput && goal == AdvisorGoal.Private && hosts.Any(h => h.Idle && h.Nvidia);
        Machine? voiceMachine = null, whisperMachine = null;
        // Keep voice and Whisper together on one free host when it has room; otherwise split them.
        if (wantGpuVoice && wantHostWhisper)
            voiceMachine = whisperMachine = Place(SpeechVram + WhisperVram, needsNvidia: true, hostsOnly: true, avoidLlm: true);
        if (wantGpuVoice) voiceMachine ??= Place(SpeechVram, needsNvidia: true);
        if (wantHostWhisper) whisperMachine ??= Place(WhisperVram, needsNvidia: true, hostsOnly: true);

        // 3. Lip-sync analysis needs NVIDIA; it runs beside playback and never delays the reply.
        Machine? faceMachine = answers.Character && answers.SpokenReplies ? Place(FaceVram, needsNvidia: true) : null;

        // Conversation model role.
        if (llmMachine is not null)
        {
            var size = LlmSize(llmVram - llmMachine.Used, goal == AdvisorGoal.Fastest);
            var where = $"{llmMachine.Name} (GPU)";
            llmMachine.Runs.Add($"Conversation model ({size})");
            var onPc = !llmMachine.IsHost;
            roles.Add(new("Conversation model (LLM)", $"A {size} model in Ollama or LM Studio", where, LlmWhat,
                goal == AdvisorGoal.Fastest
                    ? "A GPU that only runs the conversation model starts the first sentence without an internet round trip or a provider queue. Smaller models start sooner but are less capable."
                    : "Your conversation never leaves your computers. Local models are smaller and less capable than large hosted ones.",
                onPc ? AdvisorAvailability.Available : AdvisorAvailability.Planned,
                onPc
                    ? null
                    : "This works today if that computer serves the model over HTTPS with a trusted certificate; the planned Martlet host LLM role will set that up for you. Otherwise use OpenRouter, NVIDIA Build or a local server on this PC.",
                Local,
                onPc ? LocalLlmHow : "Run Ollama or another OpenAI-compatible server on that computer behind HTTPS, then enter its https://.../v1 address in Setup / resume > Destinations."));
            if (llmMachine.Gpu == AdvisorGpu.OtherVendor)
                notes.Add($"AMD and Intel GPUs can run the conversation model in Ollama or LM Studio. Check the GPU memory on {Lower(llmMachine.Name)}: the size above assumes about 8 GB.");
        }
        else if (llmOnCpu)
        {
            pc.Runs.Add("Conversation model (small, on the CPU)");
            roles.Add(new("Conversation model (LLM)", "A small 3-4B model in Ollama or LM Studio, on the CPU", "This PC (CPU)", LlmWhat,
                "Keeps everything local, but replies will be slow without a GPU.", AdvisorAvailability.Available,
                null, Local, LocalLlmHow));
            notes.Add("Without a GPU, a fully private setup is slow. A GPU on this PC or another computer makes local replies practical.");
        }
        else
        {
            online.Add("OpenRouter or NVIDIA Build");
            var (choice, why) = goal switch
            {
                AdvisorGoal.Smartest => ("The largest model you are willing to pay for, on OpenRouter or NVIDIA Build",
                    "Frontier-size hosted models give the best answers; no home GPU can hold them."),
                AdvisorGoal.Fastest => ("A small, fast hosted model on OpenRouter or NVIDIA Build",
                    answers.GamesOnThisPc && answers.ThisPcGpu != AdvisorGpu.None
                        ? "Your GPU is kept for games. A GPU that only runs a local model would start replies sooner; another computer can provide one."
                        : "No GPU is available for a local model. A dedicated local GPU would start replies sooner."),
                _ => ("A large model on OpenRouter or NVIDIA Build",
                    "Hosted models are larger and smarter than what fits on a home GPU, and they leave your GPU free for voice and face.")
            };
            roles.Add(new("Conversation model (LLM)", choice, "Online", LlmWhat, why, AdvisorAvailability.Available, null,
                "Your words and recent conversation go to the provider. Each request may cost money.", HostedLlmHow));
        }

        // Speech-to-text role.
        if (answers.VoiceInput)
        {
            if (whisperMachine is not null)
            {
                whisperMachine.Runs.Add("Speech-to-text (Whisper)");
                roles.Add(new("Speech-to-text", "Whisper (large) on the GPU", $"{whisperMachine.Name} (GPU)", SttWhat,
                    "More accurate than CPU recognition, and your audio stays on your own network.", AdvisorAvailability.Planned,
                    "OpenAI transcription works today; Windows offline recognition is coming soon.", Local));
            }
            else if (goal == AdvisorGoal.Smartest)
            {
                online.Add("OpenAI");
                roles.Add(new("Speech-to-text", "OpenAI transcription (gpt-4o-transcribe)", "Online", SttWhat,
                    "The most accurate option Martlet supports.", AdvisorAvailability.Available, null,
                    "Your recorded push-to-talk audio goes to OpenAI. Each request may cost money.", OpenAiHow));
            }
            else
            {
                pc.Runs.Add("Speech-to-text (CPU)");
                roles.Add(new("Speech-to-text", "Windows offline speech recognition", "This PC (CPU)", SttWhat,
                    goal == AdvisorGoal.Fastest
                        ? "Short push-to-talk clips transcribe quickly on the CPU with no network hop."
                        : "Keeps your microphone audio on this PC. The CPU is enough for push-to-talk.",
                    AdvisorAvailability.Planned, "OpenAI transcription works today in Setup / resume.", "Stays on this PC."));
            }
        }

        // Voice role.
        if (answers.SpokenReplies)
        {
            if (voiceMachine is not null)
            {
                voiceMachine.Runs.Add(answers.CustomVoice ? "Voice (your custom voice)" : "Voice (natural local voice)");
                roles.Add(new("Voice (text-to-speech)",
                    answers.CustomVoice
                        ? "Your own voice with a Voice Studio engine (F5, Qwen3-TTS, Chatterbox, GPT-SoVITS or XTTS-v2)"
                        : "A natural local voice with a Voice Studio engine",
                    $"{voiceMachine.Name} (GPU)", TtsWhat,
                    answers.CustomVoice ? "Cloning a voice needs a self-hosted GPU engine."
                        : goal == AdvisorGoal.Fastest ? "A separate GPU speaks without an internet round trip and never waits for the conversation model."
                        : "Natural speech without sending reply text anywhere.",
                    AdvisorAvailability.Planned,
                    answers.CustomVoice
                        ? "Use an OpenAI voice until the engine runs."
                        : "OpenAI voices work today in Setup / resume.",
                    Local,
                    answers.CustomVoice ? "Import your voice samples and transcripts in Voice Library now, so they are ready." : null));
            }
            else if (goal is AdvisorGoal.Fastest or AdvisorGoal.Private)
            {
                pc.Runs.Add("Voice (Windows voices)");
                roles.Add(new("Voice (text-to-speech)", "Windows installed voices", "This PC (CPU)", TtsWhat,
                    goal == AdvisorGoal.Fastest
                        ? "Starts speaking almost instantly but sounds robotic. OpenAI voices sound better but add an internet round trip."
                        : "Free and offline, but sounds robotic. An NVIDIA GPU with more free memory would allow a natural local voice.",
                    AdvisorAvailability.Planned, "OpenAI voices work today in Setup / resume.", "Stays on this PC."));
            }
            else
            {
                online.Add("OpenAI");
                roles.Add(new("Voice (text-to-speech)", "OpenAI voice (gpt-4o-mini-tts)", "Online", TtsWhat,
                    "A natural voice with no local GPU needed.", AdvisorAvailability.Available, null,
                    "Reply text goes to OpenAI. Each request may cost money.", OpenAiHow));
            }
            if (answers.CustomVoice && voiceMachine is null)
                notes.Add("A custom voice needs an NVIDIA GPU (8 GB+) that is free during conversations, on this PC or another computer. Until then Martlet uses a built-in voice.");
        }

        // Character and lip-sync.
        if (answers.Character)
        {
            pc.Runs.Add("Character (Live2D/VRM)");
            if (answers.SpokenReplies)
            {
                if (faceMachine is not null)
                {
                    faceMachine.Runs.Add("Lip-sync (Audio2Face)");
                    roles.Add(new("Lip-sync", "NVIDIA Audio2Face", $"{faceMachine.Name} (GPU)", FaceWhat,
                        "Rich mouth and face animation. It runs beside playback, so it never delays replies.",
                        AdvisorAvailability.Available, null,
                        faceMachine.IsHost ? "Martlet's generated voice goes to that computer only." : Local,
                        faceMachine.IsHost
                            ? $"In Martlet hosts, set up {faceMachine.Name} (Docker over SSH, native Ubuntu, or by hand), add the Audio2Face role and pair this PC."
                            : "In Martlet hosts, choose This PC with Docker Desktop, then add the Audio2Face role."));
                    notes.Add("Audio2Face is set up through Martlet hosts (Docker) and needs a free NVIDIA NGC key. It has not been verified on a real GPU yet.");
                }
                else
                {
                    pc.Runs.Add("Lip-sync (loudness)");
                    var reason = answers.GamesOnThisPc && gpuHosts.Count == 0 ? "Your GPU is kept for games."
                        : llmMachine == pc && goal == AdvisorGoal.Fastest ? "The GPU is kept for the conversation model."
                        : "No NVIDIA GPU has room for Audio2Face.";
                    roles.Add(new("Lip-sync", "Loudness lip-sync", "This PC (CPU)", FaceWhat,
                        $"{reason} Loudness moves the mouth with the voice's volume; Audio2Face is richer.",
                        AdvisorAvailability.Available, null, "Stays on this PC.",
                        "In Character settings, leave lip-sync on Auto or choose Loudness."));
                }
            }
            roles.Add(new("Character", "Live2D or VRM character", "This PC", CharacterWhat,
                "It is drawn where you see it and needs very little GPU.", AdvisorAvailability.Available, null, "Stays on this PC.",
                "Press Show character on the home screen, or pick your own model in Character settings."));
        }

        // Machines, spare capacity and general notes.
        pc.Runs.Insert(0, "Martlet app" + (answers.VoiceInput || answers.SpokenReplies ? ", microphone and speakers" : ""));
        var hostsInUse = hosts.Any(h => h.Runs.Count > 0);
        var machines = new List<AdvisorMachine> { new(pc.Name, pc.Hardware, pc.Runs.ToArray()) };
        var spare = 0;
        foreach (var host in hosts)
        {
            if (host.Runs.Count == 0 && host.Vram == 0)
                host.Runs.Add("Not needed for now: without a GPU it adds little over this PC.");
            else if (host.Runs.Count == 0)
            {
                spare++;
                host.Runs.Add(spare == 1 ? "Spare: later, screen understanding (vision) or memory"
                    : "Spare: later, Voice Studio training");
            }
            machines.Add(new(host.Name, host.Hardware, host.Runs.ToArray()));
        }
        if (hosts.Count > 0 && gpuHosts.Count == 0)
            notes.Add("Your other computers have no GPU, so they add little. Martlet keeps its work on this PC or online.");
        if (gpuHosts.Any(h => h.Gpu == AdvisorGpu.OtherVendor))
            notes.Add("AMD and Intel GPUs on other computers can run the conversation model, but voice engines and Audio2Face need NVIDIA.");
        var unsure = hosts.Prepend(pc).Where(m => m.Gpu == AdvisorGpu.Unknown).Select(m => Lower(m.Name)).ToList();
        if (unsure.Count > 0)
            notes.Add($"You were not sure which GPU is in {JoinAnd(unsure)}, so Martlet planned for an 8 GB NVIDIA card. " +
                "Check Task Manager > Performance > GPU on Windows, or run nvidia-smi on Linux, then pick the exact GPU for a better fit. " +
                "Paired Martlet hosts report their GPU automatically.");
        if (answers.GamesOnThisPc)
            notes.Add(pc.HasLlm ? "Only the local model shares this PC's GPU with your games."
                : "Your games keep this PC's GPU. Martlet only runs the app, audio and character here.");
        var interim = roles.Any(r => r.Availability != AdvisorAvailability.Available);
        notes.Add(online.Count == 0
            ? interim ? "Once every part above is available, nothing leaves your computers. Until then, the stand-ins listed above use OpenAI."
                : "Nothing leaves your computers."
            : $"Online services: {string.Join(", ", online)}. The data listed for each role goes to them, and requests may cost money.");
        if (interim)
            notes.Add("\"Being built\" and \"Planned\" parts are not in Martlet yet. Each one says what to use for now.");
        notes.Add("Each role uses exactly one place. Martlet never switches to another provider on its own; you change it in settings.");
        notes.Add("Model sizes and GPU memory are rough estimates, not measured on your hardware.");

        var steps = new List<AdvisorNextStep> { AdvisorNextStep.Setup };
        if (answers.VoiceInput || answers.SpokenReplies) steps.Add(AdvisorNextStep.AudioSetup);
        if (hostsInUse || faceMachine == pc || voiceMachine == pc) steps.Add(AdvisorNextStep.Hosts);
        if (answers.CustomVoice) steps.Add(AdvisorNextStep.VoiceLibrary);
        if (answers.Character) steps.Add(AdvisorNextStep.Character);

        var computers = 1 + gpuHosts.Count;
        var title = $"{GoalName(goal)}: {computers} computer{(computers == 1 ? "" : "s")}";
        var summary = goal switch
        {
            AdvisorGoal.Smartest => "The biggest hosted model for the best answers. Local hardware goes to voice and face.",
            AdvisorGoal.Fastest => "A conversation model on a GPU of its own, so replies start as soon as possible.",
            AdvisorGoal.Private => "Everything runs on your own computers.",
            _ => "Smart hosted answers, with your GPU spent where it helps most: voice and face."
        };
        return new(title, summary, roles.ToArray(), machines.ToArray(), notes.ToArray(), steps.ToArray());
    }

    public static string GoalName(AdvisorGoal goal) => goal switch
    {
        AdvisorGoal.Smartest => "Smartest answers",
        AdvisorGoal.Fastest => "Fastest replies",
        AdvisorGoal.Private => "Private and offline",
        _ => "Balanced"
    };

    private static bool IsNvidia(AdvisorGpu gpu) => gpu is AdvisorGpu.Unknown or (>= AdvisorGpu.Nvidia4 and <= AdvisorGpu.Nvidia32Plus);

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

    private static string Lower(string name) => name == "This PC" ? "this PC" : name;

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

    private static int LlmReserve(int vram) => vram >= 12 ? 6 : 4;

    // Leave roughly 15% headroom for drivers, context growth and the desktop.
    private static int Usable(int vram) => vram * 85 / 100;

    private static string LlmSize(int vram, bool fastest) => vram switch
    {
        < 8 => "3-4B",
        < 12 => fastest ? "3-4B" : "7-8B Q4 (short context)",
        < 16 => fastest ? "7-8B Q4" : "7-8B Q4-Q6",
        < 24 => fastest ? "7-8B Q6-Q8" : "12-14B Q4",
        < 32 => fastest ? "12-14B Q4" : "12-14B Q6-Q8",
        _ => fastest ? "12-14B Q8 or 24B Q4" : "24-32B Q4"
    };
}
