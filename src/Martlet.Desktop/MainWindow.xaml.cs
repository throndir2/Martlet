using System.Windows;
using Martlet.Core.Settings;
using Martlet.Diagnostics;

namespace Martlet.Desktop;

public partial class MainWindow : Window
{
    private readonly SettingsStore? store;
    private readonly string? startupError;

    public MainWindow(SettingsStore? store, string? startupError)
    {
        InitializeComponent();
        this.store = store;
        this.startupError = startupError;
        DataPathText.Text = store is null ? "Settings location unavailable." : $"Settings location: {store.FilePath}";
        StatusText.Text = "Foundation: loading local status. No audio or network services are active.";
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await RefreshAsync();
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        CreateButton.IsEnabled = false;
        RefreshButton.IsEnabled = false;
        if (store is null)
        {
            StatusText.Text = startupError;
            return;
        }
        try
        {
            var report = await new FoundationStatusService(store).GetReportAsync();
            StatusText.Text = ReportFormatter.Human(report);
            CreateButton.IsEnabled = report.SettingsState == SettingsLoadState.FirstRun;
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        if (store is null)
        {
            ActionText.Text = startupError;
            return;
        }
        CreateButton.IsEnabled = false;
        var result = await store.SaveAsync(AppSettings.CreateUnconfigured(), expectedRevision: null);
        ActionText.Text = result.Saved
            ? "Unconfigured profile saved. Nothing was connected or enabled."
            : result.Error!.Summary;
        await RefreshAsync();
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();
}
