using Avalonia;

namespace Martlet.Companion;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--status"))
        {
            var platform = PlatformSelector.Create();
            Console.WriteLine(CompanionStatus.Json(platform, platform.Probe.Probe()));
            return 0;
        }
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
