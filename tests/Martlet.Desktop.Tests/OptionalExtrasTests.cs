using System.IO;
using Martlet.Core.Planning;
using Martlet.Core.Reading;
using Martlet.Core.Settings;
using Martlet.Core.Singing;
using Martlet.Providers;

namespace Martlet.Desktop.Tests;

/// <summary>The Optional extras pages' main choices (OptionalExtras): each starts with Off, says what Off means, marks what is in
/// use, and describes each option with facts that compare them.</summary>
public sealed class OptionalExtrasTests
{
    private static readonly CompanionTab[] Extras =
        [.. CompanionPages.SideListOrder().Where(tab => CompanionPages.GroupOf(tab) == CompanionGroup.Extras)];

    private static string Fact(PickerOption option, string key) => option.Facts.Single(f => f.Key == key).Value;

    private static string Row(PickerOption option) => string.Join(" · ", option.Facts.Select(f => f.Short).OfType<string>().Take(3));

    [Fact]
    public void Every_optional_page_says_what_off_means_in_the_priority_lists_words()
    {
        Assert.All(Extras, tab => Assert.False(string.IsNullOrWhiteSpace(OptionalExtras.OffMeans(tab))));
        Assert.Equal("Martlet doesn't sing", OptionalExtras.OffMeans(CompanionTab.Singing));
        Assert.Equal("Martlet doesn't draw pictures", OptionalExtras.OffMeans(CompanionTab.Pictures));
        foreach (var tab in Extras)
            if (OptionalExtras.Component(tab) is { } part && ComponentRanking.CanBeOff(part))
                Assert.Equal(char.ToUpperInvariant(ComponentRanking.OffMeans(part)[0]) + ComponentRanking.OffMeans(part)[1..], OptionalExtras.OffMeans(tab));
        // The Off choice says it too, and uses nothing.
        var off = OptionalExtras.Off(CompanionTab.Reading, inUse: true);
        Assert.True(off.IsOff);
        Assert.Equal("in use", off.Badge);
        Assert.Equal(OptionalExtras.OffMeans(CompanionTab.Reading) + ".", off.Summary);
    }

    [Fact]
    public void Pages_the_priority_list_ranks_follow_its_order_and_the_others_their_fallback_place()
    {
        var order = Extras.Select(OptionalExtras.Order).ToArray();
        Assert.Equal(order.OrderBy(o => o), order);
        Assert.Equal(ComponentRanking.Of(PlanComponent.DeepThinking).Rank, OptionalExtras.Order(CompanionTab.DeepThinking));
        Assert.Null(OptionalExtras.Component(CompanionTab.Thinking));
    }

    [Fact]
    public void Singing_offers_off_or_its_role_with_its_footprint_and_license()
    {
        var notSetUp = OptionalExtras.SingingChoices(active: false, readySomewhere: false);
        Assert.Equal(new[] { "Off", "Role" }, notSetUp.Select(o => o.Key));
        Assert.True(notSetUp[0].InUse);
        Assert.Null(notSetUp[1].Badge);
        var role = notSetUp[1];
        Assert.Equal("an NVIDIA graphics card", Fact(role, "runs-on"));
        Assert.Contains("needs a 6 GB+ card", Fact(role, "vram"));
        Assert.Contains("30.6 GB", Fact(role, "download"));
        Assert.Contains("30-second song", Fact(role, "song"));
        Assert.Contains("MIT", Fact(role, "license"));
        Assert.Equal("NVIDIA GPU · 7.2 GB VRAM · 1-2 min a song", Row(role));
        // The extra facts come before the catalog's Numbers line.
        Assert.Equal("evidence", role.Facts[^1].Key);

        var singing = OptionalExtras.SingingChoices(active: true, readySomewhere: true);
        Assert.False(singing[0].InUse);
        Assert.True(singing[1].InUse);
        Assert.Equal("in use", singing[1].Badge);
        Assert.Equal("ready", OptionalExtras.SingingChoices(active: false, readySomewhere: true)[1].Badge);
    }

    [Fact]
    public void The_voice_matches_compare_their_footprint_and_license()
    {
        var matches = OptionalExtras.VoiceMatches(SongVoiceMatch.VevoSing);
        Assert.Equal(new[] { "SoulX", "VevoSing", "Vevo2", "SeedVc" }, matches.Select(m => m.Key));
        Assert.True(matches[1].InUse);
        Assert.Equal("recommended", matches[0].Badge);
        Assert.Contains("non-commercial", Fact(matches[1], "license"));
        Assert.Equal("Any use", matches[0].Facts.Single(f => f.Key == "license").Short);
        Assert.NotEqual(Fact(matches[0], "download"), Fact(matches[1], "download"));
        Assert.NotEqual(Fact(matches[0], "vram"), Fact(matches[1], "vram"));
    }

    [Fact]
    public void Reading_compares_windows_ocr_and_its_role_and_says_when_windows_cant_read()
    {
        var choices = OptionalExtras.ReadingChoices(ReadingPlace.ThisPc, windowsReads: true);
        Assert.Equal(new[] { "Off", "ThisPc", "Host" }, choices.Select(o => o.Key));
        Assert.True(choices[1].InUse);
        // The catalog's numbers come first (its short facts make the row), then what only OCR has.
        Assert.Equal(OptionFacts.Short(FootprintCatalog.Default.Find(OptionalExtras.ReadingWindowsOcr)!), Row(choices[1]));
        // Before the role is set up, it shows its recommended model, PP-OCRv5 mobile.
        Assert.Equal(OptionFacts.Short(FootprintCatalog.Default.Find(OptionalExtras.ReadingPpOcrV5)!), Row(choices[2]));
        Assert.Equal("none: it is built into Windows", Fact(choices[1], "download"));
        Assert.Contains("1.8 s", Fact(choices[2], "speed"));
        Assert.Contains("game fonts", choices[2].Summary);
        Assert.Contains("4K", choices[2].Summary);
        Assert.Contains("Chinese, English and Japanese", Fact(choices[2], "languages"));
        Assert.Contains("192", Fact(choices[2], "accuracy"));
        Assert.Contains("nothing leaves this PC", Fact(choices[1], "data"));
        // A host that runs another model shows that model's facts.
        var rapid = OptionalExtras.ReadingChoices(ReadingPlace.Host, windowsReads: true, "rapidocr-ppocrv4")[2];
        Assert.True(rapid.InUse);
        Assert.Equal(OptionFacts.Short(FootprintCatalog.Default.Find(OptionalExtras.ReadingRapidOcr)!), Row(rapid));
        Assert.Contains("0.9 s", Fact(rapid, "speed"));
        Assert.Contains("Chinese and English", Fact(rapid, "languages"));
        var server = OptionalExtras.ReadingChoices(ReadingPlace.Host, windowsReads: true, "ppocrv5-server")[2];
        Assert.Equal(OptionFacts.Short(FootprintCatalog.Default.Find(OptionalExtras.ReadingPpOcrV5Cuda)!), Row(server));
        Assert.Contains("580", Fact(server, "driver"));
        Assert.Null(choices[1].Unavailable);
        Assert.NotNull(OptionalExtras.ReadingChoices(ReadingPlace.Off, windowsReads: false)[1].Unavailable);
        Assert.True(OptionalExtras.ReadingChoices(ReadingPlace.Off, windowsReads: null)[0].InUse);
    }

    [Fact]
    public void Each_reading_model_has_its_catalog_option_and_martlet_host_answers()
    {
        Assert.Equal(new[] { "ppocrv5-mobile", "ppocrv5-mobile-gpu", "ppocrv5-server", "rapidocr-ppocrv4" }, OptionalExtras.ReadingModels.Select(m => m.Key));
        Assert.All(OptionalExtras.ReadingModels.Where(m => m.Key == m.Model),
            m => Assert.Equal(m.Model, FootprintCatalog.Default.Find(m.CatalogId)!.ModelId));
        Assert.Equal(new Dictionary<string, string>
            {
                ["choice.OCR_ENGINE"] = "ppocrv5", ["choice.OCR_MODEL"] = "ppocrv5-mobile", ["choice.accelerator"] = "cpu"
            },
            OptionalExtras.ReadingModels[0].Answers());
        Assert.Equal("gpu", OptionalExtras.ReadingModelOf("ppocrv5-server")!.Answers()["choice.accelerator"]);
        Assert.True(OptionalExtras.ReadingModelOf("ppocrv5-server")!.NeedsNvidia);
        Assert.False(OptionalExtras.ReadingModelOf("ppocrv5-mobile")!.NeedsNvidia);
        // PP-OCRv5 mobile also runs on an NVIDIA graphics card: the same model, set up with the gpu accelerator. A host's offer
        // names only the model, which is the processor one.
        var mobileGpu = OptionalExtras.ReadingModelOf("ppocrv5-mobile-gpu")!;
        Assert.True(mobileGpu.NeedsNvidia);
        Assert.Equal(new Dictionary<string, string>
            {
                ["choice.OCR_ENGINE"] = "ppocrv5", ["choice.OCR_MODEL"] = "ppocrv5-mobile", ["choice.accelerator"] = "gpu"
            },
            mobileGpu.Answers());
        Assert.Same(OptionalExtras.ReadingModels[0], OptionalExtras.ReadingModelOf("ppocrv5-mobile"));
        // RapidOCR runs only on the processor, so martlet-host doesn't ask where.
        Assert.Equal(new Dictionary<string, string> { ["choice.OCR_ENGINE"] = "rapidocr", ["choice.OCR_MODEL"] = "rapidocr-ppocrv4" },
            OptionalExtras.ReadingModelOf("rapidocr-ppocrv4")!.Answers());
        Assert.Null(OptionalExtras.ReadingModelOf("tesseract"));
        Assert.Null(OptionalExtras.ReadingModelOf(null));
    }

    private static SetupRoute Thinking(string model) => new()
    {
        Role = SetupRole.Llm, RouteType = SetupRouteType.ChatCompletions, ProviderAlias = ChatCompletionsSetup.Alias,
        Origin = GenerationSupport.LocalOllamaChatBaseUrl, ModelId = model, ConfigurationRevision = Guid.NewGuid()
    };

    [Fact]
    public void Hearing_now_says_why_it_is_off()
    {
        Assert.Equal("Martlet gets only the words you say, not how you say them", OptionalExtras.OffMeans(CompanionTab.Hearing));
        var none = new Martlet.Conversation.SenseRoute(SenseKind.Audio, Martlet.Conversation.SensePath.None, null, "");
        Assert.Equal("Off: Martlet gets only the words you say, not how you say them; set up Thinking first.",
            HearingPageModel.Now(false, null, null, none));
        Assert.EndsWith("so it waits for your tick below.", HearingPageModel.Now(false, null, Thinking("gemma4:e2b"), none));
        Assert.Equal("Off: Martlet gets only the words you say, not how you say them.", HearingPageModel.Now(false, false, Thinking("gemma4:e2b"), none));
        Assert.Equal("On: Thinking (gemma4:e2b) hears your recording itself.", HearingPageModel.Now(true, null, Thinking("gemma4:e2b"), none));
    }

    [Fact]
    public void Singing_off_is_kept_in_singing_json_and_stops_offering_songs()
    {
        var directory = Path.Combine(Path.GetTempPath(), "martlet-singing-" + Guid.NewGuid().ToString("N"));
        try
        {
            new SingingPreferences(Host: "singer").Save(directory);
            Assert.False(SingingPreferences.Load(directory).Off);
            (SingingPreferences.Load(directory) with { Off = true }).Save(directory);
            var saved = SingingPreferences.Load(directory);
            Assert.True(saved.Off);
            Assert.Equal("singer", saved.Host);
            Assert.False(SongClient.IsSetUp(directory));
            Assert.Equal("Off: Martlet doesn't sing; Singing stays set up on your computers.", SingingPageModel.Now(saved, active: false, [], listed: false, readySomewhere: true));
            Assert.StartsWith("Not set up yet: add one of your computers", SingingPageModel.Now(new SingingPreferences(), active: false, [], listed: false, readySomewhere: false));
            // With the singing pool list, Off is a list with no computer on; the music and the voice match each name their computers.
            Assert.Equal("Off: Martlet doesn't sing, because no computer in the music list is on.",
                SingingPageModel.Now(new SingingPreferences(), active: false, [], listed: true, readySomewhere: false));
            Assert.Equal("Martlet writes the music on m3-host (or the first free one of 2 computers) in high quality, and matches the " +
                "voice on m4-host with SoulX-Singer.",
                SingingPageModel.Now(new SingingPreferences(), active: true, ["m3-host", "m4-host"], listed: true, readySomewhere: true, ["m4-host"]));
            Assert.Equal("Martlet writes the music on m3-host in fast quality, but voice match is off, so songs keep the music model's own singer.",
                SingingPageModel.Now(new SingingPreferences(), active: true, ["m3-host"], listed: true, readySomewhere: true, [], matchOff: true,
                    quality: SongQuality.Fast));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Motion_says_where_it_runs_that_only_a_vrm_plays_it_and_its_list_wins_over_the_older_choice()
    {
        var older = new Martlet.Core.Motion.MotionSettings();
        Assert.Equal("Martlet makes body motions on m3-host (or the first free one of 2 computers).",
            MotionPageModel.Now(listed: true, noneOn: false, older, ["m3-host", "this PC"], fixture: false));
        Assert.Equal("Martlet makes body motions on m3-host.", MotionPageModel.Now(listed: false, noneOn: false, older, ["m3-host"], fixture: false));
        Assert.Equal("Off. Martlet makes no new body motions; the ones it kept still play. No computer in the list is on.",
            MotionPageModel.Now(listed: true, noneOn: true, older, [], fixture: false));
        Assert.StartsWith("Off. ", MotionPageModel.Now(listed: false, noneOn: false,
            new Martlet.Core.Motion.MotionSettings { Place = Martlet.Core.Motion.MotionPlace.Off }, [], fixture: false));
        Assert.StartsWith("Not set up yet:", MotionPageModel.Now(listed: false, noneOn: false, older, [], fixture: false));
        Assert.StartsWith("No computer in the list can make motions", MotionPageModel.Now(listed: true, noneOn: false, older, [], fixture: false));
        Assert.Contains("FIXTURE - NOT AI", MotionPageModel.Now(listed: true, noneOn: true, older, [], fixture: true));
        Assert.Contains("Live2D", MotionPageModel.Character(Martlet.Avatars.AvatarRenderer.Live2D));
        Assert.Contains("only on VRM characters", MotionPageModel.Character(Martlet.Avatars.AvatarRenderer.Live2D));
        Assert.StartsWith("Your character is a VRM", MotionPageModel.Character(Martlet.Avatars.AvatarRenderer.Vrm));

        var directory = Path.Combine(Path.GetTempPath(), "martlet-motion-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            // The older choice names one computer: motions are asked for (and say why when it isn't paired here).
            Assert.True(new Martlet.Core.Motion.MotionSettings { Place = Martlet.Core.Motion.MotionPlace.Host, HostId = "gpu-x" }.Save(directory));
            Assert.True(MotionClient.IsSetUp(directory));
            Assert.Equal(["gpu-x"], MotionClient.Hosts(directory));
            // A Motion list with nothing on turns motion off, whatever motion.json says.
            Assert.True(Martlet.Core.Cluster.PoolSettings.SaveFor(directory, Martlet.Core.Cluster.PoolAreas.Motion, new Martlet.Core.Cluster.PoolList
            {
                Area = "motion", Members = [Martlet.Core.Cluster.PoolMember.Computer("gpu-x") with { Off = true }]
            }));
            Assert.False(MotionClient.IsSetUp(directory));
            Assert.Null(MotionClient.For(directory));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
