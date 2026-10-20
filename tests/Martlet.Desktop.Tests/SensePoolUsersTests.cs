using Martlet.Conversation;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Desktop.Tests;

/// <summary>Companion › Vision and Hearing list the Thinking pool models that get pictures or recordings besides their own list
/// (SensePoolUsers), so an external endpoint that makes screen summaries shows on the Vision page.</summary>
public sealed class SensePoolUsersTests
{
    private const string Nvidia = "https://integrate.api.nvidia.com/v1";
    private static readonly DateTimeOffset At = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    private static DeepThinkingSettings Endpoint(string origin, string model) =>
        new() { Place = DeepThinkingPlace.Endpoint, Origin = origin, ModelId = model, CredentialId = Guid.NewGuid() };

    private static readonly DeepThinkingSettings Eyes = Endpoint(Nvidia, "acme/eyes-26b");
    private static readonly DeepThinkingSettings Ears = Endpoint(Nvidia, "acme/ears-8b");
    private static readonly DeepThinkingSettings Words = Endpoint(Nvidia, "acme/words-120b");

    private static readonly ModelAbilities Abilities = new ModelAbilities()
        .With(new() { Origin = Nvidia, ModelId = Eyes.ModelId!, Sees = true, Hears = false, Source = "a test request", CheckedAt = At })
        .With(new() { Origin = Nvidia, ModelId = Ears.ModelId!, Sees = false, Hears = true, Source = "a test request", CheckedAt = At })
        .With(new() { Origin = Nvidia, ModelId = Words.ModelId!, Sees = false, Hears = false, Source = "a test request", CheckedAt = At });

    private static readonly ThinkingPoolSettings Pool = new()
    {
        Members = [Words, Eyes, Ears], MediaAllowed = [Eyes.Key, Ears.Key]
    };

    [Fact]
    public void An_external_endpoint_that_sees_is_listed_with_the_screen_summaries_it_makes()
    {
        var users = SensePoolUsers.For(SenseKind.Image, Pool, Abilities);

        var eyes = Assert.Single(users);
        Assert.Equal("integrate.api.nvidia.com (acme/eyes-26b)", eyes.Name);
        Assert.True(eyes.External);
        Assert.True(eyes.Summaries);
        Assert.Equal("A cloud provider or server in the Thinking pool. It gets your pictures for screen summaries, and touch zones, the " +
            "character's eyes and check-ins with a screenshot. Requests there may cost money.", SensePoolUsers.Line(SenseKind.Image, eyes, false));
        Assert.Contains("screen summaries when your image model can't take them", SensePoolUsers.Line(SenseKind.Image, eyes, true));
        Assert.Equal("Your pictures also go to 1 model in the Thinking pool, for background work. They are set on Companion › Thinking pool.",
            SensePoolUsers.Summary(SenseKind.Image, users));
    }

    [Fact]
    public void A_member_that_may_not_receive_pictures_or_is_off_is_listed_with_why_it_gets_none()
    {
        var noMedia = SensePoolUsers.For(SenseKind.Image, Pool with { MediaAllowed = [] }, Abilities);
        Assert.False(Assert.Single(noMedia).Gets);
        Assert.EndsWith("It gets no pictures: May receive pictures and recordings is off for it there.",
            SensePoolUsers.Line(SenseKind.Image, noMedia[0], false));
        Assert.Equal("The Thinking pool has 1 model that sees, but none of them gets your pictures now.",
            SensePoolUsers.Summary(SenseKind.Image, noMedia));

        var off = SensePoolUsers.For(SenseKind.Image, Pool.TurnOff(Eyes.Key), Abilities);
        Assert.EndsWith("It is off there, so it gets no pictures.", SensePoolUsers.Line(SenseKind.Image, Assert.Single(off), false));

        var noQuick = SensePoolUsers.For(SenseKind.Image, Pool.WithJobs(Eyes.Key, quick: false), Abilities);
        Assert.False(Assert.Single(noQuick).Summaries);
        Assert.Contains("for touch zones", SensePoolUsers.Line(SenseKind.Image, noQuick[0], false));
    }

    [Fact]
    public void No_member_that_sees_says_only_the_list_gets_pictures()
    {
        var users = SensePoolUsers.For(SenseKind.Image, new ThinkingPoolSettings { Members = [Words] }, Abilities);
        Assert.Empty(users);
        Assert.Equal("No model in the Thinking pool sees pictures, so only the models above (or Thinking's own) get your pictures.",
            SensePoolUsers.Summary(SenseKind.Image, users));
        Assert.Equal("", SensePoolUsers.SummaryDisclosure(true, users, false));
    }

    [Fact]
    public void Hearing_lists_the_member_that_hears_for_PC_sound_descriptions()
    {
        var users = SensePoolUsers.For(SenseKind.Audio, Pool, Abilities);
        var ears = Assert.Single(users);
        Assert.Equal("integrate.api.nvidia.com (acme/ears-8b)", ears.Name);
        Assert.Contains("It gets your recordings for descriptions of PC sounds, and check-ins with a recording.",
            SensePoolUsers.Line(SenseKind.Audio, ears, false));
    }

    [Fact]
    public void Screen_summary_status_and_disclosure_name_the_pool_models_that_make_summaries()
    {
        var users = SensePoolUsers.For(SenseKind.Image, Pool, Abilities);
        var pooled = MainWindow.SenseDecisionFor(SenseJobKind.ScreenSummary, new(), null, Abilities, Pool, null, thisPc: false);
        Assert.Equal((SenseTaker.PoolMember, Eyes.Key), (pooled.Taker, pooled.Taken.Key));
        Assert.Equal("On: while Martlet watches, a Thinking pool model that sees (integrate.api.nvidia.com (acme/eyes-26b)) sums up what " +
            "changed for your next message.", VisionPageModel.ScreenSummaryStatus(true, true, pooled));
        // An image model of its own goes first; the pool model takes over when it can't.
        var own = new SenseModels { Image = new() { Source = SenseSource.Own, Own = Endpoint(Nvidia, "acme/own-eyes") } };
        var first = VisionPageModel.ScreenSummaryStatus(true, true, MainWindow.SenseDecisionFor(SenseJobKind.ScreenSummary, own, null, Abilities, Pool, null, false));
        Assert.StartsWith("On: while Martlet watches, your image model (integrate.api.nvidia.com (acme/own-eyes)) sums up", first);
        Assert.EndsWith(", and the Thinking pool does it when it can't (integrate.api.nvidia.com (acme/eyes-26b)).", first);
        Assert.StartsWith("Off for now: no image model and no Thinking pool model that sees takes them,", VisionPageModel.ScreenSummaryStatus(true, true,
            MainWindow.SenseDecisionFor(SenseJobKind.ScreenSummary, new(), null, Abilities, new ThinkingPoolSettings(), null, false)));

        var disclosure = SensePoolUsers.SummaryDisclosure(true, users, false);
        Assert.Contains("also goes to integrate.api.nvidia.com (acme/eyes-26b) in the Thinking pool", disclosure);
        Assert.EndsWith("Requests to a cloud provider may cost money.", disclosure);
        Assert.Equal("", SensePoolUsers.SummaryDisclosure(false, users, false));
    }

    [Fact]
    public void Thinking_says_which_pool_models_get_pictures_and_recordings()
    {
        var thinking = new SetupRoute
        {
            Role = SetupRole.Llm, RouteType = SetupRouteType.ChatCompletions, ProviderAlias = ChatCompletionsSetup.Alias,
            Origin = GenerationSupport.LocalOllamaChatBaseUrl, ModelId = "gemma4:e4b", ConfigurationRevision = Guid.NewGuid()
        };
        var text = MainWindow.TextModelText(new(), thinking, Abilities, Pool);
        Assert.EndsWith(" Background work also sends pictures to integrate.api.nvidia.com (acme/eyes-26b) in the Thinking pool. Background " +
            "work also sends recordings to integrate.api.nvidia.com (acme/ears-8b) in the Thinking pool.", text);
        Assert.DoesNotContain("Background work", MainWindow.TextModelText(new(), thinking, Abilities));
    }
}
