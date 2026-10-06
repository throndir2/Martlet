using System.Text.Json;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace Martlet.Companion;

/// <summary><c>Martlet.Companion --character-check [model] [--seconds N]</c>: opens only the character window with the
/// bundled (or given) character, prints each renderer state as a JSON line, and exits 0 once the character shows, 1 when
/// it fails or times out. Validation for the web view path (WebKitGTK, WKWebView) without audio, network or settings.</summary>
internal static class CharacterCheck
{
    public static bool Requested(string[] args) => args.Contains("--character-check");

    public static void Start(IClassicDesktopStyleApplicationLifetime desktop, App app, string[] args)
    {
        var at = Array.IndexOf(args, "--character-check");
        var path = at + 1 < args.Length && !args[at + 1].StartsWith("--", StringComparison.Ordinal) ? args[at + 1] : null;
        var secondsAt = Array.IndexOf(args, "--seconds");
        var seconds = secondsAt >= 0 && secondsAt + 1 < args.Length && int.TryParse(args[secondsAt + 1], out var s) ? s : 60;
        void Report(object value) => Console.WriteLine(JsonSerializer.Serialize(value));
        void Finish(int code) { desktop.Shutdown(code); }
        if (!CharacterModel.BundleBuilt) { Report(new { state = "failed: the character renderer isn't in this build" }); Finish(1); return; }
        CharacterModel? model;
        try { model = CharacterModel.Resolve(path); }
        catch (CompanionException error) { Report(new { state = "failed: " + error.Message }); Finish(1); return; }
        var server = new CharacterServer(CharacterModel.BundleFolder);
        var phase = 0f;
        var window = new CharacterWindow(server, app.Platform.Overlay, () => phase);
        window.StateChanged += (_, state) =>
        {
            Report(new { state, served = server.Served });
            if (state.StartsWith("showing", StringComparison.Ordinal))
            {
                // Open and close the mouth a few times through the loudness lip-sync path before passing.
                var steps = 0;
                DispatcherTimer.Run(() =>
                {
                    phase = steps % 2 == 0 ? 0.8f : 0f;
                    if (++steps < 8) return true;
                    Report(new { state = "lip-sync sent", mouthUpdates = steps });
                    Finish(0);
                    return false;
                }, TimeSpan.FromMilliseconds(150));
            }
            else if (state.StartsWith("failed", StringComparison.Ordinal)) Finish(1);
        };
        desktop.MainWindow = window;
        window.Show();
        window.ShowModel(model);
        DispatcherTimer.RunOnce(() => { Report(new { state = "timed out: " + window.State, served = server.Served }); Finish(1); }, TimeSpan.FromSeconds(seconds));
        desktop.Exit += (_, _) => server.Dispose();
    }
}
