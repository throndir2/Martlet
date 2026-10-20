using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Avatar.Hosting;
using Martlet.Conversation;

namespace Martlet.Desktop;

/// <summary>Touch zones' smaller parts: the zone map's zoom row, where Add zone puts a zone, When you touch Martlet while it talks
/// and Start over.</summary>
internal sealed partial class TouchZonesPage
{
    private const string ZoomHelp = "Zoom in to move and resize the boxes precisely. Ctrl+wheel over the picture zooms where " +
        "the pointer is. Zoomed in, drag the picture (not a box) or scroll to look around it; Ctrl+drag or a middle-button drag moves it " +
        "from anywhere.";

    /// <summary>The zone map's zoom: Zoom in, Zoom out and Reset zoom, how far it is zoomed in (TouchZonesZoom: "Zoom 2x") and how to
    /// zoom and look around with the mouse. <paramref name="zoomed"/> places the picture and its boxes again at each zoom, and the
    /// page keeps the zoom and the part of the picture shown while it is drawn again.</summary>
    private StackPanel ZoomRow(ZoomFrame map, Action zoomed)
    {
        var zoomIn = Compact(PageButton("Zoom in", () => map.ZoomIn(), id: "TouchZonesZoomIn"));
        var zoomOut = Compact(PageButton("Zoom out", () => map.ZoomOut(), id: "TouchZonesZoomOut"));
        var reset = Compact(PageButton("Reset zoom", () => map.ZoomTo(1), id: "TouchZonesZoomReset"));
        AutomationProperties.SetHelpText(reset, "Shows the whole picture again.");
        var level = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 6) };
        level.SetResourceReference(FrameworkElement.StyleProperty, "Muted");
        AutomationProperties.SetAutomationId(level, "TouchZonesZoom");
        AutomationProperties.SetLiveSetting(level, AutomationLiveSetting.Polite);
        void Show()
        {
            level.Text = $"Zoom {map.Zoom.ToString("0.#", CultureInfo.CurrentCulture)}x";
            zoomIn.IsEnabled = map.CanZoomIn;
            zoomOut.IsEnabled = reset.IsEnabled = map.CanZoomOut;
        }
        Show();
        map.Zoomed += () => { zoomed(); Show(); };
        map.ViewChanged += () => (zoom, center) = (map.Zoom, map.Center);
        var buttons = Row(zoomIn, zoomOut, reset);
        buttons.Children.Add(level);
        var row = new StackPanel();
        row.Children.Add(buttons);
        row.Children.Add(HelpTip.Explain(ZoomHelp, new Thickness(0), "TouchZonesZoom", "zooming"));
        return row;
    }

    /// <summary>Where Add zone puts a zone: a fifth of the picture each way in its middle or, zoomed in to <paramref name="zoom"/>, in
    /// the middle of the part the map shows (<paramref name="center"/>, fractions of the picture) and as large on the screen as at 1x.</summary>
    internal static TouchZoneBox AddedZoneBox(double zoom, Point center)
    {
        if (!(zoom > 1)) return new(0.4, 0.4, 0.2, 0.2);
        var size = 0.2 / zoom;
        return new(Math.Clamp(center.X - size / 2, 0, 1 - size), Math.Clamp(center.Y - size / 2, 0, 1 - size), size, size);
    }

    /// <summary>The Start over section at the end of the Touch zones card: the level list (TouchZonesResetLevel), what the level
    /// clears (TouchZonesResetNote), Reset (TouchZonesReset) and what the last reset did (TouchZonesResetState).
    /// <paramref name="edits"/> holds the card's unsaved zone edits, saved before a reset so they can't come back after it.</summary>
    private StackPanel StartOver(string modelId, AutoSave edits)
    {
        var section = new StackPanel { Margin = new Thickness(0, 20, 0, 0) };
        section.Children.Add(new TextBlock { Text = "Start over", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        section.Children.Add(HelpTip.Explain("Reset this character's touch to how a fresh character starts. Choose how much to reset: " +
            "Martlet asks first and lists what you lose. Only the model shown and the active persona change.", new Thickness(0, 0, 0, 6),
            "TouchZonesStartOver", "starting over", wiki: "The-Character#start-over"));
        var levels = CharacterTouchReset.Levels;
        var chosen = CharacterTouchReset.Level(services.ResetLevel);
        var list = Compact(new ComboBox { ItemsSource = levels.Select(l => l.Label).ToArray(), SelectedIndex = IndexOf(chosen), MinWidth = 180 });
        AutomationProperties.SetName(list, "What to reset");
        AutomationProperties.SetAutomationId(list, "TouchZonesResetLevel");
        var resetting = services.Resetting;
        var reset = Compact(PageButton(resetting ? "Resetting..." : "Reset...", () => services.Reset(modelId, edits), id: "TouchZonesReset"));
        reset.Margin = new Thickness(8, 0, 0, 0);
        reset.IsEnabled = !resetting && !services.Detecting && !services.Zones.Busy;
        AutomationProperties.SetHelpText(reset, "Asks first, and lists exactly what you lose, before anything is reset.");
        var row = new WrapPanel();
        row.Children.Add(list);
        row.Children.Add(reset);
        section.Children.Add(row);
        var clears = Note(chosen.Clears, new Thickness(0, 6, 0, 0));
        AutomationProperties.SetAutomationId(clears, "TouchZonesResetNote");
        section.Children.Add(clears);
        list.SelectionChanged += (_, _) =>
        {
            var level = levels[Math.Max(0, list.SelectedIndex)];
            services.ResetLevel = level.Id;
            clears.Text = level.Clears;
        };
        // Shown only while it has something to say.
        var state = Note("", new Thickness(0, 6, 0, 0));
        AutomationProperties.SetAutomationId(state, "TouchZonesResetState");
        AutomationProperties.SetLiveSetting(state, AutomationLiveSetting.Polite);
        state.Text = services.ResetState ?? "";
        state.Visibility = string.IsNullOrEmpty(services.ResetState) ? Visibility.Collapsed : Visibility.Visible;
        section.Children.Add(state);
        return section;

        static int IndexOf(TouchResetLevel level)
        {
            for (var i = 0; i < CharacterTouchReset.Levels.Count; i++)
                if (CharacterTouchReset.Levels[i] == level) return i;
            return 0;
        }
    }

    /// <summary>Touch zones › When you touch Martlet while it talks (<see cref="TalkPreferences.TouchInterrupts"/>, this PC): a
    /// touch it notices stops the reply or remark it is saying, like talking over it, and its reaction knows what it was saying.</summary>
    private void AddTouchInterrupts(List<UIElement> stack)
    {
        stack.Add(new TextBlock
        {
            Text = "When you touch Martlet while it talks", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 6),
            TextWrapping = TextWrapping.Wrap
        });
        var talk = services.Talk;
        foreach (var (choice, word, title, detail) in new (TouchInterrupts, string, string, string)[]
        {
            (TouchInterrupts.Any, "any", "It stops to react (recommended)",
                "Any touch Martlet notices stops what it is saying, and it reacts a moment after your last touch."),
            (TouchInterrupts.Intimate, "intimate", "It stops only for intimate touches",
                $"Only a touch on an intimate part ({CharacterTouchZones.IntimateParts}) stops it; other touches wait until it finishes."),
            (TouchInterrupts.Never, "never", "It finishes first",
                "Your touches wait, and Martlet reacts to them after what it is saying.")
        })
        {
            var option = Choice("TouchInterrupts", title, detail, talk.TouchInterrupts == choice, "TouchInterrupt-" + word);
            option.Checked += (_, _) => { if (services.Talk.TouchInterrupts != choice) services.SaveTalk(services.Talk with { TouchInterrupts = choice }); };
            stack.Add(option);
        }
    }
}
