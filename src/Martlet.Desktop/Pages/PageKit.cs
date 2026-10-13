using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>The small builders every settings page uses: a card, its heading, a muted note, a warning line, a row of buttons, a
/// page button and a labelled row. They only make WPF controls with the app's styles; they read and change nothing else.
/// Martlet.Desktop.csproj imports this class everywhere (<c>&lt;Using Include="Martlet.Desktop.PageKit" Static="true" /&gt;</c>),
/// so a page calls <c>Card(...)</c> or <c>Note(...)</c> directly.</summary>
internal static class PageKit
{
    /// <summary>A card (the CardStyle border) with <paramref name="children"/> stacked in it.</summary>
    internal static Border Card(params UIElement[] children)
    {
        var stack = new StackPanel();
        foreach (var child in children) stack.Children.Add(child);
        var card = new Border { Child = stack };
        card.SetResourceReference(FrameworkElement.StyleProperty, "CardStyle");
        return card;
    }

    /// <summary>A card's heading (the SectionHeading style).</summary>
    internal static TextBlock Heading(string text)
    {
        var heading = new TextBlock { Text = text, Margin = new Thickness(0, 0, 0, 6) };
        heading.SetResourceReference(FrameworkElement.StyleProperty, "SectionHeading");
        return heading;
    }

    /// <summary>A muted line of text that wraps.</summary>
    internal static TextBlock Note(string text, Thickness margin)
    {
        var note = new TextBlock { Text = text, Margin = margin };
        note.SetResourceReference(FrameworkElement.StyleProperty, "Muted");
        note.TextWrapping = TextWrapping.Wrap;
        return note;
    }

    /// <summary>A line of text in the warning colour that wraps.</summary>
    internal static TextBlock Warning(string text)
    {
        var warning = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
        warning.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
        return warning;
    }

    /// <summary>A wrapping row of <paramref name="buttons"/>; null buttons are left out.</summary>
    internal static WrapPanel Row(params Button?[] buttons)
    {
        var row = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var button in buttons.OfType<Button>())
        {
            button.Margin = new Thickness(0, 0, 10, 6);
            row.Children.Add(button);
        }
        return row;
    }

    /// <summary>A page button that runs <paramref name="run"/> when clicked: plain, <paramref name="primary"/> or a
    /// <paramref name="link"/>. <paramref name="id"/> is its automation ID; its automation name is its label.</summary>
    internal static Button PageButton(string label, Action run, bool primary = false, bool link = false, string? id = null)
    {
        var button = new Button { Content = label, MinWidth = link ? 0 : 96 };
        if (primary) button.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryButton");
        else if (link) button.SetResourceReference(FrameworkElement.StyleProperty, "LinkButton");
        AutomationProperties.SetName(button, label);
        if (id is not null) AutomationProperties.SetAutomationId(button, id);
        button.Click += (_, _) => run();
        return button;
    }

    /// <summary>A row with a 90-pixel <paramref name="label"/> on the left and <paramref name="control"/> beside it.</summary>
    internal static DockPanel TerminalRow(string label, UIElement control)
    {
        var row = new DockPanel { Margin = new Thickness(0, 8, 0, 0), LastChildFill = true };
        var name = new TextBlock { Text = label, Width = 90, VerticalAlignment = control is ComboBox ? VerticalAlignment.Center : VerticalAlignment.Top };
        DockPanel.SetDock(name, Dock.Left);
        row.Children.Add(name);
        if (control is ComboBox box) box.HorizontalAlignment = HorizontalAlignment.Left;
        row.Children.Add(control);
        return row;
    }

    /// <summary>Takes the key typed in <paramref name="box"/> as a <see cref="SecretLease"/> and clears the box and every copy
    /// of the key in memory.</summary>
    internal static SecretLease TakeKey(PasswordBox box)
    {
        using var secure = box.SecurePassword;
        var chars = new char[secure.Length];
        var pointer = Marshal.SecureStringToGlobalAllocUnicode(secure);
        try
        {
            Marshal.Copy(pointer, chars, 0, chars.Length);
            return new SecretLease(chars);
        }
        finally
        {
            box.Clear();
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(chars.AsSpan()));
            Marshal.ZeroFreeGlobalAllocUnicode(pointer);
        }
    }
}
