using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Martlet.Core.Settings;

namespace Martlet.Desktop.Tests;

/// <summary>A host PC's Home offers the way back to a companion PC first, above the host dashboard, not as a link at the end.</summary>
public sealed class HostHomeSwitchTests
{
    [Fact]
    public Task A_host_pc_shows_switch_to_companion_pc_at_the_top_of_home() => OnDispatcher(() =>
    {
        var root = Path.Combine(Path.GetTempPath(), "martlet-host-switch-" + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(data);
        DeviceRolePreference.Save(data, DeviceRole.Host);
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
        // The main window stays unshown, so nothing it starts once shown (the host's roles, sync) runs.
        var main = new MainWindow(new SettingsStore(data), null) { ShowActivated = false, ShowInTaskbar = false };
        try
        {
            var hostHome = Assert.IsType<StackPanel>(main.FindName("HostHome"));
            Assert.Equal(Visibility.Visible, hostHome.Visibility);
            Assert.Equal(Visibility.Collapsed, Assert.IsType<StackPanel>(main.FindName("CompanionHome")).Visibility);

            var card = Assert.IsType<Border>(hostHome.Children[0]);
            Assert.Same(main.FindName("SwitchToCompanionCard"), card);
            var button = Assert.Single(Descendants(main).OfType<Button>(), b => AutomationProperties.GetAutomationId(b) == "SwitchToCompanion");
            Assert.True(card.IsAncestorOf(button));
            Assert.Equal("Switch to companion PC", button.Content);
            Assert.Same(main.FindResource("PrimaryButton"), button.Style);
            Assert.True(button.IsEnabled);
            Assert.Equal("Host PC", Assert.IsType<TextBlock>(main.FindName("ModeText")).Text);
        }
        finally
        {
            main.Close();
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
        return Task.CompletedTask;
    });

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private static async Task OnDispatcher(Func<Task> action)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.UnhandledException += (_, args) =>
            {
                args.Handled = true;
                finished.TrySetException(args.Exception);
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            };
            _ = dispatcher.BeginInvoke(async () =>
            {
                try { await action(); finished.TrySetResult(); }
                catch (Exception error) { finished.TrySetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
    }
}
