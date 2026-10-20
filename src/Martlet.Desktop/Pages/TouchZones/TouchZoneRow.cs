using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Martlet.Avatar.Hosting;
using Martlet.Core.Settings;
using Martlet.Presentation;

namespace Martlet.Desktop;

/// <summary>One zone's row, compact: on, name and what it is (its parts, how the persona feels about it), Try and More; its
/// reaction list, exactly what plays, in order (each entry a chip: ‹ plays it earlier, × removes it; Add a reaction adds one
/// at the end, Defaults fills the list again with what a new zone gets); then how long it rests and Martlet notices with the
/// owner's optional words (shown while Martlet notices the zone). More shows the zone's box and Delete. A zone has one box, or
/// several areas (separated by | in its box field); Add area and Remove area add one or take the last away. A zone that
/// follows the model's own part (a tail) shows that part's areas: moving one of them places the zone there again, and Martlet
/// finds what of the model it follows from there. Its areas show on the picture.</summary>
internal sealed partial class TouchZoneRow
{
    private const string AddChoice = "Add a reaction...";
    private readonly CharacterTouchZone zone;
    private readonly CheckBox on, notices;
    private readonly TextBox name, narration, rest, box;
    private readonly ComboBox add;
    private readonly WrapPanel plays = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly List<string> reactions;
    private readonly Button? addArea, removeArea;
    private readonly IReadOnlyList<(string Id, string Label)> items;
    private readonly Action<string> hear;
    private readonly Action edited;
    // The box field as the row showed it first: while it reads the same, the zone keeps its areas exactly as saved.
    private readonly string shown;
    // Its areas follow the model's own part; the area the owner moved last places the zone again.
    private readonly bool follows;
    private int moved = -1;
    internal int Number { get; }
    internal bool Deleted { get; private set; }
    internal StackPanel View { get; } = new() { Margin = new Thickness(0, 12, 0, 0) };

    /// <summary>The row of <paramref name="zone"/>, the <paramref name="number"/>th. <paramref name="openRows"/> holds the zones whose
    /// rows show More (kept while the page is drawn again), <paramref name="tryZone"/> plays a zone's list and <paramref name="hear"/>
    /// plays a voice sound.</summary>
    internal TouchZoneRow(CharacterTouchZone zone, int number, CharacterActionCatalog catalog,
        IReadOnlyList<(string Id, string Label)> items, CharacterTouchZoneSettings settings, bool showing, Action edited,
        CharacterTouchTemperament? temperament, ISet<string> openRows, Action<CharacterTouchZone> tryZone, Action<string> hear)
    {
        this.zone = zone;
        this.items = items;
        this.hear = hear;
        this.edited = edited;
        follows = zone.Follows is not null;
        Number = number;
        var kind = CharacterTouchZones.Kind(zone.Id);
        on = RowSwitch(zone.Enabled);
        AutomationProperties.SetName(on, $"Use {zone.Name}");
        AutomationProperties.SetAutomationId(on, $"TouchZoneOn-{number}");
        name = Compact(new TextBox { Text = zone.Name, Width = 150, MaxLength = CharacterTouchZones.MaximumLabelLength });
        AutomationProperties.SetName(name, $"Name of {zone.Name}");
        AutomationProperties.SetAutomationId(name, $"TouchZoneName-{number}");
        InputHint.SetText(name, "Left ear");
        var state = new TextBlock
        {
            Text = Describe(zone, settings, CharacterTouchTemperaments.Attitude(temperament, zone.Id)),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 8, 0), FontSize = 13, TextWrapping = TextWrapping.Wrap
        };
        state.SetResourceReference(FrameworkElement.StyleProperty, "Muted");
        AutomationProperties.SetAutomationId(state, $"TouchZoneState-{number}");
        var tryIt = Compact(PageButton("Try", () => tryZone(Read()), id: $"TouchZoneTry-{number}"));
        tryIt.IsEnabled = showing;
        AutomationProperties.SetHelpText(tryIt, "Plays this zone's reaction list on the character.");
        // More shows the box and Delete; the page keeps it open while it is drawn again.
        var open = openRows.Contains(zone.Id);
        var more = Compact(PageButton(open ? "Less" : "More", () => { }, id: $"TouchZoneMore-{number}"));
        more.Margin = new Thickness(6, 0, 0, 0);
        AutomationProperties.SetHelpText(more, "Shows or hides the zone's box and Delete.");
        var delete = Compact(PageButton("Delete", () =>
        {
            Deleted = true;
            View.Visibility = Visibility.Collapsed;
            foreach (var (rectangle, label) in drawn)
            {
                rectangle.Visibility = Visibility.Collapsed;
                if (label is not null) label.Visibility = Visibility.Collapsed;
            }
            edited();
        }, id: $"TouchZoneDelete-{number}"));
        AutomationProperties.SetHelpText(delete, "Deletes this zone. Detect again finds it again when it is one of the zones it looks for.");
        // Level with the name, also when a narrow window puts the note under it.
        tryIt.VerticalAlignment = more.VerticalAlignment = VerticalAlignment.Top;
        // The note sits beside the name, or under it when the window is too narrow for both.
        var named = new StackPanel { Orientation = Orientation.Horizontal };
        named.Children.Add(on);
        named.Children.Add(name);
        var title = new FillWrapPanel { FillMinimum = 160, FillIndent = RowIndent - 10 };
        title.Children.Add(named);
        title.Children.Add(state);
        var header = new DockPanel();
        DockPanel.SetDock(more, Dock.Right);
        DockPanel.SetDock(tryIt, Dock.Right);
        header.Children.Add(more);
        header.Children.Add(tryIt);
        header.Children.Add(title);

        // The reaction list: exactly what plays, in order. A zone not filled yet shows what it plays now, and saves it so.
        reactions = [.. CharacterTouchZones.List(zone, catalog, temperament)];
        add = Compact(new ComboBox { ItemsSource = new[] { AddChoice }.Concat(items.Select(i => i.Label)).ToArray(), SelectedIndex = 0, Width = 170 });
        AutomationProperties.SetName(add, $"Add a reaction to {zone.Name}");
        AutomationProperties.SetAutomationId(add, $"TouchZoneReactionAdd-{number}");
        AutomationProperties.SetHelpText(add, $"Adds an emote, gesture, motion or voice sound at the end of the list. A zone plays up to {CharacterTouchZones.MaximumActions}.");
        add.Margin = new Thickness(0, 4, 6, 0);
        add.SelectionChanged += (_, _) =>
        {
            if (add.SelectedIndex <= 0) return;
            var id = items[add.SelectedIndex - 1].Id;
            add.SelectedIndex = 0;
            if (reactions.Count >= CharacterTouchZones.MaximumActions || reactions.Contains(id, StringComparer.Ordinal)) return;
            reactions.Add(id);
            ShowReactions();
            edited();
        };
        var defaults = PageButton("Defaults", () =>
        {
            reactions.Clear();
            reactions.AddRange(CharacterTouchZones.DefaultReactions(zone, catalog, temperament));
            ShowReactions();
            edited();
        }, link: true, id: $"TouchZoneReactionDefaults-{number}");
        defaults.VerticalAlignment = VerticalAlignment.Center;
        defaults.Margin = new Thickness(4, 4, 0, 0);
        AutomationProperties.SetHelpText(defaults, "Fills the list again with what a new zone gets: the persona's touch temperament for this part, " +
            "else the zone's built-in reactions (and the model's own tap motion).");
        addPanel = new StackPanel { Orientation = Orientation.Horizontal };
        addPanel.Children.Add(add);
        addPanel.Children.Add(defaults);
        ShowReactions();
        var list = new DockPanel { Margin = new Thickness(RowIndent, 6, 0, 0) };
        var playsLabel = RowLabel("Plays", add, width: 44);
        playsLabel.VerticalAlignment = VerticalAlignment.Top;
        playsLabel.Margin = new Thickness(0, 6, 0, 0);
        DockPanel.SetDock(playsLabel, Dock.Left);
        list.Children.Add(playsLabel);
        list.Children.Add(plays);

        notices = new CheckBox
        {
            Content = "Martlet notices", IsChecked = zone.Reaction.Notices, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 6)
        };
        AutomationProperties.SetAutomationId(notices, $"TouchZoneNotices-{number}");
        AutomationProperties.SetHelpText(notices, "Touches here go to your Thinking model: with what you say next, or in a short reply of their own.");
        narration = Compact(new TextBox
        {
            Text = zone.Reaction.Narration is { } own && own != kind?.Narration ? own : "", MinWidth = 150,
            MaxLength = CharacterTouchZones.MaximumNarrationLength
        });
        AutomationProperties.SetName(narration, $"Your own words for touching {zone.Name} (optional hint)");
        AutomationProperties.SetAutomationId(narration, $"TouchZoneNarration-{number}");
        // Only a zone Martlet notices sends the owner's words, so the box shows only then.
        var words = WithHint(narration, "Your own words for the touch (optional)");
        words.Margin = new Thickness(0, 0, 0, 6);
        words.Visibility = zone.Reaction.Notices ? Visibility.Visible : Visibility.Collapsed;
        rest = Compact(new TextBox { Text = zone.Reaction.CooldownSeconds.ToString("0.#", CultureInfo.CurrentCulture), Width = 48 });
        AutomationProperties.SetName(rest, $"Seconds {zone.Name} rests after a touch");
        AutomationProperties.SetAutomationId(rest, $"TouchZoneCooldown-{number}");
        InputHint.SetText(rest, "2");
        shown = BoxesText(zone.AllAreas.Select(a => a.Box));
        // Wide enough for four numbers with two decimals, as a drag on the zoomed-in picture writes them.
        box = Compact(new TextBox { Text = shown, MinWidth = 160, MaxWidth = 420 });
        AutomationProperties.SetName(box, $"Box of {zone.Name}: left, top, width, height in percent of the picture, each area after a |");
        AutomationProperties.SetAutomationId(box, $"TouchZoneBox-{number}");
        InputHint.SetText(box, "40, 10, 20, 15");
        box.ToolTip = follows
            ? $"Each area of the model's own {zone.Follows}, where it was in the picture: the zone follows them wherever they move, " +
              "and you can move one onto another part to place the zone there instead."
            : "Left, top, width and height, in percent of the picture, with | between areas; or drag a box on the picture, or its " +
              "corner to resize it.";
        if (!follows)
        {
            // An area starts beside the last one; move it into place.
            addArea = Compact(PageButton("Add area", () =>
            {
                if (ParsedBoxes() is not { } boxes || boxes.Count >= CharacterTouchZones.MaximumAreas) return;
                var last = boxes[^1];
                var x = last.X + last.Width + 0.01 + Math.Min(last.Width, 0.2) <= 1 ? last.X + last.Width + 0.01 : Math.Max(0, last.X - Math.Min(last.Width, 0.2) - 0.01);
                box.Text = BoxesText([.. boxes, new TouchZoneBox(x, last.Y, Math.Min(last.Width, 0.2), Math.Min(last.Height, 0.2)).Clamped()]);
            }, id: $"TouchZoneAddArea-{number}"));
            AutomationProperties.SetHelpText(addArea, "Adds another box to this zone, beside its last one. A touch in any of its boxes is a touch on the zone.");
            removeArea = Compact(PageButton("Remove area", () =>
            {
                if (ParsedBoxes() is { Count: > 1 } boxes) box.Text = BoxesText(boxes.Take(boxes.Count - 1));
            }, id: $"TouchZoneRemoveArea-{number}"));
            AutomationProperties.SetHelpText(removeArea, "Takes this zone's last box away.");
            removeArea.Margin = new Thickness(6, 0, 0, 0);
        }

        on.Checked += (_, _) => edited();
        on.Unchecked += (_, _) => edited();
        name.TextChanged += (_, _) => edited();
        notices.Checked += (_, _) => { words.Visibility = Visibility.Visible; edited(); };
        notices.Unchecked += (_, _) => { words.Visibility = Visibility.Collapsed; edited(); };
        narration.TextChanged += (_, _) => edited();
        rest.TextChanged += (_, _) => edited();
        box.TextChanged += (_, _) => { Place(); edited(); };

        // Under the list: how long it rests and Martlet notices; the owner's words take the rest of the line, or a line of
        // their own.
        var fields = new FillWrapPanel { Margin = new Thickness(RowIndent, 6, 0, 0), FillMinimum = 200 };
        var rests = RowGroup(RowLabel("Rests", rest, width: 44), rest, RowLabel("seconds", rest, 6, 0));
        fields.Children.Add(rests);
        fields.Children.Add(notices);
        fields.Children.Add(words);
        // More: the box and Delete, out of the way until wanted.
        var details = new WrapPanel { Margin = new Thickness(RowIndent, 0, 0, 0), Visibility = open ? Visibility.Visible : Visibility.Collapsed };
        details.Children.Add(addArea is null ? RowGroup(RowLabel("Box", box, width: 44), box)
            : RowGroup(RowLabel("Box", box, width: 44), box, Spaced(addArea), removeArea!));
        details.Children.Add(RowGroup(delete));
        more.Click += (_, _) =>
        {
            var opened = details.Visibility != Visibility.Visible;
            details.Visibility = opened ? Visibility.Visible : Visibility.Collapsed;
            more.Content = opened ? "Less" : "More";
            if (opened) openRows.Add(zone.Id);
            else openRows.Remove(zone.Id);
        };
        View.Children.Add(header);
        View.Children.Add(list);
        View.Children.Add(fields);
        View.Children.Add(details);
        ShowAreaButtons();
    }

    // Add a reaction and Defaults, after the list's last chip.
    private readonly StackPanel addPanel;

    /// <summary>Shows the reaction list as chips, in the order they play: each with ‹ (play it earlier; not on the first) and
    /// × (remove it), then Add a reaction (off while the list is full) and Defaults. An entry the model doesn't have (or a voice
    /// sound) shows by its name and stays.</summary>
    private void ShowReactions()
    {
        plays.Children.Clear();
        if (reactions.Count == 0)
        {
            var none = new TextBlock { Text = "nothing", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 8, 0) };
            none.SetResourceReference(FrameworkElement.StyleProperty, "Muted");
            AutomationProperties.SetAutomationId(none, $"TouchZoneReactionNone-{Number}");
            plays.Children.Add(none);
        }
        for (var k = 0; k < reactions.Count; k++)
        {
            var index = k;
            var label = Label(reactions[k]);
            var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            text.SetResourceReference(FrameworkElement.StyleProperty, "ChipText");
            if (!Known(reactions[k])) text.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            AutomationProperties.SetAutomationId(text, $"TouchZoneReactionItem-{Number}-{k + 1}");
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            if (k > 0)
            {
                var earlier = ChipButton("\u2039", $"Play {label} earlier", $"TouchZoneReactionEarlier-{Number}-{k + 1}", () =>
                {
                    (reactions[index - 1], reactions[index]) = (reactions[index], reactions[index - 1]);
                    ShowReactions();
                    edited();
                });
                earlier.Padding = new Thickness(0, 0, 6, 0);
                content.Children.Add(earlier);
            }
            content.Children.Add(text);
            // A voice sound the voice makes can be heard here (made first when needed).
            if (VoiceSounds.CueOf(reactions[k]) is { } cue && Known(reactions[k]))
                content.Children.Add(ChipButton("\u25B6", $"Hear {VoiceSounds.Label(cue).ToLowerInvariant()}", $"TouchZoneReactionHear-{Number}-{k + 1}",
                    () => hear(cue)));
            content.Children.Add(ChipButton("\u00d7", $"Remove {label}", $"TouchZoneReactionRemove-{Number}-{k + 1}", () =>
            {
                reactions.RemoveAt(index);
                ShowReactions();
                edited();
            }));
            var chip = new Border { Child = content, VerticalAlignment = VerticalAlignment.Center };
            chip.SetResourceReference(FrameworkElement.StyleProperty, "Chip");
            plays.Children.Add(chip);
        }
        add.IsEnabled = reactions.Count < CharacterTouchZones.MaximumActions;
        plays.Children.Add(addPanel);
    }

    private static Button ChipButton(string content, string name, string id, Action run)
    {
        var button = new Button { Content = content, Padding = new Thickness(6, 0, 0, 0), Margin = new Thickness(0), ToolTip = name };
        button.SetResourceReference(FrameworkElement.StyleProperty, "LinkButton");
        AutomationProperties.SetName(button, name);
        AutomationProperties.SetAutomationId(button, id);
        button.Click += (_, _) => run();
        return button;
    }

    // Whether the entry plays here: the model has it in use, or (a voice sound) the voice makes it.
    private bool Known(string entry) => items.Any(i => i.Id == entry);

    // "Blush  ·  emote" as Add a reaction names it, "Laugh  ·  sound" for a sound the voice makes; a voice sound it doesn't make
    // "laugh  ·  sound, not with this voice"; an entry the model doesn't have (or has turned off) by its name,
    // "F05  ·  not on this model".
    private string Label(string entry) =>
        items.FirstOrDefault(i => i.Id == entry).Label ??
        (CharacterTouchReaction.IsSound(entry) ? $"{entry[CharacterTouchReaction.SoundPrefix.Length..]}  \u00b7  sound, not with this voice"
            : $"{(entry.IndexOf(':') is var colon and >= 0 ? entry[(colon + 1)..] : entry)}  \u00b7  not on this model");

    private static Button Spaced(Button button)
    {
        button.Margin = new Thickness(8, 0, 0, 0);
        return button;
    }

    // "top_of_head  ·  3 parts  ·  added by you  ·  loves it".
    private static string Describe(CharacterTouchZone zone, CharacterTouchZoneSettings settings, string? attitude)
    {
        var kind = CharacterTouchZones.Kind(zone.Id);
        var parts = zone.Drawables.Count > 0 ? $"{zone.Drawables.Count} part{(zone.Drawables.Count == 1 ? "" : "s")}"
            : zone.Bones.Count > 0 ? string.Join(", ", zone.Bones.Take(3)) : "box only";
        var areas = zone.AllAreas.Count;
        var shape = zone.Follows is { } part ? $"follows the model's own {part} wherever it moves: {parts} in {areas} area{(areas == 1 ? "" : "s")}"
            : areas > 1 ? $"{areas} areas, {parts}" : parts;
        return $"{zone.Id}  \u00b7  {shape}" + (zone.Added ? "  \u00b7  added by you" : TouchZoneDetection.IsSpecial(zone.Id) ? "  \u00b7  special to this character" : "") +
            (kind?.Intimate == true && !settings.IncludeIntimate ? "  \u00b7  intimate, off" : "") +
            (attitude is null ? "" : $"  \u00b7  {attitude} it");
    }
}
