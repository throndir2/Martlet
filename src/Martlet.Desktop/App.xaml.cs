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
    /// <summary>The previous run did not exit cleanly (crash, kill or power loss); Home says so with the logs folder.</summary>
    internal bool CrashedLastTime { get; private set; }

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
            error = "Martlet can't open its data folder. Launch normally, or choose an accessible folder.";
        }
        var crashedLastTime = ErrorLog.Initialize(ErrorLog.DefaultDirectory(store?.DataDirectory), "desktop");
        CrashedLastTime = crashedLastTime && !afterUpdate;
        ErrorLog.AttachDispatcher(this, "Martlet");
        // Failed provider requests record their HTTP status and the provider's own short explanation locally.
        Martlet.Providers.ProviderDiagnostics.SetSink(line => ErrorLog.Warn(line));
        if (error is not null) ErrorLog.Warn(error);
        try
        {
            if (store is not null)
            {
                (SelectedTheme, AppearanceNotice) = Appearance.LoadForStartup(store.DataDirectory);
                HostShells.Current = new SshHostShell(store.DataDirectory);
                HostSetupResume.Initialize(store.DataDirectory, DataDirectoryArgument);
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
            MessageBox.Show($"Martlet couldn't start.\n\n{ex.Message}\n\nDetails were saved to:\n" +
                $"{ErrorLog.CurrentFile ?? "(log unavailable)"}", "Martlet couldn't start", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemParameters.StaticPropertyChanged -= SystemAppearanceChanged;
        ErrorLog.MarkCleanExit();
        base.OnExit(e);
    }
}
