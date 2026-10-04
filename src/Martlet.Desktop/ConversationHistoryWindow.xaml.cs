using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Martlet.Conversation;

namespace Martlet.Desktop;

/// <summary>The record of conversations on this PC (Companion › Memory › Open conversation history): the conversations, newest
/// first, what was said in the one selected, a search over everything said, and deleting one conversation or everything (each
/// asked first, No by default). It reads the record from memory once it is loaded; only deleting writes.</summary>
public partial class ConversationHistoryWindow : ThemedWindow
{
    private sealed record Item(HistoryConversation Conversation, string Text, IReadOnlySet<Guid> Hits)
    {
        public override string ToString() => Text;
    }

    private readonly DesktopConversationHistory history;
    private readonly Func<Window, string, string, bool> confirm;
    private readonly CancellationTokenSource lifetime = new();
    private string? query;
    private bool busy;

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
        lifetime.Cancel();
    }

    // A new exchange or a deletion elsewhere: the list follows (the selection stays when it can).
    private void HistoryChanged() => Dispatcher.InvokeAsync(() =>
    {
        if (!lifetime.IsCancellationRequested && !busy) Render();
    });

    private void Search_Click(object sender, RoutedEventArgs e)
    {
        query = string.IsNullOrWhiteSpace(SearchText.Text) ? null : SearchText.Text.Trim();
        Render();
    }

    private void ShowAll_Click(object sender, RoutedEventArgs e)
    {
        query = null;
        SearchText.Text = "";
        Render();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Render()
    {
        if (!history.Store.Loaded) return;
        var selected = (ConversationsList.SelectedItem as Item)?.Conversation.Id;
        var all = history.Store.Conversations(10_000);
        Item Describe(HistoryConversation conversation, IReadOnlySet<Guid> hits) => new(conversation,
            $"{PastConversations.When(conversation.Started, history.Zone)} · {conversation.Exchanges} " +
            $"exchange{(conversation.Exchanges == 1 ? "" : "s")}\n{conversation.Preview}", hits);
        Item[] items;
        if (query is null)
        {
            items = all.Select(conversation => Describe(conversation, new HashSet<Guid>())).ToArray();
            var stats = history.Store.Stats;
            StatusText.Text = stats.Exchanges == 0 ? "Nothing is recorded yet."
                : $"{stats.Conversations} conversation{(stats.Conversations == 1 ? "" : "s")} and {stats.Exchanges} exchange" +
                  $"{(stats.Exchanges == 1 ? "" : "s")} since {PastConversations.When(stats.Oldest!.Value, history.Zone)}." +
                  (stats.Skipped > 0 ? $" {stats.Skipped} unreadable line{(stats.Skipped == 1 ? " was" : "s were")} skipped." : "") +
                  (stats.NotIndexed > 0 ? $" The oldest {stats.NotIndexed} exchanges aren't listed." : "");
        }
        else
        {
            var hits = history.Store.Search(ConversationHistory.Terms(query), null, null, null, ConversationHistory.MaximumResults);
            var byId = all.ToDictionary(conversation => conversation.Id);
            items = hits.GroupBy(hit => hit.Exchange.ConversationId)
                .Where(group => byId.ContainsKey(group.Key))
                .Select(group => Describe(byId[group.Key], group.Select(hit => hit.Exchange.Id).ToHashSet())).ToArray();
            StatusText.Text = hits.Count == 0 ? $"Nothing found for \"{query}\"."
                : $"Found {hits.Count} exchange{(hits.Count == 1 ? "" : "s")} in {items.Length} conversation" +
                  $"{(items.Length == 1 ? "" : "s")} for \"{query}\" (best matches first; matching exchanges are marked ▶).";
        }
        ConversationsList.ItemsSource = items;
        ConversationsList.SelectedItem = items.FirstOrDefault(item => item.Conversation.Id == selected) ?? items.FirstOrDefault();
        ShowSelected();
        RenderButtons();
    }

    private void Conversations_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ShowSelected();
        RenderButtons();
    }

    private void ShowSelected()
    {
        if (ConversationsList.SelectedItem is not Item item)
        {
            ExchangesText.Text = "";
            return;
        }
        var text = new StringBuilder();
        foreach (var exchange in history.Store.Exchanges(item.Conversation.Id))
        {
            text.Append(item.Hits.Contains(exchange.Id) ? "▶ " : "").Append(PastConversations.When(exchange.At, history.Zone)).Append('\n');
            if (exchange.Kind == HistoryInputKind.Report || exchange.User.Length == 0)
                text.Append("Martlet, on its own:\n");
            else
                text.Append(exchange.Speaker ?? "You").Append(exchange.Kind == HistoryInputKind.Spoken ? " (said)" : "").Append(":\n")
                    .Append(exchange.User.Trim()).Append("\n\nMartlet:\n");
            text.Append(exchange.Reply.Trim()).Append("\n\n");
        }
        ExchangesText.Text = text.ToString().TrimEnd();
        ExchangesText.ScrollToHome();
    }

    private void RenderButtons()
    {
        var loaded = history.Store.Loaded;
        DeleteButton.IsEnabled = !busy && loaded && ConversationsList.SelectedItem is Item;
        DeleteAllButton.IsEnabled = !busy && loaded && history.Store.Stats.Exchanges > 0;
        SearchButton.IsEnabled = ShowAllButton.IsEnabled = !busy && loaded;
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (busy || ConversationsList.SelectedItem is not Item item) return;
        if (!confirm(this, $"Delete the conversation of {PastConversations.When(item.Conversation.Started, history.Zone)} " +
                $"({item.Conversation.Exchanges} exchange{(item.Conversation.Exchanges == 1 ? "" : "s")}) from the record? Martlet " +
                "won't be able to bring it up again. Facts it remembered from it stay in Memory. This can't be undone.",
                "Delete conversation"))
            return;
        await RunAsync(async token =>
        {
            var removed = await history.DeleteAsync(item.Conversation.Id, token);
            return $"Deleted the conversation ({removed} exchange{(removed == 1 ? "" : "s")}).";
        });
    }

    private async void DeleteAll_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        if (!confirm(this, "Delete the whole record of conversations on this PC? Martlet won't be able to bring any of them up " +
                "again. Facts it remembered stay in Memory. This can't be undone.", "Delete conversation history"))
            return;
        query = null;
        SearchText.Text = "";
        await RunAsync(async token =>
        {
            await history.DeleteAllAsync(token);
            return "Deleted the whole record of conversations.";
        });
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
            done = $"Couldn't delete it ({error.GetType().Name}). Nothing else changed.";
        }
        finally { busy = false; }
        if (lifetime.IsCancellationRequested) return;
        Render();
        StatusText.Text = done + " " + StatusText.Text;
    }
}
