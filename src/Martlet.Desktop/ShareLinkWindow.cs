using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Martlet.Desktop;

/// <summary>Shows a share link (docs/NETWORK.md, "Sharing with a link") so the owner can copy it and send it to their friend:
/// what it shares, the link itself (read-only) and Copy link.</summary>
internal sealed class ShareLinkWindow : ThemedWindow
{
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };

    internal ShareLinkWindow(string heading, string message, string link, string? warning)
    {
        SetResourceReference(StyleProperty, "AppWindowStyle");
        Title = "Martlet - Share link";
        Width = 640;
        MinWidth = 420;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        AutomationProperties.SetAutomationId(this, "ShareLinkWindow");
        var root = new StackPanel { Margin = new Thickness(24) };
        var title = new TextBlock { Text = heading, TextWrapping = TextWrapping.Wrap };
        title.SetResourceReference(StyleProperty, "SectionHeading");
        AutomationProperties.SetAutomationId(title, "ShareLinkHeading");
        root.Children.Add(title);
        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 8) };
        AutomationProperties.SetAutomationId(text, "ShareLinkMessage");
        root.Children.Add(text);
        if (warning is not null)
        {
            var note = new TextBlock { Text = warning, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
            note.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
            AutomationProperties.SetAutomationId(note, "ShareLinkWarning");
            AutomationProperties.SetName(note, warning);
            root.Children.Add(note);
        }
        var box = new TextBox
        {
            Text = link, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 140,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new System.Windows.Media.FontFamily("Consolas")
        };
        AutomationProperties.SetAutomationId(box, "ShareLinkText");
        AutomationProperties.SetName(box, "Share link");
        root.Children.Add(box);
        status.SetResourceReference(StyleProperty, "Muted");
        AutomationProperties.SetAutomationId(status, "ShareLinkStatus");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        root.Children.Add(status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var copy = new Button { Content = "_Copy link", MinWidth = 110, Margin = new Thickness(0, 0, 12, 0) };
        copy.SetResourceReference(StyleProperty, "PrimaryButton");
        AutomationProperties.SetAutomationId(copy, "ShareLinkCopy");
        copy.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(link);
                status.Text = "Copied. Send it to your friend in a private message; anyone with the link can use what it shares.";
            }
            catch (System.Runtime.InteropServices.COMException) { status.Text = "Couldn't copy; select the link and copy it yourself."; }
        };
        var close = new Button { Content = "Close", IsCancel = true, MinWidth = 90 };
        AutomationProperties.SetAutomationId(close, "ShareLinkClose");
        buttons.Children.Add(copy);
        buttons.Children.Add(close);
        root.Children.Add(buttons);
        Content = root;
    }
}
