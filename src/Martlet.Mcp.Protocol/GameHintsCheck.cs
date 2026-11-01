using System.IO;
using Martlet.Conversation;
using Martlet.Conversation.Guides;

namespace Martlet.Mcp;

/// <summary>game_hints_check: hints and secrets (<see cref="GameHints"/>, docs/APP_GUIDES.md "Hints and secrets"), rehearsed with
/// the production hint keeper, App guide library (<see cref="AppGuideService"/>, <see cref="FileAppGuideStore"/>) and guide search
/// (<see cref="AppGuideHintGuide"/>) on a FIXTURE guide about a made-up game in a temporary folder, FIXTURE game places and
/// preferences, and a FIXTURE Thinking pool member (answers made from the request, NOT AI; nothing is sent). It shows a hint at a
/// place, no repeat of the same topic, a stop by a preference (and no new request while the preference stays) and a hint again
/// when the preference is gone. An optional <c>reply</c> is parsed the way a pool member's answer is.</summary>
internal static class GameHintsCheck
{
    private const string Game = "Starfall Valley (FIXTURE)";
    private const string Stop = "- Stop giving hints unless I ask. (FIXTURE preference)";

    internal static async Task<object> RunAsync(string? reply, CancellationToken cancellation)
    {
        var folder = Path.Combine(Path.GetTempPath(), "martlet-game-hints-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileAppGuideStore(folder);
            var key = AppGuideKeys.Of(Game);
            await store.SaveGuideAsync(new AppGuide(key, Game, ["fixture.example"], DateTimeOffset.UtcNow,
                [new GuideSource("https://fixture.example/wiki/Main_Page", "Main Page", 1)],
                [
                    new("https://fixture.example/wiki/Crystal_Cave", "Crystal Cave", "Secrets",
                        "Crystal Cave secret: a cracked wall behind the waterfall in the Crystal Cave hides a secret room with the Moonstone Ring. FIXTURE guide."),
                    new("https://fixture.example/wiki/Crystal_Cave", "Crystal Cave", "Boss",
                        "Crystal Cave boss: the Crystal Golem is weak to fire; bring fire bombs. FIXTURE guide."),
                    new("https://fixture.example/wiki/Moonlit_Lake", "Moonlit Lake", "Puzzle",
                        "Moonlit Lake puzzle solution: light the three lanterns left to right to raise the bridge. FIXTURE guide."),
                    new("https://fixture.example/wiki/Farming", "Farming", "Crops",
                        "Turnips grow in four days in spring. FIXTURE guide.")
                ]), cancellation);
            using var guides = new AppGuideService(store, new NoBuilder());
            await guides.LoadAsync(cancellation);
            await guides.SetOnAsync(true, cancellation);

            var clock = new Clock();
            var places = new Places();
            var preferences = new Preferences();
            var thinker = new FixtureThinker();
            var speaker = new Speaker();
            using var hints = new GameHints(places, preferences, new AppGuideHintGuide(guides, null), thinker, speaker, clock,
                (time, _) => { clock.Now += time; return Task.CompletedTask; });

            var cave = places.Set(new(Game, "Crystal Cave, by the waterfall", "Find the Moonstone Ring"));
            var first = await hints.HintAsync(cave, cancellation);
            var guideFound = thinker.Asks.Count > 0 && thinker.Asks[0].Text.Contains("cracked wall behind the waterfall", StringComparison.Ordinal);
            var unrelatedLeftOut = thinker.Asks.Count > 0 && !thinker.Asks[0].Text.Contains("Turnips", StringComparison.Ordinal);
            clock.Now += TimeSpan.FromMinutes(10);
            var repeat = await hints.HintAsync(cave, cancellation);
            var toldTopicsSent = thinker.Asks.Count > 1 && thinker.Asks[1].Text.Contains("Hints already told here: Secret room behind the waterfall", StringComparison.Ordinal);

            preferences.Text = Stop;
            var lake = places.Set(new(Game, "Moonlit Lake", "Cross the lake"));
            clock.Now += TimeSpan.FromMinutes(10);
            var asksBefore = thinker.Asks.Count;
            var stopped = await hints.HintAsync(lake, cancellation);
            var preferenceSent = thinker.Asks.Count > asksBefore && thinker.Asks[^1].Text.Contains(Stop, StringComparison.Ordinal);
            var asksAfterStop = thinker.Asks.Count;
            var stillStopped = await hints.HintAsync(lake, cancellation);
            var notAskedAgain = thinker.Asks.Count == asksAfterStop;

            preferences.Text = null;
            clock.Now += TimeSpan.FromMinutes(10);
            var again = await hints.HintAsync(lake, cancellation);
            var sameStart = thinker.Asks.All(a => a.Instructions == GameHints.Instructions && a.Label == GameHints.Label);

            var ok = first.Said && guideFound && unrelatedLeftOut && repeat.Repeat && toldTopicsSent && stopped.Passed && preferenceSent &&
                stillStopped.Passed && notAskedAgain && again.Said && speaker.Said.Count == 2 && sameStart;
            var parsed = reply is null ? null : GameHints.Parse(reply);
            return new
            {
                ok,
                note = "The production hint keeper, App guide library and guide search on a FIXTURE guide about a made-up game (NOT a " +
                    "real wiki), FIXTURE places and preferences, and a FIXTURE Thinking pool member (answers made from the request, NOT " +
                    "AI). Live, the place and goal come from the game state, the preferences from your preferences, the model work runs " +
                    "as a Research job on a Thinking pool member, and the hint is brought up in the talk window at a quiet moment.",
                steps = new object[]
                {
                    Step("a hint at a new place", first), Step("the same place again (same topic)", repeat),
                    Step("a preference says stop", stopped), Step("the same place, the preference still there", stillStopped),
                    Step("the preference removed", again)
                },
                said = speaker.Said.Select(h => new { h.Place, h.Topic, h.Text }),
                guideSectionsFound = guideFound,
                unrelatedSectionsLeftOut = unrelatedLeftOut,
                toldTopicsSent,
                preferenceSent,
                notAskedAgainWhileStopped = notAskedAgain,
                instructionsStayTheSame = sameStart,
                requests = thinker.Asks.Count,
                counts = new { hints.Hints, hints.Passes, hints.Repeats, hints.Nothing, hints.Problems, hints.Places },
                limits = new
                {
                    everySeconds = (int)GameHints.DefaultEvery.TotalSeconds, holdForSeconds = (int)GameHints.DefaultHoldFor.TotalSeconds,
                    maximumPerPlace = GameHints.MaximumPerPlace, maximumSections = GameHints.MaximumSections,
                    inputCharacters = GameHints.InputCharacters, maximumHintCharacters = GameHints.MaximumHintCharacters,
                    maxOutputTokens = GameHints.MaxOutputTokens
                },
                reply = reply is null ? null : new { kind = parsed?.Kind, topic = parsed?.Topic, hint = parsed?.Hint, refused = parsed is null }
            };
        }
        finally
        {
            try { Directory.Delete(folder, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static object Step(string step, GameHintResult result) =>
        new { step, result.Said, result.Passed, result.Repeat, result.Nothing, result.Topic, result.Problem };

    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Places : IGamePlaceSource
    {
        public GamePlace? Current { get; private set; }
        public event Action<GamePlace>? Changed { add { } remove { } }
        internal GamePlace Set(GamePlace place) => Current = place;
    }

    private sealed class Preferences : IHintPreferences
    {
        internal string? Text { get; set; }
        public string? For(string game) => Text;
    }

    private sealed class Speaker : IHintSpeaker
    {
        internal List<GameHint> Said { get; } = [];
        public bool Quiet => true;
        public Task<bool> SayAsync(GameHint hint, CancellationToken cancellationToken)
        {
            Said.Add(hint);
            return Task.FromResult(true);
        }
    }

    // Answers from the request, as a model that obeys the instructions would: a pass when a preference stops hints, else the
    // secret at the place (the same topic again when it was told, which the hint keeper doesn't repeat).
    private sealed class FixtureThinker : IGuideThinker
    {
        private readonly List<GuideThinkingAsk> asks = [];
        public bool CanThink => true;
        internal IReadOnlyList<GuideThinkingAsk> Asks { get { lock (asks) return [.. asks]; } }

        public Task<(string? Text, string? Problem)> ThinkAsync(GuideThinkingAsk ask, CancellationToken cancellationToken)
        {
            lock (asks) asks.Add(ask);
            var text = ask.Text;
            var answer = text.Contains("Stop giving hints", StringComparison.Ordinal) ? GameHints.Pass
                : text.Contains("Moonlit Lake puzzle", StringComparison.Ordinal)
                    ? "Topic: Lantern puzzle\nHint: Light the three lanterns left to right and the bridge comes up. FIXTURE hint."
                    : "Topic: Secret room behind the waterfall\nHint: There's apparently a secret room behind the waterfall around here, with the Moonstone Ring. FIXTURE hint.";
            return Task.FromResult<(string?, string?)>((answer, null));
        }
    }

    private sealed class NoBuilder : IGuideBuilder
    {
        public Task<GuideBuildOutcome> BuildAsync(GuideBuildRequest request, IProgress<string>? progress, CancellationToken cancellationToken) =>
            Task.FromResult(new GuideBuildOutcome([], [], 0, 0, "not in this check"));
    }
}
