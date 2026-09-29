namespace Martlet.Core.Installation;

public enum AdvisorGoal { Balanced, Smartest, Fastest, Private }
public enum AdvisorGpu { None, Nvidia8, Nvidia12, Nvidia16, Nvidia24, Nvidia32Plus, OtherVendor }
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
    public bool GamesOnThisPc { get; init; }
    /// <summary>Other computers that can run Martlet roles; 5 means five or more.</summary>
    public int ExtraMachines { get; init; }
    public AdvisorGpu ExtraMachineGpu { get; init; } = AdvisorGpu.Nvidia24;
}

public sealed record AdvisorRole(
    string Role, string Choice, string Where, string WhatItDoes, string Why,
    AdvisorAvailability Availability, string? UntilThen, string Data)
{
    public string Status => Availability switch
    {
        AdvisorAvailability.Available => "Available now",
        AdvisorAvailability.BeingBuilt => "Being built",
        _ => "Planned"
    };
}

public sealed record AdvisorMachine(string Name, IReadOnlyList<string> Runs);

public sealed record SetupAdvice(
    string Title, string Summary, IReadOnlyList<AdvisorRole> Roles, IReadOnlyList<AdvisorMachine> Machines,
    IReadOnlyList<string> Notes, IReadOnlyList<AdvisorNextStep> NextSteps);

/// <summary>Pure goal/hardware-to-placement recommendation for the setup advisor. It saves, probes and
/// contacts nothing; sizes are rough planning estimates, not measurements (see docs/RECOMMENDED_SETUPS.md).</summary>
public static class SetupAdvisor
{
    private const int SpeechVram = 4, WhisperVram = 3, FaceVram = 4;

    private const string LlmWhat = "Thinks of each reply. Its size decides how smart answers are; where it runs decides how soon the first sentence starts.";
    private const string SttWhat = "Turns your voice into text when you hold push-to-talk.";
    private const string TtsWhat = "Reads the reply aloud, one sentence at a time, while the rest is still being written.";
    private const string FaceWhat = "Moves the character's mouth and face to match the voice.";
    private const string CharacterWhat = "Draws your Live2D or VRM companion on the desktop.";
    private const string Local = "Stays on your computers.";

    private sealed class Machine(string name, int vram, bool nvidia, bool isHost)
    {
        public string Name { get; } = name;
        public int Vram { get; } = vram;
        public bool Nvidia { get; } = nvidia;
        public bool IsHost { get; } = isHost;
        public bool HasLlm { get; set; }
        public int Used { get; set; }
        public List<string> Runs { get; } = [];
        public bool Idle => !HasLlm && Used == 0;
    }

    public static SetupAdvice Recommend(AdvisorAnswers answers)
    {
        ArgumentNullException.ThrowIfNull(answers);
        var goal = answers.Goal;
        var hostGpu = answers.ExtraMachineGpu;
        var hostCount = hostGpu == AdvisorGpu.None ? 0 : Math.Clamp(answers.ExtraMachines, 0, 5);
        var hosts = Enumerable.Range(2, hostCount)
            .Select(i => new Machine($"Computer {i}", Vram(hostGpu), IsNvidia(hostGpu), true)).ToList();
        var pcGpuFree = answers.ThisPcGpu != AdvisorGpu.None && !answers.GamesOnThisPc;
        var pc = new Machine("This PC", pcGpuFree ? Vram(answers.ThisPcGpu) : 0,
            pcGpuFree && IsNvidia(answers.ThisPcGpu), false);
        var notes = new List<string>();
        var roles = new List<AdvisorRole>();
        var online = new SortedSet<string>(StringComparer.Ordinal);

        Machine? TakeIdleHost(bool needsNvidia) => hosts.FirstOrDefault(h => h.Idle && (!needsNvidia || h.Nvidia));
        bool CanShare(Machine m, int need, bool needsNvidia) =>
            (!needsNvidia || m.Nvidia) && Usable(m.Vram) - m.Used - (m.HasLlm ? LlmReserve(m.Vram) : 0) >= need &&
            !(m.HasLlm && goal == AdvisorGoal.Fastest && !m.IsHost);
        // Prefer a free host, then a machine already doing speech work, then this PC, and the LLM's GPU last.
        int Rank(Machine m) => m.HasLlm ? 3 : m.Idle ? (m.IsHost ? 0 : 2) : 1;
        Machine? Place(int need, bool needsNvidia)
        {
            var target = hosts.Append(pc).Where(m => CanShare(m, need, needsNvidia))
                .OrderBy(Rank).FirstOrDefault();
            if (target is not null) target.Used += need;
            return target;
        }

        // 1. Conversation model: the goal decides whether it is local.
        Machine? llmMachine = null;
        var llmVram = 0;
        var llmOnCpu = false;
        if (goal is AdvisorGoal.Fastest or AdvisorGoal.Private)
        {
            llmMachine = TakeIdleHost(needsNvidia: false) ?? (pc.Vram > 0 ? pc : null);
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
        Machine? speechMachine = null;
        if (wantGpuVoice || wantHostWhisper)
        {
            var need = (wantGpuVoice ? SpeechVram : 0) + (wantHostWhisper ? WhisperVram : 0);
            speechMachine = Place(need, needsNvidia: true);
            if (speechMachine is not null && !speechMachine.IsHost && wantHostWhisper)
            {
                speechMachine.Used -= WhisperVram;
                wantHostWhisper = false;
            }
            if (speechMachine is null) wantHostWhisper = false;
        }

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
                onPc ? AdvisorAvailability.BeingBuilt : AdvisorAvailability.Planned,
                onPc
                    ? "Use OpenAI in Setup / resume while local-server support is being built."
                    : "Use OpenAI, or a local server on this PC. A model on another computer needs HTTPS or the planned Martlet host LLM role.",
                Local));
            if (answers.ThisPcGpu == AdvisorGpu.OtherVendor && onPc)
                notes.Add("AMD and Intel GPUs can run the conversation model in Ollama or LM Studio. Check its memory: the size above assumes about 8 GB.");
        }
        else if (llmOnCpu)
        {
            pc.Runs.Add("Conversation model (small, on the CPU)");
            roles.Add(new("Conversation model (LLM)", "A small 3-4B model in Ollama or LM Studio, on the CPU", "This PC (CPU)", LlmWhat,
                "Keeps everything local, but replies will be slow without a GPU.", AdvisorAvailability.BeingBuilt,
                "Use OpenAI in Setup / resume while local-server support is being built.", Local));
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
            roles.Add(new("Conversation model (LLM)", choice, "Online", LlmWhat, why, AdvisorAvailability.BeingBuilt,
                "OpenAI (gpt-4.1) works today in Setup / resume while OpenRouter and NVIDIA Build are being connected.",
                "Your words and recent conversation go to the provider. Each request may cost money."));
        }

        // Speech-to-text role.
        if (answers.VoiceInput)
        {
            if (wantHostWhisper && speechMachine is not null)
            {
                speechMachine.Runs.Add("Speech-to-text (Whisper)");
                roles.Add(new("Speech-to-text", "Whisper (large) on the GPU", $"{speechMachine.Name} (GPU)", SttWhat,
                    "More accurate than CPU recognition, and your audio stays on your own network.", AdvisorAvailability.Planned,
                    "OpenAI transcription works today; Windows offline recognition is coming soon.", Local));
            }
            else if (goal == AdvisorGoal.Smartest)
            {
                online.Add("OpenAI");
                roles.Add(new("Speech-to-text", "OpenAI transcription (gpt-4o-transcribe)", "Online", SttWhat,
                    "The most accurate option Martlet supports.", AdvisorAvailability.Available, null,
                    "Your recorded push-to-talk audio goes to OpenAI. Each request may cost money."));
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
            if (wantGpuVoice && speechMachine is not null)
            {
                speechMachine.Runs.Add(answers.CustomVoice ? "Voice (your custom voice)" : "Voice (natural local voice)");
                roles.Add(new("Voice (text-to-speech)",
                    answers.CustomVoice
                        ? "Your own voice with a Voice Studio engine (F5, Qwen3-TTS, Chatterbox, GPT-SoVITS or XTTS-v2)"
                        : "A natural local voice with a Voice Studio engine",
                    $"{speechMachine.Name} (GPU)", TtsWhat,
                    answers.CustomVoice ? "Cloning a voice needs a self-hosted GPU engine."
                        : goal == AdvisorGoal.Fastest ? "A separate GPU speaks without an internet round trip and never waits for the conversation model."
                        : "Natural speech without sending reply text anywhere.",
                    AdvisorAvailability.Planned,
                    answers.CustomVoice
                        ? "Prepare your voice samples in Voice Library now, and use an OpenAI voice until the engine runs."
                        : "OpenAI voices work today in Setup / resume.",
                    Local));
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
                    "Reply text goes to OpenAI. Each request may cost money."));
            }
            if (answers.CustomVoice && speechMachine is null)
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
                        faceMachine.IsHost ? "Martlet's generated voice goes to that computer only." : Local));
                    notes.Add("Audio2Face is set up through Martlet hosts (Docker) and needs a free NVIDIA NGC key. It has not been verified on a real GPU yet.");
                }
                else
                {
                    pc.Runs.Add("Lip-sync (loudness)");
                    var reason = answers.GamesOnThisPc && hosts.Count == 0 ? "Your GPU is kept for games."
                        : llmMachine == pc && goal == AdvisorGoal.Fastest ? "The GPU is kept for the conversation model."
                        : "No NVIDIA GPU has room for Audio2Face.";
                    roles.Add(new("Lip-sync", "Loudness lip-sync", "This PC (CPU)", FaceWhat,
                        $"{reason} Loudness moves the mouth with the voice's volume; Audio2Face is richer.",
                        AdvisorAvailability.Available, null, "Stays on this PC."));
                }
            }
            roles.Add(new("Character", "Live2D or VRM character", "This PC", CharacterWhat,
                "It is drawn where you see it and needs very little GPU.", AdvisorAvailability.Available, null, "Stays on this PC."));
        }

        // Machines, spare capacity and general notes.
        pc.Runs.Insert(0, "Martlet app" + (answers.VoiceInput || answers.SpokenReplies ? ", microphone and speakers" : ""));
        var machines = new List<AdvisorMachine> { new(pc.Name, pc.Runs.ToArray()) };
        var spare = 0;
        foreach (var host in hosts)
        {
            if (host.Runs.Count == 0)
            {
                spare++;
                host.Runs.Add(spare == 1 ? "Spare: later, screen understanding (vision) or memory"
                    : "Spare: later, Voice Studio training");
            }
            machines.Add(new(host.Name, host.Runs.ToArray()));
        }
        if (answers.ExtraMachines > 0 && hostGpu == AdvisorGpu.None)
            notes.Add("Your other computers have no GPU, so they add little. Martlet keeps its work on this PC or online.");
        if (hostGpu == AdvisorGpu.OtherVendor && hostCount > 0)
            notes.Add("AMD and Intel GPUs on other computers can run the conversation model, but voice engines and Audio2Face need NVIDIA.");
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
        if (hosts.Any(h => h.Runs.Any(r => !r.StartsWith("Spare", StringComparison.Ordinal))) ||
            faceMachine == pc || speechMachine == pc) steps.Add(AdvisorNextStep.Hosts);
        if (answers.CustomVoice) steps.Add(AdvisorNextStep.VoiceLibrary);
        if (answers.Character) steps.Add(AdvisorNextStep.Character);

        var computers = 1 + hostCount;
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

    private static bool IsNvidia(AdvisorGpu gpu) => gpu is >= AdvisorGpu.Nvidia8 and <= AdvisorGpu.Nvidia32Plus;

    private static int Vram(AdvisorGpu gpu) => gpu switch
    {
        AdvisorGpu.Nvidia8 or AdvisorGpu.OtherVendor => 8,
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
