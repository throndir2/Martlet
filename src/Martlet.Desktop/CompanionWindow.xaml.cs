using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Microsoft.Win32;

namespace Martlet.Desktop;

public partial class CompanionWindow : Window
{
    private readonly ICompanionSettingsService service;
    private readonly SetupOperationRunner operations;
    private readonly Func<string?> chooseImport;
    private readonly Func<string?> chooseExport;
    private readonly DispatcherTimer operationTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private AppSettings? draft;
    private string? revision;
    private bool busy;
    private bool closed;
    private bool rendering;
    private bool editorDirty;

    internal CompanionWindow(ICompanionSettingsService service, SetupOperationRunner operations,
        Func<string?>? chooseImport = null, Func<string?>? chooseExport = null)
    {
        this.service = service;
        this.operations = operations;
        this.chooseImport = chooseImport ?? SelectImport;
        this.chooseExport = chooseExport ?? SelectExport;
        InitializeComponent();
        operationTimer.Tick += (_, _) => RenderOperationState();
        operationTimer.Start();
        RenderOperationState();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await LoadAsync();

    private async Task LoadAsync()
    {
        if (!MayStart()) return;
        var backend = service;
        await ObserveAsync(operations.TryStart(async token => new(SetupWorkOutcome.Completed,
            Loaded: await backend.LoadAsync(token).ConfigureAwait(false))), result =>
        {
            var loaded = result.Loaded!;
            revision = loaded.Revision;
            draft = loaded.Error is null ? CompanionSettings.Begin(loaded.Settings) : null;
            ResultText.Text = loaded.Error?.Summary ?? loaded.Settings?.SchemaVersion switch
            {
                1 or 2 => $"Settings version {loaded.Settings.SchemaVersion} loaded unchanged. Save will migrate it with an atomic original-file snapshot.",
                _ => "Companion settings loaded. No model, provider, microphone or file was accessed."
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
            : "Idle. Changes are local drafts until Apply and Save; runtime persona use is not enabled yet.";
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

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!MayStart() || !ApplyDraft()) return;
        var snapshot = draft!;
        var expected = revision;
        var backend = service;
        await ObserveAsync(operations.TryStart(async token => new(SetupWorkOutcome.Completed,
            Saved: await backend.SaveAsync(snapshot, expected, token).ConfigureAwait(false))), result =>
        {
            var saved = result.Saved!;
            ResultText.Text = saved.Save.Saved
                ? saved.Save.MigratedFromSchemaVersion is { } previous
                    ? $"Companion settings saved. Version {previous} was migrated with an atomic original-file snapshot."
                    : "Companion settings saved. No model, capture or provider action was authorized."
                : saved.Save.Error!.Summary;
            if (saved.Save.Saved)
            {
                draft = saved.Settings;
                revision = saved.Save.Revision;
                RenderPersonas();
            }
        });
    }

    private async void Reload_Click(object sender, RoutedEventArgs e) => await LoadAsync();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        closed = true;
        operationTimer.Stop();
    }

    private static string UniqueName(CompanionSettings settings, string basis)
    {
        for (var index = 1; index <= CompanionSettings.MaximumPersonas; index++)
        {
            var suffix = index == 1 ? "" : $" {index}";
            var prefix = basis[..Math.Min(basis.Length, PersonaProfile.MaximumNameCharacters - suffix.Length)];
            var candidate = prefix + suffix;
            if (!settings.Personas.Any(persona => string.Equals(persona.Name, candidate, StringComparison.OrdinalIgnoreCase)))
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
