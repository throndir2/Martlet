using System.Globalization;
using Martlet.Conversation;
using Martlet.Core.Planning;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Desktop;

/// <summary>Companion's Optional extras pages (docs/UI_DESIGN.md, Optional extras): their order in the side list, what Off means on
/// each, and each page's main choice as an option list for the option picker, with the facts that compare its options (where it
/// runs, graphics memory, memory, processor, download, speed, quality, cost and where your data goes, its license, and facts of
/// its own kind). Pure: the pages add each option's details and button (<see cref="PickerOption.Details"/>,
/// <see cref="PickerOption.Action"/>). A fact comes from the footprint catalog when it has the option (<see cref="OptionFacts"/>),
/// else from here.</summary>
internal static class OptionalExtras
{
    /// <summary>The Optional extras pages in their order while the priority list doesn't rank a page yet.</summary>
    internal static readonly IReadOnlyList<CompanionTab> FallbackOrder =
    [
        CompanionTab.Vision, CompanionTab.Reading, CompanionTab.Hearing, CompanionTab.DeepThinking, CompanionTab.SmartHome,
        CompanionTab.Singing, CompanionTab.Pictures
    ];

    /// <summary>The part of the priority list (<see cref="ComponentRanking"/>) an Optional extras page sets, when the list has it.</summary>
    internal static PlanComponent? Component(CompanionTab tab) =>
        MainWindow.GroupOf(tab) == CompanionGroup.Extras && Enum.TryParse<PlanComponent>(tab.ToString(), out var component) &&
        Enum.IsDefined(component) && ComponentRanking.All.Any(info => info.Component == component)
            ? component : null;

    /// <summary>Where an Optional extras page goes in the side list: its rank in the priority list, or, for a page the list doesn't
    /// rank yet, just after the page before it in <see cref="FallbackOrder"/>.</summary>
    internal static double Order(CompanionTab tab)
    {
        if (Component(tab) is { } component) return ComponentRanking.Of(component).Rank;
        var at = FallbackOrder.ToList().IndexOf(tab);
        for (var before = at - 1; before >= 0; before--)
            if (Component(FallbackOrder[before]) is { } ranked) return ComponentRanking.Of(ranked).Rank + (at - before) * 0.01;
        return at < 0 ? 100 : at * 0.01;
    }

    /// <summary>What Off means on an Optional extras page, as a sentence without its full stop: the priority list's words when it
    /// ranks the page.</summary>
    internal static string OffMeans(CompanionTab tab)
    {
        if (Component(tab) is { } component && ComponentRanking.CanBeOff(component))
            return Capitalized(ComponentRanking.OffMeans(component));
        return tab switch
        {
            CompanionTab.Vision => "Martlet doesn't look at your screen or camera",
            CompanionTab.Reading => "Martlet doesn't read the text on your screen",
            CompanionTab.Hearing => "Martlet gets only the words you say, not how you say them",
            CompanionTab.DeepThinking => "Martlet doesn't think things over in the background",
            CompanionTab.SmartHome => "Martlet doesn't control your smart home",
            CompanionTab.Singing => "Martlet doesn't sing",
            CompanionTab.Pictures => "Martlet doesn't draw pictures",
            _ => "It doesn't run"
        };
    }

    /// <summary>The Off choice of an Optional extras page, with what Off means.</summary>
    internal static PickerOption Off(CompanionTab tab, bool inUse) => PickerOption.Off(OffMeans(tab), inUse);

    // ---------- Singing ----------

    internal const string SingingSoulX = "singing-acestep-soulx";
    internal const string SingingVevo = "singing-acestep-vevosing";

    /// <summary>Companion › Singing's main choice: Off, or Martlet's Singing role (on this PC or another of your computers).
    /// <paramref name="active"/>: Martlet sings (a computer runs Singing for this PC, and it isn't turned off); Off is in use
    /// otherwise. <paramref name="readySomewhere"/>: one of your computers runs Singing.</summary>
    internal static IReadOnlyList<PickerOption> SingingChoices(bool active, bool readySomewhere) =>
    [
        Off(CompanionTab.Singing, !active),
        new("Role", "Martlet's Singing role",
            "ACE-Step 1.5 writes the music and SoulX-Singer sings it in the voice Martlet speaks with, on an NVIDIA graphics card on " +
            "this PC or another of your computers. It frees the card when idle.")
        {
            InUse = active,
            Badge = active ? "in use" : readySomewhere ? "ready" : null,
            Facts = Facts(SingingSoulX, [],
                new("song", "Song time", "about 1 to 2 minutes for a 30-second song (measured on an RTX 4070)", "1-2 min a song",
                    "How long a song takes to make; it plays when it is ready, and the conversation goes on meanwhile."),
                new("needs", "Needs", "Docker and an NVIDIA graphics card with 6 GB or more, shared with the voice and listening", null),
                new("cost", "Cost", "free", "free"),
                new("data", "Your data", "the lyrics, the style and your voice's recording go to the computer that sings", null),
                new("license", "License", "ACE-Step 1.5 MIT, SoulX-Singer Apache-2.0: you may use songs as you like", null))
        }
    ];

    /// <summary>Singing's voice match (configuration): SoulX-Singer (set up with Singing) or VevoSing (added on request).</summary>
    internal static IReadOnlyList<PickerOption> VoiceMatches(Martlet.Core.Singing.SongVoiceMatch chosen) =>
    [
        new(nameof(Martlet.Core.Singing.SongVoiceMatch.SoulX), "SoulX-Singer",
            "Keeps the tune and the words. It is set up with Singing.")
        {
            InUse = chosen == Martlet.Core.Singing.SongVoiceMatch.SoulX,
            Badge = chosen == Martlet.Core.Singing.SongVoiceMatch.SoulX ? "in use" : "recommended",
            Facts = Facts(SingingSoulX, [],
                new("sound", "Sound", "keeps the tune and the words", "Keeps the tune"),
                new("song", "Song time", "about 1 minute for a 30-second song, about 1.5 minutes with a new worker", null),
                new("license", "License", "Apache-2.0: any use", "Any use"))
        },
        new(nameof(Martlet.Core.Singing.SongVoiceMatch.VevoSing), "VevoSing",
            "A little closer to the voice and cleaner, but it may drift off-key. Personal, non-commercial use only.")
        {
            InUse = chosen == Martlet.Core.Singing.SongVoiceMatch.VevoSing,
            Badge = chosen == Martlet.Core.Singing.SongVoiceMatch.VevoSing ? "in use" : null,
            Facts = Facts(SingingVevo, [],
                new("sound", "Sound", "closer to the voice and cleaner, but it may drift off-key", "Closer to the voice"),
                new("song", "Song time", "about 1 3/4 minutes for a 30-second song", null),
                new("license", "License", "CC-BY-NC-ND-4.0: personal, non-commercial use only; share nothing made from it", "Non-commercial"))
        }
    ];

    // ---------- Reading ----------

    internal const string ReadingWindowsOcr = "reading:windows-ocr";
    internal const string ReadingRapidOcr = "reading:rapidocr";
    internal const string ReadingPpOcrV5 = "reading:ppocrv5";
    internal const string ReadingPpOcrV5Cuda = "reading:ppocrv5-cuda";

    /// <summary>One model of Martlet's Reading role (deploy/host/roles/ocr/role.conf): its OCR_MODEL, the OCR_ENGINE that runs
    /// it, where it runs (martlet-host's accelerator; null for RapidOCR, which runs only on the processor), its name, its catalog
    /// option and its download.</summary>
    internal sealed record ReadingRoleModel(string Model, string Engine, string? Accelerator, string Name, string CatalogId, string Download)
    {
        /// <summary>It runs only on an NVIDIA graphics card.</summary>
        public bool NeedsNvidia => Accelerator == "gpu";

        /// <summary>martlet-host's answers that add (or change) the Reading role to run this model, with no questions.</summary>
        public IReadOnlyDictionary<string, string> Answers()
        {
            var answers = new Dictionary<string, string>(StringComparer.Ordinal) { ["choice.OCR_ENGINE"] = Engine, ["choice.OCR_MODEL"] = Model };
            if (Accelerator is not null) answers["choice.accelerator"] = Accelerator;
            return answers;
        }
    }

    /// <summary>The Reading role's models, the recommended first: PP-OCRv5 mobile on the processor (the most accurate that is fast
    /// there, and it leaves the graphics card to the voice and thinking), PP-OCRv5 server on an NVIDIA graphics card, and RapidOCR
    /// with PP-OCRv4 (the first Reading role, the smallest).</summary>
    internal static IReadOnlyList<ReadingRoleModel> ReadingModels { get; } =
    [
        new("ppocrv5-mobile", "ppocrv5", "cpu", "PP-OCRv5 mobile", ReadingPpOcrV5, "about 700 MB"),
        new("ppocrv5-server", "ppocrv5", "gpu", "PP-OCRv5 server", ReadingPpOcrV5Cuda, "about 3.1 GB"),
        new("rapidocr-ppocrv4", "rapidocr", null, "RapidOCR", ReadingRapidOcr, "about 200 MB")
    ];

    /// <summary>The Reading role's model named <paramref name="model"/> (a host's offer), or null when it isn't one.</summary>
    internal static ReadingRoleModel? ReadingModelOf(string? model) => ReadingModels.FirstOrDefault(m => m.Model == model);

    /// <summary>The facts of the Reading role running <paramref name="model"/>: the catalog's numbers first, then what only OCR has.</summary>
    private static IReadOnlyList<OptionFact> ReadingRoleFacts(ReadingRoleModel model) => model.Model switch
    {
        "ppocrv5-mobile" => Facts(model.CatalogId,
            [
                new("runs-on", "Runs on", "the processor of this PC or another of your computers, in Docker: no graphics card needed",
                    "Processor (Docker)"),
                new("speed", "Read time", "about 2 s for a 1920 x 1080 screenshot and 4 s for a 4K one (measured on a 24-thread processor)",
                    "2-4 s a read", "How long one screenshot takes to read. Reads run off to the side, so replies never wait for them.")
            ],
            new("download", "Download", "about 700 MB of packages and models", null),
            new("languages", "Languages", "Chinese, English and Japanese letters, digits and symbols (PaddleOCR PP-OCRv5)", null),
            new("accuracy", "Accuracy", "the best measured: all 192 small lines of a 4K screen (Windows OCR 145, RapidOCR 112)", null),
            new("game-fonts", "Game fonts", "good: often better than Windows OCR", null),
            new("cost", "Cost", "free", "free"),
            new("data", "Your data", "screenshots go to that computer; it reads them in memory and doesn't keep them", null),
            new("license", "License", "RapidOCR and PaddleOCR models Apache-2.0", null)),
        "ppocrv5-server" => Facts(model.CatalogId,
            [
                new("runs-on", "Runs on", "an NVIDIA graphics card of this PC or another of your computers, in Docker", "NVIDIA GPU (Docker)")
            ],
            new("download", "Download", "about 3.1 GB of packages and models, with NVIDIA's CUDA 13 and cuDNN libraries", null),
            new("driver", "Driver", "NVIDIA driver 580 or newer (CUDA 13); on a processor it takes about a minute a screenshot", null),
            new("languages", "Languages", "Chinese, English and Japanese letters, digits and symbols (PaddleOCR PP-OCRv5)", null),
            new("accuracy", "Accuracy", "the most accurate PP-OCRv5 model", null),
            new("game-fonts", "Game fonts", "good: often better than Windows OCR", null),
            new("cost", "Cost", "free", "free"),
            new("data", "Your data", "screenshots go to that computer; it reads them in memory and doesn't keep them", null),
            new("license", "License", "RapidOCR and PaddleOCR models Apache-2.0; CUDA and cuDNN NVIDIA's license", null)),
        _ => Facts(model.CatalogId,
            [
                new("runs-on", "Runs on", "the processor of this PC or another of your computers, in Docker: no graphics card needed",
                    "Processor (Docker)"),
                new("cpu", "Processor", "4 threads while it reads", "4 threads"),
                new("download", "Download", "about 200 MB of packages and models", null),
                new("speed", "Read time", "about 0.5 to 1 s a screenshot (0.9 s measured on a 24-thread processor)", "0.5-1 s a read",
                    "How long one screenshot takes to read. Reads run off to the side, so replies never wait for them.")
            ],
            new("languages", "Languages", "Chinese and English letters, digits and symbols (PaddleOCR PP-OCRv4)", null),
            new("game-fonts", "Game fonts", "good: often better than Windows OCR", null),
            new("cost", "Cost", "free", "free"),
            new("data", "Your data", "screenshots go to that computer; it reads them in memory and doesn't keep them", null),
            new("license", "License", "RapidOCR and PaddleOCR models Apache-2.0", null))
    };

    /// <summary>Companion › Reading's main choice, keyed by <see cref="Martlet.Core.Reading.ReadingPlace"/>: Off, Windows OCR on this
    /// PC or Martlet's Reading role. <paramref name="windowsReads"/>: whether Windows can read text here (null: not known yet).
    /// <paramref name="hostModel"/>: the Reading role's model the page shows (null: the recommended one); its facts are the role's.</summary>
    internal static IReadOnlyList<PickerOption> ReadingChoices(Martlet.Core.Reading.ReadingPlace saved, bool? windowsReads, string? hostModel = null) =>
    [
        Off(CompanionTab.Reading, saved == Martlet.Core.Reading.ReadingPlace.Off),
        new(nameof(Martlet.Core.Reading.ReadingPlace.ThisPc), "Windows OCR on this PC",
            "Built into Windows. It reads a screenshot on this PC's processor in a fraction of a second, nothing leaves this PC, " +
            "and it's free.")
        {
            InUse = saved == Martlet.Core.Reading.ReadingPlace.ThisPc,
            Badge = saved == Martlet.Core.Reading.ReadingPlace.ThisPc ? "in use" : "recommended",
            Unavailable = windowsReads == false ? "Windows has no language with text recognition here." : null,
            Facts = Facts(ReadingWindowsOcr,
                [
                    new("runs-on", "Runs on", "the processor of this PC, inside Windows: no graphics card needed", "Processor (Windows)"),
                    new("download", "Download", "none: it is built into Windows", "No download"),
                    new("speed", "Read time", "about 140 ms for a full-size 1920 x 1080 screenshot", "0.14 s a read",
                        "How long one screenshot takes to read. Reads run off to the side, so replies never wait for them.")
                ],
                new("download", "Download", "none: it is built into Windows", "No download"),
                new("languages", "Languages", "your Windows languages that include text recognition", null),
                new("game-fonts", "Game fonts", "fair: the Reading role is often better with stylized fonts", null),
                new("cost", "Cost", "free", "free"),
                new("data", "Your data", "nothing leaves this PC", null))
        },
        new(nameof(Martlet.Core.Reading.ReadingPlace.Host), "Martlet's Reading role",
            "PP-OCRv5 or RapidOCR in Docker, on this PC or another of your computers. The most accurate, also with game fonts and " +
            "small text on 4K screens.")
        {
            InUse = saved == Martlet.Core.Reading.ReadingPlace.Host,
            Badge = saved == Martlet.Core.Reading.ReadingPlace.Host ? "in use" : null,
            Facts = ReadingRoleFacts(ReadingModelOf(hostModel) ?? ReadingModels[0])
        }
    ];

    // ---------- facts ----------

    /// <summary>The catalog's facts about <paramref name="catalogId"/> (else <paramref name="fallback"/>), with
    /// <paramref name="extra"/> facts it doesn't have before its "Numbers" line.</summary>
    internal static IReadOnlyList<OptionFact> Facts(string catalogId, IReadOnlyList<OptionFact> fallback, params OptionFact[] extra)
    {
        var facts = (PlanningCatalog.Current.Find(catalogId) is { } option ? OptionFacts.Of(option) : fallback).ToList();
        foreach (var fact in extra)
        {
            if (facts.Any(f => f.Key == fact.Key)) continue;
            var evidence = facts.FindIndex(f => f.Key == "evidence");
            facts.Insert(evidence < 0 ? facts.Count : evidence, fact);
        }
        return facts;
    }

    private static string Gb(double gb) => gb.ToString("0.#", CultureInfo.InvariantCulture);

    private static string Capitalized(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
