using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Martlet.Desktop;

/// <summary>A short question with one button per choice (account security: *Continue as ...?*, *Choose another account*).
/// <see cref="Chosen"/> is the automation ID of the button pressed, or null when closed.</summary>
internal sealed class AccountChoiceDialog : ThemedWindow
{
    internal string? Chosen { get; private set; }

    internal AccountChoiceDialog(string automationId, string title, string heading, string text, IReadOnlyList<(string Id, string Label)> choices)
    {
        SetResourceReference(StyleProperty, "AppWindowStyle");
        Title = "Martlet - " + title;
        Width = 480;
        MinWidth = 360;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        AutomationProperties.SetAutomationId(this, automationId);
        var root = new StackPanel { Margin = new Thickness(24) };
        var title1 = new TextBlock { Text = heading, TextWrapping = TextWrapping.Wrap };
        title1.SetResourceReference(StyleProperty, "SectionHeading");
        AutomationProperties.SetAutomationId(title1, automationId + "Heading");
        root.Children.Add(title1);
        root.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 14) });
        var first = true;
        foreach (var (id, label) in choices)
        {
            var button = new Button { Content = label, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 8), MinHeight = 32 };
            if (first) button.SetResourceReference(StyleProperty, "PrimaryButton");
            first = false;
            AutomationProperties.SetAutomationId(button, id);
            button.Click += (_, _) =>
            {
                Chosen = id;
                DialogResult = true;
            };
            root.Children.Add(button);
        }
        Content = root;
    }
}
