using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Avatar.Hosting;

namespace Martlet.Desktop;

/// <summary>Companion › Eyes, the UI only: Now, where the character looks, then where its eyes are (for the emotes drawn over
/// them, EyesPage.Face.cs). What the page needs from the app is <see cref="IEyesPageServices"/>; its decisions are in
/// <see cref="EyesPageModel"/>. MainWindow keeps one page for the window's life and updates the lines of the page last drawn
/// (<see cref="ShowGaze"/> and <see cref="ShowEyes"/>).</summary>
internal sealed partial class EyesPage(IEyesPageServices services)
{
    private TextBlock? gazeNow;

    /// <summary>Draws the page's cards into <paramref name="page"/>: Now first, then where the character looks and where the face is.</summary>
    internal void Render(Panel page)
    {
        services.FollowActions();
        var model = new EyesPageModel(services);
        page.Children.Add(GazeCard(model));
        page.Children.Add(FaceCard(model));
        page.Children.Insert(0, PageNowCard(model.NowText, null, "EyesNow", model.NowProblem));
    }

    /// <summary>Shows where the character looks now on the page last drawn.</summary>
    internal void ShowGaze()
    {
        if (gazeNow is null) return;
        gazeNow.Text = services.Looking;
        AutomationProperties.SetHelpText(gazeNow, services.LastLook ?? "");
    }

    /// <summary>Companion › Eyes › Where the character looks (and the character's right-click Eyes menu): its usual gaze (as the
    /// personality decides, the default, or your choice: your mouse, your mouse when it's near, straight ahead or the window you're
    /// using) and whether the character may change where it looks in its replies. Saved on this PC in talk-preferences.json.</summary>
    private Border GazeCard(EyesPageModel model)
    {
        var prefs = services.Talk;
        var stack = new List<UIElement>
        {
            Heading("Where the character looks"),
            HelpTip.Explain("What the character's eyes do when nothing else draws them. You can also change it on the character's " +
                "right-click menu, under Eyes.", new Thickness(0, 0, 0, 8), "CharacterGazeUsual", "where the character looks",
                wiki: "The-Character#where-the-character-looks")
        };
        var personalityChoice = Choice("CharacterGaze", "As the personality decides (recommended)", model.PersonalityNote,
            prefs.GazeUsual is null, "CharacterGaze-personality");
        personalityChoice.Checked += (_, _) => { if (services.Talk.GazeUsual is not null) services.SaveTalk(services.Talk with { GazeUsual = null }); };
        stack.Add(personalityChoice);
        foreach (var (mode, word, _, label) in CharacterGaze.Modes)
        {
            var option = Choice("CharacterGaze", label, EyesPageModel.ModeNote(mode), prefs.GazeUsual == mode, "CharacterGaze-" + word);
            option.Checked += (_, _) => { if (services.Talk.GazeUsual != mode) services.SaveTalk(services.Talk with { GazeUsual = mode }); };
            stack.Add(option);
        }
        var free = new CheckBox
        {
            Content = "Let the character change where it looks", IsChecked = prefs.GazeFree, Margin = new Thickness(0, 4, 0, 4)
        };
        AutomationProperties.SetAutomationId(free, "CharacterGazeFree");
        free.Checked += (_, _) => { if (!services.Talk.GazeFree) services.SaveTalk(services.Talk with { GazeFree = true }); };
        free.Unchecked += (_, _) => { if (services.Talk.GazeFree) services.SaveTalk(services.Talk with { GazeFree = false }); };
        stack.Add(free);
        stack.Add(HelpTip.Explain("In its replies the character can look away, follow your mouse or watch your window, and it stays that way " +
            "until a reply changes it again or you choose here. Each reply is told its usual gaze, and a note says when its eyes " +
            "do something else. A touch can also turn its eyes to your mouse for a moment (Touch temperament, on the Touch page).",
            new Thickness(0, 0, 0, 6), "CharacterGaze", "where the eyes look"));
        gazeNow = Note(services.Looking, new Thickness(0, 2, 0, 0));
        AutomationProperties.SetAutomationId(gazeNow, "CharacterGazeNow");
        AutomationProperties.SetHelpText(gazeNow, services.LastLook ?? "");
        AutomationProperties.SetLiveSetting(gazeNow, AutomationLiveSetting.Polite);
        stack.Add(gazeNow);
        return Card([.. stack]);
    }
}
