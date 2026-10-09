namespace Martlet.Core.Planning;

/// <summary>Local: runs on a computer in the user's Martlet network. External: a hosted provider does it.</summary>
public enum OptionHosting { Local, External }

/// <summary>None: runs on the processor (uses no graphics card). AnyGpu: any vendor's card (Ollama on NVIDIA, AMD, Intel or
/// Apple). Nvidia: needs an NVIDIA card with CUDA.</summary>
public enum GpuRequirement { None, AnyGpu, Nvidia }

/// <summary>How a footprint number is known: measured on Martlet hardware, sourced from the vendor/model card, or estimated.</summary>
public enum FootprintEvidence { Estimate, Sourced, Measured }

/// <summary>How dependable an option is: free hosted endpoints rate-limit, retire models and go down (Low/Medium);
/// local options and paid providers are High.</summary>
public enum OptionReliability { Low, Medium, High }

/// <summary>Resources an option uses on one machine. <see cref="DiskGb"/> is its download and install size.</summary>
public sealed record ResourceUse(double VramGb = 0, double RamGb = 0, double CpuThreads = 0, double DiskGb = 0)
{
    public static ResourceUse Zero { get; } = new();
    public static ResourceUse operator +(ResourceUse a, ResourceUse b) =>
        new(a.VramGb + b.VramGb, a.RamGb + b.RamGb, a.CpuThreads + b.CpuThreads, a.DiskGb + b.DiskGb);
}

/// <summary>One way to do a component: a model or engine, local or hosted, with what it costs and how good it is. Catalog
/// data lives in FootprintCatalog.Seed.cs (docs/RESOURCE_FOOTPRINTS.md explains the numbers). The engine packs machines
/// with <see cref="Peak"/> memory (VRAM plus <see cref="ContextGb"/>, RAM) and <see cref="Steady"/> processor threads.</summary>
public sealed record ComponentOption
{
    /// <summary>Unique across the catalog, e.g. "gemma4:e2b", "chatterbox-turbo", "hosted:nvidia-build".</summary>
    public required string Id { get; init; }
    public required PlanComponent Component { get; init; }
    public required string DisplayName { get; init; }
    public OptionHosting Hosting { get; init; } = OptionHosting.Local;
    public GpuRequirement Gpu { get; init; } = GpuRequirement.None;
    /// <summary>The smallest card (total memory) it runs on at all.</summary>
    public double MinGpuGb { get; init; }
    /// <summary>Whether it can share a graphics card with other work (false: it wants a card to itself).</summary>
    public bool CanShareGpu { get; init; } = true;
    /// <summary>Typical use while it works (resident memory, busy processor threads).</summary>
    public ResourceUse Steady { get; init; } = ResourceUse.Zero;
    /// <summary>The most it takes at once (what the engine reserves for memory).</summary>
    public ResourceUse Peak { get; init; } = ResourceUse.Zero;
    /// <summary>Processor threads it keeps busy while idle.</summary>
    public double IdleCpuThreads { get; init; }
    /// <summary>For language models: graphics memory one conversation's context (KV cache) takes at Martlet's context
    /// setting, on top of <see cref="Peak"/>.VramGb.</summary>
    public double ContextGb { get; init; }
    /// <summary>1 (basic) to 5 (best) within its component; higher is an upgrade.</summary>
    public int QualityTier { get; init; } = 1;
    /// <summary>About how long until the first word (Thinking: first sentence; Voice: first audio; Listening: transcript;
    /// Reading: the text of one screenshot), or null.</summary>
    public int? FirstWordMs { get; init; }
    /// <summary>Thinking: hears recordings itself (an omni model), so replies need no transcript first.</summary>
    public bool HearsAudio { get; init; }
    public bool SeesImages { get; init; }
    /// <summary>Runs inside the Martlet app, so only on the PC the user talks to (Parakeet, the character).</summary>
    public bool RunsInApp { get; init; }
    /// <summary>Vision and Hearing: Thinking's own model takes the pictures or recordings (it sees or hears itself), so the
    /// option takes no resources of its own and runs where Thinking runs (docs/SENSE_MODELS.md).</summary>
    public bool UsesThinking { get; init; }
    /// <summary>Platforms it runs on (windows, linux, macos); null means any.</summary>
    public IReadOnlyList<string>? Platforms { get; init; }
    public string? ModelId { get; init; }
    /// <summary>The Martlet host role that runs it (HostRoles kinds such as "ollama", "chatterbox"), or null.</summary>
    public string? HostRoleKind { get; init; }
    /// <summary>External: the provider ("nvidia-build", "openrouter", "openai") the user must have configured or sign up for.</summary>
    public string? ProviderId { get; init; }
    /// <summary>External: usable without paying.</summary>
    public bool FreeTier { get; init; }
    /// <summary>External: needs a free account and API key before it works.</summary>
    public bool NeedsSignup { get; init; }
    public OptionReliability Reliability { get; init; } = OptionReliability.High;
    public FootprintEvidence Evidence { get; init; } = FootprintEvidence.Estimate;
    /// <summary>Where the numbers come from (doc link, measurement host).</summary>
    public string Source { get; init; } = "";

    public bool IsLocal => Hosting == OptionHosting.Local;
    /// <summary>Graphics memory reserved on a card: peak VRAM plus one context.</summary>
    public double GpuGb => Peak.VramGb + ContextGb;
    public bool UsesGpu => IsLocal && Gpu != GpuRequirement.None;

    /// <summary>What it reserves on the machine it is placed on.</summary>
    public ResourceUse Reserve => IsLocal ? new(GpuGb, Peak.RamGb, Steady.CpuThreads, Peak.DiskGb) : ResourceUse.Zero;

    /// <summary>What it usually holds where it is placed: <see cref="Steady"/> graphics memory plus one context and steady
    /// memory, never more than <see cref="Reserve"/>. The gap to <see cref="Reserve"/> is what it grows by while it works
    /// hardest. An option without steady numbers usually holds its peak.</summary>
    public ResourceUse Usual
    {
        get
        {
            if (!IsLocal) return ResourceUse.Zero;
            var steady = Steady == ResourceUse.Zero ? Peak : Steady;
            return new(Math.Min(steady.VramGb, Peak.VramGb) + ContextGb, Math.Min(steady.RamGb, Peak.RamGb), Steady.CpuThreads, Peak.DiskGb);
        }
    }

    public bool RunsOn(string? platform) =>
        Platforms is null || platform is null || Platforms.Contains(platform, StringComparer.OrdinalIgnoreCase);

    /// <summary>Where it runs and how much graphics memory it takes, as one sentence for an option's rundown: "Runs on an NVIDIA
    /// GPU: about 3.7 GB of graphics memory, up to 4.2 GB (6 GB+ card).", "Runs on the CPU: no graphics card needed." or
    /// "Runs online: nothing runs on your computers." The numbers are what it usually holds (<see cref="Usual"/>) and the most
    /// it takes (<see cref="GpuGb"/>), the same as the Devices page shows; <see cref="Evidence"/> says whether they were
    /// measured.</summary>
    public string WhereItRuns
    {
        get
        {
            if (!IsLocal) return "Runs online: nothing runs on your computers.";
            if (UsesThinking) return "Runs with Thinking: Thinking's own model does it, so it takes nothing more.";
            if (Gpu == GpuRequirement.None) return "Runs on the CPU: no graphics card needed.";
            var usual = Usual.VramGb;
            var memory = $"about {Gb(usual)} GB of graphics memory" + (GpuGb >= usual + 0.1 ? $", up to {Gb(GpuGb)} GB" : "");
            return $"Runs on {(Gpu == GpuRequirement.Nvidia ? "an NVIDIA GPU" : "a GPU")}: {memory}" +
                (MinGpuGb > 0 ? $" ({Gb(MinGpuGb)} GB+ card)." : ".");
        }
    }

    private static string Gb(double gb) => gb.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
}
