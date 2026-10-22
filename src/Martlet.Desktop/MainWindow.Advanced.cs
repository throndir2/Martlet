using System.IO;
using System.Windows;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>Show advanced settings (<see cref="AdvancedSettingsFile"/>, each person's, shared with their other computers): with it
/// off, the navigation rail hides Thinking requests and Diagnostics and Companion's side list shows only the essentials
/// (<see cref="CompanionPages.Essentials"/>). A shortcut still opens a hidden page, and that page shows in the rail or the side
/// list while it is open. Settings › What Martlet shows and the side list's last link change it.</summary>
public partial class MainWindow
{
    private bool showAdvanced;
    private bool loadingAdvanced;

    /// <summary>Reads the saved choice and shows the pages it allows.</summary>
    private void LoadAdvanced()
    {
        showAdvanced = AdvancedSettingsFile.Load(store?.DataDirectory).Show;
        ApplyAdvanced();
    }

    /// <summary>Saves the choice here and shares it with the person's other computers, then shows the pages it allows.</summary>
    private void SetShowAdvanced(bool show)
    {
        if (show == showAdvanced) return;
        showAdvanced = show;
        if (store is not null)
        {
            try { AdvancedSettingsFile.Save(store.DataDirectory, new AdvancedSettingsFile { Show = show }); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                ActionText.Text = "Couldn't save Show advanced settings: " + error.Message;
            }
        }
        ErrorLog.Info(show ? "Show advanced settings: every page shows." : "Show advanced settings: only the essential pages show.");
        ApplyAdvanced();
        QueueSettingsSync();
    }

    /// <summary>Shows or hides the advanced rail items and side list pages for <see cref="showAdvanced"/>.</summary>
    private void ApplyAdvanced()
    {
        if (ShowAdvancedChoice is not null && ShowAdvancedChoice.IsChecked != showAdvanced)
        {
            loadingAdvanced = true;
            try { ShowAdvancedChoice.IsChecked = showAdvanced; }
            finally { loadingAdvanced = false; }
        }
        RailAdvanced();
        if (openTab is { } open) BuildCompanionNav(open);
        else companionNavShape = null;
    }

    /// <summary>The rail's advanced items (Thinking requests and Diagnostics) show with Show advanced settings on, or while open.
    /// A host PC never shows Thinking requests.</summary>
    private void RailAdvanced()
    {
        if (NavDiagnostics is null || NavThinkingRequests is null) return;
        NavDiagnostics.Visibility = showAdvanced || NavDiagnostics.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        NavThinkingRequests.Visibility = Role != DeviceRole.Host && (showAdvanced || NavThinkingRequests.IsChecked == true)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowAdvancedChoice_Changed(object sender, RoutedEventArgs e)
    {
        if (loadingAdvanced || !IsLoaded) return;
        SetShowAdvanced(ShowAdvancedChoice.IsChecked == true);
    }
}
