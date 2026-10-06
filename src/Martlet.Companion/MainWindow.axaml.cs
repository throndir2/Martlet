using Avalonia.Controls;
using Martlet.Companion.Platform;

namespace Martlet.Companion;

public sealed partial class MainWindow : Window
{
    public MainWindow() : this(CompanionPlatform.Defaults(), CompanionPlatform.Defaults().Probe.Probe()) { }

    public MainWindow(CompanionPlatform platform, PlatformInfo info)
    {
        InitializeComponent();
        StatusText.Text = CompanionStatus.Json(platform, info);
    }
}
