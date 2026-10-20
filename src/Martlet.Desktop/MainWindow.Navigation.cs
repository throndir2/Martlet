using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Martlet.HostClient;
using Martlet.Avatar.Hosting;
using Martlet.Core.Cluster;
using Martlet.Core.Contracts;
using Martlet.Core.Installation;
using Martlet.Core.Platforms;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

// The app shell's navigation (see MainWindow.Shell.cs for the shell's shared state).
public partial class MainWindow
{
    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (HomePage is null || DevicesPage is null || SharePage is null || CompanionPage is null || ConversationsPage is null || CreationsPage is null ||
            TasksPage is null || ThinkingRequestsPage is null || DiagnosticsPage is null || SettingsPage is null) return;
        if (sender is RadioButton { IsChecked: false }) return;
        FrameworkElement page = ReferenceEquals(sender, NavDevices) ? DevicesPage
            : ReferenceEquals(sender, NavShare) ? SharePage
            : ReferenceEquals(sender, NavCompanion) ? CompanionPage
            : ReferenceEquals(sender, NavConversations) ? ConversationsPage
            : ReferenceEquals(sender, NavCreations) ? CreationsPage
            : ReferenceEquals(sender, NavTasks) ? TasksPage
            : ReferenceEquals(sender, NavThinkingRequests) ? ThinkingRequestsPage
            : ReferenceEquals(sender, NavDiagnostics) ? DiagnosticsPage
            : ReferenceEquals(sender, NavSettings) ? SettingsPage
            : HomePage;
        openTab = null;
        foreach (var candidate in new FrameworkElement[] { HomePage, DevicesPage, SharePage, CompanionPage, ConversationsPage, CreationsPage, TasksPage, ThinkingRequestsPage, DiagnosticsPage, SettingsPage })
            candidate.Visibility = ReferenceEquals(candidate, page) ? Visibility.Visible : Visibility.Collapsed;
        if (ReferenceEquals(page, CompanionPage)) ShowCompanionTab(entering: true);
        else Motion.Enter(page);
        if (ReferenceEquals(page, DevicesPage)) RenderMap();
        if (ReferenceEquals(page, SharePage)) RenderShare();
        if (ReferenceEquals(page, ConversationsPage)) ConversationsPage.EnterAsync().Forget();
        if (ReferenceEquals(page, CreationsPage)) RenderCreations();
        if (ReferenceEquals(page, TasksPage)) EnterTasks();
        else LeaveTasks();
        if (ReferenceEquals(page, ThinkingRequestsPage)) EnterThinkingRequests();
        else LeaveThinkingRequests();
        // Windows' own Startup apps switch can change while Martlet runs.
        if (ReferenceEquals(page, SettingsPage))
        {
            RenderBackground();
            RenderAppearance();
            RenderOtherRoles();
            RenderRecommendationPreferences();
        }
        if (ReferenceEquals(page, DiagnosticsPage)) EnterDiagnostics();
        else LeaveDiagnostics();
    }

    private void Navigate(RadioButton item)
    {
        if (item.IsChecked == true) Nav_Checked(item, new RoutedEventArgs());
        else item.IsChecked = true;
    }
}
