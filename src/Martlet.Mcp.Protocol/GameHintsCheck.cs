using System.IO;
using Martlet.Conversation;
using Martlet.Conversation.Guides;
using Martlet.Core.Activities;
using Martlet.Core.Preferences;

namespace Martlet.Mcp;

/// <summary>game_hints_check: hints and secrets (<see cref="GameHints"/>, docs/APP_GUIDES.md "Hints and secrets"), rehearsed with
/// the production hint keeper, App guide library (<see cref="AppGuideService"/>, <see cref="FileAppGuideStore"/>) and guide search
/// (<see cref="AppGuideHintGuide"/>) on a FIXTURE guide about a made-up game in a temporary folder, the production game state
/// (<see cref="GameProgress"/>, <see cref="GameProgressPlaces"/>) updated from FIXTURE screen notes, a FIXTURE preference in the production
/// preferences store (<see cref="UserPreferences"/>, <see cref="UserPreferencesHints"/>), and a FIXTURE Thinking pool member
/// (answers made from the request, NOT AI; nothing is sent). It shows a hint at a
/// place, no repeat of the same topic, a stop by a preference (and no new request while the preference stays) and a hint again
/// when the preference is gone. An optional <c>reply</c> is parsed the way a pool member's answer is.</summary>
internal static class GameHintsCheck
{
    private const string Game = "Starfall Valley (FIXTURE)";
    private const string Stop = "Stop giving hints unless I ask. (FIXTURE preference)";

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
            var saved = new UserPreferences(Path.Combine(folder, "preferences"));
            var preferences = new UserPreferencesHints(saved);
            var thinker = new FixtureThinker();
            var speaker = new Speaker();
            using var progress = new GameProgress(thinker, new ContextBoard());
            using var places = new GameProgressPlaces(progress);
            var changes = new List<GamePlace>();
            places.Changed += changes.Add;
            using var hints = new GameHints(places, preferences, new AppGuideHintGuide(guides, null), thinker, speaker, clock,
                (time, _) => { clock.Now += time; return Task.CompletedTask; });

            await progress.UpdateAsync(Game, null, "Screen over the last 9 s: a cave with glowing crystals and a waterfall. FIXTURE note.", cancellation);
            var cave = places.Current ?? new(Game, null, null);
            var placeFromState = changes.Count == 1 && cave.Place == "Crystal Cave, by the waterfall.";
            // The place change starts the hint in the background, as it does live.
            var first = await EndedAsync(hints, 1, cancellation);
            var guideFound = thinker.Asks.Count > 0 && thinker.Asks[0].Text.Contains("cracked wall behind the waterfall", StringComparison.Ordinal);
            var unrelatedLeftOut = thinker.Asks.Count > 0 && !thinker.Asks[0].Text.Contains("Turnips", StringComparison.Ordinal);
            clock.Now += TimeSpan.FromMinutes(10);
            var repeat = await hints.HintAsync(cave, cancellation);
            var toldTopicsSent = thinker.Asks.Count > 1 && thinker.Asks[1].Text.Contains("Hints already told here: Secret room behind the waterfall", StringComparison.Ordinal);

            var other = saved.Add("No hints for Skyrim. (FIXTURE preference)", ActivityScope.Game("Skyrim"), UserPreferences.Typed);
            var stop = saved.Add(Stop, ActivityScope.Game(Game), UserPreferences.Said);
            var otherGameLeftOut = preferences.For(Game) is { } note && !note.Contains("Skyrim", StringComparison.Ordinal);
            await progress.UpdateAsync(Game, null, "Screen over the last 9 s: a lake under the moon, three lanterns. FIXTURE note.", cancellation);
            var lake = places.Current ?? new(Game, null, null);
            clock.Now += TimeSpan.FromMinutes(10);
            var stopped = await EndedAsync(hints, 3, cancellation);
            var preferenceSent = thinker.Asks.Count > 2 && thinker.Asks[2].Text.Contains(Stop, StringComparison.Ordinal);
            var asksAfterStop = thinker.Asks.Count;
            var stillStopped = await hints.HintAsync(lake, cancellation);
            var notAskedAgain = thinker.Asks.Count == asksAfterStop;

            saved.Remove(stop.Preference?.Id);
            clock.Now += TimeSpan.FromMinutes(10);
            var again = await hints.HintAsync(lake, cancellation);
            var sameStart = thinker.Asks.All(a => a.Instructions == GameHints.Instructions && a.Label == GameHints.Label);

            var bothPlaces = changes.Count == 2 && lake.Place == "Moonlit Lake.";
            var ok = other.Done && stop.Done && otherGameLeftOut && placeFromState && bothPlaces && first.Said && guideFound && unrelatedLeftOut && repeat.Repeat && toldTopicsSent && stopped.Passed && preferenceSent &&
                stillStopped.Passed && notAskedAgain && again.Said && speaker.Said.Count == 2 && sameStart;
            var parsed = reply is null ? null : GameHints.Parse(reply);
            return new
            {
                ok,
                note = "The production hint keeper, App guide library and guide search on a FIXTURE guide about a made-up game (NOT a " +
                    "real wiki), the production game state updated from FIXTURE screen notes, FIXTURE preferences in the production " +
                    "preferences store (in the temporary folder), and a FIXTURE Thinking pool member (answers made from the request, NOT " +
                    "AI). Live, the place and goal come from the game state, the preferences from your preferences, the model work runs " +
                    "as a Research job on a Thinking pool member, and the hint is brought up in the talk window at a quiet moment.",
                steps = new object[]
                {
                    Step("a new place from the game state (in the background)", first), Step("the same place again (same topic)", repeat),
                    Step("a new place while a preference says stop (in the background)", stopped), Step("the same place, the preference still there", stillStopped),
                    Step("the preference removed", again)
                },
                said = speaker.Said.Select(h => new { h.Place, h.Topic, h.Text }),
                gameState = new { placeFromState, placeChanges = changes.Count, places = changes.Select(c => new { c.Place, c.Goal }) },
                guideSectionsFound = guideFound,
                unrelatedSectionsLeftOut = unrelatedLeftOut,
                toldTopicsSent,
                preferenceSent,
                preferences = new { added = stop.Done, scope = ActivityScope.Game(Game), otherGameLeftOut, removed = saved.All.Count == 1 },
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

    // Waits (at most 10 s) until <paramref name="jobs"/> hint jobs ended, and returns the last one's result.
    private static async Task<GameHintResult> EndedAsync(GameHints hints, int jobs, CancellationToken cancellation)
    {
        for (var i = 0; i < 1_000 && hints.Jobs < jobs; i++) await Task.Delay(10, cancellation);
        return hints.Jobs >= jobs && hints.Last is { } last ? last
            : new GameHintResult(Game, null, false, false, false, null, "no hint job ended in the background", TimeSpan.Zero);
    }

    private static object Step(string step, GameHintResult? result) => result is null ? new { step } :
        new { step, result.Said, result.Passed, result.Repeat, result.Nothing, result.Topic, result.Problem };

    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
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
            var text = ask.Text;
            // The game state's updates: the place and goal the FIXTURE screen notes show.
            if (ask.Label == GameProgress.Label)
                return Task.FromResult<(string?, string?)>((text.Contains("lake under the moon", StringComparison.Ordinal)
                    ? "Where: Moonlit Lake.\nGoal: Cross the lake.\nDoing: Looking at three lanterns."
                    : "Where: Crystal Cave, by the waterfall.\nGoal: Find the Moonstone Ring.\nDoing: Exploring.", null));
            lock (asks) asks.Add(ask);
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
