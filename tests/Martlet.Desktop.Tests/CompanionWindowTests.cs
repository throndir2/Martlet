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
    public Task RealWindowImportsAutoSavesMigratesAndExportsWithoutRuntimeEffects() => OnDispatcher(async () =>
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
            Assert.Equal(original, await File.ReadAllBytesAsync(store.FilePath));

            Click(window, "CompanionImport");
            await Until(() => Field<TextBox>(window, "PersonaText").Text == "Listen first.\nBe concise.");
            Field<TextBox>(window, "PersonaName").Text = "Game friend ";
            Assert.Equal("Saving...", Field<TextBlock>(window, "SaveStateText").Text);

            // No Save button: the changes are written a moment after the last one.
            await Until(() => window.AllSaved && !runner.IsRunning);
            Assert.Equal("All changes saved.", Field<TextBlock>(window, "SaveStateText").Text);
            Assert.Contains("older settings were updated", Field<TextBox>(window, "ResultText").Text, StringComparison.OrdinalIgnoreCase);
            var loaded = await store.LoadAsync();
            Assert.Equal(AppSettings.CurrentSchemaVersion, loaded.Settings!.SchemaVersion);
            var persona = loaded.Settings.Companion!.ActivePersona;
            Assert.Equal("Game friend", persona.Name);
            Assert.Equal("Listen first.\nBe concise.", persona.Text);
            Assert.DoesNotContain("\"styles\"", await File.ReadAllTextAsync(store.FilePath), StringComparison.Ordinal);
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
    public Task EmptyNameIsNotSavedAndSaysWhy() => OnDispatcher(async () =>
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
            Assert.Null(window.FindName("HelpfulWeight"));
            Field<TextBox>(window, "PersonaName").Text = " ";
            await Until(() => Field<TextBlock>(window, "SaveStateText").Text.Contains("Persona names must be", StringComparison.Ordinal));
            Assert.StartsWith("Not saved yet", Field<TextBlock>(window, "SaveStateText").Text);
            Assert.False(runner.IsRunning);
            Assert.Equal(original, await File.ReadAllBytesAsync(store.FilePath));
        }
        finally
        {
            // Put the name back so closing has nothing unsaved to ask about.
            Field<TextBox>(window, "PersonaName").Text = "Martlet";
            await Until(() => window.AllSaved && !runner.IsRunning);
            window.Close();
        }
    });

    [Fact]
    public Task AutoSaveKeepsOtherSettingsChangedMeanwhile() => OnDispatcher(async () =>
    {
        using var scope = new Scope();
        var store = new SettingsStore(scope.Data);
        Assert.True((await store.SaveAsync(CompanionSettings.Begin(null), null)).Saved);
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
            Field<TextBox>(window, "PersonaText").Text = "Keep this.";
            Click(window, "CompanionNew");
            Assert.Equal("New persona", Field<TextBox>(window, "PersonaName").Text);
            await Until(() => window.AllSaved && !runner.IsRunning);

            // Something else (another page, or sync from another computer) saves the same settings in the meantime.
            var external = await store.LoadAsync();
            var prompt = PromptCatalog.All[0].Id;
            Assert.True((await store.SaveAsync(external.Settings! with
            {
                Prompts = PromptSettings.Normalize(new Dictionary<string, string> { [prompt] = "External prompt." })
            }, external.Revision)).Saved);

            Field<TextBox>(window, "PersonaName").Text = "Second";
            await Until(() => window.AllSaved && !runner.IsRunning);

            var persisted = (await store.LoadAsync()).Settings!;
            Assert.Equal(["Martlet", "Second"], persisted.Companion!.Personas.Select(persona => persona.Name));
            Assert.Equal("Second", persisted.Companion.ActivePersona.Name);
            Assert.Equal("Keep this.", persisted.Companion.Personas[0].Text);
            Assert.Equal("External prompt.", PromptSettings.Text(persisted.Prompts, prompt));
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task CharacterCardsAddAndUpdatePersonas() => OnDispatcher(async () =>
    {
        using var scope = new Scope();
        var store = new SettingsStore(scope.Data);
        Assert.True((await store.SaveAsync(CompanionSettings.Begin(null), null)).Saved);
        Directory.CreateDirectory(scope.Root);
        var aria = Path.Combine(scope.Root, "aria.json");
        var bob = Path.Combine(scope.Root, "bob.json");
        await File.WriteAllTextAsync(aria, """{"spec":"chara_card_v2","spec_version":"2.0","data":{"name":"Aria","description":"{{char}} sings to {{user}}."}}""");
        await File.WriteAllTextAsync(bob, """{"name":"Bob","description":"Gruff.","personality":"","scenario":"","first_mes":"","mes_example":""}""");
        var card = aria;
        var runner = new SetupOperationRunner();
        var window = new CompanionWindow(new CompanionSettingsService(store), runner, chooseCard: () => card)
        {
            ShowActivated = false,
            ShowInTaskbar = false
        };
        window.Show();
        try
        {
            await Until(() => Field<StackPanel>(window, "EditorPanel").IsEnabled);
            Click(window, "CompanionCardNew");
            await Until(() => !runner.IsRunning && Field<TextBox>(window, "PersonaName").Text == "Aria");
            Assert.Equal("Aria sings to the user.", Field<TextBox>(window, "PersonaText").Text);
            Assert.Equal(2, Field<ComboBox>(window, "PersonaChoice").Items.Count);

            card = bob;
            Click(window, "CompanionCardUpdate");
            await Until(() => !runner.IsRunning && Field<TextBox>(window, "PersonaName").Text == "Bob");
            Assert.Equal("Gruff.", Field<TextBox>(window, "PersonaText").Text);
            await Until(() => window.AllSaved && !runner.IsRunning);

            var personas = (await store.LoadAsync()).Settings!.Companion!.Personas;
            Assert.Equal(["Martlet", "Bob"], personas.Select(persona => persona.Name));
            Assert.Equal("Gruff.", personas[1].Text);
        }
        finally
        {
            window.Close();
            await Until(() => !runner.IsRunning);
        }
    });

    [Fact]
    public Task CharacterCardKeywordLoreBecomesAPersonaLorebook() => OnDispatcher(async () =>
    {
        using var scope = new Scope();
        var store = new SettingsStore(scope.Data);
        Assert.True((await store.SaveAsync(CompanionSettings.Begin(null), null)).Saved);
        Directory.CreateDirectory(scope.Root);
        var card = Path.Combine(scope.Root, "aria.json");
        await File.WriteAllTextAsync(card, """
            {"spec":"chara_card_v2","data":{"name":"Aria","description":"A bard.","character_book":{"entries":[
              {"keys":[],"content":"Always lore.","enabled":true,"constant":true},
              {"keys":["inn"],"content":"The inn is the Gilded Goose.","enabled":true}]}}}
            """);
        var lore = new Martlet.Core.Lorebooks.LorebookStore(scope.Data);
        var runner = new SetupOperationRunner();
        var window = new CompanionWindow(new CompanionSettingsService(store), runner, chooseCard: () => card, lorebooks: lore)
        {
            ShowActivated = false,
            ShowInTaskbar = false
        };
        window.Show();
        try
        {
            await Until(() => Field<StackPanel>(window, "EditorPanel").IsEnabled);
            Click(window, "CompanionCardNew");
            await Until(() => !runner.IsRunning && Field<TextBox>(window, "PersonaName").Text == "Aria");
            await Until(() => window.AllSaved && !runner.IsRunning);
            Assert.Contains("Lorebook \"Aria lore\" saved for this persona", Field<TextBox>(window, "ResultText").Text);

            var aria = (await store.LoadAsync()).Settings!.Companion!.Personas.Single(persona => persona.Name == "Aria");
            var book = Assert.Single((await lore.LoadAsync()).Library.Books);
            Assert.Equal(("Aria lore", Martlet.Core.Lorebooks.LorebookActivation.SelectedPersonas, aria.Id),
                (book.Name, book.Activation, Assert.Single(book.PersonaIds)));
            Assert.Equal("The inn is the Gilded Goose.", Assert.Single(book.Entries).Content);
        }
        finally
        {
            window.Close();
            await Until(() => !runner.IsRunning);
        }
    });

    [Fact]
    public Task LorebookWindowImportsTestsEditsAndAutoSaves() => OnDispatcher(async () =>
    {
        using var scope = new Scope();
        Directory.CreateDirectory(scope.Root);
        var file = Path.Combine(scope.Root, "world.json");
        await File.WriteAllTextAsync(file, """
            {"entries":{"0":{"uid":0,"key":["castle"],"comment":"Castle","content":"The castle belongs to Queen Mab.","order":10},
                        "1":{"uid":1,"key":["Mab"],"comment":"Mab","content":"Mab rules the fae.","order":20}}}
            """);
        var lore = new Martlet.Core.Lorebooks.LorebookStore(scope.Data);
        var window = new LorebookWindow(lore, CompanionSettings.Begin(null).Companion)
        {
            ChooseImport = () => file,
            ShowActivated = false,
            ShowInTaskbar = false
        };
        window.Show();
        try
        {
            await Until(() => Field<StackPanel>(window, "EditorRoot").IsEnabled);
            Click(window, "LorebookImport");
            await Until(() => Field<TextBox>(window, "ResultText").Text.StartsWith("Imported \"world\"", StringComparison.Ordinal));
            await Until(() => Field<TextBlock>(window, "SaveStateText").Text == "All changes saved.");
            Assert.Equal("The castle belongs to Queen Mab.", Assert.Single((await lore.LoadAsync()).Library.Books).Entries[0].Content);

            Field<TextBox>(window, "TestInput").Text = "Can we visit the castle?";
            Click(window, "LorebookTest");
            var tested = Field<TextBox>(window, "TestResult").Text;
            Assert.Contains("2 entries would be added", tested);
            Assert.Contains("Mab from world", tested);

            Assert.Equal("Castle", Field<TextBox>(window, "EntryTitle").Text);
            Field<TextBox>(window, "EntryContent").Text = "The castle is empty now.";
            Assert.Equal("Saving...", Field<TextBlock>(window, "SaveStateText").Text);
            await Until(() => Field<TextBlock>(window, "SaveStateText").Text == "All changes saved.");
            var saved = Assert.Single((await lore.LoadAsync()).Library.Books);
            Assert.Equal(("world", "The castle is empty now."), (saved.Name, saved.Entries[0].Content));
        }
        finally
        {
            window.Close();
        }
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
