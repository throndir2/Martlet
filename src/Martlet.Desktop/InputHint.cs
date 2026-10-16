using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

// Shared with the character renderer and the MCP server, which compile Themes\Controls.xaml too.
namespace Martlet.Presentation;

/// <summary>Example text in an empty text box or password box, so you see what to type there (for example
/// "https://llm.example.com/v1"). The box's template (InputTemplate in Themes\Controls.xaml) shows it dimmed while the box is
/// empty and hides it at the first character. Screen readers and MCP's ui_status read it as the box's help text when the box
/// has no help text of its own. Every box you type in has one.</summary>
public static class InputHint
{
    /// <summary>The example text; null or empty shows nothing.</summary>
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(InputHint), new FrameworkPropertyMetadata(null, OnTextChanged));

    private static readonly DependencyPropertyKey ShownKey = DependencyProperty.RegisterAttachedReadOnly(
        "Shown", typeof(bool), typeof(InputHint), new FrameworkPropertyMetadata(false));

    /// <summary>Whether the box shows its example now: it has one and nothing is typed.</summary>
    public static readonly DependencyProperty ShownProperty = ShownKey.DependencyProperty;

    public static string? GetText(DependencyObject element) => (string?)element.GetValue(TextProperty);
    public static void SetText(DependencyObject element, string? value) => element.SetValue(TextProperty, value);
    public static bool GetShown(DependencyObject element) => (bool)element.GetValue(ShownProperty);

    /// <summary>Gives <paramref name="box"/> the example <paramref name="text"/> and returns the box, for object initializers.</summary>
    public static T With<T>(T box, string text) where T : Control
    {
        SetText(box, text);
        return box;
    }

    private static void OnTextChanged(DependencyObject element, DependencyPropertyChangedEventArgs change)
    {
        switch (element)
        {
            case TextBox box:
                box.TextChanged -= Refresh;
                box.TextChanged += Refresh;
                break;
            case PasswordBox box:
                box.PasswordChanged -= Refresh;
                box.PasswordChanged += Refresh;
                break;
        }
        var help = AutomationProperties.GetHelpText(element);
        if (string.IsNullOrEmpty(help) || help == change.OldValue as string)
            AutomationProperties.SetHelpText(element, change.NewValue as string ?? "");
        Update(element);
    }

    private static void Refresh(object sender, RoutedEventArgs e) => Update((DependencyObject)sender);

    private static void Update(DependencyObject element)
    {
        var empty = element switch
        {
            TextBox box => box.Text.Length == 0,
            PasswordBox box => IsEmpty(box),
            _ => false
        };
        element.SetValue(ShownKey, empty && !string.IsNullOrEmpty(GetText(element)));
    }

    private static bool IsEmpty(PasswordBox box)
    {
        using var password = box.SecurePassword;
        return password.Length == 0;
    }
}
