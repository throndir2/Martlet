using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Core.Speakers;
using Martlet.Memory;
using Microsoft.Win32;

namespace Martlet.Desktop;

public partial class MemoryWindow : ThemedWindow
{
    private sealed record RetentionOption(string Label, TimeSpan? Duration, bool KeepCurrent = false)
    {
        public override string ToString() => Label;
    }

    private enum PersonKind { All, Everyone, Voice, Forgotten }

    /// <summary>A choice of whose facts: <see cref="VoiceId"/> for one voice (or the forgotten voice a fact keeps).</summary>
    private sealed record PersonOption(string Label, PersonKind Kind, string? VoiceId = null)
    {
        public override string ToString() => Label;
    }

    /// <summary>One fact in the list, with whose it is (null for everyone's).</summary>
    internal sealed record FactItem(MemoryFact Fact, string? Person)
    {
        /// <summary>The line under the fact: whose it is, where it came from and when it last changed.</summary>
        public string Caption => $"{Person ?? EveryoneLabel} · {MemoryPromptContext.Source(Fact.LastModifiedBy.SourceKind)} · " +
            When(Fact.UpdatedAtUtc) + (Fact.Retention.Kind == MemoryRetentionKind.ExpiresAt ? " · " + RetentionText(Fact.Retention) : "");

        public override string ToString() => Person is null ? Fact.Content : $"{Person} · {Fact.Content}";
    }

    private static readonly RetentionOption[] NewRetentionOptions =
    [
        new("Until I delete it", null),
        new("Delete after 30 days", TimeSpan.FromDays(30)),
        new("Delete after 90 days", TimeSpan.FromDays(90)),
        new("Delete after 1 year", TimeSpan.FromDays(365))
    ];

    private const string EveryoneLabel = "Everyone";

    private readonly DesktopMemoryService service;
    private readonly SetupOperationRunner operations;
    private readonly Func<string?> chooseDirectory;
    private readonly Func<string?> chooseExport;
    private readonly Func<Window, string, string, bool> confirm;
    private readonly Func<VoiceRoster> voices;
    private string? showPerson;
    private VoiceRoster roster = VoiceRoster.Empty;
    private IReadOnlyList<MemoryFact> facts = [];
    private AppSettings? loadedSettings;
    private string? loadedRevision;
    private Guid configurationRevision;
    private SetupOperation? active;
    private MemoryExportPreview? exportPreview;
    private readonly AutoSave autoSave;
    private bool closed;
    private bool rendering;
    /// <summary>The fact (and its revision) the editor holds, or null for a new fact.</summary>
    private (Guid Id, long Revision)? editing;

    /// <param name="voices">The voices Martlet knows (People), whose facts the window can show and choose.</param>
    /// <param name="person">A voice ID whose facts to show first (People's "What Martlet remembers").</param>
    internal MemoryWindow(
        DesktopMemoryService service,
        SetupOperationRunner operations,
        Func<string?>? chooseDirectory = null,
        Func<string?>? chooseExport = null,
        Func<Window, string, string, bool>? confirm = null,
        Func<VoiceRoster>? voices = null,
        string? person = null)
    {
        this.service = service;
        this.operations = operations;
        this.chooseDirectory = chooseDirectory ?? PickDirectory;
        this.chooseExport = chooseExport ?? PickExport;
        this.confirm = confirm ?? Confirm;
        this.voices = voices ?? (() => VoiceRoster.Empty);
        showPerson = person;
        autoSave = new AutoSave(SaveConfigurationAsync);
        InitializeComponent();
        RetentionChoice.ItemsSource = NewRetentionOptions;
        RetentionChoice.SelectedIndex = 0;
        RenderPeople();
        RenderActions();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await LoadAsync();
    private async void Reload_Click(object sender, RoutedEventArgs e) => await LoadAsync();
    private void RetryCleanup_Click(object sender, RoutedEventArgs e) => service.RetryCleanup();

    private async Task LoadAsync()
    {
        SettingsLoadResult? loaded = null;
        await RunAsync(async token => loaded = await service.LoadAsync(token).ConfigureAwait(false),
            "Couldn't load memory settings. No facts were opened.");
        if (closed || loaded is null)
            return;
        if (loaded.State != SettingsLoadState.Loaded || loaded.Error is not null || loaded.Settings is null)
        {
            loadedSettings = null;
            loadedRevision = null;
            ConfigurationStatus.Text = loaded.Error?.Summary ??
                "Finish Setup before configuring memory.";
            RenderActions();
            return;
        }

        loadedSettings = loaded.Settings;
        loadedRevision = loaded.Revision;
        var memory = loaded.Settings.Memory;
        rendering = true;
        if (memory is null)
        {
            AppLocalChoice.IsChecked = true;
            CustomChoice.IsChecked = false;
            CustomDirectory.Text = "";
            EnableChoice.IsChecked = true;
            configurationRevision = Guid.Empty;
        }
        else
        {
            AppLocalChoice.IsChecked = memory.StoragePolicy == MemoryStoragePolicy.AppLocalData;
            CustomChoice.IsChecked = memory.StoragePolicy == MemoryStoragePolicy.CustomLocalDirectory;
            CustomDirectory.Text = memory.CustomDirectory ?? "";
            EnableChoice.IsChecked = memory.Enabled;
            configurationRevision = memory.ConfigurationRevision;
        }
        rendering = false;
        DisposeExportPreview();
        FactsList.ItemsSource = null;
        facts = [];
        ResetEditor();
        RenderPeople();
        ConfigurationStatus.Text = memory is null
            ? "Updating older memory settings..."
            : $"Memory is {(memory.Enabled ? "on" : "off")}.";
        RenderResolvedDirectory();
        RenderActions();
        // Settings from before memory existed have no memory choice yet; record the default (on, in Martlet's folder) as any
        // other saved change would, so facts can be used without a Save step.
        if (memory is null && !closed)
        {
            await autoSave.SaveNowAsync();
            return;
        }
        if (memory is { Enabled: true } && !closed)
            await RefreshFactsAsync();
    }

    /// <summary>Saves the storage and on/off choices as soon as they change (there is no Save button), into the newest saved
    /// settings. Returns false to be tried again shortly while another Martlet action runs.</summary>
    private async Task<bool> SaveConfigurationAsync()
    {
        if (closed || loadedSettings is null || ConfigurationMatchesPersisted()) return true;
        if (operations.IsRunning) return false;
        var policy = CustomChoice.IsChecked == true
            ? MemoryStoragePolicy.CustomLocalDirectory
            : MemoryStoragePolicy.AppLocalData;
        var enabled = EnableChoice.IsChecked == true;
        var customDirectory = CustomDirectory.Text;
        if (policy == MemoryStoragePolicy.CustomLocalDirectory && string.IsNullOrWhiteSpace(customDirectory))
        {
            ConfigurationStatus.Text = "Choose a folder for memory (Browse), or use the Martlet folder.";
            return true;
        }
        MemoryConfigurationSaveResult? result = null;
        ConfigurationStatus.Text = "Saving memory settings...";
        var basis = loadedSettings;
        var basisRevision = loadedRevision;
        await RunAsync(async token =>
        {
            // Save into the newest settings, so a change made elsewhere meanwhile (or synced in) isn't overwritten.
            var fresh = await service.LoadAsync(token).ConfigureAwait(false);
            result = await service.SaveConfigurationAsync(
                fresh.Settings ?? basis,
                fresh.Settings is null ? basisRevision : fresh.Revision,
                enabled,
                policy,
                customDirectory,
                token).ConfigureAwait(false);
        }, "Couldn't save memory settings. Existing settings and facts were preserved.");
        if (closed || result is null)
            return true;
        if (!result.Save.Save.Saved)
        {
            ConfigurationStatus.Text = result.Save.Save.Error?.Summary ??
                "Couldn't save memory settings. Reload and try again.";
            return true;
        }
        loadedSettings = result.Settings;
        loadedRevision = result.Save.Save.Revision;
        configurationRevision = result.Settings.Memory!.ConfigurationRevision;
        DisposeExportPreview();
        FactsList.ItemsSource = null;
        facts = [];
        ConfigurationStatus.Text = result.Settings.Memory.Enabled
            ? "Saved. Memory is on: Martlet will remember and recall lasting facts."
            : "Saved. Memory is off: saved facts stay on this PC; turn memory on to review or delete them.";
        RenderResolvedDirectory();
        RenderActions();
        if (result.Settings.Memory.Enabled && !closed)
            await RefreshFactsAsync();
        return true;
    }

    private async Task RefreshFactsAsync()
    {
        if (!RequireCurrentEnabledConfiguration(FactStatus))
            return;
        MemoryInspection? inspection = null;
        await RunAsync(async token =>
            inspection = await service.InspectAsync(configurationRevision, token).ConfigureAwait(false),
            "Couldn't refresh facts.");
        if (closed || inspection is null)
            return;
        facts = inspection.Facts;
        RenderPeople();
        RenderFacts();
        RenderActions();
    }

    /// <summary>The Show and Belongs to choices: everyone, each voice Martlet knows (yours first, then named ones), and the
    /// forgotten voices facts still belong to.</summary>
    private void RenderPeople()
    {
        roster = voices();
        var live = roster.Live.OrderByDescending(v => v.Owner).ThenByDescending(v => v.Named).ThenBy(v => v.Number)
            .Select(v => new PersonOption(PersonName(v), PersonKind.Voice, v.Id)).ToArray();
        var forgotten = facts.Any(f => f.VoiceId is { } id && roster.Resolve(id) is null);
        // Keep what was shown; a voice merged meanwhile shows the voice it joined.
        var wanted = PersonFilter.SelectedItem as PersonOption;
        var wantedVoice = MemoryPeople.Canonical(wanted?.VoiceId, roster);
        var show = showPerson is { } person ? MemoryPeople.Canonical(person, roster) : null;
        PersonOption[] filters =
        [
            new("All facts", PersonKind.All), new("Everyone's (not tied to a voice)", PersonKind.Everyone), .. live,
            .. forgotten ? new[] { new PersonOption("Forgotten voices", PersonKind.Forgotten) } : Array.Empty<PersonOption>()
        ];
        rendering = true;
        PersonFilter.ItemsSource = filters;
        PersonFilter.SelectedItem = show is not null ? filters.FirstOrDefault(o => o.VoiceId == show) ?? filters[0]
            : filters.FirstOrDefault(o => o.Kind == wanted?.Kind && o.VoiceId == wantedVoice) ?? filters[0];
        rendering = false;
        showPerson = null;
        ResetPersonChoice(null);
    }

    /// <summary>The Belongs to options; a fact whose voice was forgotten keeps that voice as an option.</summary>
    private void ResetPersonChoice(MemoryFact? fact)
    {
        var options = new List<PersonOption> { new(EveryoneLabel, PersonKind.Everyone) };
        options.AddRange(((IEnumerable<PersonOption>?)PersonFilter.ItemsSource ?? []).Where(o => o.Kind == PersonKind.Voice));
        if (fact?.VoiceId is { } id && roster.Resolve(id) is null)
            options.Add(new("A forgotten voice", PersonKind.Forgotten, id));
        PersonChoice.ItemsSource = options;
        var voice = fact is not null ? MemoryPeople.Canonical(fact.VoiceId, roster) is { } canonical && roster.Resolve(canonical) is not null
                ? canonical : fact.VoiceId
            // A new fact belongs to the voice shown, else to yours: you typed it.
            : PersonFilter.SelectedItem is PersonOption { Kind: PersonKind.Voice } shown ? shown.VoiceId
            : roster.Live.FirstOrDefault(v => v.Owner)?.Id;
        PersonChoice.SelectedItem = options.FirstOrDefault(o => o.VoiceId == voice) ?? options[0];
    }

    private void RenderFacts()
    {
        var filter = PersonFilter.SelectedItem as PersonOption;
        var words = (SearchBox?.Text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var shown = facts.Where(fact => filter?.Kind switch
            {
                PersonKind.Everyone => fact.VoiceId is null,
                PersonKind.Voice => MemoryPeople.Canonical(fact.VoiceId, roster) == filter.VoiceId,
                PersonKind.Forgotten => fact.VoiceId is { } id && roster.Resolve(id) is null,
                _ => true
            })
            .Select(fact => new FactItem(fact, PersonName(fact.VoiceId)))
            .Where(item => words.All(word => item.Fact.Content.Contains(word, StringComparison.CurrentCultureIgnoreCase) ||
                (item.Person ?? EveryoneLabel).Contains(word, StringComparison.CurrentCultureIgnoreCase)))
            .OrderByDescending(item => item.Fact.UpdatedAtUtc).ToArray();
        // Keep what was selected (an edited fact stays selected with its new revision).
        var keep = FactsList.SelectedItems.Cast<FactItem>().Select(item => item.Fact.Id).ToHashSet();
        rendering = true;
        FactsList.ItemsSource = shown;
        foreach (var item in shown.Where(item => keep.Contains(item.Fact.Id)))
            FactsList.SelectedItems.Add(item);
        rendering = false;
        var all = filter?.Kind is null or PersonKind.All && words.Length == 0;
        FactStatus.Text = Summary(shown.Length, all);
        DeleteShownButton.Visibility = all ? Visibility.Collapsed : Visibility.Visible;
        DeleteShownButton.Content = (words.Length > 0 ? shown.Length == 1 ? "Delete the 1 found" : $"Delete all {shown.Length} found"
            : filter?.Kind switch
            {
                PersonKind.Voice => $"Delete all of {ShortName(filter.VoiceId!)}'s facts ({shown.Length})",
                PersonKind.Everyone => $"Delete all facts not tied to a voice ({shown.Length})",
                _ => $"Delete forgotten voices' facts ({shown.Length})"
            }).Replace("_", "__", StringComparison.Ordinal);
        ShowSelection();
    }

    private string ShortName(string voiceId) =>
        roster.Resolve(voiceId) is { } voice ? voice.Named ? voice.DisplayName : $"Voice {voice.Number}" : "a forgotten voice";

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        if (rendering || closed) return;
        RenderFacts();
        RenderActions();
    }

    /// <summary>How many facts and whose (counts only, never a name or a fact).</summary>
    private string Summary(int shown, bool all)
    {
        var text = facts.Count == 1 ? "1 fact remembered" : $"{facts.Count} facts remembered";
        var people = facts.Select(f => f.VoiceId is { } id ? roster.Resolve(id)?.Id : null).OfType<string>().Distinct().Count();
        var owned = facts.Count(f => f.VoiceId is { } id && roster.Resolve(id) is not null);
        var forgotten = facts.Count(f => f.VoiceId is { } id && roster.Resolve(id) is null);
        if (owned > 0) text += $": {owned} belong{(owned == 1 ? "s" : "")} to {(people == 1 ? "1 person" : $"{people} people")} Martlet knows by voice";
        if (forgotten > 0) text += $"{(owned > 0 ? "," : ":")} {forgotten} to a forgotten voice";
        return text + (all ? "." : $". Showing {shown}.");
    }

    private void PersonFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (rendering || closed) return;
        RenderFacts();
        if (FactsList.SelectedItem is null) ResetPersonChoice(null);
        RenderActions();
    }

    /// <summary>A voice as the window names it: its name (and "you" for yours), else "Voice 3".</summary>
    private static string PersonName(KnownVoice voice) =>
        (voice.Named ? voice.DisplayName : $"Voice {voice.Number}") + (voice.Owner ? " (you)" : "");

    private string? PersonName(string? voiceId) =>
        voiceId is null ? null : roster.Resolve(voiceId) is { } voice ? PersonName(voice) : "A forgotten voice";

    private string? SelectedVoice() => (PersonChoice.SelectedItem as PersonOption)?.VoiceId;

    private async void SaveFact_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireCurrentEnabledConfiguration(FactStatus))
            return;
        MemoryMutationReceipt? receipt = null;
        var retention = SelectedRetention(existing: null);
        var content = FactContent.Text;
        var voice = SelectedVoice();
        if (string.IsNullOrWhiteSpace(content))
        {
            FactStatus.Text = "Write the fact first.";
            return;
        }
        if (retention is null)
        {
            FactStatus.Text = "Choose how long to keep it first.";
            return;
        }
        await RunAsync(async token =>
            receipt = await service.SaveFactAsync(
                configurationRevision, content, retention, voice, token).ConfigureAwait(false),
            "Couldn't add the fact.");
        if (closed || receipt is null)
            return;
        DisposeExportPreview();
        FactsList.UnselectAll();
        ResetEditor();
        await RefreshFactsAsync();
        FactStatus.Text = "Fact added. " + FactStatus.Text;
    }

    private async void EditFact_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireCurrentEnabledConfiguration(FactStatus))
            return;
        if (SingleSelected() is not { } fact)
            return;
        MemoryMutationReceipt? receipt = null;
        var retention = SelectedRetention(fact);
        var content = FactContent.Text;
        var voice = SelectedVoice();
        if (retention is null)
        {
            FactStatus.Text = "Choose how long to keep it first.";
            return;
        }
        await RunAsync(async token =>
            receipt = await service.EditFactAsync(
                configurationRevision, fact, content, retention, voice, token).ConfigureAwait(false),
            "Couldn't edit the fact. Refresh and try again.");
        if (closed || receipt is null)
            return;
        DisposeExportPreview();
        await RefreshFactsAsync();
        FactStatus.Text = "Fact updated. " + FactStatus.Text;
    }

    private async void DeleteFact_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedFacts();
        if (selected.Count == 1)
        {
            if (!RequireCurrentEnabledConfiguration(FactStatus))
                return;
            var fact = selected[0];
            if (!confirm(this, $"Delete this remembered fact?\n\n\"{PreviewFact(fact.Content)}\"", "Delete remembered fact"))
                return;
            MemoryDeleteReceipt? receipt = null;
            await RunAsync(async token =>
                receipt = await service.DeleteFactAsync(configurationRevision, fact, token).ConfigureAwait(false),
                "Couldn't delete the fact. Refresh and try again.");
            if (closed || receipt is null)
                return;
            DisposeExportPreview();
            await RefreshFactsAsync();
            FactStatus.Text = "Fact deleted. " + FactStatus.Text;
        }
        else if (selected.Count > 1)
            await DeleteManyAsync(selected, $"Delete the {selected.Count} selected facts?", "Delete remembered facts");
    }

    /// <summary>Deletes every fact listed now: one person's (Show), what the search found, or both.</summary>
    private async void DeleteShown_Click(object sender, RoutedEventArgs e)
    {
        var shown = ((IEnumerable<FactItem>?)FactsList.ItemsSource ?? []).Select(item => item.Fact).ToArray();
        if (shown.Length == 0) return;
        var what = string.IsNullOrWhiteSpace(SearchBox.Text) && PersonFilter.SelectedItem is PersonOption { Kind: PersonKind.Voice, VoiceId: { } voice }
            ? $"all {shown.Length} of {ShortName(voice)}'s facts" : shown.Length == 1 ? "the 1 fact shown" : $"all {shown.Length} facts shown";
        await DeleteManyAsync(shown, $"Delete {what}? This can't be undone.", "Delete remembered facts");
    }

    /// <summary>Forgets everything Martlet remembers (on every computer the memory sync reaches).</summary>
    private async void DeleteAll_Click(object sender, RoutedEventArgs e)
    {
        if (facts.Count == 0) return;
        await DeleteManyAsync(facts.ToArray(),
            $"Delete everything Martlet remembers ({(facts.Count == 1 ? "1 fact" : $"{facts.Count} facts")}), about everyone? This can't be undone.",
            "Delete all memories");
    }

    private async Task DeleteManyAsync(IReadOnlyCollection<MemoryFact> doomed, string question, string title)
    {
        if (!RequireCurrentEnabledConfiguration(FactStatus) || !confirm(this, question, title))
            return;
        MemoryExpiryReceipt? receipt = null;
        await RunAsync(async token =>
            receipt = await service.DeleteFactsAsync(configurationRevision, doomed, token).ConfigureAwait(false),
            "Couldn't delete the facts. Refresh and try again.");
        if (closed || receipt is null)
            return;
        DisposeExportPreview();
        FactsList.UnselectAll();
        await RefreshFactsAsync();
        FactStatus.Text = (receipt.DeletedFacts == 1 ? "Deleted 1 fact. " : $"Deleted {receipt.DeletedFacts} facts. ") + FactStatus.Text;
    }

    private void NewFact_Click(object sender, RoutedEventArgs e)
    {
        FactsList.UnselectAll();
        ResetEditor();
        RenderActions();
        FactContent.Focus();
    }

    private IReadOnlyList<MemoryFact> SelectedFacts() => FactsList.SelectedItems.Cast<FactItem>().Select(item => item.Fact).ToArray();

    private MemoryFact? SingleSelected() => FactsList.SelectedItems.Count == 1 && FactsList.SelectedItem is FactItem { Fact: var fact } ? fact : null;

    /// <summary>The editor for a new fact: empty, for the voice shown (else yours), kept until deleted.</summary>
    private void ResetEditor()
    {
        editing = null;
        if (FactContent is null) return;
        FactContent.Clear();
        FactDetails.Text = "";
        EditorHeading.Text = "New fact";
        RetentionChoice.ItemsSource = NewRetentionOptions;
        RetentionChoice.SelectedIndex = 0;
        ResetPersonChoice(null);
    }
    private async void CreateExportPreview_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireCurrentEnabledConfiguration(ExportStatus))
            return;
        MemoryExportPreview? preview = null;
        await RunAsync(async token =>
            preview = await service.CreateExportPreviewAsync(configurationRevision, token).ConfigureAwait(false),
            "Couldn't prepare the export. No file was created.");
        if (closed || preview is null)
            return;
        DisposeExportPreview();
        exportPreview = preview;
        ExportPreviewText.Text = Encoding.UTF8.GetString(preview.Preview());
        ExportSummary.Text = preview.FactCount == 1 ? "Preview ready: 1 fact." : $"Preview ready: {preview.FactCount} facts.";
        rendering = true;
        AcceptExport.IsChecked = false;
        rendering = false;
        ExportStatus.Text = "Review the preview. Nothing has been exported.";
        RenderActions();
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireCurrentEnabledConfiguration(ExportStatus))
            return;
        if (exportPreview is null || AcceptExport.IsChecked != true)
        {
            ExportStatus.Text = "Review the preview and choose a destination before exporting.";
            return;
        }
        MemoryExportReceipt? receipt = null;
        try
        {
            var destination = ExportDestination.Text;
            var authorization = exportPreview.Authorize(
                destination, MemoryExportDecision.Export);
            await RunAsync(async token =>
                receipt = await service.ExportAsync(
                    configurationRevision, exportPreview, authorization,
                    destination, token).ConfigureAwait(false),
                "Couldn't create the export. No file was created.");
        }
        catch (Exception error) when (error is MemoryException or ArgumentException or NotSupportedException)
        {
            ExportStatus.Text = Describe(error);
        }
        if (closed || receipt is null)
            return;
        ExportStatus.Text = "Memory export created. It was not uploaded.";
        DisposeExportPreview();
        RenderActions();
    }

    private void FactsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (rendering || closed) return;
        ShowSelection();
    }

    /// <summary>The editor follows the selection: one fact to change, several to delete, none for a new fact (what was typed for
    /// a new fact stays).</summary>
    private void ShowSelection()
    {
        var selected = SelectedFacts();
        if (selected.Count == 0)
        {
            if (editing is not null) ResetEditor();
            RenderActions();
            return;
        }
        if (selected.Count > 1)
        {
            editing = null;
            FactContent.Clear();
            EditorHeading.Text = $"{selected.Count} facts selected";
            FactDetails.Text = "Delete them together, or select just one to change it.";
            RenderActions();
            return;
        }
        var fact = selected[0];
        EditorHeading.Text = "Selected fact";
        if (editing != (fact.Id, fact.Revision))
        {
            editing = (fact.Id, fact.Revision);
            FactContent.Text = fact.Content;
            ResetPersonChoice(fact);
            RetentionChoice.ItemsSource = NewRetentionOptions.Append(
                new RetentionOption("Keep current retention", null, KeepCurrent: true));
            RetentionChoice.SelectedIndex = NewRetentionOptions.Length;
        }
        FactDetails.Text =
            $"Belongs to: {PersonName(fact.VoiceId) ?? "everyone (not tied to a voice)"}\n" +
            $"Created: {When(fact.CreatedAtUtc)} ({MemoryPromptContext.Source(fact.CreatedFrom.SourceKind)})\n" +
            $"Updated: {When(fact.UpdatedAtUtc)} ({MemoryPromptContext.Source(fact.LastModifiedBy.SourceKind)})\n" +
            $"Retention: {RetentionText(fact.Retention)}";
        RenderActions();
    }

    private MemoryRetention? SelectedRetention(MemoryFact? existing)
    {
        if (RetentionChoice.SelectedItem is not RetentionOption selected)
            return null;
        if (selected.KeepCurrent)
            return existing?.Retention;
        return selected.Duration is { } duration
            ? MemoryRetention.ExpiringAt(DateTimeOffset.UtcNow + duration)
            : MemoryRetention.UntilDeleted();
    }

    private void Configuration_Changed(object sender, RoutedEventArgs e)
    {
        if (rendering)
            return;
        InvalidateDraftPresentation();
        RenderResolvedDirectory();
        RenderActions();
        // Turning memory on or off, or choosing where it is stored, saves at once.
        if (CustomChoice.IsChecked != true || !string.IsNullOrWhiteSpace(CustomDirectory.Text))
            autoSave.SaveNowAsync().Forget();
        else
            ConfigurationStatus.Text = "Choose a folder for memory (Browse), or use the Martlet folder.";
    }

    private void ConfigurationText_Changed(object sender, TextChangedEventArgs e)
    {
        if (rendering)
            return;
        InvalidateDraftPresentation();
        RenderResolvedDirectory();
        RenderActions();
    }

    // A typed folder saves when you leave the field or press Enter, not while it is half typed.
    private void CustomDirectory_LostFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        if (!rendering && !ConfigurationMatchesPersisted()) autoSave.SaveNowAsync().Forget();
    }

    private void CustomDirectory_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter && !ConfigurationMatchesPersisted()) autoSave.SaveNowAsync().Forget();
    }

    private void RenderResolvedDirectory()
    {
        if (ResolvedDirectoryText is null)
            return;
        ResolvedDirectoryText.Text = CustomChoice.IsChecked == true
            ? $"Custom folder: {CustomDirectory.Text}"
            : $"Folder: {service.DefaultDirectory}";
        CustomDirectory.IsEnabled = CustomChoice.IsChecked == true;
        BrowseDirectoryButton.IsEnabled = CustomChoice.IsChecked == true && !operations.IsRunning;
    }

    private void BrowseDirectory_Click(object sender, RoutedEventArgs e)
    {
        var selected = chooseDirectory();
        if (string.IsNullOrWhiteSpace(selected))
            return;
        CustomDirectory.Text = selected;
        autoSave.SaveNowAsync().Forget();
    }

    private void BrowseExport_Click(object sender, RoutedEventArgs e)
    {
        var selected = chooseExport();
        if (!string.IsNullOrWhiteSpace(selected))
            ExportDestination.Text = selected;
    }

    private void ExportDestination_Changed(object sender, TextChangedEventArgs e)
    {
        if (!rendering && AcceptExport is not null)
        {
            rendering = true;
            AcceptExport.IsChecked = false;
            rendering = false;
        }
        RenderActions();
    }
    private void ExportConsent_Changed(object sender, RoutedEventArgs e)
    {
        if (!rendering)
            RenderActions();
    }

    private async Task RunAsync(Func<CancellationToken, Task> action, string failureText)
    {
        if (closed) return;
        if (operations.IsRunning)
        {
            ConfigurationStatus.Text = "Another Martlet action is still finishing. Wait a moment and try again.";
            while (!closed && operations.IsRunning)
            {
                RenderActions();
                await Task.Delay(250);
            }
            if (!closed) RenderActions();
            return;
        }
        Exception? failure = null;
        var worker = operations.TryStart(async token =>
        {
            try
            {
                await action(token).ConfigureAwait(false);
                return new SetupWorkResult(SetupWorkOutcome.Completed);
            }
            catch (Exception error) when (error is MemoryException or DesktopMemoryException or
                ContractException or OperationCanceledException or ArgumentException or
                NotSupportedException or IOException or UnauthorizedAccessException)
            {
                failure = error;
                return new SetupWorkResult(error is OperationCanceledException
                    ? SetupWorkOutcome.Canceled : SetupWorkOutcome.Failed);
            }
        });
        if (worker is null)
            return;
        active = worker;
        RenderActions();
        while (!worker.Completion.IsCompleted)
        {
            await Task.WhenAny(worker.Completion, Task.Delay(250));
            if (!closed) RenderActions();
        }
        var completed = await worker.Completion;
        active = null;
        if (closed)
            return;
        if (failure is not null)
        {
            var text = Describe(failure);
            ConfigurationStatus.Text = FactStatus.Text = ExportStatus.Text =
                string.IsNullOrWhiteSpace(text) ? failureText : text;
        }
        else if (completed.Outcome != SetupWorkOutcome.Completed)
        {
            ConfigurationStatus.Text = FactStatus.Text = ExportStatus.Text = failureText;
        }
        RenderActions();
    }

    private void RenderActions()
    {
        if (RetryCleanupButton is null)
            return;
        var busy = operations.IsRunning;
        RetryCleanupButton.IsEnabled = service.HasPendingCleanup;
        RetryCleanupButton.Visibility = service.HasPendingCleanup ? Visibility.Visible : Visibility.Collapsed;
        if (service.HasPendingCleanup)
            ConfigurationStatus.Text = "Memory cleanup is pending. Check folder access, then retry cleanup.";
        var enabled = ConfigurationMatchesPersisted() &&
            loadedSettings?.Memory is { Enabled: true } memory &&
            memory.ConfigurationRevision == configurationRevision;
        ReloadButton.IsEnabled = !busy;
        CreateExportPreviewButton.IsEnabled = enabled && !busy;
        var selected = FactsList.SelectedItems.Count;
        SaveFactButton.IsEnabled = enabled && !busy;
        SaveFactButton.Content = selected == 1 ? "_Add as new fact" : "_Add fact";
        EditFactButton.Visibility = selected == 1 ? Visibility.Visible : Visibility.Collapsed;
        EditFactButton.IsEnabled = enabled && !busy && selected == 1;
        NewFactButton.Visibility = selected > 0 ? Visibility.Visible : Visibility.Collapsed;
        DeleteFactButton.IsEnabled = enabled && !busy && selected > 0;
        DeleteFactButton.Content = selected > 1 ? $"_Delete {selected} selected" : "_Delete selected";
        DeleteShownButton.IsEnabled = enabled && !busy && FactsList.Items.Count > 0;
        DeleteAllButton.IsEnabled = enabled && !busy && facts.Count > 0;
        BrowseExportButton.IsEnabled = !busy;
        ExportButton.IsEnabled = enabled && !busy && exportPreview is not null &&
            AcceptExport.IsChecked == true && !string.IsNullOrWhiteSpace(ExportDestination.Text);
        RenderResolvedDirectory();
    }

    private bool ConfigurationMatchesPersisted()
    {
        if (loadedSettings?.Memory is not { } persisted)
            return false;
        var policy = CustomChoice.IsChecked == true
            ? MemoryStoragePolicy.CustomLocalDirectory
            : MemoryStoragePolicy.AppLocalData;
        if ((EnableChoice.IsChecked == true) != persisted.Enabled ||
            policy != persisted.StoragePolicy)
            return false;
        if (policy == MemoryStoragePolicy.AppLocalData)
            return true;
        try
        {
            return string.Equals(
                MemorySettings.NormalizeCustomDirectory(CustomDirectory.Text),
                persisted.CustomDirectory,
                StringComparison.Ordinal);
        }
        catch (ContractException)
        {
            return false;
        }
    }

    private bool RequireCurrentEnabledConfiguration(TextBlock status)
    {
        if (ConfigurationMatchesPersisted() &&
            loadedSettings?.Memory is { Enabled: true } memory &&
            memory.ConfigurationRevision == configurationRevision)
            return true;
        status.Text = loadedSettings?.Memory is { Enabled: false } && ConfigurationMatchesPersisted()
            ? "Turn memory on to add or change facts."
            : "Your memory settings aren't saved yet. Finish the folder (press Enter) or wait a moment, then try again.";
        return false;
    }

    private void InvalidateDraftPresentation()
    {
        if (ConfigurationMatchesPersisted())
            return;
        FactsList.ItemsSource = null;
        facts = [];
        ResetEditor();
        DisposeExportPreview();
        FactStatus.Text = "Memory settings changed. Facts show again once they are saved.";
    }

    private void DisposeExportPreview()
    {
        exportPreview?.Dispose();
        exportPreview = null;
        if (ExportPreviewText is null)
            return;
        ExportPreviewText.Clear();
        ExportSummary.Text = "";
        rendering = true;
        AcceptExport.IsChecked = false;
        rendering = false;
    }

    private static string Describe(Exception error) => error switch
    {
        DesktopMemoryException app => app.Message,
        MemoryException memory => memory.Message,
        ContractException contract => contract.Message,
        OperationCanceledException => "Memory action canceled. Reload if the window looks out of date.",
        _ => "Memory storage is unavailable. Check the selected folder and free space."
    };

    private static string RetentionText(MemoryRetention retention) =>
        retention.Kind == MemoryRetentionKind.UntilDeleted
            ? "kept until you delete it"
            : $"expires {When(retention.ExpiresAtUtc!.Value)}";

    private static string When(DateTimeOffset value) => value.LocalDateTime.ToString("g");

    private static string PreviewFact(string content)
    {
        var oneLine = string.Join(' ', content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return oneLine.Length <= 160 ? oneLine : oneLine[..157] + "...";
    }

    private static bool Confirm(Window owner, string text, string title) =>
        ConfirmationDialog.Confirm(owner, text, title);

    private static string? PickDirectory()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose a local memory directory",
            Multiselect = false
        };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    private static string? PickExport()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Create a local memory JSON export",
            Filter = "JSON files (*.json)|*.json",
            DefaultExt = ".json",
            AddExtension = true,
            OverwritePrompt = true
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private bool closeConfirmed;

    /// <summary>A typed custom folder that wasn't committed yet is saved before the window closes.</summary>
    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!closeConfirmed && loadedSettings is not null && !ConfigurationMatchesPersisted() && !operations.IsRunning &&
            (CustomChoice.IsChecked != true || !string.IsNullOrWhiteSpace(CustomDirectory.Text)))
        {
            e.Cancel = true;
            closeConfirmed = true;
            await autoSave.SaveNowAsync();
            await Dispatcher.InvokeAsync(Close);
            return;
        }
        closed = true;
        autoSave.Cancel();
        active?.RequestCancellation();
        DisposeExportPreview();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
