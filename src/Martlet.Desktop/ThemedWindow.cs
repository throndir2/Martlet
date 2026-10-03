using System.ComponentModel;
using System.Windows;

namespace Martlet.Desktop;

public class ThemedWindow : Window
{
    private ResourceDictionary? fallbackPalette;
    private bool fallbackHighContrast;
    private bool closed;

    public ThemedWindow()
    {
        if (Application.Current is App) return;

        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/Martlet.Desktop;component/Themes/Controls.xaml")
        });
        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/Martlet.Desktop;component/Themes/Motion.xaml")
        });
        RefreshFallbackPalette();
        Loaded += (_, _) =>
        {
            if (fallbackHighContrast != SystemParameters.HighContrast) RefreshFallbackPalette();
            SystemParameters.StaticPropertyChanged += SystemAppearanceChanged;
        };
        Dispatcher.ShutdownStarted += (_, _) => SystemParameters.StaticPropertyChanged -= SystemAppearanceChanged;
        Closed += (_, _) =>
        {
            closed = true;
            SystemParameters.StaticPropertyChanged -= SystemAppearanceChanged;
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ScreenFit.Attach(this);
    }

    private void SystemAppearanceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.HighContrast) &&
            !closed && !Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
            Dispatcher.InvokeAsync(() => { if (!closed) RefreshFallbackPalette(); });
    }

    private void RefreshFallbackPalette()
    {
        if (fallbackPalette is not null) Resources.MergedDictionaries.Remove(fallbackPalette);
        fallbackHighContrast = SystemParameters.HighContrast;
        fallbackPalette = Appearance.Palette(PinkTheme.Light, fallbackHighContrast);
        Resources.MergedDictionaries.Add(fallbackPalette);
    }
}
