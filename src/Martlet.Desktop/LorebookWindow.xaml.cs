using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Martlet.Core.Contracts;
using Martlet.Core.Lorebooks;
using Martlet.Core.Settings;
using Microsoft.Win32;

namespace Martlet.Desktop;

/// <summary>Edits every lorebook on this PC: SillyTavern World Info import/export, entries, persona scope, scan settings and a
/// local "what triggers" test. Nothing here contacts a model or provider. There is no Save button: every edit saves on its own
/// (a short pause after typing, at once for adding, deleting or importing), and the footer says whether everything is saved.</summary>
public partial class LorebookWindow : ThemedWindow
{
    private readonly LorebookStore store;
    private readonly IReadOnlyList<PersonaProfile> personas;
    private readonly PersonaProfile? activePersona;
    private readonly bool importOnOpen;
    private readonly List<LorebookItem> books = [];
    private readonly AutoSave autoSave;
    private string? revision;
    private string? savedSnapshot;
    private bool loaded;
    private bool busy;
    private bool saving;
    private bool closeConfirmed;
    private bool finishing;
    /// <summary>Why the current edits can't be saved (an invalid entry or setting), or why the last save failed.</summary>
    private string? problem;
    private string? saveError;

    internal Func<string?> ChooseImport { get; init; } = SelectImport;
    internal Func<string, string?> ChooseExport { get; init; } = SelectExport;

    internal LorebookWindow(LorebookStore store, CompanionSettings? companion, bool importOnOpen = false)
    {
        this.store = store;
        personas = companion?.Personas ?? [];
        activePersona = companion?.ActivePersona;
        this.importOnOpen = importOnOpen;
        autoSave = new AutoSave(SaveChangesAsync);
        autoSave.Settled += () => { if (!closeConfirmed) RenderSaveState(); };
        InitializeComponent();
        TestPersona.ItemsSource = personas;
        TestPersona.SelectedItem = activePersona;
        // Every edit in the editor bubbles up here (typing, ticking, choosing); browsing, searching and testing don't save.
        EditorRoot.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new RoutedEventHandler(Edited));
        EditorRoot.AddHandler(System.Windows.Controls.Primitives.ToggleButton.CheckedEvent, new RoutedEventHandler(Edited));
        EditorRoot.AddHandler(System.Windows.Controls.Primitives.ToggleButton.UncheckedEvent, new RoutedEventHandler(Edited));
        EditorRoot.AddHandler(System.Windows.Controls.Primitives.Selector.SelectionChangedEvent, new RoutedEventHandler(Edited));
        Render();
    }

    private void Edited(object sender, RoutedEventArgs e)
    {
        if (!loaded || busy || e.OriginalSource is not DependencyObject source ||
            Within(source, BookChoice, EntryList, EntrySearch, TestInput, TestPersona, TestResult)) return;
        problem = null;
        autoSave.Changed();
        RenderSaveState();
    }

    private static bool Within(DependencyObject source, params DependencyObject[] controls)
    {
        for (var node = source; node is not null; node = node is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                 ? System.Windows.Media.VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            if (controls.Contains(node)) return true;
        return false;
    }

    private void SaveNow()
    {
        autoSave.SaveNowAsync().Forget();
        RenderSaveState();
    }

    private void RenderSaveState()
    {
        var unsaved = problem ?? saveError;
        SaveStateText.Text = !loaded ? busy ? "Loading your lorebooks..." : "Your lorebooks couldn't be loaded."
            : problem is not null ? "Not saved yet: " + problem
            : saveError is not null ? "Not saved: " + saveError + " Martlet tries again with your next change."
            : saving || autoSave.Pending ? "Saving..."
            : "All changes saved.";
        SaveStateText.SetResourceReference(TextBlock.ForegroundProperty, unsaved is null ? "MutedBrush" : "WarningBrush");
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await LoadAsync();
        if (importOnOpen && loaded) await ImportAsync(ChooseImport());
    }

    private async Task LoadAsync()
    {
        busy = true;
        Render();
        try
        {
            var result = await store.LoadAsync();
            revision = result.Revision;
            loaded = result.Loaded;
            books.Clear();
            if (loaded)
                books.AddRange(result.Library.Books.Select(book => LorebookItem.From(book, personas)));
            ShowSettings(result.Library);
            savedSnapshot = loaded ? Snapshot(result.Library) : null;
            problem = saveError = null;
            ResultText.Text = result.Error ?? (books.Count == 0
                ? "No lorebooks yet. Create one, or import a lorebook or character card."
                : $"{books.Count} {(books.Count == 1 ? "lorebook" : "lorebooks")} loaded.");
            RenderBooks(books.FirstOrDefault());
        }
        finally
        {
            busy = false;
            Render();
        }
    }

    private void ShowSettings(LorebookLibrary library)
    {
        ScanDepth.Text = library.ScanDepth.ToString(CultureInfo.CurrentCulture);
        Budget.Text = library.BudgetUtf8Bytes.ToString(CultureInfo.CurrentCulture);
        Recursive.IsChecked = library.Recursive;
        CaseSensitive.IsChecked = library.CaseSensitive;
        WholeWords.IsChecked = library.MatchWholeWords;
    }

    private void Render()
    {
        EditorRoot.IsEnabled = loaded && !busy;
        var book = BookChoice.SelectedItem as LorebookItem;
        BookPanel.Visibility = book is null ? Visibility.Collapsed : Visibility.Visible;
        ExportButton.IsEnabled = DeleteBookButton.IsEnabled = book is not null;
        EntryPanel.Visibility = EntryList.SelectedItem is null ? Visibility.Collapsed : Visibility.Visible;
        EntryCount.Text = book is null ? "" : CollectionViewSource.GetDefaultView(book.Entries) is { } view && EntrySearch.Text.Length > 0
            ? $"{view.Cast<object>().Count()} of {book.Entries.Count} entries match."
            : book.Entries.Count == 0 ? "No entries yet. Add one." : "";
        RenderSaveState();
    }

    private void RenderBooks(LorebookItem? select)
    {
        BookChoice.ItemsSource = null;
        BookChoice.ItemsSource = books;
        BookChoice.SelectedItem = select;
        Render();
    }

    private void BookChoice_Changed(object sender, SelectionChangedEventArgs e)
    {
        foreach (var previous in e.RemovedItems.OfType<LorebookItem>())
            CollectionViewSource.GetDefaultView(previous.Entries).Filter = null;
        EntrySearch.Text = "";
        ApplyFilter();
        if (BookChoice.SelectedItem is LorebookItem { Entries.Count: > 0 } book) EntryList.SelectedItem = book.Entries[0];
        Render();
    }

    private void EntryList_Changed(object sender, SelectionChangedEventArgs e) => Render();

    private void EntrySearch_Changed(object sender, TextChangedEventArgs e)
    {
        ApplyFilter();
        Render();
    }

    private void ApplyFilter()
    {
        if (BookChoice.SelectedItem is not LorebookItem book) return;
        var search = EntrySearch.Text.Trim();
        CollectionViewSource.GetDefaultView(book.Entries).Filter = search.Length == 0 ? null
            : item => item is LorebookEntryItem entry && entry.Matches(search);
    }

    private void NewBook_Click(object sender, RoutedEventArgs e)
    {
        if (books.Count >= LorebookLibrary.MaximumBooks)
        {
            ResultText.Text = $"At most {LorebookLibrary.MaximumBooks} lorebooks are supported. Delete one first.";
            return;
        }
        var book = new LorebookItem { Name = UniqueName("New lorebook") };
        foreach (var persona in personas) book.Personas.Add(new(persona.Id, persona.Name, false));
        book.Entries.Add(new LorebookEntryItem { Uid = 0, Title = "First entry" });
        books.Add(book);
        RenderBooks(book);
        ResultText.Text = "New lorebook added. Add entries; they save as you go.";
        SaveNow();
    }

    private void DeleteBook_Click(object sender, RoutedEventArgs e)
    {
        if (BookChoice.SelectedItem is not LorebookItem book) return;
        if (!ConfirmationDialog.Confirm(this, $"Delete \"{book.Name}\" and its entries? This can't be undone.",
            "Delete lorebook")) return;
        var index = books.IndexOf(book);
        books.Remove(book);
        RenderBooks(books.Count == 0 ? null : books[Math.Min(index, books.Count - 1)]);
        ResultText.Text = $"Deleted \"{book.Name}\".";
        SaveNow();
    }

    private void AddEntry_Click(object sender, RoutedEventArgs e)
    {
        if (BookChoice.SelectedItem is not LorebookItem book) return;
        if (book.Entries.Count >= Lorebook.MaximumEntries)
        {
            ResultText.Text = $"A lorebook has at most {Lorebook.MaximumEntries} entries.";
            return;
        }
        var entry = new LorebookEntryItem { Uid = book.NextUid() };
        book.Entries.Add(entry);
        SelectEntry(entry);
        EntryKeys.Focus();
        SaveNow();
    }

    private void DuplicateEntry_Click(object sender, RoutedEventArgs e)
    {
        if (BookChoice.SelectedItem is not LorebookItem book || EntryList.SelectedItem is not LorebookEntryItem entry) return;
        try
        {
            var copy = entry.Copy(book.NextUid());
            book.Entries.Insert(book.Entries.IndexOf(entry) + 1, copy);
            SelectEntry(copy);
            SaveNow();
        }
        catch (ContractException error) { ResultText.Text = "Fix this entry before duplicating. " + error.Message; }
    }

    private void DeleteEntry_Click(object sender, RoutedEventArgs e)
    {
        if (BookChoice.SelectedItem is not LorebookItem book || EntryList.SelectedItem is not LorebookEntryItem entry) return;
        var index = book.Entries.IndexOf(entry);
        book.Entries.Remove(entry);
        if (book.Entries.Count > 0) EntryList.SelectedItem = book.Entries[Math.Min(index, book.Entries.Count - 1)];
        ResultText.Text = $"Deleted the entry \"{entry.Label}\".";
        Render();
        SaveNow();
    }

    private void SelectEntry(LorebookEntryItem entry)
    {
        if (EntrySearch.Text.Length > 0) EntrySearch.Text = "";
        EntryList.SelectedItem = entry;
        EntryList.ScrollIntoView(entry);
        Render();
    }

    private async void Import_Click(object sender, RoutedEventArgs e) => await ImportAsync(ChooseImport());

    private async Task ImportAsync(string? path)
    {
        if (path is null || !loaded || busy) return;
        if (books.Count >= LorebookLibrary.MaximumBooks)
        {
            ResultText.Text = $"At most {LorebookLibrary.MaximumBooks} lorebooks are supported. Delete one first.";
            return;
        }
        busy = true;
        Render();
        var imported = false;
        try
        {
            var import = await Task.Run(() =>
            {
                using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                return SillyTavernLorebooks.Import(input, Path.GetFileNameWithoutExtension(path));
            });
            var item = LorebookItem.From(import.Book with { Name = UniqueName(import.Book.Name) }, personas);
            books.Add(item);
            RenderBooks(item);
            var constant = import.Book.Entries.Count(entry => entry.Constant);
            ResultText.Text = $"Imported \"{item.Name}\" with {import.Book.Entries.Count} " +
                $"{(import.Book.Entries.Count == 1 ? "entry" : "entries")}" + (constant > 0 ? $" ({constant} always on)" : "") +
                (import.SkippedEntries > 0 ? $". {import.SkippedEntries} skipped." : "") +
                " It is on for every persona.";
            imported = true;
        }
        catch (ContractException error) { ResultText.Text = error.Message; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            ResultText.Text = "That file could not be read. Check that it exists and you can open it, then try again.";
        }
        finally
        {
            busy = false;
            Render();
        }
        if (imported) SaveNow();
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (BookChoice.SelectedItem is not LorebookItem item) return;
        Lorebook book;
        try { book = item.ToBook(); }
        catch (ContractException error) { ResultText.Text = "Fix this lorebook before exporting it. " + error.Message; return; }
        var path = ChooseExport(book.Name);
        if (path is null) return;
        try
        {
            File.WriteAllBytes(path, SillyTavernLorebooks.Export(book));
            ResultText.Text = $"Exported \"{book.Name}\". Import it into SillyTavern as World Info.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            ResultText.Text = "The lorebook could not be exported. Choose a folder you can write to, then try again.";
        }
    }

    private LorebookLibrary Build()
    {
        int Number(TextBox box, string name, int minimum, int maximum)
        {
            ContractRules.Require(int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var value) &&
                value >= minimum && value <= maximum, $"{name} must be a whole number from {minimum:N0} to {maximum:N0}.");
            return value;
        }
        var library = new LorebookLibrary
        {
            SchemaVersion = LorebookLibrary.CurrentSchemaVersion,
            ScanDepth = Number(ScanDepth, "Scan depth", 0, LorebookLibrary.MaximumScanDepth),
            BudgetUtf8Bytes = Number(Budget, "The budget", LorebookLibrary.MinimumBudgetUtf8Bytes, LorebookLibrary.MaximumBudgetUtf8Bytes),
            Recursive = Recursive.IsChecked == true,
            CaseSensitive = CaseSensitive.IsChecked == true,
            MatchWholeWords = WholeWords.IsChecked == true,
            Books = books.Select(book => book.ToBook()).ToArray()
        };
        library.Validate();
        return library;
    }

    private static string Snapshot(LorebookLibrary library) => JsonSerializer.Serialize(library);

    /// <summary>The auto-save: writes the lorebooks when they differ from what is saved. Returns false to be tried again shortly
    /// while an import or load runs.</summary>
    private async Task<bool> SaveChangesAsync()
    {
        if (!loaded) return true;
        if (busy) return false;
        LorebookLibrary library;
        try { library = Build(); }
        catch (ContractException error)
        {
            problem = error.Message;
            RenderSaveState();
            return true;
        }
        problem = null;
        var snapshot = Snapshot(library);
        if (snapshot == savedSnapshot)
        {
            saveError = null;
            RenderSaveState();
            return true;
        }
        saving = true;
        RenderSaveState();
        try
        {
            var saved = await store.SaveAsync(library, revision);
            if (!saved.Saved)
            {
                saveError = saved.Revision != revision
                    ? "Your lorebooks were changed somewhere else. Close Lorebooks and open it again to see them."
                    : saved.Error ?? "Your lorebooks couldn't be written.";
                return true;
            }
            saveError = null;
            revision = saved.Revision;
            savedSnapshot = snapshot;
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ContractException)
        {
            saveError = error is ContractException ? error.Message : "Your lorebooks couldn't be written. Check access to your data folder.";
            return true;
        }
        finally
        {
            saving = false;
            RenderSaveState();
        }
    }

    private void Test_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TestInput.Text))
        {
            TestResult.Text = "Type a message to test first.";
            return;
        }
        LorebookLibrary library;
        try { library = Build(); }
        catch (ContractException error)
        {
            TestResult.Text = "Fix this first: " + error.Message;
            return;
        }
        var persona = TestPersona.SelectedItem as PersonaProfile;
        // Chance-based entries are shown as if their roll succeeded.
        var result = LorebookScanner.Scan(library, new(TestInput.Text, [], persona?.Id, persona?.Name), _ => 0);
        TestResult.Text = Describe(result, library);
    }

    private static string Describe(LorebookScanResult result, LorebookLibrary library)
    {
        if (result.ActiveBooks == 0) return "No lorebook is on for this persona.";
        var text = new StringBuilder();
        if (result.Included.Count == 0)
            text.Append("No entries would be added. Martlet checks the last ").Append(library.ScanDepth)
                .Append(library.ScanDepth == 1 ? " message" : " messages").Append(library.MatchWholeWords ? " as whole words" : "").Append('.');
        else
        {
            text.Append($"{result.Included.Count} {(result.Included.Count == 1 ? "entry" : "entries")} would be added:\n");
            foreach (var hit in result.Included)
                text.Append($"- {hit.Entry.Label} from {hit.BookName} ({Trigger(hit)}, " +
                    $"{(hit.Entry.Position == LorebookPosition.BeforePersona ? "before" : "after")} the persona" +
                    (hit.Entry.Probability < 100 ? $"; {hit.Entry.Probability}% chance" : "") + ")\n");
        }
        if (result.OverBudget.Count > 0)
            text.Append($"\nSkipped because of the budget: {string.Join(", ", result.OverBudget.Select(hit => hit.Entry.Label))}.\n");
        return text.ToString().TrimEnd();
    }

    private static string Trigger(LorebookHit hit) => hit.Trigger == "always on" ? "always on" : $"keyword \"{hit.Trigger}\"";

    private string UniqueName(string basis)
    {
        var library = LorebookLibrary.Create() with
        {
            Books = books.Select(book => new Lorebook { Id = book.Id, Name = book.Name.Trim().Length > 0 ? book.Name.Trim() : " " }).ToArray()
        };
        return library.UniqueName(basis);
    }

    private void Window_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Effects = loaded && !busy ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_PreviewDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: 1 } files)
        {
            ResultText.Text = "Drop one lorebook or character card at a time.";
            return;
        }
        await ImportAsync(files[0]);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Closing saves what is still waiting first. Only edits that can't be saved ask before they are dropped.</summary>
    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (closeConfirmed || !loaded ||
            !busy && !saving && !autoSave.Pending && problem is null && saveError is null && !IsDirty()) return;
        e.Cancel = true;
        if (finishing) return;
        finishing = true;
        try
        {
            for (var tries = 0; tries < 50 && (busy || saving || autoSave.Pending || IsDirty()); tries++)
            {
                if (busy) await Task.Delay(100);
                else
                {
                    await autoSave.SaveNowAsync();
                    if (problem is not null || saveError is not null) break;
                }
            }
            var unsaved = problem ?? saveError ?? (busy || IsDirty() ? "Martlet is still busy with your lorebooks." : null);
            if (unsaved is not null && !ConfirmationDialog.Confirm(this,
                    $"Your latest lorebook change isn't saved: {unsaved}\n\nClose anyway and lose it?", "Unsaved lorebook change"))
                return;
            autoSave.Cancel();
            closeConfirmed = true;
            await Dispatcher.InvokeAsync(Close);
        }
        finally { finishing = false; }
    }

    private bool IsDirty()
    {
        if (!loaded) return false;
        try { return Snapshot(Build()) != savedSnapshot; }
        catch (ContractException) { return true; }
    }

    private static string? SelectImport()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import a lorebook",
            Filter = "Lorebooks and character cards (*.json;*.png;*.charx)|*.json;*.png;*.charx|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private static string? SelectExport(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var dialog = new SaveFileDialog
        {
            Title = "Export lorebook for SillyTavern",
            Filter = "SillyTavern World Info (*.json)|*.json",
            DefaultExt = ".json",
            AddExtension = true,
            FileName = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()) + ".json"
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
