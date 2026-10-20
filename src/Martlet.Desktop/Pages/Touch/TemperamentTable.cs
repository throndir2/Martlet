using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Martlet.Avatar.Hosting;
using Martlet.Presentation;

namespace Martlet.Desktop;

/// <summary>The temperament's table: rows of a part's name and five cells (feels, plays, then, lingers, looks) whose columns
/// share one width. The three choices share what the card's width leaves, each up to a most; on a narrow window each row's
/// cells wrap under each other, all at the same places, to the right of the names, instead of being cut off.</summary>
internal sealed class TemperamentTable
{
    internal const double Part = 130, Gap = 8;
    private const double Number = 60;
    private static readonly (double Weight, double Least, double Most)[] Choices = [(1.1, 96, 170), (1.2, 104, 200), (1.2, 104, 200)];
    private readonly List<FrameworkElement>[] columns = [[], [], [], [], []];
    private readonly double[] widths = [96, 104, 104, Number, Number];
    internal StackPanel View { get; } = new() { Margin = new Thickness(0, 12, 0, 0) };

    internal TemperamentTable() => View.SizeChanged += (_, e) => { if (e.WidthChanged) Fit(e.NewSize.Width); };

    /// <summary>Adds a row: the part's name, then a cell per column.</summary>
    internal DockPanel Add(FrameworkElement part, params FrameworkElement[] cells)
    {
        var rest = new WrapPanel();
        for (var column = 0; column < cells.Length; column++)
        {
            cells[column].Margin = new Thickness(0, 3, Gap, 3);
            cells[column].Width = widths[column];
            columns[column].Add(cells[column]);
            rest.Children.Add(cells[column]);
        }
        return Line(part, rest);
    }

    /// <summary>Adds a row of the part's name and something outside the columns.</summary>
    internal DockPanel Line(FrameworkElement part, FrameworkElement content)
    {
        part.Width = Part;
        part.Margin = new Thickness(0, 3, Gap, 3);
        DockPanel.SetDock(part, Dock.Left);
        var row = new DockPanel();
        row.Children.Add(part);
        row.Children.Add(content);
        View.Children.Add(row);
        return row;
    }

    private void Fit(double width)
    {
        // Two pixels to spare, so rounding never pushes a row's last cell onto a line of its own.
        var left = width - Part - 2 * Number - (columns.Length + 1) * Gap - 2;
        var weight = Choices.Sum(c => c.Weight);
        for (var i = 0; i < Choices.Length; i++)
        {
            widths[i] = Math.Clamp(left * Choices[i].Weight / weight, Choices[i].Least, Choices[i].Most);
            foreach (var cell in columns[i]) cell.Width = widths[i];
        }
    }
}
/// <summary>One group's or part's line of the temperament table: how the character feels about a touch there, up to two
/// reactions (or none at all), how long the first lingers and how long the eyes then look at your mouse, and a part's
/// Remove. Controls that don't apply now are hidden: all but the feeling of a group left to its built-in reactions, a
/// second reaction after the feeling's default, and the linger after no reaction.</summary>
internal sealed class TemperamentRow
{
    private const string BuiltIn = "(built-in)", Default = "(default)", Nothing = "(nothing)";
    private readonly ComboBox attitude, first, second;
    private readonly TextBox linger, look;
    private readonly DockPanel view;
    internal string Id { get; }
    internal bool IsGroup { get; }
    internal bool Removed { get; private set; }
    /// <summary>Whether the line has a feeling of its own (not a group left to its built-in reactions, nor removed).</summary>
    internal bool On => !Removed && (!IsGroup || attitude.SelectedIndex > 0);

    /// <summary>A choice shown on one line, cut short with an ellipsis when its box is narrow.</summary>
    internal static readonly DataTemplate OneLine = OneLineTemplate();

    /// <summary>A line; a category's first feeling choice leaves it uncovered, and <paramref name="unset"/> names what that does (the
    /// built-in reactions unless said otherwise), with <paramref name="unsetHelp"/> as its tooltip.</summary>
    internal TemperamentRow(TemperamentTable table, string id, string label, bool isGroup, TouchTemperamentEntry? entry, Action edited,
        string? unset = null, string? unsetHelp = null)
    {
        Id = id;
        IsGroup = isGroup;
        var words = CharacterTouchTemperaments.Vocabulary;
        var shown = words.Select(Display).ToArray();
        var name = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        FrameworkElement part = name;
        if (!isGroup)
        {
            var glyph = new TextBlock { Text = "\uE711", FontSize = 11 };
            glyph.SetResourceReference(FrameworkElement.StyleProperty, "Icon");
            var remove = PageButton("Remove", () =>
            {
                Removed = true;
                view!.Visibility = Visibility.Collapsed;
                edited();
            }, id: $"TouchTemperamentRemove-{id}");
            remove.Content = glyph;
            remove.MinWidth = remove.MinHeight = 0;
            remove.Width = remove.Height = 28;
            remove.Padding = new Thickness(0);
            remove.Margin = new Thickness(6, 0, 4, 0);
            remove.VerticalAlignment = VerticalAlignment.Center;
            // Quiet until pointed at: the button's hover ring shows it.
            remove.Background = remove.BorderBrush = Brushes.Transparent;
            remove.SetResourceReference(Control.ForegroundProperty, "MutedBrush");
            remove.ToolTip = $"Remove {label}'s own line, so it reacts like its category";
            AutomationProperties.SetName(remove, $"Remove {label}");
            var dock = new DockPanel();
            DockPanel.SetDock(remove, Dock.Right);
            dock.Children.Add(remove);
            dock.Children.Add(name);
            part = dock;
        }
        attitude = Combo((isGroup ? new[] { unset ?? BuiltIn } : []).Concat(CharacterTouchTemperaments.AttitudeWords).ToArray());
        if (unsetHelp is not null)
        {
            attitude.ToolTip = unsetHelp;
            AutomationProperties.SetHelpText(attitude, unsetHelp);
        }
        var offset = isGroup ? 1 : 0;
        attitude.SelectedIndex = entry is null ? 0 : entry.Attitude - CharacterTouchTemperaments.MinimumAttitude + offset;
        AutomationProperties.SetName(attitude, $"How the character feels about a touch on: {label}");
        AutomationProperties.SetAutomationId(attitude, $"TouchTemperamentAttitude-{id}");
        // First choices: the attitude's own reactions, no reaction at all, then each reaction.
        first = Combo(new[] { Default, Nothing }.Concat(shown).ToArray());
        first.SelectedIndex = entry?.Reactions switch
        {
            { Count: 0 } => 1,
            { Count: > 0 } r when IndexOf(words, r[0]) is var i and >= 0 => i + 2,
            _ => 0
        };
        AutomationProperties.SetName(first, $"First reaction to a touch on: {label}");
        AutomationProperties.SetAutomationId(first, $"TouchTemperamentReaction-{id}");
        second = Combo(new[] { Nothing }.Concat(shown).ToArray());
        second.SelectedIndex = entry?.Reactions is { Count: > 1 } r2 ? Math.Max(0, IndexOf(words, r2[1]) + 1) : 0;
        AutomationProperties.SetName(second, $"Second reaction to a touch on: {label}");
        AutomationProperties.SetAutomationId(second, $"TouchTemperamentReaction2-{id}");
        linger = SecondsBox(entry?.LingerSeconds ?? 0);
        AutomationProperties.SetName(linger, $"Seconds the first reaction to {label} stays on");
        AutomationProperties.SetAutomationId(linger, $"TouchTemperamentLinger-{id}");
        look = SecondsBox(entry?.LookSeconds ?? 0);
        AutomationProperties.SetName(look, $"Seconds the character looks at your mouse after a touch on {label}");
        AutomationProperties.SetAutomationId(look, $"TouchTemperamentLook-{id}");
        void Apply()
        {
            var on = !isGroup || attitude.SelectedIndex > 0;
            // A line without a feeling of its own ends after it; otherwise a gap keeps the cells after it in their columns.
            var off = on ? Visibility.Hidden : Visibility.Collapsed;
            first.Visibility = look.Visibility = on ? Visibility.Visible : off;
            linger.Visibility = on && first.SelectedIndex != 1 ? Visibility.Visible : off;
            second.Visibility = on && first.SelectedIndex > 1 ? Visibility.Visible : off;
            // What (default) plays for the feeling chosen now.
            var feeling = attitude.SelectedIndex - offset + CharacterTouchTemperaments.MinimumAttitude;
            var help = $"(default) plays what {CharacterTouchTemperaments.AttitudeWord(feeling)} usually plays: " +
                Words(CharacterTouchTemperaments.DefaultReactions(feeling)) + ". (nothing) plays nothing at all.";
            first.ToolTip = help;
            AutomationProperties.SetHelpText(first, help);
        }
        Apply();
        attitude.SelectionChanged += (_, _) => { Apply(); edited(); };
        first.SelectionChanged += (_, _) => { Apply(); edited(); };
        second.SelectionChanged += (_, _) => edited();
        linger.TextChanged += (_, _) => edited();
        look.TextChanged += (_, _) => edited();
        view = table.Add(part, attitude, first, second, linger, look);
    }
    /// <summary>Reactions in words: "anger and look away", "hearts, blush and lean in".</summary>
    internal static string Words(IReadOnlyList<string> words) => words.Count switch
    {
        0 => "nothing",
        1 => Display(words[0]),
        _ => string.Join(", ", words.Take(words.Count - 1).Select(Display)) + " and " + Display(words[^1])
    };

    private static string Display(string word) => word.Replace('_', ' ');

    private static ComboBox Combo(IReadOnlyList<string> items) => Compact(new ComboBox { ItemsSource = items, ItemTemplate = OneLine });

    private static TextBox SecondsBox(double value) => InputHint.With(Compact(new TextBox
    {
        Text = value.ToString("0.#", CultureInfo.CurrentCulture), MaxLength = 4, TextAlignment = TextAlignment.Center
    }), 1.5.ToString("0.#", CultureInfo.CurrentCulture));

    private static DataTemplate OneLineTemplate()
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding());
        text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.NoWrap);
        text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        var template = new DataTemplate { VisualTree = text };
        template.Seal();
        return template;
    }

    private static int IndexOf(IReadOnlyList<string> words, string word)
    {
        for (var i = 0; i < words.Count; i++) if (words[i] == word) return i;
        return -1;
    }

    /// <summary>The line as an entry, or null for a group left to its built-in reactions.</summary>
    internal TouchTemperamentEntry? Read()
    {
        var offset = IsGroup ? 1 : 0;
        if (attitude.SelectedIndex < offset) return null;
        var words = CharacterTouchTemperaments.Vocabulary;
        IReadOnlyList<string>? reactions = first.SelectedIndex switch
        {
            <= 0 => null,
            1 => [],
            _ => new[] { words[first.SelectedIndex - 2] }.Concat(second.SelectedIndex > 0 ? [words[second.SelectedIndex - 1]] : Array.Empty<string>())
                .Distinct(StringComparer.Ordinal).ToArray()
        };
        static double Seconds(TextBox box, double most) =>
            double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var seconds) && double.IsFinite(seconds)
                ? Math.Clamp(seconds, 0, most) : 0;
        return new()
        {
            Attitude = attitude.SelectedIndex - offset + CharacterTouchTemperaments.MinimumAttitude, Reactions = reactions,
            LingerSeconds = Seconds(linger, CharacterTouchTemperaments.MaximumLinger),
            LookSeconds = Seconds(look, CharacterTouchTemperaments.MaximumLook)
        };
    }
}
