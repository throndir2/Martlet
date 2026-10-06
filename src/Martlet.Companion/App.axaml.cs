using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Martlet.Companion.Platform;

namespace Martlet.Companion;

public sealed class App : Application
{
    public CompanionPlatform Platform { get; } = PlatformSelector.Create();

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow(Platform, Platform.Probe.Probe());
        base.OnFrameworkInitializationCompleted();
    }
}
