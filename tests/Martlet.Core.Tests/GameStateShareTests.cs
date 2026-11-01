using Martlet.Core.Settings;
using Martlet.Core.Sync;

namespace Martlet.Core.Tests;

/// <summary>The game state is one for all the owner's computers: game-state.json travels as the account setting
/// <c>game-state</c>, turning saving off on one computer turns it off (and removes the games) on the other, and the file keeps its
/// bounds.</summary>
public sealed class GameStateShareTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "martlet-game-state-share-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task Two_computers_keep_one_game_state()
    {
        Assert.True(SettingScopes.IsAccountKey(GameStateFile.SharedKey));
        Assert.Equal(SettingsFileScope.Account, SettingsFiles.Find(GameStateFile.FileName)?.Scope);
        var a = Path.Combine(root, "a");
        var b = Path.Combine(root, "b");
        var followedOnB = 0;
        var nodeA = new SharedSettingsNode(a, "desktop-a", [GameStateFile.Section(a)]);
        var nodeB = new SharedSettingsNode(b, "desktop-b", [GameStateFile.Section(b, () => followedOnB++)]);
        var start = DateTimeOffset.UtcNow;

        await nodeB.SyncAsync([], true, start, CancellationToken.None);
        GameStateFile.Write(a, new GameStateFile
        {
            Playthroughs =
            [
                new("Skyrim", GameStateFile.MainPlaythrough, true, start, new Dictionary<string, GameFact>
                {
                    [GameFields.Place] = new("Whiterun", start), [GameFields.CharacterLevel] = new("12", start, ByUser: true)
                })
            ]
        });
        await nodeA.SyncAsync([nodeB.Document], true, start.AddSeconds(15), CancellationToken.None);
        var followed = await nodeB.SyncAsync([nodeA.Document], true, start.AddSeconds(30), CancellationToken.None);
        Assert.Contains(followed.Applied, change => change.Key == GameStateFile.SharedKey && change.By == "desktop-a");
        Assert.Equal(1, followedOnB);
        var onB = GameStateFile.Load(b);
        Assert.Equal(GameStateFile.Load(a).ToJson(), onB.ToJson());
        Assert.True(onB.Playthroughs[0].Facts[GameFields.CharacterLevel].ByUser);

        // Saving turned off on B: A follows and keeps no games on disk.
        GameStateFile.Write(b, onB with { Save = false });
        await nodeB.SyncAsync([nodeA.Document], true, start.AddSeconds(45), CancellationToken.None);
        await nodeA.SyncAsync([nodeB.Document], true, start.AddSeconds(60), CancellationToken.None);
        var onA = GameStateFile.Load(a);
        Assert.False(onA.Save);
        Assert.Empty(onA.Playthroughs);

        var newer = nodeA.Document.Find(GameStateFile.SharedKey)!;
        Assert.Null(GameStateFile.Parse(newer.Value.Replace("\"schema_version\":1", "\"schema_version\":2", StringComparison.Ordinal)));
    }

    [Fact]
    public void The_file_keeps_its_bounds()
    {
        var directory = Path.Combine(root, "bounds");
        Assert.Null(GameStateFile.TryLoad(directory));
        Assert.True(GameStateFile.Load(directory).Save);
        var now = DateTimeOffset.UtcNow;
        GamePlaythrough Play(string game, string playthrough, string field, string value, bool current = true) =>
            new(game, playthrough, current, now, new Dictionary<string, GameFact> { [field] = new(value, now) });
        Assert.ThrowsAny<Exception>(() => GameStateFile.Write(directory, new() { Playthroughs = [Play("Skyrim", "main", "mood", "fine")] }));
        Assert.ThrowsAny<Exception>(() => GameStateFile.Write(directory, new() { Playthroughs = [Play("Skyrim", "main", GameFields.Place, new string('a', 161))] }));
        Assert.ThrowsAny<Exception>(() => GameStateFile.Write(directory,
            new() { Playthroughs = [Play("Skyrim", "main", GameFields.Place, "a"), Play("skyrim", "Main", GameFields.Place, "b", false)] }));
        Assert.ThrowsAny<Exception>(() => GameStateFile.Write(directory,
            new() { Playthroughs = [.. Enumerable.Range(0, GameStateFile.MaximumGames + 1).Select(i => Play("Game " + i, "main", GameFields.Place, "a"))] }));
        Directory.CreateDirectory(directory);
        File.WriteAllText(GameStateFile.PathOf(directory), "not json");
        Assert.Null(GameStateFile.TryLoad(directory));
        Assert.Equal("elden-ring-new-game", GameStateFile.KeyOf("Elden Ring", "New Game+"));
    }
}
