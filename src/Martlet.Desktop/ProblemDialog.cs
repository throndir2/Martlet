using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Martlet.Presentation;

namespace Martlet.Desktop;

/// <summary>Tells the owner something went wrong, in a read-only box whose Copy button (Copy-ProblemText) takes the whole
/// report, so it pastes straight into a chat when asking for help. <paramref name="openLogs"/> adds Open logs folder.</summary>
internal sealed class ProblemDialog : ThemedWindow
{
    private ProblemDialog(string title, string heading, string report, Action? openLogs)
    {
        SetResourceReference(StyleProperty, "AppWindowStyle");
        Title = title;
        Width = 720;
        MinWidth = 460;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        AutomationProperties.SetAutomationId(this, "ProblemDialog");
        var root = new StackPanel { Margin = new Thickness(24) };
        var headingText = new TextBlock { Text = heading, TextWrapping = TextWrapping.Wrap };
        headingText.SetResourceReference(StyleProperty, "SectionHeading");
        AutomationProperties.SetAutomationId(headingText, "ProblemHeading");
        root.Children.Add(headingText);
        var text = new TextBox
        {
            Text = report, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 360,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Consolas"), FontSize = 12,
            Margin = new Thickness(0, 4, 0, 16)
        };
        AutomationProperties.SetAutomationId(text, "ProblemText");
        AutomationProperties.SetName(text, "What happened");
        CopyText.SetContext(text, $"{CopyText.Product}: {heading}");
        root.Children.Add(text);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        if (openLogs is not null)
        {
            var logs = new Button { Content = "_Open logs folder", MinWidth = 90, Margin = new Thickness(0, 0, 12, 0) };
            AutomationProperties.SetAutomationId(logs, "ProblemOpenLogs");
            logs.Click += (_, _) => openLogs();
            buttons.Children.Add(logs);
        }
        var close = new Button { Content = "_Close", IsDefault = true, IsCancel = true, MinWidth = 90 };
        close.SetResourceReference(StyleProperty, "PrimaryButton");
        AutomationProperties.SetAutomationId(close, "ProblemClose");
        close.Click += (_, _) => Close();
        buttons.Children.Add(close);
        root.Children.Add(buttons);
        Content = root;
        Loaded += (_, _) => close.Focus();
    }

    /// <summary>Shows the report and waits until it is closed. Returns false when the dialog couldn't be shown (the caller
    /// falls back to a plain message box).</summary>
    internal static bool Show(Window? owner, string title, string heading, string report, Action? openLogs = null)
    {
        try
        {
            var dialog = new ProblemDialog(title, heading, report, openLogs);
            if (owner is { IsVisible: true }) dialog.Owner = owner;
            else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dialog.ShowDialog();
            return true;
        }
        catch (Exception error) when (!ErrorLog.IsFatal(error))
        {
            ErrorLog.Error("The problem dialog couldn't be shown", error);
            return false;
        }
    }
}
