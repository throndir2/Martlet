using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using Martlet.Core.Text;

namespace Martlet.Desktop;

/// <summary>A small round "?" that holds an explanation, so a page shows only what you need to act. Pointing at it shows
/// <see cref="Text"/> as a tooltip; clicking it (or Enter or Space) keeps the text open in a small card until you click
/// elsewhere or press Escape. With a <see cref="Wiki"/> page, the card also has a "Read more in the wiki" link to the full
/// instructions. Screen readers and MCP read the text as its help text.
/// <para>Automation IDs (docs/MCP.md): the "?" is Help-&lt;id&gt;, the open card's text Help-&lt;id&gt;-Card (its name is the
/// text) and the link Help-&lt;id&gt;-Wiki (its help text is the URL). How to move a long text behind a "?":
/// docs/UI_DESIGN.md#help-marks-and-the-wiki.</para></summary>
public sealed partial class HelpTip : Button
{
    /// <summary>Where the wiki's pages are (docs/wiki/ is their source).</summary>
    internal const string WikiAddress = "https://github.com/throndir2/Martlet/wiki/";

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(HelpTip),
        new PropertyMetadata("", (owner, _) => ((HelpTip)owner).Show()));

    public static readonly DependencyProperty WikiProperty = DependencyProperty.Register(nameof(Wiki), typeof(string), typeof(HelpTip),
        new PropertyMetadata(null, (owner, _) => ((HelpTip)owner).Show()));

    private readonly Popup card;
    private readonly TextBlock shown;
    private readonly TextBlock linkLine;
    private readonly Hyperlink link;
    private Window? owner;

    public HelpTip()
    {
        SetResourceReference(StyleProperty, "HelpTipButton");
        shown = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 380, FontSize = 13 };
        shown.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        link = new Hyperlink(new Run("Read more in the wiki")) { Cursor = Cursors.Hand };
        link.SetResourceReference(TextElement.ForegroundProperty, "AccentBrush");
        AutomationProperties.SetName(link, "Read more in the wiki");
        link.Click += (_, _) => OpenWiki();
        linkLine = new TextBlock { FontSize = 13, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        linkLine.Inlines.Add(link);
        var border = new Border
        {
            Child = new StackPanel { Children = { shown, linkLine } }, Padding = new Thickness(14, 10, 14, 10),
            CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1)
        };
        border.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        card = new Popup
        {
            // The card closes itself (a click elsewhere, Escape, the window losing the focus, the page going away): a popup that
            // closes on its own loses the mouse at once while another program is in front, and then MCP could never read it.
            Child = border, PlacementTarget = this, Placement = PlacementMode.Bottom, StaysOpen = true, AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade, VerticalOffset = 4
        };
        // The tooltip and the open card say the same, so the tooltip stays away while the card is open.
        card.Opened += (_, _) =>
        {
            NameCard();
            ToolTipService.SetIsEnabled(this, false);
            owner = Window.GetWindow(this);
            if (owner is null) return;
            owner.PreviewMouseDown += ClickedElsewhere;
            owner.PreviewKeyDown += EscapePressed;
            owner.Deactivated += WindowLeft;
            owner.LocationChanged += WindowLeft;
        };
        card.Closed += (_, _) =>
        {
            ToolTipService.SetIsEnabled(this, true);
            if (owner is null) return;
            owner.PreviewMouseDown -= ClickedElsewhere;
            owner.PreviewKeyDown -= EscapePressed;
            owner.Deactivated -= WindowLeft;
            owner.LocationChanged -= WindowLeft;
            owner = null;
        };
        Unloaded += (_, _) => card.IsOpen = false;
        ToolTipService.SetInitialShowDelay(this, 250);
        ToolTipService.SetShowDuration(this, 60_000);
        AutomationProperties.SetName(this, "More information");
    }

    /// <summary>The explanation: one or two short sentences. The full instructions go to the <see cref="Wiki"/>.</summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>The wiki page and, after a '#', the heading on it that has the full instructions, such as
    /// "Thinking#check-ins" (docs/wiki/Thinking.md, heading "Check-ins"). Null for no link.</summary>
    public string? Wiki
    {
        get => (string?)GetValue(WikiProperty);
        set => SetValue(WikiProperty, value);
    }

    /// <summary>Whether the card with the text is open (it was clicked).</summary>
    public bool IsOpen => card.IsOpen;

    /// <summary>The address of a <see cref="Wiki"/> value: https://github.com/throndir2/Martlet/wiki/Page#anchor.</summary>
    internal static string WikiUrl(string wiki) => WikiValue().IsMatch(wiki) ? WikiAddress + wiki
        : throw new ArgumentException($"'{wiki}' is not a wiki page and anchor, such as Thinking#check-ins.", nameof(wiki));

    /// <summary>A "?" about <paramref name="topic"/> ("check-ins") with the automation ID Help-<paramref name="id"/>. Its card
    /// shows <paramref name="text"/> and, with <paramref name="wiki"/> (such as "Thinking#check-ins"), a link to that wiki
    /// section.</summary>
    public static HelpTip For(string topic, string text, string id, string? wiki = null)
    {
        var tip = new HelpTip { Text = text, Wiki = wiki };
        AutomationProperties.SetName(tip, "About " + topic);
        AutomationProperties.SetAutomationId(tip, "Help-" + id);
        tip.NameCard();
        return tip;
    }

    protected override void OnClick()
    {
        base.OnClick();
        card.IsOpen = !card.IsOpen;
    }

    private void ClickedElsewhere(object sender, MouseButtonEventArgs e)
    {
        // A click on the "?" itself opens or closes the card in OnClick.
        for (var at = e.OriginalSource as DependencyObject; at is not null;
             at = at is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(at) : LogicalTreeHelper.GetParent(at))
            if (ReferenceEquals(at, this)) return;
        card.IsOpen = false;
    }

    private void EscapePressed(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        card.IsOpen = false;
        e.Handled = true;
    }

    private void WindowLeft(object? sender, EventArgs e) => card.IsOpen = false;

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

    private void Show()
    {
        var text = Text ?? "";
        shown.Text = text;
        shown.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        ToolTip = text.Length == 0 ? null : new ToolTip { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 380 } };
        AutomationProperties.SetHelpText(this, text);
        AutomationProperties.SetName(shown, text);
        var url = Wiki is { Length: > 0 } wiki ? WikiUrl(wiki) : null;
        linkLine.Visibility = url is null ? Visibility.Collapsed : Visibility.Visible;
        linkLine.Margin = new Thickness(0, text.Length == 0 ? 0 : 8, 0, 0);
        AutomationProperties.SetHelpText(link, url ?? "");
        link.ToolTip = url;
        Visibility = text.Length == 0 && url is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Gives the card's text and link the IDs that go with the "?": Help-&lt;id&gt;-Card and Help-&lt;id&gt;-Wiki.</summary>
    private void NameCard()
    {
        if (AutomationProperties.GetAutomationId(this) is not { Length: > 0 } id) return;
        AutomationProperties.SetAutomationId(shown, id + "-Card");
        AutomationProperties.SetAutomationId(link, id + "-Wiki");
    }

    private void OpenWiki()
    {
        card.IsOpen = false;
        if (Wiki is not { Length: > 0 } wiki) return;
        try { Process.Start(new ProcessStartInfo(WikiUrl(wiki)) { UseShellExecute = true })?.Dispose(); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ErrorLog.Warn("The wiki page couldn't open in the browser: " + WikiUrl(wiki), error);
        }
    }

    [GeneratedRegex(@"^[A-Z][A-Za-z0-9-]*(#[a-z0-9][a-z0-9-]*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex WikiValue();

    // ---------- short explanations ----------

    /// <summary>Splits <paramref name="text"/> after its first sentence: the page shows only that sentence. More is null when
    /// the text has only one sentence.</summary>
    internal static (string Summary, string? More) Split(string text) => Sentences.Split(text);

    /// <summary>A muted explanation that shows its first sentence and puts all of it behind a "?" at the end of the line. With
    /// <paramref name="wiki"/>, the "?" is there also for one sentence, and its card links to that wiki section.</summary>
    internal static TextBlock Explain(string text, Thickness margin, string id, string? topic = null, string? wiki = null)
    {
        var (summary, rest) = Split(text);
        var block = new TextBlock { Margin = margin };
        block.SetResourceReference(FrameworkElement.StyleProperty, "Muted");
        block.TextWrapping = TextWrapping.Wrap;
        block.Inlines.Add(new Run(summary));
        // Screen readers and MCP read the summary as its name and all of the text as its help.
        AutomationProperties.SetName(block, summary);
        if (rest is not null) AutomationProperties.SetHelpText(block, text);
        if (rest is not null || wiki is not null)
        {
            var tip = For(topic ?? "this", text, id, wiki);
            tip.Margin = new Thickness(6, 0, 0, 0);
            block.Inlines.Add(new InlineUIContainer(tip) { BaselineAlignment = BaselineAlignment.Center });
        }
        return block;
    }

    /// <summary><paramref name="label"/> (a heading, a label or a check box) with a "?" after it; its card shows
    /// <paramref name="text"/> and, with <paramref name="wiki"/>, links to that wiki section.</summary>
    internal static StackPanel After(FrameworkElement label, string topic, string text, string id, string? wiki = null)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = label.Margin };
        label.Margin = new Thickness(0);
        label.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(label);
        row.Children.Add(For(topic, text, id, wiki));
        return row;
    }
}
