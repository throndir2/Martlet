using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Avatar.Hosting;

namespace Martlet.Desktop;

/// <summary>Companion › Eyes › Where the character looks (and the character's right-click Eyes menu): its usual gaze
/// (as the personality decides, the default, or your choice: your mouse, your mouse when it's near, straight ahead or the window
/// you're using) and whether the character may change where it looks in its replies. Saved on this PC in
/// talk-preferences.json.</summary>
public partial class MainWindow
{
    private TextBlock? characterGazeNow;

    private void WireCharacterGaze() => avatar.Gaze.Changed += () => Dispatcher.InvokeAsync(() =>
    {
        if (characterGazeNow is null) return;
        characterGazeNow.Text = avatar.Gaze.Looking;
        AutomationProperties.SetHelpText(characterGazeNow, avatar.Gaze.LastLookText ?? "");
    });

    /// <summary>A choice from the character's Eyes menu: its usual gaze (<c>look-personality</c>, <c>look-mouse</c>...) or whether
    /// it may change where it looks (<c>look-free-on</c>, <c>look-free-off</c>). Saved like the same choice on Companion ›
    /// Character, which the overlay then shows checked.</summary>
    private void ChooseCharacterGaze(string action)
    {
        if (action == RendererRequest.FreeOn || action == RendererRequest.FreeOff)
        {
            SaveTalk(Talk with { GazeFree = action == RendererRequest.FreeOn }, render: openTab == CompanionTab.Eyes);
            return;
        }
        var choice = action[RendererRequest.LookPrefix.Length..];
        if (!RendererGaze.Choices.Contains(choice)) return;
        SaveTalk(Talk with { GazeUsual = choice == RendererGaze.Personality ? null : CharacterGaze.ModeOf(choice) },
            render: openTab == CompanionTab.Eyes);
    }

    private Border CharacterGazeCard()
    {
        var prefs = Talk;
        var personality = characterTemperaments.For(homeSettings?.Companion?.ActivePersonaId)?.Gaze;
        var stack = new List<UIElement>
        {
            Heading("Where the character looks"),
            Note("What the character's eyes do when nothing else draws them. You can also change it on the character's right-click " +
                "menu, under Eyes.", new Thickness(0, 0, 0, 8))
        };
        var personalityChoice = Choice("CharacterGaze", "As the personality decides (recommended)",
            "Your Thinking model picks it from the personality, with the touch temperament (on the Touch page): " +
            (personality is { } decided ? $"now {CharacterGaze.Label(decided)}." : "not decided yet, so it follows your mouse."),
            prefs.GazeUsual is null, "CharacterGaze-personality");
        personalityChoice.Checked += (_, _) => { if (Talk.GazeUsual is not null) SaveTalk(Talk with { GazeUsual = null }, render: true); };
        stack.Add(personalityChoice);
        foreach (var (mode, word, _, label) in CharacterGaze.Modes)
        {
            var option = Choice("CharacterGaze", label, mode switch
            {
                GazeMode.Near => "It looks at your mouse pointer only while it is near the character, and otherwise straight ahead.",
                GazeMode.Ahead => "It looks straight ahead and doesn't follow your mouse.",
                GazeMode.Window => "It watches what you do in the window you're using: where you move the mouse or type in it, " +
                    "and its middle until you do.",
                _ => "Its head and eyes follow your mouse pointer everywhere."
            }, prefs.GazeUsual == mode, "CharacterGaze-" + word);
            option.Checked += (_, _) => { if (Talk.GazeUsual != mode) SaveTalk(Talk with { GazeUsual = mode }, render: true); };
            stack.Add(option);
        }
        var free = new CheckBox
        {
            Content = "Let the character change where it looks", IsChecked = prefs.GazeFree, Margin = new Thickness(0, 4, 0, 4)
        };
        AutomationProperties.SetAutomationId(free, "CharacterGazeFree");
        free.Checked += (_, _) => { if (!Talk.GazeFree) SaveTalk(Talk with { GazeFree = true }, render: true); };
        free.Unchecked += (_, _) => { if (Talk.GazeFree) SaveTalk(Talk with { GazeFree = false }, render: true); };
        stack.Add(free);
        stack.Add(Note("In its replies the character can look away, follow your mouse or watch your window, and it stays that way " +
            "until a reply changes it again or you choose here. Each reply is told its usual gaze, and a note says when its eyes " +
            "do something else. A touch can also turn its eyes to your mouse for a moment (Touch temperament, on the Touch page).",
            new Thickness(0, 0, 0, 6)));
        characterGazeNow = Note(avatar.Gaze.Looking, new Thickness(0, 2, 0, 0));
        AutomationProperties.SetAutomationId(characterGazeNow, "CharacterGazeNow");
        AutomationProperties.SetHelpText(characterGazeNow, avatar.Gaze.LastLookText ?? "");
        AutomationProperties.SetLiveSetting(characterGazeNow, AutomationLiveSetting.Polite);
        stack.Add(characterGazeNow);
        return Card([.. stack]);
    }
}
