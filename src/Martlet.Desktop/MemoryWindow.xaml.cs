using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Core.Speakers;
using Martlet.Memory;
using Microsoft.Win32;

namespace Martlet.Desktop;

/// <summary>The Memory window: this file holds its state, loading, refreshing, running actions and closing. The parts by feature
/// are MemoryWindow.Facts.cs (the list and the editor), MemoryWindow.Configuration.cs (memory on or off and where it is stored),
/// MemoryWindow.Export.cs and MemoryWindow.Sharing.cs.</summary>
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

    /// <summary>One fact in the list, with whose it is (null for everyone's) and, for a fact kept in another memory space than
    /// the active one, that space (its ID and name). Sharing facts between spaces (workstream W10) starts from the space here.</summary>
    internal sealed record FactItem(MemoryFact Fact, string? Person, string? Space = null, string? SpaceName = null)
    {
        /// <summary>The line under the fact: whose it is, where it came from, when it last changed and, for a fact kept in another
        /// memory space than yours, that space. A replaced fact starts with when it stopped being true.</summary>
        public string Caption => (Fact.ReplacedAtUtc is { } replaced ? $"Replaced {When(replaced)} · " : "") +
            $"{Person ?? EveryoneLabel} · {MemoryPromptContext.Source(Fact.LastModifiedBy.SourceKind)} · " +
            When(Fact.UpdatedAtUtc) + (Fact.Retention.Kind == MemoryRetentionKind.ExpiresAt ? " · " + RetentionText(Fact.Retention) : "") +
            (Fact.Activity is null ? "" : " · " + MemoryActivity.Describe(Fact.Activity)) +
            (SpaceName is null ? "" : " · " + SpaceName);

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
    private readonly Func<KnownVoice, bool> yours;
    private string? showPerson;
    private VoiceRoster roster = VoiceRoster.Empty;
    private IReadOnlyList<MemoryFact> facts = [];
    /// <summary>The memory space of each listed fact kept in another space than the active one (the household's, or shared).</summary>
    private IReadOnlyDictionary<Guid, MemorySpace> elsewhere = new Dictionary<Guid, MemorySpace>();
    private string? activeSpace;
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
    /// <summary>Cancels the window's own reads when it closes.</summary>
    private readonly CancellationTokenSource lifetime = new();
    /// <summary>The read of the facts running now, and whether to read once more after it.</summary>
    private Task? refreshing;
    private bool refreshAgain;
    /// <summary>The store version the list shows, so a read that finds nothing new leaves the list and its status alone.</summary>
    private string? shownStore;
    /// <summary>What the facts line says on its own (the counts, or why the facts can't be shown). An action here puts what it
    /// did in front of it, so a change shown meanwhile never doubles that.</summary>
    private string factsLine = "";
    /// <summary>Follows other Martlet work (a reply) that holds the shared setup slot, so the buttons come back when it ends.</summary>
    private readonly DispatcherTimer busyCheck = new() { Interval = TimeSpan.FromMilliseconds(250) };

    /// <param name="voices">The voices Martlet knows (People), whose facts the window can show and choose.</param>
    /// <param name="person">A voice ID whose facts to show first (People's "What Martlet remembers").</param>
    /// <param name="yours">Whether a voice is the signed-in person's (<see cref="LocalVoices.IsYours"/>).</param>
    internal MemoryWindow(
        DesktopMemoryService service,
        SetupOperationRunner operations,
        Func<string?>? chooseDirectory = null,
        Func<string?>? chooseExport = null,
        Func<Window, string, string, bool>? confirm = null,
        Func<VoiceRoster>? voices = null,
        string? person = null,
        Func<KnownVoice, bool>? yours = null)
    {
        this.service = service;
        this.operations = operations;
        this.chooseDirectory = chooseDirectory ?? PickDirectory;
        this.chooseExport = chooseExport ?? PickExport;
        this.confirm = confirm ?? Confirm;
        this.voices = voices ?? (() => VoiceRoster.Empty);
        this.yours = yours ?? (voice => voice.Owner);
        showPerson = person;
        autoSave = new AutoSave(SaveConfigurationAsync);
        busyCheck.Tick += (_, _) => BusyChecked();
        InitializeComponent();
        RetentionChoice.ItemsSource = NewRetentionOptions;
        RetentionChoice.SelectedIndex = 0;
        RenderPeople();
        RenderActions();
    }

    /// <summary>The window is closed and none of its reads or actions still runs (tests wait for this before deleting files).</summary>
    internal bool Settled => closed && active is null && (refreshing is null or { IsCompleted: true });

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // Facts Martlet remembers, changes or forgets while the window is open (in a conversation, or when asked to) show at once.
        service.FactsChanged += Service_FactsChanged;
        await LoadAsync();
    }

    private async void Reload_Click(object sender, RoutedEventArgs e) => await LoadAsync();
    private void RetryCleanup_Click(object sender, RoutedEventArgs e) => service.RetryCleanup();

    // Raised off the dispatcher by any work that changed facts, this window's own included.
    private void Service_FactsChanged() => Dispatcher.InvokeAsync(() =>
    {
        if (!closed) RefreshFactsAsync().Forget();
    });

    /// <summary>Reads the settings and then the facts. Both are read directly, not on the setup slot a reply holds while
    /// Martlet answers, so the window fills in the moment it opens, even mid-conversation.</summary>
    private async Task LoadAsync()
    {
        SettingsLoadResult loaded;
        try
        {
            loaded = await service.LoadAsync(lifetime.Token);
        }
        catch (Exception error) when (error is OperationCanceledException or ContractException or IOException or
            UnauthorizedAccessException or ArgumentException)
        {
            if (!closed) ConfigurationStatus.Text = "Couldn't load memory settings, so no facts were opened.";
            return;
        }
        if (closed)
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
        ClearFacts();
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

    /// <summary>Reads the facts again and shows them: on opening, after an action here and whenever facts change elsewhere (a
    /// conversation remembered something, or Martlet was asked to change its memories). One read runs at a time; asked again
    /// meanwhile, it reads once more after it, so the list always ends up showing the newest facts.</summary>
    private Task RefreshFactsAsync()
    {
        if (refreshing is { IsCompleted: false })
        {
            refreshAgain = true;
            return refreshing;
        }
        return refreshing = RefreshLoopAsync();
    }

    private async Task RefreshLoopAsync()
    {
        do
        {
            refreshAgain = false;
            await ReadFactsAsync();
        }
        while (refreshAgain && !closed);
    }

    private async Task ReadFactsAsync()
    {
        // Facts show once memory is on with the settings shown here saved. A store waiting for Retry cleanup stays held.
        if (closed || !CurrentEnabledConfiguration() || service.HasPendingCleanup)
            return;
        var revision = configurationRevision;
        IReadOnlyList<MemorySpaceFacts> spaces;
        try
        {
            // Not on the setup slot (a reply holds it while Martlet answers): the memory service lets one user at a time in.
            spaces = await service.InspectSpacesAsync(revision, lifetime.Token);
        }
        catch (Exception error) when (error is MemoryException or DesktopMemoryException or ContractException or
            OperationCanceledException or ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            if (!closed && revision == configurationRevision)
                FactStatus.Text = factsLine = Describe(error);
            return;
        }
        if (closed || revision != configurationRevision || !CurrentEnabledConfiguration())
            return;
        // Nothing changed since the list was shown: keep it, its selection and what the last action said.
        var version = string.Join('|', spaces.Select(s => $"{s.Space.Id}:{s.Inspection.StoreId:N}:{s.Inspection.StoreRevision}"));
        if (shownStore == version)
            return;
        shownStore = version;
        var inspection = spaces[0].Inspection;
        activeSpace = spaces[0].Space.Id;
        if (exportPreview is not null && exportPreview.StoreRevision != inspection.StoreRevision)
        {
            DisposeExportPreview();
            ExportStatus.Text = "Memory changed after the preview; preview the export again.";
        }
        facts = [.. spaces.SelectMany(s => s.Inspection.Facts)];
        elsewhere = spaces.Skip(1).SelectMany(s => s.Inspection.Facts.Select(f => (f.Id, s.Space)))
            .GroupBy(f => f.Id).ToDictionary(g => g.Key, g => g.First().Space);
        RenderPeople();
        RenderFacts();
        RenderActions();
    }

    /// <summary>Empties the list until the facts are read again.</summary>
    private void ClearFacts()
    {
        FactsList.ItemsSource = null;
        facts = [];
        elsewhere = new Dictionary<Guid, MemorySpace>();
        shownStore = null;
    }

    private async Task RunAsync(Func<CancellationToken, Task> action, string failureText)
    {
        if (closed) return;
        if (operations.IsRunning)
        {
            ConfigurationStatus.Text = "Another Martlet action is still finishing; wait a moment and try again.";
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
            ConfigurationStatus.Text = "Memory cleanup is pending; check folder access, then choose Retry cleanup.";
        var enabled = CurrentEnabledConfiguration();
        // Refresh only reads, so it works while a reply holds the setup slot; it waits for this window's own action.
        ReloadButton.IsEnabled = active is null;
        CreateExportPreviewButton.IsEnabled = enabled && !busy;
        var selected = FactsList.SelectedItems.Count;
        SaveFactButton.IsEnabled = enabled && !busy;
        SaveFactButton.Content = selected == 1 ? "_Add as new fact" : "_Add fact";
        EditFactButton.Visibility = selected == 1 ? Visibility.Visible : Visibility.Collapsed;
        var changeable = SelectedFacts().All(Changeable);
        // History stays as it was: a replaced fact can be deleted, not changed.
        EditFactButton.IsEnabled = enabled && !busy && selected == 1 && changeable && SingleSelected() is { IsHistory: false };
        NewFactButton.Visibility = selected > 0 ? Visibility.Visible : Visibility.Collapsed;
        DeleteFactButton.IsEnabled = enabled && !busy && selected > 0 && changeable;
        DeleteFactButton.Content = selected > 1 ? $"_Delete {selected} selected" : "_Delete selected";
        DeleteShownButton.IsEnabled = enabled && !busy && FactsList.Items.Count > 0;
        DeleteAllButton.IsEnabled = enabled && !busy && facts.Any(f => !elsewhere.ContainsKey(f.Id));
        BrowseExportButton.IsEnabled = !busy;
        ExportButton.IsEnabled = enabled && !busy && exportPreview is not null &&
            AcceptExport.IsChecked == true && !string.IsNullOrWhiteSpace(ExportDestination.Text);
        RenderResolvedDirectory();
        RenderShare();
        if (busy && !closed && !busyCheck.IsEnabled)
            busyCheck.Start();
    }

    /// <summary>Other Martlet work (a reply, or a store waiting for Retry cleanup) holds the setup slot: once it ends, the
    /// buttons come back, and facts not read yet are read.</summary>
    private void BusyChecked()
    {
        if (closed)
        {
            busyCheck.Stop();
            return;
        }
        if (operations.IsRunning)
            return;
        busyCheck.Stop();
        RenderActions();
        if (shownStore is null)
            RefreshFactsAsync().Forget();
    }

    private static string Describe(Exception error) => error switch
    {
        DesktopMemoryException app => app.Message,
        MemoryException memory => memory.Message,
        ContractException contract => contract.Message,
        OperationCanceledException => "Memory action canceled; choose Refresh if the window looks out of date.",
        _ => "Memory storage is unavailable; check the selected folder and free space."
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
        service.FactsChanged -= Service_FactsChanged;
        busyCheck.Stop();
        lifetime.Cancel();
        autoSave.Cancel();
        active?.RequestCancellation();
        DisposeExportPreview();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
