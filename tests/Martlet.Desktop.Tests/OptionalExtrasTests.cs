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
        Assert.Equal(OptionFacts.Short(FootprintCatalog.Default.Find(OptionalExtras.ReadingRapidOcr)!), Row(choices[2]));
        Assert.Equal("none: it is built into Windows", Fact(choices[1], "download"));
        Assert.Contains("0.9 s", Fact(choices[2], "speed"));
        Assert.Contains("game fonts", choices[2].Summary);
        Assert.Contains("Chinese and English", Fact(choices[2], "languages"));
        Assert.Contains("nothing leaves this PC", Fact(choices[1], "data"));
        Assert.Null(choices[1].Unavailable);
        Assert.NotNull(OptionalExtras.ReadingChoices(ReadingPlace.Off, windowsReads: false)[1].Unavailable);
        Assert.True(OptionalExtras.ReadingChoices(ReadingPlace.Off, windowsReads: null)[0].InUse);
    }

    [Fact]
    public void The_thinking_pool_marks_each_place_that_has_members()
    {
        DeepThinkingSettings[] members =
        [
            new() { Place = DeepThinkingPlace.Host, HostId = "diva", ModelId = "gemma4:e4b" },
            new() { Place = DeepThinkingPlace.Endpoint, Origin = MainWindow.LocalOllamaBaseUrl, ModelId = "gemma4:e4b" }
        ];
        var on = OptionalExtras.ThinkingPoolChoices(on: true, members);
        Assert.Equal(new[] { "Off", "Computer", "ThisPc", "Cloud" }, on.Select(o => o.Key));
        Assert.Equal(new[] { "Computer", "ThisPc" }, on.Where(o => o.InUse).Select(o => o.Key));
        // The graphics memory a member takes comes from the catalog's Thinking pool models.
        var local = FootprintCatalog.Default.For(PlanComponent.DeepThinking).Where(o => o.IsLocal).ToArray();
        Assert.Contains(local.Max(o => o.GpuGb).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " GB", Fact(on[2], "vram"));
        Assert.Equal("Shares the GPU", on[2].Facts.Single(f => f.Key == "conversation").Short);

        var off = OptionalExtras.ThinkingPoolChoices(on: false, members);
        Assert.True(off[0].InUse);
        Assert.DoesNotContain(off.Skip(1), o => o.InUse);
    }

    private static SetupRoute Thinking(string model) => new()
    {
        Role = SetupRole.Llm, RouteType = SetupRouteType.ChatCompletions, ProviderAlias = ChatCompletionsSetup.Alias,
        Origin = GenerationSupport.LocalOllamaChatBaseUrl, ModelId = model, ConfigurationRevision = Guid.NewGuid()
    };

    [Fact]
    public void Vision_offers_off_then_the_image_models_and_keeps_the_chosen_one_while_off()
    {
        var thinking = Thinking("gemma4:e2b");
        var on = OptionalExtras.SenseChoices(SenseKind.Image, new(), thinking, null, on: true, "Ollama on this PC", "Vision is off.");
        Assert.Equal(new[] { "Off", "Thinking", "OtherSense", "ThisPc", "Cloud", "Computer" }, on.Select(o => o.Key));
        Assert.Equal(new[] { "Thinking" }, on.Where(o => o.InUse).Select(o => o.Key));
        Assert.Equal("With Thinking · No extra model · sees", Row(on[1]));
        Assert.Equal("with Thinking's request, to Ollama on this PC", Fact(on[1], "data"));
        Assert.Equal("nowhere: they stay on this PC", Fact(on[3], "data"));
        // The cloud's cost names the catalog's providers (NVIDIA Build has a free tier) and the others.
        Assert.Equal("free or paid", on[4].Facts.Single(f => f.Key == "cost").Short);
        Assert.Contains("NVIDIA Build (free endpoint): a free tier", Fact(on[4], "cost"));
        // Ollama on this PC: a second model's graphics memory, from the catalog's image models.
        var local = FootprintCatalog.Default.For(PlanComponent.Vision).Where(o => o.IsLocal && !o.UsesThinking).ToArray();
        Assert.Contains(local.Max(o => o.GpuGb).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " GB", Fact(on[3], "vram"));

        var off = OptionalExtras.SenseChoices(SenseKind.Image, new(), thinking, null, on: false, "Ollama on this PC", "Vision is off.");
        Assert.True(off[0].InUse);
        Assert.False(off[1].InUse);
        Assert.Equal("chosen", off[1].Badge);
        Assert.Equal("Vision is off.", off[1].State);

        // A text-only Thinking model warns; without Thinking its own model can't be chosen.
        var text = OptionalExtras.SenseChoices(SenseKind.Image, new(), Thinking("qwen3:8b"), null, on: true, null, "");
        Assert.True(text[1].Warn);
        Assert.Contains("doesn't see pictures", text[1].State);
        Assert.NotNull(OptionalExtras.SenseChoices(SenseKind.Image, new(), null, null, on: true, null, "")[1].Unavailable);
    }

    [Fact]
    public void Hearing_offers_off_then_the_audio_models_without_your_computers()
    {
        var senses = new SenseModels
        {
            Audio = new() { Source = SenseSource.Own, Own = new() { Place = DeepThinkingPlace.Endpoint, Origin = "https://openrouter.ai/api/v1", ModelId = "x/omni" } }
        };
        var choices = OptionalExtras.SenseChoices(SenseKind.Audio, senses, Thinking("gemma4:e2b"), null, on: true, null, "");
        Assert.Equal(new[] { "Off", "Thinking", "OtherSense", "ThisPc", "Cloud" }, choices.Select(o => o.Key));
        Assert.Equal(new[] { "Cloud" }, choices.Where(o => o.InUse).Select(o => o.Key));
        Assert.Equal("recommended", choices[1].Badge);
        Assert.Equal("Recordings go", choices[4].Facts.Single(f => f.Key == "data").Label);
        Assert.Equal(OptionalExtras.OffMeans(CompanionTab.Hearing) + ".", choices[0].Summary);
        Assert.Equal("Martlet gets only the words you say, not how you say them", OptionalExtras.OffMeans(CompanionTab.Hearing));
    }

    [Fact]
    public void Hearing_with_thinking_on_one_of_your_computers_does_not_blame_a_model_that_hears()
    {
        var host = new SetupRoute
        {
            Role = SetupRole.Llm, RouteType = SetupRouteType.GatewayOllama, ProviderAlias = SelfHostSetup.GatewayOllamaAlias,
            Origin = "https://miku-host.local:9443", ModelId = "gemma4-e4b", ConfigurationRevision = Guid.NewGuid()
        };
        var choices = OptionalExtras.SenseChoices(SenseKind.Audio, new(), host, null, on: true, null, "");
        Assert.False(choices[1].Warn);
        Assert.Equal(new[] { "Thinking" }, choices.Where(o => o.InUse).Select(o => o.Key));

        // A route that takes no recordings says so; it doesn't say the model doesn't hear.
        var openAi = host with { RouteType = SetupRouteType.OpenAi, ProviderAlias = "openai", Origin = "https://api.openai.com", ModelId = "gpt-4.1-mini" };
        var refused = OptionalExtras.SenseChoices(SenseKind.Audio, new(), openAi, null, on: true, null, "");
        Assert.True(refused[1].Warn);
        Assert.StartsWith("Thinking's route takes no recordings, so gpt-4.1-mini gets the transcript.", refused[1].State);
    }

    [Fact]
    public void Hearing_now_says_why_it_is_off()
    {
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
