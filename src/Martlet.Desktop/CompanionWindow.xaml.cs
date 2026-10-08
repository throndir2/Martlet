using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Martlet.Core.Contracts;
using Martlet.Core.Lorebooks;
using Martlet.Core.Settings;
using Microsoft.Win32;

namespace Martlet.Desktop;

/// <summary>Personality: the personas Martlet can be and where their voice pauses. There is no Save button: every change saves
/// on its own (shortly after typing stops, at once when a persona is chosen, added, duplicated, deleted or imported) into the
/// newest saved settings, so nothing synced in from your other computers in the meantime is overwritten. The footer says
/// whether everything is saved, or why the latest change isn't.</summary>
public partial class CompanionWindow : ThemedWindow
{
    private readonly ICompanionSettingsService service;
    private readonly SetupOperationRunner operations;
    private readonly Func<string?> chooseImport;
    private readonly Func<string?> chooseExport;
    private readonly Func<string?> chooseCard;
    private readonly bool importCardOnOpen;
    private readonly LorebookStore? lorebooks;
    // Keyword lorebooks from imported character cards, by persona; saved to the lorebook library together with the personas.
    private readonly Dictionary<Guid, Lorebook> pendingLore = [];
    private readonly AutoSave autoSave;
    /// <summary>The personas as edited (the editor's fields are applied by <see cref="ApplyEditor"/>).</summary>
    private CompanionSettings? companion;
    /// <summary>The personas as last saved (or loaded); the same instance as <see cref="companion"/> when nothing changed.</summary>
    private CompanionSettings? saved;
    /// <summary>The persona shown in the editor; always the one Martlet uses.</summary>
    private Guid? editingId;
    /// <summary>Why the latest change can't be saved (for example an empty name), or why the latest save failed.</summary>
    private string? problem;
    private string? saveError;
    private bool working;
    private bool saving;
    private bool closed;
    private bool closeConfirmed;
    private bool finishing;
    private bool rendering;

    /// <summary>The least persona text room worth importing a card into.</summary>
    private const int MinimumCardCharacters = 500;

    internal CompanionWindow(ICompanionSettingsService service, SetupOperationRunner operations,
        Func<string?>? chooseImport = null, Func<string?>? chooseExport = null, Func<string?>? chooseCard = null,
        bool importCardOnOpen = false, LorebookStore? lorebooks = null)
    {
        this.service = service;
        this.operations = operations;
        this.chooseImport = chooseImport ?? SelectImport;
        this.chooseExport = chooseExport ?? SelectExport;
        this.chooseCard = chooseCard ?? SelectCard;
        this.importCardOnOpen = importCardOnOpen;
        this.lorebooks = lorebooks;
        autoSave = new AutoSave(SaveChangesAsync);
        autoSave.Settled += () => { if (!closed) RenderState(); };
        InitializeComponent();
        // The persona list shows each persona's name; give each entry that name for screen readers and UI Automation too.
        var entry = new Style(typeof(ComboBoxItem), TryFindResource(typeof(ComboBoxItem)) as Style);
        entry.Setters.Add(new Setter(System.Windows.Automation.AutomationProperties.NameProperty,
            new System.Windows.Data.Binding(nameof(PersonaProfile.Name))));
        PersonaChoice.ItemContainerStyle = entry;
        RenderState();
    }

    /// <summary>Whether every change is saved (nothing waiting, nothing failed).</summary>
    internal bool AllSaved => !autoSave.Pending && problem is null && saveError is null &&
        ReferenceEquals(companion, saved) && pendingLore.Count == 0;

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await LoadAsync();
        if (importCardOnOpen && !closed && companion is not null)
            await ImportCardAsync(update: false);
    }

    private async Task LoadAsync()
    {
        var backend = service;
        var result = await ObserveAsync(async token => new(SetupWorkOutcome.Completed,
            Loaded: await backend.LoadAsync(token).ConfigureAwait(false)));
        if (closed) return;
        if (result?.Loaded is not { } loaded)
        {
            ResultText.Text = "Your personas couldn't be read. Close Personality and open it again.";
            RenderState();
            return;
        }
        pendingLore.Clear();
        companion = saved = loaded.Error is null ? CompanionSettings.Begin(loaded.Settings).Companion : null;
        ResultText.Text = loaded.Error?.Summary ?? "";
        RenderPersonas();
    }

    /// <summary>Runs a short blocking action (loading, reading a file) on the shared settings worker, waiting a moment if another
    /// Martlet action (such as a save) holds it. The editor is disabled meanwhile.</summary>
    private async Task<SetupWorkResult?> ObserveAsync(Func<CancellationToken, Task<SetupWorkResult>> work)
    {
        if (closed || working) return null;
        working = true;
        RenderState();
        try
        {
            SetupOperation? operation = null;
            for (var waited = 0; operation is null && !closed; waited++)
            {
                operation = operations.TryStart(work);
                if (operation is not null) break;
                if (waited == 100)
                {
                    ResultText.Text = "Another Martlet action is still finishing. Try again in a moment.";
                    return null;
                }
                await Task.Delay(100);
            }
            if (operation is null) return null;
            var result = await operation.Completion;
            if (closed) return null;
            if (result.Outcome == SetupWorkOutcome.Completed) return result;
            ResultText.Text = result.Outcome == SetupWorkOutcome.Canceled ? "Canceled." : "That didn't work. Try again.";
            return null;
        }
        finally
        {
            working = false;
            if (!closed) RenderState();
        }
    }

    private void RenderState()
    {
        if (closed) return;
        EditorPanel.IsEnabled = !working && companion is not null;
        var unsaved = problem ?? saveError;
        SaveStateText.Text = companion is null ? working ? "Loading your personas..." : "Your personas couldn't be loaded."
            : problem is not null ? "Not saved yet: " + problem
            : saveError is not null ? "Not saved: " + saveError + " Martlet tries again with your next change."
            : saving || autoSave.Pending ? "Saving..."
            : "All changes saved.";
        SaveStateText.SetResourceReference(TextBlock.ForegroundProperty, unsaved is null ? "MutedBrush" : "WarningBrush");
    }

    private void RenderPersonas(Guid? selected = null)
    {
        rendering = true;
        try
        {
            var id = selected ?? companion?.ActivePersonaId;
            PersonaChoice.ItemsSource = companion?.Personas;
            PersonaChoice.SelectedItem = companion?.Personas.SingleOrDefault(persona => persona.Id == id);
            RenderEditor();
        }
        finally { rendering = false; }
        RenderState();
    }

    /// <summary>Refreshes the persona list's names after a save without touching the editor, so typing continues undisturbed.</summary>
    private void RenderChoices()
    {
        if (companion is null || PersonaChoice.ItemsSource is IReadOnlyList<PersonaProfile> shown &&
            shown.Select(persona => (persona.Id, persona.Name)).SequenceEqual(companion.Personas.Select(persona => (persona.Id, persona.Name))))
            return;
        rendering = true;
        try
        {
            PersonaChoice.ItemsSource = companion.Personas;
            PersonaChoice.SelectedItem = companion.Personas.SingleOrDefault(persona => persona.Id == editingId);
        }
        finally { rendering = false; }
    }

    private void RenderEditor()
    {
        var persona = PersonaChoice.SelectedItem as PersonaProfile;
        rendering = true;
        editingId = persona?.Id;
        PersonaName.Text = persona?.Name ?? "";
        PersonaText.Text = persona?.Text ?? "";
        var breaks = persona?.SpokenBreaks ?? SpeechBreaks.Default;
        BreakPeriods.IsChecked = breaks.Periods;
        BreakQuestions.IsChecked = breaks.QuestionMarks;
        BreakExclamations.IsChecked = breaks.ExclamationMarks;
        ShortEnding.SelectedIndex = breaks.ShortEndingWords;
        rendering = false;
    }

    /// <summary>Choosing a persona makes it the one Martlet uses, saved at once. The persona being left keeps its edits; one
    /// that can't be saved yet (an empty or duplicate name) must be fixed first.</summary>
    private void Persona_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (rendering || companion is null || PersonaChoice.SelectedItem is not PersonaProfile persona) return;
        if (!ApplyEditor())
        {
            rendering = true;
            try { PersonaChoice.SelectedItem = e.RemovedItems.OfType<PersonaProfile>().FirstOrDefault(); }
            finally { rendering = false; }
            ResultText.Text = "Fix this persona before switching: " + problem;
            RenderState();
            return;
        }
        companion = companion.Select(persona.Id);
        RenderPersonas(persona.Id);
        ResultText.Text = $"Martlet now uses {persona.Name}.";
        SaveNow();
    }

    private void Editor_Changed(object sender, TextChangedEventArgs e)
    {
        if (!rendering) Changed();
    }

    private void Break_Changed(object sender, RoutedEventArgs e)
    {
        if (!rendering && SaveStateText is not null) Changed();
    }

    private void ShortEnding_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!rendering && SaveStateText is not null) Changed();
    }

    /// <summary>The stops the editor shows for the persona's voice.</summary>
    private SpeechBreaks Breaks() => new()
    {
        Periods = BreakPeriods.IsChecked == true,
        QuestionMarks = BreakQuestions.IsChecked == true,
        ExclamationMarks = BreakExclamations.IsChecked == true,
        ShortEndingWords = Math.Clamp(ShortEnding.SelectedIndex, 0, SpeechBreaks.MaximumShortEndingWords)
    };

    private void Changed()
    {
        problem = null;
        autoSave.Changed();
        RenderState();
    }

    private void SaveNow()
    {
        autoSave.SaveNowAsync().Forget();
        RenderState();
    }

    /// <summary>Takes the editor's fields into the personas (a name's surrounding spaces are dropped), or records why they can't
    /// be saved in <see cref="problem"/>.</summary>
    private bool ApplyEditor()
    {
        if (companion is null) return false;
        if (editingId is not { } id) return true;
        try
        {
            companion = companion.Update(id, PersonaName.Text.Trim(), PersonaText.Text, Breaks());
            problem = null;
            return true;
        }
        catch (ContractException error)
        {
            problem = error.Message;
            return false;
        }
    }

    /// <summary>Applies the editor before a persona is added, duplicated, deleted or replaced; says why when it can't.</summary>
    private bool ReadyForChange()
    {
        if (companion is null || working) return false;
        if (ApplyEditor()) return true;
        ResultText.Text = "Fix this persona first: " + problem;
        RenderState();
        return false;
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        if (!ReadyForChange()) return;
        try
        {
            var updated = companion!.Add(UniqueName(companion, "New persona"));
            companion = updated;
            RenderPersonas(updated.ActivePersonaId);
            ResultText.Text = "New persona added, and Martlet uses it now. Give it a name and describe it.";
            SaveNow();
        }
        catch (ContractException error) { ResultText.Text = error.Message; }
    }

    private void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        if (!ReadyForChange() || editingId is not { } id) return;
        var selected = companion!.Personas.Single(persona => persona.Id == id);
        try
        {
            var updated = companion.Add(UniqueName(companion, selected.Name + " copy"), selected);
            companion = updated;
            RenderPersonas(updated.ActivePersonaId);
            ResultText.Text = $"Duplicated {selected.Name}. Martlet uses the copy now.";
            SaveNow();
        }
        catch (ContractException error) { ResultText.Text = error.Message; }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (companion is null || working || editingId is not { } id) return;
        var selected = companion.Personas.Single(persona => persona.Id == id);
        if (companion.Personas.Count > 1 && !ConfirmationDialog.Confirm(this,
                $"Delete the persona \"{selected.Name}\"? This can't be undone.", "Delete persona"))
            return;
        try
        {
            var updated = companion.Remove(id);
            pendingLore.Remove(id);
            problem = null;
            companion = updated;
            RenderPersonas(updated.ActivePersonaId);
            ResultText.Text = $"Deleted {selected.Name}. Martlet uses {updated.ActivePersona.Name} now.";
            SaveNow();
        }
        catch (ContractException error) { ResultText.Text = error.Message; }
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (companion is null || working) return;
        var path = chooseImport();
        if (path is null) { ResultText.Text = "Import canceled. Nothing changed."; return; }
        var backend = service;
        var result = await ObserveAsync(async token => new(SetupWorkOutcome.Completed,
            PersonaFile: await backend.ImportTextAsync(path, token).ConfigureAwait(false)));
        if (result?.PersonaFile is not { } imported) return;
        ResultText.Text = imported.Summary;
        if (imported.Succeeded) PersonaText.Text = imported.Text!;
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (!ReadyForChange() || editingId is not { } id) return;
        var path = chooseExport();
        if (path is null) { ResultText.Text = "Export canceled. No file was created."; return; }
        var text = companion!.Personas.Single(persona => persona.Id == id).Text;
        var backend = service;
        var result = await ObserveAsync(async token => new(SetupWorkOutcome.Completed,
            PersonaFile: await backend.ExportTextAsync(path, text, token).ConfigureAwait(false)));
        if (result?.PersonaFile is { } exported) ResultText.Text = exported.Summary;
    }

    private async void CardNew_Click(object sender, RoutedEventArgs e) => await ImportCardAsync(update: false);
    private async void CardUpdate_Click(object sender, RoutedEventArgs e) => await ImportCardAsync(update: true);

    /// <summary>Reads a character card and either adds it as a new persona (which Martlet then uses) or puts it into the shown
    /// persona, which keeps its identity and where its voice pauses. Either is saved at once.</summary>
    private async Task ImportCardAsync(bool update, string? path = null)
    {
        if (!ReadyForChange()) return;
        path ??= chooseCard();
        if (path is null) { ResultText.Text = "Character card import canceled. Nothing changed."; return; }
        var backend = service;
        var result = await ObserveAsync(async token => new(SetupWorkOutcome.Completed,
            CardFile: await backend.ImportCardAsync(path, token).ConfigureAwait(false)));
        if (result?.CardFile is not { } file) return;
        ResultText.Text = !file.Succeeded ? file.Summary
            : update ? UpdateFromCard(file.Card!, path)
            : AddFromCard(file.Card!, path);
    }

    private string AddFromCard(CharacterCard card, string path)
    {
        if (companion is null) return "Close Personality and open it again before importing a character card.";
        if (companion.Personas.Count >= CompanionSettings.MaximumPersonas)
            return $"At most {CompanionSettings.MaximumPersonas} personas are supported. Delete one, or update an existing persona.";
        var (characters, bytes) = CardRoom(companion, except: null);
        if (characters < MinimumCardCharacters) return NoRoom;
        var persona = card.ToPersona(Path.GetFileNameWithoutExtension(path), characters, bytes);
        try
        {
            var name = UniqueName(companion, persona.Name);
            var added = companion.Add(name);
            var updated = added.Update(added.ActivePersonaId, name, persona.Text);
            companion = updated;
            var lore = KeepCardLore(card, updated.ActivePersonaId);
            RenderPersonas(updated.ActivePersonaId);
            SaveNow();
            return $"Added \"{name}\" from the character card, and Martlet uses it now." + Fitting(card, persona, lore);
        }
        catch (ContractException error) { return error.Message; }
    }

    private string UpdateFromCard(CharacterCard card, string path)
    {
        if (companion is null || editingId is not { } id)
            return "Choose the persona to update, then import the character card again.";
        var selected = companion.Personas.Single(persona => persona.Id == id);
        var (characters, bytes) = CardRoom(companion, except: id);
        if (characters < MinimumCardCharacters) return NoRoom;
        var persona = card.ToPersona(Path.GetFileNameWithoutExtension(path), characters, bytes);
        try
        {
            PersonaName.Text = UniqueName(companion, persona.Name, except: id);
            PersonaText.Text = persona.Text;
            var lore = KeepCardLore(card, id);
            SaveNow();
            return $"Loaded the card into \"{selected.Name}\"." + Fitting(card, persona, lore);
        }
        catch (ContractException error) { return error.Message; }
    }

    private const string NoRoom =
        "There isn't enough room for this card. Shorten or delete another persona, then try again.";

    /// <summary>Room for a card's persona text: the per-persona limit, or less when the other personas' texts leave less of
    /// the combined settings limit.</summary>
    private static (int Characters, int Bytes) CardRoom(CompanionSettings companion, Guid? except)
    {
        var others = companion.Personas.Where(persona => persona.Id != except).ToArray();
        return (Math.Min(PersonaProfile.MaximumTextCharacters,
                CompanionSettings.MaximumAggregateTextCharacters - others.Sum(persona => persona.Text.Length)),
            Math.Min(PersonaProfile.MaximumTextUtf8Bytes,
                CompanionSettings.MaximumAggregateTextUtf8Bytes - others.Sum(persona => Encoding.UTF8.GetByteCount(persona.Text))));
    }

    private static string Fitting(CharacterCard card, CharacterCardPersona persona, Lorebook? lore)
    {
        var notes = new List<string>();
        if (persona.Shortened.Count > 0) notes.Add("Some card text was shortened to fit: " + string.Join(", ", persona.Shortened) + ".");
        if (persona.LeftOut.Count > 0) notes.Add("Some card text was left out: " + string.Join(", ", persona.LeftOut) + ".");
        if (lore is not null)
            notes.Add($"{lore.Entries.Count} lorebook " + (lore.Entries.Count == 1 ? "entry is" : "entries are") +
                " saved for this persona.");
        else if (card.KeywordLoreEntries > 0)
            notes.Add($"{card.KeywordLoreEntries} lorebook " +
                (card.KeywordLoreEntries == 1 ? "entry wasn't" : "entries weren't") + " imported here.");
        return notes.Count == 0 ? "" : " " + string.Join(" ", notes);
    }

    /// <summary>Keeps the card's keyword-triggered lorebook entries, attached to <paramref name="persona"/>, for the next save.
    /// Always-on entries are already in the persona text.</summary>
    private Lorebook? KeepCardLore(CharacterCard card, Guid persona)
    {
        pendingLore.Remove(persona);
        if (lorebooks is null || card.Lorebook is not { } import) return null;
        var entries = import.Book.Entries.Where(entry => !entry.Constant).ToArray();
        if (entries.Length == 0) return null;
        var book = import.Book with
        {
            Id = Guid.NewGuid(), Activation = LorebookActivation.SelectedPersonas, PersonaIds = [persona], Entries = entries
        };
        pendingLore[persona] = book;
        return book;
    }

    private void Window_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Effects = EditorPanel.IsEnabled ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_PreviewDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Handled = true;
        if (!EditorPanel.IsEnabled) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: 1 } files)
        {
            ResultText.Text = "Drop one character card at a time (PNG, JSON or CHARX).";
            return;
        }
        await ImportCardAsync(update: false, files[0]);
    }

    /// <summary>The auto-save: writes the personas (and any card lorebooks) when they differ from what is saved. Returns false
    /// to be tried again shortly while another Martlet action holds the settings.</summary>
    private async Task<bool> SaveChangesAsync()
    {
        if (companion is null) return true;
        if (working) return false;
        if (!ApplyEditor())
        {
            RenderState();
            return true;
        }
        var lore = pendingLore.Values.ToArray();
        if (ReferenceEquals(companion, saved) && lore.Length == 0)
        {
            saveError = null;
            RenderState();
            return true;
        }
        var snapshot = companion;
        var backend = service;
        var store = lorebooks;
        LorebookSaveResult? loreSaved = null;
        var operation = operations.TryStart(async token =>
        {
            var result = await backend.SaveCompanionAsync(snapshot, token).ConfigureAwait(false);
            if (result.Save.Saved && lore.Length > 0 && store is not null)
            {
                try { loreSaved = await store.UpdateAsync(library => AttachCardLore(library, lore, result.Settings), token).ConfigureAwait(false); }
                catch (ContractException error) { loreSaved = new(false, LorebookLibrary.Create(), null, error.Message); }
            }
            return new(SetupWorkOutcome.Completed, Saved: result);
        });
        if (operation is null) return false;
        saving = true;
        RenderState();
        try
        {
            var outcome = await operation.Completion;
            if (outcome.Outcome != SetupWorkOutcome.Completed || outcome.Saved is not { } result)
            {
                saveError = "Martlet couldn't write your settings.";
                return true;
            }
            if (!result.Save.Saved)
            {
                saveError = result.Save.Error?.Summary ?? "Martlet couldn't write your settings.";
                return true;
            }
            saveError = null;
            saved = snapshot;
            if (result.Save.MigratedFromSchemaVersion is not null)
                ResultText.Text = "Your older settings were updated for this version of Martlet.";
            if (loreSaved is not null)
            {
                foreach (var book in lore)
                    if (pendingLore.FirstOrDefault(pair => ReferenceEquals(pair.Value, book)) is { Value: not null } pair)
                        pendingLore.Remove(pair.Key);
                ResultText.Text = loreSaved.Saved
                    ? string.Join(" ", lore.Select(book => $"Lorebook \"{book.Name}\" saved for this persona."))
                    : "The card's lorebook wasn't saved: " + loreSaved.Error;
            }
            if (!closed) RenderChoices();
            return true;
        }
        finally
        {
            saving = false;
            RenderState();
        }
    }

    /// <summary>Adds each card's keyword lorebook for its persona, replacing an earlier copy from the same card (same name, same
    /// single persona). Lorebooks for personas that were deleted before saving are dropped.</summary>
    internal static LorebookLibrary AttachCardLore(LorebookLibrary library, IEnumerable<Lorebook> books, AppSettings saved)
    {
        var personas = saved.Companion?.Personas.Select(persona => persona.Id).ToHashSet() ?? [];
        foreach (var book in books)
        {
            if (book.PersonaIds is not [var persona] || !personas.Contains(persona)) continue;
            var existing = library.Books.FirstOrDefault(item => item.Activation == LorebookActivation.SelectedPersonas &&
                item.PersonaIds is [var only] && only == persona && string.Equals(item.Name, book.Name, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                library = library with
                {
                    Books = library.Books.Select(item => item.Id == existing.Id
                        ? existing with { Entries = book.Entries, Description = book.Description } : item).ToArray()
                };
                continue;
            }
            ContractRules.Require(library.Books.Count < LorebookLibrary.MaximumBooks,
                $"At most {LorebookLibrary.MaximumBooks} lorebooks are supported. Delete one in Lorebooks.");
            library = library with { Books = library.Books.Append(book with { Name = library.UniqueName(book.Name) }).ToArray() };
        }
        library.Validate();
        return library;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Closing saves what is still waiting first. Only a change that can't be saved asks before it is dropped.</summary>
    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (closeConfirmed || companion is null || !working && ApplyEditor() && AllSaved)
        {
            autoSave.Cancel();
            closed = true;
            return;
        }
        e.Cancel = true;
        if (finishing) return;
        finishing = true;
        try
        {
            for (var tries = 0; tries < 50 && (working || autoSave.Pending || !ReferenceEquals(companion, saved) || pendingLore.Count > 0); tries++)
            {
                if (problem is not null || saveError is not null) break;
                if (working) await Task.Delay(100);
                else
                {
                    await autoSave.SaveNowAsync();
                    if (autoSave.Pending) await Task.Delay(100);
                }
            }
            var unsaved = problem ?? saveError ?? (AllSaved ? null : "Another Martlet action is still finishing.");
            if (unsaved is not null && !ConfirmationDialog.Confirm(this,
                    $"Your latest change isn't saved: {unsaved}\n\nClose anyway and lose it?", "Unsaved change"))
                return;
            closeConfirmed = true;
            await Dispatcher.InvokeAsync(Close);
        }
        finally { finishing = false; }
    }

    private static string UniqueName(CompanionSettings settings, string basis, Guid? except = null)
    {
        for (var index = 1; index <= CompanionSettings.MaximumPersonas; index++)
        {
            var suffix = index == 1 ? "" : $" {index}";
            var prefix = basis[..Math.Min(basis.Length, PersonaProfile.MaximumNameCharacters - suffix.Length)].TrimEnd();
            var candidate = prefix + suffix;
            if (!settings.Personas.Any(persona => persona.Id != except &&
                    string.Equals(persona.Name, candidate, StringComparison.OrdinalIgnoreCase)))
                return candidate;
        }
        throw new ContractException(ErrorCode.InvalidContract, "No unique persona name is available.");
    }

    private static string? SelectImport()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import persona text",
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private static string? SelectCard()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import a character card",
            Filter = "Character cards (*.png;*.json;*.charx)|*.png;*.json;*.charx|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private static string? SelectExport()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export persona text",
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
            DefaultExt = ".txt",
            AddExtension = true,
            OverwritePrompt = false
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
