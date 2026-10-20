using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Martlet.Companion;

/// <summary>A small "?" that holds an explanation, as the Windows app's HelpTip does, so the page shows one short line. Pointing at
/// it shows <see cref="Text"/>; clicking it opens a small card with the text and, with a <see cref="Wiki"/> page, a "Read more in
/// the wiki" link. Automation IDs: the "?" is Help-&lt;id&gt; (set in XAML), the card's text Help-&lt;id&gt;-Card and the link
/// Help-&lt;id&gt;-Wiki.</summary>
public sealed class HelpMark : Button
{
    internal const string WikiAddress = "https://github.com/throndir2/Martlet/wiki/";

    public static readonly StyledProperty<string> TextProperty = AvaloniaProperty.Register<HelpMark, string>(nameof(Text), "");
    public static readonly StyledProperty<string?> WikiProperty = AvaloniaProperty.Register<HelpMark, string?>(nameof(Wiki));

    private readonly TextBlock shown = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 360 };
    private readonly HyperlinkButton link = new() { Content = "Read more in the wiki", IsVisible = false };

    protected override Type StyleKeyOverride => typeof(Button);

    public HelpMark()
    {
        Content = "?";
        Padding = new Thickness(7, 0);
        MinWidth = 0;
        VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(this, "More information");
        Flyout = new Flyout { Content = new StackPanel { Spacing = 8, Children = { shown, link } } };
    }

    /// <summary>The explanation: one or two short sentences. The full instructions go to the <see cref="Wiki"/>.</summary>
    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>The wiki page and heading anchor, such as "Linux-and-Mac#what-the-early-app-can-do".</summary>
    public string? Wiki
    {
        get => GetValue(WikiProperty);
        set => SetValue(WikiProperty, value);
    }

    /// <summary>The address of a <see cref="Wiki"/> value.</summary>
    internal static Uri WikiUrl(string wiki) => new(WikiAddress + wiki);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty)
        {
            shown.Text = Text;
            ToolTip.SetTip(this, string.IsNullOrEmpty(Text) ? null : Text);
            AutomationProperties.SetHelpText(this, Text);
            AutomationProperties.SetName(shown, Text);
        }
        else if (change.Property == WikiProperty)
        {
            link.NavigateUri = Wiki is { Length: > 0 } wiki ? WikiUrl(wiki) : null;
            link.IsVisible = link.NavigateUri is not null;
            AutomationProperties.SetHelpText(link, link.NavigateUri?.ToString() ?? "");
        }
        else if (change.Property == AutomationProperties.AutomationIdProperty && AutomationProperties.GetAutomationId(this) is { Length: > 0 } id)
        {
            AutomationProperties.SetAutomationId(shown, id + "-Card");
            AutomationProperties.SetAutomationId(link, id + "-Wiki");
        }
    }
}
