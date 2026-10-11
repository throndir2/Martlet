using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Martlet.Conversation.Guides;
using Martlet.Core.Settings;
using Martlet.Desktop;

namespace Martlet.Desktop.Tests;

/// <summary>Companion › App guides in Martlet's main window: the Add an app boxes stay the same boxes, with what you typed and the
/// caret, when the page draws again under your typing (the guide library changes as reading up starts and ends, or Home refreshes),
/// and Add empties them.</summary>
public sealed class AppGuidesPageTests
{
    [Fact]
    public Task TheAddAnAppBoxesKeepWhatYouTypeWhenThePageDrawsAgain() => OnDispatcherAsync(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "martlet-app-guides-page-" + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(data);
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
        // The main window stays unshown, so nothing it starts once shown runs.
        var main = new MainWindow(new SettingsStore(data), null) { ShowActivated = false, ShowInTaskbar = false };
        try
        {
            typeof(MainWindow).GetField("companionTab", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(main, CompanionTab.AppGuides);
            Assert.IsType<RadioButton>(main.FindName("NavCompanion")).IsChecked = true;
            var name = await Eventually(() => Find<TextBox>(main, "AppGuideAddName"));
            var programs = Find<TextBox>(main, "AppGuideAddPrograms")!;
            var sites = Find<TextBox>(main, "AppGuideAddSites")!;
            name.Text = "Stardew Valley";
            name.Select(1, 0);
            programs.Text = "StardewValley";
            sites.Text = "stardewvalleywiki.com";

            // The guide library changes (as when reading up starts, adds the app and ends): the page draws again, with the same boxes.
            var ask = Find<CheckBox>(main, "AppGuidesAsk")!;
            var guides = (AppGuideService)typeof(MainWindow).GetField("appGuides", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
            await guides.SetAskAsync(false);
            await Eventually(() => Find<CheckBox>(main, "AppGuidesAsk") is { } drawn && !ReferenceEquals(drawn, ask) ? drawn : null);
            Kept();

            // Home refreshes draw the open page again too.
            typeof(MainWindow).GetMethod("RenderTab", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, null);
            Kept();

            // Add puts the app on the list and empties the boxes, which stay on the page.
            Click(Find<Button>(main, "AppGuideAdd")!);
            await Eventually(() => Find<TextBlock>(main, "AppGuideName-" + AppGuideService.KeyOf("Stardew Valley")));
            Assert.Equal(("", "", ""), (name.Text, programs.Text, sites.Text));
            Assert.Same(name, Find<TextBox>(main, "AppGuideAddName"));
            var entry = Assert.Single(guides.Library.Apps);
            Assert.Equal(("Stardew Valley", "StardewValley"), (entry.Name, Assert.Single(entry.Programs)));

            void Kept()
            {
                Assert.Same(name, Find<TextBox>(main, "AppGuideAddName"));
                Assert.Same(programs, Find<TextBox>(main, "AppGuideAddPrograms"));
                Assert.Same(sites, Find<TextBox>(main, "AppGuideAddSites"));
                Assert.Equal(("Stardew Valley", "StardewValley", "stardewvalleywiki.com"), (name.Text, programs.Text, sites.Text));
                Assert.Equal((1, 0), (name.CaretIndex, name.SelectionLength));
                Assert.Equal(1, Descendants(main).OfType<TextBox>().Count(box => AutomationProperties.GetAutomationId(box) == "AppGuideAddName"));
            }
        }
        finally
        {
            main.Close();
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    });

    private static async Task<T> Eventually<T>(Func<T?> find) where T : class
    {
        for (var i = 0; i < 600; i++)
        {
            if (find() is { } found) return found;
            await Task.Delay(50);
        }
        throw new TimeoutException($"Nothing of type {typeof(T).Name} showed up in 30 seconds.");
    }

    private static T? Find<T>(DependencyObject root, string id) where T : FrameworkElement =>
        Descendants(root).OfType<T>().FirstOrDefault(element => AutomationProperties.GetAutomationId(element) == id);

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void Click(Button button)
    {
        Assert.True(button.IsEnabled, AutomationProperties.GetAutomationId(button) + " is off.");
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    }

    private static async Task OnDispatcherAsync(Func<Task> action)
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
        await finished.Task.WaitAsync(TimeSpan.FromMinutes(3));
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
    }
}
