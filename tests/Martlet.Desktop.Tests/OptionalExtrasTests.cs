using System.IO;
using Martlet.Core.Pictures;
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
        [.. MainWindow.SideListOrder().Where(tab => MainWindow.GroupOf(tab) == CompanionGroup.Extras)];

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
        Assert.Equal(new[] { "SoulX", "VevoSing" }, matches.Select(m => m.Key));
        Assert.True(matches[1].InUse);
        Assert.Equal("recommended", matches[0].Badge);
        Assert.Contains("non-commercial", Fact(matches[1], "license"));
        Assert.Equal("Any use", matches[0].Facts.Single(f => f.Key == "license").Short);
        Assert.NotEqual(Fact(matches[0], "download"), Fact(matches[1], "download"));
        Assert.NotEqual(Fact(matches[0], "vram"), Fact(matches[1], "vram"));
    }

    [Fact]
    public void Pictures_offers_off_and_every_place_with_cost_and_where_descriptions_go()
    {
        var choices = OptionalExtras.PicturesChoices(PicturePlace.OpenRouter);
        Assert.Equal(new[] { "Off", "Host", "ComfyUi", "OpenRouter", "NvidiaBuild" }, choices.Select(o => o.Key));
        Assert.Equal(new[] { "OpenRouter" }, choices.Where(o => o.InUse).Select(o => o.Key));
        Assert.Equal("recommended", choices[1].Badge);
        Assert.Contains("needs a 8 GB+ card", Fact(choices[1], "vram"));
        Assert.Equal("free", Fact(choices[1], "cost"));
        Assert.StartsWith("paid", Fact(choices[3], "cost"));
        Assert.Contains("OpenRouter", Fact(choices[3], "data"));
        Assert.All(choices.Skip(1), option => Assert.Contains(option.Facts, f => f.Key == "size"));
        Assert.True(OptionalExtras.PicturesChoices(PicturePlace.Off)[0].InUse);
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
        Assert.Equal(new[] { "ppocrv5-mobile", "ppocrv5-server", "rapidocr-ppocrv4" }, OptionalExtras.ReadingModels.Select(m => m.Model));
        Assert.All(OptionalExtras.ReadingModels, m => Assert.Equal(m.Model, FootprintCatalog.Default.Find(m.CatalogId)!.ModelId));
        Assert.Equal(new Dictionary<string, string>
            {
                ["choice.OCR_ENGINE"] = "ppocrv5", ["choice.OCR_MODEL"] = "ppocrv5-mobile", ["choice.accelerator"] = "cpu"
            },
            OptionalExtras.ReadingModels[0].Answers());
        Assert.Equal("gpu", OptionalExtras.ReadingModelOf("ppocrv5-server")!.Answers()["choice.accelerator"]);
        Assert.True(OptionalExtras.ReadingModelOf("ppocrv5-server")!.NeedsNvidia);
        Assert.False(OptionalExtras.ReadingModelOf("ppocrv5-mobile")!.NeedsNvidia);
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
        Assert.Equal("Off. Martlet gets only the words you say, not how you say them. Set up Thinking first.",
            MainWindow.HearingNow(false, null, null, none));
        Assert.EndsWith("so it waits for your tick below.", MainWindow.HearingNow(false, null, Thinking("gemma4:e2b"), none));
        Assert.Equal("Off. Martlet gets only the words you say, not how you say them.", MainWindow.HearingNow(false, false, Thinking("gemma4:e2b"), none));
        Assert.Equal("On. Thinking (gemma4:e2b) hears your recording itself.", MainWindow.HearingNow(true, null, Thinking("gemma4:e2b"), none));
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
            Assert.StartsWith("Off. Martlet doesn't sing.", MainWindow.SingingNow(saved, active: false, "this-pc-host", readySomewhere: true));
            Assert.StartsWith("Not set up yet.", MainWindow.SingingNow(new SingingPreferences(), active: false, null, readySomewhere: false));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
