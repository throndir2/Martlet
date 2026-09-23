using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Martlet.Core.Settings;
using Martlet.Desktop;

namespace Martlet.Desktop.Tests;

public sealed class CompanionWindowTests
{
    [Fact]
    public Task RealWindowImportsAppliesSavesMigratesAndExportsWithoutRuntimeEffects() => OnDispatcher(async () =>
    {
        using var scope = new Scope();
        var store = new SettingsStore(scope.Data);
        Assert.True((await store.SaveAsync(AppSettings.CreateUnconfigured(), null)).Saved);
        var original = await File.ReadAllBytesAsync(store.FilePath);
        Directory.CreateDirectory(scope.Root);
        await File.WriteAllTextAsync(scope.Import, "Listen first.\nBe concise.");
        var runner = new SetupOperationRunner();
        var window = new CompanionWindow(new CompanionSettingsService(store), runner,
            chooseImport: () => scope.Import, chooseExport: () => scope.Export)
        {
            ShowActivated = false,
            ShowInTaskbar = false
        };
        window.Show();
        try
        {
            await Until(() => Field<StackPanel>(window, "EditorPanel").IsEnabled);
            Assert.Equal("Martlet", Field<TextBox>(window, "PersonaName").Text);
            Assert.Equal(100, Field<Slider>(window, "HelpfulWeight").Value);

            Click(window, "CompanionImport");
            await Until(() => Field<TextBox>(window, "PersonaText").Text == "Listen first.\nBe concise.");
            Field<TextBox>(window, "PersonaName").Text = "Game friend";
            Field<Slider>(window, "HelpfulWeight").Value = 50;
            Field<Slider>(window, "SarcasticWeight").Value = 20;
            Field<Slider>(window, "SillyWeight").Value = 15;
            Field<Slider>(window, "DistractedWeight").Value = 5;
            Field<Slider>(window, "TeasingWeight").Value = 10;
            Click(window, "CompanionApply");
            Assert.Contains("applied", Field<TextBox>(window, "ResultText").Text, StringComparison.OrdinalIgnoreCase);

            Click(window, "CompanionSave");
            await Until(() => !runner.IsRunning &&
                Field<TextBox>(window, "ResultText").Text.Contains("migrated", StringComparison.OrdinalIgnoreCase));
            var loaded = await store.LoadAsync();
            Assert.Equal(AppSettings.CurrentSchemaVersion, loaded.Settings!.SchemaVersion);
            var persona = loaded.Settings.Companion!.ActivePersona;
            Assert.Equal("Game friend", persona.Name);
            Assert.Equal("Listen first.\nBe concise.", persona.Text);
            Assert.Equal(new ResponseStyleWeights
            {
                Helpful = 50, Sarcastic = 20, Silly = 15, Distracted = 5, PlayfulTeasing = 10
            }, persona.Styles);
            Assert.Equal(original, await File.ReadAllBytesAsync(Assert.Single(
                Directory.GetFiles(scope.Data, "settings.v1.*.bak"))));

            Click(window, "CompanionExport");
            await Until(() => File.Exists(scope.Export) && !runner.IsRunning);
            Assert.Equal(persona.Text, await File.ReadAllTextAsync(scope.Export));
            Click(window, "CompanionExport");
            await Until(() => Field<TextBox>(window, "ResultText").Text.Contains("already exists", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(persona.Text, await File.ReadAllTextAsync(scope.Export));
        }
        finally
        {
            window.Close();
            await Until(() => !runner.IsRunning);
        }
    });

    [Fact]
    public Task AllZeroWeightsStayAnUnsavedDraft() => OnDispatcher(async () =>
    {
        using var scope = new Scope();
        var store = new SettingsStore(scope.Data);
        var settings = CompanionSettings.Begin(null);
        Assert.True((await store.SaveAsync(settings, null)).Saved);
        var original = await File.ReadAllBytesAsync(store.FilePath);
        var runner = new SetupOperationRunner();
        var window = new CompanionWindow(new CompanionSettingsService(store), runner)
        {
            ShowActivated = false,
            ShowInTaskbar = false
        };
        window.Show();
        try
        {
            await Until(() => Field<StackPanel>(window, "EditorPanel").IsEnabled);
            foreach (var name in new[] { "HelpfulWeight", "SarcasticWeight", "SillyWeight", "DistractedWeight", "TeasingWeight" })
                Field<Slider>(window, name).Value = 0;
            Click(window, "CompanionSave");
            Assert.Contains("greater than zero", Field<TextBox>(window, "ResultText").Text);
            Assert.False(runner.IsRunning);
            Assert.Equal(original, await File.ReadAllBytesAsync(store.FilePath));
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task NewAppliesDirtyEditorAndFailedSaveRetainsDraft() => OnDispatcher(async () =>
    {
        using var scope = new Scope();
        var store = new SettingsStore(scope.Data);
        var settings = CompanionSettings.Begin(null);
        var initial = await store.SaveAsync(settings, null);
        Assert.True(initial.Saved);
        var runner = new SetupOperationRunner();
        var window = new CompanionWindow(new CompanionSettingsService(store), runner)
        {
            ShowActivated = false,
            ShowInTaskbar = false
        };
        window.Show();
        try
        {
            await Until(() => Field<StackPanel>(window, "EditorPanel").IsEnabled);
            Field<TextBox>(window, "PersonaText").Text = "Keep this unsaved draft.";
            Click(window, "CompanionNew");
            Assert.Equal("New persona", Field<TextBox>(window, "PersonaName").Text);

            var external = (await store.LoadAsync()).Settings!;
            var externalPersona = external.Companion!.ActivePersona;
            external = external with
            {
                Companion = external.Companion.Update(externalPersona.Id, externalPersona.Name,
                    "External change.", externalPersona.Styles)
            };
            Assert.True((await store.SaveAsync(external, initial.Revision)).Saved);

            Click(window, "CompanionSave");
            await Until(() => !runner.IsRunning &&
                Field<TextBox>(window, "ResultText").Text.Contains("changed since", StringComparison.OrdinalIgnoreCase));
            Assert.True(Field<StackPanel>(window, "EditorPanel").IsEnabled);
            Assert.Equal("New persona", Field<TextBox>(window, "PersonaName").Text);

            var persisted = (await store.LoadAsync()).Settings!;
            Assert.Equal("External change.", persisted.Companion!.ActivePersona.Text);
        }
        finally { window.Close(); }
    });

    private static T Field<T>(Window window, string name) where T : FrameworkElement =>
        Assert.IsType<T>(window.FindName(name));

    private static void Click(DependencyObject root, string id) =>
        ButtonById(root, id).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static Button ButtonById(DependencyObject root, string id)
    {
        if (root is Button button && AutomationProperties.GetAutomationId(button) == id) return button;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            if (Find(child) is { } found) return found;
        throw new InvalidOperationException("Required authored button missing: " + id);

        Button? Find(DependencyObject current)
        {
            if (current is Button candidate && AutomationProperties.GetAutomationId(candidate) == id) return candidate;
            foreach (var child in LogicalTreeHelper.GetChildren(current).OfType<DependencyObject>())
                if (Find(child) is { } found) return found;
            return null;
        }
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(condition());
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

    private sealed class Scope : IDisposable
    {
        internal string Root { get; } = Path.Combine(AppContext.BaseDirectory, "companion-ui", Guid.NewGuid().ToString("N"));
        internal string Data => Path.Combine(Root, "data");
        internal string Import => Path.Combine(Root, "persona.txt");
        internal string Export => Path.Combine(Root, "persona-export.txt");
        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}
