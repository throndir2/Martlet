using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Core.Creations;

namespace Martlet.Desktop;

/// <summary>Creations: everything Martlet made (songs now, other kinds later), kept in the signed-in account's folder and the
/// same on every Martlet computer where that account is signed in, through the paired hosts (docs/CREATIONS.md, docs/ACCOUNTS.md). The page lists them and shows each one's text and details, with
/// Rename and Delete. It has no Play, Show or Activate: Martlet performs its creations itself when asked in conversation
/// (perform_creation).</summary>
public partial class MainWindow
{
    private readonly DispatcherTimer creationTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private bool creationBusy, creationQueued;
    private CreationSync? creationSync;
    private string creationStatus = "No other Martlet computers are paired yet, so your creations stay on this PC.";
    private string? selectedCreation;

    /// <summary>Where the signed-in account keeps its creations: its account folder (docs/ACCOUNTS.md); the data folder when this
    /// PC's account session couldn't be opened; null until Martlet can use its data folder.</summary>
    private string? CreationsFolder => accounts?.AccountFolder ?? store?.DataDirectory;

    /// <summary>An account change step (at start and on every switch): the creations kept in the data folder before accounts move
    /// once into the folder of the first account that signs in here (on an updated desktop, the owner), recorded in
    /// accounts\creations-moved.json. A move that fails is logged and tried again at the next change; it never stops a switch.</summary>
    internal static async Task MoveDataFolderCreationsAsync(AccountChange change, CancellationToken token)
    {
        if (CreationAccounts.Moved(change.HouseholdFolder) is not null) return;
        try
        {
            var move = await Task.Run(() => CreationAccounts.MoveDataFolderCreationsOnceAsync(change.HouseholdFolder, change.To, change.ToFolder,
                DateTimeOffset.UtcNow, token), token);
            if (move.Creations + move.Assets > 0)
                ErrorLog.Info($"Creations: moved {move.Creations} creation{(move.Creations == 1 ? "" : "s")} and {move.Assets} asset " +
                    $"file{(move.Assets == 1 ? "" : "s")} from the data folder to account {AccountSession.Short(change.To)}.");
        }
        catch (Exception error) when (CreationStore.IsFailure(error))
        {
            ErrorLog.Warn("Creations: couldn't move the data folder's creations to the account; trying again at the next account change.", error);
        }
    }

    private void InitializeCreations()
    {
        creationTimer.Tick += (_, _) => SyncCreationsAsync().Forget();
        if (conversation is not null) conversation.CreationsDirectory = CreationsFolder;
        if (accounts is not null) accounts.AccountChanged += () => Dispatcher.BeginInvoke(CreationsAccountChanged);
        CreationStore.Changed += directory => Dispatcher.InvokeAsync(() =>
        {
            if (closing || CreationsFolder is not { } folder || !string.Equals(directory, folder, StringComparison.OrdinalIgnoreCase)) return;
            QueueCreationSync();
            if (CreationsPage.IsVisible) RenderCreations();
        });
    }

    /// <summary>Another account is in use: Martlet keeps and shares that account's creations from now on.</summary>
    private void CreationsAccountChanged()
    {
        if (closing) return;
        if (conversation is not null) conversation.CreationsDirectory = CreationsFolder;
        selectedCreation = null;
        creationStatus = "Checking your creations on your other computers…";
        if (CreationsPage.IsVisible) RenderCreations();
        QueueCreationSync();
    }

    private void StartCreations()
    {
        if (store is null || closing) return;
        creationTimer.Start();
        SyncCreationsAsync().Forget();
    }

    /// <summary>Shares a change soon (debounced), so the owner's other computers get it quickly.</summary>
    private void QueueCreationSync()
    {
        if (creationQueued || closing) return;
        creationQueued = true;
        SyncSoonAsync().Forget();

        async Task SyncSoonAsync()
        {
            try { await Task.Delay(TimeSpan.FromSeconds(2), lifetime.Token); }
            catch (OperationCanceledException) { return; }
            finally { creationQueued = false; }
            await SyncCreationsAsync();
        }
    }

    /// <summary>Keeps this PC's creations the same as every paired host's (<see cref="CreationSync"/>): takes what other
    /// computers made, copies their assets, gives every host the merged list and every piece it lacks, so a computer that was
    /// off catches up from any host. Nothing is written while no host is paired or while the switch is off.</summary>
    private async Task SyncCreationsAsync()
    {
        if (creationBusy || closing || store is null) return;
        var before = creationStatus;
        if (!clusterEnabled)
        {
            creationStatus = "Keep Martlet the same on all my computers is off, so your creations stay on this PC.";
            if (CreationsPage.IsVisible && before != creationStatus) RenderCreations();
            return;
        }
        var hosts = NetworkMap.Hosts(Inputs());
        if (hosts.Count == 0)
        {
            creationStatus = "No other Martlet computers are paired yet, so your creations stay on this PC.";
            if (CreationsPage.IsVisible && before != creationStatus) RenderCreations();
            return;
        }
        creationBusy = true;
        var dataDirectory = CreationsFolder!;
        var token = lifetime.Token;
        var digest = CreationStore.View(dataDirectory).Digest();
        var changed = false;
        // The signed-in account's own list on each host; the owner's is joined with the old single list that desktops on an older
        // Martlet use (docs/ACCOUNTS.md). Without an account session, the old list as before.
        var account = accounts is { } session ? (Id: session.AccountId, Owner: session.OwnerId == session.AccountId) : ((Guid Id, bool Owner)?)null;
        var peers = hosts.Select(host => account is { } signedIn
            ? HostCreationPeer.ForAccount(host.HostId, () => ClusterSync.Connect(host.Pairing), signedIn.Id, signedIn.Owner)
            : new HostCreationPeer(host.HostId, () => ClusterSync.Connect(host.Pairing))).ToArray();
        try
        {
            if (creationSync?.DataDirectory != dataDirectory) creationSync = new CreationSync(dataDirectory);
            var sync = creationSync;
            var result = await Task.Run(() => sync.RunAsync(peers, token), token);
            changed = result.Added > 0 || result.Removed > 0 || result.Library.Digest() != digest;
            creationStatus = result.Describe().TrimEnd('.') + $" at {DateTime.Now:t}.";
            if (result.Added > 0 || result.Removed > 0)
                ErrorLog.Info($"Creations: copied {result.Added} asset{(result.Added == 1 ? "" : "s")} here and deleted {result.Removed}; " +
                    $"sent {result.Sent} piece{(result.Sent == 1 ? "" : "s")} to your hosts.");
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (CreationStore.IsFailure(error))
        {
            creationStatus = "Couldn't share your creations just now: " + error.Message;
            ErrorLog.Warn("Creations couldn't sync; trying again on the next check.", error);
        }
        finally
        {
            foreach (var peer in peers) (peer as IDisposable)?.Dispose();
            creationBusy = false;
            static string Gist(string text) => System.Text.RegularExpressions.Regex.Replace(text, @" at [^.]+\.", ".");
            if (!closing && CreationsPage.IsVisible && (changed || Gist(before) != Gist(creationStatus))) RenderCreations();
        }
    }

    private void RenderCreations()
    {
        if (CreationsFolder is not { } dataDirectory)
        {
            CreationsStatusText.Text = "Creations are unavailable until Martlet can use its data folder.";
            return;
        }
        var library = CreationStore.View(dataDirectory);
        var live = library.Live;
        var sync = CreationSyncState.Load(dataDirectory);
        var hosts = clusterEnabled ? sync?.Hosts ?? [] : [];
        CreationsStatusText.Text = creationStatus;
        CreationsSummaryText.Text = live.Count == 0 ? "No creations yet."
            : $"{live.Count} creation{(live.Count == 1 ? "" : "s")}, {SizeText(library.LiveBytes)} on every computer.";
        CreationsEmptyCard.Visibility = live.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CreationsContent.Visibility = live.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        CreationsList.Children.Clear();
        CreationDetail.Children.Clear();
        if (live.Count == 0) return;
        var selected = live.FirstOrDefault(c => c.Id == selectedCreation) ?? live[0];
        selectedCreation = selected.Id;
        foreach (var creation in live)
        {
            var kind = CreationRegistry.Shared.Find(creation.Kind);
            var content = new StackPanel();
            content.Children.Add(new TextBlock { Text = creation.Title, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            var state = Note(StateLine(creation, kind, dataDirectory, hosts), new Thickness(0, 2, 0, 0));
            state.FontSize = 12.5;
            AutomationProperties.SetAutomationId(state, "CreationState-" + creation.Key);
            content.Children.Add(state);
            var item = new RadioButton
            {
                Content = content, Tag = kind?.Glyph ?? "\uE7C3", GroupName = "Creations", IsChecked = creation.Id == selected.Id,
                Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 0, 0, 10)
            };
            item.SetResourceReference(StyleProperty, "ChoiceCard");
            AutomationProperties.SetAutomationId(item, "Creation-" + creation.Key);
            AutomationProperties.SetName(item, $"{creation.Title}: {state.Text}");
            var id = creation.Id;
            item.Checked += (_, _) =>
            {
                if (selectedCreation == id) return;
                selectedCreation = id;
                RenderCreationDetail(CreationStore.View(dataDirectory), dataDirectory, hosts);
            };
            CreationsList.Children.Add(item);
        }
        RenderCreationDetail(library, dataDirectory, hosts);
    }

    private void RenderCreationDetail(CreationLibrary library, string dataDirectory, IReadOnlyList<CreationHostState> hosts)
    {
        CreationDetail.Children.Clear();
        if (library.Find(selectedCreation ?? "") is not { Removed: false } creation) return;
        var kind = CreationRegistry.Shared.Find(creation.Kind);
        var title = new TextBlock { Text = creation.Title, FontSize = 20, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(title, "CreationTitle");
        CreationDetail.Children.Add(title);
        var made = Note(KindLine(creation, kind), new Thickness(0, 4, 0, 0));
        AutomationProperties.SetAutomationId(made, "CreationKind");
        CreationDetail.Children.Add(made);
        if (creation.CreatedBy is { Voice: not null } or { Persona: not null })
            CreationDetail.Children.Add(Note(string.Join(" · ", new[]
            {
                creation.CreatedBy.Voice is { } voice ? "Voice: " + voice : null,
                creation.CreatedBy.Persona is { } persona ? "Personality: " + persona : null
            }.OfType<string>()), new Thickness(0, 2, 0, 0)));
        var sync = Note(SyncLine(creation, dataDirectory, hosts), new Thickness(0, 2, 0, 0));
        AutomationProperties.SetAutomationId(sync, "CreationSync");
        CreationDetail.Children.Add(sync);
        var ask = Note(kind is null
                ? $"This Martlet doesn't know {creation.Kind} creations yet. Update Martlet to use it here; it is kept for your other computers."
                : $"Ask Martlet to {kind.Verb} it.", new Thickness(0, 10, 0, 6));
        ask.FontWeight = FontWeights.SemiBold;
        AutomationProperties.SetAutomationId(ask, "CreationAsk");
        CreationDetail.Children.Add(ask);

        // A picture shows itself here (Martlet shows it in conversation too).
        if (creation.Kind == Martlet.Conversation.PictureCreations.KindName && CreationStore.IsComplete(dataDirectory, creation))
        {
            var picture = new Image { MaxHeight = 360, MaxWidth = 520, Stretch = System.Windows.Media.Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 6) };
            AutomationProperties.SetAutomationId(picture, "CreationPicture");
            AutomationProperties.SetName(picture, creation.Title);
            CreationDetail.Children.Add(picture);
            LoadCreationPictureAsync(picture, dataDirectory, creation).Forget();
        }

        if (!string.IsNullOrWhiteSpace(creation.Summary)) CreationDetail.Children.Add(Body(creation.Summary));
        foreach (var section in kind?.DetailsFor(creation) ?? CreationKind.Sections(creation))
        {
            if (!string.IsNullOrWhiteSpace(section.Heading)) CreationDetail.Children.Add(Subheading(section.Heading, 12));
            CreationDetail.Children.Add(Body(section.Text));
        }
        var details = MetadataLines(creation);
        if (details.Count > 0)
        {
            CreationDetail.Children.Add(Subheading("Details", 16));
            foreach (var line in details) CreationDetail.Children.Add(Note(line, new Thickness(0, 1, 0, 0)));
        }

        var rename = new TextBox { Text = creation.Title, MaxLength = CreationLibrary.MaximumTitleLength, MinWidth = 260, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAutomationId(rename, "CreationRenameText");
        AutomationProperties.SetName(rename, "New title");
        var renameButton = PageButton("Rename", () => RenameCreationAsync(creation, rename.Text).Forget(), id: "CreationRename");
        var deleteButton = PageButton("Delete", () => DeleteCreationAsync(creation).Forget(), id: "CreationDelete");
        rename.KeyDown += (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Enter) return;
            e.Handled = true;
            RenameCreationAsync(creation, rename.Text).Forget();
        };
        var row = new WrapPanel { Margin = new Thickness(0, 18, 0, 0) };
        rename.Margin = new Thickness(0, 0, 10, 6);
        row.Children.Add(rename);
        renameButton.Margin = new Thickness(0, 0, 10, 6);
        row.Children.Add(renameButton);
        deleteButton.Margin = new Thickness(0, 0, 10, 6);
        row.Children.Add(deleteButton);
        CreationDetail.Children.Add(row);

        TextBlock Body(string text) => new() { Text = text.Trim(), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), FontSize = 14 };
        static TextBlock Subheading(string text, double top) =>
            new() { Text = text, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, top, 0, 2), TextWrapping = TextWrapping.Wrap };
    }

    private async Task LoadCreationPictureAsync(Image target, string dataDirectory, Creation creation)
    {
        try
        {
            var bytes = await CreationStore.Assets(dataDirectory, creation).ReadAsync(Martlet.Conversation.PictureCreations.Image, lifetime.Token);
            if (bytes is null || closing) return;
            var image = await Task.Run(() => PictureView.Decode(bytes, 1040));
            if (!closing) target.Source = image;
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (CreationStore.IsFailure(error)) { ErrorLog.Info($"Creations: couldn't show the picture ({error.Message})."); }
    }

    private async Task RenameCreationAsync(Creation creation, string? title)
    {
        if (store is null || closing) return;
        title = title?.Trim() ?? "";
        if (title == creation.Title) return;
        try
        {
            await CreationStore.RenameAsync(CreationsFolder!, creation.Id, title, ClusterDevice, DateTimeOffset.UtcNow, lifetime.Token);
            ActionText.Text = $"Renamed to '{title}' on all your computers.";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (CreationStore.IsFailure(error)) { ActionText.Text = error.Message; }
    }

    private async Task DeleteCreationAsync(Creation creation)
    {
        if (store is null || closing) return;
        var noun = CreationRegistry.Shared.Find(creation.Kind)?.Noun ?? "creation";
        if (!ConfirmationDialog.Confirm(this, $"Delete '{creation.Title}'?\n\nMartlet deletes this {noun} on this PC and all your other Martlet " +
                "computers. It can't be brought back.", "Delete " + noun, "Delete", "Keep it"))
            return;
        try
        {
            await CreationStore.RemoveAsync(CreationsFolder!, creation.Id, ClusterDevice, DateTimeOffset.UtcNow, lifetime.Token);
            selectedCreation = null;
            ActionText.Text = $"'{creation.Title}' was deleted on all your computers.";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (CreationStore.IsFailure(error)) { ActionText.Text = error.Message; }
    }

    /// <summary>A creation's row line: kind, length, size, when and where it was made, and where it is (never its title).</summary>
    private static string StateLine(Creation creation, CreationKind? kind, string dataDirectory, IReadOnlyList<CreationHostState> hosts) =>
        KindLine(creation, kind) + " · " + ShortSync(creation, dataDirectory, hosts);

    private static string KindLine(Creation creation, CreationKind? kind)
    {
        var parts = new List<string> { Capitalize(kind?.Noun ?? creation.Kind ?? "creation") };
        if (creation.Duration is { } duration) parts.Add(duration.TotalHours >= 1 ? duration.ToString(@"h\:mm\:ss", CultureInfo.CurrentCulture)
            : duration.ToString(@"m\:ss", CultureInfo.CurrentCulture));
        parts.Add(SizeText(creation.Bytes));
        var where = creation.CreatedBy?.Computer ?? creation.CreatedBy?.Device;
        parts.Add($"made {creation.CreatedAt.ToLocalTime():g}" + (where is null ? "" : $" on {where}"));
        return string.Join(" · ", parts);
    }

    private static string ShortSync(Creation creation, string dataDirectory, IReadOnlyList<CreationHostState> hosts)
    {
        var here = CreationStore.IsComplete(dataDirectory, creation) ? "on this PC" : "copying to this PC";
        if (hosts.Count == 0) return here;
        return $"{here}, on {hosts.Count(h => h.Complete.Contains(creation.Id))} of {hosts.Count} host{(hosts.Count == 1 ? "" : "s")}";
    }

    /// <summary>Where a creation is: this PC and each paired host that holds every piece of it, as of the last sync.</summary>
    private static string SyncLine(Creation creation, string dataDirectory, IReadOnlyList<CreationHostState> hosts)
    {
        var here = CreationStore.IsComplete(dataDirectory, creation) ? "On this PC." : "Still copying to this PC.";
        if (hosts.Count == 0) return here + " Not shared with other computers yet.";
        var holding = hosts.Where(h => h.Complete.Contains(creation.Id)).Select(h => h.HostId).ToArray();
        var missing = hosts.Where(h => !h.Complete.Contains(creation.Id)).Select(h => h.HostId + (h.State == CreationSyncState.Shared ? "" : $" ({h.State})")).ToArray();
        return here + (holding.Length > 0 ? $" Kept on {string.Join(", ", holding)} for your other computers." : "") +
            (missing.Length > 0 ? $" Not on {string.Join(", ", missing)} yet." : "");
    }

    /// <summary>The kind's metadata as readable lines: each top-level value (lists by their length), never more than 12.</summary>
    private static IReadOnlyList<string> MetadataLines(Creation creation)
    {
        var lines = new List<string>();
        if (creation.Metadata is { ValueKind: JsonValueKind.Object } metadata)
            foreach (var property in metadata.EnumerateObject().Take(12))
            {
                var name = Capitalize(property.Name.Replace('_', ' '));
                var value = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString(),
                    JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => property.Value.GetRawText(),
                    JsonValueKind.Array => $"{property.Value.GetArrayLength()} items",
                    JsonValueKind.Object => $"{property.Value.EnumerateObject().Count()} values",
                    _ => null
                };
                if (!string.IsNullOrWhiteSpace(value)) lines.Add($"{name}: {(value.Length > 120 ? value[..119] + "…" : value)}");
            }
        lines.Add("Parts: " + string.Join(", ", creation.Assets!.Select(a => $"{a.Name} ({SizeText(a.Bytes)})")));
        return lines;
    }

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpper(text[0], CultureInfo.CurrentCulture) + text[1..];

    private static string SizeText(long bytes) => bytes >= 1024 * 1024 ? $"{bytes / (1024d * 1024):0.#} MB" : $"{Math.Max(1, bytes / 1024)} KB";
}
