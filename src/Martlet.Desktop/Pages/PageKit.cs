using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>The small builders every settings page uses: a card, its heading, a muted note, a warning line, a row of buttons, a
/// page button, a labelled row and the dense editor rows (a compact control, a row label, a row group and a row switch). They
/// only make WPF controls with the app's styles; they read and change nothing else.
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

    /// <summary>A large radio choice in <paramref name="group"/>: its <paramref name="title"/> in bold and its
    /// <paramref name="detail"/> under it. Its automation name is both; <paramref name="id"/> is its automation ID.</summary>
    internal static RadioButton Choice(string group, string title, string detail, bool isChecked, string id)
    {
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        text.Children.Add(Note(detail, new Thickness(0, 2, 0, 0)));
        var option = new RadioButton { Content = text, GroupName = group, IsChecked = isChecked, Margin = new Thickness(0, 0, 0, 10) };
        AutomationProperties.SetName(option, $"{title}: {detail}");
        AutomationProperties.SetAutomationId(option, id);
        return option;
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

    // ---------- dense editor rows (Companion › Check-ins, Emotes and motions, and Touch's touch zones and touch temperament) ----------

    /// <summary>Where a dense row's fields start, under its name: an empty check box is 29 pixels wide (its box and the gap its
    /// template keeps before content), then a 6-pixel gap.</summary>
    internal const double RowIndent = 35;

    /// <summary>The check box that turns a dense row on, before its name.</summary>
    internal static CheckBox RowSwitch(bool on) =>
        new() { IsChecked = on, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, RowIndent - 29, 0) };

    /// <summary>Makes a text box, choice or button one of a dense row's compact controls: 32 pixels tall, on the row's centre line.</summary>
    internal static T Compact<T>(T control) where T : Control
    {
        control.SetResourceReference(FrameworkElement.StyleProperty, control switch
        {
            TextBox => "CompactTextBox", ComboBox => "CompactComboBox", _ => "CompactButton"
        });
        if (control is Button) control.MinWidth = 56;
        return control;
    }

    /// <summary>A short muted label in a dense row, on the centre line of the control it names. Labels that start a row's lines get
    /// the same <paramref name="width"/>, so the boxes after them line up.</summary>
    internal static Label RowLabel(string text, UIElement target, double left = 0, double right = 6, double width = 0)
    {
        var label = new Label
        {
            Content = text, Target = target, Padding = new Thickness(left, 0, right, 0), VerticalAlignment = VerticalAlignment.Center,
            MinWidth = width
        };
        label.SetResourceReference(Control.ForegroundProperty, "MutedBrush");
        return label;
    }

    /// <summary>Items of a dense row that stay together when the row wraps, such as a label and its box.</summary>
    internal static StackPanel RowGroup(params UIElement[] items)
    {
        var group = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 18, 6) };
        foreach (var item in items) group.Children.Add(item);
        return group;
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
