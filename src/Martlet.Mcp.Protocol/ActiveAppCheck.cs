using System.IO;
using System.Runtime.InteropServices;
using Martlet.Conversation;
using Martlet.Core.Settings;
using Martlet.Mcp.Shared;

namespace Martlet.Mcp;

/// <summary>active_app_check: the program in front and whether it fills its monitor, as Martlet's screen glances tell the
/// Thinking model (docs/SCREEN_COMMENTARY.md), read with the desktop's production <see cref="ActiveApp"/>: the window in front
/// now (never its title), a program every Windows PC has, the full-screen rule on FIXTURE windows, and the words a look, a
/// message with a picture and the conversation get for a FIXTURE app with the data directory's prompt edits. Reads no
/// credentials and contacts nothing.</summary>
internal static class ActiveAppCheck
{
    private const string FixtureApp = "ELDEN RING", FixtureTitle = "ELDEN RING";
    private static readonly (int, int, int, int) Monitor = (0, 0, 1920, 1080);

    internal static async Task<object> RunAsync(string directory, CancellationToken cancellation)
    {
        var loaded = await new SettingsStore(directory).LoadAsync(cancellation);
        var prompts = loaded.Settings?.Prompts;
        var window = GetForegroundWindow();
        // How long a capture spends on it: the first read of a program's file, then the name already known.
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var (name, fullScreen) = ActiveApp.Of(window);
        var first = watch.Elapsed.TotalMilliseconds;
        watch.Restart();
        ActiveApp.Of(window);
        var again = watch.Elapsed.TotalMilliseconds;
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        var told = ActiveApp.Describe(FixtureApp, true);
        return new
        {
            inFront = new
            {
                found = window != 0, app = name, fullScreen, told = ActiveApp.Describe(name, fullScreen),
                readMs = new { first = Math.Round(first, 3), again = Math.Round(again, 3) }
            },
            sample = new { file = "explorer.exe", app = File.Exists(explorer) ? ActiveApp.NameOf(explorer) : null },
            rules = new[]
            {
                Rule("borderless full-screen game or video", (0, 0, 1920, 1080), Monitor, false, false),
                Rule("maximized borderless window", (0, 0, 1920, 1080), Monitor, true, false),
                Rule("maximized window over a taskbar that hides itself", (-8, -8, 1928, 1088), Monitor, true, true),
                Rule("maximized window above the taskbar", (-8, -8, 1928, 1048), Monitor, true, true),
                Rule("window smaller than its monitor", (100, 100, 1500, 900), Monitor, false, true),
                Rule("borderless window filling the second monitor", (1920, 0, 4480, 1440), (1920, 0, 4480, 1440), false, false)
            },
            prompts = new
            {
                state = loaded.State switch
                {
                    SettingsLoadState.Loaded => "loaded",
                    SettingsLoadState.FirstRun => "none",
                    _ => "unreadable"
                },
                fixture = new { app = FixtureApp, fullScreen = true, title = FixtureTitle },
                glance = PromptSettings.Fill(prompts, PromptCatalog.GlanceScreen, ("app", told), ("title", FixtureTitle),
                    ("remarks", ""), ("silent", StayQuiet.Marker)),
                withMessage = PromptSettings.Fill(prompts, PromptCatalog.SeenApp, ("app", told), ("title", FixtureTitle)),
                kept = VisionHistory.Look(false, VisionHistory.Screen(true, FixtureTitle, ActiveApp.Label(FixtureApp, true)), null,
                    "a boss fight, low health")
            }
        };
    }

    private static object Rule(string window, (int, int, int, int) bounds, (int, int, int, int) monitor, bool maximized, bool titleBar) =>
        new { window, maximized, titleBar, fullScreen = ActiveApp.Fills(bounds, monitor, maximized, titleBar) };

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
}
