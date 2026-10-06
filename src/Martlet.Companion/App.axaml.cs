using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Martlet.Companion.Platform;

namespace Martlet.Companion;

public sealed class App : Application
{
    public CompanionPlatform Platform { get; } = PlatformSelector.Create();

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (CharacterCheck.Requested(desktop.Args ?? []))
            {
                CharacterCheck.Start(desktop, this, desktop.Args ?? []);
                base.OnFrameworkInitializationCompleted();
                return;
            }
            var window = new MainWindow(Platform, Platform.Probe.Probe(), new SoundFlowAudio(),
                new CompanionSettingsStore(CompanionSettingsStore.DefaultFolder()));
            desktop.MainWindow = window;
            AddTray(desktop, window);
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>The platform's tray (it appears on its first Show) or, when it has none, Avalonia's TrayIcon.</summary>
    private void AddTray(IClassicDesktopStyleApplicationLifetime desktop, MainWindow window)
    {
        void Run(TrayCommand command)
        {
            switch (command)
            {
                case TrayCommand.ShowWindow: window.Show(); window.Activate(); break;
                case TrayCommand.ToggleCharacter: window.ToggleCharacterFromTray(); break;
                case TrayCommand.Quit: desktop.Shutdown(); break;
            }
        }
        if (Platform.Tray is { } tray)
        {
            tray.Command += (_, command) => Avalonia.Threading.Dispatcher.UIThread.Post(() => Run(command));
            tray.Show(TrayState.Idle, "Martlet");
            window.StateChanged += (state, text) => tray.Show(state, text);
            return;
        }
        try
        {
            using var icon = AssetLoader.Open(new Uri("avares://Martlet.Companion/Assets/Martlet.png"));
            var menu = new NativeMenu();
            foreach (var (label, command) in new[] { ("Show Martlet", TrayCommand.ShowWindow), ("Show or hide the character", TrayCommand.ToggleCharacter), ("Quit", TrayCommand.Quit) })
            {
                var item = new NativeMenuItem(label);
                item.Click += (_, _) => Run(command);
                menu.Items.Add(item);
            }
            var trayIcon = new TrayIcon { Icon = new WindowIcon(icon), ToolTipText = "Martlet", Menu = menu, IsVisible = true };
            trayIcon.Clicked += (_, _) => Run(TrayCommand.ShowWindow);
            window.StateChanged += (_, text) => trayIcon.ToolTipText = "Martlet: " + text;
            TrayIcon.SetIcons(this, [trayIcon]);
        }
        catch (Exception error) when (error is InvalidOperationException or NotSupportedException or PlatformNotSupportedException) { }
    }
}
