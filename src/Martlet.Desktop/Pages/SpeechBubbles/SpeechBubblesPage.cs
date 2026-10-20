using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Core.Settings;
using Martlet.Presentation;

namespace Martlet.Desktop;

/// <summary>Companion › Speech bubbles, the UI only: Now, where Martlet's words show (speech bubbles, subtitles, both or Off), then
/// where the bubble goes. The same saved choices as the character window's, applied from Martlet's next sentence. What the page needs
/// from the app is <see cref="ISpeechBubblesPageServices"/>; its words are in <see cref="SpeechBubblesPageModel"/>. MainWindow calls
/// <see cref="Show"/> when the saved choices change, whichever window changed them.</summary>
internal sealed class SpeechBubblesPage(ISpeechBubblesPageServices services)
{
    private (TextBlock Text, TextBlock Problem)? speechBubblesNow;
    private readonly Dictionary<(bool Bubbles, bool Subtitles), RadioButton> speechDisplayChoices = [];
    private ComboBox? bubblePlacementChoice;
    private TextBox? bubbleOffsetX, bubbleOffsetY;
    private TextBlock? speechDisplayText;
    private Button? speechPreviewButton;
    private bool showingSpeechDisplay;

    /// <summary>Companion › Speech bubbles: Now, where Martlet's words show (with Off), then where the bubble goes.</summary>
    internal void Render(Panel page)
    {
        var prefs = services.Preferences;
        var (card, text, problem) = LiveNowCard("SpeechBubblesNow", MainWindow.SpeechBubblesNow(prefs),
            MainWindow.SpeechBubblesProblem(prefs, services.AvatarShowing));
        speechBubblesNow = (text, problem);
        page.Children.Add(card);
        page.Children.Add(SpeechDisplayChoiceCard(prefs));
        page.Children.Add(SpeechDisplayCard());
    }

    /// <summary>A Now card whose line and problem change while the page shows (<see cref="ShowNow"/>): the problem's text block
    /// is always there, collapsed while there is no problem.</summary>
    private static (Border Card, TextBlock Text, TextBlock Problem) LiveNowCard(string id, string text, string? problem)
    {
        var now = new StackPanel();
        now.Children.Add(Heading("Now"));
        var status = new TextBlock { FontSize = 15, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(status, id);
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        now.Children.Add(status);
        var problemText = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        problemText.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
        AutomationProperties.SetAutomationId(problemText, id + "Problem");
        now.Children.Add(problemText);
        ShowNow(status, problemText, text, problem);
        return (Card(now), status, problemText);
    }

    private static void ShowNow(TextBlock status, TextBlock problemText, string text, string? problem)
    {
        status.Text = text;
        problemText.Text = problem ?? "";
        problemText.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Where Martlet's spoken words show: speech bubbles, subtitles, both or Off. The same saved choices as the
    /// character window's, applied from Martlet's next sentence.</summary>
    private Border SpeechDisplayChoiceCard(SpeechDisplayPreferences prefs)
    {
        speechDisplayChoices.Clear();
        var stack = new List<UIElement> { Heading("Where Martlet's words show") };
        foreach (var (bubbles, subtitles, title, detail, id) in SpeechBubblesPageModel.Choices)
        {
            var option = Choice("SpeechDisplay", title, detail, prefs.SpeechBubbles == bubbles && prefs.Subtitles == subtitles, id);
            option.Checked += (_, _) => ChooseSpeechDisplay(bubbles, subtitles);
            speechDisplayChoices[(bubbles, subtitles)] = option;
            stack.Add(option);
        }
        return Card([.. stack]);
    }

    private void ChooseSpeechDisplay(bool bubbles, bool subtitles)
    {
        if (showingSpeechDisplay) return;
        var prefs = services.Preferences;
        if (prefs.SpeechBubbles == bubbles && prefs.Subtitles == subtitles) return;
        if (!services.Save(prefs with { SpeechBubbles = bubbles, Subtitles = subtitles }))
            Show("Applied for now, but couldn't save your speech display settings.");
    }

    /// <summary>Follows the saved choices on the page's main choice and Now line, whichever window changed them.</summary>
    private void ShowSpeechDisplayChoice(SpeechDisplayPreferences prefs)
    {
        if (speechDisplayChoices.TryGetValue((prefs.SpeechBubbles, prefs.Subtitles), out var chosen)) chosen.IsChecked = true;
        if (speechBubblesNow is var (text, problem))
            ShowNow(text, problem, MainWindow.SpeechBubblesNow(prefs), MainWindow.SpeechBubblesProblem(prefs, services.AvatarShowing));
    }

    /// <summary>Companion › Speech bubbles' configuration: where the bubble goes, following the character's head (default) or
    /// staying in one place, nudged by horizontal and vertical offsets; the same saved choices as the character settings window,
    /// applied from Martlet's next sentence. Whether bubbles and subtitles show at all is the page's main choice
    /// (<see cref="SpeechDisplayChoiceCard"/>).</summary>
    private Border SpeechDisplayCard()
    {
        const string BubbleFollows = SpeechBubblesPageModel.BubbleFollows, BubbleStays = SpeechBubblesPageModel.BubbleStays;
        var prefs = services.Preferences;
        bubblePlacementChoice = new ComboBox
        {
            ItemsSource = new[] { BubbleFollows, BubbleStays }, MinHeight = 30, MinWidth = 260, MaxWidth = 420,
            HorizontalAlignment = HorizontalAlignment.Left, SelectedItem = prefs.StaticBubble ? BubbleStays : BubbleFollows
        };
        AutomationProperties.SetName(bubblePlacementChoice, "Bubble position");
        AutomationProperties.SetAutomationId(bubblePlacementChoice, "SetupCharacterBubblePlacement");
        bubblePlacementChoice.SelectionChanged += (_, _) => SaveSpeechDisplay();
        TextBox Offset(string name, string id, double value)
        {
            var box = new TextBox { Width = 90, MaxLength = 6, Text = SpeechBubblesPageModel.Offset(value) };
            AutomationProperties.SetName(box, name);
            AutomationProperties.SetAutomationId(box, id);
            box.TextChanged += (_, _) => SaveSpeechDisplay();
            return box;
        }
        bubbleOffsetX = Offset("Bubble horizontal offset", "SetupCharacterBubbleOffsetX", prefs.BubbleOffsetX);
        bubbleOffsetY = Offset("Bubble vertical offset", "SetupCharacterBubbleOffsetY", prefs.BubbleOffsetY);
        InputHint.SetText(bubbleOffsetX, "20");
        InputHint.SetText(bubbleOffsetY, "-40");
        var offsets = new WrapPanel
        {
            Margin = new Thickness(0, 8, 0, 0),
            Children =
            {
                new Label { Content = "_Horizontal offset", Target = bubbleOffsetX, Padding = new Thickness(0, 4, 8, 4) }, bubbleOffsetX,
                new Label { Content = "_Vertical offset", Target = bubbleOffsetY, Padding = new Thickness(18, 4, 8, 4) }, bubbleOffsetY
            }
        };

        speechDisplayText = Note("", new Thickness(0, 8, 0, 0));
        AutomationProperties.SetAutomationId(speechDisplayText, "SetupCharacterSpeechDisplay");
        speechPreviewButton = PageButton("Preview a speech bubble", () => PreviewSpeechBubbleAsync().Forget(), id: "SetupCharacterPreviewBubble");
        Show();
        return Card(Heading("Where the bubble goes"),
            Note("Speech bubbles show Martlet's spoken words beside the character; subtitles show them at the bottom of the active screen.",
                new Thickness(0, 0, 0, 0)),
            new Label { Content = "Bubble _position", Target = bubblePlacementChoice, Padding = new Thickness(0, 12, 0, 4) },
            bubblePlacementChoice, offsets,
            HelpTip.Explain("The offsets move the bubble in pixels, positive to the right and down. Following the character, the bubble sits " +
                "beside its head on whichever side has room and keeps up as you move or zoom; staying in one place, the offsets are " +
                "measured from the top-left of the character's screen.", new Thickness(0, 6, 0, 0), "SpeechBubbleOffsets", "the bubble's offsets",
                wiki: "The-Character#speech-bubbles-and-subtitles"),
            speechDisplayText, Row(speechPreviewButton));
    }

    private void SaveSpeechDisplay()
    {
        if (showingSpeechDisplay || bubblePlacementChoice is null) return;
        var prefs = services.Preferences with
        {
            StaticBubble = ReferenceEquals(bubblePlacementChoice.SelectedItem, SpeechBubblesPageModel.BubbleStays)
        };
        var x = SpeechBubblesPageModel.ReadOffset(bubbleOffsetX?.Text);
        var y = SpeechBubblesPageModel.ReadOffset(bubbleOffsetY?.Text);
        if (x is double.NaN || y is double.NaN)
        {
            // Leave the half-typed text alone (such as a lone "-"); just say what's expected.
            if (speechDisplayText is not null) speechDisplayText.Text = SpeechBubblesPageModel.OffsetsProblem;
            return;
        }
        prefs = prefs with { BubbleOffsetX = x ?? prefs.BubbleOffsetX, BubbleOffsetY = y ?? prefs.BubbleOffsetY };
        var saved = services.Save(prefs);
        if (!saved) Show("Applied for now, but couldn't save your speech display settings.");
    }

    /// <summary>Follows the saved choices, whichever window changed them (nothing before the page first draws).</summary>
    internal void Show(string? outcome = null)
    {
        if (speechDisplayText is null) return;
        var prefs = services.Preferences;
        showingSpeechDisplay = true;
        try
        {
            ShowSpeechDisplayChoice(prefs);
            if (bubblePlacementChoice is not null)
                bubblePlacementChoice.SelectedItem = prefs.StaticBubble ? SpeechBubblesPageModel.BubbleStays : SpeechBubblesPageModel.BubbleFollows;
            // Only rewrite an offset box when its number differs, so typing in it isn't interrupted.
            foreach (var (box, value) in new[] { (bubbleOffsetX, prefs.BubbleOffsetX), (bubbleOffsetY, prefs.BubbleOffsetY) })
                if (box is not null && !SpeechBubblesPageModel.Shows(box.Text, value)) box.Text = SpeechBubblesPageModel.Offset(value);
        }
        finally { showingSpeechDisplay = false; }
        var showing = services.AvatarShowing;
        if (speechPreviewButton is not null) speechPreviewButton.IsEnabled = showing && prefs.SpeechBubbles;
        speechDisplayText.Text = SpeechBubblesPageModel.DisplayLine(prefs, showing, outcome);
    }

    private async Task PreviewSpeechBubbleAsync()
    {
        var shown = await services.PreviewAsync("Hi! While I talk, what I say shows up here.");
        if (services.Closing) return;
        Show(shown is null ? "Couldn't show the preview; show the character and try again."
            : $"Preview shown {SpeechBubblesPageModel.BubbleWhere(shown)}{services.BubbleTheme(shown)}.");
    }
}
