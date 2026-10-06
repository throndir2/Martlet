using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Martlet.Conversation;

namespace Martlet.Desktop;

/// <summary>The record of conversations (Companion › Memory › Open conversation history): the conversations, newest first,
/// from every app (this PC, Telegram, Discord, WhatsApp; filtered by app), each message of the one selected, a search over
/// everything said, and deleting or editing one message, deleting one conversation or everything (each delete asked first, No
/// by default). With <see cref="AlsoThere"/> (on), deletions and edits of Telegram and Discord messages are made there too, as
/// far as each app allows, through a queue at each app's pace (<see cref="PlatformChanges"/>). It reads the record from memory
/// once it is loaded; only deleting and editing write.</summary>
public partial class ConversationHistoryWindow : ThemedWindow
{
    private sealed record ConversationItem(HistoryConversation Conversation, string Text, string Name, IReadOnlySet<Guid> Hits)
    {
        public override string ToString() => Text;
    }

    private sealed record MessageItem(HistoryExchange Exchange, HistorySide Side, string Text, string Name)
    {
        public override string ToString() => Text;
    }

    private readonly DesktopConversationHistory history;
    private readonly Func<Window, string, string, bool> confirm;
    private readonly CancellationTokenSource lifetime = new();
    private string? query;
    private bool busy;
    private MessageItem? editing;

    internal ConversationHistoryWindow(DesktopConversationHistory history, Func<Window, string, string, bool>? confirm = null)
    {
        this.history = history;
        this.confirm = confirm ?? ((owner, text, title) => ConfirmationDialog.Confirm(owner, text, title));
        InitializeComponent();
        RenderButtons();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "Reading the record…";
        history.Platforms.Changed += PlatformsChanged;
        RenderPlatforms();
        try { await history.Store.LoadAsync(lifetime.Token); }
        catch (OperationCanceledException) { return; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = "Couldn't read the record of conversations.";
            return;
        }
        history.Changed += HistoryChanged;
        Render();
        SearchText.Focus();
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        history.Changed -= HistoryChanged;
        history.Platforms.Changed -= PlatformsChanged;
        lifetime.Cancel();
    }

    // A new exchange or a deletion elsewhere: the list follows (the selection stays when it can).
    private void HistoryChanged() => Dispatcher.InvokeAsync(() =>
    {
        if (!lifetime.IsCancellationRequested && !busy && editing is null) Render();
    });

    private void PlatformsChanged() => Dispatcher.InvokeAsync(() =>
    {
        if (!lifetime.IsCancellationRequested) RenderPlatforms();
    });

    private void RenderPlatforms()
    {
        var status = history.Platforms.Status;
        PlatformText.Text = status.Describe();
        PlatformCancelButton.Visibility = status.Pending > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The app chosen in <see cref="AppFilter"/>, or null for all.</summary>
    private string? App => (AppFilter.SelectedItem as ComboBoxItem)?.Tag is string { Length: > 0 } app ? app : null;

    private bool Shown(HistoryExchange exchange) => App is not { } app || exchange.App == app;

    private void AppFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) Render();
    }

    private void Search_Click(object sender, RoutedEventArgs e)
    {
        query = string.IsNullOrWhiteSpace(SearchText.Text) ? null : SearchText.Text.Trim();
        Render();
    }

    private void ShowAll_Click(object sender, RoutedEventArgs e)
    {
        query = null;
        SearchText.Text = "";
        AppFilter.SelectedIndex = 0;
        Render();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private static string Apps(HistoryConversation conversation) =>
        string.Join(", ", (conversation.Apps ?? [HistoryApps.Pc]).Select(HistoryApps.Name));

    private void Render()
    {
        if (!history.Store.Loaded) return;
        StopEditing();
        var selected = (ConversationsList.SelectedItem as ConversationItem)?.Conversation.Id;
        var app = App;
        var all = history.Store.Conversations(10_000).Where(conversation => app is null || conversation.Apps?.Contains(app) == true ||
            conversation.Apps is null && app == HistoryApps.Pc).ToArray();
        var number = 0;
        ConversationItem Describe(HistoryConversation conversation, IReadOnlySet<Guid> hits) => new(conversation,
            $"{PastConversations.When(conversation.Started, history.Zone)} · {Apps(conversation)}" +
            (conversation.ChatName is { } chat ? $" · {chat}" : "") +
            $" · {conversation.Exchanges} exchange{(conversation.Exchanges == 1 ? "" : "s")}\n{conversation.Preview}",
            $"Conversation {++number} ({Apps(conversation)})", hits);
        ConversationItem[] items;
        var shown = app is null ? "" : $" from {HistoryApps.Name(app)}";
        if (query is null)
        {
            items = all.Select(conversation => Describe(conversation, new HashSet<Guid>())).ToArray();
            var stats = history.Store.Stats;
            StatusText.Text = stats.Exchanges == 0 ? "Nothing is recorded yet."
                : $"{stats.Conversations} conversation{(stats.Conversations == 1 ? "" : "s")} and {stats.Exchanges} exchange" +
                  $"{(stats.Exchanges == 1 ? "" : "s")} since {PastConversations.When(stats.Oldest!.Value, history.Zone)}" +
                  (stats.Apps is { Count: > 1 } apps ? $" ({string.Join(", ", apps.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{HistoryApps.Name(pair.Key)} {pair.Value}"))})" : "") + "." +
                  (app is null ? "" : $" Showing {items.Length} with messages{shown}.") +
                  (stats.Skipped > 0 ? $" {stats.Skipped} unreadable line{(stats.Skipped == 1 ? " was" : "s were")} skipped." : "") +
                  (stats.NotIndexed > 0 ? $" The oldest {stats.NotIndexed} exchanges aren't listed." : "");
        }
        else
        {
            var hits = history.Store.Search(ConversationHistory.Terms(query), null, null, null, ConversationHistory.MaximumResults, Shown);
            var byId = all.ToDictionary(conversation => conversation.Id);
            items = hits.GroupBy(hit => hit.Exchange.ConversationId)
                .Where(group => byId.ContainsKey(group.Key))
                .Select(group => Describe(byId[group.Key], group.Select(hit => hit.Exchange.Id).ToHashSet())).ToArray();
            StatusText.Text = hits.Count == 0 ? $"Nothing found for \"{query}\"{shown}."
                : $"Found {hits.Count} exchange{(hits.Count == 1 ? "" : "s")} in {items.Length} conversation" +
                  $"{(items.Length == 1 ? "" : "s")} for \"{query}\"{shown} (best matches first; matching messages are marked ▶).";
        }
        ConversationsList.ItemsSource = items;
        ConversationsList.SelectedItem = items.FirstOrDefault(item => item.Conversation.Id == selected) ?? items.FirstOrDefault();
        ShowSelected();
        RenderButtons();
    }

    private void Conversations_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        StopEditing();
        ShowSelected();
        RenderButtons();
    }

    private void Messages_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        StopEditing();
        RenderButtons();
    }

    private void ShowSelected()
    {
        var keep = MessagesList.SelectedItem is MessageItem chosen ? (chosen.Exchange.Id, chosen.Side) : default;
        if (ConversationsList.SelectedItem is not ConversationItem item)
        {
            MessagesList.ItemsSource = Array.Empty<MessageItem>();
            return;
        }
        var messages = new List<MessageItem>();
        foreach (var exchange in history.Store.Exchanges(item.Conversation.Id).Where(Shown))
        {
            var mark = item.Hits.Contains(exchange.Id) ? "▶ " : "";
            var when = PastConversations.When(exchange.At, history.Zone);
            var where = exchange.App == HistoryApps.Pc ? "" : $" · {HistoryApps.Name(exchange.App)}";
            var edited = exchange.Edited is null ? "" : " · edited";
            if (exchange.User.Length > 0 && exchange.Kind != HistoryInputKind.Report)
            {
                var who = (exchange.Speaker ?? "You") + (exchange.Kind == HistoryInputKind.Spoken ? " (said)" : "");
                messages.Add(new(exchange, HistorySide.User, $"{mark}{when}{where}{edited}\n{who}:\n{exchange.User.Trim()}",
                    $"Message {messages.Count + 1}: {(exchange.Speaker is null ? "you" : "the person")}{where}"));
            }
            if (exchange.Reply.Length > 0)
            {
                var who = exchange.Kind == HistoryInputKind.Report || exchange.User.Length == 0 ? "Martlet, on its own" : "Martlet";
                messages.Add(new(exchange, HistorySide.Reply, $"{mark}{when}{where}{edited}\n{who}:\n{exchange.Reply.Trim()}",
                    $"Message {messages.Count + 1}: Martlet{where}"));
            }
        }
        MessagesList.ItemsSource = messages;
        MessagesList.SelectedItem = messages.FirstOrDefault(message => (message.Exchange.Id, message.Side) == keep);
        if (MessagesList.Items.Count > 0) MessagesList.ScrollIntoView(MessagesList.SelectedItem ?? MessagesList.Items[0]);
    }

    private void RenderButtons()
    {
        var loaded = history.Store.Loaded;
        var message = MessagesList.SelectedItem is MessageItem;
        DeleteButton.IsEnabled = !busy && loaded && ConversationsList.SelectedItem is ConversationItem;
        DeleteAllButton.IsEnabled = !busy && loaded && history.Store.Stats.Exchanges > 0;
        DeleteMessageButton.IsEnabled = EditButton.IsEnabled = !busy && loaded && message && editing is null;
        EditSaveButton.IsEnabled = !busy && editing is not null;
        SearchButton.IsEnabled = ShowAllButton.IsEnabled = AppFilter.IsEnabled = !busy && loaded;
    }

    private bool There => AlsoThere.IsChecked == true;

    private static string Where(HistoryExchange exchange) => exchange.App == HistoryApps.Pc ? "" : $" in {HistoryApps.Name(exchange.App)}";

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (busy || MessagesList.SelectedItem is not MessageItem item) return;
        editing = item;
        EditLabel.Content = item.Side == HistorySide.User
            ? $"Edit {(item.Exchange.Speaker is null ? "your" : "the person's")} message (here only{(item.Exchange.App == HistoryApps.Pc ? "" : $": {HistoryApps.Name(item.Exchange.App)} doesn't let Martlet edit it there")})"
            : $"Edit Martlet's reply{(There && item.Exchange.Source?.ReplyMessages is { Count: > 0 } ? $" (here and{Where(item.Exchange)})" : " (here)")}";
        EditText.Text = item.Side == HistorySide.User ? item.Exchange.User : item.Exchange.Reply;
        EditorPanel.Visibility = Visibility.Visible;
        EditText.Focus();
        RenderButtons();
    }

    private void EditCancel_Click(object sender, RoutedEventArgs e)
    {
        StopEditing();
        RenderButtons();
    }

    private void StopEditing()
    {
        editing = null;
        EditorPanel.Visibility = Visibility.Collapsed;
    }

    private async void EditSave_Click(object sender, RoutedEventArgs e)
    {
        if (busy || editing is not { } item) return;
        var text = EditText.Text;
        if (text.Trim() == (item.Side == HistorySide.User ? item.Exchange.User : item.Exchange.Reply).Trim())
        {
            StopEditing();
            RenderButtons();
            StatusText.Text = "Nothing changed. " + StatusText.Text;
            return;
        }
        if (string.IsNullOrWhiteSpace(text) && !confirm(this, "The new text is empty, so the message will be deleted. Delete it?", "Delete message"))
            return;
        var there = There;
        StopEditing();
        await RunAsync(async token =>
        {
            var change = await history.EditAsync(item.Exchange.Id, item.Side, text, there, token);
            return (string.IsNullOrWhiteSpace(text) ? "Deleted the message." : "Saved the edit.") + Describe(change, item.Exchange);
        });
    }

    private async void DeleteMessage_Click(object sender, RoutedEventArgs e)
    {
        if (busy || MessagesList.SelectedItem is not MessageItem item) return;
        var there = There && item.Exchange.Source is not null;
        var what = item.Side == HistorySide.User ? (item.Exchange.Speaker is null ? "your message" : "this message") : "Martlet's reply";
        if (!confirm(this, $"Delete {what} of {PastConversations.When(item.Exchange.At, history.Zone)} from the record?" +
                (there ? $" Martlet also deletes it{Where(item.Exchange)} when the app allows it." : "") +
                " Martlet won't be able to bring it up again. This can't be undone.", "Delete message"))
            return;
        await RunAsync(async token =>
        {
            var change = await history.DeleteMessageAsync(item.Exchange.Id, item.Side, there, token);
            return "Deleted the message." + Describe(change, item.Exchange);
        });
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (busy || ConversationsList.SelectedItem is not ConversationItem item) return;
        var there = There && item.Conversation.Apps?.Any(app => app != HistoryApps.Pc) == true;
        if (!confirm(this, $"Delete the conversation of {PastConversations.When(item.Conversation.Started, history.Zone)} " +
                $"({item.Conversation.Exchanges} exchange{(item.Conversation.Exchanges == 1 ? "" : "s")}) from the record?" +
                (there ? " Martlet also deletes its messages in Telegram and Discord where the app allows it." : "") +
                " Martlet won't be able to bring it up again. Facts it remembered from it stay in Memory. This can't be undone.",
                "Delete conversation"))
            return;
        await RunAsync(async token =>
        {
            var change = await history.DeleteAsync(item.Conversation.Id, there, token);
            return $"Deleted the conversation ({change.Removed} exchange{(change.Removed == 1 ? "" : "s")})." + Describe(change, null);
        });
    }

    private async void DeleteAll_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        var there = There && history.Store.Stats.Apps?.Keys.Any(app => app != HistoryApps.Pc) == true;
        if (!confirm(this, "Delete the whole record of conversations on this PC?" +
                (there ? " Martlet also deletes every recorded message in Telegram and Discord where the app allows it." : "") +
                " Martlet won't be able to bring any of them up again. Facts it remembered stay in Memory. This can't be undone.",
                "Delete conversation history"))
            return;
        query = null;
        SearchText.Text = "";
        await RunAsync(async token =>
        {
            var change = await history.DeleteAllAsync(there, token);
            return "Deleted the whole record of conversations." + Describe(change, null);
        });
    }

    private void PlatformCancel_Click(object sender, RoutedEventArgs e)
    {
        var waiting = history.Platforms.Status.Pending;
        if (waiting == 0) return;
        if (!confirm(this, $"Stop waiting to make {waiting} change{(waiting == 1 ? "" : "s")} in Telegram and Discord? Those messages stay " +
                "there as they are; the record here doesn't change.", "Stop waiting changes"))
            return;
        history.Platforms.Clear();
        RenderPlatforms();
    }

    private static string Describe(HistoryChange change, HistoryExchange? exchange)
    {
        var text = new StringBuilder();
        if (change.Queued > 0)
            text.Append($" {change.Queued} change{(change.Queued == 1 ? "" : "s")} to make{(exchange is null ? " in Telegram and Discord" : Where(exchange))} (see below).");
        foreach (var kept in change.KeptThere) text.Append(' ').Append(kept);
        return text.ToString();
    }

    private async Task RunAsync(Func<CancellationToken, Task<string>> work)
    {
        busy = true;
        RenderButtons();
        string done;
        try { done = await work(lifetime.Token); }
        catch (OperationCanceledException) { return; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            done = $"Couldn't change it ({error.GetType().Name}). Nothing else changed.";
        }
        finally { busy = false; }
        if (lifetime.IsCancellationRequested) return;
        Render();
        RenderPlatforms();
        StatusText.Text = done + " " + StatusText.Text;
    }
}
