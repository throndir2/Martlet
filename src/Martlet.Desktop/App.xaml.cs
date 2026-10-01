using System.IO;
using System.Windows;
using System.ComponentModel;
using System.Windows.Threading;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

public partial class App : Application
{
    private ResourceDictionary? palette;
    internal PinkTheme SelectedTheme { get; private set; }
    internal string? AppearanceNotice { get; private set; }
    /// <summary>The --data-directory Martlet was started with, so an update restarts it with the same one.</summary>
    internal string? DataDirectoryArgument { get; private set; }

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
        // An automatic update restarts Martlet minimized and without taking focus from whatever you are doing.
        var afterUpdate = e.Args is [.., "--after-update"];
        var args = afterUpdate ? e.Args[..^1] : e.Args;
        try
        {
            var directory = args switch
            {
                [] => SettingsStore.DefaultDataDirectory(),
                ["--data-directory", var path] => path,
                _ => throw new ArgumentException("Invalid launch arguments.")
            };
            store = new SettingsStore(directory);
            if (args.Length == 2) DataDirectoryArgument = directory;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or InvalidOperationException)
        {
            error = "Cannot open the data directory. Launch without arguments, or use --data-directory with an accessible absolute path. Do not run as administrator.";
        }
        var crashedLastTime = ErrorLog.Initialize(ErrorLog.DefaultDirectory(store?.DataDirectory), "desktop");
        ErrorLog.AttachDispatcher(this, "Martlet");
        if (error is not null) ErrorLog.Warn(error);
        try
        {
            if (store is not null)
            {
                (SelectedTheme, AppearanceNotice) = Appearance.LoadForStartup(store.DataDirectory);
                HostShells.Current = new SshHostShell(store.DataDirectory);
            }
            ApplyTheme(SelectedTheme);
            SystemParameters.StaticPropertyChanged += SystemAppearanceChanged;
            MainWindow = new MainWindow(store, error);
            if (afterUpdate)
            {
                MainWindow.ShowActivated = false;
                MainWindow.WindowState = WindowState.Minimized;
            }
            MainWindow.Show();
        }
        catch (Exception ex)
        {
            ErrorLog.Error("Martlet failed to start", ex);
            MessageBox.Show($"Martlet could not start.\n\n{ex.GetType().Name}: {ex.Message}\n\nDetails were saved to:\n" +
                $"{ErrorLog.CurrentFile ?? "(log unavailable)"}", "Martlet - startup failed", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
        if (crashedLastTime && !afterUpdate)
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
            {
                if (MessageBox.Show(MainWindow, "Martlet closed unexpectedly last time. Details, if any were captured, are in the local log:\n" +
                        $"{ErrorLog.CurrentFile}\n\nOpen the logs folder?", "Martlet - previous session ended unexpectedly",
                        MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                    ErrorLog.OpenFolder();
            });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemParameters.StaticPropertyChanged -= SystemAppearanceChanged;
        ErrorLog.MarkCleanExit();
        base.OnExit(e);
    }
}
