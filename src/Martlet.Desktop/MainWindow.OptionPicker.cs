using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Core.Planning;

namespace Martlet.Desktop;

/// <summary>One choice in an option picker (<see cref="MainWindow.OptionPicker"/>): a model, an engine, a provider, a place, or
/// Off. <see cref="Facts"/> are what a compact row and the compare table show (<see cref="OptionFacts.Of"/> for a catalog
/// option); <see cref="Summary"/> is its strength in one sentence. <see cref="Unavailable"/>: why it can't run here (it is
/// listed last, greyed, and can still be looked at). <see cref="Details"/> adds this option's own lines to its details panel
/// (an abilities line, a license, fields), and <see cref="Action"/> is the button that uses it.</summary>
internal sealed record PickerOption(string Key, string Name, string Summary)
{
    /// <summary>"in use", "recommended", "ready": a short word after the name.</summary>
    public string? Badge { get; init; }
    public IReadOnlyList<OptionFact> Facts { get; init; } = [];
    /// <summary>Where it stands now, in words ("Ready on gpu-box.", "Setting up on this PC...").</summary>
    public string? State { get; init; }
    public bool Warn { get; init; }
    public string? Unavailable { get; init; }
    public bool InUse { get; init; }
    /// <summary>The Off choice of an optional part.</summary>
    public bool IsOff { get; init; }
    public Func<IEnumerable<UIElement>>? Details { get; init; }
    public Func<Button?>? Action { get; init; }

    /// <summary>The Off choice of an optional part: <paramref name="means"/> says what Off means ("Martlet doesn't sing").</summary>
    internal static PickerOption Off(string means, bool inUse, Func<Button?>? action = null) =>
        new("Off", "Off", means + ".")
        {
            IsOff = true, InUse = inUse, Badge = inUse ? "in use" : null,
            Facts = [new OptionFact("runs-on", "Runs on", "nothing: it uses no graphics card, memory or processor", "Uses nothing")],
            Action = action
        };

    /// <summary>A catalog option with its facts.</summary>
    internal static PickerOption From(ComponentOption option, string? key = null, string? summary = null) =>
        new(key ?? option.Id, option.DisplayName, summary ?? option.WhereItRuns) { Facts = OptionFacts.Of(option) };
}

/// <summary>The option picker every list of models, engines and places on the Companion pages uses, so a growing list stays
/// small: one compact row per option (its name, a badge and its key facts), the chosen option's full details under the list
/// (every fact, where it stands, its own lines and its button), and a Compare table of the facts that differ. Choosing a row
/// only shows its details; the details' button commits. Automation IDs: <c>Picker-&lt;id&gt;-&lt;key&gt;</c> (the row),
/// <c>PickerFacts-&lt;id&gt;-&lt;key&gt;</c>, <c>PickerDetail-&lt;id&gt;</c>, <c>PickerSummary-&lt;id&gt;</c>,
/// <c>PickerFact-&lt;id&gt;-&lt;fact&gt;</c>, <c>PickerState-&lt;id&gt;</c>, <c>PickerMore-&lt;id&gt;</c>, <c>PickerCompare-&lt;id&gt;</c> and
/// <c>PickerCell-&lt;id&gt;-&lt;key&gt;-&lt;fact&gt;</c>.</summary>
public partial class MainWindow
{
    /// <summary>The option each picker shows, by picker id, until the page's choice is saved (<see cref="ForgetPicker"/>).</summary>
    private readonly Dictionary<string, string> pickerShown = new(StringComparer.Ordinal);
    /// <summary>The pickers whose Compare table is open.</summary>
    private readonly HashSet<string> pickerCompare = new(StringComparer.Ordinal);
    /// <summary>The pickers that show all their rows (Show N more).</summary>
    private readonly HashSet<string> pickerMore = new(StringComparer.Ordinal);
    /// <summary>A fact value this long or shorter shares its line in the details with another short one.</summary>
    private const int PickerShortFact = 30;
    /// <summary>How many rows a picker shows before Show N more.</summary>
    private const int PickerRows = 4;

    /// <summary>Forgets which option picker <paramref name="id"/> shows, so it shows the one in use again (after a save).</summary>
    private void ForgetPicker(string id) => pickerShown.Remove(id);

    /// <summary>The option the picker shows: the one chosen in this session, else <paramref name="fallback"/>, else the one in
    /// use, else the recommended one, else the first that can run here.</summary>
    private string PickerShown(string id, IReadOnlyList<PickerOption> options, string? fallback = null) =>
        pickerShown.TryGetValue(id, out var key) && options.Any(o => o.Key == key) ? key
        : fallback is not null && options.Any(o => o.Key == fallback) ? fallback
        : (options.FirstOrDefault(o => o.InUse) ?? options.FirstOrDefault(o => o.Badge == "recommended")
           ?? options.FirstOrDefault(o => o.Unavailable is null) ?? options.FirstOrDefault())?.Key ?? "";

    /// <summary>A card with <paramref name="heading"/>, <paramref name="note"/> and the picker for <paramref name="options"/>.
    /// Choosing a row calls <paramref name="chosen"/> (default: show that option's details) and draws the page again. With
    /// <paramref name="details"/> false the list has no details panel: a card below it is the chosen option's details. It shows
    /// at most <paramref name="rows"/> rows until Show N more.</summary>
    private Border OptionPicker(string id, string heading, string? note, IReadOnlyList<PickerOption> options, string? shown = null,
        Action<string>? chosen = null, bool details = true, int rows = PickerRows)
    {
        var stack = new List<UIElement> { Heading(heading) };
        if (note is not null) stack.Add(Note(note, new Thickness(0, 0, 0, 8)));
        stack.Add(OptionPickerBody(id, options, shown, chosen, details, rows));
        return Card([.. stack]);
    }

    /// <summary>The order a picker lists its options in: the one in use, the recommended one and the shown one first, then the
    /// others as given, with those that can't run here last. The first rows are the ones shown before Show N more, so a list
    /// that keeps growing never hides the options that matter.
    /// <c>Pinned</c> is how many lead the list this way: they always show, even past the rows' limit.</summary>
    internal static (IReadOnlyList<PickerOption> Ordered, int Pinned) PickerOrder(IReadOnlyList<PickerOption> options, string shown)
    {
        var rest = options.Where(o => o.Unavailable is null).Concat(options.Where(o => o.Unavailable is not null)).ToList();
        var first = new[]
        {
            options.FirstOrDefault(o => o.InUse), options.FirstOrDefault(o => o.Badge == "recommended"), options.FirstOrDefault(o => o.Key == shown)
        }.OfType<PickerOption>().Distinct().ToList();
        return ([.. first, .. rest.Except(first)], first.Count);
    }

    /// <summary>The picker without its card: the first <paramref name="rows"/> rows (<see cref="PickerOrder"/>), Show N more
    /// (<c>PickerMore-&lt;id&gt;</c>) and Compare on one line, the table when open and the shown option's details. A list of one
    /// option has nothing to choose, so it shows only that option's details.</summary>
    private StackPanel OptionPickerBody(string id, IReadOnlyList<PickerOption> options, string? shown = null, Action<string>? chosen = null,
        bool details = true, int rows = PickerRows)
    {
        var body = new StackPanel();
        if (options.Count == 1)
        {
            var only = PickerDetails(id, options[0]);
            only.Margin = new Thickness(0, 2, 0, 0);
            body.Children.Add(only);
            return body;
        }
        var key = PickerShown(id, options, shown);
        var (ordered, pinned) = PickerOrder(options, key);
        var all = pickerMore.Contains(id);
        var hidden = all ? 0 : Math.Max(0, ordered.Count - Math.Max(pinned, Math.Max(1, rows)));
        var list = new StackPanel();
        AutomationProperties.SetName(list, "Choices");
        foreach (var option in ordered.Take(ordered.Count - hidden)) list.Children.Add(PickerRow(id, option, option.Key == key, chosen));
        body.Children.Add(list);

        var links = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
        if (hidden > 0 || all && ordered.Count > Math.Max(pinned, rows))
        {
            var more = PageButton(all ? "Show fewer" : $"Show {hidden} more", () =>
            {
                if (!pickerMore.Remove(id)) pickerMore.Add(id);
                RenderTab();
            }, link: true, id: "PickerMore-" + id);
            more.Margin = new Thickness(0, 0, 16, 0);
            links.Children.Add(more);
        }
        var comparable = ordered.Count(o => !o.IsOff) >= 2;
        var open = comparable && pickerCompare.Contains(id);
        if (comparable)
            links.Children.Add(PageButton(open ? "Hide the comparison" : "Compare them", () =>
            {
                if (!pickerCompare.Remove(id)) pickerCompare.Add(id);
                RenderTab();
            }, link: true, id: "PickerCompare-" + id));
        if (links.Children.Count > 0) body.Children.Add(links);
        // The comparison covers every option, also those Show N more hides.
        if (open) body.Children.Add(PickerTable(id, ordered.Where(o => !o.IsOff).ToList()));

        if (details && ordered.FirstOrDefault(o => o.Key == key) is { } selected) body.Children.Add(PickerDetails(id, selected));
        return body;
    }

    /// <summary>One compact row: a radio button with the name, its badge and its short facts in one line.</summary>
    private RadioButton PickerRow(string id, PickerOption option, bool isShown, Action<string>? chosen)
    {
        var line = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var name = new System.Windows.Documents.Run(option.Name) { FontWeight = FontWeights.SemiBold };
        line.Inlines.Add(name);
        if (option.Badge is { } badge)
        {
            var tag = new System.Windows.Documents.Run("  \u00b7  " + badge) { FontSize = 13 };
            tag.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, badge == "in use" ? "SuccessBrush" : "AccentBrush");
            line.Inlines.Add(tag);
        }
        var shortFacts = option.Unavailable is { } why ? "Can't run here: " + why
            : string.Join(" \u00b7 ", option.Facts.Select(f => f.Short).OfType<string>().Take(3));
        var facts = new TextBlock { Text = shortFacts, TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Margin = new Thickness(12, 1, 0, 0),
            VerticalAlignment = VerticalAlignment.Center };
        facts.SetResourceReference(StyleProperty, "Muted");
        if (option.Unavailable is not null) facts.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
        AutomationProperties.SetAutomationId(facts, $"PickerFacts-{id}-{option.Key}");
        // The name and its facts share one line, so a long list stays short; the facts wrap beside the name when narrow.
        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.VerticalAlignment = VerticalAlignment.Center;
        line.MaxWidth = 300;
        content.Children.Add(line);
        if (shortFacts.Length > 0)
        {
            Grid.SetColumn(facts, 1);
            content.Children.Add(facts);
        }
        var row = new RadioButton { Content = content, GroupName = "Picker-" + id, IsChecked = isShown, Margin = new Thickness(0, 0, 0, 5),
            Opacity = option.Unavailable is null ? 1 : 0.75, ToolTip = option.Summary, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(row, $"{option.Name}{(option.Badge is null ? "" : " · " + option.Badge)}: {shortFacts}");
        AutomationProperties.SetAutomationId(row, $"Picker-{id}-{option.Key}");
        row.Checked += (_, _) =>
        {
            if (pickerShown.TryGetValue(id, out var was) && was == option.Key) return;
            pickerShown[id] = option.Key;
            chosen?.Invoke(option.Key);
            RenderTab();
        };
        return row;
    }

    /// <summary>The shown option in full: its name, strength, every fact, where it stands, its own lines and its button.</summary>
    private Border PickerDetails(string id, PickerOption option)
    {
        var stack = new StackPanel();
        var title = OptionTitle(option.Name, option.Badge, 15);
        AutomationProperties.SetName(title, option.Name + (option.Badge is null ? "" : " · " + option.Badge));
        AutomationProperties.SetAutomationId(title, "PickerDetail-" + id);
        stack.Children.Add(title);
        var summary = Note(option.Summary, new Thickness(0, 2, 0, 6));
        AutomationProperties.SetAutomationId(summary, "PickerSummary-" + id);
        stack.Children.Add(summary);
        if (option.Facts.Count > 0)
        {
            // Short facts sit two to a line and long ones take a whole line, so a long list of facts stays short.
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var right = false;
            foreach (var fact in option.Facts)
            {
                var wide = fact.Value.Length > PickerShortFact;
                var column = right && !wide ? 2 : 0;
                if (column == 0) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                right = column == 0 && !wide;
                var row = grid.RowDefinitions.Count - 1;
                var label = new TextBlock { Text = fact.Label, Margin = new Thickness(column == 0 ? 0 : 10, 1, 12, 1), ToolTip = fact.Help };
                label.SetResourceReference(StyleProperty, "Muted");
                Grid.SetRow(label, row);
                Grid.SetColumn(label, column);
                var value = new TextBlock { Text = fact.Value, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1), ToolTip = fact.Help };
                AutomationProperties.SetName(value, $"{fact.Label}: {fact.Value}");
                AutomationProperties.SetAutomationId(value, $"PickerFact-{id}-{fact.Key}");
                Grid.SetRow(value, row);
                Grid.SetColumn(value, column + 1);
                if (wide) Grid.SetColumnSpan(value, 3);
                grid.Children.Add(label);
                grid.Children.Add(value);
            }
            stack.Children.Add(grid);
        }
        if (option.Unavailable is { } why || option.State is not null)
        {
            var state = Note(option.Unavailable is { } cannot ? "Can't run here: " + cannot : option.State!, new Thickness(0, 4, 0, 0));
            if (option.Warn || option.Unavailable is not null) state.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
            AutomationProperties.SetAutomationId(state, "PickerState-" + id);
            stack.Children.Add(state);
        }
        foreach (var element in option.Details?.Invoke() ?? []) stack.Children.Add(element);
        if (option.Action?.Invoke() is { } button)
        {
            if (option.Unavailable is { } blocked)
            {
                button.IsEnabled = false;
                button.ToolTip = blocked;
                ToolTipService.SetShowOnDisabled(button, true);
                AutomationProperties.SetHelpText(button, blocked);
            }
            stack.Children.Add(Row(button));
        }
        var panel = new Border { Child = stack, BorderThickness = new Thickness(option.InUse ? 2 : 1), CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 6, 0, 0) };
        panel.SetResourceReference(Border.BorderBrushProperty, option.InUse ? "AccentBrush" : "BorderBrush");
        AutomationProperties.SetName(panel, option.Name + " details");
        return panel;
    }

    /// <summary>The Compare table: one row per option, one column per fact that differs between them.</summary>
    private static ScrollViewer PickerTable(string id, IReadOnlyList<PickerOption> options)
    {
        var keys = options.SelectMany(o => o.Facts.Select(f => f.Key)).Distinct(StringComparer.Ordinal)
            .Where(key => options.Select(o => o.Facts.FirstOrDefault(f => f.Key == key)?.Value ?? "").Distinct(StringComparer.Ordinal).Count() > 1)
            .ToList();
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        foreach (var _ in keys) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        void Cell(int row, int column, string text, string? automationId, bool header)
        {
            var cell = new TextBlock { Text = text, Margin = new Thickness(0, 2, 16, 2), TextWrapping = TextWrapping.Wrap, MaxWidth = 220 };
            if (header) cell.FontWeight = FontWeights.SemiBold;
            if (automationId is not null) AutomationProperties.SetAutomationId(cell, automationId);
            Grid.SetRow(cell, row);
            Grid.SetColumn(cell, column);
            grid.Children.Add(cell);
        }
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Cell(0, 0, "", null, true);
        for (var c = 0; c < keys.Count; c++)
            Cell(0, c + 1, options.SelectMany(o => o.Facts).First(f => f.Key == keys[c]).Label, null, true);
        for (var r = 0; r < options.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Cell(r + 1, 0, options[r].Name, null, true);
            for (var c = 0; c < keys.Count; c++)
            {
                var fact = options[r].Facts.FirstOrDefault(f => f.Key == keys[c]);
                Cell(r + 1, c + 1, fact?.Short ?? fact?.Value ?? "\u2013", $"PickerCell-{id}-{options[r].Key}-{keys[c]}", false);
            }
        }
        var scroll = new ScrollViewer { Content = grid, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        AutomationProperties.SetName(scroll, "Comparison");
        AutomationProperties.SetAutomationId(scroll, "PickerTable-" + id);
        return scroll;
    }
}
