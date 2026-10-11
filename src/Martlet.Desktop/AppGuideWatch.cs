using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Martlet.Audio;
using Martlet.Conversation.Guides;

namespace Martlet.Desktop;

/// <summary>Companion › App guides' look at the program in front (docs/APP_GUIDES.md): every <see cref="Every"/> while App guides
/// are on, on a thread-pool timer (never the UI thread, never a reply's path), it reads the program the window in front belongs
/// to (its name and file, <see cref="ActiveApp"/>), whether it fills its screen and whether Windows says a game runs in exclusive
/// full screen, tells a game with <see cref="PcActivity.Classify"/> (a game library folder or exclusive full screen) and gives it to
/// <see cref="AppGuideService.See"/>. Then it raises <see cref="OfferDue"/> when Martlet may offer to read up on it. It never reads
/// window titles or the screen; Martlet's own windows are passed over (what was in front before stays).</summary>
internal sealed class AppGuideWatch : IDisposable
{
    /// <summary>How often it looks.</summary>
    internal static TimeSpan Every { get; } = TimeSpan.FromSeconds(4);

    private readonly AppGuideService guides;
    private readonly object gate = new();
    private readonly object looking = new();
    private Timer? timer;
    private bool disposed;

    internal AppGuideWatch(AppGuideService guides) => this.guides = guides ?? throw new ArgumentNullException(nameof(guides));

    /// <summary>Raised on the timer's thread when Martlet may offer to read up on the app in front.</summary>
    internal event Action<AppGuideOfferCandidate>? OfferDue;

    /// <summary>Whether it looks now. Turning it off forgets the app in front.</summary>
    internal bool On
    {
        get { lock (gate) return timer is not null; }
        set
        {
            lock (gate)
            {
                if (disposed || value == (timer is not null)) return;
                if (value) timer = new Timer(_ => Look(), null, TimeSpan.FromSeconds(1), Every);
                else
                {
                    timer!.Dispose();
                    timer = null;
                }
            }
            if (!value) guides.Forget();
        }
    }

    /// <summary>One look (the timer calls it; a look still running is skipped).</summary>
    internal void Look()
    {
        if (!Monitor.TryEnter(looking)) return;
        try
        {
            lock (gate) if (disposed || timer is null) return;
            var window = GetForegroundWindow();
            var process = ActiveApp.ProcessOf(window);
            // Martlet's own window in front (the talk window, this page): what was in front before stays.
            if (window == 0 || process == 0 || process == (uint)Environment.ProcessId) return;
            var path = ActiveApp.ProgramPath(window);
            var name = path is null ? "" : ActiveApp.NameOf(path);
            if (name.StartsWith("Martlet", StringComparison.OrdinalIgnoreCase)) return;
            var full = ActiveApp.FullScreen(window);
            var exclusive = full && SHQueryUserNotificationState(out var state) == 0 && state == RunningD3dFullScreen;
            var seen = Describe(path, name, full, exclusive);
            guides.See(seen.Name, seen.File, seen.Game, full);
            if (guides.Offer() is { } offer) OfferDue?.Invoke(offer);
        }
        catch (Exception error) when (error is Win32Exception or ExternalException or InvalidOperationException or UnauthorizedAccessException)
        {
            // The next look tries again.
        }
        finally { Monitor.Exit(looking); }
    }

    /// <summary>What a look tells App guides about the program in <paramref name="path"/>: its name (Windows' name for it, else its
    /// file name), its file name without .exe and whether it is a game (in a game library folder, or in exclusive full screen).</summary>
    internal static (string Name, string File, bool Game) Describe(string? path, string name, bool fullScreen, bool exclusive)
    {
        var file = path is null ? "" : Path.GetFileNameWithoutExtension(path);
        var kind = PcActivity.Classify(new PcAppFacts(file, path, Foreground: true, FullScreen: fullScreen, ExclusiveFullScreen: exclusive));
        return (name.Length > 0 ? name : file, file, kind.Kind == PcActivityKind.Game);
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            timer?.Dispose();
            timer = null;
        }
    }

    private const int RunningD3dFullScreen = 3;

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);
}
