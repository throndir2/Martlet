using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Martlet.Core.Settings;
using Martlet.Desktop;
using Martlet.Memory;

namespace Martlet.Desktop.Tests;

public sealed class MemoryWindowTests
{
    [Fact]
    public Task RealWindowKeepsDisabledStoreClosedAndRunsExplicitFactAndFrozenExportActions() =>
        OnDispatcher(async () =>
    {
        using var scope = new Scope();
        var store = new SettingsStore(scope.Data);
        Assert.True((await store.SaveAsync(AppSettings.CreateUnconfigured(), null)).Saved);
        var runner = new SetupOperationRunner();
        using var memory = new DesktopMemoryService(store);
        var allowDelete = false;
        var window = new MemoryWindow(
            memory,
            runner,
            chooseExport: () => scope.Export,
            confirm: (_, _, title) => title == "Delete local memory fact" && allowDelete)
        {
            ShowActivated = false,
            ShowInTaskbar = false
        };
        window.Show();
        try
        {
            await Until(() => Text(window, "ConfigurationStatus").Contains(
                "Legacy settings loaded", StringComparison.Ordinal));
            Assert.False(Directory.Exists(scope.Memory));
            Assert.False(Check(window, "MemoryEnable").IsChecked);
            Assert.Equal("MemoryReload", AutomationProperties.GetAutomationId(
                Control<Button>(window, "ReloadButton")));

            Click(window, "MemoryRefreshFacts");
            Assert.Contains("Save or reload", Text(window, "FactStatus"));
            Assert.False(Directory.Exists(scope.Memory));

            Check(window, "MemoryEnable").IsChecked = true;
            Check(window, "MemoryAcceptEnable").IsChecked = true;
            Assert.True(Control<Button>(window, "SaveConfigurationButton").IsEnabled,
                $"runner={runner.IsRunning}; enable={Check(window, "MemoryEnable").IsChecked}; consent={Check(window, "MemoryAcceptEnable").IsChecked}");
            Click(window, "MemorySaveConfiguration");
            await Until(() => !runner.IsRunning && Text(window, "ConfigurationStatus").Contains(
                "enabled", StringComparison.OrdinalIgnoreCase),
                () => $"runner={runner.IsRunning}; status={Text(window, "ConfigurationStatus")}; settings={File.ReadAllText(store.FilePath)}");
            Assert.False(Directory.Exists(scope.Memory));
            var configured = await store.LoadAsync();
            Assert.True(configured.Settings!.Memory!.Enabled);
            Assert.Equal(MemoryStoragePolicy.AppLocalData, configured.Settings.Memory.StoragePolicy);

            Control<TextBox>(window, "FactContent").Text = "Preferred server region is west.";
            Control<ComboBox>(window, "RetentionChoice").SelectedIndex = 1;
            Click(window, "MemorySaveFact");
            await Until(() => !runner.IsRunning && Control<ListBox>(window, "FactsList").Items.Count == 1);
            Assert.True(Directory.Exists(scope.Memory));
            Assert.True(File.Exists(Path.Combine(scope.Memory, ".martlet-memory.v1.json")));
            Assert.DoesNotContain("transcript", await File.ReadAllTextAsync(
                Path.Combine(scope.Memory, ".martlet-memory.v1.json")), StringComparison.OrdinalIgnoreCase);

            var list = Control<ListBox>(window, "FactsList");
            list.SelectedIndex = 0;
            Assert.Contains("Created provenance: UserEntry", Text(window, "FactDetails"));
            Assert.Contains("expires at", Text(window, "FactDetails"));
            Control<TextBox>(window, "FactContent").Text = "Preferred server region is north-west.";
            Click(window, "MemoryEditFact");
            await Until(() => !runner.IsRunning && list.Items.Cast<MemoryFact>().Single().Content.Contains(
                "north-west", StringComparison.Ordinal));
            list.SelectedIndex = 0;
            Assert.Contains("Last-modified provenance: UserEntry", Text(window, "FactDetails"));

            Click(window, "MemoryCreateExportPreview");
            await Until(() => !runner.IsRunning && Text(window, "ExportSummary").Contains(
                "Export approval default: NO", StringComparison.Ordinal));
            Assert.False(Check(window, "MemoryAcceptExport").IsChecked);
            var exactPreview = Control<TextBox>(window, "ExportPreviewText").Text;
            using (var json = JsonDocument.Parse(exactPreview))
            {
                Assert.Equal(1, json.RootElement.GetProperty("schema_version").GetInt32());
                Assert.Single(json.RootElement.GetProperty("facts").EnumerateArray());
            }
            Control<TextBox>(window, "ExportDestination").Text = scope.Export;
            Click(window, "MemoryExport");
            Assert.False(File.Exists(scope.Export));
            Assert.Contains("remains NO", Text(window, "ExportStatus"));

            Check(window, "MemoryAcceptExport").IsChecked = true;
            Click(window, "MemoryExport");
            await Until(() => !runner.IsRunning && File.Exists(scope.Export));
            Assert.Equal(
                JsonDocument.Parse(exactPreview).RootElement.GetRawText(),
                JsonDocument.Parse(await File.ReadAllTextAsync(scope.Export)).RootElement.GetRawText());
            Assert.Contains("It was not uploaded", Text(window, "ExportStatus"));

            list.SelectedIndex = 0;
            Click(window, "MemoryDeleteFact");
            Assert.Single(list.Items.Cast<MemoryFact>());
            allowDelete = true;
            Click(window, "MemoryDeleteFact");
            await Until(() => !runner.IsRunning && list.Items.Count == 0);

            Click(window, "MemoryPurgeExpired");
            await Until(() => !runner.IsRunning && Text(window, "FactStatus").Contains(
                "Purged 0", StringComparison.Ordinal));
        }
        finally
        {
            window.Close();
            await Until(() => !runner.IsRunning);
        }
    });

    [Fact]
    public Task ScopeAndDestinationEditsClearConsentAndBlockThePersistedStore() =>
        OnDispatcher(async () =>
    {
        using var scope = new Scope();
        var store = new SettingsStore(scope.Data);
        var initial = SetupSettings.Begin(null);
        var saved = await store.SaveAsync(initial, null);
        Assert.True(saved.Saved);
        var runner = new SetupOperationRunner();
        using var memory = new DesktopMemoryService(store);
        var configured = await memory.SaveConfigurationAsync(
            initial, saved.Revision, enabled: true, enableApproved: true,
            policy: MemoryStoragePolicy.AppLocalData, customDirectory: null);
        Assert.True(configured.Save.Save.Saved);
        await memory.SaveFactAsync(configured.Settings.Memory!.ConfigurationRevision,
            "server region is west", MemoryRetention.UntilDeleted());
        var window = new MemoryWindow(memory, runner)
        {
            ShowActivated = false,
            ShowInTaskbar = false
        };
        window.Show();
        try
        {
            await Until(() => Text(window, "ConfigurationStatus").Contains(
                "ENABLED", StringComparison.Ordinal));
            Click(window, "MemoryRefreshFacts");
            await Until(() => !runner.IsRunning &&
                Control<ListBox>(window, "FactsList").Items.Count == 1);
            Click(window, "MemoryCreateExportPreview");
            await Until(() => !runner.IsRunning &&
                !string.IsNullOrWhiteSpace(Control<TextBox>(window, "ExportPreviewText").Text));

            Control<TextBox>(window, "ExportDestination").Text = scope.Export;
            Check(window, "MemoryAcceptExport").IsChecked = true;
            Assert.True(Control<Button>(window, "ExportButton").IsEnabled);
            Control<TextBox>(window, "ExportDestination").Text = scope.Export2;
            Assert.False(Check(window, "MemoryAcceptExport").IsChecked);
            Assert.False(Control<Button>(window, "ExportButton").IsEnabled);

            Check(window, "MemoryAcceptEnable").IsChecked = true;
            Control<RadioButton>(window, "CustomChoice").IsChecked = true;
            Control<TextBox>(window, "CustomDirectory").Text = scope.OtherMemory;

            Assert.False(Check(window, "MemoryAcceptEnable").IsChecked);
            Assert.False(Control<Button>(window, "RefreshFactsButton").IsEnabled);
            Assert.Empty(Control<ListBox>(window, "FactsList").Items);
            Assert.Equal("", Control<TextBox>(window, "ExportPreviewText").Text);
            Assert.False(Directory.Exists(scope.OtherMemory));

            var preview = MemoryStoreActivationPreview.Create(scope.Memory);
            using (MemoryStore.Open(preview,
                preview.Authorize(MemoryConsentDecision.Allow)))
            {
                Click(window, "MemoryRefreshFacts");
                Assert.Contains("Save or reload", Text(window, "FactStatus"));
            }
            Assert.Single((await memory.InspectAsync(
                configured.Settings.Memory.ConfigurationRevision)).Facts);
        }
        finally
        {
            window.Close();
            await Until(() => !runner.IsRunning);
        }
    });

    [Fact]
    public async Task UnsafeNetworkConfigurationIsRejectedWithoutStoreAccessOrSettingsMutation()
    {
        using var scope = new Scope();
        var store = new SettingsStore(scope.Data);
        var settings = SetupSettings.Begin(null);
        var saved = await store.SaveAsync(settings, null);
        Assert.True(saved.Saved);
        var before = await File.ReadAllBytesAsync(store.FilePath);
        using var memory = new DesktopMemoryService(store);

        await Assert.ThrowsAsync<Martlet.Core.Contracts.ContractException>(() =>
            memory.SaveConfigurationAsync(
                settings,
                saved.Revision,
                enabled: true,
                enableApproved: true,
                policy: MemoryStoragePolicy.CustomLocalDirectory,
                customDirectory: @"\\server\share\memory"));

        Assert.Equal(before, await File.ReadAllBytesAsync(store.FilePath));
        Assert.False(Directory.Exists(scope.Memory));
    }

    [Fact]
    public async Task AppLocalEnableMigratesLegacySettingsWithoutOpeningStore()
    {
        using var scope = new Scope();
        var store = new SettingsStore(scope.Data);
        var original = AppSettings.CreateUnconfigured();
        var saved = await store.SaveAsync(original, null);
        Assert.True(saved.Saved);
        using var memory = new DesktopMemoryService(store);

        var result = await Task.Run(() => memory.SaveConfigurationAsync(
            original,
            saved.Revision,
            enabled: true,
            enableApproved: true,
            policy: MemoryStoragePolicy.AppLocalData,
            customDirectory: null));

        Assert.True(result.Save.Save.Saved);
        Assert.True(result.Settings.Memory!.Enabled);
        Assert.False(Directory.Exists(scope.Memory));
        Assert.Single(Directory.GetFiles(scope.Data, "settings.v1.*.bak"));
    }

    private static T Control<T>(Window window, string name) where T : FrameworkElement =>
        Assert.IsType<T>(window.FindName(name));

    private static CheckBox Check(Window window, string id) =>
        Descendants(window).OfType<CheckBox>().Single(
            item => AutomationProperties.GetAutomationId(item) == id);

    private static void Click(Window window, string id) =>
        Descendants(window).OfType<Button>().Single(
            item => AutomationProperties.GetAutomationId(item) == id)
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static string Text(Window window, string name) =>
        window.FindName(name) switch
        {
            TextBlock block => block.Text,
            TextBox box => box.Text,
            _ => throw new InvalidOperationException("Required text control missing: " + name)
        };

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var descendant in Descendants(child))
                yield return descendant;
        }
    }

    private static async Task Until(Func<bool> condition, Func<string>? state = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.True(condition(), state?.Invoke() ?? "The bounded UI condition was not reached.");
    }

    private static async Task OnDispatcher(Func<Task> action)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(dispatcher));
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

    private sealed class Scope : IDisposable
    {
        internal string Root { get; } = Path.Combine(
            AppContext.BaseDirectory, "memory-ui", Guid.NewGuid().ToString("N"));
        internal string Data => Path.Combine(Root, "data");
        internal string Memory => Path.Combine(Data, MemorySettings.AppLocalDirectoryName);
        internal string OtherMemory => Path.Combine(Root, "other-memory");
        internal string Export => Path.Combine(Root, "memory-export.json");
        internal string Export2 => Path.Combine(Root, "memory-export-2.json");

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}
