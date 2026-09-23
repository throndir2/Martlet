using System.IO;
using System.Windows;
using System.ComponentModel;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

public partial class App : Application
{
    private ResourceDictionary? palette;
    internal PinkTheme SelectedTheme { get; private set; }
    internal string? AppearanceNotice { get; private set; }

    internal void ApplyTheme(PinkTheme theme)
    {
        SelectedTheme = theme;
        var next = Appearance.Palette(theme, SystemParameters.HighContrast);
        if (palette is not null) Resources.MergedDictionaries.Remove(palette);
        Resources.MergedDictionaries.Add(next);
        palette = next;
    }

    private void SystemAppearanceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.HighContrast))
            Dispatcher.InvokeAsync(() => ApplyTheme(SelectedTheme));
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        SettingsStore? store = null;
        string? error = null;
        try
        {
            var directory = e.Args switch
            {
                [] => SettingsStore.DefaultDataDirectory(),
                ["--data-directory", var path] => path,
                _ => throw new ArgumentException("Invalid launch arguments.")
            };
            store = new SettingsStore(directory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or InvalidOperationException)
        {
            error = "Cannot open the data directory. Launch without arguments, or use --data-directory with an accessible absolute path. Do not run as administrator.";
        }
        if (store is not null)
            (SelectedTheme, AppearanceNotice) = Appearance.LoadForStartup(store.DataDirectory);
        ApplyTheme(SelectedTheme);
        SystemParameters.StaticPropertyChanged += SystemAppearanceChanged;
        MainWindow = new MainWindow(store, error);
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemParameters.StaticPropertyChanged -= SystemAppearanceChanged;
        base.OnExit(e);
    }
}
