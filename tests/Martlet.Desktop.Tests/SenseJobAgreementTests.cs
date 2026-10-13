using Martlet.Conversation;
using Martlet.Core.Settings;

namespace Martlet.Desktop.Tests;

// Agreement: for each picture and recording job and each setup, Companion's pages name the model the conversation's runner picks
// (SenseJobRouting, docs/SENSE_MODELS.md, Who takes each picture and recording). The page decides from the saved settings
// (MainWindow.SenseDecisionFor), the conversation from what it runs with (LiveConversationController.SenseDecision); the runners
// (the screen summary, the PC sound judge, helper jobs with a picture) follow that decision. NOT AI: fixture models and endpoints
// only, nothing is sent.
public sealed class SenseJobAgreementTests
{
    private const string Nvidia = "https://integrate.api.nvidia.com/v1";
    private static readonly DateTimeOffset At = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    private static DeepThinkingSettings Endpoint(string origin, string model) => new() { Place = DeepThinkingPlace.Endpoint, Origin = origin, ModelId = model };

    // Thinking pool members on a cloud endpoint, by what a test found: one sees, one hears, one reads text only.
    private static readonly DeepThinkingSettings Eyes = Endpoint(Nvidia, "acme/eyes-26b") with { CredentialId = Guid.NewGuid() };
    private static readonly DeepThinkingSettings Ears = Endpoint(Nvidia, "acme/ears-8b") with { CredentialId = Guid.NewGuid() };
    private static readonly DeepThinkingSettings Words = Endpoint(Nvidia, "acme/words-120b") with { CredentialId = Guid.NewGuid() };

    // An image model and an audio model of their own (Companion › Vision and Hearing).
    private static readonly DeepThinkingSettings OwnEyes = Endpoint("https://10.77.0.20:8444/v1", "qwen2.5vl:7b");
    private static readonly DeepThinkingSettings OwnEars = Endpoint("https://10.77.0.20:8444/v1", "gemma3n:e4b");

    private static readonly ModelAbilities Abilities = new ModelAbilities()
        .With(new() { Origin = Nvidia, ModelId = Eyes.ModelId!, Sees = true, Hears = false, Source = ModelAbility.TestRequest, CheckedAt = At })
        .With(new() { Origin = Nvidia, ModelId = Ears.ModelId!, Sees = false, Hears = true, Source = ModelAbility.TestRequest, CheckedAt = At })
        .With(new() { Origin = Nvidia, ModelId = Words.ModelId!, Sees = false, Hears = false, Source = ModelAbility.TestRequest, CheckedAt = At });

    public static TheoryData<string> Setups() => ["empty", "pool", "own", "own and pool", "pool may not receive"];

    private static (ThinkingPoolSettings Pool, SenseModels Senses) Setup(string name) => name switch
    {
        "pool" => (new() { Members = [Words, Eyes, Ears], MediaAllowed = [Eyes.Key, Ears.Key] }, new()),
        "own" => (new() { Members = [Words] }, Own()),
        "own and pool" => (new() { Members = [Words, Eyes, Ears], MediaAllowed = [Eyes.Key, Ears.Key] }, Own()),
        "pool may not receive" => (new() { Members = [Words, Eyes, Ears] }, new()),
        _ => (new(), new())
    };

    private static SenseModels Own() => new()
    {
        Image = new() { Source = SenseSource.Own, Own = OwnEyes }, Audio = new() { Source = SenseSource.Own, Own = OwnEars }
    };

    private static async Task<LiveFixture> Fixture(string setup, bool soundTagger)
    {
        var fixture = await LiveFixture.Create(data: true);
        var (pool, senses) = Setup(setup);
        Assert.True(Abilities.Save(fixture.DirectoryPath));
        Assert.True(pool.Save(fixture.DirectoryPath));
        fixture.Controller.Configure(await fixture.Store.LoadAsync());
        fixture.Controller.ReloadThinkingPool();
        fixture.Controller.SenseModels = senses;
        fixture.Controller.SoundTaggerHere = soundTagger;
        return fixture;
    }

    [Theory]
    [MemberData(nameof(Setups))]
    public async Task The_page_and_the_conversation_pick_the_same_model_for_every_job(string setup)
    {
        foreach (var soundTagger in new[] { false, true })
        {
            await using var fixture = await Fixture(setup, soundTagger);
            var (pool, senses) = Setup(setup);
            var thinking = fixture.Controller.Configuration!.Routes.Single(r => r.Role == SetupRole.Llm);
            var abilities = ModelAbilities.Load(fixture.DirectoryPath);
            foreach (var job in Enum.GetValues<SenseJobKind>())
            {
                var runner = fixture.Controller.SenseDecision(job);
                // Before a conversation reads the pool (the saved pool's settings), and with the conversation's own pool.
                var saved = MainWindow.SenseDecisionFor(job, senses, thinking, abilities, pool, null, soundTagger);
                var live = MainWindow.SenseDecisionFor(job, senses, thinking, abilities, new(), fixture.Controller.ThinkingPool, soundTagger);
                foreach (var page in new[] { saved, live })
                {
                    Assert.Equal((runner.Taker, runner.Taken.Key, runner.Name), (page.Taker, page.Taken.Key, page.Name));
                    Assert.Equal(runner.Steps.Select(s => (s.Taker, s.Takes)), page.Steps.Select(s => (s.Taker, s.Takes)));
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Setups))]
    public async Task The_page_text_names_the_model_the_runner_picks(string setup)
    {
        await using var fixture = await Fixture(setup, soundTagger: true);
        var controller = fixture.Controller;
        var thinking = controller.Configuration!.Routes.Single(r => r.Role == SetupRole.Llm);
        var abilities = controller.Configuration.Abilities;

        // Screen summaries: Companion › Vision's status names the model, and the runner can make them only with one.
        var summary = controller.SenseDecision(SenseJobKind.ScreenSummary);
        var status = MainWindow.ScreenSummaryStatus(true, true, summary);
        Assert.Equal(summary.Taker != SenseTaker.Nobody, controller.ScreenDigestThinker.CanSee);
        if (summary.Name is { } summarizer) Assert.Contains($"({summarizer})", status);
        if (summary.Then is { } then) Assert.Contains($"({then.Name})", status);

        // Touch zones and the eyes: Companion › Touch's line names the model, and so does the helper job's fallback.
        var helper = controller.SenseDecision(SenseJobKind.HelperPicture);
        var sight = MainWindow.TouchZonesSight(helper, thinking, abilities, fixture: false);
        Assert.Equal(helper.Taker != SenseTaker.Nobody, sight.CanSee);
        if (helper.Taker is SenseTaker.PoolMember or SenseTaker.SenseModel) Assert.Contains(helper.Name!, sight.Line);

        // PC sound descriptions: Companion › Listening's judge is the judge the sound digest gets.
        var sound = controller.SenseDecision(SenseJobKind.PcSound);
        var (judge, kind) = MainWindow.SoundJudgeOf(sound);
        var running = controller.SoundJudge();
        Assert.Equal(sound.Taker is SenseTaker.SenseModel or SenseTaker.PoolMember ? judge : null, running?.Name);
        if (running is not null) Assert.Equal(kind, running.Kind);

        // An empty Vision or Hearing list says who takes each job instead, by the same names.
        foreach (var sense in new[] { SenseKind.Image, SenseKind.Audio })
        {
            var decisions = SenseJobRouting.Of(sense).Select(row => controller.SenseDecision(row.Job)).ToArray();
            var instead = SenseJobRouting.Instead(decisions);
            foreach (var decision in decisions.Where(d => d.Name is not null)) Assert.Contains(decision.Name!, instead);
        }
    }

    [Fact]
    public async Task Each_setup_takes_what_the_table_says()
    {
        async Task<(SenseTaker Summary, SenseTaker Helper, SenseTaker Sound, string? Summarizer)> Picks(string setup)
        {
            await using var fixture = await Fixture(setup, soundTagger: true);
            var summary = fixture.Controller.SenseDecision(SenseJobKind.ScreenSummary);
            return (summary.Taker, fixture.Controller.SenseDecision(SenseJobKind.HelperPicture).Taker,
                fixture.Controller.SenseDecision(SenseJobKind.PcSound).Taker, summary.Name);
        }

        Assert.Equal((SenseTaker.Nobody, SenseTaker.ThinkingModel, SenseTaker.ThisPc, (string?)null), await Picks("empty"));
        Assert.Equal((SenseTaker.PoolMember, SenseTaker.PoolMember, SenseTaker.PoolMember, "integrate.api.nvidia.com (acme/eyes-26b)"), await Picks("pool"));
        Assert.Equal((SenseTaker.SenseModel, SenseTaker.SenseModel, SenseTaker.SenseModel, "10.77.0.20 (qwen2.5vl:7b)"), await Picks("own"));
        // The pool goes first for helper jobs, the image and audio models for summaries.
        Assert.Equal((SenseTaker.SenseModel, SenseTaker.PoolMember, SenseTaker.SenseModel, "10.77.0.20 (qwen2.5vl:7b)"), await Picks("own and pool"));
        // A cloud member whose May receive pictures and recordings is off gets none.
        Assert.Equal((SenseTaker.Nobody, SenseTaker.ThinkingModel, SenseTaker.ThisPc, (string?)null), await Picks("pool may not receive"));
    }
}
