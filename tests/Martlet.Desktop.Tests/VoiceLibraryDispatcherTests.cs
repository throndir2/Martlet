using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Martlet.Core.Settings;
using Martlet.Core.Voices;
using Martlet.Desktop;

namespace Martlet.Desktop.Tests;

public sealed class VoiceLibraryDispatcherTests
{
    [Fact]
    public Task OpeningAndSwitchingAllEnginesIsPassive() => OnDispatcher(async () =>
    {
        var path = Path.Combine(Path.GetTempPath(), "Martlet.VoiceUI." + Guid.NewGuid().ToString("N"));
        var runner = new SetupOperationRunner();
        var window = new VoiceLibraryWindow(new VoiceLibrary(path), runner);
        try
        {
            window.Show();
            var engines = Control<ComboBox>(window, "EngineChoice");
            Assert.Equal(5, engines.Items.Count);
            for (var index = 0; index < engines.Items.Count; index++)
            {
                engines.SelectedIndex = index;
                Assert.Contains("Not integrated", Control<TextBox>(window, "EngineDetails").Text);
            }
            await Dispatcher.Yield();
            Assert.False(Directory.Exists(path));
            Assert.False(runner.IsRunning);
            Assert.False(Control<CheckBox>(window, "RightsConfirmed").IsChecked);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task ImportAndReopenUseRealLocalStoreWithoutChangingSettings() => OnDispatcher(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "Martlet.VoiceUI." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "sample.wav");
        File.WriteAllBytes(source, Martlet.Providers.Tests.ProviderFixtures.Wave(160_000));
        var library = new VoiceLibrary(Path.Combine(root, "library"));
        var runner = new SetupOperationRunner();
        var window = new VoiceLibraryWindow(library, runner, () => source);
        try
        {
            window.Show();
            Click(window, "VoiceBrowse");
            Control<TextBox>(window, "NameInput").Text = "Synthetic UI sample";
            Control<TextBox>(window, "TranscriptInput").Text = "Synthetic fixture, not a human voice";
            Control<ComboBox>(window, "RightsChoice").SelectedIndex = 0;
            Click(window, "VoiceImport");
            Assert.Contains("confirm local storage", Control<TextBox>(window, "ResultText").Text);
            Assert.False(Directory.Exists(Path.Combine(root, "library")));
            Control<CheckBox>(window, "RightsConfirmed").IsChecked = true;
            Click(window, "VoiceImport");
            await Wait(() => Control<TextBox>(window, "ResultText").Text.StartsWith("Local copy saved.", StringComparison.Ordinal));
            Assert.False(Control<CheckBox>(window, "RightsConfirmed").IsChecked);
            Assert.Single(await library.ListAsync());
            Assert.False(File.Exists(Path.Combine(root, "settings.json")));
            window.Close();
            window = new VoiceLibraryWindow(library, runner);
            window.Show();
            Assert.Empty(Control<ComboBox>(window, "AssetChoice").Items);
            Click(window, "VoiceReload");
            await Wait(() => Control<TextBox>(window, "ResultText").Text.StartsWith("1 saved", StringComparison.Ordinal));
            Assert.Single(Control<ComboBox>(window, "AssetChoice").Items.Cast<object>());
            Control<ComboBox>(window, "EngineChoice").SelectedIndex = 2;
            Assert.Contains("Turbo", Control<TextBox>(window, "AssetDetails").Text);
        }
        finally
        {
            window.Close();
            await Wait(() => !runner.IsRunning);
            Directory.Delete(root, recursive: true);
        }
    });

    [Fact]
    public Task ExistingAppOperationCannotBeBypassedByReloadOrClose() => OnDispatcher(async () =>
    {
        var path = Path.Combine(Path.GetTempPath(), "Martlet.VoiceUI." + Guid.NewGuid().ToString("N"));
        var runner = new SetupOperationRunner();
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var existing = runner.TryStart(async _ => { await released.Task; return new(SetupWorkOutcome.Completed); })!;
        var window = new VoiceLibraryWindow(new VoiceLibrary(path), runner);
        try
        {
            window.Show();
            Click(window, "VoiceReload");
            Assert.Contains("owns resources", Control<TextBox>(window, "ResultText").Text);
            Assert.False(Directory.Exists(path));
            window.Close();
            Assert.True(runner.IsRunning);
        }
        finally
        {
            window.Close();
            released.TrySetResult();
            await existing.Completion;
        }
    });

    [Fact]
    public Task ClosingDuringImportRetainsOwnershipUntilRealCleanup() => OnDispatcher(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "Martlet.VoiceUI." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "sample.wav");
        File.WriteAllBytes(source, Martlet.Providers.Tests.ProviderFixtures.Wave(160_000));
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var library = new VoiceLibrary(Path.Combine(root, "library"))
        {
            BeforePublication = _ => { entered.TrySetResult(); release.Wait(); }
        };
        var runner = new SetupOperationRunner();
        var main = new MainWindow(new SettingsStore(root), null);
        var mainClosed = false;
        main.Closed += (_, _) => mainClosed = true;
        var window = new VoiceLibraryWindow(library, runner, () => source)
        {
            OperationStarted = main.ObserveVoiceOperation
        };
        VoiceLibraryWindow? reopened = null;
        try
        {
            main.Show();
            window.Owner = main;
            window.Show();
            Click(window, "VoiceBrowse");
            Control<TextBox>(window, "NameInput").Text = "Canceled import";
            Control<TextBox>(window, "TranscriptInput").Text = "Synthetic fixture";
            Control<ComboBox>(window, "RightsChoice").SelectedIndex = 0;
            Control<CheckBox>(window, "RightsConfirmed").IsChecked = true;
            Click(window, "VoiceImport");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            window.Close();
            var oldResult = Control<TextBox>(window, "ResultText").Text;
            Assert.True(runner.IsRunning);
            main.Close();
            Assert.False(mainClosed);
            Assert.True(main.IsVisible);
            Assert.Contains("Voice Library IO", Control<TextBlock>(main, "ActionText").Text);
            reopened = new VoiceLibraryWindow(library, runner);
            reopened.Show();
            Click(reopened, "VoiceReload");
            Assert.Contains("owns resources", Control<TextBox>(reopened, "ResultText").Text);
            release.Set();
            await Wait(() => !runner.IsRunning);
            await Dispatcher.Yield();
            Assert.Equal(oldResult, Control<TextBox>(window, "ResultText").Text);
            Assert.Empty(await library.ListAsync());
            Assert.Empty(Directory.GetFiles(Path.Combine(root, "library"), "*.pending"));
            Assert.True(File.Exists(source));
        }
        finally
        {
            release.Set();
            window.Close();
            reopened?.Close();
            await Wait(() => !runner.IsRunning);
            main.Close();
            await Wait(() => mainClosed);
            Directory.Delete(root, recursive: true);
        }
    });

    private static T Control<T>(Window window, string name) => Assert.IsType<T>(window.FindName(name));
    private static void Click(Window window, string id) =>
        Descendants(window).OfType<Button>().Single(button => AutomationProperties.GetAutomationId(button) == id)
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static async Task Wait(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Voice UI operation did not complete.");
            await Task.Delay(10);
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
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
    }
}
