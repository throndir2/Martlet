using System.Globalization;

namespace Martlet.Core.Planning;

/// <summary>One fact about an option, for comparing options side by side: <see cref="Key"/> is stable ("runs-on", "needs",
/// "vram", "ram", "cpu", "download", "speed", "quality", "hears", "sees", "cost", "evidence"), <see cref="Label"/> and
/// <see cref="Value"/> are words ("Graphics memory", "about 3.7 GB, up to 4.2 GB"), <see cref="Short"/> is the few words a
/// compact row shows ("4.2 GB VRAM"), null when the fact isn't worth a compact row, and <see cref="Help"/> says what it means.</summary>
public sealed record OptionFact(string Key, string Label, string Value, string? Short = null, string? Help = null);

/// <summary>The facts every option list in Martlet shows, from the footprint catalog (<see cref="ComponentOption"/>): where it
/// runs (graphics card, processor, inside Martlet, with Thinking's own model or online), what a host role needs (Docker, or
/// Linux), the graphics memory, memory, processor threads and download it takes, how soon it answers (named for its part: a
/// first sentence for Thinking, first audio for the voice, a transcript for listening, a read for Reading, a description for
/// Vision and Hearing), how good it is within its part, whether a Thinking model hears or sees, its cost when online, and
/// whether the numbers were measured. Pure: one option's facts in the same words on every page.</summary>
public static class OptionFacts
{
    /// <summary>Every fact about <paramref name="option"/>, in the order a details panel shows them.</summary>
    public static IReadOnlyList<OptionFact> Of(ComponentOption option)
    {
        ArgumentNullException.ThrowIfNull(option);
        var facts = new List<OptionFact> { RunsOn(option) };
        if (Needs(option) is { } needs) facts.Add(needs);
        if (option.IsLocal)
        {
            if (option.UsesGpu)
            {
                var usual = option.Usual.VramGb;
                var most = option.GpuGb;
                facts.Add(new("vram", "Graphics memory",
                    (most >= usual + 0.1 ? $"about {Gb(usual)} GB, up to {Gb(most)} GB" : $"about {Gb(most)} GB") +
                    (option.MinGpuGb > 0 ? $" (needs a {Gb(option.MinGpuGb)} GB+ card)" : ""),
                    $"{Gb(most)} GB VRAM", "Memory on the graphics card it holds while it runs; the most it takes while it works hardest."));
            }
            if (option.Peak.RamGb > 0)
                facts.Add(new("ram", "Memory", $"about {Gb(option.Peak.RamGb)} GB", option.UsesGpu ? null : $"{Gb(option.Peak.RamGb)} GB RAM",
                    "Main memory (RAM) it takes at most."));
            if (option.Steady.CpuThreads > 0)
                facts.Add(new("cpu", "Processor", Threads(option.Steady.CpuThreads) + (option.Peak.CpuThreads > option.Steady.CpuThreads + 0.05
                        ? $", up to {Threads(option.Peak.CpuThreads)}" : ""),
                    option.UsesGpu || option.Steady.CpuThreads < 2 ? null : $"{Threads(option.Steady.CpuThreads)}",
                    "Processor threads it keeps busy while it works."));
            if (option.Peak.DiskGb > 0)
                facts.Add(new("download", "Download", $"about {Gb(option.Peak.DiskGb)} GB", null, "What it downloads and installs once."));
        }
        if (option.FirstWordMs is { } ms)
            facts.Add(new("speed", SpeedLabel(option.Component), $"about {Seconds(ms)}", $"{Seconds(ms)} {SpeedShort(option.Component)}",
                SpeedHelp(option.Component)));
        facts.Add(new("quality", "Quality", option.UsesThinking ? "the same as your Thinking model" : QualityWord(option.QualityTier), null,
            "How good it is compared with the other choices for the same part, from 1 (basic) to 5 (best)."));
        if (option.Component is PlanComponent.Thinking or PlanComponent.DeepThinking)
        {
            facts.Add(new("hears", "Hears your voice", option.HearsAudio ? "yes: it takes your recording itself" : "no: it gets the transcript",
                option.HearsAudio ? "hears" : null, "A model that hears needs no speech-to-text before it answers."));
            facts.Add(new("sees", "Sees pictures", option.SeesImages ? "yes" : "no", option.SeesImages ? "sees" : null,
                "Whether it looks at your screen or camera itself."));
        }
        if (!option.IsLocal)
            facts.Add(new("cost", "Cost", option.FreeTier ? "free tier" + (option.NeedsSignup ? ", needs a free account and key" : "")
                    : option.NeedsSignup ? "paid, needs an account and key" : "paid",
                option.FreeTier ? "free" : "paid", "What it costs to use online."));
        facts.Add(new("evidence", "Numbers", option.Evidence switch
        {
            FootprintEvidence.Measured => "measured on Martlet hardware",
            FootprintEvidence.Sourced => "from the model's own documentation",
            _ => "estimated"
        }, null, option.Source.Length > 0 ? option.Source : null));
        return facts;
    }

    /// <summary>The few facts a compact row shows, joined: "Graphics card · 4.2 GB VRAM · 0.45 s to first audio".</summary>
    public static string Short(ComponentOption option, int most = 3)
    {
        ArgumentNullException.ThrowIfNull(option);
        return string.Join(" \u00b7 ", Of(option).Select(f => f.Short).OfType<string>().Take(Math.Max(1, most)));
    }

    /// <summary>The facts that differ between <paramref name="options"/>, in each option's order: what a compare table shows
    /// (a fact every option has the same is left out). Keys in the order they first appear.</summary>
    public static IReadOnlyList<string> CompareKeys(IEnumerable<ComponentOption> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var all = options.Select(Of).ToList();
        var keys = all.SelectMany(f => f.Select(x => x.Key)).Distinct(StringComparer.Ordinal).ToList();
        return keys.Where(key =>
        {
            var values = all.Select(f => f.FirstOrDefault(x => x.Key == key)?.Value ?? "").Distinct(StringComparer.Ordinal).Count();
            return values > 1;
        }).ToList();
    }

    private static OptionFact RunsOn(ComponentOption option)
    {
        if (!option.IsLocal) return new("runs-on", "Runs on", "online: nothing runs on your computers", "Online");
        if (option.UsesThinking)
            return new("runs-on", "Runs on", "with Thinking: Thinking's own model does it, so nothing more runs", "With Thinking");
        if (option.RunsInApp && !option.UsesGpu)
            return new("runs-on", "Runs on", "the processor, inside Martlet on the PC you talk to", "Processor (in Martlet)");
        return option.Gpu switch
        {
            GpuRequirement.Nvidia => new("runs-on", "Runs on", "an NVIDIA graphics card", "NVIDIA GPU"),
            GpuRequirement.AnyGpu => new("runs-on", "Runs on", "a graphics card (NVIDIA, AMD, Intel or Apple)", "GPU"),
            _ => new("runs-on", "Runs on", "the processor: no graphics card needed", "Processor")
        };
    }

    /// <summary>What a host role needs on the computer that runs it: Docker, and on Linux only for a role such as Home
    /// Assistant (Docker Engine with the computer's own network). Null for other options.</summary>
    private static OptionFact? Needs(ComponentOption option)
    {
        if (!option.IsLocal || option.HostRoleKind is null) return null;
        var linuxOnly = option.Platforms is { Count: 1 } only && string.Equals(only[0], "linux", StringComparison.OrdinalIgnoreCase);
        return linuxOnly
            ? new("needs", "Needs", "a Linux computer with Docker Engine and Martlet's host service", "Linux",
                "What the computer that runs it must have.")
            : new("needs", "Needs", "Martlet's host service in Docker, on Windows or Linux", null, "What the computer that runs it must have.");
    }

    private static string SpeedLabel(PlanComponent component) => component switch
    {
        PlanComponent.Thinking or PlanComponent.DeepThinking => "First sentence",
        PlanComponent.Voice => "First audio",
        PlanComponent.Listening => "Transcript",
        PlanComponent.Reading => "Read time",
        PlanComponent.Vision or PlanComponent.Hearing => "Description",
        _ => "Response"
    };

    private static string SpeedShort(PlanComponent component) => component switch
    {
        PlanComponent.Thinking or PlanComponent.DeepThinking => "to first sentence",
        PlanComponent.Voice => "to first audio",
        PlanComponent.Listening => "to transcript",
        PlanComponent.Reading => "to read the screen",
        PlanComponent.Vision or PlanComponent.Hearing => "to describe",
        _ => "to respond"
    };

    private static string SpeedHelp(PlanComponent component) => component switch
    {
        PlanComponent.Thinking or PlanComponent.DeepThinking => "About how long until the model writes its first sentence.",
        PlanComponent.Voice => "About how long from a sentence to the first audio you hear.",
        PlanComponent.Listening => "About how long from the end of your speech to the transcript.",
        PlanComponent.Reading => "About how long it takes to read the text of one screenshot. A reply never waits for it.",
        PlanComponent.Vision => "About how long it takes to put one picture into words. A reply never waits for it.",
        PlanComponent.Hearing => "About how long it takes to describe how you sounded. A reply never waits for it.",
        _ => "About how long until it responds."
    };

    /// <summary>"basic", "good", "very good", "excellent" or "best" for tiers 1 to 5.</summary>
    public static string QualityWord(int tier) => Math.Clamp(tier, 1, 5) switch
    {
        1 => "basic",
        2 => "good",
        3 => "very good",
        4 => "excellent",
        _ => "best"
    };

    private static string Threads(double threads) =>
        $"{threads.ToString(threads % 1 == 0 ? "0" : "0.#", CultureInfo.InvariantCulture)} thread{(Math.Abs(threads - 1) < 0.05 ? "" : "s")}";

    private static string Gb(double gb) => gb.ToString("0.#", CultureInfo.InvariantCulture);

    private static string Seconds(int ms) => (ms / 1000d).ToString(ms < 1000 ? "0.##" : "0.#", CultureInfo.InvariantCulture) + " s";
}
