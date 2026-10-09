using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;

namespace Martlet.Desktop;

/// <summary>A small round "?" that holds an explanation, so a page shows only what you need to act. Pointing at it shows
/// <see cref="Text"/> as a tooltip; clicking it (or Enter or Space) keeps the text open in a small card until you click
/// elsewhere or press Escape. Screen readers and MCP read the text as its help text.</summary>
public sealed class HelpTip : Button
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(HelpTip),
        new PropertyMetadata("", (owner, change) => ((HelpTip)owner).Show((string?)change.NewValue ?? "")));

    private readonly Popup card;
    private readonly TextBlock shown;
    private long closedAt;

    public HelpTip()
    {
        SetResourceReference(StyleProperty, "HelpTipButton");
        shown = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 380, FontSize = 13 };
        shown.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        var border = new Border { Child = shown, Padding = new Thickness(14, 10, 14, 10), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1) };
        border.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        card = new Popup
        {
            Child = border, PlacementTarget = this, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade, VerticalOffset = 4
        };
        // The tooltip and the open card say the same, so the tooltip stays away while the card is open.
        card.Opened += (_, _) => ToolTipService.SetIsEnabled(this, false);
        card.Closed += (_, _) =>
        {
            closedAt = Environment.TickCount64;
            ToolTipService.SetIsEnabled(this, true);
        };
        ToolTipService.SetInitialShowDelay(this, 250);
        ToolTipService.SetShowDuration(this, 60_000);
        AutomationProperties.SetName(this, "More information");
    }

    /// <summary>The explanation.</summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>Whether the card with the text is open (it was clicked).</summary>
    public bool IsOpen => card.IsOpen;

    /// <summary>A "?" about <paramref name="topic"/> ("Check-ins") with the automation ID Help-<paramref name="id"/>.</summary>
    public static HelpTip For(string topic, string text, string id)
    {
        var tip = new HelpTip { Text = text };
        AutomationProperties.SetName(tip, "About " + topic);
        AutomationProperties.SetAutomationId(tip, "Help-" + id);
        return tip;
    }

    protected override void OnClick()
    {
        base.OnClick();
        // A click on the "?" while its card is open closes the card (the card closes on the mouse press); it doesn't open it again.
        if (Environment.TickCount64 - closedAt < 300) return;
        card.IsOpen = !card.IsOpen;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && card.IsOpen)
        {
            card.IsOpen = false;
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    private void Show(string text)
    {
        shown.Text = text;
        ToolTip = text.Length == 0 ? null : new ToolTip { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 380 } };
        AutomationProperties.SetHelpText(this, text);
        Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---------- short explanations ----------

    private static readonly Regex SentenceEnd = new(@"(?<!\b(?:e\.g|i\.e|etc|vs|Mr|Dr))[.!?][""”)]?\s+(?=[""“(]?[A-Z0-9{])",
        RegexOptions.CultureInvariant);

    /// <summary>The fewest characters a summary keeps, so a page never shows only "Off by default."</summary>
    internal const int SummaryMinimum = 40;

    /// <summary>Splits <paramref name="text"/> after its first sentence (or the first sentences, until there are at least
    /// <see cref="SummaryMinimum"/> characters). More is null when nothing worth hiding is left.</summary>
    internal static (string Summary, string? More) Split(string text)
    {
        foreach (Match end in SentenceEnd.Matches(text))
        {
            var at = end.Index + end.Length;
            if (at < SummaryMinimum) continue;
            var rest = text[at..].Trim();
            return rest.Length < SummaryMinimum ? (text, null) : (text[..at].TrimEnd(), rest);
        }
        return (text, null);
    }

    /// <summary>A muted explanation that shows its first sentence and puts all of it behind a "?" at the end of the line.</summary>
    internal static TextBlock Explain(string text, Thickness margin, string id, string? topic = null)
    {
        var (summary, rest) = Split(text);
        var block = new TextBlock { Margin = margin };
        block.SetResourceReference(FrameworkElement.StyleProperty, "Muted");
        block.TextWrapping = TextWrapping.Wrap;
        block.Inlines.Add(new Run(summary));
        // Screen readers and MCP read the summary as its name and all of the text as its help.
        AutomationProperties.SetName(block, summary);
        if (rest is not null)
        {
            AutomationProperties.SetHelpText(block, text);
            var tip = For(topic ?? "this", text, id);
            tip.Margin = new Thickness(6, 0, 0, 0);
            block.Inlines.Add(new InlineUIContainer(tip) { BaselineAlignment = BaselineAlignment.Center });
        }
        return block;
    }

    /// <summary><paramref name="label"/> (a heading, a label or a check box) with a "?" after it.</summary>
    internal static StackPanel After(FrameworkElement label, string topic, string text, string id)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = label.Margin };
        label.Margin = new Thickness(0);
        label.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(label);
        row.Children.Add(For(topic, text, id));
        return row;
    }
}
