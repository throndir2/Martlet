using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Avatar.Hosting;
using Martlet.Avatars;
using Martlet.Core.Cluster;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Desktop;

/// <summary>Companion's Personality and Memory pages.</summary>
public partial class MainWindow
{
    // ---------- personality ----------

    private void RenderPersonalityTab(Panel page)
    {
        var companion = homeSettings?.Companion;
        var persona = companion?.ActivePersona;
        var count = companion?.Personas.Count ?? 1;
        page.Children.Add(PageNowCard(persona is null
            ? "Default persona."
            : $"{persona.Name}{(count > 1 ? $", one of {count} personas" : "")}.", null));

        page.Children.Add(Card(Heading("Personas"),
            HelpTip.Explain("Create, edit or switch personas. Changes save on their own, and the next message uses the chosen persona.",
                new Thickness(0, 0, 0, 8), "PersonasAbout", "personas", wiki: "Personality-and-Characters#personas"),
            Row(PageButton("Edit personality", () => Companion_Click(this, new RoutedEventArgs()), primary: true, id: "OpenCompanion"))));

        page.Children.Add(ProfilesLinkCard());

        page.Children.Add(Card(Heading("Character cards"),
            Note("Import a PNG, JSON or CHARX character card to create a persona.", new Thickness(0, 0, 0, 8)),
            Row(PageButton("Import a character card", () => OpenCompanionWindowAsync(importCard: true).Forget(), id: "ImportCharacterCard"))));

        page.Children.Add(Card(Heading("Prompts"),
            HelpTip.Explain("See and edit Martlet's own instructions around the persona. The persona is wrapped in them, along with lore, " +
                "memory, reply length and more.", new Thickness(0, 0, 0, 8), "PromptsAround", "prompts",
                wiki: "Personality-and-Characters#prompts"),
            Row(PageButton("Edit prompts", () => OpenCompanion(CompanionTab.Prompts), id: "OpenPrompts"))));
    }

    // ---------- memory ----------

    private void RenderMemoryTab(Panel page)
    {
        var on = homeSettings?.Memory?.Enabled == true;
        page.Children.Add(Card(Heading("Now"),
            new TextBlock { Text = on ? "On" : "Off", FontSize = 15, TextWrapping = TextWrapping.Wrap },
            Note(on
                ? "Martlet remembers lasting facts you share and uses them later. Everything stays on this PC; manage facts in Manage memory."
                : "Martlet doesn't remember or recall anything between conversations: turn memory on in Manage memory.",
                new Thickness(0, 2, 0, 8)),
            Row(PageButton("Manage memory", () => Memory_Click(this, new RoutedEventArgs()), primary: !on, id: "OpenMemory"))));
        if (conversation?.MemoryBrief is { } brief) page.Children.Add(MemoryBriefCard(brief, on));
        if (conversation?.Meaning is { } meaning) page.Children.Add(MeaningSearchCard(meaning));
        if (conversationHistory is { } record) page.Children.Add(HistoryCard(record, on));
    }

    /// <summary>Companion › Memory › Conversation history: whether Martlet keeps a record of conversations (on by default while
    /// memory is on; the same on all your computers) and may search it on its own (search_conversations, off by default), what it
    /// holds and the window to read, search and delete it. Each choice saves at once (conversation-history.json, shared as
    /// conversation-history).</summary>
    private Border HistoryCard(DesktopConversationHistory record, bool memoryOn)
    {
        var prefs = record.Preferences;
        var status = Note(record.Describe(homeSettings?.Memory), new Thickness(0, 2, 0, 8));
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
            QueueSettingsSync();
            search.IsEnabled = memoryOn && next.Keep;
            status.Text = record.Describe(homeSettings?.Memory);
        }
        foreach (var choice in new[] { keep, search })
        {
            choice.Checked += (_, _) => Save();
            choice.Unchecked += (_, _) => Save();
        }
        // The counts appear once the record has been read (in the background, the first time).
        if (!record.Store.Loaded)
            _ = record.Store.LoadAsync().ContinueWith(_ => Dispatcher.InvokeAsync(() => status.Text = record.Describe(homeSettings?.Memory)),
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
            Row(PageButton("Open conversations", History_Click, id: "OpenHistory")));
    }

}
