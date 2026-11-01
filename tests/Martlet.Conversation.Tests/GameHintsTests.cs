using Martlet.Conversation.Guides;

namespace Martlet.Conversation.Tests;

/// <summary>Hints and secrets (docs/APP_GUIDES.md, "Hints and secrets"): a pool member picks one hint from the guide sections on
/// the place, with the user's preferences; Martlet says it at a quiet moment and never twice for the same place and topic. The
/// game state, the preferences, the guide, the pool member and the speaker are stand-ins.</summary>
public sealed class GameHintsTests
{
    private static readonly GamePlace Waterfall = new("Elden Ring", "Liurnia, the waterfall by the Academy gate", "Reach the Academy");

    [Fact]
    public async Task A_place_gives_one_hint_that_tells_the_secret_and_the_request_has_the_preferences_and_sections()
    {
        var setup = new Setup("Topic: Secret room behind the waterfall\nHint: There's apparently a secret room behind the waterfall here, with a Golden Seed inside.");
        var result = await setup.Hints.HintAsync(Waterfall);

        Assert.True(result.Said);
        var said = Assert.Single(setup.Speaker.Said);
        Assert.Equal("Secret room behind the waterfall", said.Topic);
        Assert.Contains("secret room behind the waterfall", said.Text, StringComparison.Ordinal);
        var ask = Assert.Single(setup.Thinker.Asks);
        Assert.Equal(GameHints.Instructions, ask.Instructions);
        Assert.Equal(GameHints.Label, ask.Label);
        Assert.Contains("Where the player is: Liurnia, the waterfall by the Academy gate", ask.Text, StringComparison.Ordinal);
        Assert.Contains("Their goal: Reach the Academy", ask.Text, StringComparison.Ordinal);
        Assert.Contains("The user's preferences: none", ask.Text, StringComparison.Ordinal);
        Assert.Contains("Hints already told here: none", ask.Text, StringComparison.Ordinal);
        Assert.Contains("A hidden passage behind the waterfall", ask.Text, StringComparison.Ordinal);
        Assert.Equal(1, setup.Hints.Hints);
        // The guide is searched for secrets, items, puzzles, bosses and the goal at the place.
        Assert.Contains(setup.Guide.Questions, q => q.Contains("secret", StringComparison.Ordinal) && q.StartsWith("Liurnia", StringComparison.Ordinal));
        Assert.Contains("Reach the Academy", setup.Guide.Questions);
        Assert.Equal(0, setup.Guide.Researched);
    }

    [Fact]
    public async Task A_preference_stops_the_hint_and_the_place_is_not_asked_again_until_the_preferences_change()
    {
        var setup = new Setup(GameHints.Pass, "Topic: Hidden chest\nHint: A hidden chest sits under the bridge in this area.");
        setup.Preferences.Text = "- Stop giving hints unless I ask.";

        var stopped = await setup.Hints.HintAsync(Waterfall);
        Assert.True(stopped.Passed);
        Assert.Empty(setup.Speaker.Said);
        Assert.Contains("The user's preferences: \n- Stop giving hints unless I ask.", setup.Thinker.Asks[0].Text, StringComparison.Ordinal);

        var again = await setup.Hints.HintAsync(Waterfall);
        Assert.True(again.Passed);
        Assert.Single(setup.Thinker.Asks);
        Assert.Equal(2, setup.Hints.Passes);

        setup.Preferences.Text = null;
        var said = await setup.Hints.HintAsync(Waterfall);
        Assert.True(said.Said);
        Assert.Equal(2, setup.Thinker.Asks.Count);
    }

    [Fact]
    public async Task The_same_topic_is_not_told_again_and_the_told_topics_go_with_the_request()
    {
        var setup = new Setup("Topic: Secret room behind the waterfall\nHint: There's a secret room behind the waterfall here.",
            "Topic: secret room, behind the waterfall!\nHint: Behind the waterfall here is a secret room.", GameHints.None);
        setup.Clock.Now += TimeSpan.FromHours(1);
        Assert.True((await setup.Hints.HintAsync(Waterfall)).Said);
        setup.Clock.Now += TimeSpan.FromHours(1);

        var repeat = await setup.Hints.HintAsync(Waterfall);
        Assert.True(repeat.Repeat);
        Assert.Contains("Hints already told here: Secret room behind the waterfall", setup.Thinker.Asks[1].Text, StringComparison.Ordinal);
        var nothing = await setup.Hints.HintAsync(Waterfall);
        Assert.True(nothing.Nothing);
        Assert.Single(setup.Speaker.Said);
        Assert.Equal((1, 1, 1), (setup.Hints.Hints, setup.Hints.Repeats, setup.Hints.Nothing));
        Assert.Equal(["Secret room behind the waterfall"], setup.Hints.Told("ELDEN RING", "Liurnia - the waterfall by the Academy gate"));
    }

    [Fact]
    public async Task A_place_with_its_share_of_hints_is_not_asked_again()
    {
        var setup = new Setup("Topic: One\nHint: The first useful thing about here.", "Topic: Two\nHint: The second useful thing about here.",
            "Topic: Three\nHint: The third useful thing about here.");
        for (var i = 0; i < GameHints.MaximumPerPlace; i++)
        {
            setup.Clock.Now += TimeSpan.FromHours(1);
            Assert.True((await setup.Hints.HintAsync(Waterfall)).Said);
        }
        Assert.True((await setup.Hints.HintAsync(Waterfall)).Repeat);
        Assert.Equal(GameHints.MaximumPerPlace, setup.Thinker.Asks.Count);
    }

    [Fact]
    public async Task A_hint_waits_for_a_quiet_moment_and_not_too_soon_after_the_last_one()
    {
        var setup = new Setup("Topic: Boss weakness\nHint: The boss ahead is weak to fire, so bring fire pots.");
        setup.Speaker.Quiet = false;
        setup.Waited = () => { if (setup.Clock.Now - Setup.Start >= TimeSpan.FromSeconds(30)) setup.Speaker.Quiet = true; };
        var result = await setup.Hints.HintAsync(Waterfall);
        Assert.True(result.Said);
        Assert.True(setup.Clock.Now - Setup.Start >= TimeSpan.FromSeconds(30));

        // Another place soon after: it waits for the time between hints, then drops when the hold ends first.
        var next = Waterfall with { Place = "Raya Lucaria Academy, main hall" };
        setup.Places.Current = next;
        setup.Thinker.Answers.Add("Topic: Key location\nHint: The Academy key is on a corpse near the gate guard.");
        var held = await setup.Hints.HintAsync(next);
        Assert.False(held.Said);
        Assert.Equal("a hint was said lately", held.Problem);
    }

    [Fact]
    public async Task A_hint_is_dropped_when_the_user_moves_on_or_the_preferences_change_while_it_waits()
    {
        var setup = new Setup("Topic: Hidden chest\nHint: A hidden chest sits under the bridge in this area.");
        setup.Speaker.Quiet = false;
        setup.Waited = () => setup.Places.Current = Waterfall with { Place = "Somewhere else" };
        Assert.Equal("the user moved on", (await setup.Hints.HintAsync(Waterfall)).Problem);

        setup.Places.Current = Waterfall;
        setup.Waited = () => setup.Preferences.Text = "- No hints.";
        Assert.Equal("the preferences changed", (await setup.Hints.HintAsync(Waterfall)).Problem);
        Assert.Empty(setup.Speaker.Said);
    }

    [Fact]
    public async Task Few_sections_start_research_on_the_place_and_no_sections_make_no_request()
    {
        var setup = new Setup("Topic: Hidden chest\nHint: A hidden chest sits under the bridge in this area.");
        setup.Guide.Sections.Clear();
        var none = await setup.Hints.HintAsync(Waterfall);
        Assert.Equal("the guide has nothing on this place", none.Problem);
        Assert.Equal(1, setup.Guide.Researched);
        Assert.Empty(setup.Thinker.Asks);

        setup.Guide.AfterResearch = new GuideChunk("https://wiki/liurnia", "Liurnia", "Secrets", "A chest hides under the bridge.");
        Assert.True((await setup.Hints.HintAsync(Waterfall)).Said);
        Assert.Equal(2, setup.Guide.Researched);
    }

    [Fact]
    public async Task No_hint_without_a_place_or_a_pool_member()
    {
        var setup = new Setup("Topic: X\nHint: Something useful about this area.");
        Assert.Equal("no place is known", (await setup.Hints.HintAsync(Waterfall with { Place = null })).Problem);
        setup.Thinker.CanThink = false;
        Assert.Equal("no Thinking pool member can take it now", (await setup.Hints.HintAsync(Waterfall)).Problem);
        Assert.Empty(setup.Thinker.Asks);
        Assert.Equal(2, setup.Hints.Problems);
    }

    [Fact]
    public async Task A_place_change_starts_a_hint_in_the_background()
    {
        var setup = new Setup("Topic: Secret room\nHint: There's apparently a secret room behind the waterfall here.");
        setup.Places.Raise(Waterfall);
        for (var i = 0; i < 300 && setup.Speaker.Said.Count == 0; i++) await Task.Delay(10);
        Assert.Single(setup.Speaker.Said);
    }

    [Fact]
    public void The_answer_is_a_pass_none_or_a_topic_and_hint_and_never_a_claim_about_the_screen()
    {
        Assert.Equal(GameHints.Pass, GameHints.Parse("\"[PASS]\"")!.Value.Kind);
        Assert.Equal(GameHints.None, GameHints.Parse("[none].")!.Value.Kind);
        Assert.Null(GameHints.Parse(null));
        Assert.Null(GameHints.Parse("Sure, here you go."));
        var hint = GameHints.Parse("**Topic:** Hidden wall\n**Hint:** Hit the wall left of the altar\nto open a passage.")!.Value;
        Assert.Equal(("hint", "Hidden wall", "Hit the wall left of the altar to open a passage."), (hint.Kind, hint.Topic, hint.Hint));
        Assert.Null(GameHints.Parse("Topic: Chest\nHint: I can see a chest on your screen, open it."));
        Assert.Null(GameHints.Parse("Topic: Chest\nHint: The chest on screen has a key."));
        Assert.Equal("A hint for Elden Ring at Liurnia, from the game's guide (not from the screen, so say where it is, not that you see it): Look behind the waterfall.",
            GameHints.Words(new GameHint("Elden Ring", "Liurnia", "Waterfall", "Look behind the waterfall.")));
    }

    [Fact]
    public void Guide_text_goes_in_as_bounded_data_after_the_preferences()
    {
        var big = new GuideChunk("https://wiki/a", "Page", "Section", new string('x', 20_000));
        var input = GameHints.Input(Waterfall, "- No spoilers.", [], [big, big]);
        Assert.True(input.Length < GameHints.InputCharacters + 1_000);
        Assert.True(input.IndexOf("- No spoilers.", StringComparison.Ordinal) < input.IndexOf("Guide sections:", StringComparison.Ordinal));
    }

    private sealed class Setup
    {
        public static readonly DateTimeOffset Start = new(2026, 10, 31, 18, 0, 0, TimeSpan.Zero);
        public Clock Clock { get; } = new() { Now = Start };
        public Places Places { get; } = new() { Current = Waterfall };
        public Preferences Preferences { get; } = new();
        public Guide Guide { get; } = new();
        public Thinker Thinker { get; }
        public Speaker Speaker { get; } = new();
        public GameHints Hints { get; }
        public Action? Waited { get; set; }

        public Setup(params string[] answers)
        {
            Thinker = new([.. answers]);
            Hints = new(Places, Preferences, Guide, Thinker, Speaker, Clock, (time, _) =>
            {
                Clock.Now += time;
                Waited?.Invoke();
                return Task.CompletedTask;
            });
        }
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }
        public override DateTimeOffset GetUtcNow() => Now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class Places : IGamePlaceSource
    {
        public GamePlace? Current { get; set; }
        public event Action<GamePlace>? Changed;
        public void Raise(GamePlace place)
        {
            Current = place;
            Changed?.Invoke(place);
        }
    }

    private sealed class Preferences : IHintPreferences
    {
        public string? Text { get; set; }
        public string? For(string game) => Text;
    }

    private sealed class Guide : IHintGuide
    {
        public List<GuideChunk> Sections { get; } =
        [
            new("https://wiki/liurnia", "Liurnia of the Lakes", "Secrets", "A hidden passage behind the waterfall leads to a room with a Golden Seed."),
            new("https://wiki/liurnia", "Liurnia of the Lakes", "Bosses", "The Glintstone Dragon is weak to strike damage.")
        ];
        public GuideChunk? AfterResearch { get; set; }
        public List<string> Questions { get; } = [];
        public int Researched { get; private set; }

        public Task<IReadOnlyList<GuideChunk>> FindAsync(GamePlace place, IReadOnlyList<string> questions, int max, CancellationToken cancellationToken)
        {
            Questions.AddRange(questions);
            return Task.FromResult<IReadOnlyList<GuideChunk>>([.. Sections.Take(max)]);
        }

        public Task<bool> ResearchAsync(GamePlace place, string area, CancellationToken cancellationToken)
        {
            Researched++;
            if (AfterResearch is not { } added) return Task.FromResult(false);
            Sections.Add(added);
            return Task.FromResult(true);
        }
    }

    private sealed class Thinker(List<string> answers) : IGuideThinker
    {
        private readonly List<GuideThinkingAsk> asks = [];
        public List<string> Answers => answers;
        public bool CanThink { get; set; } = true;
        public IReadOnlyList<GuideThinkingAsk> Asks { get { lock (asks) return [.. asks]; } }

        public Task<(string? Text, string? Problem)> ThinkAsync(GuideThinkingAsk ask, CancellationToken cancellationToken)
        {
            lock (asks)
            {
                asks.Add(ask);
                return Task.FromResult<(string?, string?)>((answers[Math.Min(asks.Count - 1, answers.Count - 1)], null));
            }
        }
    }

    private sealed class Speaker : IHintSpeaker
    {
        public bool Quiet { get; set; } = true;
        public List<GameHint> Said { get; } = [];

        public Task<bool> SayAsync(GameHint hint, CancellationToken cancellationToken)
        {
            lock (Said) Said.Add(hint);
            return Task.FromResult(true);
        }
    }
}
