using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Martlet.Desktop;

/// <summary>Companion › Memory, the UI only: Now (memory on or off, with Manage memory), the Memory brief and Finding by meaning
/// cards (MainWindow makes them) and Conversation history. Its lines come from the settings and the record itself, so it has no
/// page model; what it needs from the app is <see cref="IMemoryPageServices"/>.</summary>
internal sealed class MemoryPage(IMemoryPageServices services)
{
    /// <summary>Draws the page's cards into <paramref name="page"/>.</summary>
    internal void Render(Panel page)
    {
        var on = services.Settings?.Memory?.Enabled == true;
        page.Children.Add(Card(Heading("Now"),
            new TextBlock { Text = on ? "On" : "Off", FontSize = 15, TextWrapping = TextWrapping.Wrap },
            Note(on
                ? "Martlet remembers lasting facts you share and uses them later. Everything stays on this PC; manage facts in Manage memory."
                : "Martlet doesn't remember or recall anything between conversations: turn memory on in Manage memory.",
                new Thickness(0, 2, 0, 8)),
            Row(PageButton("Manage memory", services.OpenMemory, primary: !on, id: "OpenMemory"))));
        if (services.MemoryBriefCard(on) is { } brief) page.Children.Add(brief);
        if (services.MeaningSearchCard() is { } meaning) page.Children.Add(meaning);
        if (services.History is { } record) page.Children.Add(HistoryCard(record, on));
    }

    /// <summary>Companion › Memory › Conversation history: whether Martlet keeps a record of conversations (on by default while
    /// memory is on; the same on all your computers) and may search it on its own (search_conversations, off by default), what it
    /// holds and the window to read, search and delete it. Each choice saves at once (conversation-history.json, shared as
    /// conversation-history).</summary>
    private Border HistoryCard(DesktopConversationHistory record, bool memoryOn)
    {
        var prefs = record.Preferences;
        var status = Note(record.Describe(services.Settings?.Memory), new Thickness(0, 2, 0, 8));
        AutomationProperties.SetAutomationId(status, "HistoryStatus");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        var keep = new CheckBox { Content = "Keep a record of my conversations", IsChecked = prefs.Keep, IsEnabled = memoryOn };
        AutomationProperties.SetAutomationId(keep, "HistoryKeep");
        var search = new CheckBox
        {
            Content = "Let Martlet search the record on its own", IsChecked = prefs.Search, IsEnabled = memoryOn && prefs.Keep,
            Margin = new Thickness(0, 10, 0, 0)
        };
        AutomationProperties.SetAutomationId(search, "HistorySearch");
        void Save()
        {
            var next = new ConversationHistoryPreferences(keep.IsChecked == true, search.IsChecked == true);
            if (next == record.Preferences) return;
            if (!record.SetPreferences(next))
                ErrorLog.Warn("Couldn't save the conversation history choice on this PC; it applies until Martlet restarts.");
            // Your other computers take the choice on their next check (the conversation-history shared setting).
            services.QueueSettingsSync();
            search.IsEnabled = memoryOn && next.Keep;
            status.Text = record.Describe(services.Settings?.Memory);
        }
        foreach (var choice in new[] { keep, search })
        {
            choice.Checked += (_, _) => Save();
            choice.Unchecked += (_, _) => Save();
        }
        // The counts appear once the record has been read (in the background, the first time).
        if (!record.Store.Loaded)
            _ = record.Store.LoadAsync().ContinueWith(_ => status.Dispatcher.InvokeAsync(() => status.Text = record.Describe(services.Settings?.Memory)),
                TaskScheduler.Default);
        return Card(Heading("Conversation history"), status, keep,
            HelpTip.Explain("Each exchange (what you typed or said and Martlet's reply) is kept in your record, the same on all your " +
                "computers. When you mention an earlier " +
                "conversation, like \"remember when...\" or \"what did we talk about yesterday?\", Martlet brings back what was said. " +
                "Screen glances and what this PC plays are never recorded.", new Thickness(24, 2, 0, 0), "ConversationHistory", "conversation history"),
            search,
            HelpTip.Explain("With a Thinking model that uses tools, Martlet can also look things up in the record whenever it thinks that " +
                "helps. Its search tool makes every request a little longer, so the first reply of a conversation may start a " +
                "little later.", new Thickness(24, 2, 0, 0), "ConversationSearch", "searching the record"),
            Row(PageButton("Open conversations", services.OpenHistory, id: "OpenHistory")));
    }
}
