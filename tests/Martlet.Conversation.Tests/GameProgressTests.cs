using System.Text;
using Martlet.Conversation.Guides;
using Martlet.Core.Settings;

namespace Martlet.Conversation.Tests;

/// <summary>The game state (docs/CONVERSATION.md, "Where you are in a game"): a Thinking pool member fills the fields the screen
/// summaries show, the user's corrections win, each game keeps its playthroughs, the state is saved and read again as not seen
/// this session, and the board line keeps place and goal first within its byte limit. The pool member is a stand-in.</summary>
public sealed class GameProgressTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "martlet-game-progress-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task An_update_changes_only_the_fields_it_shows_and_the_next_request_carries_the_rest()
    {
        var clock = new Clock();
        var board = new ContextBoard();
        var thinker = new Thinker("Where: Sunken Crypt, lowest floor.\nGoal: Find the valve key.\nLevel: 9\nDoing: Fighting the drowned knights.",
            "**Where:** Sunken Crypt, chapel cellar.\n- Doing: Turning the second valve.\nLately: Beat the drowned knights.\nBuild: unknown");
        using var progress = new GameProgress(thinker, board, clock);
        var changes = new List<GamePlaceChange>();
        progress.PlaceOrGoalChanged += changes.Add;

        var first = await progress.UpdateAsync("Skyrim", "What it is: an open-world fantasy game.", "Screen over the last 9 s: a flooded crypt, two knights attack.");
        Assert.True(first.Updated);
        var note = Assert.Single(board.Snapshot(clock.GetLocalNow()).Notes, n => n.Source == ContextBoard.Game);
        Assert.StartsWith("Where the user is in Skyrim", note.Text, StringComparison.Ordinal);
        Assert.Contains("Where: Sunken Crypt, lowest floor.; Goal: Find the valve key.; Doing: Fighting the drowned knights.; Level: 9", note.Text, StringComparison.Ordinal);
        Assert.Equal(GameProgress.NoteAge, note.MaxAge);
        Assert.Contains("About the game:\nWhat it is: an open-world fantasy game.", thinker.Asks[0].Text, StringComparison.Ordinal);
        Assert.DoesNotContain("What you noted before", thinker.Asks[0].Text, StringComparison.Ordinal);
        Assert.Equal(GameProgress.Instructions, thinker.Asks[0].Instructions);
        Assert.Equal("Sunken Crypt, lowest floor.", Assert.Single(changes).Place);

        clock.Now += TimeSpan.FromMinutes(2);
        var second = await progress.UpdateAsync("Skyrim", null, "Screen over the last 9 s: a valve turns in a cellar.");
        Assert.True(second.Updated);
        Assert.Contains("What you noted before:\nWhere: Sunken Crypt, lowest floor. (2 min ago)\nGoal: Find the valve key. (2 min ago)",
            thinker.Asks[1].Text, StringComparison.Ordinal);
        var state = progress.Read()!;
        Assert.Equal("Skyrim", state.Game);
        Assert.Equal(GameStateFile.MainPlaythrough, state.Playthrough);
        Assert.Equal("Sunken Crypt, chapel cellar.", state.Place);
        // Missing means unknown, not removed; "unknown" is not a value.
        Assert.Equal("Find the valve key.", state.Goal);
        Assert.Equal("9", state.Field(GameFields.CharacterLevel)!.Value);
        Assert.Null(state.Field(GameFields.Build));
        Assert.Equal(2, changes.Count);
        Assert.Equal("Sunken Crypt, lowest floor.", changes[1].EarlierPlace);
        Assert.Equal(thinker.Asks[0].Instructions, thinker.Asks[1].Instructions);
    }

    [Fact]
    public async Task Same_keeps_the_state_and_posts_it_again_and_a_bad_answer_keeps_the_board_as_it_was()
    {
        var clock = new Clock();
        var board = new ContextBoard();
        var thinker = new Thinker("Where: Ember Mines.\nDoing: Mining iron ore.", "[same]", "ok");
        using var progress = new GameProgress(thinker, board, clock);
        await progress.UpdateAsync("Skyrim", null, "Mining in a cave.");
        clock.Now += TimeSpan.FromMinutes(9);

        var same = await progress.UpdateAsync("Skyrim", null, "Still mining in a cave.");
        Assert.True(same.Kept);
        Assert.False(same.Updated);
        clock.Now += TimeSpan.FromMinutes(5);
        Assert.Contains("Ember Mines", Assert.Single(board.Snapshot(clock.GetLocalNow()).Notes).Text, StringComparison.Ordinal);

        var bad = await progress.UpdateAsync("Skyrim", null, "A loading screen.");
        Assert.Equal("the answer wasn't a note", bad.Problem);
        Assert.Equal(1, progress.Problems);
        Assert.Contains("Ember Mines", Assert.Single(board.Snapshot(clock.GetLocalNow()).Notes).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_users_correction_wins_over_the_screen_for_an_hour()
    {
        var clock = new Clock();
        var board = new ContextBoard();
        var thinker = new Thinker("Level: 9\nWhere: Whiterun.", "Level: 10");
        using var progress = new GameProgress(thinker, board, clock);
        await progress.UpdateAsync("Skyrim", null, "A city gate.");

        var (state, problem) = progress.Correct(null, "Level", "12");
        Assert.Null(problem);
        Assert.True(state!.Field(GameFields.CharacterLevel)!.ByUser);
        Assert.Contains("Level (the user said): 12", board.Snapshot(clock.GetLocalNow()).Notes.Single().Text, StringComparison.Ordinal);

        clock.Now += TimeSpan.FromMinutes(10);
        await progress.UpdateAsync("Skyrim", null, "A level-up screen.");
        Assert.Contains("(the user said, 10 min ago)", thinker.Asks[1].Text, StringComparison.Ordinal);
        Assert.Equal("12", progress.Read()!.Field(GameFields.CharacterLevel)!.Value);

        clock.Now += GameProgress.UserHold;
        await progress.UpdateAsync("Skyrim", null, "Another level-up screen.");
        Assert.Equal("10", progress.Read()!.Field(GameFields.CharacterLevel)!.Value);

        Assert.NotNull(progress.Correct(null, "mood", "happy").Problem);
        progress.Correct(null, GameFields.Place, "");
        Assert.Null(progress.Read()!.Place);
    }

    [Fact]
    public async Task The_state_is_saved_and_read_again_as_not_seen_this_session_until_new_evidence_shows_it()
    {
        var clock = new Clock();
        var thinker = new Thinker("Where: Whiterun.\nGoal: Talk to the Jarl.", "Where: Whiterun.");
        using (var progress = new GameProgress(thinker, new ContextBoard(), clock, root))
            await progress.UpdateAsync("Skyrim", null, "A city gate.");
        Assert.True(File.Exists(GameStateFile.PathOf(root)));

        clock.Now += TimeSpan.FromHours(20);
        var board = new ContextBoard();
        using var restarted = new GameProgress(thinker, board, clock, root);
        var loaded = restarted.Read("skyrim")!;
        Assert.All(loaded.Fields, f => Assert.False(f.SeenThisSession));
        Assert.Contains("Where (last session): Whiterun.", GameProgress.BoardText(loaded, clock.GetUtcNow()), StringComparison.Ordinal);

        // The game comes to the front: its saved state goes on the board at once.
        restarted.Screen("Skyrim", null, "A market.");
        Assert.Contains("Goal (last session): Talk to the Jarl.", board.Snapshot(clock.GetLocalNow()).Notes.Single().Text, StringComparison.Ordinal);
        await WaitAsync(() => restarted.Updates + restarted.Kept == 1);
        var confirmed = restarted.Read("Skyrim")!;
        Assert.True(confirmed.Field(GameFields.Place)!.SeenThisSession);
        Assert.False(confirmed.Field(GameFields.Goal)!.SeenThisSession);
        Assert.Contains("from an earlier session", thinker.Asks[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Each_game_keeps_its_playthroughs_and_a_game_can_be_forgotten_and_saving_turned_off()
    {
        var clock = new Clock();
        var board = new ContextBoard();
        using var progress = new GameProgress(new Thinker(), board, clock, root);
        progress.Correct("Elden Ring", GameFields.Place, "Limgrave");
        var (fresh, _) = progress.UsePlaythrough(null, "new game plus");
        Assert.Equal("new game plus", fresh!.Playthrough);
        Assert.Empty(fresh.Fields);
        Assert.Equal(["main", "new game plus"], fresh.Playthroughs);
        progress.UsePlaythrough("elden ring", "main");
        Assert.Equal("Limgrave", progress.Read("Elden Ring")!.Place);
        clock.Now += TimeSpan.FromMinutes(1);
        progress.Correct("Stardew Valley", GameFields.Place, "Pelican Town");
        Assert.Equal(2, progress.Games);
        Assert.Equal("Stardew Valley", progress.ReadAll()[0].Game);

        Assert.True(progress.Forget("Stardew Valley"));
        Assert.Null(progress.Read("Stardew Valley"));
        Assert.Empty(board.Snapshot(clock.GetLocalNow()).Notes);
        Assert.Single(GameStateFile.Load(root).Playthroughs.Select(p => p.Game).Distinct());

        progress.SetSaving(false);
        var off = GameStateFile.Load(root);
        Assert.False(off.Save);
        Assert.Empty(off.Playthroughs);
        Assert.Equal("Limgrave", progress.Read("Elden Ring")!.Place);
        using var again = new GameProgress(new Thinker(), new ContextBoard(), clock, root);
        Assert.False(again.Saving);
        Assert.Null(again.Read("Elden Ring"));
    }

    [Fact]
    public async Task Screen_updates_in_the_background_at_most_once_in_a_while_and_not_without_a_pool_member()
    {
        var clock = new Clock();
        var board = new ContextBoard();
        var thinker = new Thinker("Where: Ember Mines.", "Where: Whiterun.");
        using var progress = new GameProgress(thinker, board, clock);
        Assert.True(progress.WantsScreen);

        progress.Screen("Skyrim", null, "Mining in a cave.");
        await WaitAsync(() => progress.Updates == 1);
        Assert.False(progress.WantsScreen);
        progress.Screen("Skyrim", null, "A city gate.");
        clock.Now += GameProgress.DefaultEvery;
        progress.Screen("Skyrim", null, "Mining in a cave.");
        Assert.Single(thinker.Asks);

        thinker.CanThink = false;
        progress.Screen("Skyrim", null, "A city gate.");
        await Task.Delay(100);
        Assert.Single(thinker.Asks);
    }

    [Fact]
    public void The_answer_is_parsed_into_bounded_fields_and_the_board_line_keeps_place_and_goal_within_its_bytes()
    {
        Assert.Null(GameProgress.Parse(null));
        Assert.Null(GameProgress.Parse("NONE"));
        Assert.True(GameProgress.Parse("\"[same]\"")!.Same);
        var parsed = GameProgress.Parse("```\n## **Where:** Frost Caves.\nGear: Iron sword\nMood: fine\nLevel: n/a\n```")!;
        Assert.Equal(new Dictionary<string, string> { [GameFields.Place] = "Frost Caves.", [GameFields.Equipment] = "Iron sword" }, parsed.Fields);
        var lately = GameProgress.Parse("Lately: " + new string('x', 1000))!.Fields[GameFields.Lately];
        Assert.True(lately.Length <= GameFields.MaximumCharacters(GameFields.Lately));

        var now = new DateTimeOffset(2026, 10, 13, 18, 0, 0, TimeSpan.Zero);
        GameStateField Field(string name, string value) => new(name, GameFields.Label(name), value, now, false, true);
        Assert.Equal("Where the user is in Skyrim (your running note from the screen; it may be a little out of date): Where: A.; Doing: B.",
            GameProgress.BoardText(new("Skyrim", "main", now, [Field(GameFields.Place, "A."), Field(GameFields.Doing, "B.")], ["main"]), now));
        var big = new GameState("Skyrim", "main", now, [.. GameFields.All.Select(f => Field(f, string.Concat(Enumerable.Repeat("é long words ", 30))[..GameFields.MaximumCharacters(f)]))], ["main"]);
        var line = GameProgress.BoardText(big, now)!;
        Assert.True(Encoding.UTF8.GetByteCount(line) <= ContextBoard.MaximumNoteUtf8Bytes);
        Assert.Contains("Where: é long", line, StringComparison.Ordinal);
        Assert.Contains("Goal: é long", line, StringComparison.Ordinal);
        // A fleeting field older than six hours is not shown again.
        Assert.Null(GameProgress.BoardText(new("Skyrim", "main", now, [Field(GameFields.Doing, "B.") with { At = now - GameProgress.EarlierAge }], ["main"]), now));
    }

    [Fact]
    public void The_reply_tool_reads_and_corrects_with_the_same_definition_every_time()
    {
        Assert.Equal(GameStateTools.Definition.ParametersJson, GameStateTools.ParametersJson);
        Assert.Contains("\"character_level\"", GameStateTools.ParametersJson, StringComparison.Ordinal);
        Assert.Equal(((string?, string?, string?, string?, string?))(GameStateTools.Correct, null, "place", "Whiterun", null),
            GameStateTools.Parse("{\"action\":\"correct\",\"field\":\"place\",\"value\":\"Whiterun\"}"));
        Assert.NotNull(GameStateTools.Parse("{\"action\":\"correct\"}").Problem);
        Assert.NotNull(GameStateTools.Parse("{\"action\":\"fly\"}").Problem);
        Assert.Equal(GameStateTools.Read, GameStateTools.Parse("{\"action\":\"read\",\"game\":\"Skyrim\"}").Action);

        var now = new DateTimeOffset(2026, 10, 13, 18, 0, 0, TimeSpan.Zero);
        var text = GameStateTools.Format(new("Skyrim", "main", now, [new(GameFields.Place, "Where", "Whiterun", now.AddMinutes(-5), true, false)], ["main", "mage"]), now);
        Assert.Equal("Game: Skyrim\nPlaythrough: main (others: mage)\nWhere: Whiterun (the user said, in an earlier session, 5 min ago)", text);
        Assert.StartsWith("Nothing is known", GameStateTools.Format(null, now), StringComparison.Ordinal);
    }

    private static async Task WaitAsync(Func<bool> done)
    {
        for (var i = 0; i < 200 && !done(); i++) await Task.Delay(10);
        Assert.True(done());
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 13, 18, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class Thinker(params string[] answers) : IGuideThinker
    {
        private readonly List<GuideThinkingAsk> asks = [];
        public bool CanThink { get; set; } = true;
        internal IReadOnlyList<GuideThinkingAsk> Asks { get { lock (asks) return [.. asks]; } }

        public Task<(string? Text, string? Problem)> ThinkAsync(GuideThinkingAsk ask, CancellationToken cancellationToken)
        {
            lock (asks)
            {
                asks.Add(ask);
                return Task.FromResult<(string?, string?)>((answers[Math.Min(asks.Count - 1, answers.Length - 1)], null));
            }
        }
    }
}
