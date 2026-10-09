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

    // ---------- Pictures ----------

    internal const string PicturesRole = "pictures-comfyui";
    internal const string PicturesOpenRouter = "hosted:openrouter-pictures";
    internal const string PicturesNvidiaBuild = "hosted:nvidia-build-pictures";

    /// <summary>Companion › Pictures' main choice, keyed by <see cref="Martlet.Core.Pictures.PicturePlace"/>: Off, Martlet's Pictures
    /// role, your own ComfyUI, OpenRouter or NVIDIA Build.</summary>
    internal static IReadOnlyList<PickerOption> PicturesChoices(Martlet.Core.Pictures.PicturePlace saved)
    {
        PickerOption Place(Martlet.Core.Pictures.PicturePlace place, string name, string summary, IReadOnlyList<OptionFact> facts, bool recommended = false) =>
            new(place.ToString(), name, summary)
            {
                InUse = saved == place, Badge = saved == place ? "in use" : recommended ? "recommended" : null, Facts = facts
            };
        return
        [
            Off(CompanionTab.Pictures, saved == Martlet.Core.Pictures.PicturePlace.Off),
            Place(Martlet.Core.Pictures.PicturePlace.Host, "Martlet's Pictures role",
                "ComfyUI with Z-Image Turbo on this PC or another of your computers with an NVIDIA graphics card. Private and free.",
                Facts(PicturesRole, [],
                    new("picture", "Per picture", "a few seconds; a 12 GB+ card is faster", "Seconds a picture"),
                    new("size", "Picture size", "about 1 megapixel: 1024x1024, or a wide or tall shape", null),
                    new("cost", "Cost", "free", "free"),
                    new("data", "Your data", "descriptions stay on your computers", null),
                    new("license", "License", "Z-Image Turbo Apache-2.0, ComfyUI GPL-3.0", null)), recommended: true),
            Place(Martlet.Core.Pictures.PicturePlace.ComfyUi, "My own ComfyUI",
                "A ComfyUI you already run, on this PC or another machine, at its address. Use its models or your own workflow.",
                [
                    new("runs-on", "Runs on", "the graphics card of the computer that runs your ComfyUI", "Your ComfyUI"),
                    new("vram", "Graphics memory", "what its model needs (Z-Image Turbo: an 8 GB+ card)", null),
                    new("download", "Download", "nothing from Martlet: it uses the models your ComfyUI has", "No download"),
                    new("picture", "Per picture", "depends on your model and card", null),
                    new("size", "Picture size", "Z-Image Turbo and XL models about 1024 pixels, others 512-768", null),
                    new("cost", "Cost", "free", "free"),
                    new("data", "Your data", "descriptions go to your ComfyUI's address (it has no password: only on your own network)", null)
                ]),
            Place(Martlet.Core.Pictures.PicturePlace.OpenRouter, "OpenRouter", "Many image models in the cloud. Each picture costs money.",
                Facts(PicturesOpenRouter, [new("runs-on", "Runs on", "online: nothing runs on your computers", "Online")],
                    new("picture", "Per picture", "seconds, by the model", null),
                    new("size", "Picture size", "about 1K, in the shape Martlet picks", null),
                    new("cost", "Cost", "paid: each picture costs money (an OpenRouter key)", "paid"),
                    new("data", "Your data", "descriptions go to OpenRouter and the model's provider", null),
                    new("models", "Models", "any OpenRouter model that draws (default google/gemini-3.1-flash-image)", null))),
            Place(Martlet.Core.Pictures.PicturePlace.NvidiaBuild, "NVIDIA Build", "FLUX models in NVIDIA's cloud with your NVIDIA API key.",
                Facts(PicturesNvidiaBuild, [new("runs-on", "Runs on", "online: nothing runs on your computers", "Online")],
                    new("picture", "Per picture", "seconds (FLUX.1 schnell is fast)", null),
                    new("size", "Picture size", "about 1 megapixel, in the shape Martlet picks", null),
                    new("cost", "Cost", "your nvapi- key; each picture may cost money", "API key"),
                    new("data", "Your data", "descriptions go to NVIDIA", null),
                    new("models", "Models", "NVIDIA's FLUX models (default black-forest-labs/flux.1-schnell)", null)))
        ];
    }

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

    // ---------- Thinking pool ----------

    /// <summary>Companion › Thinking pool's main choice, keyed by where a member thinks: Off, one of your computers, Ollama on this
    /// PC or a cloud provider or server. <paramref name="members"/>: the pool's members now (several places can be in use at once).</summary>
    internal static IReadOnlyList<PickerOption> ThinkingPoolChoices(bool on, IReadOnlyList<DeepThinkingSettings> members)
    {
        var local = FootprintCatalog.Default.For(PlanComponent.DeepThinking).Where(o => o.IsLocal).ToArray();
        var vram = local.Length == 0 ? "" : $"about {Gb(local.Min(o => o.GpuGb))} to {Gb(local.Max(o => o.GpuGb))} GB, by the model, with a long think";
        var download = local.Length == 0 ? "" : $"about {Gb(local.Min(o => o.Peak.DiskGb))} to {Gb(local.Max(o => o.Peak.DiskGb))} GB, by the model";
        bool Has(Func<DeepThinkingSettings, bool> place) => on && members.Any(place);
        var computer = Has(m => m.Place == DeepThinkingPlace.Host);
        var thisPc = Has(m => m.Place == DeepThinkingPlace.Endpoint && m.Origin == MainWindow.LocalOllamaBaseUrl);
        var cloud = Has(m => m.Place == DeepThinkingPlace.Endpoint && m.Origin != MainWindow.LocalOllamaBaseUrl);
        return
        [
            Off(CompanionTab.DeepThinking, !on),
            new("Computer", "One of your computers",
                "Your paired computers with a Thinking model join the pool by themselves: local, private and parallel.")
            {
                InUse = computer, Badge = computer ? "in use" : "recommended",
                Facts =
                [
                    new("runs-on", "Runs on", "a graphics card on another of your computers (its Thinking pool role, or its Ollama)", "Another PC's GPU"),
                    new("vram", "Graphics memory", $"on that computer: {vram}", null),
                    new("download", "Download", $"on that computer: {download}", null),
                    new("conversation", "The conversation", "keeps full speed: the work runs on another computer", "No slowdown"),
                    new("cost", "Cost", "free", "free"),
                    new("data", "Your data", "a job's text goes to that computer through its paired, pinned connection", null)
                ]
            },
            new("ThisPc", "Ollama on this PC", "A second model beside Thinking's, when both fit on the graphics card.")
            {
                InUse = thisPc, Badge = thisPc ? "in use" : null,
                Facts =
                [
                    new("runs-on", "Runs on", "this PC's graphics card, beside Thinking's model", "This PC's GPU"),
                    new("vram", "Graphics memory", $"{vram}, more than Thinking's; it thinks only while both fit", null),
                    new("download", "Download", download, null),
                    new("conversation", "The conversation", "shares the graphics card: replies may start a little later while it thinks", "Shares the GPU"),
                    new("cost", "Cost", "free", "free"),
                    new("data", "Your data", "stays on this PC", null)
                ]
            },
            new("Cloud", "A cloud provider or server",
                "OpenRouter, OpenAI, NVIDIA Build or another compatible server. Requests may cost money.")
            {
                InUse = cloud, Badge = cloud ? "in use" : null,
                Facts =
                [
                    new("runs-on", "Runs on", "online: nothing runs on your computers", "Online"),
                    new("conversation", "The conversation", "keeps full speed: they never wait for each other", "No slowdown"),
                    new("quality", "Quality", "strong reasoning models you can't run at home", null),
                    new("cost", "Cost", "NVIDIA Build has free endpoints; other providers charge for each request", "free or paid"),
                    new("data", "Your data", "a job's text (for a think, the recent conversation and the task) goes to the provider", null)
                ]
            }
        ];
    }

    // ---------- Vision and Hearing (the image model and the audio model) ----------

    /// <summary>The picker of a sense's page: Vision for pictures, Hearing for recordings.</summary>
    internal static string SensePickerId(SenseKind kind) => kind == SenseKind.Image ? "Vision" : "Hearing";

    internal static CompanionTab SenseTab(SenseKind kind) => kind == SenseKind.Image ? CompanionTab.Vision : CompanionTab.Hearing;

    /// <summary>Companion › Vision's or Hearing's main choice: Off, then what takes <paramref name="kind"/> (keys: Off, then
    /// <see cref="MainWindow.SenseChoice"/> names): Thinking's own model, the other sense's model, Ollama on this PC, a cloud
    /// provider or server, and for pictures one of your computers. <paramref name="on"/>: vision is on, or a model hears your
    /// voice. The saved choice is in use while the part is on, and "chosen" while it is off. <paramref name="thinkingPlace"/>
    /// says where Thinking runs ("Ollama on this PC").</summary>
    internal static IReadOnlyList<PickerOption> SenseChoices(SenseKind kind, SenseModels senses, SetupRoute? thinking, ModelAbilities? abilities,
        bool on, string? thinkingPlace, string offState)
    {
        var image = kind == SenseKind.Image;
        var saved = MainWindow.ChoiceOf(senses.For(kind));
        var inputs = image ? "pictures" : "recordings";
        var other = MainWindow.SenseWord(SenseModels.Other(kind));
        var verb = image ? "Sees pictures" : "Hears recordings";
        var takes = thinking is null ? (bool?)null : image
            ? Said(SenseRouting.ThinkingSees(thinking, abilities)) : Said(SenseRouting.ThinkingHears(thinking, abilities));
        PickerOption Choice(MainWindow.SenseChoice choice, string name, string summary, IReadOnlyList<OptionFact> facts, string? catalogId = null)
        {
            var chosen = saved == choice;
            return new(choice.ToString(), name, summary)
            {
                InUse = on && chosen,
                Badge = chosen ? on ? "in use" : "chosen" : null,
                State = chosen && !on ? offState : null,
                // The catalog's facts (when it has the option) replace where it runs; the facts of this kind stay.
                Facts = catalogId is null ? facts : Facts(catalogId, facts.Take(1).ToList(), [.. facts.Skip(1)])
            };
        }
        var place = thinkingPlace ?? (thinking is null ? "not set up yet" : thinking.ModelId);
        // The catalog's models of this kind: the second models Ollama on this PC can run, and the cloud providers it knows.
        var part = image ? PlanComponent.Vision : PlanComponent.Hearing;
        var local = FootprintCatalog.Default.For(part).Where(o => o.IsLocal && !o.UsesThinking).OrderBy(o => o.GpuGb).ToArray();
        var hosted = FootprintCatalog.Default.For(part).Where(o => !o.IsLocal).ToArray();
        var vram = local.Length == 0 ? "a second model's graphics memory"
            : $"a second model: about {Gb(local[0].GpuGb)} GB ({local[0].DisplayName}) to {Gb(local[^1].GpuGb)} GB ({local[^1].DisplayName})";
        var vramShort = local.Length == 0 ? null : $"{Gb(local[0].GpuGb)}-{Gb(local[^1].GpuGb)} GB VRAM";
        var download = local.Length == 0 ? "the model you pick"
            : $"the model you pick, about {Gb(local.Min(o => o.Peak.DiskGb))} to {Gb(local.Max(o => o.Peak.DiskGb))} GB";
        var cloudCost = string.Join("; ", hosted.Select(o => $"{o.DisplayName}: {(o.FreeTier ? "a free tier" : "paid")}")
            .Append("other providers may charge for each request (a key)"));
        var thinkingTakes = thinking is null ? "Thinking isn't set up yet"
            : takes switch
            {
                true => "yes",
                false => image ? "no: it is text-only" : "no: it gets the transcript",
                _ => "not known yet: " + (image ? "Test vision" : "Test hearing") + " finds out"
            };
        var options = new List<PickerOption>
        {
            Off(SenseTab(kind), !on),
            Choice(MainWindow.SenseChoice.Thinking, "Thinking's own model",
                thinking is null ? "Thinking isn't set up yet."
                    : $"Thinking ({thinking.ModelId}) takes the {inputs} itself. Recommended with an omni model such as Gemma 4 E2B: " +
                      "nothing changes in a conversation.",
                [
                    new("runs-on", "Runs on", $"with Thinking: {place}", "With Thinking"),
                    new("extra", "Extra model", $"none: Thinking takes the {inputs} in its own request", "No extra model"),
                    new(image ? "sees" : "hears", verb, thinkingTakes, takes switch { true => image ? "sees" : "hears", false => image ? "text only" : "transcript only", _ => null }),
                    new("cost", "Cost", "as Thinking's own requests, a little larger", null),
                    new("data", image ? "Pictures go" : "Recordings go", $"with Thinking's request, to {place}", null)
                ], image ? "vision:thinking" : "hearing:thinking"),
            Choice(MainWindow.SenseChoice.OtherSense, $"The {other} model",
                $"The {other} model is now {MainWindow.OtherNow(kind, senses)}. One model does both, one job at a time.",
                [
                    new("runs-on", "Runs on", $"where the {other} model runs: {MainWindow.OtherNow(kind, senses)}", $"With the {other} model"),
                    new("extra", "Extra model", $"none beyond the {other} model; it does one job at a time", "No extra model"),
                    new("data", image ? "Pictures go" : "Recordings go", $"to the {other} model", null)
                ]),
            Choice(MainWindow.SenseChoice.ThisPc, "Ollama on this PC",
                $"A second model beside Thinking's, such as {(image ? "Qwen2.5-VL" : "Gemma 4 E2B")}. It describes {inputs} only while both " +
                "fit on the graphics card.",
                [
                    new("runs-on", "Runs on", "this PC's graphics card, beside Thinking's model", "This PC's GPU"),
                    new("vram", "Graphics memory", $"{vram}; it works only while both fit", vramShort),
                    new("download", "Download", download, null),
                    new("delay", "Reply delay", "none: it describes in the background, and a reply never waits", null),
                    new("cost", "Cost", "free", "free"),
                    new("data", image ? "Pictures go" : "Recordings go", "nowhere: they stay on this PC", null)
                ]),
            Choice(MainWindow.SenseChoice.Cloud, "A cloud provider or server",
                "OpenRouter, OpenAI, NVIDIA Build, a model app on this PC or another OpenAI-compatible server. A cloud provider may " +
                "charge for each request.",
                [
                    new("runs-on", "Runs on", "online, or a model app or server you choose", "Online"),
                    new("delay", "Reply delay", "none: it describes in the background, and a reply never waits", null),
                    new("cost", "Cost", cloudCost, hosted.Any(o => o.FreeTier) ? "free or paid" : "may cost money"),
                    new("data", image ? "Pictures go" : "Recordings go", "to the provider or server you choose", null)
                ])
        };
        if (image)
            options.Add(Choice(MainWindow.SenseChoice.Computer, "One of your computers",
                "A paired computer's model: its Thinking pool role, or its Ollama when it doesn't do Thinking for this PC.",
                [
                    new("runs-on", "Runs on", "a graphics card on another of your computers", "Another PC's GPU"),
                    new("vram", "Graphics memory", "none on this PC: that computer's model uses its own card", "None here"),
                    new("delay", "Reply delay", "none: it describes in the background, and waits while a reply needs that computer", null),
                    new("cost", "Cost", "free", "free"),
                    new("data", "Pictures go", "to that computer through its paired, pinned connection", null)
                ]));
        if (thinking is null)
            options[1] = options[1] with { Unavailable = "Thinking isn't set up yet." };
        else if (takes == false)
            options[1] = options[1] with
            {
                Warn = true,
                // A route that carries no recordings (OpenAI's Responses) says so; it doesn't blame the model.
                State = (!image && !HearingModelCatalog.CarriesAudio(thinking.RouteType)
                        ? $"Thinking's route takes no recordings, so {thinking.ModelId} gets the transcript. Choose a model of its own, " +
                          "or a Thinking route and model that hears."
                        : $"{thinking.ModelId} {(image ? "doesn't see pictures" : "doesn't hear recordings")}. Choose a model of its own, " +
                          "or a Thinking model that " + (image ? "sees." : "hears.")) + (options[1].State is { } off ? " " + off : "")
            };
        else if (takes == true && saved != MainWindow.SenseChoice.Thinking)
            options[1] = options[1] with { Badge = "recommended" };
        return options;
    }

    private static bool? Said(VisionSupport support) => support switch { VisionSupport.Supported => true, VisionSupport.Unsupported => false, _ => null };
    private static bool? Said(HearingSupport support) => support switch { HearingSupport.Supported => true, HearingSupport.Unsupported => false, _ => null };

    // ---------- facts ----------

    /// <summary>The catalog's facts about <paramref name="catalogId"/> (else <paramref name="fallback"/>), with
    /// <paramref name="extra"/> facts it doesn't have before its "Numbers" line.</summary>
    internal static IReadOnlyList<OptionFact> Facts(string catalogId, IReadOnlyList<OptionFact> fallback, params OptionFact[] extra)
    {
        var facts = (FootprintCatalog.Default.Find(catalogId) is { } option ? OptionFacts.Of(option) : fallback).ToList();
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
