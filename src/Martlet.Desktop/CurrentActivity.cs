using Martlet.Memory;

namespace Martlet.Desktop;

/// <summary>What the user does on this PC now, as memory scopes it (<see cref="MemoryActivity"/>): the game the game watch sees
/// (MainWindow.Situations, while memory is on), or the FIXTURE game of MARTLET_SIMULATE_SITUATION. Recall leaves out the facts kept
/// for another activity, and remembering keeps a new fact for this one when it matters only here. Read on the reply's path, so it is
/// a field read: the watch looks on its own thread every few seconds.</summary>
internal static class CurrentActivity
{
    private static string? game;

    /// <summary>The game being played now (its name as the game watch says it), or null.</summary>
    internal static string? Game
    {
        get => Volatile.Read(ref game);
        set
        {
            var before = Interlocked.Exchange(ref game, string.IsNullOrWhiteSpace(value) ? null : value.Trim());
            if (!string.Equals(before, Game, StringComparison.Ordinal))
                ErrorLog.Info(Game is null ? "Memory: no game is played now; recall uses memories for everywhere."
                    : "Memory: a game is played now; recall adds its memories and leaves out other games'.");
        }
    }

    /// <summary>The activity now ("gaming/Palworld"), or null when nothing is known.</summary>
    internal static string? Now => MemoryActivity.Game(Game);
}
