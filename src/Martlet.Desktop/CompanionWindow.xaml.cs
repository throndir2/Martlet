using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Martlet.Core.Contracts;
using Martlet.Core.Lorebooks;
using Martlet.Core.Settings;
using Microsoft.Win32;

namespace Martlet.Desktop;

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
    private readonly DispatcherTimer operationTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private AppSettings? draft;
    private string? revision;
    private bool busy;
    private bool closed;
    private bool rendering;
    private bool editorDirty;

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
        InitializeComponent();
        operationTimer.Tick += (_, _) => RenderOperationState();
        operationTimer.Start();
        RenderOperationState();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await LoadAsync();
        if (importCardOnOpen && !closed && draft?.Companion is not null)
            await ImportCardAsync(update: false);
    }

    private async Task LoadAsync()
    {
        if (!MayStart()) return;
        var backend = service;
        await ObserveAsync(operations.TryStart(async token => new(SetupWorkOutcome.Completed,
            Loaded: await backend.LoadAsync(token).ConfigureAwait(false))), result =>
        {
            var loaded = result.Loaded!;
            revision = loaded.Revision;
            pendingLore.Clear();
            draft = loaded.Error is null ? CompanionSettings.Begin(loaded.Settings) : null;
            ResultText.Text = loaded.Error?.Summary ?? loaded.Settings?.SchemaVersion switch
            {
                1 or 2 => $"Settings version {loaded.Settings.SchemaVersion} loaded unchanged. Save will migrate it with an atomic original-file snapshot.",
                _ => "Companion settings loaded. No model, provider or microphone was accessed."
            };
            RenderPersonas();
        });
    }

    private bool MayStart()
    {
        if (closed) return false;
        if (!busy && !operations.IsRunning) return true;
        ResultText.Text = "Another setup, audio, conversation or companion action still owns the app worker. Wait for actual cleanup before retrying.";
        return false;
    }

    private async Task<bool> ObserveAsync(SetupOperation? operation, Action<SetupWorkResult> apply)
    {
        if (operation is null) { MayStart(); return false; }
        busy = true;
        RenderOperationState();
        try
        {
            var result = await operation.Completion;
            if (closed) return false;
            if (result.Outcome != SetupWorkOutcome.Completed)
            {
                ResultText.Text = result.Outcome == SetupWorkOutcome.Canceled
                    ? "Companion action canceled. Reload before editing; cancellation is not proof of rollback."
                    : "Companion action failed. Reload and review saved settings; no success is assumed.";
                draft = null;
                return false;
            }
            apply(result);
            return true;
        }
        finally
        {
            busy = false;
            if (!closed) RenderOperationState();
        }
    }

    private void RenderOperationState()
    {
        if (closed) return;
        var active = busy || operations.IsRunning;
        EditorPanel.IsEnabled = !active && draft?.Companion is not null;
        ReloadButton.IsEnabled = !active;
        ActivityText.Text = active
            ? "Companion settings worker active. No overlapping app effect can start."
            : "Idle. Changes are local drafts until Apply and Save; fresh explicit turns use the saved active persona.";
    }

    private void RenderPersonas(Guid? selected = null)
    {
        rendering = true;
        var companion = draft?.Companion;
        var id = selected ?? companion?.ActivePersonaId;
        PersonaChoice.ItemsSource = companion?.Personas;
        PersonaChoice.SelectedItem = companion?.Personas.SingleOrDefault(persona => persona.Id == id);
        RenderEditor();
        rendering = false;
        RenderOperationState();
    }

    private void RenderEditor()
    {
        var persona = PersonaChoice.SelectedItem as PersonaProfile;
        rendering = true;
        PersonaName.Text = persona?.Name ?? "";
        PersonaText.Text = persona?.Text ?? "";
        HelpfulWeight.Value = persona?.Styles.Helpful ?? 0;
        SarcasticWeight.Value = persona?.Styles.Sarcastic ?? 0;
        SillyWeight.Value = persona?.Styles.Silly ?? 0;
        DistractedWeight.Value = persona?.Styles.Distracted ?? 0;
        TeasingWeight.Value = persona?.Styles.PlayfulTeasing ?? 0;
        editorDirty = false;
        RenderWeightValues();
        rendering = false;
    }

    private void Persona_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (rendering || draft?.Companion is not { } companion ||
            PersonaChoice.SelectedItem is not PersonaProfile persona) return;
        if (editorDirty)
            ResultText.Text = "Unapplied persona edits were discarded when changing profiles. Apply edits before switching.";
        draft = draft with { Companion = companion.Select(persona.Id) };
        RenderEditor();
    }

    private void Editor_Changed(object sender, TextChangedEventArgs e)
    {
        if (!rendering) editorDirty = true;
    }

    private void Weight_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (HelpfulValue is null) return;
        RenderWeightValues();
        if (!rendering) editorDirty = true;
    }

    private void RenderWeightValues()
    {
        HelpfulValue.Text = ((int)HelpfulWeight.Value).ToString();
        SarcasticValue.Text = ((int)SarcasticWeight.Value).ToString();
        SillyValue.Text = ((int)SillyWeight.Value).ToString();
        DistractedValue.Text = ((int)DistractedWeight.Value).ToString();
        TeasingValue.Text = ((int)TeasingWeight.Value).ToString();
    }

    private ResponseStyleWeights Styles() => new()
    {
        Helpful = (int)HelpfulWeight.Value,
        Sarcastic = (int)SarcasticWeight.Value,
        Silly = (int)SillyWeight.Value,
        Distracted = (int)DistractedWeight.Value,
        PlayfulTeasing = (int)TeasingWeight.Value
    };

    private bool ApplyDraft()
    {
        if (draft?.Companion is not { } companion || PersonaChoice.SelectedItem is not PersonaProfile selected)
            return false;
        try
        {
            draft = draft with { Companion = companion.Update(selected.Id, PersonaName.Text, PersonaText.Text, Styles()) };
            editorDirty = false;
            ResultText.Text = "Persona edits applied to the local draft. Save to persist them; no runtime prompt or permission changed.";
            RenderPersonas(selected.Id);
            return true;
        }
        catch (ContractException error)
        {
            ResultText.Text = error.Message;
            return false;
        }
    }

    private void Apply_Click(object sender, RoutedEventArgs e) => ApplyDraft();

    private void New_Click(object sender, RoutedEventArgs e)
    {
        if (draft?.Companion is not { } companion) return;
        if (editorDirty && !ApplyDraft()) return;
        companion = draft!.Companion!;
        try
        {
            var updated = companion.Add(UniqueName(companion, "New persona"));
            draft = draft with { Companion = updated };
            ResultText.Text = "New persona added to the local draft. Apply edits and Save to persist it.";
            RenderPersonas(updated.ActivePersonaId);
        }
        catch (ContractException error) { ResultText.Text = error.Message; }
    }

    private void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        if (draft?.Companion is not { } companion || PersonaChoice.SelectedItem is not PersonaProfile selected) return;
        if (editorDirty && !ApplyDraft()) return;
        companion = draft!.Companion!;
        selected = companion.Personas.Single(persona => persona.Id == selected.Id);
        try
        {
            var updated = companion.Add(UniqueName(companion, selected.Name + " copy"), selected);
            draft = draft with { Companion = updated };
            ResultText.Text = "Persona duplicated in the local draft. Save to persist it.";
            RenderPersonas(updated.ActivePersonaId);
        }
        catch (ContractException error) { ResultText.Text = error.Message; }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (draft?.Companion is not { } companion || PersonaChoice.SelectedItem is not PersonaProfile selected) return;
        try
        {
            var updated = companion.Remove(selected.Id);
            pendingLore.Remove(selected.Id);
            draft = draft with { Companion = updated };
            ResultText.Text = "Persona removed from the local draft. Save to persist the deletion.";
            RenderPersonas(updated.ActivePersonaId);
        }
        catch (ContractException error) { ResultText.Text = error.Message; }
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (!MayStart()) return;
        var path = chooseImport();
        if (path is null) { ResultText.Text = "Import canceled. The draft is unchanged."; return; }
        var backend = service;
        await ObserveAsync(operations.TryStart(async token => new(SetupWorkOutcome.Completed,
            PersonaFile: await backend.ImportTextAsync(path, token).ConfigureAwait(false))), result =>
        {
            var imported = result.PersonaFile!;
            ResultText.Text = imported.Summary;
            if (imported.Succeeded)
            {
                PersonaText.Text = imported.Text!;
                editorDirty = true;
            }
        });
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (!MayStart() || !ApplyDraft()) return;
        var path = chooseExport();
        if (path is null) { ResultText.Text = "Export canceled. No file was created."; return; }
        var text = ((PersonaProfile)PersonaChoice.SelectedItem).Text;
        var backend = service;
        await ObserveAsync(operations.TryStart(async token => new(SetupWorkOutcome.Completed,
            PersonaFile: await backend.ExportTextAsync(path, text, token).ConfigureAwait(false))),
            result => ResultText.Text = result.PersonaFile!.Summary);
    }

    private async void CardNew_Click(object sender, RoutedEventArgs e) => await ImportCardAsync(update: false);
    private async void CardUpdate_Click(object sender, RoutedEventArgs e) => await ImportCardAsync(update: true);

    /// <summary>Reads a character card and either adds it as a new persona (selected in the draft) or loads it into the
    /// editor for the selected persona, which keeps its identity and response-style weights. Both stay drafts until Save.</summary>
    private async Task ImportCardAsync(bool update, string? path = null)
    {
        if (!MayStart() || draft?.Companion is null) return;
        if (update && PersonaChoice.SelectedItem is not PersonaProfile) return;
        if (!update && editorDirty && !ApplyDraft()) return;
        path ??= chooseCard();
        if (path is null) { ResultText.Text = "Character card import canceled. The draft is unchanged."; return; }
        var backend = service;
        await ObserveAsync(operations.TryStart(async token => new(SetupWorkOutcome.Completed,
            CardFile: await backend.ImportCardAsync(path, token).ConfigureAwait(false))), result =>
        {
            var file = result.CardFile!;
            ResultText.Text = !file.Succeeded ? file.Summary
                : update ? UpdateFromCard(file.Card!, path)
                : AddFromCard(file.Card!, path);
        });
    }

    private string AddFromCard(CharacterCard card, string path)
    {
        if (draft?.Companion is not { } companion) return "Reload before importing a character card.";
        if (companion.Personas.Count >= CompanionSettings.MaximumPersonas)
            return $"At most {CompanionSettings.MaximumPersonas} personas are supported. Delete one, or update an existing persona from the card instead.";
        var (characters, bytes) = CardRoom(companion, except: null);
        if (characters < MinimumCardCharacters) return NoRoom;
        var persona = card.ToPersona(Path.GetFileNameWithoutExtension(path), characters, bytes);
        try
        {
            var name = UniqueName(companion, persona.Name);
            var added = companion.Add(name);
            var updated = added.Update(added.ActivePersonaId, name, persona.Text, ResponseStyleWeights.HelpfulOnly());
            draft = draft with { Companion = updated };
            RenderPersonas(updated.ActivePersonaId);
            return $"Added {Describe(card)} as the new persona \"{name}\" in the local draft. Review it, then Save to keep it." +
                Fitting(card, persona, KeepCardLore(card, updated.ActivePersonaId));
        }
        catch (ContractException error) { return error.Message; }
    }

    private string UpdateFromCard(CharacterCard card, string path)
    {
        if (draft?.Companion is not { } companion || PersonaChoice.SelectedItem is not PersonaProfile selected)
            return "Select the persona to update, then import the character card again.";
        var (characters, bytes) = CardRoom(companion, except: selected.Id);
        if (characters < MinimumCardCharacters) return NoRoom;
        var persona = card.ToPersona(Path.GetFileNameWithoutExtension(path), characters, bytes);
        try
        {
            PersonaName.Text = UniqueName(companion, persona.Name, except: selected.Id);
            PersonaText.Text = persona.Text;
            editorDirty = true;
            return $"Loaded {Describe(card)} into the editor for \"{selected.Name}\"; its response-style weights are unchanged. " +
                "Review it, then Apply and Save." + Fitting(card, persona, KeepCardLore(card, selected.Id));
        }
        catch (ContractException error) { return error.Message; }
    }

    private const string NoRoom =
        "All persona texts together are near the 16,384-character limit. Shorten or delete another persona, then import the card again.";

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

    private static string Describe(CharacterCard card) =>
        $"{card.FormatName} \"{card.DisplayName}\"" + (card.Creator.Length > 0 ? $" by {card.Creator}" : "");

    private static string Fitting(CharacterCard card, CharacterCardPersona persona, Lorebook? lore)
    {
        var notes = new List<string>();
        if (persona.Shortened.Count > 0) notes.Add("Shortened to fit: " + string.Join(", ", persona.Shortened) + ".");
        if (persona.LeftOut.Count > 0) notes.Add("Left out to fit: " + string.Join(", ", persona.LeftOut) + ".");
        if (lore is not null)
            notes.Add($"Its {lore.Entries.Count} keyword-triggered lorebook " + (lore.Entries.Count == 1 ? "entry goes" : "entries go") +
                $" to the lorebook \"{lore.Name}\", used only with this persona, when you Save (always-on entries are in the persona text).");
        else if (card.KeywordLoreEntries > 0)
            notes.Add($"Not imported: {card.KeywordLoreEntries} keyword-triggered lorebook " +
                (card.KeywordLoreEntries == 1 ? "entry" : "entries") + " (lorebooks are unavailable here; always-on entries are included).");
        return notes.Count == 0 ? "" : " " + string.Join(" ", notes);
    }

    /// <summary>Keeps the card's keyword-triggered lorebook entries, attached to <paramref name="persona"/>, until Save. Always-on
    /// entries are already in the persona text.</summary>
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
        if (!EditorPanel.IsEnabled) { MayStart(); return; }
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: 1 } files)
        {
            ResultText.Text = "Drop one character card at a time (PNG, JSON or CHARX).";
            return;
        }
        await ImportCardAsync(update: false, files[0]);
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!MayStart() || !ApplyDraft()) return;
        var snapshot = draft!;
        var expected = revision;
        var backend = service;
        var store = lorebooks;
        var lore = pendingLore.Values.ToArray();
        LorebookSaveResult? loreSaved = null;
        await ObserveAsync(operations.TryStart(async token =>
        {
            var saved = await backend.SaveAsync(snapshot, expected, token).ConfigureAwait(false);
            if (saved.Save.Saved && lore.Length > 0 && store is not null)
                loreSaved = await store.UpdateAsync(library => AttachCardLore(library, lore, saved.Settings), token).ConfigureAwait(false);
            return new(SetupWorkOutcome.Completed, Saved: saved);
        }), result =>
        {
            var saved = result.Saved!;
            ResultText.Text = saved.Save.Saved
                ? saved.Save.MigratedFromSchemaVersion is { } previous
                    ? $"Companion settings saved. Version {previous} was migrated with an atomic original-file snapshot."
                    : "Companion settings saved. No model, capture or provider action was authorized."
                : saved.Save.Error!.Summary;
            if (saved.Save.Saved)
            {
                if (loreSaved is { Saved: true })
                {
                    ResultText.Text += " " + string.Join(" ", lore.Select(book =>
                        $"The lorebook \"{book.Name}\" ({book.Entries.Count} keyword {(book.Entries.Count == 1 ? "entry" : "entries")}) is saved and used with its persona; change it on Companion > Lorebook."));
                    pendingLore.Clear();
                }
                else if (loreSaved is { } failed)
                    ResultText.Text += " The character card's lorebook was not saved: " + failed.Error + " Save again to retry.";
                draft = saved.Settings;
                revision = saved.Save.Revision;
                RenderPersonas();
            }
        });
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
                $"At most {LorebookLibrary.MaximumBooks} lorebooks are supported. Delete one on Companion > Lorebook.");
            library = library with { Books = library.Books.Append(book with { Name = library.UniqueName(book.Name) }).ToArray() };
        }
        library.Validate();
        return library;
    }

    private async void Reload_Click(object sender, RoutedEventArgs e) => await LoadAsync();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        closed = true;
        operationTimer.Stop();
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
            Filter = "UTF-8 text (*.txt)|*.txt|All files (*.*)|*.*",
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
            Title = "Export persona text to a new file",
            Filter = "UTF-8 text (*.txt)|*.txt|All files (*.*)|*.*",
            DefaultExt = ".txt",
            AddExtension = true,
            OverwritePrompt = false
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
