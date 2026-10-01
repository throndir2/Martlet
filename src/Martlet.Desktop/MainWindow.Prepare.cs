using System.Windows;

namespace Martlet.Desktop;

public partial class MainWindow
{
    /// <summary>Opens Prepare this computer for a paired Linux host (or, with no host, any Linux computer over SSH) and
    /// refreshes the Devices map when it closes, so a Wake-on-LAN address it learned shows up.</summary>
    private void OpenPrepare(PairedHost? host, PrepareStart start)
    {
        if (store is null || setupService is null || closing) return;
        var window = new PrepareHostWindow(host, host is null ? null : Pairings(), start) { Owner = this };
        window.Closed += (_, _) =>
        {
            if (!closing) RefreshHomeAsync().Forget();
        };
        window.Show();
    }
}
