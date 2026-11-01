using System.IO;
using System.Text;
using Martlet.Conversation;
using Martlet.Conversation.Guides;
using Martlet.Core.Settings;
using Martlet.Core.Sync;

namespace Martlet.Mcp;

/// <summary>game_progress_check and the game state tools (<see cref="GameProgress"/>, docs/CONVERSATION.md "Where you are in a
/// game"). The check rehearses the production state keeper and context board with a FIXTURE Thinking pool member (canned answers,
/// NOT AI; nothing is sent) in a disposable data folder: updates that change only the fields they show, "[same]", a bad answer,
/// the user's correction winning over the screen, the saved state read again as not seen this session, a new playthrough, a
/// deleted game, saving off, the board line's byte limit and the state shared between two simulated computers.
/// game_state_read, game_state_correct and game_state_delete read and change a data folder's game-state.json through the same
/// keeper.</summary>
internal static class GameProgressCheck
{
    private const string Game = "Skyrim (FIXTURE)";

    internal static async Task<object> RunAsync(string? reply, CancellationToken cancellation)
    {
        var root = Path.Combine(Path.GetTempPath(), "martlet-game-state-check-" + Guid.NewGuid().ToString("N"));
        var dataA = Path.Combine(root, "computer-a");
        var dataB = Path.Combine(root, "computer-b");
        Directory.CreateDirectory(dataA);
        Directory.CreateDirectory(dataB);
        var steps = new List<object>();
        void Step(string name, bool passed, object? detail = null) => steps.Add(new { step = name, passed, detail });
        try
        {
            var board = new ContextBoard();
            var thinker = new FixtureThinker(
                "Where: Sunken Crypt, lowest floor.\nGoal: Find the Bronze Valve key.\nCharacter: Lydia\nClass: Nord warrior\nLevel: 9\n" +
                "Equipment: Steel sword, iron shield. FIXTURE",
                "Where: Sunken Crypt, chapel cellar.\nDoing: Turning the second bronze valve.\nLately: Beat the drowned knights. FIXTURE",
                GameProgress.Same,
                "ok",
                "Level: 10\nWhere: Sunken Crypt, chapel cellar.",
                "Where: Sunken Crypt, chapel cellar.");
            var changes = new List<GamePlaceChange>();
            using (var progress = new GameProgress(thinker, board, null, dataA))
            {
                progress.PlaceOrGoalChanged += changes.Add;
                board.Post(ContextBoard.Screen, "Screen over the last 9 s: a flooded crypt; two knights attack. FIXTURE note.", DateTimeOffset.Now,
                    TimeSpan.FromSeconds(45));
                var first = await progress.UpdateAsync(Game, "What it is: an open-world fantasy game. FIXTURE brief.",
                    "Screen over the last 9 s: a flooded crypt; two knights attack. FIXTURE note.", cancellation);
                var afterFirst = progress.Read();
                Step("a screen summary fills the fields it shows and raises the place-or-goal event",
                    first.Updated && afterFirst?.Place == "Sunken Crypt, lowest floor." && afterFirst.Field(GameFields.CharacterLevel)?.Value == "9" &&
                    changes.Count == 1 && changes[0].EarlierPlace is null, new { first.Fields, changes = changes.Count });

                var second = await progress.UpdateAsync(Game, null, "Screen over the last 9 s: a valve turns in a cellar. FIXTURE note.", cancellation);
                var afterSecond = progress.Read()!;
                var asks = thinker.Asks;
                Step("the next update changes only what it shows: the goal, character and equipment stay, and the request carried them",
                    second.Updated && afterSecond.Place == "Sunken Crypt, chapel cellar." && afterSecond.Goal == "Find the Bronze Valve key." &&
                    afterSecond.Field(GameFields.Equipment) is not null && asks[1].Text.Contains("What you noted before", StringComparison.Ordinal) &&
                    asks[1].Text.Contains("lowest floor", StringComparison.Ordinal) && changes.Count == 2,
                    new { fields = afterSecond.Fields.Select(f => f.Name) });

                var same = await progress.UpdateAsync(Game, null, "Screen over the last 9 s: still in the cellar. FIXTURE note.", cancellation);
                var bad = await progress.UpdateAsync(Game, null, "Screen over the last 9 s: a loading screen. FIXTURE note.", cancellation);
                Step("\"[same]\" keeps the state, and an answer that isn't one changes nothing", same.Kept && bad.Problem is not null &&
                    progress.Read()!.Place == "Sunken Crypt, chapel cellar.", new { same = same.Kept, bad = bad.Problem });

                var (corrected, refused) = progress.Correct(null, "level", "12");
                var held = await progress.UpdateAsync(Game, null, "Screen over the last 9 s: a level-up screen. FIXTURE note.", cancellation);
                var level = progress.Read()!.Field(GameFields.CharacterLevel);
                Step("the user's correction wins over a later screen summary", refused is null && corrected is not null &&
                    level is { Value: "12", ByUser: true } && thinker.Asks.Count >= 5, new { held.Updated, held.Kept, level = level?.Value });

                var note = board.Snapshot(DateTimeOffset.Now).Notes.FirstOrDefault(n => n.Source == ContextBoard.Game);
                Step("the board line goes right after the screen note, place and goal first",
                    note is not null && board.Snapshot(DateTimeOffset.Now).Sources.SequenceEqual([ContextBoard.Screen, ContextBoard.Game]) &&
                    note.Text.IndexOf("Where:", StringComparison.Ordinal) < note.Text.IndexOf("Goal:", StringComparison.Ordinal) &&
                    note.Text.IndexOf("Goal:", StringComparison.Ordinal) < note.Text.IndexOf("Level", StringComparison.Ordinal),
                    new { note?.Text, bytes = note is null ? 0 : Encoding.UTF8.GetByteCount(note.Text) });
            }

            var saved = GameStateFile.TryLoad(dataA);
            using (var restarted = new GameProgress(thinker, new ContextBoard(), null, dataA))
            {
                var loaded = restarted.Read(Game);
                var line = loaded is null ? null : GameProgress.BoardText(loaded, DateTimeOffset.UtcNow);
                Step("after a restart the saved state is read again, marked not seen this session",
                    saved is { Save: true, Playthroughs.Count: 1 } && loaded is not null && loaded.Fields.All(f => !f.SeenThisSession) &&
                    line?.Contains("Where (last session):", StringComparison.Ordinal) == true &&
                    line.Contains("Level (the user said):", StringComparison.Ordinal), new { line });
                await restarted.UpdateAsync(Game, null, "Screen over the last 9 s: the chapel cellar again. FIXTURE note.", cancellation);
                var confirmed = restarted.Read(Game)!;
                Step("new evidence confirms a loaded fact", confirmed.Field(GameFields.Place)!.SeenThisSession &&
                    !confirmed.Field(GameFields.Goal)!.SeenThisSession);

                var (fresh, _) = restarted.UsePlaythrough(Game, "mage run");
                Step("a new playthrough starts empty and the first one stays", fresh is { Playthrough: "mage run", Fields.Count: 0, Playthroughs.Count: 2 });
                restarted.UsePlaythrough(Game, GameStateFile.MainPlaythrough);

                var longValue = string.Concat(Enumerable.Repeat("a very long place name ", 20));
                restarted.Correct(Game, GameFields.Goal, string.Concat(Enumerable.Repeat("a very long goal ", 20)));
                restarted.Correct(Game, GameFields.Place, longValue);
                var bounded = GameProgress.BoardText(restarted.Read(Game)!, DateTimeOffset.UtcNow)!;
                Step("the board line stays within its byte limit and keeps place and goal",
                    Encoding.UTF8.GetByteCount(bounded) <= ContextBoard.MaximumNoteUtf8Bytes && bounded.Contains("Where", StringComparison.Ordinal) &&
                    bounded.Contains("Goal", StringComparison.Ordinal), new { bytes = Encoding.UTF8.GetByteCount(bounded), limit = ContextBoard.MaximumNoteUtf8Bytes });

                // Two computers: A's state reaches B through the shared settings.
                var nodeA = new SharedSettingsNode(dataA, "desktop-a", [GameStateFile.Section(dataA)]);
                var onB = new GameProgress(thinker, new ContextBoard(), null, dataB);
                var nodeB = new SharedSettingsNode(dataB, "desktop-b", [GameStateFile.Section(dataB, onB.Followed)]);
                var now = DateTimeOffset.UtcNow;
                await nodeB.SyncAsync([], true, now, cancellation);
                await nodeA.SyncAsync([nodeB.Document], true, now.AddSeconds(15), cancellation);
                var followed = await nodeB.SyncAsync([nodeA.Document], true, now.AddSeconds(30), cancellation);
                Step("another computer takes the same game state through the shared settings",
                    followed.Applied.Any(c => c.Key == GameStateFile.SharedKey) && onB.Read(Game)?.Playthroughs.Count == 2 &&
                    SettingScopes.IsAccountKey(GameStateFile.SharedKey), new { applied = followed.Applied.Count, scope = SettingScopes.Of(GameStateFile.SharedKey) });
                onB.Dispose();

                var forgot = restarted.Forget(Game);
                Step("deleting a game removes it from memory and disk", forgot && restarted.Read(Game) is null &&
                    GameStateFile.Load(dataA).Playthroughs.Count == 0);

                restarted.Correct(Game, GameFields.Place, "Whiterun. FIXTURE");
                restarted.SetSaving(false);
                var off = GameStateFile.Load(dataA);
                Step("with saving off nothing is kept on disk, and this run keeps its state in memory",
                    !off.Save && off.Playthroughs.Count == 0 && restarted.Read(Game)?.Place == "Whiterun. FIXTURE");
            }

            var sameStart = thinker.Asks.All(a => a.Instructions == GameProgress.Instructions && a.Label == GameProgress.Label);
            var parsed = reply is null ? null : GameProgress.Parse(reply);
            return new
            {
                ok = sameStart && steps.All(step => (bool)step.GetType().GetProperty("passed")!.GetValue(step)!),
                note = "The production game state keeper, context board and shared-settings section with a FIXTURE Thinking pool member " +
                    "(canned answers, NOT AI), FIXTURE screen summaries (NOT anything seen) and disposable data folders. Live, it runs as " +
                    "a Research job on a Thinking pool member while a game (or an app with a guide) is in front, App guides watch the " +
                    "front window and screen summaries are on.",
                steps,
                instructionsStayTheSame = sameStart,
                limits = new
                {
                    everySeconds = (int)GameProgress.DefaultEvery.TotalSeconds, noteAgeSeconds = (int)GameProgress.NoteAge.TotalSeconds,
                    fleetingHours = (int)GameProgress.EarlierAge.TotalHours, userHoldMinutes = (int)GameProgress.UserHold.TotalMinutes,
                    maximumGames = GameStateFile.MaximumGames, playthroughsPerGame = GameStateFile.MaximumPlaythroughsPerGame,
                    boardBytes = ContextBoard.MaximumNoteUtf8Bytes, maxOutputTokens = GameProgress.MaxOutputTokens, fields = GameFields.All
                },
                reply = reply is null ? null : new
                {
                    same = parsed?.Same ?? false,
                    fields = parsed?.Fields,
                    understood = parsed is not null
                }
            };
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>game_state_read: a data folder's saved game state (game-state.json): saving on or off and each game's playthroughs
    /// with their fields. Game facts only.</summary>
    internal static object Read(string dataDirectory)
    {
        var path = GameStateFile.PathOf(dataDirectory);
        if (!File.Exists(path)) return new { state = "none", save = true, why = "No game state was saved with this data directory yet.", games = Array.Empty<object>() };
        if (GameStateFile.TryLoad(dataDirectory) is not { } file)
            return new { state = "unreadable", why = $"{GameStateFile.FileName} can't be read or was written by a newer Martlet." };
        return new
        {
            state = "loaded",
            save = file.Save,
            games = file.Playthroughs.GroupBy(p => p.Game, StringComparer.OrdinalIgnoreCase).Select(g => new
            {
                game = g.First().Game,
                key = GameStateFile.KeyOf(g.First().Game),
                playthroughs = g.Select(p => new
                {
                    playthrough = p.Playthrough, current = p.Current, updatedAt = p.UpdatedAt,
                    fields = GameFields.All.Where(p.Facts.ContainsKey).Select(f => new
                    {
                        name = f, label = GameFields.Label(f), value = p.Facts[f].Value, at = p.Facts[f].At, byUser = p.Facts[f].ByUser
                    })
                })
            })
        };
    }

    /// <summary>game_state_correct: the user's correction of one field (or a playthrough chosen) in a data folder's game state,
    /// through the production keeper. A running Martlet reads the file again before its next use of the state.</summary>
    internal static object Correct(string dataDirectory, string game, string? field, string? value, string? playthrough)
    {
        using var progress = new GameProgress(new FixtureThinker(), new ContextBoard(), null, dataDirectory);
        string? problem = null;
        if (playthrough is not null) problem = progress.UsePlaythrough(game, playthrough).Problem;
        if (problem is null && field is not null) problem = progress.Correct(game, field, value).Problem;
        if (problem is null && field is null && playthrough is null) problem = "Give a field (with a value, or empty to clear it) or a playthrough.";
        return new { ok = problem is null, problem, saved = progress.Saving, state = Read(dataDirectory) };
    }

    /// <summary>game_state_delete: deletes a game's state from a data folder, through the production keeper.</summary>
    internal static object Delete(string dataDirectory, string game)
    {
        using var progress = new GameProgress(new FixtureThinker(), new ContextBoard(), null, dataDirectory);
        var deleted = progress.Forget(game);
        return new { ok = true, deleted, state = Read(dataDirectory) };
    }

    private sealed class FixtureThinker(params string[] answers) : IGuideThinker
    {
        private readonly List<GuideThinkingAsk> asks = [];
        public bool CanThink => answers.Length > 0;
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
