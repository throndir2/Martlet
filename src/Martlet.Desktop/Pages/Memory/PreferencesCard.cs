using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Core.Activities;
using Martlet.Core.Preferences;
using Martlet.Presentation;

namespace Martlet.Desktop;

/// <summary>Companion › Memory › Your preferences, the UI only: the user's own rules for how Martlet behaves (docs/CONVERSATION.md,
/// "Your preferences"), with where each applies, and Add, Edit and Remove. It reads and changes the list only through its owner,
/// <see cref="UserPreferences"/>, and follows changes the reply's manage_preferences tool, MCP or another computer made. The list
/// is the same on all your computers (the user-preferences account setting); <c>queueSync</c> lets them take a change at once.
/// Automation IDs: PreferencesStatus, PreferencesList, PreferenceRow-&lt;id&gt;, PreferenceEdit-&lt;id&gt;,
/// PreferenceRemove-&lt;id&gt;, PreferenceNewText, PreferenceWhere, PreferenceGame, PreferenceAdd, PreferenceCancel and
/// PreferenceProblem.</summary>
internal sealed class PreferencesCard(UserPreferences preferences, Action queueSync)
{
    private const string Everywhere = "Everywhere", WhileGaming = "While gaming", OneGame = "In one game";

    private readonly TextBlock status = Status(preferences.Status(), "PreferencesStatus");
    private readonly StackPanel list = new() { Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBox text = new() { MaxLength = UserPreferences.MaximumTextCharacters, MinWidth = 320, TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox where = new() { ItemsSource = new[] { Everywhere, WhileGaming, OneGame }, SelectedIndex = 0, MinWidth = 140 };
    private readonly TextBox game = new() { MaxLength = ActivityScope.MaximumCharacters - ActivityScope.Gaming.Length - 1, MinWidth = 180,
        Visibility = Visibility.Collapsed };
    private readonly TextBlock problem = Note("", new Thickness(0, 4, 0, 0));
    private Button? add, cancel;
    // The preference being edited, or null while the box adds a new one.
    private string? editing;

    /// <summary>The card, following the list while it is on the page.</summary>
    internal Border Build()
    {
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        status.Margin = new Thickness(0, 2, 0, 0);
        AutomationProperties.SetAutomationId(list, "PreferencesList");
        AutomationProperties.SetAutomationId(text, "PreferenceNewText");
        AutomationProperties.SetName(text, "Preference");
        InputHint.SetText(text, "No hints unless I ask");
        AutomationProperties.SetAutomationId(where, "PreferenceWhere");
        AutomationProperties.SetName(where, "Where it applies");
        AutomationProperties.SetAutomationId(game, "PreferenceGame");
        AutomationProperties.SetName(game, "Game");
        InputHint.SetText(game, "The game's name");
        AutomationProperties.SetAutomationId(problem, "PreferenceProblem");
        problem.Visibility = Visibility.Collapsed;
        where.SelectionChanged += (_, _) => game.Visibility = (string?)where.SelectedItem == OneGame ? Visibility.Visible : Visibility.Collapsed;
        add = PageButton("Add", Save, primary: true, id: "PreferenceAdd");
        cancel = PageButton("Cancel", () => Edit(null), id: "PreferenceCancel");
        cancel.Visibility = Visibility.Collapsed;
        var editor = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        foreach (var part in new FrameworkElement[] { text, where, game })
        {
            part.Margin = new Thickness(0, 0, 10, 6);
            part.VerticalAlignment = VerticalAlignment.Center;
            editor.Children.Add(part);
        }
        Show();
        var card = Card(Heading("Your preferences"), status,
            HelpTip.Explain("Tell Martlet how to behave, in your own words, and it keeps to it from then on, on all your computers. " +
                "You can also just say it while you talk, such as \"stop giving hints\" or \"no spoilers for Elden Ring\", and " +
                "Martlet saves it here itself; say \"you can give hints again\" to change it back. Each reply, screen glance and " +
                "check-in gets the preferences that apply now at its end, so the first word of a reply never waits for them.",
                new Thickness(0, 2, 0, 0), "UserPreferences", "your preferences", wiki: "Memory#your-preferences"),
            list, editor, Row(add, cancel), problem);
        AutomationProperties.SetAutomationId(card, "PreferencesCard");
        void Follow() => card.Dispatcher.InvokeAsync(Show);
        card.Loaded += (_, _) => preferences.Changed += Follow;
        card.Unloaded += (_, _) => preferences.Changed -= Follow;
        return card;
    }

    // The status line and one row for each preference, oldest first.
    private void Show()
    {
        status.Text = preferences.Status();
        list.Children.Clear();
        foreach (var preference in preferences.All)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6), LastChildFill = true };
            AutomationProperties.SetAutomationId(row, "PreferenceRow-" + preference.Id);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            var edit = PageButton("Edit", () => Edit(preference.Id), link: true, id: "PreferenceEdit-" + preference.Id);
            var remove = PageButton("Remove", () => Remove(preference.Id), link: true, id: "PreferenceRemove-" + preference.Id);
            edit.Margin = new Thickness(10, 0, 0, 0);
            remove.Margin = new Thickness(10, 0, 0, 0);
            buttons.Children.Add(edit);
            buttons.Children.Add(remove);
            DockPanel.SetDock(buttons, Dock.Right);
            row.Children.Add(buttons);
            var words = new StackPanel();
            words.Children.Add(new TextBlock { Text = preference.Text, TextWrapping = TextWrapping.Wrap });
            words.Children.Add(Note($"{Capital(preference.Where)} · {(preference.Source == UserPreferences.Said ? "you said it" : preference.Source == UserPreferences.Typed ? "you typed it" : "set by an assistant")}",
                new Thickness(0, 1, 0, 0)));
            row.Children.Add(words);
            list.Children.Add(row);
        }
        list.Visibility = list.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (editing is not null && preferences.Find(editing) is null) Edit(null);
    }

    // Puts preference id in the box to change it (null: back to adding a new one).
    private void Edit(string? id)
    {
        var preference = id is null ? null : preferences.Find(id);
        editing = preference?.Id;
        text.Text = preference?.Text ?? "";
        where.SelectedItem = preference?.Activity is not { } activity ? Everywhere : ActivityScope.Name(activity) is null ? WhileGaming : OneGame;
        game.Text = ActivityScope.Name(preference?.Activity) ?? "";
        if (add is not null) add.Content = editing is null ? "Add" : "Save";
        if (add is not null) AutomationProperties.SetName(add, editing is null ? "Add" : "Save");
        if (cancel is not null) cancel.Visibility = editing is null ? Visibility.Collapsed : Visibility.Visible;
        Problem(null);
        if (editing is not null) text.Focus();
    }

    private void Save()
    {
        string? activity = (string?)where.SelectedItem switch
        {
            WhileGaming => ActivityScope.Gaming,
            OneGame => ActivityScope.Game(game.Text),
            _ => null
        };
        if ((string?)where.SelectedItem == OneGame && activity is null)
        {
            Problem("Type the game's name.");
            return;
        }
        var change = editing is null
            ? preferences.Add(text.Text, activity, UserPreferences.Typed)
            : preferences.Update(editing, text.Text, setActivity: true, activity, UserPreferences.Typed);
        if (change.Problem is { } refused)
        {
            Problem(refused);
            return;
        }
        if (!change.Changed) Problem(editing is null ? "You already have this preference." : null);
        else queueSync();
        if (change.Changed || editing is not null) Edit(null);
        Show();
    }

    private void Remove(string id)
    {
        var change = preferences.Remove(id);
        Problem(change.Problem);
        if (change.Changed) queueSync();
        Show();
    }

    private void Problem(string? text) => ShowStatusLine(problem, text);

    private static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
