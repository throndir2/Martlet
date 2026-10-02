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
/// local "what triggers" test. Nothing here contacts a model or provider.</summary>
public partial class LorebookWindow : ThemedWindow
{
    private readonly LorebookStore store;
    private readonly IReadOnlyList<PersonaProfile> personas;
    private readonly PersonaProfile? activePersona;
    private readonly bool importOnOpen;
    private readonly List<LorebookItem> books = [];
    private string? revision;
    private string? savedSnapshot;
    private bool loaded;
    private bool busy;
    private bool closeConfirmed;

    internal Func<string?> ChooseImport { get; init; } = SelectImport;
    internal Func<string, string?> ChooseExport { get; init; } = SelectExport;

    internal LorebookWindow(LorebookStore store, CompanionSettings? companion, bool importOnOpen = false)
    {
        this.store = store;
        personas = companion?.Personas ?? [];
        activePersona = companion?.ActivePersona;
        this.importOnOpen = importOnOpen;
        InitializeComponent();
        TestPersona.ItemsSource = personas;
        TestPersona.SelectedItem = activePersona;
        Render();
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
            ResultText.Text = result.Error ?? (books.Count == 0
                ? "No lorebooks yet. Create one, or import a SillyTavern World Info file or a character card with a lorebook."
                : $"{books.Count} {(books.Count == 1 ? "lorebook" : "lorebooks")} loaded from this PC.");
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
        SaveButton.IsEnabled = loaded && !busy;
        var book = BookChoice.SelectedItem as LorebookItem;
        BookPanel.Visibility = book is null ? Visibility.Collapsed : Visibility.Visible;
        ExportButton.IsEnabled = DeleteBookButton.IsEnabled = book is not null;
        EntryPanel.Visibility = EntryList.SelectedItem is null ? Visibility.Collapsed : Visibility.Visible;
        EntryCount.Text = book is null ? "" : CollectionViewSource.GetDefaultView(book.Entries) is { } view && EntrySearch.Text.Length > 0
            ? $"{view.Cast<object>().Count()} of {book.Entries.Count} entries match."
            : book.Entries.Count == 0 ? "No entries yet. Add one." : "";
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
        ResultText.Text = "New lorebook added. Give it entries, then Save lorebooks to keep it.";
    }

    private void DeleteBook_Click(object sender, RoutedEventArgs e)
    {
        if (BookChoice.SelectedItem is not LorebookItem book) return;
        if (!ConfirmationDialog.Confirm(this, $"Delete the lorebook \"{book.Name}\" and its {book.Entries.Count} entries? " +
            "It is gone once you Save lorebooks; Reload brings it back before then.", "Delete lorebook")) return;
        var index = books.IndexOf(book);
        books.Remove(book);
        RenderBooks(books.Count == 0 ? null : books[Math.Min(index, books.Count - 1)]);
        ResultText.Text = $"\"{book.Name}\" removed. Save lorebooks to make it permanent.";
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
    }

    private void DuplicateEntry_Click(object sender, RoutedEventArgs e)
    {
        if (BookChoice.SelectedItem is not LorebookItem book || EntryList.SelectedItem is not LorebookEntryItem entry) return;
        try
        {
            var copy = entry.Copy(book.NextUid());
            book.Entries.Insert(book.Entries.IndexOf(entry) + 1, copy);
            SelectEntry(copy);
        }
        catch (ContractException error) { ResultText.Text = "Fix this entry before duplicating it. " + error.Message; }
    }

    private void DeleteEntry_Click(object sender, RoutedEventArgs e)
    {
        if (BookChoice.SelectedItem is not LorebookItem book || EntryList.SelectedItem is not LorebookEntryItem entry) return;
        var index = book.Entries.IndexOf(entry);
        book.Entries.Remove(entry);
        if (book.Entries.Count > 0) EntryList.SelectedItem = book.Entries[Math.Min(index, book.Entries.Count - 1)];
        ResultText.Text = $"Entry \"{entry.Label}\" removed. Save lorebooks to make it permanent.";
        Render();
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
            ResultText.Text = $"Imported \"{item.Name}\" from a {import.Format}: {import.Book.Entries.Count} " +
                $"{(import.Book.Entries.Count == 1 ? "entry" : "entries")}" + (constant > 0 ? $" ({constant} always on)" : "") +
                (import.SkippedEntries > 0 ? $"; {import.SkippedEntries} skipped (empty, too long or without keywords)" : "") +
                ". It is on for every persona; change that above, then Save lorebooks to keep it.";
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
            ResultText.Text = $"Exported \"{book.Name}\" as SillyTavern World Info JSON. Import it in SillyTavern under World Info.";
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

    private bool IsDirty()
    {
        if (!loaded) return false;
        try { return Snapshot(Build()) != savedSnapshot; }
        catch (ContractException) { return true; }
    }

    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveAsync();

    private async Task<bool> SaveAsync()
    {
        if (!loaded || busy) return false;
        LorebookLibrary library;
        try { library = Build(); }
        catch (ContractException error)
        {
            ResultText.Text = "Not saved. " + error.Message;
            return false;
        }
        busy = true;
        Render();
        try
        {
            var saved = await store.SaveAsync(library, revision);
            if (!saved.Saved)
            {
                ResultText.Text = "Not saved. " + saved.Error;
                return false;
            }
            revision = saved.Revision;
            savedSnapshot = Snapshot(library);
            var on = library.Books.Count(book => book.Activation != LorebookActivation.Off);
            ResultText.Text = $"Lorebooks saved on this PC: {library.Books.Count} {(library.Books.Count == 1 ? "lorebook" : "lorebooks")}, {on} on. " +
                "The next reply uses them.";
            return true;
        }
        finally
        {
            busy = false;
            Render();
        }
    }

    private async void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (IsDirty() && !ConfirmationDialog.Confirm(this, "Discard your unsaved lorebook changes and reload the saved lorebooks?", "Reload lorebooks"))
            return;
        await LoadAsync();
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
        if (result.ActiveBooks == 0) return "No lorebook is on for this persona, so nothing would be added.";
        var text = new StringBuilder();
        if (result.Included.Count == 0)
            text.Append("Nothing triggers. Keywords are searched in the last ").Append(library.ScanDepth)
                .Append(library.ScanDepth == 1 ? " message" : " messages").Append(library.MatchWholeWords ? " as whole words" : "").Append('.');
        else
        {
            text.Append($"{result.Included.Count} {(result.Included.Count == 1 ? "entry" : "entries")} would be added " +
                $"({result.UsedUtf8Bytes:N0} of {library.BudgetUtf8Bytes:N0} budget bytes):\n");
            foreach (var hit in result.Included)
                text.Append($"- {hit.Entry.Label} ({hit.BookName}; {Trigger(hit)}; " +
                    $"{(hit.Entry.Position == LorebookPosition.BeforePersona ? "before" : "after")} the persona" +
                    (hit.Entry.Probability < 100 ? $"; {hit.Entry.Probability}% chance" : "") + ")\n");
        }
        if (result.OverBudget.Count > 0)
            text.Append($"\nLeft out by the budget: {string.Join(", ", result.OverBudget.Select(hit => hit.Entry.Label))}.\n");
        if (result.Included.Count > 0)
        {
            var (before, after) = LorebookPromptContext.Blocks(result.Included);
            text.Append("\nAdded to the instructions:\n");
            if (before is not null) text.Append(before).Append("\n[persona]\n");
            else text.Append("[persona]\n");
            if (after is not null) text.Append(after).Append('\n');
        }
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

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (closeConfirmed) return;
        if (busy || IsDirty() && !ConfirmationDialog.Confirm(this, "Close without saving your lorebook changes?", "Unsaved lorebook changes"))
        {
            e.Cancel = true;
            return;
        }
        closeConfirmed = true;
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
