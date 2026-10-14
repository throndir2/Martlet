using Martlet.Conversation.Guides;

namespace Martlet.Conversation.Tests;

/// <summary>Where the user is in a game (docs/CONVERSATION.md, "Where you are in a game"): a Thinking pool member keeps a running
/// note from the screen summaries, the earlier note and the brief, and the note goes on the context board as source "game". The
/// pool member is a stand-in.</summary>
public sealed class GameProgressTests
{
    [Fact]
    public async Task A_screen_summary_makes_a_note_on_the_context_board_and_the_next_one_builds_on_it()
    {
        var clock = new Clock();
        var board = new ContextBoard();
        var thinker = new Thinker("Where: Sunken Crypt, lowest floor.\nDoing: Fighting the drowned knights.",
            "**Where:** Sunken Crypt, chapel cellar.\n- Doing: Turning the second valve.\nLately: Beat the drowned knights.");
        using var progress = new GameProgress(thinker, board, clock);

        var first = await progress.UpdateAsync("Skyrim", "What it is: an open-world fantasy game.", "Screen over the last 9 s: a flooded crypt, two knights attack.");
        Assert.True(first.Updated);
        var note = Assert.Single(board.Snapshot(clock.GetLocalNow()).Notes, n => n.Source == ContextBoard.Game);
        Assert.StartsWith("Where the user is in Skyrim", note.Text, StringComparison.Ordinal);
        Assert.Contains("Where: Sunken Crypt, lowest floor.; Doing: Fighting the drowned knights.", note.Text, StringComparison.Ordinal);
        Assert.Equal(GameProgress.NoteAge, note.MaxAge);
        Assert.Contains("About the game:\nWhat it is: an open-world fantasy game.", thinker.Asks[0].Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Earlier note", thinker.Asks[0].Text, StringComparison.Ordinal);
        Assert.Equal(GameProgress.Instructions, thinker.Asks[0].Instructions);

        clock.Now += TimeSpan.FromMinutes(2);
        var second = await progress.UpdateAsync("Skyrim", null, "Screen over the last 9 s: a valve turns in a cellar.");
        Assert.True(second.Updated);
        Assert.Contains("Earlier note (2 min ago):\nWhere: Sunken Crypt, lowest floor.", thinker.Asks[1].Text, StringComparison.Ordinal);
        Assert.Equal("Where: Sunken Crypt, chapel cellar.\nDoing: Turning the second valve.\nLately: Beat the drowned knights.",
            progress.Note("Skyrim")!.Value.Note);
        Assert.Equal(2, progress.Updates);
        // The instructions never change, so every request starts the same way.
        Assert.Equal(thinker.Asks[0].Instructions, thinker.Asks[1].Instructions);
    }

    [Fact]
    public async Task Same_keeps_the_note_and_posts_it_again_and_a_bad_answer_keeps_the_board_as_it_was()
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
        // Posted again, so it stays fresh for another 10 minutes.
        clock.Now += TimeSpan.FromMinutes(5);
        Assert.Contains("Ember Mines", Assert.Single(board.Snapshot(clock.GetLocalNow()).Notes).Text, StringComparison.Ordinal);

        var bad = await progress.UpdateAsync("Skyrim", null, "A loading screen.");
        Assert.Equal("the answer wasn't a note", bad.Problem);
        Assert.Equal(1, progress.Problems);
        Assert.Contains("Ember Mines", Assert.Single(board.Snapshot(clock.GetLocalNow()).Notes).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Each_game_has_its_own_note_and_an_old_note_is_not_given_again()
    {
        var clock = new Clock();
        var thinker = new Thinker("Where: Pelican Town.", "Where: Whiterun.", "Where: Riverwood.");
        using var progress = new GameProgress(thinker, new ContextBoard(), clock);
        await progress.UpdateAsync("Stardew Valley", null, "A farm.");
        await progress.UpdateAsync("Skyrim", null, "A city gate.");
        Assert.DoesNotContain("Pelican", thinker.Asks[1].Text, StringComparison.Ordinal);
        Assert.Equal(2, progress.Games);

        clock.Now += GameProgress.EarlierAge;
        await progress.UpdateAsync("Skyrim", null, "A village.");
        Assert.DoesNotContain("Earlier note", thinker.Asks[2].Text, StringComparison.Ordinal);
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
        // The same screen note again is not news.
        progress.Screen("Skyrim", null, "Mining in a cave.");
        Assert.Single(thinker.Asks);

        thinker.CanThink = false;
        progress.Screen("Skyrim", null, "A city gate.");
        await Task.Delay(100);
        Assert.Single(thinker.Asks);
    }

    [Fact]
    public void The_note_is_plain_bounded_text()
    {
        Assert.Null(GameProgress.Clean(null));
        Assert.Null(GameProgress.Clean("NONE"));
        Assert.Equal(GameProgress.Same, GameProgress.Clean("\"[same]\""));
        Assert.Equal("Where: Frost Caves.", GameProgress.Clean("```\n## **Where:** Frost Caves.\n```"));
        var cut = GameProgress.Clean(string.Join('\n', Enumerable.Repeat("Lately: a long thing happened in the caves.", 30)))!;
        Assert.True(cut.Length <= GameProgress.MaximumCharacters);
        Assert.Equal("Where the user is in Skyrim (your running note from the screen; it may be a little out of date): Where: A.; Doing: B.",
            GameProgress.BoardText("Skyrim", "Where: A.\nDoing: B."));
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
