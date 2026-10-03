using System.Windows.Threading;

namespace Martlet.Desktop;

/// <summary>Saves an editor's changes on its own, so settings never wait for a Save button: a short pause after the last
/// change (typing isn't saved on every keystroke), at once on <see cref="SaveNowAsync"/> (a switch, an added item, closing),
/// one save at a time, and once more when changes arrive while a save runs. A save that can't start yet (its delegate returns
/// false, for example while another Martlet action holds the settings) is tried again after the same pause. Each save
/// writes into the newest saved document, so changes synced in from your other computers in the meantime are kept.</summary>
internal sealed class AutoSave
{
    internal static readonly TimeSpan TypingPause = TimeSpan.FromMilliseconds(700);

    private readonly Func<Task<bool>> save;
    private readonly DispatcherTimer timer;
    private Task? running;
    private bool again;

    /// <param name="save">Saves the current changes. Returns false to be tried again after the pause; it reports its own
    /// outcome (saved, or why not) and must not throw.</param>
    internal AutoSave(Func<Task<bool>> save, TimeSpan? pause = null)
    {
        this.save = save;
        timer = new DispatcherTimer { Interval = pause ?? TypingPause };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _ = RunAsync();
        };
    }

    /// <summary>A change waits for its pause, or a save is running.</summary>
    internal bool Pending => timer.IsEnabled || running is not null;

    /// <summary>Raised when the saves have finished (or the waiting change was dropped), so a status line can say so.</summary>
    internal event Action? Settled;

    /// <summary>Something changed: save after the pause (restarted by each further change).</summary>
    internal void Changed()
    {
        timer.Stop();
        timer.Start();
    }

    /// <summary>Saves now (or right after the save that is running) and completes when nothing is left to save or the save
    /// has to wait; check <see cref="Pending"/> afterwards.</summary>
    internal Task SaveNowAsync()
    {
        timer.Stop();
        return RunAsync();
    }

    /// <summary>Drops a waiting change (the editor was reloaded or closed without it).</summary>
    internal void Cancel()
    {
        timer.Stop();
        if (running is null) Settled?.Invoke();
    }

    private async Task RunAsync()
    {
        if (running is { } current)
        {
            again = true;
            await current;
            return;
        }
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        running = done.Task;
        try
        {
            do
            {
                again = false;
                if (!await save())
                {
                    timer.Start();
                    break;
                }
            }
            while (again);
        }
        finally
        {
            running = null;
            done.SetResult();
            if (!timer.IsEnabled) Settled?.Invoke();
        }
    }
}
