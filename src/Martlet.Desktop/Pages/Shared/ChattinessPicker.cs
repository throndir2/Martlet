using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>Companion's chattiness choice: Vision's How often it comments, and the same choice under Listening › Watch along.
/// Quiet, Normal, Chatty or Martlet decides, then what the choice means and, while a conversation runs, the level Martlet
/// picked. Both pages save the same talk choice (ScreenChattiness).</summary>
internal static class ChattinessPicker
{
    /// <summary>The choices, in the order of <see cref="ChattinessChoice"/>.</summary>
    internal static readonly string[] Choices = ["Quiet", "Normal", "Chatty", "Martlet decides"];

    internal const string About = "Martlet decides: Martlet picks Quiet, Normal or Chatty itself and switches as it goes, " +
        "from what's happening and what you say. Ask it to hush, or to tell you what it thinks, and it follows. It starts at Normal.";

    /// <summary>The choice (<paramref name="id"/>) and its status line (<paramref name="id"/>Status). <paramref name="saved"/> reads
    /// the saved choice; a different choice calls <paramref name="save"/> with its index.</summary>
    internal static UIElement[] Create(string id, Func<int> saved, Chattiness? decided, Action<int> save)
    {
        var choice = ChattinessTags.Choice(saved());
        var chatty = new ComboBox { Width = 280, ItemsSource = Choices, SelectedIndex = (int)choice, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(chatty, "How often Martlet comments");
        AutomationProperties.SetAutomationId(chatty, id);
        chatty.SelectionChanged += (_, _) =>
        {
            if (chatty.SelectedIndex >= 0 && chatty.SelectedIndex != saved()) save(chatty.SelectedIndex);
        };
        var status = Note(Status(choice, decided), new Thickness(0, 6, 0, 0));
        AutomationProperties.SetAutomationId(status, id + "Status");
        return [chatty, status];
    }

    /// <summary>What <paramref name="choice"/> means and, for Martlet decides, the level it picked (<paramref name="decided"/>).</summary>
    internal static string Status(ChattinessChoice choice, Chattiness? decided) => choice switch
    {
        ChattinessChoice.MartletDecides => About +
            (decided is { } level ? $" Right now it is {ChattinessTags.Name(level)}." : ""),
        ChattinessChoice.Quiet => "Quiet: Martlet speaks up only when something is clearly remarkable.",
        ChattinessChoice.Chatty => "Chatty: Martlet reacts more often, but still stays quiet when nothing is new.",
        _ => "Normal: Martlet says something when it's worth saying."
    };
}
