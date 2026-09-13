using System.IO;
using System.Windows;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

public partial class App : Application
{
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
        MainWindow = new MainWindow(store, error);
        MainWindow.Show();
    }
}
