using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Conversation.Guides;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>Companion › App guides: the Game state card (docs/CONVERSATION.md, "Where you are in a game"). It shows what
/// <see cref="GameProgress"/> knows about each game (its current playthrough and its fields), the Save game state box and a
/// Forget button for each game. Only the card's own panel is drawn again when the state changes, so the page keeps its focus.</summary>
internal sealed partial class AppGuidesPage
{
    private StackPanel? gameStatePanel;

    private Border GameStateCard()
    {
        gameStatePanel = new StackPanel();
        RenderGameState();
        return Card(Heading("Game state"),
            HelpTip.Explain("While you play, Martlet keeps track of where you are, your goal, your character, build and equipment, " +
                "for each game and playthrough, from the screen summary. Tell it when it is wrong (\"I'm level 12 now\" or \"I'm " +
                "playing a new save\"): your word wins over what it saw. It is saved and shared with your other computers unless " +
                "you turn that off.", new Thickness(0, 0, 0, 8), "GameStateAbout", "game state", wiki: "App-Guides#where-you-are-in-a-game"),
            gameStatePanel);
    }

    /// <summary>Draws the Game state card's contents again (the state changed).</summary>
    internal void RenderGameState()
    {
        if (gameStatePanel is not { } panel) return;
        panel.Children.Clear();
        if (services.GameProgress is not { } progress)
        {
            var off = Note("Martlet keeps the game state while App guides are on and a conversation can run.", new Thickness(0, 4, 0, 0));
            AutomationProperties.SetAutomationId(off, "GameStateNow");
            panel.Children.Add(off);
            return;
        }
        var save = new CheckBox { Content = "Save the game state and share it with my other computers", IsChecked = progress.Saving, IsEnabled = progress.Saved };
        AutomationProperties.SetAutomationId(save, "GameStateSave");
        save.Checked += (_, _) => progress.SetSaving(true);
        save.Unchecked += (_, _) => progress.SetSaving(false);
        panel.Children.Add(save);
        var games = progress.ReadAll();
        var now = Note(NowText(progress, games.Count), new Thickness(0, 6, 0, 0));
        AutomationProperties.SetAutomationId(now, "GameStateNow");
        AutomationProperties.SetLiveSetting(now, AutomationLiveSetting.Polite);
        panel.Children.Add(now);
        foreach (var game in games) panel.Children.Add(GameRow(progress, game));
    }

    private static string NowText(GameProgress progress, int games) =>
        (games == 0 ? "No game state yet: start a game while the screen summary is on, or tell Martlet where you are."
            : $"{games} game{(games == 1 ? "" : "s")}" + (progress.CurrentGame is { } current ? $"; now: {current}." : ".")) +
        (!progress.Saved ? " Kept in memory only." : progress.Saving ? progress.SaveProblem is { } why ? $" Not saved: {why}." : " Saved."
            : " Not saved: kept until Martlet closes.");

    private DockPanel GameRow(GameProgress progress, GameState game)
    {
        var key = GameStateFile.KeyOf(game.Game);
        var row = new DockPanel { Margin = new Thickness(0, 8, 0, 4), LastChildFill = true };
        var forget = PageButton("Forget", () =>
        {
            var forgot = progress.Forget(game.Game);
            services.Say(forgot ? $"Martlet forgot where you are in {game.Game}." : $"Martlet knew nothing about {game.Game}.");
        }, id: "GameStateForget-" + key);
        forget.ToolTip = "Delete everything Martlet keeps about this game, on every computer.";
        forget.VerticalAlignment = VerticalAlignment.Top;
        DockPanel.SetDock(forget, Dock.Right);
        row.Children.Add(forget);
        var text = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        var name = new TextBlock
        {
            Text = $"{game.Game} · playthrough {game.Playthrough}" + (game.Playthroughs.Count > 1 ? $" (of {game.Playthroughs.Count})" : "") +
                $" · updated {game.UpdatedAt.ToLocalTime():g}",
            FontSize = 15,
            TextWrapping = TextWrapping.Wrap
        };
        AutomationProperties.SetAutomationId(name, "GameStateGame-" + key);
        text.Children.Add(name);
        if (game.Fields.Count == 0) text.Children.Add(Note("Nothing noted yet.", new Thickness(0)));
        foreach (var field in game.Fields)
        {
            var line = Note($"{field.Label}: {field.Value} ({(field.ByUser ? "you said" : "seen")}" +
                $"{(field.SeenThisSession ? "" : ", earlier session")}, {field.At.ToLocalTime():g})", new Thickness(0));
            AutomationProperties.SetAutomationId(line, $"GameStateField-{key}-{field.Name}");
            text.Children.Add(line);
        }
        row.Children.Add(text);
        return row;
    }
}
