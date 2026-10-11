using Martlet.Core.Planning;

namespace Martlet.Audio;

/// <summary>Watches for a game on this PC for the While gaming plan (<see cref="LiveSituations"/>): every few seconds it looks
/// at the window in front and the apps that play sound, and tells what each is with <see cref="PcActivity.Classify"/> (a game:
/// in a game library, in exclusive full screen, full screen with the graphics card busy, or the graphics card very busy). A game
/// counts as running until Martlet hasn't seen it for <see cref="LiveSituations.GameHold"/>, so a short switch to another window
/// doesn't end it. It runs only while <see cref="On"/> (the desktop turns it on when the owner said this PC is used for games
/// and live Thinking runs on its graphics card). It reads the volume mixer's meters and window facts only: nothing is recorded,
/// kept or sent, and it never runs on a reply's path.</summary>
public sealed class GameWatch : IDisposable
{
    /// <summary>How often it looks.</summary>
    public static TimeSpan Every { get; } = TimeSpan.FromSeconds(3);

    private readonly Func<IPcActivitySource> sources;
    private readonly TimeProvider clock;
    private readonly bool manual;
    private readonly object gate = new();
    private readonly object looking = new();
    private IPcActivitySource? source;
    private ITimer? timer;
    private string? game;
    private long seenAt;
    private bool on, disposed;
    private string? problem;

    /// <param name="sources">Opens the source each time the watch turns on (it is disposed when it turns off).</param>
    /// <param name="manual">Never starts a timer: the owner calls <see cref="Look"/> (tests and MCP's check).</param>
    public GameWatch(Func<IPcActivitySource> sources, TimeProvider? clock = null, bool manual = false)
    {
        this.sources = sources ?? throw new ArgumentNullException(nameof(sources));
        this.clock = clock ?? TimeProvider.System;
        this.manual = manual;
    }

    /// <summary>Raised when a game starts, changes or ends (null), on the watch's thread.</summary>
    public event Action<string?>? Changed;

    /// <summary>The game running on this PC now (its name, as <see cref="PcSource.App"/> says it), or null.</summary>
    public string? Game { get { lock (gate) return game; } }

    /// <summary>Why the last look failed (the next look tries again), or null.</summary>
    public string? Problem { get { lock (gate) return problem; } }

    /// <summary>Whether it watches now. Turning it off forgets the game.</summary>
    public bool On
    {
        get { lock (gate) return on; }
        set
        {
            string? ended = null;
            lock (gate)
            {
                if (disposed || on == value) return;
                on = value;
                if (value)
                {
                    if (!manual) timer = clock.CreateTimer(_ => Look(), null, TimeSpan.Zero, Every);
                }
                else
                {
                    timer?.Dispose();
                    timer = null;
                    ended = game;
                    game = null;
                }
            }
            if (!value)
            {
                Release();
                if (ended is not null) Changed?.Invoke(null);
            }
        }
    }

    /// <summary>One look: the apps playing sound and the window in front. The timer calls it while <see cref="On"/>; tests call
    /// it themselves. Does nothing while off.</summary>
    public void Look()
    {
        string? found = null;
        lock (looking)
        {
            IPcActivitySource current;
            lock (gate)
            {
                if (!on || disposed) return;
                current = source ??= sources();
            }
            try
            {
                var playing = current.Levels().Where(level => level.Peak >= PcActivityMonitor.Floor && !string.IsNullOrWhiteSpace(level.App))
                    .Select(level => level.App).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                foreach (var app in current.Facts(playing))
                {
                    if (!app.Foreground && !playing.Contains(app.Process, StringComparer.OrdinalIgnoreCase)) continue;
                    var kind = PcActivity.Classify(app);
                    if (kind.Kind != PcActivityKind.Game) continue;
                    found = kind.App;
                    if (app.Foreground) break;
                }
                lock (gate) problem = null;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                // A source that fails (an audio device removed, Windows refusing a call) is opened again at the next look; the game
                // Martlet saw last still counts until its hold ends.
                lock (gate) problem = error.GetType().Name + ": " + error.Message;
                Release();
            }
        }
        bool raise;
        string? now;
        lock (gate)
        {
            if (!on) return;
            var before = game;
            if (found is not null)
            {
                seenAt = clock.GetTimestamp();
                game = found;
            }
            else if (game is not null && clock.GetElapsedTime(seenAt) >= LiveSituations.GameHold) game = null;
            raise = !string.Equals(before, game, StringComparison.Ordinal);
            now = game;
        }
        if (raise) Changed?.Invoke(now);
    }

    private void Release()
    {
        IPcActivitySource? old;
        lock (gate)
        {
            old = source;
            source = null;
        }
        if (old is null) return;
        lock (looking) old.Dispose();
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            on = false;
            timer?.Dispose();
            timer = null;
        }
        Release();
    }
}
