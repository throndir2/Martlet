using Martlet.Conversation;
using Martlet.Conversation.Guides;

namespace Martlet.Mcp;

/// <summary>game_progress_check: where the user is in a game (<see cref="GameProgress"/>, docs/CONVERSATION.md "Where you are in a
/// game"), rehearsed with the production note keeper and context board and a FIXTURE Thinking pool member (canned answers, NOT
/// AI; nothing is sent). Two FIXTURE screen summaries make a note and then a newer note that builds on it, a "[same]" answer
/// keeps it, a bad answer leaves the board as it was, and the board's snapshot shows where the note goes among the other notes.
/// An optional <c>reply</c> is cleaned the way a pool member's answer is.</summary>
internal static class GameProgressCheck
{
    internal static async Task<object> RunAsync(string? reply, CancellationToken cancellation)
    {
        var board = new ContextBoard();
        var thinker = new FixtureThinker(
            "Where: Sunken Crypt, lowest floor.\nDoing: Fighting two drowned knights.\nProgress: Early game. FIXTURE note.",
            "Where: Sunken Crypt, chapel cellar.\nDoing: Turning the second bronze valve.\nLately: Beat the drowned knights. FIXTURE note.",
            GameProgress.Same,
            "ok");
        using var progress = new GameProgress(thinker, board);
        const string game = "Skyrim (FIXTURE)";
        board.Post(ContextBoard.Screen, "Screen over the last 9 s: a flooded crypt; two knights attack. FIXTURE note.", DateTimeOffset.Now,
            TimeSpan.FromSeconds(45));
        var first = await progress.UpdateAsync(game, "What it is: an open-world fantasy game. FIXTURE brief.",
            "Screen over the last 9 s: a flooded crypt; two knights attack. FIXTURE note.", cancellation);
        var second = await progress.UpdateAsync(game, null, "Screen over the last 9 s: a valve turns in a cellar. FIXTURE note.", cancellation);
        var same = await progress.UpdateAsync(game, null, "Screen over the last 9 s: still in the cellar. FIXTURE note.", cancellation);
        var bad = await progress.UpdateAsync(game, null, "Screen over the last 9 s: a loading screen. FIXTURE note.", cancellation);
        var snapshot = board.Snapshot(DateTimeOffset.Now);
        var note = snapshot.Notes.FirstOrDefault(n => n.Source == ContextBoard.Game);
        var asks = thinker.Asks;
        var secondBuiltOnFirst = asks.Count > 1 && asks[1].Text.Contains("Earlier note", StringComparison.Ordinal) &&
            asks[1].Text.Contains("lowest floor", StringComparison.Ordinal);
        var sameStart = asks.All(a => a.Instructions == GameProgress.Instructions && a.Label == GameProgress.Label);
        var afterScreen = snapshot.Sources.SequenceEqual([ContextBoard.Screen, ContextBoard.Game]);
        var ok = first.Updated && second.Updated && same.Kept && bad.Problem is not null && note is not null &&
            note.Text.Contains("chapel cellar", StringComparison.Ordinal) && secondBuiltOnFirst && sameStart && afterScreen;
        string? cleaned = null;
        if (reply is not null) cleaned = GameProgress.Clean(reply);
        return new
        {
            ok,
            note = "The production game note keeper and context board with a FIXTURE Thinking pool member (canned answers, NOT AI) " +
                "and FIXTURE screen summaries (NOT anything seen). Live, it runs as a Research job on a Thinking pool member while " +
                "a game (or an app with a guide) is in front, App guides watch the front window and screen summaries are on.",
            updates = new object[]
            {
                new { step = "first", first.Updated, first.Kept, first.Characters, first.Problem },
                new { step = "second (with the earlier note)", second.Updated, second.Kept, second.Characters, second.Problem },
                new { step = "[same]", same.Updated, same.Kept, same.Characters, same.Problem },
                new { step = "a bad answer", bad.Updated, bad.Kept, bad.Characters, bad.Problem }
            },
            secondBuiltOnFirst,
            instructionsStayTheSame = sameStart,
            board = new
            {
                sources = snapshot.Sources,
                game = note is null ? null : new { note.Text, maxAgeSeconds = (int)note.MaxAge.TotalSeconds },
                gameAfterScreen = afterScreen
            },
            counts = new { progress.Updates, progress.Kept, progress.Problems, progress.Games },
            limits = new
            {
                everySeconds = (int)GameProgress.DefaultEvery.TotalSeconds, noteAgeSeconds = (int)GameProgress.NoteAge.TotalSeconds,
                earlierNoteHours = (int)GameProgress.EarlierAge.TotalHours, maximumCharacters = GameProgress.MaximumCharacters,
                maximumGames = GameProgress.MaximumGames, maxOutputTokens = GameProgress.MaxOutputTokens
            },
            reply = reply is null ? null : new { cleaned, same = cleaned == GameProgress.Same, board = cleaned is null or GameProgress.Same ? null : GameProgress.BoardText(game, cleaned) }
        };
    }

    private sealed class FixtureThinker(params string[] answers) : IGuideThinker
    {
        private readonly List<GuideThinkingAsk> asks = [];
        public bool CanThink => true;
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
