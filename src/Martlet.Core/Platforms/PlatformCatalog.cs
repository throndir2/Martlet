using System.Globalization;
using Martlet.Core.Cluster;
using Martlet.Core.Installation;

namespace Martlet.Core.Platforms;

/// <summary>The operating systems Martlet targets, as the device you talk to (the companion) or as a host.</summary>
public enum DevicePlatform { Windows, Linux, MacOs, Ios, Android }

/// <summary>Where an engine or feature runs: on the device you talk to, or on a paired host that does a job for it.</summary>
public enum PlatformSide { Companion, Host }

/// <summary>What Martlet offers for an engine on a platform: working now, planned in a named slice, not planned, or not
/// possible on that platform at all.</summary>
public enum PlatformAvailability { Works, Planned, NotPlanned, Impossible }

/// <summary>The answer for one device: yes; no (with the reason); unknown (the device has not reported what is needed,
/// so Martlet allows it and says what it could not check); or not yet (planned, not built).</summary>
public enum PlatformVerdict { Yes, No, Unknown, NotYet }

public sealed record PlatformCheck(PlatformVerdict Verdict, string Reason)
{
    /// <summary>Whether Martlet lets you choose it: yes, or unknown because the device has not reported enough.</summary>
    public bool Allowed => Verdict is PlatformVerdict.Yes or PlatformVerdict.Unknown;
}

public sealed record PlatformGpu(string Name, string Vendor, double? MemoryGb)
{
    public bool IsNvidia => Vendor == "nvidia";
    public string Describe() => MemoryGb is { } gb ? $"{Name} ({gb.ToString("0.#", CultureInfo.InvariantCulture)} GB)" : Name;
}

/// <summary>Device features a host or companion can report beyond its hardware (machine report <c>features</c>).</summary>
public static class PlatformFeatures
{
    public const string AppleIntelligence = "apple-intelligence";
    public const string GeminiNano = "gemini-nano";
    /// <summary>The device hosts only while Martlet is open on its screen (iPhone, iPad).</summary>
    public const string ForegroundOnly = "foreground-only";
    public const string OnBattery = "battery";
}

/// <summary>What a device reported about itself: this PC, or a paired host's machine report. Missing facts are null and
/// make requirement checks answer <see cref="PlatformVerdict.Unknown"/>, never yes.</summary>
public sealed record PlatformDevice
{
    public required DevicePlatform Platform { get; init; }
    /// <summary>The name shown in reasons, for example a host ID or "This PC".</summary>
    public required string Name { get; init; }
    public Version? OsVersion { get; init; }
    public bool? Arm64 { get; init; }
    public double? MemoryGb { get; init; }
    /// <summary>The GPUs it reported; null when it reported no hardware at all.</summary>
    public IReadOnlyList<PlatformGpu>? Gpus { get; init; }
    /// <summary>Docker hosts: whether containers can use its NVIDIA GPUs.</summary>
    public bool? GpuContainers { get; init; }
    /// <summary>Reported features (<see cref="PlatformFeatures"/>); null when the device reports none.</summary>
    public IReadOnlySet<string>? Features { get; init; }

    /// <summary>Whether it hosts only while Martlet is open on its screen. An iPhone or iPad that reported nothing is
    /// assumed to.</summary>
    public bool ForegroundOnly => Features?.Contains(PlatformFeatures.ForegroundOnly) == true ||
        Platform == DevicePlatform.Ios && Features is null;

    /// <summary>A paired host from its last machine report. Hosts that predate platform reporting run the Linux host
    /// engine (Ubuntu, or Docker containers on Windows or a Mac), so they count as Linux.</summary>
    public static PlatformDevice FromHost(string hostId, HostHardware? report) => report is null
        ? new() { Platform = DevicePlatform.Linux, Name = hostId }
        : new()
        {
            Platform = PlatformCatalog.ParsePlatform(report.Platform) ?? DevicePlatform.Linux,
            Name = hostId,
            OsVersion = PlatformCatalog.ParseVersion(report.OsVersion),
            Arm64 = report.Architecture is { } arch ? arch is "arm64" or "aarch64" : null,
            MemoryGb = report.MemoryGb,
            Gpus = report.Gpus.Select(g => new PlatformGpu(g.Name, g.Vendor, g.MemoryGb)).ToArray(),
            GpuContainers = report.Method == "docker" ? report.NvidiaContainers switch { "yes" => true, "no" => false, _ => null } : null,
            Features = report.Features is { } features ? features.ToHashSet(StringComparer.Ordinal) : null
        };
}

/// <summary>A minimum a device must meet for an engine. An unmet fact is a "no" with its reason; an unreported fact is
/// an "unknown".</summary>
public sealed record PlatformRequirement
{
    public Version? MinimumOs { get; init; }
    public bool AppleSilicon { get; init; }
    public double? NvidiaGb { get; init; }
    public double? MemoryGb { get; init; }
    public string? Feature { get; init; }

    public string Describe()
    {
        var parts = new List<string>();
        if (NvidiaGb is { } gb) parts.Add($"an NVIDIA GPU with {gb:0} GB+");
        if (AppleSilicon) parts.Add("Apple silicon (M1 or later)");
        if (Feature == PlatformFeatures.AppleIntelligence) parts.Add("Apple Intelligence");
        if (Feature == PlatformFeatures.GeminiNano) parts.Add("Gemini Nano");
        if (MinimumOs is { } os) parts.Add($"system version {os.Major}+");
        if (MemoryGb is { } memory) parts.Add($"{memory:0} GB+ memory");
        return string.Join(", ", parts);
    }

    internal PlatformCheck Check(PlatformDevice device, string engine)
    {
        var unknown = new List<string>();
        if (NvidiaGb is { } need)
        {
            if (device.Gpus is null) unknown.Add($"{device.Name} hasn't reported its hardware yet");
            else
            {
                var nvidia = device.Gpus.Where(g => g.IsNvidia).OrderByDescending(g => g.MemoryGb ?? 0).FirstOrDefault();
                if (nvidia is null)
                    return No($"{engine} needs an NVIDIA GPU with {need:0} GB+; {device.Name} reports " +
                        (device.Gpus.Count == 0 ? "no GPU" + (device.GpuContainers == false ? " its containers can use" : "")
                            : string.Join(", ", device.Gpus.Select(g => g.Describe()))) + ".");
                if (nvidia.MemoryGb is not { } has) unknown.Add($"{device.Name}'s NVIDIA driver didn't report the GPU's memory");
                else if (has < need - 0.25)
                    return No($"{engine} needs an NVIDIA GPU with {need:0} GB+; {device.Name}'s {nvidia.Describe()} is too small.");
            }
        }
        if (AppleSilicon)
        {
            if (device.Arm64 is null) unknown.Add($"{device.Name} hasn't reported its processor type");
            else if (device.Arm64 == false)
                return No($"{engine} needs a Mac with Apple silicon (M1 or later); {device.Name} has an Intel processor.");
        }
        if (Feature is { } feature)
        {
            var name = feature == PlatformFeatures.AppleIntelligence ? "Apple Intelligence" : "Gemini Nano";
            if (device.Features is null) unknown.Add($"{device.Name} hasn't reported whether it has {name}");
            else if (!device.Features.Contains(feature))
                return No($"{engine} needs {name}; {device.Name} doesn't have it, or it is turned off there.");
        }
        if (MinimumOs is { } minimum)
        {
            if (device.OsVersion is null) unknown.Add($"{device.Name} hasn't reported its system version");
            else if (device.OsVersion < minimum)
                return No($"{engine} needs {PlatformCatalog.Name(device.Platform)} {minimum.Major} or later; {device.Name} runs {device.OsVersion}.");
        }
        if (MemoryGb is { } memoryNeed)
        {
            if (device.MemoryGb is null) unknown.Add($"{device.Name} hasn't reported its memory");
            else if (device.MemoryGb < memoryNeed - 0.25)
                return No($"{engine} needs {memoryNeed:0} GB+ memory; {device.Name} has {device.MemoryGb:0.#} GB.");
        }
        return unknown.Count == 0 ? new(PlatformVerdict.Yes, "")
            : new(PlatformVerdict.Unknown, $"{engine} needs {Describe()}; Martlet can't check that yet: {string.Join("; ", unknown)}.");
    }

    private static PlatformCheck No(string reason) => new(PlatformVerdict.No, reason);
}

/// <summary>Whether and how one engine is offered on one platform and side.</summary>
public sealed record PlatformSupport(DevicePlatform Platform, PlatformSide Side, PlatformAvailability Availability,
    string Note = "", PlatformRequirement? Requires = null, string? Slice = null);

/// <summary>An engine that can do a job (thinking, listening, speaking, lip-sync) or a feature of a companion or host.</summary>
public sealed record PlatformEngine(string Id, string Job, string Name, IReadOnlyList<PlatformSupport> Support)
{
    public PlatformSupport? On(DevicePlatform platform, PlatformSide side) =>
        Support.FirstOrDefault(s => s.Platform == platform && s.Side == side);
}

/// <summary>Which engine and feature each operating system supports, as the device you talk to and as a host. The single
/// source of truth behind docs/PLATFORMS.md and the guardrails that keep the app from offering impossible choices; the
/// platform plans (docs/IOS.md, MACOS.md, ANDROID.md) feed it.</summary>
public static class PlatformCatalog
{
    /// <summary>The job value for features that are not one of <see cref="ClusterJobs"/>.</summary>
    public const string Feature = "feature";

    private const DevicePlatform Win = DevicePlatform.Windows, Linux = DevicePlatform.Linux, Mac = DevicePlatform.MacOs,
        Ios = DevicePlatform.Ios, Android = DevicePlatform.Android;
    private const PlatformSide You = PlatformSide.Companion, Host = PlatformSide.Host;
    private const string IosPlan = "IOS.md", MacPlan = "MACOS.md", AndroidPlan = "ANDROID.md";
    private const string NoLinuxApp = "there is no Linux companion app; Linux computers are hosts";

    private static readonly PlatformRequirement Nvidia4 = new() { NvidiaGb = 4 };
    private static readonly PlatformRequirement Nvidia6 = new() { NvidiaGb = 6 };
    private static readonly PlatformRequirement AppleSilicon = new() { AppleSilicon = true };
    private static readonly PlatformRequirement Os26 = new() { MinimumOs = new(26, 0) };
    private static readonly PlatformRequirement IosIntelligence = new() { MinimumOs = new(26, 0), Feature = PlatformFeatures.AppleIntelligence };
    private static readonly PlatformRequirement MacIntelligence = new()
        { MinimumOs = new(26, 0), AppleSilicon = true, Feature = PlatformFeatures.AppleIntelligence };
    private static readonly PlatformRequirement Pcc = new() { MinimumOs = new(27, 0), Feature = PlatformFeatures.AppleIntelligence };
    private static readonly PlatformRequirement Nano = new() { Feature = PlatformFeatures.GeminiNano };
    private static readonly PlatformRequirement Phone6Gb = new() { MemoryGb = 6 };

    private static PlatformSupport Works(DevicePlatform p, PlatformSide s, string note = "", PlatformRequirement? r = null) =>
        new(p, s, PlatformAvailability.Works, note, r);
    private static PlatformSupport Planned(DevicePlatform p, PlatformSide s, string slice, string note = "", PlatformRequirement? r = null) =>
        new(p, s, PlatformAvailability.Planned, note, r, slice);
    private static PlatformSupport NotPlanned(DevicePlatform p, PlatformSide s, string note) => new(p, s, PlatformAvailability.NotPlanned, note);
    private static PlatformSupport Impossible(DevicePlatform p, PlatformSide s, string note) => new(p, s, PlatformAvailability.Impossible, note);

    /// <summary>Cloud engines run at a provider, so every companion app can use them.</summary>
    private static PlatformSupport[] Cloud(string windowsNote = "") =>
    [
        Works(Win, You, windowsNote), Planned(Mac, You, MacPlan), Planned(Ios, You, "IO05"), Planned(Android, You, AndroidPlan),
        NotPlanned(Linux, You, NoLinuxApp)
    ];

    private static PlatformSupport[] AppleOnly(string engine, params PlatformSide[] sides) =>
    [
        .. sides.SelectMany(side => new[] { Win, Linux, Android }.Select(p => Impossible(p, side, $"{engine} runs only on Apple devices")))
    ];

    private static PlatformSupport[] NvidiaOnly(string engine) =>
    [
        Impossible(Mac, Host, $"{engine} needs an NVIDIA GPU; Macs have none"),
        Impossible(Ios, Host, $"{engine} needs an NVIDIA GPU; iPhones and iPads have none"),
        Impossible(Android, Host, $"{engine} needs an NVIDIA GPU; phones and tablets have none")
    ];

    public static IReadOnlyList<PlatformEngine> Engines { get; } =
    [
        // ---- thinking ----
        new("openai-llm", ClusterJobs.Thinking, "OpenAI", Cloud()),
        new("chat-completions", ClusterJobs.Thinking, "OpenRouter, NVIDIA Build or another OpenAI-compatible server",
            Cloud("also a local server such as Ollama or LM Studio on this PC")),
        new("ollama", ClusterJobs.Thinking, "Ollama",
        [
            Works(Linux, Host, "an NVIDIA GPU makes replies fast; small models also run on the CPU"),
            Works(Win, Host, "through Docker Desktop (This PC's host service), or as a local server through Chat Completions"),
            Planned(Mac, Host, MacPlan, "native Ollama uses the Mac's GPU; Docker on a Mac is CPU-only"),
            Impossible(Ios, Host, "Ollama does not run on iPhone or iPad; Apple Intelligence does the thinking there"),
            NotPlanned(Android, Host, "Ollama has no Android app; Android hosts use Gemini Nano or a small on-phone model")
        ]),
        new("apple-on-device-llm", ClusterJobs.Thinking, "Apple Intelligence (on-device model)",
        [
            Planned(Ios, You, "IO05", "on the iPhone or iPad itself", IosIntelligence),
            Planned(Ios, Host, "IO03", "served to your other computers", IosIntelligence),
            Planned(Mac, You, MacPlan, "", MacIntelligence), Planned(Mac, Host, MacPlan, "", MacIntelligence),
            .. AppleOnly("Apple's on-device model", You, Host)
        ]),
        new("apple-pcc", ClusterJobs.Thinking, "Apple Private Cloud Compute",
        [
            Planned(Ios, You, "IO05", "Apple's server model; needs an Apple-granted entitlement and has a daily limit", Pcc),
            Planned(Mac, You, MacPlan, "Apple's server model; needs an Apple-granted entitlement and has a daily limit", Pcc),
            .. AppleOnly("Private Cloud Compute", You)
        ]),
        new("gemini-nano", ClusterJobs.Thinking, "Gemini Nano (on the phone)",
        [
            Planned(Android, You, AndroidPlan, "only on phones with Google's on-device model", Nano),
            Planned(Android, Host, AndroidPlan, "only on phones with Google's on-device model", Nano),
            Impossible(Win, You, "Gemini Nano runs only on supported Android phones"),
            Impossible(Ios, You, "Gemini Nano runs only on supported Android phones")
        ]),
        new("litert-llm", ClusterJobs.Thinking, "Small open model on the phone (LiteRT or llama.cpp)",
        [
            Planned(Android, You, AndroidPlan, "small Gemma-class models; slow on older phones", Phone6Gb),
            Planned(Android, Host, AndroidPlan, "small Gemma-class models; slow on older phones", Phone6Gb)
        ]),
        new("mlx-llm", ClusterJobs.Thinking, "Open model on a Mac's GPU (MLX)",
        [
            Planned(Mac, Host, MacPlan, "", AppleSilicon), Planned(Mac, You, MacPlan, "", AppleSilicon),
            Impossible(Win, Host, "MLX runs only on Apple silicon"), Impossible(Linux, Host, "MLX runs only on Apple silicon")
        ]),

        // ---- listening ----
        new("openai-stt", ClusterJobs.Listening, "OpenAI transcription", Cloud()),
        new("whisper", ClusterJobs.Listening, "whisper",
        [
            Works(Linux, Host, "runs well on the CPU; an NVIDIA GPU makes it faster"),
            Works(Win, Host, "through Docker Desktop (This PC's host service)"),
            Planned(Mac, Host, MacPlan, "whisper.cpp uses the Mac's GPU natively"),
            NotPlanned(Ios, Host, "iPhones and iPads use Apple speech recognition instead"),
            Planned(Android, Host, AndroidPlan, "whisper.cpp on the phone's CPU; small models only")
        ]),
        new("local-whisper", ClusterJobs.Listening, "whisper.cpp on this PC",
        [
            Planned(Win, You, "PL02", "it can be saved in Setup, but conversations don't use it yet"),
            NotPlanned(Linux, You, NoLinuxApp)
        ]),
        new("windows-speech", ClusterJobs.Listening, "Windows speech recognition",
        [
            Planned(Win, You, "PL02", "it can be saved in Setup, but conversations don't use it yet"),
            Impossible(Mac, You, "Windows speech runs only on Windows"), Impossible(Ios, You, "Windows speech runs only on Windows"),
            Impossible(Android, You, "Windows speech runs only on Windows")
        ]),
        new("apple-speech", ClusterJobs.Listening, "Apple speech recognition (on-device)",
        [
            Planned(Ios, You, "IO05", "", Os26), Planned(Ios, Host, "IO03", "served to your other computers", Os26),
            Planned(Mac, You, MacPlan, "", Os26), Planned(Mac, Host, MacPlan, "", Os26),
            .. AppleOnly("Apple speech recognition", You, Host)
        ]),
        new("android-speech", ClusterJobs.Listening, "Android speech recognition",
        [
            Planned(Android, You, AndroidPlan, "on the phone on Android 12+; older phones may send audio to Google"),
            Planned(Android, Host, AndroidPlan, "on the phone on Android 12+"),
            Impossible(Win, You, "Android speech runs only on Android"), Impossible(Ios, You, "Android speech runs only on Android")
        ]),

        // ---- speaking ----
        new("openai-tts", ClusterJobs.Speaking, "OpenAI voices", Cloud()),
        new("f5", ClusterJobs.Speaking, "F5 voice cloning",
        [
            Works(Linux, Host, "", Nvidia6), Works(Win, Host, "through Docker Desktop (This PC's host service)", Nvidia6),
            Impossible(Mac, Host, "the F5 host role needs an NVIDIA GPU; a Mac uses F5 on MLX instead"),
            Impossible(Ios, Host, "F5 needs an NVIDIA GPU; iPhones and iPads have none"),
            Impossible(Android, Host, "F5 needs an NVIDIA GPU; phones and tablets have none")
        ]),
        new("f5-mlx", ClusterJobs.Speaking, "F5 voice cloning on a Mac (MLX)",
        [
            Planned(Mac, Host, "IO11", "", AppleSilicon),
            Impossible(Win, Host, "MLX runs only on Apple silicon"), Impossible(Linux, Host, "MLX runs only on Apple silicon")
        ]),
        new("windows-voices", ClusterJobs.Speaking, "Windows voices",
        [
            Planned(Win, You, "PL02", "they can be saved in Setup, but conversations don't use them yet"),
            Impossible(Mac, You, "Windows voices exist only on Windows"), Impossible(Ios, You, "Windows voices exist only on Windows"),
            Impossible(Android, You, "Windows voices exist only on Windows")
        ]),
        new("apple-voices", ClusterJobs.Speaking, "Apple voices",
        [
            Planned(Ios, You, "IO05"), Planned(Ios, Host, "IO04", "served to your other computers"),
            Planned(Mac, You, MacPlan), Planned(Mac, Host, MacPlan),
            .. AppleOnly("Apple voices", You, Host)
        ]),
        new("personal-voice", ClusterJobs.Speaking, "Your Personal Voice (Apple)",
        [
            Planned(Ios, You, "IO05", "after you allow Martlet to use it", new() { MinimumOs = new(17, 0) }),
            Planned(Ios, Host, "IO04", "after you allow it and confirm on the iPhone", new() { MinimumOs = new(17, 0) }),
            Planned(Mac, You, MacPlan, "after you allow Martlet to use it", new() { MinimumOs = new(14, 0) }),
            .. AppleOnly("Personal Voice", You)
        ]),
        new("android-tts", ClusterJobs.Speaking, "Android voices",
        [
            Planned(Android, You, AndroidPlan), Planned(Android, Host, AndroidPlan, "served to your other computers"),
            Impossible(Win, You, "Android voices exist only on Android"), Impossible(Ios, You, "Android voices exist only on Android")
        ]),

        // ---- lip-sync ----
        new("audio2face", ClusterJobs.LipSync, "Audio2Face",
        [
            Works(Linux, Host, "", Nvidia4), Works(Win, Host, "through Docker Desktop (This PC's host service)", Nvidia4),
            .. NvidiaOnly("Audio2Face")
        ]),
        new("loudness-lipsync", ClusterJobs.LipSync, "Mouth follows the voice's loudness",
        [
            Works(Win, You), Planned(Mac, You, MacPlan), Planned(Ios, You, "IO07"), Planned(Android, You, AndroidPlan)
        ]),

        // ---- features of the device you talk to, and of hosts ----
        new("character-overlay", Feature, "Character over other windows and games",
        [
            Works(Win, You, "a transparent always-on-top window"),
            Planned(Mac, You, MacPlan, "a floating window above other apps"),
            Planned(Ios, You, "IO08", "inside Martlet and beside a game on iPad; over a full-screen game only through Picture-in-Picture (experimental)"),
            Planned(Android, You, AndroidPlan, "needs the 'Display over other apps' permission"),
            NotPlanned(Linux, You, NoLinuxApp)
        ]),
        new("screen-watch", Feature, "Watch my screen (game commentary)",
        [
            Works(Win, You, "borderless or windowed games; protected video reads back black"),
            Planned(Mac, You, MacPlan, "needs the Screen Recording permission"),
            Planned(Ios, You, "IO06", "only while a screen broadcast you start yourself is running"),
            Planned(Android, You, AndroidPlan, "asks for screen-capture permission each session"),
            NotPlanned(Linux, You, NoLinuxApp)
        ]),
        new("hands-free", Feature, "Hands-free listening",
        [
            Works(Win, You), Planned(Mac, You, MacPlan),
            Planned(Ios, You, "IO05", "keeps listening behind a game only while listening is on"),
            Planned(Android, You, AndroidPlan, "keeps listening behind a game with a notification showing"),
            NotPlanned(Linux, You, NoLinuxApp)
        ]),
        new("voice-id", Feature, "Voice ID (only respond to my voice)",
        [
            Works(Win, You), Planned(Mac, You, MacPlan), Planned(Ios, You, "IO09"), Planned(Android, You, AndroidPlan)
        ]),
        new("memory", Feature, "Local memory",
        [
            Works(Win, You), Planned(Mac, You, MacPlan), Planned(Ios, You, "IO09"), Planned(Android, You, AndroidPlan)
        ]),
        new("satellite", Feature, "Microphone and speaker for another computer's companion",
        [
            Planned(Ios, Host, "IO10"), Planned(Android, Host, AndroidPlan), Planned(Mac, Host, MacPlan),
            NotPlanned(Win, Host, "a Windows PC runs the full companion instead"),
            NotPlanned(Linux, Host, "Linux hosts have no audio role")
        ]),
        new("host-service", Feature, "Host jobs for your other computers",
        [
            Works(Linux, Host, "Docker or native Ubuntu; runs in the background"),
            Works(Win, Host, "through Docker Desktop and WSL 2"),
            Planned(Mac, Host, MacPlan, "runs in the background"),
            Planned(Ios, Host, "IO03", "only while Martlet is open on the screen; it stops when the app goes to the background"),
            Planned(Android, Host, AndroidPlan, "runs in the background with a notification showing")
        ]),
        new("remote-roles", Feature, "Install and remove host roles from your desktop",
        [
            Works(Linux, Host, "over SSH, or in a console on the host"), Works(Win, Host, "This PC's Docker Desktop"),
            Planned(Mac, Host, MacPlan),
            NotPlanned(Ios, Host, "roles are switched on in Martlet on the iPhone or iPad"),
            NotPlanned(Android, Host, "roles are switched on in Martlet on the phone or tablet")
        ])
    ];

    /// <summary>The engine a Martlet host role kind installs (deploy/host/roles).</summary>
    public static string? EngineForHostRole(string roleKind) => roleKind switch
    {
        "ollama" => "ollama",
        "stt" => "whisper",
        "f5" => "f5",
        "audio2face" => "audio2face",
        _ => null
    };

    public static PlatformEngine Engine(string id) => Engines.FirstOrDefault(e => e.Id == id) ??
        throw new ArgumentOutOfRangeException(nameof(id), id, "Unknown platform engine.");

    public static IEnumerable<PlatformEngine> ForJob(string job) => Engines.Where(e => e.Job == job);

    /// <summary>Whether <paramref name="device"/> can run <paramref name="engineId"/> on <paramref name="side"/>, and why not.</summary>
    public static PlatformCheck Check(string engineId, PlatformSide side, PlatformDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        var engine = Engine(engineId);
        var where = side == PlatformSide.Host ? $"{device.Name} ({Name(device.Platform)} host)" : $"Martlet for {Name(device.Platform)}";
        var support = engine.On(device.Platform, side);
        if (support is null) return new(PlatformVerdict.No, $"{engine.Name} isn't available on {where}.");
        return support.Availability switch
        {
            PlatformAvailability.Impossible => new(PlatformVerdict.No, $"{engine.Name} can't run on {where}: {support.Note}."),
            PlatformAvailability.NotPlanned => new(PlatformVerdict.No, $"{engine.Name} isn't offered on {where}: {support.Note}."),
            PlatformAvailability.Planned => new(PlatformVerdict.NotYet,
                $"{engine.Name} on {where} is planned ({SliceText(support.Slice)}), not built yet."),
            _ => support.Requires?.Check(device, engine.Name) ?? new(PlatformVerdict.Yes, support.Note)
        };
    }

    /// <summary>Plain-language notes about hosting on a device, such as an iPhone that hosts only while Martlet is open.</summary>
    public static IEnumerable<string> HostNotes(PlatformDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (device.ForegroundOnly)
            yield return $"{device.Name} hosts only while Martlet is open on its screen. Keep it on a charger with the app open, " +
                "and turn on failover for its jobs.";
        if (device.Features?.Contains(PlatformFeatures.OnBattery) == true)
            yield return $"{device.Name} is running on battery; it may slow down or stop hosting when the battery is low.";
        if (Engine("remote-roles").On(device.Platform, PlatformSide.Host) is { Availability: PlatformAvailability.NotPlanned } remote)
            yield return $"Martlet can't install or remove its roles from here: {remote.Note}.";
    }

    /// <summary>Whether this desktop can install and remove a host's roles (Linux and Docker hosts), as opposed to a phone
    /// or tablet whose roles are switched on on the device.</summary>
    public static bool ManagesRolesRemotely(PlatformDevice device) =>
        Engine("remote-roles").On(device.Platform, PlatformSide.Host)?.Availability == PlatformAvailability.Works;

    public static string Name(DevicePlatform platform) => platform switch
    {
        DevicePlatform.Windows => "Windows",
        DevicePlatform.Linux => "Linux",
        DevicePlatform.MacOs => "macOS",
        DevicePlatform.Ios => "iOS/iPadOS",
        _ => "Android"
    };

    public static DevicePlatform? ParsePlatform(string? value) => value switch
    {
        "windows" => DevicePlatform.Windows,
        "linux" => DevicePlatform.Linux,
        "macos" => DevicePlatform.MacOs,
        "ios" or "ipados" => DevicePlatform.Ios,
        "android" => DevicePlatform.Android,
        _ => null
    };

    public static Version? ParseVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var digits = new string(value.Trim().TakeWhile(c => char.IsAsciiDigit(c) || c == '.').ToArray()).TrimEnd('.');
        if (digits.Length == 0) return null;
        if (!digits.Contains('.', StringComparison.Ordinal)) digits += ".0";
        return Version.TryParse(digits, out var version) ? version : null;
    }

    private static string SliceText(string? slice) => slice switch
    {
        null or "" => "a later slice",
        IosPlan => "see the iOS plan",
        MacPlan => "see the macOS plan",
        AndroidPlan => "see the Android plan",
        _ => slice
    };
}
