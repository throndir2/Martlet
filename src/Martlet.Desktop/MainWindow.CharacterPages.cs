using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Avatar.Hosting;
using Martlet.Avatars;
using Martlet.Core.Cluster;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Presentation;
using Martlet.Providers;

namespace Martlet.Desktop;

/// <summary>Companion's How it looks pages that live here: Character, Eyes, Touch and the speech display card (Speech bubbles is in MainWindow.PageNow.cs).</summary>
public partial class MainWindow
{
    // ---------- character ----------

    private void RenderCharacterTab(Panel page)
    {
        var showing = avatar.IsShowing;
        page.Children.Add(PageNowCard(CharacterModelName() + (homeAvatar is { } profile ? $" ({profile.Renderer})" : "") +
            (showing ? ", on your desktop." : ", hidden."), null, "SetupCharacterNow", characterCleanupProblem));

        var locked = avatar.PlacementLocked;
        // Reset stays available while hidden once a place is saved, in case the character got lost off screen.
        var resetPosition = showing || avatar.Placement is not null
            ? PageButton("Reset position", () => ResetCharacterPositionAsync().Forget(), id: "SetupCharacterResetPosition") : null;
        var modelCard = Card(Heading("Character model"),
            Note("Choose a character model, then adjust its size, position and motion. Choices save on their own and a showing character switches right away.", new Thickness(0, 0, 0, 8)),
            Row(PageButton(showing ? "Hide character" : "Show character", () => RunNodeAction(NodeAction.ToggleCharacter), primary: !showing, id: "SetupCharacterToggle"),
                PageButton("Choose and customize", () => RunNodeAction(NodeAction.Character), id: "OpenAvatar"),
                resetPosition,
                showing ? PageButton("Reset zoom", () => ResetCharacterZoomAsync().Forget(), id: "SetupCharacterResetZoom") : null,
                // Unlocking also works here while the character is hidden.
                showing || locked ? PageButton(locked ? "Unlock position" : "Lock position",
                    () => SetCharacterLockAsync(!avatar.PlacementLocked).Forget(), id: "SetupCharacterLock") : null,
                // The same goes for click-through: the mouse can't reach the character to turn it off.
                showing || avatar.ClickThrough ? PageButton(avatar.ClickThrough ? "Turn off click-through" : "Turn on click-through",
                    () => SetCharacterClickThroughAsync(!avatar.ClickThrough).Forget(), id: "SetupCharacterClickThrough") : null));
        if (modelCard.Child is Panel modelPanel)
        {
            var placementNote = characterPlacementNote = Note(CharacterPlacementText(), new Thickness(0, 4, 0, 0));
            AutomationProperties.SetAutomationId(placementNote, "SetupCharacterPlacement");
            modelPanel.Children.Add(placementNote);
            var clickThroughNote = Note(avatar.ClickThrough
                ? "Click-through is on: clicks pass through the character to the windows under it, so you can't drag, zoom or right-click it. Turn it off here or from Martlet's icon in the notification area."
                : "Click-through is off: the character catches clicks. Turn it on here or from the character's right-click menu to let clicks pass through it, for example while you play a game.",
                new Thickness(0, 4, 0, 0));
            AutomationProperties.SetAutomationId(clickThroughNote, "SetupCharacterClickThroughNote");
            modelPanel.Children.Add(clickThroughNote);
        }
        // What the showing model drives: textures (and any downscaling), blinking, mouth, motions and physics.
        if (showing && avatar.Capabilities is { } loaded && modelCard.Child is Panel modelStack)
        {
            var modelNote = Note("Model: " + AvatarRendererProcess.Describe(loaded), new Thickness(0, 4, 0, 0));
            AutomationProperties.SetAutomationId(modelNote, "SetupCharacterModel");
            modelStack.Children.Add(modelNote);
        }
        page.Children.Add(modelCard);
        characterViewText = null;
        if (showing)
        {
            // Wheel zoom happens on the overlay itself; Martlet reads the overlay's view whenever this page renders.
            characterViewText = Note("", new Thickness(0, 8, 0, 0));
            AutomationProperties.SetAutomationId(characterViewText, "SetupCharacterView");
            page.Children.Add(Card(Heading("Zoom"),
                Note("Use the mouse wheel over the character to zoom. Ctrl+drag or middle-drag pans when zoomed in.", new Thickness(0, 0, 0, 0)),
                Row(PageButton("Zoom in", () => ZoomCharacterAsync("in").Forget(), id: "SetupCharacterZoomIn"),
                    PageButton("Zoom out", () => ZoomCharacterAsync("out").Forget(), id: "SetupCharacterZoomOut")),
                characterViewText));
            ZoomCharacterAsync("status").Forget();
        }
        page.Children.Add(CharacterModelsCard());
        page.Children.Add(VRoidHubCard());
        page.Children.Add(ProfilesLinkCard());
    }

    /// <summary>Companion › Eyes: Now, where the character looks, then where its eyes are (for the emotes drawn over them).</summary>
    private void RenderEyesTab(Panel page)
    {
        FollowCharacterActions();
        page.Children.Add(CharacterGazeCard());
        page.Children.Add(CharacterEyesCard());
        page.Children.Insert(0, EyesNowCard());
    }

    /// <summary>Companion › Touch: Now, the shown model's touch zones, then the active persona's touch temperament (which joins
    /// with the zones' last rows on a page just opened).</summary>
    private void RenderTouchTab(Panel page)
    {
        FollowCharacterActions();
        page.Children.Add(CharacterTouchZonesCard());
        page.Children.Insert(0, TouchNowCard());
        AddRow(page, CharacterTemperamentCard());
        page.Children.Add(CharacterReactionChangesCard());
    }

    private ComboBox? bubblePlacementChoice;
    private TextBox? bubbleOffsetX, bubbleOffsetY;
    private TextBlock? speechDisplayText;
    private Button? speechPreviewButton;

    /// <summary>Companion › Speech bubbles' configuration: where the bubble goes, following the character's head (default) or
    /// staying in one place, nudged by horizontal and vertical offsets; the same saved choices as the character settings window,
    /// applied from Martlet's next sentence. Whether bubbles and subtitles show at all is the page's main choice
    /// (<see cref="SpeechDisplayChoiceCard"/>).</summary>
    private Border SpeechDisplayCard()
    {
        var prefs = captions.Preferences;
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
            var box = new TextBox { Width = 90, MaxLength = 6, Text = value.ToString("0.##", CultureInfo.CurrentCulture) };
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
        ShowSpeechDisplay();
        return Card(Heading("Where the bubble goes"),
            Note("Speech bubbles show Martlet's spoken words beside the character; subtitles show them at the bottom of the active screen.",
                new Thickness(0, 0, 0, 0)),
            new Label { Content = "Bubble _position", Target = bubblePlacementChoice, Padding = new Thickness(0, 12, 0, 4) },
            bubblePlacementChoice, offsets,
            Note("Following the character, the bubble sits beside its head on whichever side has room, and keeps up as you move or zoom; " +
                "the offsets nudge it (in pixels, positive is right and down). Staying in one place, the offsets are measured from the " +
                "top-left of the character's screen.", new Thickness(0, 6, 0, 0)),
            speechDisplayText, Row(speechPreviewButton));
    }

    private const string BubbleFollows = "Follows the character", BubbleStays = "Stays in one place";

    private void SaveSpeechDisplay()
    {
        if (showingSpeechDisplay || bubblePlacementChoice is null) return;
        var prefs = captions.Preferences with { StaticBubble = ReferenceEquals(bubblePlacementChoice.SelectedItem, BubbleStays) };
        static double? Read(TextBox? box) =>
            box is null ? null
            : double.TryParse(box.Text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out var value) &&
              double.IsFinite(value) && Math.Abs(value) <= SpeechDisplayPreferences.MaximumOffset ? value
            : double.NaN;
        var x = Read(bubbleOffsetX);
        var y = Read(bubbleOffsetY);
        if (x is double.NaN || y is double.NaN)
        {
            // Leave the half-typed text alone (such as a lone "-"); just say what's expected.
            if (speechDisplayText is not null)
                speechDisplayText.Text = $"Offsets must be numbers of pixels from -{SpeechDisplayPreferences.MaximumOffset:0} to {SpeechDisplayPreferences.MaximumOffset:0}.";
            return;
        }
        prefs = prefs with { BubbleOffsetX = x ?? prefs.BubbleOffsetX, BubbleOffsetY = y ?? prefs.BubbleOffsetY };
        var saved = captions.Update(prefs);
        if (!saved) ShowSpeechDisplay("Applied for now, but couldn't save your speech display settings.");
    }

    private bool showingSpeechDisplay;

    /// <summary>Follows the saved choices, whichever window changed them.</summary>
    private void ShowSpeechDisplay(string? outcome = null)
    {
        if (speechDisplayText is null) return;
        var prefs = captions.Preferences;
        showingSpeechDisplay = true;
        try
        {
            ShowSpeechDisplayChoice(prefs);
            if (bubblePlacementChoice is not null) bubblePlacementChoice.SelectedItem = prefs.StaticBubble ? BubbleStays : BubbleFollows;
            // Only rewrite an offset box when its number differs, so typing in it isn't interrupted.
            foreach (var (box, value) in new[] { (bubbleOffsetX, prefs.BubbleOffsetX), (bubbleOffsetY, prefs.BubbleOffsetY) })
                if (box is not null && !(double.TryParse(box.Text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out var shown) && shown == value))
                    box.Text = value.ToString("0.##", CultureInfo.CurrentCulture);
        }
        finally { showingSpeechDisplay = false; }
        if (speechPreviewButton is not null) speechPreviewButton.IsEnabled = avatar.IsShowing && prefs.SpeechBubbles;
        var bubbles = !prefs.SpeechBubbles ? "Speech bubbles are off."
            : avatar.IsShowing ? "Speech bubbles are on."
            : "Speech bubbles will appear when the character is showing.";
        var position = prefs.StaticBubble
            ? $" Bubbles stay in one place, {prefs.BubbleOffsetX:0.##}, {prefs.BubbleOffsetY:0.##} from the top-left of the character's screen."
            : $" Bubbles follow the character, offset by {prefs.BubbleOffsetX:0.##}, {prefs.BubbleOffsetY:0.##}.";
        speechDisplayText.Text = $"{bubbles} Subtitles are {(prefs.Subtitles ? "on" : "off")}." + position + (outcome is null ? "" : " " + outcome);
    }

    private async Task PreviewSpeechBubbleAsync()
    {
        var shown = await captions.PreviewAsync("Hi! While I talk, what I say shows up here.");
        if (closing) return;
        ShowSpeechDisplay(shown is null ? "Couldn't show the preview. Show the character and try again."
            : $"Preview shown {BubbleWhere(shown)}" +
              $"{BubbleTheme(shown, SelectedTheme.Name(), key => TryFindResource(key) as System.Windows.Media.Brush, SystemParameters.HighContrast)}.");
    }

    /// <summary>Whether the shown bubble is drawn in the palette Martlet's windows use now (<paramref name="brush"/> by resource
    /// key): its fill, outline, text and halo are the palette's Surface, Accent, Text and Glow (no halo in high contrast).
    /// Empty when the overlay didn't say which colors it used.</summary>
    internal static string BubbleTheme(RendererBubble bubble, string palette, Func<string, System.Windows.Media.Brush?> brush, bool highContrast)
    {
        if (bubble.Colors is not { } drawn) return "";
        var differing = new List<string>();
        void Check(string part, string? actual, string? role)
        {
            var expected = role is null ? null
                : brush(role + "Brush") is System.Windows.Media.SolidColorBrush solid ? $"#{solid.Color.R:X2}{solid.Color.G:X2}{solid.Color.B:X2}" : "";
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) differing.Add($"{part} {actual ?? "none"}");
        }
        Check("fill", drawn.Fill, "Surface");
        Check("outline", drawn.Outline, "Accent");
        Check("text", drawn.Text, "Text");
        Check("halo", drawn.Halo, highContrast ? null : "Glow");
        return differing.Count == 0 ? $", in the {palette} colors" : $", but not in the {palette} colors ({string.Join(", ", differing)})";
    }

    internal static string BubbleWhere(RendererBubble bubble) => bubble.Placement switch
    {
        "left" => "to the left of the character's head",
        "right" => "to the right of the character's head",
        "above" => "above the character's head",
        "static" => "in its fixed place",
        "hidden" => "hidden",
        _ => "beside the character"
    } + (bubble.Width > 0 ? $" at {bubble.Left:0}, {bubble.Top:0} ({bubble.Width:0} × {bubble.Height:0})" : "") + (bubble.TextFits switch
    {
        true => ", holding all its text",
        false => ", but its text doesn't fit inside it",
        null => ""
    });
}
