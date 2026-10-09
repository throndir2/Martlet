using System.Globalization;

namespace Martlet.Core.Planning;

/// <summary>One fact about an option, for comparing options side by side: <see cref="Key"/> is stable ("runs-on", "needs",
/// "vram", "ram", "cpu", "download", "speed", "quality", "hears", "sees", "cost", "evidence"; for one part's options also
/// "fits", "tools", "context", "cloning", "sounds", "emotions", "streams", "languages", "samples", "license", "accuracy",
/// "key", "data", "reliability"), <see cref="Label"/> and
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

    /// <summary>Moves the facts with <paramref name="keys"/> to the front, in that order, and keeps the others in their order:
    /// a compact row shows the first three short facts, so the facts that tell a part's options apart lead.</summary>
    public static IReadOnlyList<OptionFact> Lead(IEnumerable<OptionFact> facts, params string[] keys)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var list = facts.ToList();
        var first = keys.Select(key => list.FirstOrDefault(f => f.Key == key)).OfType<OptionFact>().ToList();
        return [.. first, .. list.Where(f => !first.Contains(f))];
    }

    /// <summary>A Thinking model's facts: <see cref="Of"/> plus whether it calls tools, the context its graphics memory is for,
    /// and whether it fits this PC's graphics card (<paramref name="cardGb"/>, null when there is none) beside a game and
    /// Martlet's character (<paramref name="comfortableGb"/>: the card it needs for that, 0 for any PC). Its fit, memory,
    /// speed and hearing lead, since every Ollama model runs on a graphics card.</summary>
    public static IReadOnlyList<OptionFact> ThinkingModel(ComponentOption option, double? cardGb, double comfortableGb, bool? callsTools)
    {
        ArgumentNullException.ThrowIfNull(option);
        // A model that doesn't hear says so in a compact row too: its replies wait for the transcript.
        // Ollama runs natively on this PC, so the host service's Docker need is left out.
        // Short values, so most facts sit two to a line in the details.
        var facts = Of(option).Where(f => f.Key is not ("evidence" or "needs"))
            .Select(f => f.Key switch
            {
                "hears" => f with { Value = option.HearsAudio ? "yes, your recording itself" : "no, the transcript", Short = f.Short ?? "transcript" },
                "runs-on" when option.Gpu == GpuRequirement.AnyGpu => f with { Value = "a graphics card of any brand" },
                _ => f
            }).ToList();
        if (callsTools is { } tools)
            facts.Add(new("tools", "Calls tools", tools ? "yes" : "no", null,
                "Whether it can call tools (smart home, MCP servers) while it answers."));
        if (option.IsLocal)
            facts.Add(new("context", "Context", "8,192 tokens, as measured", null,
                "How much of the conversation it reads at once; the graphics memory above is for this much. A longer context takes " +
                "more graphics memory. Test model shows what Ollama gives it."));
        if (option.IsLocal && option.UsesGpu) facts.Add(Fits(option, cardGb, comfortableGb));
        facts.Add(Of(option).Single(f => f.Key == "evidence"));
        return Lead(facts, "fits", "vram", "speed", "hears");
    }

    /// <summary>Whether a graphics-card option fits this PC's card (<paramref name="cardGb"/>) with room for a game and
    /// Martlet's character (<paramref name="comfortableGb"/>: the card it needs for that, 0 for any card). Short only when it
    /// doesn't fit comfortably, so a compact row warns.</summary>
    public static OptionFact Fits(ComponentOption option, double? cardGb, double comfortableGb)
    {
        ArgumentNullException.ThrowIfNull(option);
        const string help = "Whether this PC's graphics card holds it with room left for a game and Martlet's character.";
        if (cardGb is not { } card)
            return new("fits", "Fits this PC", "no card: the processor, slowly", "no card here",
                help + " Without a graphics card it runs on the processor, much slower.");
        // A "12 GB" card reports a little less.
        if (comfortableGb <= card + 0.5)
            return new("fits", "Fits this PC", $"yes, with room to spare", null, help + $" This PC's card has {Gb(card)} GB.");
        return option.GpuGb <= card
            ? new("fits", "Fits this PC", "only just: little room left", "tight here",
                help + $" It fits this PC's {Gb(card)} GB card, with little room left for a game or the character.")
            : new("fits", "Fits this PC", $"no: it takes {Gb(option.GpuGb)} GB", "too big here",
                help + $" This PC's card has {Gb(card)} GB, so part of it runs on the processor, much slower.");
    }

    /// <summary>A voice's facts: <see cref="Of"/> its footprint (null: a cloud voice, <paramref name="cloud"/> names its
    /// provider), then what it can do (voice cloning, laughs and sighs, emotions), whether it streams, its languages, the
    /// recordings it learns from and its license, and whether that allows commercial use.</summary>
    public static IReadOnlyList<OptionFact> Voice(ComponentOption? footprint, Settings.VoiceAbilities abilities, string languages,
        bool streams, string? samples, string license, bool nonCommercial, string? cloud = null, bool alsoProcessor = false)
    {
        ArgumentNullException.ThrowIfNull(abilities);
        List<OptionFact> facts = footprint is null
            ? [new("runs-on", "Runs on", $"online: {cloud ?? "the provider"}'s servers; nothing runs on your computers", "Online")]
            : [.. Of(footprint).Where(f => f.Key is not ("evidence" or "cost"))];
        if (alsoProcessor && facts.FindIndex(f => f.Key == "runs-on") is var at and >= 0)
            facts[at] = new("runs-on", "Runs on", facts[at].Value + ", or the processor on a computer without one",
                facts[at].Short + " or processor");
        foreach (var item in abilities.Items)
        {
            var (key, label) = item.Name switch
            {
                "Voice cloning" => ("cloning", "Voice cloning"),
                "Laughs & sighs" => ("sounds", "Laughs and sighs"),
                _ => ("emotions", "Emotions")
            };
            var value = item.Level switch
            {
                Settings.AbilityLevel.Yes => "yes",
                Settings.AbilityLevel.No => "no",
                _ => item.Note ?? "partly"
            };
            facts.Add(new(key, label, value, key == "sounds" && item.Level == Settings.AbilityLevel.Yes ? "laughs" : null, item.Help));
        }
        facts.Add(new("streams", "Streaming", streams ? "yes: it starts speaking before a sentence is finished" : "no: it speaks each sentence once it is made",
            null, "A voice that streams speaks sooner."));
        facts.Add(new("languages", "Languages", languages));
        if (samples is not null) facts.Add(new("samples", "Your recordings", samples, null, "The recordings it copies a voice from."));
        facts.Add(new("license", "License", cloud is not null ? license : license + (nonCommercial ? ": non-commercial use only" : ": commercial use allowed"),
            null, "The model's terms."));
        if (footprint is null || !footprint.IsLocal)
            facts.Add(new("cost", "Cost", "paid, with your own account and API key", "paid", "What it costs to use online."));
        if (footprint is not null) facts.Add(Of(footprint).Single(f => f.Key == "evidence"));
        return facts;
    }

    /// <summary>A voice engine's facts (<see cref="Voice"/>), from its footprint and the engine's own data.</summary>
    public static IReadOnlyList<OptionFact> VoiceEngine(Settings.SpeechEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        var footprint = engine.Cloud ? null : engine.Footprint;
        var samples = (engine.SampleLimits ?? "1-30 s") + (engine.MultipleReferences ? "; learns from each of several" : "");
        return Voice(footprint, engine.Abilities, engine.Languages, engine.StreamsWhileGenerating, engine.Abilities.Cloning ? samples : null,
            engine.WeightsLicense, engine.NonCommercial, engine.Cloud ? engine.Name : null, alsoProcessor: !engine.NeedsGpu && !engine.Cloud);
    }

    /// <summary>A speech recognizer's facts: <see cref="Of"/> plus the languages it understands, how accurate it is and
    /// whether it streams. Where it runs, how soon the transcript comes and its languages lead.</summary>
    public static IReadOnlyList<OptionFact> SpeechRecognizer(ComponentOption option, string languages, string accuracy, bool streams)
    {
        ArgumentNullException.ThrowIfNull(option);
        var facts = Of(option).Where(f => f.Key != "evidence").ToList();
        facts.Add(new("languages", "Languages", languages, languages, "The languages it writes down."));
        facts.Add(new("accuracy", "Accuracy", accuracy, null, "How well it hears you, compared with the other choices."));
        facts.Add(new("streams", "Streaming", streams ? "yes: it writes your words down while you speak" : "no: it writes your words down once you pause",
            null, "Whether the transcript grows while you speak."));
        facts.Add(Of(option).Single(f => f.Key == "evidence"));
        return Lead(facts, "runs-on", "speed", "languages");
    }

    /// <summary>A cloud provider's or server's facts, in values short enough to sit two to a line: where it runs, what it costs,
    /// whether it needs a key, that your data goes to it (<paramref name="sent"/>, what <paramref name="name"/> gets, is in the
    /// fact's help: "your messages and recent conversation"), and from <paramref name="option"/> (null when
    /// Martlet has no numbers for it) how soon it answers, its quality, hearing and seeing and how reliable it is.
    /// <paramref name="hears"/> and <paramref name="sees"/> fill in for a provider without an option. Online, its cost and
    /// its speed lead.</summary>
    public static IReadOnlyList<OptionFact> Hosted(string name, ComponentOption? option, bool? free, bool keyNeeded, string sent,
        bool? hears = null, bool? sees = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        // Short values, so most facts sit two to a line in the details.
        List<OptionFact> facts = option is null
            ? [new("runs-on", "Runs on", "online, not on your computers", "Online")]
            : [.. Of(option).Where(f => f.Key is not ("cost" or "evidence"))
                .Select(f => f.Key switch
                {
                    "runs-on" => f with { Value = "online, not on your computers" },
                    "hears" => f with { Value = option.HearsAudio ? "yes, your recording itself" : "no, the transcript" },
                    _ => f
                })];
        if (option is null && hears is { } hearing)
            facts.Add(new("hears", "Hears your voice", hearing ? "yes, your recording itself" : "no, the transcript", hearing ? "hears" : null,
                "A model that hears needs no speech-to-text before it answers."));
        if (option is null && sees is { } seeing) facts.Add(new("sees", "Sees pictures", seeing ? "yes" : "no", seeing ? "sees" : null));
        facts.Add(new("cost", "Cost", free switch
        {
            true => "free within its limits",
            false => "paid per request",
            _ => "depends on the server"
        }, free switch { true => "free tier", false => "paid", _ => null }, "What it costs to use."));
        facts.Add(new("key", "API key", keyNeeded ? "your own key" : "only if the server asks", null,
            "What you need before it works. Martlet keeps a key in Windows Credential Manager."));
        if (option is { Reliability: not OptionReliability.High })
            facts.Add(new("reliability", "Reliability", "may limit or retire models", null,
                "Free endpoints limit requests, go down and retire models now and then."));
        facts.Add(new("data", "Your data", $"sent to {name}", null,
            $"{name} gets {sent}, only when you talk or start something; saving settings sends nothing."));
        return Lead(facts, "runs-on", "cost", "speed");
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
